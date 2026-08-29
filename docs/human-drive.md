# Driving it yourself

## Open

1. Open the project in Unity **6000.5.10f1** (Unity Hub → Add → this folder).
2. **menu ▸ FluidStabilizer ▸ Rebuild Play Scene**  — this generates
   `Assets/FluidStabilizer/Scenes/PlayScene.unity` (it is not in git: the builder
   reshuffles every id on each run, so it is regenerated on demand).
3. Open that scene and press **Play**.

Re-run the menu item any time the scene looks wrong or you changed `SceneBuilder`.

## Controls

| | |
|---|---|
| **W / S**  or  gamepad **triggers** | throttle / brake |
| **A / D**  or  left **stick** | steer |
| **Space** | stabilizer **on / off** (feel the difference) |
| **0 / 1 / 2 / 3** | pick stabilizer: 0 = off, 1 = specific-force align, 2 = + integral, 3 = privileged feed-forward (cheats — comparison only) |
| **M** | slosh model: pendulum ROM ⇄ shallow water (restarts the run) |
| **G** | road: flat ⇄ real road (rolling grade, curve bank, roughness, potholes) |
| **R** | reset the run |
| **T** | slow-mo (0.25×) |
| **Tab** | camera: chase → side → top |
| **P** | **record** your drive to a trace file / press again to stop & save |
| **L** | **replay** the newest recorded trace / press again to drive again |
| **C** | **compare** stabilizers side by side on the newest trace / press again to exit |

A banner across the top of the screen always shows the trace state: **idle**,
**● REC** (with elapsed time + step count, plus a red border), or **▶ REPLAY**
(with a progress bar). Every save logs the full file path to the Console.

## What you're looking at

- The blue flatbed is the truck; on a short dark mount above it sits the tank —
  drawn as an **open edge frame** (12 beams, no panels) so you can see straight
  in. The tank is lifted a small cosmetic amount (`SimView.TankVisualLift`) so
  the surface tilt and any overflow are clearly visible; the physics is
  unchanged. The frame flashes **red** on a spill.
- Inside is the liquid — a closed, deforming water body whose top surface is the
  slosh model's free surface, translucent (fresnel edge, specular glint,
  ripple). It never overhangs the walls; anything cresting the rim is overflow.
- Under a hard maneuver with the stabilizer **off** and the **shallow-water**
  model the tank can visibly sag to its 30° travel limit — the gimbal servo
  saturates against the slosh moment (see `docs/findings.md` F3 addendum). This
  does not happen under gentle driving or the pendulum model.
- **M** switches the slosh model. **Pendulum ROM** (default): the surface is a
  tilted plane from the two first-mode angles — a proxy, the ROM has no spatial
  surface. **Shallow water**: the surface is the 2-D solver's real height field —
  travelling waves that run up the walls and slop over the rim.
- It is not photoreal and never will be — no particles, foam or spray. Full
  SPH/FLIP fluid breaks determinism and the RL step budget (design-review §6,
  §21). "Clearly a body of sloshing liquid" is the target, not CGI water.
- HUD: speed, longitudinal / lateral g, **min freeboard** (turns red near 0),
  litres spilled (flashes on a spill), slosh energy, and which stabilizer is on.

## What to try

- Drive smoothly on a straight, then a gentle lane change — watch the surface
  wobble and settle.
- Hard brake with the stabilizer **off** (Space), then **on** — compare the
  freeboard bar and the spill counter.
- A quick swerve at speed: the naive stabilizers (1, 2) can *pump* the slosh on
  sharp inputs (finding F1); smooth driving is where they clearly help (F2).

## Recording your drives (for the three-way comparison)

Press **P**, drive, press **P** again. The drive is saved as a small text file
to **`<project>/Traces/drive-<timestamp>.trace.csv`** (in the Editor) — a header
with the setup (seed, fill, road, slosh model) and one `throttle,brake,steer`
row per physics step. The whole simulation is deterministic given the seed, so
this file is a complete, reproducible definition of that drive.

Press **L** to replay the newest one. During replay you are *not* driving — the
recorded inputs play back — so you can press **0–3** to switch the stabilizer
and watch the *same* drive handled by rigid / rule-based / (later) the learned
policy, over the *same* road. That is the headline comparison; the next step
adds side-by-side replay of two tanks at once plus a running spill count.

Record several *different* drives (calm commute, brisk highway, a rough-road run
with **G** on). Some become the training disturbance data; some are held out as
the evaluation set. For drives meant to *discriminate* the stabilizers, keep the
**road on (`G`)** and the tank fairly full — below about 55 % fill the pendulum
model's waves never reach the rim, so nothing spills.

**Batch numbers:** menu ▸ *FluidStabilizer ▸ Evaluate Recorded Drives* replays
every trace in `Traces/` through the analytic baselines and writes a per-drive
litre table to `Logs/trace-eval.txt`.

## Comparing stabilizers side by side (C)

Press **C** to replay the newest trace through **three tanks at once**, lined up
across the screen over the same road:

- **Rigid (no stabilizer)** — the tank bolted down (① in the three-way comparison)
- **Rule: PID** — the reactive tilt law (②)
- **Privileged feed-forward** — a reference that cheats (uses the true slosh-free
  tilt); the learned policy (③) will take a lane here once it exists

Each lane has a live panel: **litres spilled** (big), time spent **near the rim**,
**peak slosh energy**, and how long the actuator sat **torque-saturated**. The
lowest-spill lane gets a green border. `R` restarts, `T` is slow-mo, `C` exits.

This is the headline result of the project — the point where you can *see*
whether a stabilizer actually keeps more water in the tank than doing nothing.

## Notes / current limitations

- Two slosh models are available (**M**): the cheap pendulum ROM (proxy
  surface) and the 2-D shallow-water solver (real height-field mesh). Neither is
  a particle/SPH fluid — no foam or splashes (out of scope: determinism + RL
  step budget).
- Vehicle is a reduced 3-DOF sprung-mass model (pitch / roll / heave). Feel is
  "laden truck": heavy, long braking, lazy steering. Press **G** for a real road
  (grade, bank, roughness, potholes) — a scrolling road strip then appears under
  the truck so you can see the bump arrive before the tank reacts; flat road
  (no strip) is the default.
- Stabilizer option 3 uses information a real sensor could not give — it is only
  there as a comparison ceiling.
