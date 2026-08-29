# Phase 1 progress

Tracks the phase-1 scope from `design-review.md` §20. Design approved 2026-08-27.

> **Resume here (next session):** plan review 2026-08-29 → new
> [project-goals.md](project-goals.md) (three-way comparison as the headline
> result; final-product architecture = finished Unity app + separate Python
> training, visible bridge). **Road model landed** (decision D6,
> [road-model.md](road-model.md)) — grade/bank/roughness/potholes + a suspension
> heave DOF; disabled by default so the F2–F4 guards are unchanged; two new
> `RoughRoad*` scenarios; `-rlRoad` flag; `G` toggles it in the play scene.
> **Next, in order:** (1) get the RL server running and run a first PPO job,
> compare vs F4 [being debugged in a separate session]; (2) human trace
> record/replay (item 3) so the policy trains on real driving; (3) domain
> randomization (item 13); (4) Phase 1.5 — the product shell (in-Unity control
> panel, standalone build, ONNX inference, Python trainer GUI).

| # | Item | Status |
|---|---|---|
| 1 | Unity project + docs + `frames-units-conventions.md` locked | ✅ done |
| 2 | `SimulationCore`: fixed-step scheduler, `IClock`, seeded RNG | ✅ done — `ManualClock`, `DeterministicRandom` (PCG32), `SimulationLoop` (600/200/100 Hz cadence, bundle latency, GT recording), engine-free |
| 3 | Vehicle: sprung mass + friction-circle bicycle model + road; `IDriverInput` | 🟡 `VehicleModel` done — now 3-DOF sprung mass (pitch + roll + **heave**). Drivers: `ConstantDriver`, `ScriptedManeuverDriver`, `RandomManeuverDriver` (aggressive), `HighwayDriver` (realistic band-limited — see finding F2). **Road model done** (decision D6): `RoadModel` — grade/bank/roughness + Poisson potholes as a function of distance, deterministic, disabled by default, `Severity` knob, 8 tests. `RoughRoadHighway` / `RoughRoadLowFreeboard` scenarios; `-rlRoad {none\|mild\|rough}`; `G` toggles it live in the play scene; HUD shows grade/bank/surface. **Human keyboard/gamepad input done** (`HumanDriverInput`, item 11). **Still not started: trace record/replay** — the next task, so the policy can train on the owner's real driving (project-goals.md ③). |
| 4 | Tank on 2-DOF actuated gimbal, pivot at CoM | ✅ `TankKinematics` + two-way slosh↔joint coupling (decision D3: reflected inertia + lagged load, pivot not translated by joint motion) |
| 5 | `ISloshModel` + pendulum ROM + continuous overflow volume | ✅ `PendulumSloshModel`: 1 planar pendulum/axis + fixed mass, rectangular-tank first-mode ωₙ & participating mass, rotating-frame EOM, full liquid reaction, weir-style continuous overflow that drains the fill. Verified: natural frequency matches theory ±15%, damping decays, response direction correct, spills under resonant shake / not when calm. Surface-slope gain still needs calibration. **Phase-2 model A landed:** `ShallowWaterSloshModel` — 2-D Saint-Venant, ~48×28 grid, Rusanov FV, reflective walls + weir overflow, deterministic CFL sub-stepping, quasi-steady reaction + shared reflected-inertia estimate (decision D4). 13 tests: lake-at-rest exact, ring-down frequency vs theory ±15%, mass+spill conservation, low-amplitude agreement with the pendulum ROM, determinism, 40 s no-blow-up. Grid-converged + CFL-stable across all scenarios (`SolverHealth`/`GridConvergence`). Wave-breaking eddy-viscosity + rim run-up return added → **findings F3** (bracket-model discipline) and **F3 addendum → D5** (reaction ≈ 2× the ROM, actuator resize). Multi-mode still deferred. |
| 6 | Actuator model per axis | 🟡 `GimbalActuator`: transport delay, **PD+I position servo** (rate cmd → setpoint), peak-torque saturation, Coulomb+viscous friction, rate + travel limits with anti-windup, reflected-inertia input, external-load clamp, current estimate. **`Locked` mode (rigid joint, no torque) for the `Hold` baseline; I²t winding-heat model derating peak→continuous torque (decision D5); actuator resized (peak 900, continuous 420 N·m).** Backlash present but off by default; motor+gearbox detail pending |
| 7 | Sensor layer + DTOs + noise/bias/delay/rate | 🟡 `SensorSuite`: vehicle+tank IMU (specific force, no gravity split), encoders (quantised), motors, 4 load cells, optional level/CAN; per-sample noise + per-episode bias; bundle latency in the loop. Load-cell slosh redistribution + bias random-walk/misalignment pending |
| 8 | `ISensorBus` / `IActuatorBus` + in-process impl | 🟡 `IController` seam + DTOs carry the contract; explicit bus deferred |
| 9 | Baseline controllers: specific-force alignment, PID, privileged MPC | 🟡 `Hold` (now a **locked rigid joint**, decision D5), `SpecificForceAlignment`, `SpecificForceAlignment+I`, `PrivilegedFeedforwardTilt` done + evaluated. `LqrSloshController`/`AxisLqr` built but **shelved** (design-model mismatch → resonates). **Finding F4: against a rigid `Hold`, no analytic reactive tilt controller wins on any scenario — beating rigid needs anticipation/learning.** (F2 superseded.) Privileged MPC on the real actuator is future work. |
| 10 | Eval harness: fixed scenarios, metrics, baseline comparison, GT logging | ✅ `Evaluator` + `ScenarioSet` (6 scenarios) + `AggregateResult` + deterministic text report. |
| 11 | Playable human-drive scene | ✅ `FluidStabilizer.Sim`: `SimulationRunner` (real-time stepping), `HumanDriverInput` (keyboard + gamepad), `SimView`, `Hud`, `PlayControls` (live stabilizer switch, cameras, slow-mo), `Frames` (SI↔Unity), `SceneBuilder` editor script → `Assets/FluidStabilizer/Scenes/PlayScene.unity`. See `docs/human-drive.md`. Liquid is a proxy (pendulum ROM); real height-field viz waits for shallow water. |
| 12 | RL path: Gymnasium socket adapter + PPO, asymmetric critic, frame-stack | 🟡 C# side done + tested: `FluidStabilizer.Rl` (`ObservationEncoder` frame-stack, `PrivilegedEncoder` for the asymmetric critic, `RewardFunction` §11, `RlEnvironment` deterministic reset/step, `TrainingServer` length-prefixed binary TCP — loopback-tested bit-identical to the in-process env) + `RlServerLauncher` (`-executeMethod` host). Python side written (`python/`: socket client, `gymnasium.Env`, CleanRL-style asymmetric-critic PPO, `play.py`) but **not yet run** (needs `torch`). See `docs/rl.md`. Curriculum automation + parallel rollouts pending. |
| 13 | Domain-randomization config (narrow) + per-episode param logging | 🟡 config structs exist with nominal values; the randomizer + logging pending |
| 14 | Keep docs in sync | ongoing |

