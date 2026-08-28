# Findings

Empirical results from the simulation as it is built. Newest first.

---

## F3 — 2026-08-28 — The shallow-water reference model contradicts F2: tilt control never beats "hold rigid"

**Status: project-shaping. Needs a direction decision (see end).**

The deciding experiment promised in F1 (interpretation 1). `ShallowWaterSloshModel`
— a 2-D depth-averaged Saint-Venant solver on a ~48×28 grid (decision D4) — was
dropped in behind `ISloshModel` and the whole baseline evaluation re-run
(6 scenarios × 4 controllers × 6 seeds, `ShallowWaterValidationTests`).

### Result — mean spill [L], `Hold` vs the best tilt controller

| scenario | Hold (ROM → SW) | best tilt (ROM → SW) | ROM verdict | SW verdict |
|---|---|---|---|---|
| HighwayCalm | 0 → **0** | 0 → 0.37 | tie | tilt slightly worse |
| HighwayBrisk | — → **0** | — → 1.1 | — | tilt worse |
| HighwayBriskLowFreeboard (0.27 m) | 6.98 → **42.1** | 0.39 → 45.6 | **tilt wins ~15×** | **tilt ties/worse** |
| ScriptedManeuver | — → 99.8 | — → **17.3** | — | tilt wins ~6× |
| AggressiveRandom | — → 111 | — → 126 | tilt worse (F1) | tilt worse |
| HighFillAggressive | — → 264 | — → 283 | tilt worse (F1) | tilt worse |

Under the shallow-water model **no analytic tilt controller beats holding the
tank rigid on any highway scenario**, and the one scenario where F2 said tilt
control wins by 15× (`HighwayBriskLowFreeboard`) now has tilt control tying or
slightly losing — while `Hold` itself spills ~6× more than the pendulum ROM
predicted. `PrivilegedFeedforward` (most aggressive tilting) is the worst
controller in every scenario. Only the single-axis `ScriptedManeuver` still
favours tilt control (~6×).

### The result is not a numerical artifact

- **Grid convergence** (`GridConvergence_…`): spill on `HighwayBriskLowFreeboard`/
  `Hold` is 27.6 → 27.0 → 27.0 L across 30×18, 48×28, 71×41 grids. Mesh-independent.
- **Solver health** (`SolverHealth_AcrossScenarios`): sub-step count peaks at
  2–4 (cap 64), the CFL cap is never hit, the |u| ≤ 8 m/s clamp never fires — in
  *any* scenario including the aggressive ones. The scheme runs well inside its
  stability envelope.
- **Low-amplitude agreement** holds: under a gentle step acceleration the
  shallow-water surface tilt tracks the pendulum ROM within ~2 % (13 % at
  5 m/s²). The models only diverge at large amplitude — which is exactly where
  the ROM caps wave elevation at 0.8·depth and leans on tuned quadratic damping.
