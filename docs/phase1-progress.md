# Phase 1 progress

Tracks the phase-1 scope from `design-review.md` §20. Design approved 2026-08-27.

| # | Item | Status |
|---|---|---|
| 1 | Unity project + docs + `frames-units-conventions.md` locked | ✅ done |
| 2 | `SimulationCore`: fixed-step scheduler, `IClock`, seeded RNG service | 🟡 in progress — `IClock`/`ManualClock`, `IRandomSource`/`DeterministicRandom` (PCG32) done + tested; the step scheduler MonoBehaviour is next |
| 3 | Vehicle: 3-DOF sprung mass + friction-circle bicycle model + road; `IDriverInput` (human + scripted + trace) | ⬜ not started |
| 4 | Tank rigid body on 2-DOF actuated gimbal, pivot near CoM | ⬜ not started |
| 5 | `ISloshModel` + pendulum ROM + continuous overflow volume | 🟡 interface drafted; implementation not started |
| 6 | Actuator model per axis (motor+gear+backlash+delay+friction+limits) | ⬜ not started |
| 7 | Sensor layer + message DTOs + noise/bias/delay/rate | 🟡 DTOs done (`SensorBundle` etc.); sensor models not started |
| 8 | `ISensorBus` / `IActuatorBus` + in-process impl | 🟡 `IController` seam + DTOs done; explicit bus deferred until it earns its place |
| 9 | Baseline controllers: specific-force alignment, PID, privileged MPC | 🟡 `IController` + `HoldController` (null baseline) done; real baselines not started |
| 10 | Eval harness: fixed scenarios, metrics, baseline comparison, GT logging | ⬜ not started |
| 11 | Playable human-drive scene | ⬜ not started |
| 12 | RL path: Gymnasium socket adapter + PPO, asymmetric critic, frame-stack | ⬜ not started |
| 13 | Domain-randomization config (narrow) + per-episode param logging | ⬜ not started |
| 14 | Keep docs in sync | ongoing |

## Assemblies (see `Assets/FluidStabilizer/README.md`)

`Contracts` (engine-free) · `Core` (engine-free) · `Controller` (engine-free) ·
`Sim` (Unity) · `Tests.EditMode`.

## Verification so far

- Headless compile: clean (0 errors).
- EditMode tests: 16/16 pass (`Double3`, `MathD`, `DeterministicRandom`
  determinism + named-fork stability + Gaussian moments, `ManualClock`,
  `HoldController`).
- Re-run tests headless:
  `"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -`
