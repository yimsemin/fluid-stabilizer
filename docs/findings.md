# Findings

Empirical results from the simulation as it is built. Newest first.

> **2026-08-29 — tank resized `TankHeightZ` 0.8 → 0.6 m** (a shallower open tank
> spills far more readily; it puts the three-way comparison in the regime that
> matters — see [experiment-plan.md](experiment-plan.md)). **The numeric tables
> in F1–F4 below predate this** and a full re-sweep is pending. The *direction*
> of every finding is unchanged, and on the discriminating scenarios it is now
> **stronger**: on the 0.6 m tank, locked Hold vs the reactive tilt laws —
> `HighwayBriskLowFreeboard` 0.67 L vs ~27 L, `RoughRoadHighway` 0 L vs ~8 L,
> `RoughRoadLowFreeboard` 0.1 L vs ~30 L, `HighFillAggressive` 35 L vs ~68 L.
> Rigid still wins everywhere; anticipation is still the only way past it.
> Guards `EvaluatorTests` retuned to the new magnitudes.

---

## F5 — 2026-08-29 — Feasibility ceiling: full-hindsight gimbal control still loses to a locked tank on the recorded drives

**The experiment-plan.md step-3 decision gate — and it does not pass.**

`HindsightOptimizer` (engine-free, `FluidStabilizer.Core.Sim.Eval`): for one fixed
recorded drive, a gradient-free `(1+1)`-ES with random restarts searches a
low-dimensional gimbal-**rate** trajectory (per axis, 6–10 Catmull-Rom knots over
the whole drive, scaled to the real `MaxRateRadPerSec`) for the feed-forward
motion that minimises spill. Full hindsight (the optimiser sees the entire
drive), real actuator limits (rate, ±30° travel, 900/420 N·m thermal), replayed
bit-deterministically. Restart 0 starts from the zero (do-nothing) trajectory, so
the result can never "beat" by losing. Run via **Menu ▸ FluidStabilizer ▸
Feasibility Ceiling (Hindsight)** or `-executeMethod …HindsightEvalRunner.Run`
→ `Logs/hindsight-eval.txt` + per-drive optimal trajectories in
`Logs/hindsight/*.traj.csv`.

### Result — 6 held-out drives (road on, fill 0.62, 30–48 s), spill [L]

| drive | Hold (locked) | do-nothing (servo @0) | ceiling (hindsight) | ceiling − Hold |
|---|---|---|---|---|
| 132335 | 19.3 | 25.6 | 24.1 | **−4.8** |
| 132407 | 19.4 | 25.3 | 24.9 | **−5.4** |
| 132508 | 22.4 | 29.6 | 27.2 | **−4.8** |
| 140245 | 20.6 | 25.0 | 22.8 | **−2.2** |
| 140321 | 20.8 | 26.6 | 26.1 | **−5.2** |
| 140440 | 21.1 | 28.0 | 27.2 | **−6.1** |

Pendulum ROM. Under **shallow water** the ceiling is 30–140 L *worse* than Hold on
every drive (one −10 L "win" appeared on a single drive at one search budget and
vanished at every other budget / knot count / rate authority — a ROM-fit artefact,
exactly what bracket discipline F3 exists to catch).

**Why more search won't flip it.** As the budget grows (42 → 364 evaluations,
6 → 16 knots/axis, rate authority 0.2 → 1.0) the pendulum ceiling creeps down
from ≈ 25.5 L toward ≈ 23 L on drive 132335 — i.e. active anti-slosh recovers
only ≈ 2–3 L of the ≈ 6 L the servo loses to the locked joint just by being an
active servo — and plateaus there, still ≈ 4 L above locked Hold. The wall is the
`do-nothing` floor (25 L), not the optimiser.

**Limitation to keep in mind:** the trajectory is deliberately low-bandwidth
(6–16 knots over a 30–48 s drive ≈ 0.1–0.3 Hz). It can shape the tank to the
maneuver profile (braking / cornering events last 1–5 s) but cannot do
per-wave-cycle anti-slosh modulation at the ≈ 1.2 Hz roll-slosh frequency. A
higher-bandwidth feed-forward might do better — but it would also be far outside
what a causal sensor-only policy could ever reproduce, so it does not change the
gate decision.

### What it means

1. **The servo commanding zero already loses ≈ 5–7 L to a mechanically locked
   joint** (F3-addendum / D5, now quantified on real drives): the 4 kW-class
   gimbal is torque-limited against the true slosh moment, so "hold 0" is dragged
   off target and *pumps* the mode. `do-nothing` is the optimiser's own starting
   point.
