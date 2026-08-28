# Plan — shallow-water model, gentler driver, human-drive scene

Agreed direction after finding F1: run interpretations 1 + 3 (shallow-water
reference model, gentler driver) and add a playable human-drive scene.

**Order (owner decision 2026-08-28): B → C → A.** Gentle driver first (cheap, may
move F1), then the human-drive scene so the owner can drive right away with the
pendulum ROM, then shallow water — swapped in behind `ISloshModel` with no scene
changes. Truck feel: **realistic** (heavy, long braking, lazy steering).
Input: **keyboard + gamepad**.

**Status 2026-08-28:** B done (see finding F2), C done (see `docs/human-drive.md`).
**A done** — `ShallowWaterSloshModel` + 13 tests (decision D4), validation
experiment run, height-field mesh viz + `M` scene toggle.

**⚠ The validation experiment produced finding F3 and a pending decision.**
The shallow-water model contradicts F2: no analytic tilt controller beats
holding the tank rigid, on any highway scenario. Result is grid-converged and
CFL-stable. Before phase-1 items 12/13 the owner needs to pick a direction —
see `docs/findings.md` F3 "Interpretations (the decision)":
1. believe shallow water → pivot to anticipatory/learned control + re-fit the ROM;
2. add wave-breaking dissipation to the shallow-water model and re-run;
3. keep both as bracket models, validate every result under both.

### Liquid visualization (owner asked)

Right now the liquid is a translucent blue block plus a brighter surface quad
tilted by the two pendulum angles — a proxy, because the pendulum ROM has no
spatial surface, only two mode angles. **Real water-like liquid comes with A:**
the shallow-water model produces an actual height field, and `SimView` will then
render it as a deforming mesh with a water shader — visible traveling waves that
climb the walls and slop over the rim. Full particle/SPH fluid (foam, splashes,
refraction) stays out of scope — it breaks determinism and the RL step budget
(design-review §6, §21). The shallow-water mesh + a good surface shader is the
target look.

---

## B. Gentler, more realistic driver + scenario revision

**Problem:** `RandomManeuverDriver` fires sharp 0.3–1.0 s events that land near
the roll slosh period (~0.8 s) and resonate; sustained 0.45 g lateral is
autocross, not highway driving.

**Work:**
- `HighwayDriver` — band-limited: throttle/brake/steer are each a slow OU process
  (τ ≈ 3–5 s) plus rare, *smooth* (raised-cosine) events: a lane change, a
  highway curve entered and held, a gradual slow-down. Peak lateral ≈ 0.25 g
  typical, 0.35 g rare. Deterministic given seed.
- Keep `RandomManeuverDriver` as the "aggressive / adversarial" profile.
- Revise `ScenarioSet`: Highway-calm, Highway-brisk, Highway + long curve,
  Aggressive (kept), plus one "emergency" (single hard brake / swerve) so the
  hard case is a *transient*, not sustained.
- Re-run the baseline evaluation. If a baseline now beats Hold on the brisk
  highway scenario, F1 interpretation 3 is (partly) the answer.

**Estimate:** small. One new class, scenario edits, re-run.

---

## A. Shallow-water slosh model (the reference)

**Why:** the whole controllability verdict hinges on how the slosh model reacts
to tank rotation. The equivalent-pendulum ROM's rotation coupling (Euler term +
gravity-rotation transient) is the unvalidated part. A depth-averaged
shallow-water solver captures traveling waves, spatially-varying surface and
spill location directly, and reacts to base motion through first principles —
so it is an independent check.

**Model:** 1-D Saint-Venant per axis is too weak (misses the diagonal coupling);
go **2-D shallow water on a coarse grid** (e.g. 48 × 28 cells over the tank
floor), depth-averaged:

```
∂h/∂t + ∂(hu)/∂x + ∂(hv)/∂y = 0
∂(hu)/∂t + ∂(hu² + ½g_z h²)/∂x + ∂(huv)/∂y = h·(g_x − a_x)   (+ bed friction)
∂(hv)/∂t + ∂(huv)/∂x + ∂(hv² + ½g_z h²)/∂y = h·(g_y − a_y)
```

- Body forces from the tank-frame gravity + linear acceleration + (rotational
  terms from tank angular velocity/acceleration — the key coupling to get right).
- Solver: an explicit, positivity-preserving scheme (HLL flux or a well-tested
  MacCormack + limiter). CFL-limited; sub-step within the 600 Hz physics tick as
  needed. Must stay deterministic (fixed sub-stepping, no threading races).
