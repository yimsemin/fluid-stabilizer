# fluid-stabilizer — RL trainer

External Python trainer. **No Unity dependency** — talks to the C#
`TrainingServer` over a plain TCP socket.

- `fluidstab/client.py` — raw socket protocol (matches `Assets/FluidStabilizer/Rl/TrainingServer.cs`)
- `fluidstab/env.py` — `FluidStabEnv(gymnasium.Env)`; `info["privileged"]` holds the asymmetric-critic obs
- `train_ppo.py` — CleanRL-style PPO with an asymmetric critic
- `play.py` — run a checkpoint, print episode returns

See [`../docs/rl.md`](../docs/rl.md) for the full workflow.

```bash
python -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt
```

Status: the C# side is tested (loopback bit-identical to the in-process env).
The Python side is written but **not yet run** — validate the reward curve on
the first training run.