2. **No feed-forward trajectory the optimiser found recovers that gap**, let alone
   beats Hold — even with the entire future known. This is **F1/F4 sharpened**:
   not just reactive control, but *hindsight-optimal* control loses to bolting the
   tank down, on the owner's real recorded drives, under both slosh models.
3. The `IdealTiltTracking_…` diagnostic (F4) that showed head-room is a *kinematic*
   tank-rotation result with no actuator; put the real servo + 900 N·m limit +
   ±30° travel in the loop and the head-room is gone at this freeboard.

### Decision-gate verdict: **do not start residual RL yet** (experiment-plan step 3, fail branch)

A causal sensor-only policy cannot beat what full hindsight cannot beat. The
premise has to change first — candidates, for the owner:

- **Freeboard.** Fill 0.62 on the 0.6 m tank = 0.23 m freeboard; a *locked* tank
  already spills 19–22 L on these drives. The driving is simply too violent for
  this freeboard regardless of stabilisation. Lower fill / taller tank until a
  locked tank is near the spill threshold, so there is a gap for control to close.
- **Road / driving severity.** `RoadConfig.Severity`, or record calmer held-out
  drives.
- **The actuator, or the action space.** The "hold" state for the *active*
  controller is a sagging servo, not a rigid joint. A hybrid action that can
  **mechanically lock** unless actively correcting (so the baseline for residual
  RL is 19 L, not 25 L) would remove the structural handicap — worth designing
  before step 4.
- Preview/look-ahead is *not* the fix on its own: the hindsight trajectory
  already has perfect look-ahead and still fails.

### Guards

`HindsightOptimizerTests` (5): search determinism, actuator limits bound an absurd
commanded trajectory, ceiling never worse than do-nothing, trajectory-CSV export,
"inconclusive" verdict when a locked tank never spills.

---

## F4 — 2026-08-28 — A rigid (locked) Hold baseline is near-unbeatable by reactive tilt control

**Supersedes F2.** After the F3 addendum (the "Hold" servo saturates and sags
under aggressive slosh loads), the owner set two things:

- **`Hold` = a mechanically *locked* joint** (decision D5): the tank is rigid to
  the vehicle, the slosh moment is reacted by the vehicle structure, not the
  actuator. New `StabilizerCommandMode.Locked`; it consumes no torque and cannot
  saturate or sag. This is the true "no stabilizer" floor.
- **Actuator resized for realism, under-actuation kept deliberate:**
  `MaxTorqueNm` 650 → **900** (covers the ≈ 740 N·m quasi-static slosh moment at
  the 0.45 g friction limit with margin, still under-powered for aggressive
  dynamic tracking); `ContinuousTorqueNm` 280 → **420** and now *enforced* by an
  I²t winding-heat model (`ThermalTimeConstantSeconds` = 15 s) — bursts to peak
  are fine, sustained hard maneuvers derate the peak toward continuous.

### Baseline re-evaluation — pendulum ROM, mean spill [L], 6 seeds

| scenario | Hold (locked) | SFA | PID | PrivFF |
|---|---|---|---|---|
| HighwayCalm | **0** | 0 | 0 | 0 |
| HighwayBrisk | **0** | 0 | 0 | 0 |
| HighwayBriskLowFreeboard (0.27 m) | **0** | 0.30 | 0.26 | 0.28 |
| ScriptedManeuver | **0** | 0 | 0 | 0 |
| AggressiveRandom | **0** | 0 | 0 | 0 |
| HighFillAggressive (0.22 m) | **9.8** | 42.6 | 56.5 | 97.6 |

