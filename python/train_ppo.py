"""Asymmetric-critic PPO for fluid-stabilizer (design-review §12).

CleanRL-style single file (https://github.com/vwxyzjn/cleanrl) adapted for:
  * a single socket-backed env (the C# TrainingServer is single-client), and
  * an ASYMMETRIC critic — the value network sees info["privileged"] (true slosh
    state, CoM, actuator/thermal state) that the policy never gets.

Not yet run in CI (needs torch). Validate the reward curve on the first run and
tune RewardWeights in the C# side if the policy reward-hacks (slamming the tank
to an end-stop is the classic one — watch the joint-limit-proximity term).

    # terminal 1: start the sim server (headless Unity, ~15 s to boot)
    UNITY=/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity
    "$UNITY" -batchmode -nographics -projectPath .. \
        -executeMethod FluidStabilizer.SimEditor.RlServerLauncher.Serve \
        -rlPort 5555 -rlDriver highway -rlBriskness 0.5 -rlFill 0.5

    # terminal 2
    python train_ppo.py --port 5555 --total-steps 2_000_000
"""
from __future__ import annotations

import argparse
import os
import time
from collections import deque

import numpy as np
import torch
import torch.nn as nn
from torch.distributions import Normal
from torch.utils.tensorboard import SummaryWriter

from fluidstab import FluidStabEnv


def layer_init(layer, std=np.sqrt(2), bias=0.0):
    nn.init.orthogonal_(layer.weight, std)
    nn.init.constant_(layer.bias, bias)
    return layer


class RunningMeanStd:
    """Welford running mean/variance, same recipe CleanRL uses for its
    NormalizeObservation / NormalizeReward wrappers. The C# side hands us raw
    physical quantities on wildly different scales (rad, rad/s, N, m, J, ...);
    without this the first Linear layer saturates its tanh and nothing learns
    (measured 2026-08-29: two 600-800 k-step runs stayed flat and never even
    found the trivial hold-still policy)."""

    def __init__(self, shape=()):
        self.mean = np.zeros(shape, "float64")
        self.var = np.ones(shape, "float64")
        self.count = 1e-4

    def update(self, x):
        x = np.asarray(x, "float64")
        self.count += 1
        delta = x - self.mean
        self.mean += delta / self.count
        self.var += (delta * (x - self.mean) - self.var) / self.count

    def whiten(self, x, clip=10.0):
        z = (np.asarray(x, "float64") - self.mean) / np.sqrt(self.var + 1e-8)
        return np.clip(z, -clip, clip).astype(np.float32)


