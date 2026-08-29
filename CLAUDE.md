# CLAUDE.md — guidance for Claude Code in this repo

## What this project is

`fluid-stabilizer` — a Physical AI simulation. An AI controls a 2-axis
(pitch/roll) stabilizer under an open liquid tank on a **human-driven** truck, to
minimize liquid spillage. The AI does **not** drive. Full context:
[docs/project-brief.md](docs/project-brief.md).

## Read before working

1. [docs/project-brief.md](docs/project-brief.md) — the goal (owner's vision).
1b. [docs/project-goals.md](docs/project-goals.md) — the owner's **clarified
   intent** (2026-08-29): the learning purpose, the three-way comparison as the
   headline result, and the final-product architecture. Wins over older docs on
   scope.
2. [docs/design-review.md](docs/design-review.md) — critical review + the
   recommended architecture. **This is the source of truth for design
   decisions.**
3. [docs/frames-units-conventions.md](docs/frames-units-conventions.md) — frames,
   units, timing. Must be agreed before physics code.
4. [docs/setup-status.md](docs/setup-status.md) — environment, what's done,
   what's blocked.

## The owner

Not a professional developer. Prioritize a structure they can understand and
manage together with Claude Code. Keep it simple; explain trade-offs; do not
build enterprise-grade abstraction. Do not silently adopt a heavier approach —
say so and why.

## Hard rules (from the design review)

- **Controller code has zero `UnityEngine` references.** Messages in, messages
  out. It must run unchanged against the sim now and a ROS 2 bridge later.
- **Keep the Sensor → Controller → Actuator boundary.** Interfaces:
  `ISensorBus`, `IActuatorBus`, `ISloshModel`, `IDriverInput`, `IClock`,
  `IMessageBus`.
- **AI actor observation = realistic sensor data only.** Ground truth (true
  liquid CoM, slosh amplitudes, attitude, future road/input, physics internals)
  goes to the critic and logging only — never the actor.
- **Determinism:** fixed timestep, seeded RNG for every stochastic element, slosh
  ODE integrated outside PhysX. No frame-rate-dependent logic.
- **Model latency, sensor noise/bias, and actuator non-idealities from day one.**
- **Build the baseline controllers (specific-force alignment, PID, privileged
  MPC) before/alongside RL.** Every RL result is reported against them.
- SI units, right-handed frames, timestamps on every message.

## Do NOT install / adopt without an explicit decision

ROS 2 · Unity ML-Agents · SPH/particle-fluid plugins (Obi, Zibra) · CFD ·
full WheelCollider vehicle sim · cloud/CI infrastructure. Rationale in
design-review §21 and setup-status §4.

## Slosh model

v1 = custom analytic **equivalent mechanical model** (1 pendulum per axis,
integrated in `FixedUpdate`, two-way coupled), behind `ISloshModel`. Shallow-water
and multi-mode models are phase-2 drop-in replacements. design-review §6.

## Environment facts

- Unity **6000.5.10f1**, pinned via committed `ProjectSettings/ProjectVersion.txt`.
  Same version on Mac and Windows. **Built-in render pipeline** (not URP).
- Unity project is at the **repo root** (`Assets/`, `Packages/`,
  `ProjectSettings/` alongside `docs/`).
- macOS arm64 (Apple M4 Max). Windows may be used later — keep config
  platform-neutral.
- Git on `main`, Git LFS active. `gh` authed as `yimsemin`. No remote yet.
- Generated files (`Library/`, `*.sln`, `*.csproj`, `Logs/`, `UserSettings/`,
  `Temp/`) are gitignored.
- `Packages/manifest.json` is minimal. When phase 1 is approved, add
  `com.unity.test-framework` and `com.unity.inputsystem` first.
- Headless verify: `"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -quit -nographics -projectPath . -logFile -`

## Current state

Design approved; **phase 1 well underway** (see
[docs/phase1-progress.md](docs/phase1-progress.md) for the live status). Built:
the engine-free deterministic sim core, vehicle (3-DOF sprung mass +
friction-circle) + road model, 2-DOF gimbal actuator with thermal limits, two
slosh models (pendulum ROM + shallow water) under bracket discipline, sensor
suite, baseline controllers, the evaluation harness, a playable human-drive
scene, the RL environment + TCP training bridge, and human-drive trace
record/replay (`DriverTrace`; `P` records / `L` replays in the play scene).
Findings F1–F4 in [docs/findings.md](docs/findings.md). The first PPO run
happened — it does not learn (the F1/F4 anticipation cliff, not a bug;
`docs/rl.md`). Session commits go straight to `main` (solo local repo — no
feature branches).

**Next (the agreed plan — [docs/experiment-plan.md](docs/experiment-plan.md)):**
the recorded-drive evaluation and the feasibility ceiling (`HindsightOptimizer`,
step 3) are both built and run. **The decision gate did not pass — finding F5:**
with full hindsight and real actuator limits the best gimbal trajectory still
spills more than a mechanically locked tank on the owner's recorded drives.
**Residual RL is on hold** pending a premise adjustment (tank freeboard, road
severity, or a hybrid lock-capable action space) and a step-3 re-run. **Do not
start residual RL or re-run PPO-from-scratch until that gate passes.**

## Docs discipline

When a design decision changes, update the relevant `docs/*.md` in the same
change. The docs are how the owner tracks the project.

## Git & commits (applies to every session in this repo)

- **Solo local repo with a GitHub remote (`origin`).** Work on `main`, commit
  directly, **no feature branches**.
- **Standing authorization:** Claude commits to `main` **and pushes to
  `origin/main`** as each logical step completes — without asking each time.
  Commit step-by-step (one self-contained change per commit: a feature + its
  tests + its doc updates), not one giant end-of-session commit — the owner
  tracks progress through the history. Run the EditMode suite before committing
  code; note the pass/skip count in the message.
- **Still ask first** for anything that rewrites or discards history (force-push,
  rebase of pushed commits, `reset --hard` across commits, branch/tag deletion).
- **Commit message:** a concise summary line, then a short body explaining *why*
  when it isn't obvious. End every commit with:
  `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`
- **Do NOT commit generated or local files:** `Library/ Temp/ Logs/
  UserSettings/`, `Assets/FluidStabilizer/Scenes/PlayScene.unity` (regenerated
  by SceneBuilder), `Traces/` (recorded drives — local data; a curated held-out
  set is force-added deliberately when it stabilises), `python/.venv/
  python/runs/`, `*.pt *.onnx`. **Do** commit `.meta` files for tracked assets.
- `.gitignore` already covers these; if a new tool writes generated output,
  add it to `.gitignore` in the same change.
