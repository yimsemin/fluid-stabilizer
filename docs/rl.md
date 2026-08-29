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
| Python env + PPO | `python/` | 🟡 asymmetric-critic CleanRL-style PPO; runs end-to-end, trainer brought up to the CleanRL reference (obs/return normalisation, 2026-08-29). **PPO-from-scratch still does not learn** — the task needs residual RL / curriculum / warm-start, not tuning. See "First-run result" |
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

**Bridge verified end-to-end 2026-08-29** (macOS arm64, M4 Max): the commands
below run the full loop — ~1400 steps/s, checkpoint + TensorBoard logs written.
No code changes were needed to get the loop running; the earlier failures were
the two gotchas called out below. **The PPO does not learn yet** (see "First-run
result" further down) — that is a trainer problem, not a bridge problem.

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
Everything except `-rlPort/-rlStack` **defaults to `experiment.txt`** now; the flags override per-run.

**Trace-driven training (step C0):** `-rlTrace all` trains on the recorded drives
that have a `Logs/hindsight/*.traj.csv` (the BC set — 6 drives); `-rlTrace <substr>`
filters by filename. One drive per episode by seed; config = the first drive's
fill/road, episode length = the shortest drive.

**BC warm start (step C):** `FluidStabilizer ▸ Export BC Dataset` (or
`-executeMethod …BcExportRunner.Run`) replays each drive through the real obs
pipeline feeding it the hindsight action → `Logs/bc/dataset.csv`. Then
`python bc_pretrain.py` fits `actor_mean` + the whitener → `runs/bc/actor.pt`,
and `python train_ppo.py --init-actor runs/bc/actor.pt` warm-starts PPO. Because
the F5 hindsight optimum is nearly do-nothing, BC teaches a low-amplitude smooth
prior (keeps exploration off the cliff), not a competent policy.

**Policy serving (step D — the deployment split):** the trained policy runs as
its own process, `python serve_policy.py --checkpoint runs/<run>/actor.pt --port
5601` (loads `actor_mean` + the whitener, returns the deterministic mean, threaded
one-per-client). Unity's `RemotePolicyController` (engine-free, `FluidStabilizer.Rl`)
is the bridge client: it runs `ObservationEncoder` locally, sends the vector, and
turns the reply into a joint-rate command — degrading to a locked Hold (and
re-probing every ~2 s) if the server is absent. Wire format in the class doc-comment.
Use it via `StabilizerChoice.LearnedPolicy` (play-scene key `4`), or
**FluidStabilizer ▸ Evaluate Learned Policy** → `Logs/policy-eval.txt`. This is
*not* baked ONNX in the Unity binary — see project-goals.md "Final-product architecture".

**Residual mode (experiment-plan step B, 2026-08-29):** `-rlBaseline {none|hold|sfa|pid}`
`-rlResidualFrac <0..1>` (default 0.35). With a baseline, each control tick the
env emits `baselineRate + policy·(residualFrac·MaxRate)` in joint-rate space
(baseline angle/locked commands are converted to an equivalent rate — P-tracked
for angle, PD-held-at-zero for locked), both clamped to the max rate. The policy
only learns a small correction around a sane operating point — the F1/F4-cliff
fix. Action space and the Python trainer are unchanged; the composition is
server-side. `RlEnvironment` gained `baselineFactory` / `residualRateScaleRadPerSec`
ctor params + `ActorName` / `GimbalState` getters. `ResidualRlTests` (4).

**Curriculum** (manual for now): start `highway`, briskness ~0.4, fill ~0.45;
once the reward plateaus, restart the server with a harder driver / fuller tank /
`shallow` model and continue training from the checkpoint.

**Parallel rollouts:** the server is single-client. Launch N servers on N ports
and wrap N `FluidStabEnv` in a Gymnasium `AsyncVectorEnv` (the PPO script is
single-env in v1 — extend when throughput matters).

## First-run result (2026-08-29): plumbing works; PPO-from-scratch does **not** learn this task

Three runs (600 k–1 M steps): the trained policy is always *worse than a
zero-action policy* and the return curve is dead flat. Two separate problems
were found; the first is fixed, the second is a task-design decision.

### (1) Trainer was below the CleanRL reference — **fixed**

`train_ppo.py` fed the raw 234-dim frame-stacked obs (physical units on wildly
different scales) straight into a `tanh` MLP, with unscaled reward targets and
`--ent-coef 0`. The first Linear layer saturates and nothing learns. Added, this
session:

- `RunningMeanStd` whitening (±10 clip) of **both** the actor obs and the
  privileged critic obs;
- CleanRL-style return normalisation (reward ÷ running std of the discounted
  return);
- `--ent-coef` default `0 → 1e-3`;
- the obs-normaliser stats are saved in the checkpoint and re-applied by
  `play.py` (a policy trained on whitened obs is garbage without them);
- console now prints `return(mean50)` (raw), flushed.

`v_loss` went from spiking to ~1e-2 — the value head is now sane. But the policy
still does not improve, because of (2).

### (2) The task is an anticipation problem — reactive exploration falls off a cliff

Measured on `-rlDriver aggressive -rlAggression 0.7 -rlFill 0.72` (pendulum ROM,
30 s episodes), mean episode return:

| policy | return | spills out? |
|---|---|---|
| zero action / hold still | **≈ −1800** | no, survives the episode |
| **any** reactive P or D controller on the felt-tilt obs, **any gain, either sign** | ≈ −3200 | yes, 3–4 / 4 |
| PPO after 1 M steps | ≈ −3250 | yes |

This is **finding F1/F4 exactly**: the instant the gimbal moves in response to
the wave it pumps slosh energy and the tank overflows. The only good policy is a
narrow "hold / anticipate" ridge; Gaussian action noise around a random init is
*always* on the cliff, every rollout terminates early at ≈ −3200, the critic
learns "everything is −3200", the gradient vanishes and the log-std collapses.
A from-scratch on-policy learner cannot discover the anticipatory policy here.

**This needs a task-design change, not more steps or tuning. Candidates:**

- **Residual RL** (design-review's stated approach): the policy outputs a small
  correction on top of a baseline (locked-hold, or SFA), so exploration happens
  around a sane operating point instead of random gimbal slams. Needs a change to
  `RlEnvironment` (compose baseline + policy action) — not wired.
- **Curriculum from easy**: start with a calm driver + low fill where holding
  still already scores ~0 and gentle motion helps; ramp difficulty once the
  actuator behaviour is learned. Needs **checkpoint resume** (also not wired).
- **Warm start / behaviour cloning** from the locked-hold or from human traces
  (project-goals ③), then RL fine-tune.
- Much heavier action-rate penalty / lower `MaxRateRadPerSec` so exploratory
  jitter can't pump the mode.

The bridge, the trainer, and the reward are ready for whichever of these the
owner picks.

### Decision (step-back session, 2026-08-29) — see [experiment-plan.md](experiment-plan.md)

No more RL runs until two things exist: **recorded human drives** (done — the
`DriverTrace` path, see phase1-progress item 3) and a **feasibility ceiling**
(offline hindsight trajectory optimisation on a fixed recorded drive). The
ceiling is a hard gate: if a privileged/hindsight solution cannot beat the
locked-Hold baseline on the recorded drives, the premise is adjusted before any
RL.

**Update 2026-08-29 — the ceiling was built and run (`HindsightOptimizer`), and
the gate does NOT pass. See finding F5.** On 6 held-out drives, the
hindsight-optimal gimbal-rate trajectory spills 2–6 L *more* than a locked tank
(pendulum ROM; far worse under shallow water). The actuator's own servo,
commanding zero, already loses ≈ 5–7 L to a mechanically locked joint. **Residual
RL is on hold** until the premise is adjusted (freeboard, road severity, or a
hybrid lock-capable action space so the residual baseline is the 19 L locked tank
rather than the 25 L sagging servo) and a step-3 re-run shows hindsight beating
locked Hold. When RL does resume it is still **residual RL** — policy = baseline +
small correction, warm-started by behaviour-cloning the
`Logs/hindsight/*.traj.csv` trajectories — which needs `RlEnvironment` to compose
(baseline action + policy action) and checkpoint-resume in `train_ppo.py`.

## Notes / risks

- **No spill-metric readout over the wire.** `play.py` only sums the shaped
  reward. The F4 table is spill in litres on 6 named scenarios
  (`docs/findings.md`); `RlEnvironment.Summary()` produces that roll-up in C# but
  the TCP protocol (RESET/STEP/CLOSE) does not expose it, and neither does the
  per-term `RewardBreakdown`. To compare a policy against F4 you need either a
  `SUMMARY` opcode or the Phase-1.5 ONNX-in-`Evaluator` path.
- **No checkpoint resume.** `train_ppo.py` always starts from random weights, so
  the "continue training from the checkpoint" curriculum below is not yet
  possible.
- **Bracket-model discipline (F3):** train on the pendulum ROM (cheap), and
  *always* re-evaluate the trained policy on `-rlSlosh shallow` before trusting a
  win. A policy that beats rigid only under the ROM has probably learned a ROM
  artefact.
- **Reward hacking watch:** the classic exploit is caging the liquid at a hard
  end-stop. Watch the `joint-limit-proximity` and `actuator-effort` terms in the
  per-step breakdown; raise `k6`/`k5` if it happens.
- **Reward weights bumped 2026-08-29:** `Overflow` 500 → 5e4, `TerminalSpill`
  5 → 500. With the old values a 50 L spill cost only ≈ −25 and ending the
  episode by dumping the tank scored *better* than holding on — a fail-fast
  exploit. `RlEnvironmentTests` / `RewardFunction` tests still pass.
- **Throughput:** the binary bridge is fine for v1. If it bottlenecks, batch
  steps or move the host to a standalone .NET process (the env code is
  engine-free — only the launcher is Unity).
