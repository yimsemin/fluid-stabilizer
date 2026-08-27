# Phase 1 progress

Tracks the phase-1 scope from `design-review.md` §20. Design approved 2026-08-27.

| # | Item | Status |
|---|---|---|
| 1 | Unity project + docs + `frames-units-conventions.md` locked | ✅ done |
| 2 | `SimulationCore`: fixed-step scheduler, `IClock`, seeded RNG | ✅ done — `ManualClock`, `DeterministicRandom` (PCG32), `SimulationLoop` (600/200/100 Hz cadence, bundle latency, GT recording), engine-free |
| 3 | Vehicle: sprung mass + friction-circle bicycle model + road; `IDriverInput` | 🟡 `VehicleModel` (longitudinal + bicycle + friction circle + 2nd-order pitch/roll) + `ScriptedManeuverDriver`/`ConstantDriver` done; road grade/bank/bumps hook present but unused; human input + trace record/replay not started |
| 4 | Tank on 2-DOF actuated gimbal, pivot at CoM | 🟡 `TankKinematics` (kinematically attached per decision D1) done; slosh↔joint load coupling still stubbed |
| 5 | `ISloshModel` + pendulum ROM + continuous overflow volume | 🟡 interface + `NullSloshModel` placeholder done; **pendulum ROM is the next task** |
| 6 | Actuator model per axis | 🟡 `GimbalActuator`: transport delay, first-order rate loop, peak-torque saturation, Coulomb+viscous friction, rate + travel limits, external-load input, current estimate. Backlash present but off by default; motor+gearbox detail + continuous-torque thermal model pending |
| 7 | Sensor layer + DTOs + noise/bias/delay/rate | 🟡 `SensorSuite`: vehicle+tank IMU (specific force, no gravity split), encoders (quantised), motors, 4 load cells, optional level/CAN; per-sample noise + per-episode bias; bundle latency in the loop. Load-cell slosh redistribution + bias random-walk/misalignment pending |
| 8 | `ISensorBus` / `IActuatorBus` + in-process impl | 🟡 `IController` seam + DTOs carry the contract; explicit bus deferred |
| 9 | Baseline controllers: specific-force alignment, PID, privileged MPC | 🟡 `HoldController` only; real baselines pending |
| 10 | Eval harness: fixed scenarios, metrics, baseline comparison, GT logging | 🟡 `EpisodeResult` metrics + `GroundTruthFrame` done; scenario set + batch runner + comparison report pending |
| 11 | Playable human-drive scene | ⬜ not started |
| 12 | RL path: Gymnasium socket adapter + PPO, asymmetric critic, frame-stack | ⬜ not started |
| 13 | Domain-randomization config (narrow) + per-episode param logging | 🟡 config structs exist with nominal values; the randomizer + logging pending |
| 14 | Keep docs in sync | ongoing |

## Assemblies (see `Assets/FluidStabilizer/README.md`)

`Contracts` (engine-free) · `Core` (engine-free) · `Controller` (engine-free) ·
`Sim` (Unity) · `Tests.EditMode`.

## Verification so far

- Headless compile: clean (0 errors, 0 warnings).
- EditMode tests: **25/25 pass** — `Double3`/`MathD`, `DeterministicRandom`
  (determinism, named-fork stability, Gaussian moments), `ManualClock`,
  `HoldController`, `GimbalActuator` (direction, rate limit, travel limit,
  command delay), `SimulationLoop` (episode length, bit-identical trace for a
  fixed seed, brake/accel affects speed, tank IMU ≈ 1 g up at rest, hold =
  no motion, null slosh never spills).
- Re-run tests headless:
  `"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -`
