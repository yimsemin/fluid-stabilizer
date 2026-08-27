# Findings

Empirical results from the simulation as it is built. Newest first.

---

## F1 — 2026-08-28 — Naive reactive control makes spilling *worse*; "do nothing" is a strong baseline

**What the evaluation shows** (`Evaluator` over the fixed scenario set, 6 seeds;
run `BaselineControllerTests.Report_PrintsAndIsDeterministic`):

| scenario | Hold spill | PID spill | SFA spill | PrivFullState spill |
|---|---|---|---|---|
| CalmCruise | 0 | 0 | 0 | 0 |
| ScriptedManeuver | 0 | 0 | 0 | 0 |
| RandomModerate | 0 | 0 | 0 | 0 |
| RandomAggressive | 0 | 0 | 0 | 0 |
| **ModerateFreeboard** (0.30 m freeboard, brisk) | **7.0 L** | **36.9 L** | **37.3 L** | **7.6 L** |
| **HighFillAggressive** (0.22 m freeboard) | **96.0 L** | **101.0 L** | **101.3 L** | **86.7 L** |

Slosh energy (∫ KE dt) tells the mechanism: the naive feedback controllers
(`PID`, `SpecificForceAlignment`) run **2–13× more** slosh energy than `Hold`.
They are pumping the first slosh mode into resonance.

**Why.** The gimbal only influences the liquid through (a) rotating the
gravity vector in the tank frame and (b) the tank's angular acceleration, which
forces the slosh pendulum directly. A controller that reacts hard to the felt
tilt — which itself oscillates at the slosh frequency — moves the tank at that
frequency, and through the rate→setpoint integration and servo lag the phase
comes out wrong, so it adds energy instead of removing it. Slosh natural
frequency here is ~0.7 Hz (pitch) / ~1.3 Hz (roll), squarely in the band of
real maneuvers and of the actuator's own response.

**What beats it.** Only a smooth, heavily-low-passed, slew-limited command that
does *not* chase the fast component (`PrivilegedFullState`, kTrack=1, 1.6 Hz
low-pass, slew-limited). Even with perfect state it only **matches** Hold on
spill and shaves ~10 % on the hardest scenario. A controller that *substantially*
beats Hold does not exist yet.

**Assessment — this is good for the project premise, with caveats.**

- It confirms the task is a *real* Physical-AI problem: not solvable by an
  obvious rule, and naive control is actively harmful (design-review §2, §18.6–7,
  §19.1). The RL objective is well-posed: beat Hold, and decisively beat naive
  PID, by learning anticipatory, slosh-state-aware, smooth control.
- **Caveat 1 — the ceiling is currently weak.** `PrivilegedFullState` is
  hand-tuned smooth feed-forward, not a solved optimal controller. Before RL
  results can be judged we need a genuine ceiling: a proper LQR/MPC on the
  linearized coupled model, or a well-designed anticipatory controller (preview
  of the maneuver, notch at the slosh frequency, phase-correct slosh damping).
  This is the top near-term task.
- **Caveat 2 — model calibration is unverified.** `SurfaceSlopePerPendulumAngle`
  (pendulum angle → wall wave height) is set to 1.0 by a quasi-static argument
  but never checked against a shallow-water run or data. The absolute spill
  volumes and the exact controllability boundary depend on it. Fidelity
  conclusions are provisional until this is calibrated (design-review §6 caveat).
- **Caveat 3 — regime is narrow.** With realistic accelerations (≤ ~0.45 g) the
  nominal 0.40 m freeboard never spills for anyone; spill only appears below
  ~0.30 m freeboard. The "interesting" band is real but thin, and domain
  randomization needs to keep episodes inside it.

**Guard.** `EvaluatorTests.NaiveFeedbackControllers_ExciteSlosh_AndWorsenSpill`
asserts the finding, so if a later plant change quietly makes the task trivially
controllable, a test fails and we notice.
