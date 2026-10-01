"""Run a trained actor against the env and print the episode roll-up metrics
(the same metrics the analytic baselines are scored on — compare against
finding F4 in docs/findings.md).

    python play.py --port 5555 --actor runs/<name>/actor.pt --episodes 20
"""
from __future__ import annotations

import argparse

import numpy as np
import torch
import torch.nn as nn

from fluidstab import FluidStabEnv


def build_actor(ckpt, obs_dim, act_dim, hidden=256):
    net = nn.Sequential(
        nn.Linear(obs_dim, hidden), nn.Tanh(),
        nn.Linear(hidden, hidden), nn.Tanh(),
        nn.Linear(hidden, act_dim),
    )
    net.load_state_dict(ckpt["actor_mean"])
    net.eval()
    return net


def make_whitener(ckpt):
    """Reproduce train_ppo's observation normalisation from the checkpoint.
    The actor was trained on whitened obs, so eval must whiten identically."""
    if "obs_rms_mean" not in ckpt:
        return lambda o: np.asarray(o, np.float32)      # pre-normalisation checkpoint
    mean = np.asarray(ckpt["obs_rms_mean"], np.float64)
    var = np.asarray(ckpt["obs_rms_var"], np.float64)
    return lambda o: np.clip(
        (np.asarray(o, np.float64) - mean) / np.sqrt(var + 1e-8), -10.0, 10.0
    ).astype(np.float32)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=5555)
    p.add_argument("--actor", required=True)
    p.add_argument("--episodes", type=int, default=20)
    p.add_argument("--deterministic", action="store_true", default=True)
    args = p.parse_args()

    env = FluidStabEnv(args.host, args.port)
    ckpt = torch.load(args.actor, map_location="cpu", weights_only=False)
    actor = build_actor(ckpt, env.observation_space.shape[0], env.action_space.shape[0])
    whiten = make_whitener(ckpt)

    returns = []
    for ep in range(args.episodes):
        obs, _ = env.reset(seed=1000 + ep)
        done = False
        total = 0.0
        while not done:
            with torch.no_grad():
                a = actor(torch.tensor(whiten(obs), dtype=torch.float32).unsqueeze(0))[0].numpy()
            obs, r, term, trunc, _ = env.step(np.clip(a, -1, 1))
            total += r
            done = term or trunc
        returns.append(total)
        print(f"episode {ep:2d}  return {total:+.3f}")

    print(f"\nmean return {np.mean(returns):+.3f} ± {np.std(returns):.3f} over {args.episodes} episodes")
    env.close()


if __name__ == "__main__":
    main()