**With a rigid Hold, no analytic tilt controller beats it on any scenario.** On
low-freeboard highway driving the reactive laws now *spill where Hold does not*
(they add slosh energy by rotating the tank — F1's mechanism). In the one
scenario that defeats even a rigid tank (`HighFillAggressive`), reactive tilt
control spills 4–10× more and the actuator sits at 90–100 % torque-saturated —
authority-limited, exactly as intended.

F2's headline ("tilt beats Hold ~15× on brisk highway") was an artefact of the
*sagging servo* Hold it compared against. Against a rigid Hold that advantage is
gone.

### What this means for the project

The value proposition is now unambiguous: **rigid is a strong baseline, and
beating it needs anticipation / learning, not reactive feedback.** The
ideal-tilt diagnostic still shows head-room (perfect tank-tilt tracking → 0.40×
wall wave, `IdealTiltTracking_…`), but no causal reactive law reaches it. This is
the gap the RL policy (phase-1 item 12) and a privileged MPC exist to close.
`PrivilegedFeedforward` (most aggressive tilting) remains the *worst* controller
under stress — over-actuation is the enemy.

Bracket-model discipline (F3) still stands. **Shallow-water re-run with the D5
config confirms it** — locked Hold beats every reactive tilt controller on every
scenario (`HighwayBriskLowFreeboard`: Hold 0.0004 L vs 15–20 L; `AggressiveRandom`:
0.48 vs 7–50 L; `HighFillAggressive`: 56 vs 94–231 L). Shallow-water Hold does
spill in the aggressive regime (a rigid tank still transmits the truck jerk to
the liquid) — that residual is the real target for a learned/anticipatory policy.

### Guards

`EvaluatorTests.RigidHold_NotBeatenByReactiveTilt_OnLowFreeboardHighway`,
`ReactiveTilt_LosesToRigidHold_UnderAggressiveHighFill`,
`GimbalActuatorTests` (locked joint, thermal derate).

---

## F3 — 2026-08-28 — Shallow-water reference model: raw run flipped F2; with wave-breaking it reconciles F1 + F2

The deciding experiment promised in F1 (interpretation 1). `ShallowWaterSloshModel`
— a 2-D depth-averaged Saint-Venant solver on a ~48×28 grid (decision D4) —
dropped in behind `ISloshModel`; the whole baseline evaluation re-run
(6 scenarios × 4 controllers × 6 seeds, `ShallowWaterValidationTests`, all
`[Explicit]`, full sweep ~25–40 min).

### Stage 1 — raw NLSW solver: tilt control never beat `Hold`

The first run said **no analytic tilt controller beats holding the tank rigid on
any highway scenario**, and `HighwayBriskLowFreeboard` — where F2 had tilt
control winning 15× under the ROM — had tilt tying/losing while `Hold` itself
spilled ~6× more than the ROM predicted. Grid-converged (27.6/27.0/27.0 L across
30×18/48×28/71×41) and CFL-stable (sub-steps 2–4 of 64, no clamps) — **not a
numerical artifact**. Low-amplitude agreement with the ROM still held (surface
tilt within ~2 %, 13 % at 5 m/s²); the models only diverged at large amplitude.

Cause: shallow-water equations **cannot break a wave** — it steepens into a bore
that carries water up the wall instead of white-capping and dissipating — so
NLSW is known to over-predict wall run-up, and every wave that touched the rim
was counted as spilled.

### Stage 2 — add the missing physics (owner's call: "fix shallow water first")

Two physically-standard additions to `ShallowWaterSloshModel` (decision D4):

- **Wave-breaking eddy viscosity** — across a front steeper than ~31° a
  diffusive momentum flux `ν = C·Δ·√(g·h̄)·ramp` damps the front (Kennedy et al.
  2000, simplified to a slope trigger). `C = 0.6`.
- **Rim run-up return** — the weir head is measured from a low-passed boundary
  surface (`τ = 0.2 s`), not the instantaneous peak: a wave that slaps the rim
  and recedes barely spills, only a sustained surge does.

Both constants are within the literature range and were fixed before re-running.
Solver still healthy (sub-steps 4–14, no clamps), grid-converged
(37.8/39.2/40.3 L), low-amplitude ROM agreement unchanged.

### Stage 3 — result with wave-breaking: F1 and F2 reconciled

Mean spill [L] over 6 seeds — `Hold` vs the best analytic tilt controller:

| scenario | ROM Hold→tilt | SW Hold→tilt | verdict (both models) |
|---|---|---|---|
| HighwayCalm | 0 → 0 | **0 → 0** | nobody spills |
| HighwayBrisk | — | **0 → 0** | nobody spills |
| HighwayBriskLowFreeboard (0.27 m) | 6.98 → 0.39 | **22.4 → 15.5** | tilt helps (ROM 15×, SW ~1.4×) |
| ScriptedManeuver (clean 1-axis) | — | **27.1 → 0.0004** | tilt helps enormously |
| AggressiveRandom | tilt worse | **0.72 → 8.4** | **tilt hurts** (pumps slosh) |
| HighFillAggressive | tilt worse | **90.9 → 138** | **tilt hurts** |

`PrivilegedFeedforward` (most aggressive tilting) is the **worst** controller in
every scenario under stress (`AggressiveRandom` 70 L, `HighFillAggressive`
210 L). Over-actuation is the enemy.

**The two independent slosh models now agree on direction everywhere:**
- gentle / moderate highway → nobody spills;
- brisk highway at tight freeboard → reactive tilt control helps (ROM says a
  lot, shallow water says a little);
- clean single-axis manoeuvre → tilt control nearly eliminates the spill;
- aggressive / adversarial transients → reactive tilt control *pumps* the slosh
  and loses to holding rigid — **this is the RL / anticipation target** (F1 stands).

They differ on magnitude: the ROM is optimistic about how much tilt helps on
highways, shallow water is pessimistic about aggressive driving being
controllable. The `HighwayBriskLowFreeboard` "tilt helps ~1.4×" sits in the
regime where the breaking constants matter — treat it as "small positive", not a
precise number.

### Net effect on the project

- The premise is **not** broken. Tank-tilt stabilization is a real, well-posed
  task for highway driving; a modest rule-based win, with headroom for a learned
  policy (ideal-tilt tracking → 0.40× wall wave, `IdealTiltTracking_…`).
- The pendulum ROM is **trustworthy enough for bulk RL training** — it agrees
  with the 2-D model on direction and ranking. Shallow water is the fidelity
  check.
- **Adopted: bracket-model discipline** (interpretation 3). Report controllers
  against both models; a result counts only if it holds under both. RL trains on
  the ROM (cheap), is validated on shallow water.

### Guards / how to re-check

`ShallowWaterValidationTests` — all `[Explicit]` (full sweep ~25–40 min; too slow
for the default suite, unlike the ROM guards in `EvaluatorTests`). Run
`SolverHealth_AcrossScenarios` + `BaselineEvaluation_UnderShallowWater_PrintsReport`
after any change to the shallow-water model, the actuator, or the analytic
controllers. `GridConvergence_…` if the scheme changes.

### Addendum (2026-08-28) — shallow-water reaction ≈ 2× the ROM's → the Hold gimbal saturates

> **Resolved by decision D5 / finding F4.** The owner's call: the actuator was
> resized (`MaxTorqueNm` 650 → 900, `ContinuousTorqueNm` 280 → 420 now enforced
> via an I²t thermal model) and, more importantly, `Hold` is now a *mechanically
> locked* joint that does not use the actuator at all — so "Hold" can no longer
> sag. The 2× reaction difference between the models is real and still matters
> for the *active* controllers; it no longer muddies the Hold baseline.

Found while rebuilding the scene visualisation (`SceneCapture`). Under the
aggressive `ScriptedManeuverDriver` + `Hold`:

| slosh model | max gimbal pitch | angle-limited | spill |
|---|---|---|---|
| Pendulum ROM | ~0° (servo holds) | no | 0 L |
| Shallow water | **30° (pegged at the travel limit)** | yes | 1.1 L |

Under the realistic `HighwayDriver` (briskness 0.5) the shallow-water Hold gimbal
stays at ~0° and does not spill — this is an aggressive-maneuver effect only.

Why: the fore-aft reaction moment on a rigidly-held tank under a hard
longitudinal maneuver is ~M·aₓ·(h/2 + L²/12h) ≈ 500 N·m at 3 m/s². The pendulum
ROM returns roughly half that (~230 N·m): it caps the participating mass at 0.45
and puts the remainder on-axis, where it makes little moment. The gimbal's
`ContinuousTorqueNm = 280` sits between the two, so the Hold servo holds under
the ROM and saturates under shallow water.

Consequences:
- F3's aggressive-regime rows still rank `Hold` best, but under shallow water
  "Hold" there is *a saturated servo sagging toward its 30° limit*, not a rigid
  tank. Part of why aggressive driving is hard under shallow water is that the
  actuator is torque-limited against the true slosh moment.
- The ROM is optimistic about **actuator loading**, not only about spill.
- Open questions for the owner (not yet actioned): (a) is
  `GimbalConfig.ContinuousTorqueNm` / `MaxTorqueNm` sized for the real slosh
  moment, or is under-authority the intended challenge? (b) should the `Hold`
  baseline model a *locked* joint (moment reacted by the truck, no sag) as the
  true floor, distinct from "servo commanding 0"?

---

## F2 — 2026-08-28 — A realistic driver flips F1: tilt control works for highway driving

> **Superseded by F4.** The "tilt beats Hold ~15×" result here compared against a
> Hold whose servo *sagged* under slosh load. Against the rigid locked-joint Hold
> (decision D5) that advantage disappears — reactive tilt control does not beat a
> rigid tank on any scenario. Kept for the driver-design reasoning.

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
  (Tank later resized to 0.6 m tall — see the note at the top of this file.)

### Guards

`EvaluatorTests.NoBaselineControllerBeatsHold_OnTheDiscriminatingScenario` and
`NaiveFeedbackControllers_ExciteSlosh_AndWorsenSpill` lock in the current state;
if a plant/controller change flips either, this file must be revisited.
