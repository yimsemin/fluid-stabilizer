# RL path (phase-1 item 12)

The learned-policy integration. Design-review §11–§12: a **language-agnostic
Gymnasium-style boundary**, not Unity ML-Agents — so the trainer is an ordinary
Python process with no Unity dependency, and the same policy contract works
against a ROS 2 bridge later.

## Why RL here at all

Findings F1–F4: against a rigid (locked-joint) `Hold`, **no analytic reactive
tilt controller wins on any scenario** — reactive feedback that rotates the tank
adds slosh energy. Yet perfect (non-causal) tank-tilt tracking cuts the wall
wave to 0.40× (`IdealTiltTracking_…`). The gap is *anticipation* — inferring the
hidden slosh state from a history of noisy sensors and acting ahead of the wave,
within a torque budget that a hard maneuver can thermally exhaust. That is the
RL target.

## Pieces

| Piece | Where | Status |
|---|---|---|
| `ObservationEncoder` | `Assets/FluidStabilizer/Rl/` | ✅ actor obs: sensor-derived, ~unit-scaled, N-frame stack (default 6) + allowed derived felt-tilt |
| `PrivilegedEncoder` | `Rl/` | ✅ float[21] for the asymmetric critic (true slosh state, CoM, actuator + thermal) |
| `RewardFunction` | `Rl/` | ✅ the §11 continuous reward, weights configurable, per-term breakdown |
| `RlEnvironment` | `Rl/` | ✅ deterministic `Reset(seed)→obs`, `Step(action)→(obs, reward, terminated, truncated, privileged)` |
| `TrainingServer` | `Rl/` | ✅ length-prefixed binary TCP bridge (one client); loopback-tested bit-identical to the in-process env |
| `RlServerLauncher` | `Sim/Editor/` | ✅ `-executeMethod` entry point (headless Unity host) |
| Python env + PPO | `python/` | 🟡 written, **not yet run** (needs `torch`); asymmetric-critic CleanRL-style PPO |
| Domain randomization | — | ⬜ item 13 |
| Curriculum automation | — | ⬜ (restart the server with harder flags for now) |

## Action / observation / reward

- **Action:** per-axis joint **angular-rate** command in `[-1, 1]`, scaled to
  `GimbalConfig.MaxRateRadPerSec`. Rate mode, not angle (design-review §11; F1).
- **Actor observation:** `ObservationEncoder` — vehicle + tank IMU, joint state,
  motor state, 4 load cells, optional level/CAN, last action, and the low-passed
  felt-tilt direction in the tank frame + its rate. Frame-stacked. **Never** any
  ground truth.
- **Critic observation:** the actor obs concatenated (Python side) with
  `PrivilegedEncoder` output.
- **Reward** (`RewardWeights.Default()`, tune in C#): `−k1·overflow −k2·near-rim²
  −k3·slosh KE −k4·|Δaction| −k5·actuator effort −k6·joint-limit-proximity²
  −k7·tank-tilt² (+k8 alive) −K_term on a large-spill terminal`.

## Running it

```bash
# 1. install the trainer (once), on the machine that will train
cd python && python -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt

# 2. start the sim server (headless Unity, ~15 s to boot; leave it running)
UNITY="/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity"
"$UNITY" -batchmode -nographics -projectPath . \
  -executeMethod FluidStabilizer.SimEditor.RlServerLauncher.Serve \
  -rlPort 5555 -rlDriver highway -rlBriskness 0.5 -rlFill 0.5 -rlSlosh pendulum

# 3. train
cd python && python train_ppo.py --port 5555 --total-steps 2_000_000
tensorboard --logdir python/runs

# 4. evaluate a checkpoint (compare mean return / spill against F4)
python play.py --port 5555 --actor runs/<run>/actor.pt --episodes 20
```

Launcher flags: `-rlPort -rlFill -rlSeconds -rlStack -rlDriver {highway|aggressive|scripted}
-rlSlosh {pendulum|shallow} -rlBriskness -rlAggression`.

**Curriculum** (manual for now): start `highway`, briskness ~0.4, fill ~0.45;
once the reward plateaus, restart the server with a harder driver / fuller tank /
`shallow` model and continue training from the checkpoint.

**Parallel rollouts:** the server is single-client. Launch N servers on N ports
and wrap N `FluidStabEnv` in a Gymnasium `AsyncVectorEnv` (the PPO script is
single-env in v1 — extend when throughput matters).

## Notes / risks

- **Bracket-model discipline (F3):** train on the pendulum ROM (cheap), and
  *always* re-evaluate the trained policy on `-rlSlosh shallow` before trusting a
  win. A policy that beats rigid only under the ROM has probably learned a ROM
  artefact.
- **Reward hacking watch:** the classic exploit is caging the liquid at a hard
  end-stop. Watch the `joint-limit-proximity` and `actuator-effort` terms in the
  per-step breakdown; raise `k6`/`k5` if it happens.
- **Throughput:** the binary bridge is fine for v1. If it bottlenecks, batch
  steps or move the host to a standalone .NET process (the env code is
  engine-free — only the launcher is Unity).
