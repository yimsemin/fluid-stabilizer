# Decisions log

Short records of architectural decisions taken during implementation, with the
reasoning. Newest first. These refine `design-review.md`; they do not replace it.

---

## D2 — 2026-08-27 — Optional sensors use `Has*` flags, not `Nullable<T>`

`Nullable<struct>` does not survive Unity serialization and does not map cleanly
to a wire/ROS representation. `SensorBundle` carries `HasLevels` / `HasVehicleCan`
booleans instead.

---

## D1 — 2026-08-27 — The mechanical simulation runs in managed code, not PhysX

**Decision.** The whole rigid-body dynamics of the problem — vehicle sprung-mass
motion, the 2-axis gimbal, and the slosh model — is integrated in plain C# in
`FluidStabilizer.Core` at the fixed 500 Hz step. Unity PhysX is **not** used for
any simulation dynamics. `FluidStabilizer.Sim` uses Unity only for
visualization, input, and scene wiring.

**Why.**

- **Determinism across Mac and Windows.** PhysX in Unity is only reproducible on
  the same machine/build; cross-platform bit-reproducibility is not guaranteed.
  The design review lists determinism as a top-5 risk and the owner wants to
  develop on both platforms. A managed fixed-step integrator with a seeded RNG is
  bit-identical everywhere.
- **The system is small.** Vehicle heave/pitch/roll + longitudinal/lateral
  kinematics (~10 states), 2 gimbal joints + actuator internal state (~8), 2
  slosh pendulums (~4). ~20 state variables total — a custom semi-implicit /
  RK4 integrator handles this trivially at 500 Hz.
- **The design already points here.** It calls for a custom 3-DOF sprung-mass
  vehicle (not WheelColliders) and for the slosh ODE to be integrated outside
  PhysX. Extending "custom" to the whole mechanical core removes the seam between
  PhysX bodies and the managed slosh ODE, which is where solver noise and
  coupling bugs would live.
- **Speed and testability.** No physics engine in the RL step loop → more
  simulation steps per second, and the entire simulation is unit-testable with
  no Unity process (it lives in an engine-free assembly).

**Cost / risk.**

- We write our own contact/road model and gimbal-joint dynamics. Mitigated by
  keeping fidelity deliberately modest (design review §7–§9) and validating
  against analytic cases in tests.
- The tank is kinematically attached to the vehicle through the gimbal, so slosh
  forces do not move the vehicle in v1 (truck mass ≫ tank+liquid). Slosh does
  load the gimbal actuator and the load cells. This approximation is documented
  and revisitable.

**Consequences for layout.** `FluidStabilizer.Core` now also contains the
simulation loop and physics subsystems. `Sim` shrinks to a thin Unity shell.
