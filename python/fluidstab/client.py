"""Raw socket client for the C# ``TrainingServer`` (see
``Assets/FluidStabilizer/Rl/TrainingServer.cs`` for the wire format).

All integers/floats little-endian. One TCP connection = one environment; the
server serves a single client and ``reset(seed)`` reseeds it.
"""
from __future__ import annotations

import socket
import struct

MAGIC = 0x464C5544
VERSION = 1

_OP_RESET = 0
_OP_STEP = 1
_OP_CLOSE = 2


class ProtocolError(RuntimeError):
    pass


class FluidStabClient:
    def __init__(self, host: str = "127.0.0.1", port: int = 5555, timeout: float = 30.0):
        self._sock = socket.create_connection((host, port), timeout=timeout)
        self._sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

        hs = self._recv_exact(20)
        magic, version, self.obs_size, self.priv_size, self.act_size = struct.unpack("<IIiii", hs)
        if magic != MAGIC or version != VERSION:
            raise ProtocolError(f"bad handshake: magic={magic:#x} version={version}")

    # ---- public API ----

    def reset(self, seed: int) -> "np.ndarray":
        self._send_frame(struct.pack("<BQ", _OP_RESET, int(seed) & 0xFFFFFFFFFFFFFFFF))
        payload = self._recv_frame()
        return _floats(payload, self.obs_size)

    def step(self, action) -> tuple:
        if len(action) != self.act_size:
            raise ValueError(f"expected {self.act_size} action values, got {len(action)}")
        body = struct.pack("<B", _OP_STEP) + struct.pack(f"<{self.act_size}f", *[float(a) for a in action])
        self._send_frame(body)
        p = self._recv_frame()

        o = 0
        obs = _floats(p[o : o + self.obs_size * 4], self.obs_size)
        o += self.obs_size * 4
        (reward,) = struct.unpack_from("<f", p, o)
        o += 4
        terminated = p[o] != 0
        truncated = p[o + 1] != 0
        o += 2
        priv = _floats(p[o : o + self.priv_size * 4], self.priv_size)
        return obs, float(reward), bool(terminated), bool(truncated), priv

    def close(self) -> None:
        try:
            self._send_frame(struct.pack("<B", _OP_CLOSE))
        except OSError:
            pass
        finally:
            self._sock.close()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    # ---- framing ----

    def _send_frame(self, payload: bytes) -> None:
        self._sock.sendall(struct.pack("<I", len(payload)) + payload)

    def _recv_frame(self) -> bytes:
        (length,) = struct.unpack("<I", self._recv_exact(4))
        return self._recv_exact(length)

    def _recv_exact(self, n: int) -> bytes:
        buf = bytearray()
        while len(buf) < n:
            chunk = self._sock.recv(n - len(buf))
            if not chunk:
                raise ProtocolError("connection closed by server")
            buf.extend(chunk)
        return bytes(buf)


def _floats(b: bytes, n: int):
    import numpy as np

    return np.frombuffer(bytes(b), dtype="<f4", count=n).astype(np.float32)
