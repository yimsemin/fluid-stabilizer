# Experiment plan — "where are we, what's next"

> Written 2026-08-29 in the step-back session after the first RL run. This is the
> owner-facing map. It refines the roadmap in
> [project-goals.md](project-goals.md) and the step-back note in
> [phase1-progress.md](phase1-progress.md); where they disagree, this file wins
> until it is folded back in.

## The one-paragraph status

The whole deterministic simulation is built and tested (physics, road, actuator,
two slosh models, sensors, baselines, evaluator, play scene, RL bridge). We know
from findings **F1–F4** that on a flat road a rigidly-locked tank is almost
unbeatable by reactive tilt control — beating it needs *anticipation*, which is
the entire reason the learned policy ③ exists. The first RL run confirmed the
flip side: plain PPO-from-scratch cannot find the anticipatory policy, because
every random exploratory gimbal motion pumps the slosh and spills (the "F1/F4
cliff"). Two pieces the design always called for were never built: **recorded
human drives** (the real disturbance data) and a **feasibility ceiling** (proof
that the task is winnable at all). Those are the next work, before any more RL.

## Where we are (done ✅ / not done ⬜)

| Layer | State |
|---|---|
| Deterministic sim core, vehicle + road + heave, gimbal actuator, 2 slosh models, sensors | ✅ |
| Baselines ① locked Hold, ② SFA / PID / privileged-feedforward, evaluator, play scene | ✅ |
| RL bridge (headless Unity ↔ Python PPO), runs end-to-end | ✅ |
| **Recorded human drives** (record + replay) | 🟡 landing now (step 1) |
| **Feasibility ceiling** (best achievable spill on a fixed drive) | ⬜ |
| **Residual RL** (policy = baseline + small correction) | ⬜ |
| Domain randomization + per-episode logging | ⬜ |
| Phase 1.5 product shell (in-Unity control panel, standalone build, ONNX inference) | ⬜ |

## The next steps, in order

1. **Human trace record / replay** — ✅ *done 2026-08-29.* `DriverTrace` +
   `TraceRecorder` (`P`/`L` in the play scene). Gives the training disturbance
   **and** the held-out evaluation condition in one move.
2. **Rebuild the evaluation around recorded drives.** The headline three-way
   comparison (①/②/③, later ④ the ceiling) runs on **3–5 held-out real drives**,
   each replayed under **both** slosh models (bracket discipline, F3), reported
   **per drive** in litres spilled — not just a mean over synthetic scenarios.
   The synthetic scenarios stay, demoted to a training / robustness / regression
   set. Sub-parts:
   - **2a** ✅ *done 2026-08-29* — `ComparisonRunner`: the play scene's `C` key
     replays one trace through three stabilizers at once, side by side, over the
     same road, with a per-lane spill / near-rim / slosh-energy panel. This is
     the visual headline.
   - **2b** ✅ *done 2026-08-29* — `RoadRibbon`: a textured road strip that
     follows the road's curve, tilts with grade/bank and dips into potholes,
     rebuilt from an independent `RoadModel` replica (same seed/config as the
     physics). Opaque shader (no z-fighting), procedural lane markings. Visible
     only with the road on (`G`). *Quality pass 2026-08-29 after owner feedback:
     added road curvature so it's a course you follow (see road-model.md),
     opaque `RoadRibbon.shader`, tiled asphalt/markings texture, curved
     centreline, exaggerated bump rendering; fixed the comparison camera to
     track the (now curving) pack.*
   - **2c** ✅ *done 2026-08-29* — `TraceEvaluator` + menu item **FluidStabilizer
     ▸ Evaluate Recorded Drives** (and a `+ shallow water` variant): replays every
     trace in `Traces/` through the analytic baselines, per drive, writes
     `Logs/trace-eval.txt`.
     **Note from the first run:** the pendulum ROM only spills above
     `FillRatio ≈ 0.56` (its wave-elevation cap is `0.8·depth`); the four
     exploratory traces (fill 0.55, road off) show 0 L for everyone. The
     held-out drives for the real comparison must be recorded with **road on
     (`G`)** and **fill ≥ 0.60** to discriminate — and checked under shallow
     water too.
3. **Feasibility ceiling (hindsight).** For each fixed recorded drive, search
   offline for the gimbal-motion trajectory that minimises spill (gradient-free
   optimisation over a low-dimensional trajectory, full hindsight, real actuator
   limits). This is much simpler than a real-time MPC and answers the same
   question. **← decision gate:**
   - ceiling clearly beats locked Hold on the recorded drives → the task is in
     the winnable bracket; proceed to step 4 with a real target number.
   - ceiling *cannot* beat Hold → stop and adjust the premise with the owner
     (tank freeboard, road severity, add preview/look-ahead, or reframe as
     "robustness under mild conditions") before spending effort on RL.
4. **Residual RL.** The policy outputs a small correction on top of a baseline
   (locked-hold or SFA), so exploration happens around a sane operating point
   instead of random gimbal slams — the specific fix for the F1/F4 cliff, and
   the approach design-review named from the start. Warm-start by
   behaviour-cloning the step-3 hindsight trajectories, then let RL make it
   causal and sensor-only. Needs a small change to `RlEnvironment` (compose
   baseline + policy action) and checkpoint-resume in the trainer.
5. **Domain randomization + per-episode logging**, then **Phase 1.5** — the
   product shell (in-Unity control panel, standalone build, ONNX inference in
   Unity, a light Python trainer GUI).

## The three concerns from the step-back session — resolved

### 1. There is no "driving course" / route

**Decision: no explicit route / waypoint / map system.** The AI does not drive;
a path-following subsystem would add complexity without adding understanding.
The unit of "a drive" is a **recorded human trace + a fixed road seed** — fully
deterministic and reproducible. That is step 1.

**Road curvature is not coupled to steering yet, and that is acceptable for
now.** The road's `bank` is a function of distance, independent of where the
driver steers; in the play scene this reads as an extra exogenous cross-slope.
For a *disturbance-rejection* experiment that is fine. Physically coupling road
curvature ↔ steering ↔ superelevation is a fidelity upgrade to consider **after**
the first three-way comparison, and only if the road disturbance turns out not
to be interesting enough.

### 2. Is "mean spill over 6 synthetic scenarios × 6 seeds" the right benchmark?

**Decision: no — the headline benchmark is the owner's own recorded drives.**
3–5 held-out real drives, replayed under both slosh models, reported per drive in
litres. Primary metric: litres spilled. Secondary: time-near-rim, peak slosh
kinetic energy, actuator effort / thermal load. The synthetic scenario set is
kept for domain randomization and as a fast regression signal, not as the
headline result. This matches design-review §17 ("record human driving sessions
→ a fixed held-out test set").

### 3. Is the RL bet still the right approach after F4?

**Decision: build the ceiling first, then switch to residual RL.** The first RL
run already gave us its main lesson (the cliff); re-running plain PPO would teach
nothing new. Instead: (a) step 3 establishes whether the task is winnable at all
and by how much; (b) step 4 uses **residual RL** — policy on top of a baseline —
which is design-review's stated method and the direct fix for the cliff, with a
behaviour-cloning warm-start from the hindsight-optimal trajectories. Curriculum
(needs checkpoint-resume) is a supporting tactic, not the primary plan.

## What this changes vs. the older roadmap

- `project-goals.md` step 1 was "finish and run RL once". That happened; its
  result is the cliff. The honest next step is recorded traces + a ceiling, not
  another RL run — so steps 1–3 here come before any RL.
- The evaluation benchmark shifts from "synthetic scenario mean" to "held-out
  recorded drives", with synthetic kept as a secondary set.
- A hard **decision gate** is added after step 3: don't start RL until a
  privileged/hindsight solution is shown to beat the rigid baseline.
- The tank was made shallower (`TankHeightZ` 0.8 → 0.6 m) so it spills more
  readily — the F1–F4 direction is unchanged and, on the discriminating
  scenarios, now stronger (see the note at the top of
  [findings.md](findings.md)). A full baseline re-sweep is pending.
