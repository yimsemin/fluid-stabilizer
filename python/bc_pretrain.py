"""Behaviour-cloning warm start for the fluid-stabilizer policy (experiment-plan
step C).

Reads the dataset exported by the C# ``BcExportRunner``
(``Logs/bc/dataset.csv`` — one row per control tick, ``obs… , aPitch, aRoll``,
the actions being the offline hindsight trajectory normalised to ±1) and fits the
same MLP ``train_ppo.py`` uses for ``actor_mean`` to it, plus the observation
whitener. The checkpoint it writes drops straight into
``train_ppo.py --init-actor``.

Not a policy that wins — a policy that starts PPO from roughly the F5 ceiling
instead of from noise, so on-policy exploration does not immediately fall off the
F1/F4 cliff.

    cd python
    python bc_pretrain.py --dataset ../Logs/bc/dataset.csv --out runs/bc/actor.pt
    # then, with the sim server running on :5555 with matching -rlTrace / -rlStack:
    python train_ppo.py --port 5555 --init-actor runs/bc/actor.pt --total-steps 300000
"""
from __future__ import annotations

import argparse
import os

import numpy as np
import torch
import torch.nn as nn

from train_ppo import RunningMeanStd, layer_init


def load_dataset(path: str):
    meta: dict[str, str] = {}
    n_act = 2
    rows: list[list[float]] = []
    with open(path) as f:
        for line in f:
            if not line.strip():
                continue
            if line.startswith("#"):
                k, _, v = line[1:].partition(":")
                meta[k.strip()] = v.strip()
                continue
            if line[0] == "o":  # "o0,...,aPitch,aRoll[,aLock]" column header
                n_act = 3 if line.rstrip().endswith("aLock") else 2
                continue
            rows.append([float(x) for x in line.split(",")])
    arr = np.asarray(rows, dtype=np.float32)
    # Explicit-lock contracts carry a third action column (the release request).
    obs, act = arr[:, :-n_act], arr[:, -n_act:]
    return obs, act, meta


def build_actor(obs_dim: int, act_dim: int, hidden: int) -> nn.Sequential:
    # identical to train_ppo.ActorCritic.actor_mean
    return nn.Sequential(
        layer_init(nn.Linear(obs_dim, hidden)), nn.Tanh(),
        layer_init(nn.Linear(hidden, hidden)), nn.Tanh(),
        layer_init(nn.Linear(hidden, act_dim), std=0.01),
    )


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--dataset", default="../Logs/bc/dataset.csv")
    p.add_argument("--out", default="runs/bc/actor.pt")
    p.add_argument("--hidden", type=int, default=256)
    p.add_argument("--epochs", type=int, default=150)
    p.add_argument("--batch", type=int, default=512)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--val-frac", type=float, default=0.1)
    p.add_argument("--seed", type=int, default=1)
    args = p.parse_args()

    torch.manual_seed(args.seed)
    np.random.seed(args.seed)

    obs, act, meta = load_dataset(args.dataset)
    n, obs_dim = obs.shape
    act_dim = act.shape[1]
    print(f"dataset: {n} rows, obs={obs_dim}, act={act_dim}, drives={meta.get('drives', '?')}")

    # whitener fit on the whole dataset (frozen; PPO keeps it via the checkpoint)
    rms = RunningMeanStd(obs_dim)
    for x in obs:
        rms.update(x)
    obs_w = rms.whiten(obs)

    idx = np.random.permutation(n)
    n_val = int(n * args.val_frac)
    val_i, tr_i = idx[:n_val], idx[n_val:]

    dev = torch.device("cuda" if torch.cuda.is_available() else
                       ("mps" if torch.backends.mps.is_available() else "cpu"))
    Xtr = torch.tensor(obs_w[tr_i], device=dev)
    Ytr = torch.tensor(act[tr_i], device=dev)
    Xva = torch.tensor(obs_w[val_i], device=dev)
    Yva = torch.tensor(act[val_i], device=dev)

    net = build_actor(obs_dim, act_dim, args.hidden).to(dev)
    opt = torch.optim.Adam(net.parameters(), lr=args.lr)

    best_val = float("inf")
    best_state = None
    for ep in range(1, args.epochs + 1):
        net.train()
        perm = torch.randperm(Xtr.shape[0], device=dev)
        tot = 0.0
        for s in range(0, Xtr.shape[0], args.batch):
            mb = perm[s: s + args.batch]
            pred = net(Xtr[mb])
            loss = ((pred - Ytr[mb]) ** 2).mean()
            opt.zero_grad()
            loss.backward()
            opt.step()
            tot += loss.item() * mb.shape[0]
        net.eval()
        with torch.no_grad():
            val = ((net(Xva) - Yva) ** 2).mean().item()
        if val < best_val:
            best_val = val
            best_state = {k: v.detach().cpu().clone() for k, v in net.state_dict().items()}
        if ep % 10 == 0 or ep == 1:
            print(f"epoch {ep:>4}  train_mse={tot / Xtr.shape[0]:.4f}  val_mse={val:.4f}")

    net.load_state_dict(best_state)
    print(f"best val_mse={best_val:.4f}  (action units, ±1 range)")

    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    torch.save(
        {
            "actor_mean": {k: v.cpu() for k, v in net.state_dict().items()},
            "actor_logstd": torch.zeros(1, act_dim) - 0.5,
            "obs_dim": obs_dim,
            "act_dim": act_dim,
            "obs_rms_mean": np.asarray(rms.mean, dtype="float64").tolist(),
            "obs_rms_var": np.asarray(rms.var, dtype="float64").tolist(),
            "bc": {"rows": int(n), "val_mse": best_val, "drives": meta.get("drives", ""),
                   "clamped_fraction": meta.get("clampedFraction", "")},
            # The action space these targets live in — must match the environment the
            # student is fine-tuned in and the bridge it is served through.
            "contract": meta.get("contract", ""),
        },
        args.out,
    )
    print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