class ActorCritic(nn.Module):
    def __init__(self, obs_dim: int, priv_dim: int, act_dim: int, hidden: int = 256):
        super().__init__()
        self.actor_mean = nn.Sequential(
            layer_init(nn.Linear(obs_dim, hidden)), nn.Tanh(),
            layer_init(nn.Linear(hidden, hidden)), nn.Tanh(),
            layer_init(nn.Linear(hidden, act_dim), std=0.01),
        )
        self.actor_logstd = nn.Parameter(torch.zeros(1, act_dim) - 0.5)
        # asymmetric: critic sees obs + privileged
        self.critic = nn.Sequential(
            layer_init(nn.Linear(obs_dim + priv_dim, hidden)), nn.Tanh(),
            layer_init(nn.Linear(hidden, hidden)), nn.Tanh(),
            layer_init(nn.Linear(hidden, 1), std=1.0),
        )

    def value(self, obs, priv):
        return self.critic(torch.cat([obs, priv], dim=-1)).squeeze(-1)

    def act(self, obs, action=None):
        mean = self.actor_mean(obs)
        std = self.actor_logstd.expand_as(mean).exp()
        dist = Normal(mean, std)
        if action is None:
            action = dist.sample()
        logprob = dist.log_prob(action).sum(-1)
        entropy = dist.entropy().sum(-1)
        return action, logprob, entropy


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=5555)
    p.add_argument("--total-steps", type=int, default=2_000_000)
    p.add_argument("--rollout", type=int, default=2048)
    p.add_argument("--epochs", type=int, default=10)
    p.add_argument("--minibatches", type=int, default=32)
    p.add_argument("--gamma", type=float, default=0.99)
    p.add_argument("--gae-lambda", type=float, default=0.95)
    p.add_argument("--clip", type=float, default=0.2)
    p.add_argument("--ent-coef", type=float, default=1e-3)  # keep the free log-std from collapsing
    p.add_argument("--vf-coef", type=float, default=0.5)
    p.add_argument("--lr", type=float, default=3e-4)
    p.add_argument("--max-grad-norm", type=float, default=0.5)
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--run-name", default=None)
    p.add_argument("--init-actor", default=None,
                   help="behaviour-cloning checkpoint from bc_pretrain.py — warm-starts "
                        "actor_mean + the obs whitener (experiment-plan step C)")
    args = p.parse_args()

    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")

    run_name = args.run_name or f"ppo-{int(time.time())}"
    os.makedirs(f"runs/{run_name}", exist_ok=True)
    writer = SummaryWriter(f"runs/{run_name}")

    env = FluidStabEnv(args.host, args.port)
    obs_dim = env.observation_space.shape[0]
    priv_dim = env.privileged_size
    act_dim = env.action_space.shape[0]
    print(f"obs={obs_dim} priv={priv_dim} act={act_dim} device={device}")

    agent = ActorCritic(obs_dim, priv_dim, act_dim).to(device)
    opt = torch.optim.Adam(agent.parameters(), lr=args.lr, eps=1e-5)

    # input/return normalisation (updated only while collecting rollouts)
    obs_rms = RunningMeanStd(obs_dim)
    priv_rms = RunningMeanStd(priv_dim)
    ret_rms = RunningMeanStd()          # running std of the discounted return
    running_ret = 0.0

    if args.init_actor:
        ck = torch.load(args.init_actor, map_location=device, weights_only=False)
        assert ck["obs_dim"] == obs_dim and ck["act_dim"] == act_dim, "BC checkpoint shape mismatch"
        agent.actor_mean.load_state_dict({k: v.to(device) for k, v in ck["actor_mean"].items()})
        if "actor_logstd" in ck:
            agent.actor_logstd.data.copy_(torch.as_tensor(ck["actor_logstd"]).to(device))
        # keep the BC whitener; nudge count high so rollouts barely shift it
        obs_rms.mean = np.asarray(ck["obs_rms_mean"], dtype="float64").copy()
        obs_rms.var = np.asarray(ck["obs_rms_var"], dtype="float64").copy()
        obs_rms.count = 1.0e5
        print(f"[init] actor warm-started from {args.init_actor} "
              f"(bc={ck.get('bc', {})})")

    N = args.rollout
    obs_buf = torch.zeros(N, obs_dim, device=device)
    priv_buf = torch.zeros(N, priv_dim, device=device)
    act_buf = torch.zeros(N, act_dim, device=device)
    logp_buf = torch.zeros(N, device=device)
    rew_buf = torch.zeros(N, device=device)
    done_buf = torch.zeros(N, device=device)
    val_buf = torch.zeros(N, device=device)

    def ingest(obs_raw, info, update=True):
        """raw env output -> whitened (obs, priv) tensors, updating the stats."""
        if update:
            obs_rms.update(obs_raw)
            priv_rms.update(info["privileged"])
        o = torch.tensor(obs_rms.whiten(obs_raw), dtype=torch.float32, device=device)
        p = torch.tensor(priv_rms.whiten(info["privileged"]), dtype=torch.float32, device=device)
        return o, p

    obs_raw, info = env.reset(seed=args.seed)
    obs, priv = ingest(obs_raw, info)
    ep_ret, ep_len = 0.0, 0          # ep_ret is the RAW (un-normalised) episode return
    ep_ret_hist = deque(maxlen=50)
    global_step = 0

    while global_step < args.total_steps:
        for t in range(N):
            global_step += 1
            obs_buf[t] = obs
            priv_buf[t] = priv
            with torch.no_grad():
                action, logprob, _ = agent.act(obs.unsqueeze(0))
                value = agent.value(obs.unsqueeze(0), priv.unsqueeze(0))
            act_buf[t] = action[0]
            logp_buf[t] = logprob[0]
            val_buf[t] = value[0]

            nobs, reward, terminated, truncated, info = env.step(action[0].cpu().numpy())
            done = terminated or truncated

            # CleanRL-style return normalisation: scale reward by the running std
            # of the discounted return so the value target is ~unit variance.
            running_ret = running_ret * args.gamma * (1.0 - float(done)) + reward
            ret_rms.update(running_ret)
            rew_buf[t] = float(np.clip(reward / (np.sqrt(ret_rms.var) + 1e-8), -10.0, 10.0))
            done_buf[t] = float(done)
            ep_ret += reward          # RAW reward for logging
            ep_len += 1

            if done:
                writer.add_scalar("charts/episode_return", ep_ret, global_step)
                writer.add_scalar("charts/episode_length", ep_len, global_step)
                ep_ret_hist.append(ep_ret)
                ep_ret, ep_len = 0.0, 0
                running_ret = 0.0
                nobs, info = env.reset()

            obs, priv = ingest(nobs, info)

        # GAE
        with torch.no_grad():
            last_val = agent.value(obs.unsqueeze(0), priv.unsqueeze(0))[0]
        adv = torch.zeros(N, device=device)
        last_gae = 0.0
        for t in reversed(range(N)):
            next_val = last_val if t == N - 1 else val_buf[t + 1]
            next_nonterminal = 1.0 - done_buf[t]
            delta = rew_buf[t] + args.gamma * next_val * next_nonterminal - val_buf[t]
            last_gae = delta + args.gamma * args.gae_lambda * next_nonterminal * last_gae
            adv[t] = last_gae
        returns = adv + val_buf

        b_inds = np.arange(N)
        mb_size = N // args.minibatches
        adv_norm = (adv - adv.mean()) / (adv.std() + 1e-8)

        for _ in range(args.epochs):
            np.random.shuffle(b_inds)
            for start in range(0, N, mb_size):
                mb = b_inds[start : start + mb_size]
                _, new_logp, entropy = agent.act(obs_buf[mb], act_buf[mb])
                ratio = (new_logp - logp_buf[mb]).exp()

                pg1 = -adv_norm[mb] * ratio
                pg2 = -adv_norm[mb] * torch.clamp(ratio, 1 - args.clip, 1 + args.clip)
                pg_loss = torch.max(pg1, pg2).mean()

                new_val = agent.value(obs_buf[mb], priv_buf[mb])
                v_loss = 0.5 * ((new_val - returns[mb]) ** 2).mean()

                loss = pg_loss - args.ent_coef * entropy.mean() + args.vf_coef * v_loss
                opt.zero_grad()
                loss.backward()
                nn.utils.clip_grad_norm_(agent.parameters(), args.max_grad_norm)
                opt.step()

        recent = np.mean(ep_ret_hist) if ep_ret_hist else float("nan")
        writer.add_scalar("losses/policy", pg_loss.item(), global_step)
        writer.add_scalar("losses/value", v_loss.item(), global_step)
        writer.add_scalar("losses/entropy", entropy.mean().item(), global_step)
        writer.add_scalar("charts/episode_return_mean50", recent, global_step)
        print(f"step {global_step:>9}  return(mean50)={recent:+9.1f}  "
              f"pg={pg_loss.item():+.3f}  v={v_loss.item():.3f}", flush=True)

        torch.save(
            {"actor_mean": agent.actor_mean.state_dict(),
             "actor_logstd": agent.actor_logstd.detach().cpu(),
             "obs_dim": obs_dim, "act_dim": act_dim,
             "obs_rms_mean": obs_rms.mean, "obs_rms_var": obs_rms.var},
            f"runs/{run_name}/actor.pt",
        )

    env.close()
    writer.close()


if __name__ == "__main__":
    main()
