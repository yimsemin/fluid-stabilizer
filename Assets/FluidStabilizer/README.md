# Assets/FluidStabilizer — code layout

Four assemblies, arranged so the architectural boundary from
`docs/design-review.md` is enforced by the **compiler**, not by discipline.

| Assembly | Engine ref? | Depends on | Holds |
|---|---|---|---|
| **FluidStabilizer.Contracts** | ❌ `noEngineReferences` | — | Message DTOs (`SensorBundle`, `StabilizerCommand`, …), interfaces (`IClock`, `IRandomSource`, `IController`, `ISloshModel`, `IDriverInput`), math (`Double3`, `MathD`). |
| **FluidStabilizer.Core** | ❌ `noEngineReferences` | Contracts | Deterministic reference implementations with no engine dependency: `Pcg32`, `DeterministicRandom`, `ManualClock`, (later) numerical integrators. |
| **FluidStabilizer.Controller** | ❌ `noEngineReferences` | Contracts, Core | Stabilizer controllers only: `HoldController` now; analytic / PID / privileged-MPC baselines and the RL policy adapter later. **The design's hard rule — "controller has zero `UnityEngine` references" — is guaranteed here by `noEngineReferences: true`.** |
| **FluidStabilizer.Sim** | ✅ | Contracts, Core, Controller, Unity.InputSystem | The thin Unity shell: `SimulationRunner` (paces the engine-free loop in real time), `HumanDriverInput`, `SimView` (visualisation), `Hud`, `PlayControls`, `Frames` (the one place SI right-handed ↔ Unity left-handed conversion happens). Never leaks a Unity type inward. |
| **FluidStabilizer.Sim.Editor** | ✅ (Editor) | Contracts, Core, Controller, Sim | `SceneBuilder` — builds `Scenes/PlayScene.unity` from code (menu ▸ FluidStabilizer ▸ Rebuild Play Scene). |
| **FluidStabilizer.Tests.EditMode** | ✅ (Editor only) | Contracts, Core, Controller | Fast deterministic unit tests for the pure-C# code. Run with the Unity Test Runner or headless (`-runTests -testPlatform EditMode`). |

Why the split is worth it (and not over-abstraction): it is the minimum needed
so that a future ROS 2 bridge or an external Python trainer can consume
`Contracts` + `Controller` with **no Unity install at all**, and so that
accidentally referencing a `GameObject` from a controller is a compile error.

## Adding code

- New message type or interface → `Contracts`.
- New engine-free helper (integrator, filter, math) → `Core`.
- New controller / policy → `Controller`.
- Anything touching `MonoBehaviour`, `Rigidbody`, `Transform`, input, rendering → `Sim`.

See `docs/frames-units-conventions.md` for frames, units, and update rates before
adding physics.
