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
    p.add_argument("--ent-coef", type=float, default=0.0)
    p.add_argument("--vf-coef", type=float, default=0.5)
    p.add_argument("--lr", type=float, default=3e-4)
    p.add_argument("--max-grad-norm", type=float, default=0.5)
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--run-name", default=None)
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

    N = args.rollout
    obs_buf = torch.zeros(N, obs_dim, device=device)
    priv_buf = torch.zeros(N, priv_dim, device=device)
    act_buf = torch.zeros(N, act_dim, device=device)
    logp_buf = torch.zeros(N, device=device)
    rew_buf = torch.zeros(N, device=device)
    done_buf = torch.zeros(N, device=device)
    val_buf = torch.zeros(N, device=device)

    obs, info = env.reset(seed=args.seed)
    obs = torch.tensor(obs, dtype=torch.float32, device=device)
    priv = torch.tensor(info["privileged"], dtype=torch.float32, device=device)
    ep_ret, ep_len = 0.0, 0
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
            rew_buf[t] = reward
            done_buf[t] = float(done)
            ep_ret += reward
            ep_len += 1

            if done:
                writer.add_scalar("charts/episode_return", ep_ret, global_step)
                writer.add_scalar("charts/episode_length", ep_len, global_step)
                ep_ret, ep_len = 0.0, 0
                nobs, info = env.reset()

            obs = torch.tensor(nobs, dtype=torch.float32, device=device)
            priv = torch.tensor(info["privileged"], dtype=torch.float32, device=device)

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

        writer.add_scalar("losses/policy", pg_loss.item(), global_step)
        writer.add_scalar("losses/value", v_loss.item(), global_step)
        writer.add_scalar("losses/entropy", entropy.mean().item(), global_step)
        print(f"step {global_step:>9}  pg={pg_loss.item():+.3f}  v={v_loss.item():.3f}")

        torch.save(
            {"actor_mean": agent.actor_mean.state_dict(),
             "actor_logstd": agent.actor_logstd.detach().cpu(),
             "obs_dim": obs_dim, "act_dim": act_dim},
            f"runs/{run_name}/actor.pt",
        )

    env.close()
    writer.close()


if __name__ == "__main__":
    main()
