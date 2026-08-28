# Phase 1 progress

Tracks the phase-1 scope from `design-review.md` §20. Design approved 2026-08-27.

> **Resume here (next session):** step **A** done — `ShallowWaterSloshModel`
> (+ wave-breaking + rim run-up), 13 tests, validation sweep, height-field mesh
> viz + `M` scene toggle. **Finding F3 resolved:** the raw NLSW solver flipped
> F2, but with wave-breaking modelled the two slosh models agree on direction
> everywhere — gentle highway fine, brisk-low-freeboard tilt helps a little,
> aggressive transients defeat reactive tilt (the RL target). Adopted
> **bracket-model discipline**: RL trains on the pendulum ROM, every result
> validated on shallow water. **Phase-1 items 12 (RL socket adapter) + 13
> (domain randomization) are unblocked** — that's the next work.
> EditMode: 61 pass / 8 skipped (5 new `[Explicit]` SW diagnostics).

| # | Item | Status |
|---|---|---|
| 1 | Unity project + docs + `frames-units-conventions.md` locked | ✅ done |
| 2 | `SimulationCore`: fixed-step scheduler, `IClock`, seeded RNG | ✅ done — `ManualClock`, `DeterministicRandom` (PCG32), `SimulationLoop` (600/200/100 Hz cadence, bundle latency, GT recording), engine-free |
| 3 | Vehicle: sprung mass + friction-circle bicycle model + road; `IDriverInput` | 🟡 `VehicleModel` done. Drivers: `ConstantDriver`, `ScriptedManeuverDriver`, `RandomManeuverDriver` (aggressive), `HighwayDriver` (realistic band-limited — see finding F2). Road grade/bank/bumps hook present but unused; **human input (next) + trace record/replay** not started. |
| 4 | Tank on 2-DOF actuated gimbal, pivot at CoM | ✅ `TankKinematics` + two-way slosh↔joint coupling (decision D3: reflected inertia + lagged load, pivot not translated by joint motion) |
| 5 | `ISloshModel` + pendulum ROM + continuous overflow volume | ✅ `PendulumSloshModel`: 1 planar pendulum/axis + fixed mass, rectangular-tank first-mode ωₙ & participating mass, rotating-frame EOM, full liquid reaction, weir-style continuous overflow that drains the fill. Verified: natural frequency matches theory ±15%, damping decays, response direction correct, spills under resonant shake / not when calm. Surface-slope gain still needs calibration. **Phase-2 model A landed:** `ShallowWaterSloshModel` — 2-D Saint-Venant, ~48×28 grid, Rusanov FV, reflective walls + weir overflow, deterministic CFL sub-stepping, quasi-steady reaction + shared reflected-inertia estimate (decision D4). 13 tests: lake-at-rest exact, ring-down frequency vs theory ±15%, mass+spill conservation, low-amplitude agreement with the pendulum ROM, determinism, 40 s no-blow-up. Grid-converged + CFL-stable across all scenarios (`SolverHealth`/`GridConvergence`). **→ finding F3** (tilt control never beats Hold under this model; decision pending). Multi-mode still deferred. |
| 6 | Actuator model per axis | 🟡 `GimbalActuator`: transport delay, **PD+I position servo** (rate cmd → setpoint), peak-torque saturation, Coulomb+viscous friction, rate + travel limits with anti-windup, reflected-inertia input, external-load clamp, current estimate. Backlash present but off by default; motor+gearbox detail + continuous-torque thermal model pending |
| 7 | Sensor layer + DTOs + noise/bias/delay/rate | 🟡 `SensorSuite`: vehicle+tank IMU (specific force, no gravity split), encoders (quantised), motors, 4 load cells, optional level/CAN; per-sample noise + per-episode bias; bundle latency in the loop. Load-cell slosh redistribution + bias random-walk/misalignment pending |
| 8 | `ISensorBus` / `IActuatorBus` + in-process impl | 🟡 `IController` seam + DTOs carry the contract; explicit bus deferred |
| 9 | Baseline controllers: specific-force alignment, PID, privileged MPC | 🟡 `Hold`, `SpecificForceAlignment`, `SpecificForceAlignment+I`, `PrivilegedFeedforwardTilt` done + evaluated. `LqrSloshController`/`AxisLqr` built but **shelved** (design-model mismatch → resonates). **Finding F2: with the realistic `HighwayDriver` the tilt baselines beat Hold ~15×; under aggressive driving they still struggle.** LQR-on-real-actuator (MPC) is future work. |
| 10 | Eval harness: fixed scenarios, metrics, baseline comparison, GT logging | ✅ `Evaluator` + `ScenarioSet` (6 scenarios) + `AggregateResult` + deterministic text report. |
| 11 | Playable human-drive scene | ✅ `FluidStabilizer.Sim`: `SimulationRunner` (real-time stepping), `HumanDriverInput` (keyboard + gamepad), `SimView`, `Hud`, `PlayControls` (live stabilizer switch, cameras, slow-mo), `Frames` (SI↔Unity), `SceneBuilder` editor script → `Assets/FluidStabilizer/Scenes/PlayScene.unity`. See `docs/human-drive.md`. Liquid is a proxy (pendulum ROM); real height-field viz waits for shallow water. |
| 12 | RL path: Gymnasium socket adapter + PPO, asymmetric critic, frame-stack | ⬜ not started |
| 13 | Domain-randomization config (narrow) + per-episode param logging | 🟡 config structs exist with nominal values; the randomizer + logging pending |
| 14 | Keep docs in sync | ongoing |

## Assemblies (see `Assets/FluidStabilizer/README.md`)

`Contracts` (engine-free) · `Core` (engine-free) · `Controller` (engine-free) ·
`Sim` (Unity) · `Tests.EditMode`.

## Verification so far

- Headless compile: clean (0 errors, 0 warnings).
- EditMode tests: **60 pass, 3 skipped** (skipped = `[Explicit]` tuning/trace
  diagnostics). Covers the deterministic core, `PendulumSloshModel` and
  `ShallowWaterSloshModel` physics (natural frequency vs theory, damping,
  response direction, spill under resonant shake, mass conservation,
  cross-model agreement), the coupled loop (determinism, no blow-up over 40 s),
  the baselines, and finding F1 (naive control excites slosh + worsens spill).
- Run: `"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -`
- Controller comparison report:
  `-testFilter FluidStabilizer.Tests.BaselineControllerTests.Report_PrintsAndIsDeterministic` (prints the table).
- Re-run tests headless:
  `"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -`
