# CLAUDE.md — guidance for Claude Code in this repo

## What this project is

`fluid-stabilizer` — a Physical AI simulation. An AI controls a 2-axis
(pitch/roll) stabilizer under an open liquid tank on a **human-driven** truck, to
minimize liquid spillage. The AI does **not** drive. Full context:
[docs/project-brief.md](docs/project-brief.md).

## Read before working

1. [docs/project-brief.md](docs/project-brief.md) — the goal (owner's vision).
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

Design + verified empty Unity project. **No feature code yet.** Environment,
Git, and the Unity project are done and committed locally. **Waiting for the
owner's decision on the design review** before implementing anything.

## Docs discipline

When a design decision changes, update the relevant `docs/*.md` in the same
change. The docs are how the owner tracks the project.
