# Decisions log

Short records of architectural decisions taken during implementation, with the
reasoning. Newest first. These refine `design-review.md`; they do not replace it.

---

## D4 — 2026-08-28 — Shallow-water slosh model: scheme and coupling choices

`ShallowWaterSloshModel` (phase-2 model A, design-review §6) is a 2-D
depth-averaged Saint-Venant solver behind the unchanged `ISloshModel`
interface. Choices made:

- **Scheme.** First-order finite-volume, **Rusanov (local Lax–Friedrichs)**
  flux, forward Euler, on a ~48 × 28 grid (nominal tank) derived from a ~25 mm
  target cell size. Reflective walls (only the hydrostatic pressure passes),
  weir overflow once a wall cell tops the rim. Chosen for robustness and
  guaranteed positivity over accuracy; it is numerically diffusive but the
  first mode is resolved by ~90 cells/wavelength so the frequency is still
  within the same ±15 % band the pendulum model meets. A MUSCL/limited
  reconstruction is a drop-in upgrade if a later result needs less damping.
- **Determinism.** Sub-step count each 600 Hz tick is `ceil(1.5·dt·s_max/CFL)`
  (CFL 0.40), a deterministic function of the field — no wall-clock, no
  threading. Hard clamps (h ≥ 1e-6, |u| ≤ 8 m/s) mirror the pendulum model's
  anti-NaN guards.
- **Frame.** Internal grid origin at the **tank floor centre**, matching the
  effective convention of `PendulumSloshModel` (which also returns torque
  about the floor centre). If the true gimbal-pivot height ever needs to be
  the torque reference, both models change together.
- **Reaction (decision D3 applied).** The solver integrates the *true* motion
  including the tank angular-acceleration (Euler) term. The reaction fed back
  to the tank is **quasi-steady**: Σ mₖ·(g − a_O − ω×(ω×r) − 2ω×v_rel) per
  cell, i.e. the liquid mass distribution × its body force with the Euler term
  omitted. That term is delivered as a constant **reflected inertia** computed
  from the *same* analytic equivalent-mechanical estimate the pendulum model
  uses — so the actuator sees an identical rotational load under both models
  and only the wave dynamics differ. The wave's own momentum flux against the
  walls (a second-order reaction correction) is not fed back in v1.
- **Not used from `SloshParameters`:** `ParticipatingMassScale` (inherent in
  the field) except for the reflected-inertia estimate, `SurfaceSlopePer­
  PendulumAngle` (there is a real surface). `NaturalFrequencyScale` is applied
  by scaling the gravity-wave term only.
- **`ModeState`** is reported as an *equivalent* pitch/roll slosh angle+rate
  from the mean free-surface slope, so the privileged baselines keep working.

The pendulum ROM stays the default for bulk RL (cheaper); shallow water is the
fidelity check and an optional higher-fidelity eval/training model.

---

## D3 — 2026-08-28 — Slosh ↔ actuator coupling: split inertia vs. load

The slosh model and the gimbal actuator are coupled both ways. Feeding the full
slosh reaction torque to the actuator as an external load created an unstable
explicit algebraic loop (the reaction's angular-acceleration term acts like
negative inertia). Resolved by:

1. **Split the reaction.** The slosh model reports a roughly-constant
   `ReflectedInertia*` per axis (`IReflectedInertiaSource`); the simulation adds
   it to the actuator's axis inertia. The reaction torque the model returns each
   step then omits the tank-angular-acceleration term, leaving only the genuine
   state-dependent (θ, θ̇, base motion) disturbance, which is fed back as an
   external load with a one-step lag — stable at 600 Hz.
2. **Pivot doesn't translate under gimbal motion.** `TankKinematics` computes the
   pivot's linear acceleration from vehicle body motion only. The gimbal pivot is
   the centre of rotation, so joint rates/accels don't move it; including them
   injected a large lever-arm (≈2.6 m) feedback into the slosh model.
3. **Actuator is a position servo, not a bare rate loop.** The commanded rate
   integrates to a position setpoint that a PD+I loop tracks, so a steady
   external load produces bounded sag, not drift.

Pendulum angle/rate are clamped to the ROM's validity range (~75°, 25 rad/s) so a
bad transient can't cascade to NaN; hitting the clamp in normal use signals an
out-of-envelope scenario.

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
