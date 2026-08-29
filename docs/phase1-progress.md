# Phase 1 progress

Tracks the phase-1 scope from `design-review.md` §20. Design approved 2026-08-27.

> **Resume here (next session):** the step-back session of 2026-08-29 produced an
> agreed plan — see **[experiment-plan.md](experiment-plan.md)** (the
> owner-facing map). Summary:
>
> - The RL bridge runs end-to-end but **PPO-from-scratch cannot learn the task**
>   — not a bug: any reactive gimbal motion pumps slosh and spills (F1/F4), so
>   the good policy is a narrow "hold/anticipate" ridge Gaussian exploration
>   never lands on (`docs/rl.md` "First-run result").
> - **Three concerns resolved:** (a) no route/waypoint system — the unit of "a
>   drive" is a *recorded human trace + fixed road seed*; (b) the headline
>   benchmark becomes *the owner's own held-out recorded drives* (per-drive
>   litres, both slosh models), synthetic scenarios demoted to a training /
>   regression set; (c) build a **feasibility ceiling** before more RL, then use
>   **residual RL** (policy on top of a baseline) with a behaviour-cloning
>   warm-start.
>
> **Next, in order (experiment-plan.md):** (1) **human trace record/replay
> (item 3)** — ✅ *landed this session* (`DriverTrace` + `RecordingDriver` +
> `RecordedTraceDriver`, `TraceRecorder` in the play scene, `P` records / `L`
> replays); (2) rebuild the evaluation around recorded drives; (3) **feasibility
> ceiling** (offline hindsight trajectory optimisation on a fixed drive) — a
> **decision gate**: no RL until a privileged solution beats rigid Hold;
> (4) residual RL; (5) domain randomization → Phase 1.5 product shell.
>
> Context that still holds: [project-goals.md](project-goals.md) (three-way
> comparison = headline result). Road model landed (D6,
> [road-model.md](road-model.md)) — grade/bank/roughness/potholes + heave DOF,
> disabled by default; `RoughRoad*` scenarios; `-rlRoad` flag; `G` toggles it in
> the play scene.