## Assemblies (see `Assets/FluidStabilizer/README.md`)

`Contracts` (engine-free) · `Core` (engine-free) · `Controller` (engine-free) ·
`Sim` (Unity) · `Tests.EditMode`.

## Phase 1.5 — the product shell (after a first RL result)

Not started; scoped in [project-goals.md](project-goals.md). In-Unity control
panel (all config + run/record/evaluate + live metrics, replacing terminal
flags); a standalone build (`BuildPipeline` menu item → self-contained app);
an ONNX inference path so the finished Unity app runs a trained policy with no
Python; a lightweight Python trainer GUI / experiment launcher. Deliberately
sequenced *after* RL proves out — don't build the shell around an unproven core.

## Verification so far

- Headless compile: clean (0 errors, 0 warnings).
- EditMode tests: **80 pass, 8 skipped** (skipped = `[Explicit]` tuning/trace
  diagnostics). Includes `RoadModelTests` (8) — determinism, step-size
  independence, roughness RMS, severity scaling, grade bound, localised events,
  and an in-loop check that a rough road adds slosh energy. Covers the
  deterministic core, `PendulumSloshModel` and
  `ShallowWaterSloshModel` physics (natural frequency vs theory, damping,
  response direction, spill under resonant shake, mass conservation,
  cross-model agreement), the coupled loop (determinism, no blow-up over 40 s),
  the baselines, and finding F1 (naive control excites slosh + worsens spill).
- Run: `"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -`
- Controller comparison report:
  `-testFilter FluidStabilizer.Tests.BaselineControllerTests.Report_PrintsAndIsDeterministic` (prints the table).
- Re-run tests headless:
  `"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -`
