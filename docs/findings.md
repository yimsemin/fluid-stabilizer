# Findings

Empirical results from the simulation as it is built. Newest first.

---

## F1 — 2026-08-28 — No baseline controller beats "hold the tank rigid"

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
