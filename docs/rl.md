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
| Python env + PPO | `python/` | ✅ asymmetric-critic CleanRL-style PPO; **first run 2026-08-29** — trains end-to-end against the headless server, checkpoints + TensorBoard OK. Reward not yet tuned; no long job / no F4 comparison yet |
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

**Verified end-to-end 2026-08-29** (macOS arm64, M4 Max): the commands below
train for real — 30 k steps / 10 episodes in ~22 s wall, checkpoint + TensorBoard
logs written, `episode_return` ≈ −360 (untuned). No code changes were needed to
get here; the earlier failures were the two gotchas called out below.

> ⚠️ **Close the Unity Editor before starting the headless server.** Unity is
> single-instance per project — if the Editor (or a stale `-batchmode` Unity, or
> an `AssetImportWorker`) already holds the project, the `-executeMethod` launch
> silently blocks / never binds the port. Check with
> `pgrep -fl "Unity.app/Contents/MacOS/Unity"` and confirm `Temp/UnityLockfile`
> is gone. Later this should become a `[MenuItem]` so the server can run from
> inside an open Editor; not built yet.

```bash
# 1. install the trainer (once), on the machine that will train.
#    Any CPython 3.9–3.13 works; torch / gymnasium / tensorboard have arm64
#    wheels. The committed python/.venv is 3.9.6 (CommandLineTools) and is fine.
cd python && python3 -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt

# 2. start the sim server (headless Unity; leave it running — Serve() blocks the
#    main thread on an accept loop forever, that is normal, do NOT pass -quit).
#    Log to a file so you can watch the boot; ~5 s warm, up to a minute cold.
UNITY="/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity"
"$UNITY" -batchmode -nographics -projectPath . \
  -executeMethod FluidStabilizer.SimEditor.RlServerLauncher.Serve \
  -rlPort 5555 -rlDriver highway -rlBriskness 0.5 -rlFill 0.5 -rlSlosh pendulum \
  -logFile Logs/rlserver.log &
# wait for this line before connecting:  [rl] TrainingServer listening on 127.0.0.1:5555
#   (the UnityEngine.Debug:ExtractStackTrace... block right after
#    "[rl] launching TrainingServer" is NOT an error — batchmode attaches a
#    stack trace to every Debug.Log.)

# 3. train  (start short to confirm the loop, then go long)
cd python && python train_ppo.py --port 5555 --total-steps 30000 --run-name smoke
python train_ppo.py --port 5555 --total-steps 2_000_000
tensorboard --logdir python/runs

# 4. evaluate a checkpoint (compare mean return / spill against F4)
python play.py --port 5555 --actor runs/<run>/actor.pt --episodes 20
```

The server serves one client at a time and loops back to `accept` when the
trainer disconnects, so you can re-run `train_ppo.py` against the same server
process. Restart the server only to change the `-rl*` scenario flags.

Launcher flags: `-rlPort -rlFill -rlSeconds -rlStack -rlDriver {highway|aggressive|scripted}
-rlSlosh {pendulum|shallow} -rlRoad {none|mild|rough} -rlBriskness -rlAggression`.

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