| # | Item | Status |
|---|---|---|
| 1 | Unity project + docs + `frames-units-conventions.md` locked | ✅ done |
| 2 | `SimulationCore`: fixed-step scheduler, `IClock`, seeded RNG | ✅ done — `ManualClock`, `DeterministicRandom` (PCG32), `SimulationLoop` (600/200/100 Hz cadence, bundle latency, GT recording), engine-free |
| 3 | Vehicle: sprung mass + friction-circle bicycle model + road; `IDriverInput` | 🟡 `VehicleModel` done — now 3-DOF sprung mass (pitch + roll + **heave**). Drivers: `ConstantDriver`, `ScriptedManeuverDriver`, `RandomManeuverDriver` (aggressive), `HighwayDriver` (realistic band-limited — see finding F2). **Road model done** (decision D6): `RoadModel` — grade/bank/roughness + Poisson potholes as a function of distance, deterministic, disabled by default, `Severity` knob, 8 tests. `RoughRoadHighway` / `RoughRoadLowFreeboard` scenarios; `-rlRoad {none\|mild\|rough}`; `G` toggles it live in the play scene; HUD shows grade/bank/surface. **Human keyboard/gamepad input done** (`HumanDriverInput`, item 11). **Trace record/replay done** (2026-08-29): `DriverTrace` (small text file: setup echo + one `throttle,brake,steer` row per physics step), `RecordingDriver` (wraps any driver, appends each sample), `RecordedTraceDriver` (deterministic replay). `TraceRecorder` MonoBehaviour in the play scene — `P` records the owner's drive to `<project>/Traces/` (top-of-screen banner shows idle / ● REC / ▶ REPLAY), `L` replays the newest. `DriverTraceTests` (5) incl. a bit-for-bit record→save→load→replay guard. This is the real disturbance data + held-out eval condition for the three-way comparison (project-goals.md ③, [experiment-plan.md](experiment-plan.md)). |
| 4 | Tank on 2-DOF actuated gimbal, pivot at CoM | ✅ `TankKinematics` + two-way slosh↔joint coupling (decision D3: reflected inertia + lagged load, pivot not translated by joint motion) |
| 5 | `ISloshModel` + pendulum ROM + continuous overflow volume | ✅ `PendulumSloshModel`: 1 planar pendulum/axis + fixed mass, rectangular-tank first-mode ωₙ & participating mass, rotating-frame EOM, full liquid reaction, weir-style continuous overflow that drains the fill. Verified: natural frequency matches theory ±15%, damping decays, response direction correct, spills under resonant shake / not when calm. Surface-slope gain still needs calibration. **Phase-2 model A landed:** `ShallowWaterSloshModel` — 2-D Saint-Venant, ~48×28 grid, Rusanov FV, reflective walls + weir overflow, deterministic CFL sub-stepping, quasi-steady reaction + shared reflected-inertia estimate (decision D4). 13 tests: lake-at-rest exact, ring-down frequency vs theory ±15%, mass+spill conservation, low-amplitude agreement with the pendulum ROM, determinism, 40 s no-blow-up. Grid-converged + CFL-stable across all scenarios (`SolverHealth`/`GridConvergence`). Wave-breaking eddy-viscosity + rim run-up return added → **findings F3** (bracket-model discipline) and **F3 addendum → D5** (reaction ≈ 2× the ROM, actuator resize). Multi-mode still deferred. |
| 6 | Actuator model per axis | 🟡 `GimbalActuator`: transport delay, **PD+I position servo** (rate cmd → setpoint), peak-torque saturation, Coulomb+viscous friction, rate + travel limits with anti-windup, reflected-inertia input, external-load clamp, current estimate. **`Locked` mode (rigid joint, no torque) for the `Hold` baseline; I²t winding-heat model derating peak→continuous torque (decision D5); actuator resized (peak 900, continuous 420 N·m).** Backlash present but off by default; motor+gearbox detail pending |
| 7 | Sensor layer + DTOs + noise/bias/delay/rate | 🟡 `SensorSuite`: vehicle+tank IMU (specific force, no gravity split), encoders (quantised), motors, 4 load cells, optional level/CAN; per-sample noise + per-episode bias; bundle latency in the loop. Load-cell slosh redistribution + bias random-walk/misalignment pending |
| 8 | `ISensorBus` / `IActuatorBus` + in-process impl | 🟡 `IController` seam + DTOs carry the contract; explicit bus deferred |
| 9 | Baseline controllers: specific-force alignment, PID, privileged MPC | 🟡 `Hold` (now a **locked rigid joint**, decision D5), `SpecificForceAlignment`, `SpecificForceAlignment+I`, `PrivilegedFeedforwardTilt` done + evaluated. `LqrSloshController`/`AxisLqr` built but **shelved** (design-model mismatch → resonates). **Finding F4: against a rigid `Hold`, no analytic reactive tilt controller wins on any scenario — beating rigid needs anticipation/learning.** (F2 superseded.) Privileged MPC on the real actuator is future work. |
| 10 | Eval harness: fixed scenarios, metrics, baseline comparison, GT logging | ✅ `Evaluator` + `ScenarioSet` (8 scenarios) + `AggregateResult` + deterministic text report. **`TraceEvaluator`** (experiment-plan step 2c): replays recorded drives × baselines × both slosh models, per-drive litre tables — menu ▸ FluidStabilizer ▸ Evaluate Recorded Drives → `Logs/trace-eval.txt`. `TraceEvaluatorTests` (2). **`HindsightOptimizer`** (experiment-plan step 3 — the feasibility ceiling): gradient-free `(1+1)`-ES over a low-dim gimbal-rate trajectory, full hindsight, real actuator limits — menu ▸ FluidStabilizer ▸ Feasibility Ceiling (Hindsight) → `Logs/hindsight-eval.txt` + `Logs/hindsight/*.traj.csv`. `HindsightOptimizerTests` (5). **Decision gate does NOT pass — finding F5**: hindsight-optimal gimbal control still loses to a locked tank on the recorded drives; residual RL (item 12) is blocked on a premise adjustment. |
| 11 | Playable human-drive scene | ✅ `FluidStabilizer.Sim`: `SimulationRunner` (real-time stepping), `HumanDriverInput` (keyboard + gamepad), `SimView`, `Hud`, `PlayControls` (live stabilizer switch, cameras, slow-mo), `Frames` (SI↔Unity), `SceneBuilder` editor script → `Assets/FluidStabilizer/Scenes/PlayScene.unity`. See `docs/human-drive.md`. **Trace record/replay** (`TraceRecorder`, `P`/`L`). **Side-by-side comparison** (`ComparisonRunner`, `C`): one recorded trace replayed through 3 stabilizers at once, per-lane spill/near-rim/slosh-energy panel — experiment-plan.md step 2a. **Road rendering** (`RoadRibbon`): a scrolling road strip (grade/bank/roughness/potholes) under each truck when the road is on — step 2b. `ComparisonRunnerTests` (1), `RoadRibbonTests` (2). |
| 12 | RL path: Gymnasium socket adapter + PPO, asymmetric critic, frame-stack | 🟡 C# side done + tested: `FluidStabilizer.Rl` (`ObservationEncoder` frame-stack, `PrivilegedEncoder` for the asymmetric critic, `RewardFunction` §11, `RlEnvironment` deterministic reset/step, `TrainingServer` length-prefixed binary TCP — loopback-tested bit-identical to the in-process env) + `RlServerLauncher` (`-executeMethod` host). Python side (`python/`: socket client, `gymnasium.Env`, asymmetric-critic PPO, `play.py`) **run end-to-end 2026-08-29**; trainer brought up to the CleanRL reference (obs + return normalisation). **PPO-from-scratch does not learn the task** — it's the F1/F4 anticipation cliff, not a bug (see `docs/rl.md` "First-run result"). Needs residual RL / curriculum / warm-start = a task-design decision. Curriculum automation, checkpoint-resume, parallel rollouts, spill-metric-over-TCP all still pending. |
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
- EditMode tests: **85 pass, 8 skipped** (skipped = `[Explicit]` tuning/trace
  diagnostics). Includes `DriverTraceTests` (5) — bit-for-bit record→replay,
  text-format round-trip, end-of-trace hold, `ToConfig`. Includes
  `RoadModelTests` (8) — determinism, step-size
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
