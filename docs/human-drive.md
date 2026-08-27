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
| **R** | reset the run |
| **T** | slow-mo (0.25×) |
| **Tab** | camera: chase → side → top |

## What you're looking at

- The blue block is the truck; the translucent box on it is the open tank; the
  blue volume inside is the liquid, with a brighter tilting surface showing the
  first sloshing mode.
- HUD: speed, longitudinal / lateral g, **min freeboard** (turns red near 0),
  litres spilled (flashes on a spill), slosh energy, and which stabilizer is on.

## What to try

- Drive smoothly on a straight, then a gentle lane change — watch the surface
  wobble and settle.
- Hard brake with the stabilizer **off** (Space), then **on** — compare the
  freeboard bar and the spill counter.
- A quick swerve at speed: the naive stabilizers (1, 2) can *pump* the slosh on
  sharp inputs (finding F1); smooth driving is where they clearly help (F2).

## Notes / current limitations

- The slosh is the reduced-order pendulum model — the surface is a proxy, not a
  real fluid mesh. The shallow-water model (next) will render an actual height
  field.
- Vehicle is a reduced sprung-mass model on flat ground (no road bumps/hills
  yet). Feel is "laden truck": heavy, long braking, lazy steering.
- Stabilizer option 3 uses information a real sensor could not give — it is only
  there as a comparison ceiling.
