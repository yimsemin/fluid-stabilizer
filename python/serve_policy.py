"""Policy-inference server for fluid-stabilizer (experiment-plan step D).

Loads a trained checkpoint (from ``train_ppo.py`` or ``bc_pretrain.py`` — both
write ``actor_mean`` + ``obs_rms_mean/var``) and answers inference requests from
the Unity-side ``RemotePolicyController`` over TCP. This is "the brain" as its own
process, linked to "the world" (Unity) by the bridge — the split a real robot has
(project-goals.md, design-review §12). Deterministic: it returns the actor mean,
no sampling.

    cd python
    python serve_policy.py --checkpoint runs/bc/actor.pt --port 5601
    # then in Unity pick the "Learned policy" stabilizer (host 127.0.0.1 port 5601)

Wire format (little-endian, ``[u32 len][payload]`` frames): on connect the server
sends ``[u32 magic=0x504F4C59][u32 version=1][i32 obsSize][i32 actSize]``. Then
per request the client sends ``[u8 op][...]``:
  op 0 = reset   -> ``[u8 1]``           (no server-side episode state for an MLP)
  op 1 = infer   -> ``[f32 act*actSize]``  payload = ``[f32 obs*obsSize]``
  op 2 = close
"""
from __future__ import annotations

import argparse
import socket
import struct
import threading

import numpy as np
import torch

from bc_pretrain import build_actor

MAGIC = 0x504F4C59
VERSION = 1


def load_policy(path: str):
    ck = torch.load(path, map_location="cpu", weights_only=False)
    obs_dim, act_dim = int(ck["obs_dim"]), int(ck["act_dim"])
    hidden = ck["actor_mean"]["0.weight"].shape[0]
    net = build_actor(obs_dim, act_dim, hidden)
    net.load_state_dict({k: torch.as_tensor(v) for k, v in ck["actor_mean"].items()})
    net.eval()
    mean = np.asarray(ck["obs_rms_mean"], dtype=np.float64)
    var = np.asarray(ck["obs_rms_var"], dtype=np.float64)
    print(f"loaded {path}: obs={obs_dim} act={act_dim} hidden={hidden} bc={ck.get('bc', {})}")

    def whiten(x, clip=10.0):
        z = (np.asarray(x, np.float64) - mean) / np.sqrt(var + 1e-8)
        return np.clip(z, -clip, clip).astype(np.float32)

    @torch.no_grad()
    def act(obs_raw):
        o = torch.tensor(whiten(obs_raw)).unsqueeze(0)
        a = net(o)[0].numpy()
        return np.clip(a, -1.0, 1.0).astype("<f4")

    return act, obs_dim, act_dim


def _recv_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


def _recv_frame(sock):
    head = _recv_exact(sock, 4)
    if head is None:
        return None
    (length,) = struct.unpack("<I", head)
    return _recv_exact(sock, length) if length else b""


def _send_frame(sock, payload):
    sock.sendall(struct.pack("<I", len(payload)) + payload)


def serve_client(conn, act, obs_dim, act_dim):
    _send_frame_head = struct.pack("<IIii", MAGIC, VERSION, obs_dim, act_dim)
    conn.sendall(_send_frame_head)  # 16-byte handshake, not length-prefixed

    while True:
        frame = _recv_frame(conn)
        if not frame:
            return
        op = frame[0]
        if op == 2:  # close
            return
        if op == 0:  # reset
            _send_frame(conn, b"\x01")
            continue
        if op == 1:  # infer
            obs = np.frombuffer(frame, dtype="<f4", count=obs_dim, offset=1)
            a = act(obs)
            _send_frame(conn, a.tobytes())
            continue
        raise ValueError(f"unknown op {op}")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--checkpoint", default="runs/bc/actor.pt")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=5601)
    args = p.parse_args()

    act, obs_dim, act_dim = load_policy(args.checkpoint)

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.host, args.port))
    srv.listen(1)
    print(f"[policy] serving {args.checkpoint} on {args.host}:{args.port}")
    try:
        def handle(conn, addr):
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            print(f"[policy] client {addr} connected", flush=True)
            try:
                serve_client(conn, act, obs_dim, act_dim)
            except (ConnectionError, OSError) as e:
                print(f"[policy] client error: {e}", flush=True)
            finally:
                conn.close()
                print("[policy] client disconnected", flush=True)

        while True:
            conn, addr = srv.accept()
            threading.Thread(target=handle, args=(conn, addr), daemon=True).start()
    except KeyboardInterrupt:
        pass
    finally:
        srv.close()


if __name__ == "__main__":
    main()