- **Kinematic controllability still exists**: with *perfect* tank-tilt tracking
  the wall wave drops to 0.40× "held" (0.52× through a 2 Hz lag,
  `IdealTiltTracking_Helps_UnderShallowWater`). The plant *is* controllable by
  tilt — the sensor-driven analytic laws just don't capture it in closed loop
  (same story as F1's lagged-tracking diagnostic).

### Why the two models disagree

The pendulum ROM has one DOF per axis. Commanding the tank to a low-passed felt
tilt slows the effective gravity rotation the pendulum sees and it settles. The
shallow-water field has spatial structure: rotating the tank physically sweeps
the walls through the liquid and rotates the gravity vector across the whole
surface, launching a travelling wave that runs up the far wall and tops the rim
mid-manoeuvre. The ROM cannot represent "the wave sloshed to the other end and
climbed the wall while the tank was rotating"; the shallow-water model can, and
says that is the dominant spill mechanism.

### Which model to believe

Shallow-water equations are known to **over-predict wall run-up** — they cannot
break a wave, so it steepens into a bore that carries water up the wall instead
of white-capping and dissipating. So the truth is likely **bracketed**: the
pendulum ROM (elevation capped, amplitude damping tuned) is an optimistic lower
bound on spill; the raw shallow-water solver is a pessimistic upper bound. Both
agree on **direction** — moving the tank makes waves — and on the ranking
(`Hold` ≤ `SFA` ≈ `PID` < `PrivilegedFeedforward`).

### Interpretations (the decision)

1. **Believe the shallow-water model (roughly).** Then 2-axis tank-tilt
   stabilization by any *reactive* law is close to useless for spill control,
   even on highways, and the project's value is in (a) anticipatory / learned
   control — the ideal-tilt diagnostic shows 2.5× headroom a preview-based MPC
   or RL policy might reach — and/or (b) reframing to "robustness under mild
   conditions + graceful degradation". The pendulum ROM would need its
   elevation/spill calibration re-fit against shallow water before it is used as
   the RL training model.
2. **The shallow-water model is still too violent.** Add a wave-breaking energy
   sink (cap the local Froude number / add roller dissipation), or a rim
   run-up/return term so not every wave that touches the rim leaves. Re-run and
   see whether the highway scenarios come back to "tilt control helps".
3. **Keep both as bracket models.** Report every controller against *both* the
   ROM and shallow water; treat a result as real only if it holds under both.
   RL trains on the ROM (cheap) and is validated on shallow water.

### Guards / how to re-check

`ShallowWaterValidationTests` — all `[Explicit]` (the full sweep is ~25 min, one
episode ~10–15 s; too slow for the default suite, unlike the ROM guards in
`EvaluatorTests`). Run `SolverHealth_AcrossScenarios` +
`BaselineEvaluation_UnderShallowWater_PrintsReport` after any change to the
shallow-water model, the actuator, or the analytic controllers.

---

## F2 — 2026-08-28 — A realistic driver flips F1: tilt control works for highway driving

Interpretation 3 of F1 (the synthetic driver was too broadband) largely holds.

`HighwayDriver` — slow lane-keeping wander plus **smooth** (raised-cosine)
maneuvers: lane changes, a highway curve held and released, easing for traffic,
a firm-but-not-emergency brake. No sharp edges near the slosh frequency.

Re-running the evaluation with it (`HighwayBriskLowFreeboard`: 0.27 m freeboard,
briskness 0.9, 40 s, 6 seeds):

| controller | spill (L, mean) | spill / episodes |
|---|---|---|
| PID (SpecificForceAlignment+I) | **0.39** | 4/6 |
| SpecificForceAlignment | **0.44** | 4/6 |
| PrivilegedFeedforwardTilt | 0.50 | 4/6 |
| **Hold** | **6.98** (max 38.8) | 4/6 |

The analytic tilt controllers now spill **~15× less than doing nothing**. For
realistic highway driving the task is well-posed and rule-based control helps.

**But the aggressive/adversarial regime is unchanged** (`AggressiveRandom`,
`HighFillAggressive`): every tilt controller still spills more than Hold and runs
2–13× the slosh energy — the sharp broadband maneuvers push the equivalent-
pendulum ROM out of its small-amplitude validity and the tilt loop resonates.

**Net picture — a healthy problem:**
- Highway driving: rule-based tilt control works; RL should match/beat it easily.
- Aggressive transients + plant randomization: where RL / anticipation earns its
  place, and where the shallow-water check (F1 interpretation 1, still to do)
  matters for trusting the numbers.

**LQR shelved as a baseline.** `LqrSloshController` resonates in every scenario:
its linear design model (ideal rate follower) is too far from the real
servo + transport delay + angle-command actuator. `AxisLqr`'s Riccati math is
correct and still tested; a proper MPC on the real actuator model is future work.

Guard: `EvaluatorTests.TiltControl_ClearlyBeatsHold_OnRealisticBriskDriving` and
`NaiveTiltControl_StillExcitesSlosh_UnderAggressiveDriving`.

---

## F1 — 2026-08-28 — No baseline controller beats "hold the tank rigid"

**Superseded in part by F2** (realistic driver) — kept for the reasoning and the
plant-model history. The aggressive-regime finding still stands.

**Status: project-shaping. Needs a direction decision (see end).**

### What was tried (design-review option A: build a real ceiling)

Four baselines, evaluated over the fixed 6-scenario set × 6 seeds
(`BaselineControllerTests.Report_PrintsAndIsDeterministic`):

- **Hold** — command joint angle 0 (tank rigid to the bed). The floor.
- **SpecificForceAlignment** — command the joint *angle* = low-passed felt tilt
  (sensor-only). Servo tracks it.
- **SpecificForceAlignment+I** — the above plus a clamped integral trim.
- **PrivilegedFeedforwardTilt** — command the joint angle = low-passed
  *slosh-free* ideal tilt (privileged: clean specific force).
- **LQR** — per-axis discrete LQR on the coupled slosh + gimbal model, using the
  true slosh state and modal frequency/damping (privileged). Solved via the
  discrete Riccati recursion (`AxisLqr`).

### Result

On the four low/moderate scenarios nobody spills. On the two that discriminate:

| ModerateFreeboard (0.30 m freeboard) | spill (L, mean) |
|---|---|
| **Hold** | **0.4** |
| SpecificForceAlignment | 5.8 |
| SpecificForceAlignment+I | 17.7 |
| PrivilegedFeedforwardTilt | 35.9 |
| LQR | 36.7 |

Every active controller spills **more** than doing nothing, and *more
intervention makes it worse* (the aggressive trackers, Priv and LQR, are worst).
HighFillAggressive shows the same ordering.

### Why

Rotating the tank to track a tilt target excites the first slosh mode — through
the gravity-vector rotation transient and the Euler term (tank angular
acceleration forces the pendulum). For this driver's broadband, event-driven
maneuvers the slosh energy added by moving the tank exceeds the spill reduction
from being aligned.

This is **not** fixable by actuator authority (`ActuatorAuthorityTests`): the
privileged controller stays at ~36–42 L spill across ±30°→±60° travel, 2× rate,
2× torque, 2× bandwidth, and all of them combined. Widening the travel limit
actually makes **Hold worse** (the load-dragged servo swings further off-target
and kicks the slosh), so the ±30° limit is currently *protecting* Hold.

Yet an isolated diagnostic (`PlantControllabilityTests`) shows that *kinematic*
tank tilt tracking a clean sinusoidal maneuver cuts the wall wave ~12× (perfect)
or ~3× (through a 2 Hz lag). So tilt control helps for smooth single-axis input;
it fails for the sharp, two-axis, actuator-in-the-loop case.

### Interpretations (the decision)

1. **Slosh-ROM coupling too pessimistic.** The equivalent-pendulum model's
   response to tank rotation (Euler term + gravity-rotation transient) may
   over-penalize tank motion. A shallow-water model could show control helping.
   → calibrate/upgrade the slosh model before trusting the controllability
   verdict (design-review §6 caveat; this was the deferred "option B").
2. **The task as posed is near-uncontrollable for aggressive driving.** Real tank
   trucks use baffles + careful driving, not active gimbals. 2-axis rotation of
   an open tank may simply not have the authority. → adjust the premise: add
   preview/anticipation, a third (heave) axis, baffles, or accept a "robustness
   under mild conditions" framing.
3. **The synthetic driver is too broadband.** `RandomManeuverDriver` fires sharp
   0.3–1.0 s events that sit near the roll slosh period (~0.8 s) and resonate.
   Real highway driving is gentler and more band-limited. → redesign the driver
   distribution and re-evaluate.

### Model changes made along the way (committed)

- Overflow rewritten as a bounded wedge + weir; wave elevation capped at
  0.8·depth (the old weir over-estimated ~100×).
- Quadratic (amplitude-dependent) slosh damping added — models wave-breaking
  dissipation, keeps the ROM from running far outside small-amplitude validity
  (Dodge/Keulegan).
- Gimbal reported angular acceleration low-passed (18 Hz) so raw servo accel
  doesn't kick the pendulum.
- `StabilizerCommand` gained a `JointAngle` mode (design-review §11); the
  analytic baselines command angle, the actuator servo tracks it. RL keeps
  `JointRate`.
- `AxisLqr` (discrete Riccati) + `LqrSloshController`; `PrivilegedObservation`
  now carries the true modal frequencies/damping.
- Realistic magnitudes: brake/friction ≈ 0.45 g, tank 0.8 m tall, 0.5 fill.

### Guards

`EvaluatorTests.NoBaselineControllerBeatsHold_OnTheDiscriminatingScenario` and
`NaiveFeedbackControllers_ExciteSlosh_AndWorsenSpill` lock in the current state;
if a plant/controller change flips either, this file must be revisited.