- Reaction on the tank: integrate the pressure force + momentum flux over the
  walls and floor → force + torque about the tank origin.
- Overflow: water advected past a rim cell leaves the domain; sum it.
- Ground truth: liquid CoM, kinetic + potential energy, per-cell height (for viz
  and, if ever wanted, an "ideal" observer — never the actor's).
- Behind the existing `ISloshModel` interface — drop-in for `PendulumSloshModel`.

**Validation:**
- Slosh natural frequency of a small perturbation matches the analytic
  rectangular-tank first mode (same test as the pendulum model).
- Sloshing under a step acceleration: compare wall wave and settling to the
  pendulum ROM — expect agreement at low amplitude, divergence when big.
- Re-run `PlantControllabilityTests` and the baseline evaluation with this model.
  **This is the deciding experiment for F1's aggressive-regime finding.**

**Then:** `SimView` renders the height field as a mesh (a `MeshFilter` updated
each frame from the grid) with a simple water shader, replacing the tilted-quad
proxy. Add a scene toggle for pendulum-ROM vs shallow-water.

**Cost / risk:** the solver is the main effort and the main risk (instability
blowing up training later). Keep the grid small, the scheme simple and proven,
and gate it with stability tests. Compute at 48×28 × a few hundred Hz is still
cheap on CPU and RL-viable, but heavier than the pendulum ROM — the pendulum
model stays the default for bulk RL; shallow water is the fidelity check and an
optional higher-fidelity training/eval model.

**Estimate:** medium-large (the solver). Done in steps: grid + advection +
stability test → body forces + coupling → reaction force/torque → overflow →
validation.

---

## C. Human-drive scene (Unity)

First real content in `FluidStabilizer.Sim`. Sets the Unity-side patterns.

### Architecture

| Piece | Role |
|---|---|
| `SimulationRunner` (MonoBehaviour) | Owns a `SimulationLoop`; steps it in real time (accumulator: 600 Hz sim vs display rate); exposes latest state. Inspector: which `ISloshModel`, which stabilizer `IController`, `SimulationConfig`. |
| `HumanDriverInput` (`IDriverInput`, in Sim) | Reads throttle / brake / steer from the Input System (keyboard **and** gamepad); `Sample()` returns the last poll. |
| `SceneView` (MonoBehaviour) | Each frame: place the truck (world pose), the tank (local gimbal rotation), and a liquid proxy — a tilted translucent volume for the pendulum ROM, the actual height-field mesh for shallow water. Right-handed SI → Unity left-handed via one fixed basis swap. |
| `Hud` (UGUI) | Speed, longitudinal/lateral g, **min freeboard** (bar, turns red near 0), cumulative spill (+ a "SPILL" flash), slosh energy, which stabilizer is active, a controls hint. |
| `PlayControls` (MonoBehaviour) | Number keys switch the stabilizer live (0 = rigid/Hold, 1 = SpecificForceAlignment, 2 = +I, 3 = LQR); **`S` toggles the stabilizer entirely on/off** so you can feel the difference; `R` resets the episode; `T` slow-mo (0.25×); `Tab` cycles camera (chase / side / top). |
| `SceneBuilder` (Editor script) | Builds `Assets/FluidStabilizer/Scenes/PlayScene.unity` from code and saves it — reproducible, version-controlled, so the scene is regenerated by an `-executeMethod` call, not hand-wired. |

### Frames

SI right-handed `(x=forward, y=left, z=up)` → Unity `(x=right, y=up, z=forward)`
by `unity = (−si.y, si.z, si.x)` (determinant −1, so it flips handedness
correctly). All conversion lives in one helper in `Sim`; the core never sees a
Unity type.

### Verification

- `SceneBuilder` runs headless and the project compiles clean.
- A headless play-mode smoke test: load the scene, step `SimulationRunner` for a
  few seconds with a scripted driver, assert no exceptions and state advances.
- The owner opens `PlayScene`, presses Play, and drives.

### Not in this pass

Vehicle art/wheels (use primitives), engine/tyre audio, force feedback,
multiple vehicles, replay recording UI (trace record/playback is a separate
`IDriverInput` task), VR.

**Estimate:** medium. Mostly wiring + a builder script + a simple HUD.

---

## Open questions for the owner

See the review request. Defaults if unanswered: keyboard + gamepad; live
stabilizer on/off + switching; chase camera default with Tab to cycle;
pendulum-ROM liquid shown as a tilted translucent block.
