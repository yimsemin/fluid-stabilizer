# Road model

Added 2026-08-29 (plan review). Design-review §4/§5 list "road with grade, bank
and roughness" as a v1 must-model; it had a stub (`RoadInput`) but nothing fed
it. This closes that gap.

## Why it matters

A rigidly-held tank (`HoldController`, decision D5) transmits every road
disturbance straight into the liquid. On a perfectly flat road the rigid tank
barely spills under realistic highway driving (finding F4), so there is little
for a learned policy to beat. A real road — undulating grade, curve bank, surface
roughness, the odd pothole — is the disturbance that a rigid tank *cannot*
reject and an anticipatory policy can. It puts the three-way comparison
(project-goals.md) back into an interesting regime.

## What it models — `RoadModel` (engine-free, `FluidStabilizer.Core.Sim`)

A deterministic function of **distance travelled** `s`, three superposed parts:

| Part | Model | Effect on the truck |
|---|---|---|
| **Hills** | two sinusoids vs `s`, wavelength ~`HillWavelengthM` (140 m), peak `GradeAmplitudeRad` (~3.5 %) | a slowly varying **grade**: a gravity component along the road + a body-pitch trim → the tank tilts relative to gravity |
| **Bank** | one sinusoid vs `s`, wavelength ~`BankWavelengthM` (90 m), amplitude `BankAmplitudeRad` (~1.1°) | curve superelevation / cross-slope → a body-roll trim |
| **Roughness** | 6 sinusoids, wavelengths geometric 0.6–9 m, red-ish spectrum (amp ∝ 1/k), rescaled so RMS = `RoughnessRmsM` (6 mm) | continuous vertical surface displacement → ride vibration |
| **Events** | Poisson process along the road, `EventsPerKm` (12), raised-cosine bump/pothole of mean height `EventHeightM` (±4 cm), length `EventLengthM` (0.35 m) | discrete jolts — expansion joints, potholes, stray rocks, curbs |

`Severity` (default 1) scales roughness + events (not grade/bank) — the single
knob for a curriculum or domain randomization. All output is zero when
`RoadConfig.Enabled` is false, which is the v1 default, so **every existing
scenario and determinism guard is unchanged.**

## Suspension heave

`VehicleModel` gained a third sprung-mass DOF (it had pitch + roll; design-review
§8 asks for "heave, pitch, roll"). The body follows the road surface through a
spring-mass-damper — `HeaveNaturalFreqHz` 1.6, `HeaveDampingRatio` 0.30, the
~1.5 Hz truck ride mode. So a sharp bump reaches the tank as a *filtered* jolt
with the suspension's own resonance (design-review §4: "the suspension is a
low-pass with a ~1–2 Hz resonance that shapes the disturbance"), not an
instantaneous acceleration spike. The body's vertical acceleration goes into the
tank-frame kinematics exactly like the longitudinal/lateral terms; its vertical
displacement goes into `VehicleState.OriginWorld.Z` so the truck visibly bounces.

## Determinism

`RoadModel` draws all its parameters from one forked `IRandomSource` at
`Reset(seed)`. Discrete events are generated lazily as `s` advances, in a strict
forward order, so the sequence is independent of how the distance axis is chunked
(`RoadModelTests.Sampling_IsIndependentOfStepSize`). A full loop episode with the
road on is bit-reproducible (`InLoop_RoughRoad_AddsSloshEnergy_AndIsDeterministic`).

## Where it is wired

- `SimulationConfig.Road` (`RoadConfig`) — all parameters.
- `SimulationLoop` — samples the road each physics step at the running distance,
  feeds `VehicleModel.Step`, records grade/bank/surface height into
  `GroundTruthFrame` (HUD + logging).
- Scenarios: `RoughRoadHighway`, `RoughRoadLowFreeboard` (the existing 6 are
  untouched).
- RL launcher: `-rlRoad {none|mild|rough}`.
- Play scene: `SimulationRunner.Road` / `RoadSeverity` inspector fields; **`G`**
  toggles the road live; the HUD shows grade %, bank, surface height.

## Not modelled (deferred)

Road-induced yaw (crown steer), tyre envelopment of short bumps, spatially
correlated left/right tracks (single-track profile in v1), banked-curve lateral
load coupling beyond the body-roll trim. None affect the dominant disturbance
path (vertical + grade) that reaches the liquid.
