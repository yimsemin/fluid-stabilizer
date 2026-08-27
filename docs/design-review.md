# Design review — fluid stabilizer

Status: **draft for owner decision.** No implementation has started based on this.

This document critically reviews the project brief, recommends an architecture,
and calls out the failure modes most likely to sink the project. Read
`project-brief.md` first.

---

## 0. Executive summary

The idea is a **legitimate Physical AI problem**, but only if the plant is
deliberately tuned into the regime where reactive rule-based control fails. Left
naive, a hand-written "keep the liquid surface perpendicular to the felt
acceleration" controller will solve most of it, and RL will have little to prove.

The main recommendations:

1. **Do not use a particle fluid (SPH/FLIP) or PhysX joint pendulums for the
   slosh model.** Use a custom-integrated **equivalent mechanical model** (one
   pendulum per axis, analytically integrated), behind an interface so a
   shallow-water model can replace it later. Reasons: determinism, speed,
   calibration, RL step budget.
2. **Do not adopt Unity ML-Agents as the backbone.** Keep a clean
   sensor/actuator message boundary; make the trainer swappable. ML-Agents, if
   used at all, sits behind an adapter.
3. **Build the baseline controllers first** (specific-force alignment, tuned
   PID, and a privileged full-state MPC/LQR). They define the floor and ceiling.
   If RL cannot get meaningfully above the PID floor, the task or the sim is
   wrong — find that out in week 2, not month 6.
4. **Use residual RL**: the policy outputs a correction on top of the analytic
   baseline. Guarantees a sane floor and focuses learning on the hard residual
   (slosh state, actuator delay/limits, plant variation).
5. **Model latency, discrete-time control, sensor noise/bias, and actuator
   non-idealities from day one.** These are the sim-to-real killers and they are
   cheap to include.
6. **Asymmetric actor-critic**: critic sees ground truth, actor sees only
   sensors. Enforced by the interface — the actor literally cannot call
   ground-truth.
7. **Spend fidelity on the actuator, not the vehicle.** The AI controls the
   actuator; it only needs *believable bed motion* from the vehicle.

---

## 1. Is this an appropriate Physical AI learning problem?

**Yes, conditionally.** It has the ingredients that make a control problem
RL-worthy:

- **Genuine partial observability.** Liquid CoM, slosh mode amplitudes, and the
  gravity/acceleration split on a tilting vehicle are not directly measurable.
- **Non-trivial temporal dynamics.** Sloshing is a lightly damped oscillator;
  ring-down outlasts the maneuver; the control action shapes future state.
- **Back-coupling.** Moving liquid changes tank CoM and the torque the actuator
  feels — the plant is state-dependent.
- **Non-ideal actuator.** Rate/torque limits + transport delay + backlash mean
  you cannot purely react; you must anticipate.
- **Unpredictable exogenous disturbance** (the human driver).
- **Plant uncertainty** (fill level, liquid properties, actuator wear) that
  rewards an adaptive policy over fixed gains.

**The condition:** every one of those must be strong enough to matter. If the
actuator is fast, the tank rigid, the freeboard generous, and the slosh heavily
damped, the problem collapses to "point the tank at the felt gravity vector" and
a first-year controls exercise beats RL. See §2 and §12.

**Framing to keep honest:** RL's realistic contribution here is *adaptive
disturbance rejection and slosh-state inference*, not "discovering control from
nothing." That is still a real Physical AI result. Don't oversell it as more.

---

## 2. Will a rule-based controller solve most of it?

**Risk: high, if the plant is not tuned against it.** The obvious baseline:

> Estimate the specific-force vector **f = a − g** in the tank frame from the
> tank IMU (low-pass to reject slosh-induced accel). Command the tank attitude
> so the tank floor is perpendicular to **f**. Rate-limit the command. Add a
> notch filter at the slosh natural frequency so the command does not excite the
> slosh mode.

This is a pendulum-emulating controller and it is genuinely good for the
quasi-static and slow-maneuver regime. A tuned PID on `(tank tilt error, tank
gyro)` gets further. Together they will likely prevent spills for gentle-to-
moderate driving.

**Where rule-based control breaks down — and where RL earns its place:**

| Regime | Why rule-based struggles |
|---|---|
| Actuator rate/torque-limited vs. the disturbance bandwidth | By the time you *feel* the acceleration and start moving, it is too late. Needs anticipation from disturbance trends and driver-intent signals. |
| Slosh resonance | Naive tilt-tracking pumps energy into the slosh mode at certain maneuver frequencies. Needs active slosh damping, which fights the tilt-tracking objective. |
| Actuator delay + backlash | Fixed-gain feedback goes unstable or chatters near the backlash dead zone; needs a learned compensator. |
| Hidden slosh state | Feedforward from IMU alone is insufficient; benefits from an internal observer / memory. |
| Plant variation (fill, damping, wear) | One gain set is a compromise; an adaptive policy that infers the plant from history wins. |
| Post-maneuver ring-down while a *new* maneuver starts | Superposed slosh modes with phase that matters; hard to hand-tune. |

**Mitigations baked into the design:**

- Choose tank geometry so the **first slosh natural frequency (~0.4–1.2 Hz)**
  overlaps the band of real truck maneuvers and truck-suspension resonance.
- Choose an actuator whose **closed-loop bandwidth (~2–5 Hz)** and **rate limit**
  are low enough that anticipation beats reaction for aggressive driving.
- Mount the gimbal pivot **near the tank CoM** (neutrally stable), so a free
  gimbal does *not* passively solve the static case and active control is
  actually required. Document this as a deliberate choice.
- Keep **freeboard** (rim height above the still-liquid surface) small enough
  that spillage is reachable under aggressive driving but not under gentle
  driving.
- **Domain-randomize** the plant so no single fixed-gain controller is optimal.
- Use **residual RL** so the comparison is explicitly "baseline" vs.
  "baseline + learned correction," and any RL win is unambiguous.

**Methodological rule:** the analytic + PID baselines are deliverables, not
throwaways. Every RL result is reported against them and against a privileged
full-state MPC (the performance ceiling, §12).

---

## 3. What is needed to make slosh temporal dynamics a real learning problem?

1. **A slosh model with real dynamics**, not a quasi-static CoM shift. It must
   represent at minimum the **first mode's natural frequency and damping ratio**
   per axis, and its force/moment reaction on the tank.
2. **Two-way coupling.** Slosh reaction force/torque is applied to the tank
   rigid body and to the actuated joint (varying load torque).
3. **Slosh state hidden from the actor.** The policy must infer it from the
   history of tank IMU, motor current, and load cells → forces a memory
   (frame-stack or recurrent) and rewards an internal observer.
4. **Disturbance spectrum overlapping the slosh band**, so maneuvers can excite
   resonance and the "when do I move" decision is non-trivial.
5. **Episodes long enough to contain ring-down** — several slosh periods after
   the last maneuver — so the cost of leaving energy in the liquid is felt.
6. **Reward that penalizes slosh energy and near-rim margin**, not only spill
   events (§11), so fine continuous control is rewarded.
7. **Pre-excited initial conditions** in some episodes (liquid already moving at
   t=0) so the policy cannot assume a calm start.

---

## 4. Important physics / variables the brief did not mention

- **Freeboard / rim height.** The single most important geometric parameter for
  "does it spill." Must be an explicit, randomized parameter.
- **Slosh natural frequency and damping ratio as first-class parameters.** The
  brief lists "amount of liquid" but not the dynamic response it produces.
- **Vehicle suspension.** The truck bed does not see the road directly — the
  suspension is a low-pass with a ~1–2 Hz resonance that *shapes* the
  disturbance the tank feels. Must be modeled at least as a simple sprung mass.
- **Gravity/acceleration ambiguity.** An IMU measures specific force
  **f = a − g**. Separating gravity from motion on a tilting truck needs an
  attitude estimator. Keep this in the problem — do **not** hand the policy a
  clean gravity vector.
- **Actuator holding torque (thermal limit).** On a long hill the actuator holds
  a tilted tank against gravity indefinitely. Continuous-torque limit ≠ peak
  torque. Model both, or the real motor overheats.
- **Two-axis kinematic coupling.** Rotation order matters; near ±90° the axes
  degenerate. Keep max travel well under 90° (≈ ±25–35°) and fix the rotation
  order.
- **Rotary / swirl slosh** in near-square or cylindrical tanks (diagonal
  excitation drives a rotating wave). Real and hard. **Avoid it in v1** by using
  a distinctly rectangular tank (aspect ratio ≥ ~1.6).
- **Continuous spill measure.** "Spilled / not spilled" is too sparse for a
  reward gradient. Need overflow volume per step.
- **Sensor imperfections as concrete models** — noise density, bias, bias random
  walk, scale-factor error, axis misalignment, quantization, update rate,
  latency, jitter, dropped frames. "Realistic sensors" must be numbers, or the
  policy overfits to clean data.
- **Control/actuation latency and its jitter.** The #1 sim-to-real gap for
  feedback control.
- **Time-scale separation.** Physics/slosh integration (200–500 Hz), sensor
  sampling (100–200 Hz), control (50–100 Hz) — must be designed explicitly, not
  left to Unity's frame rate.
- **Mounting misalignment** tank↔vehicle and IMU↔body (small unknown rotations
  and offsets) — a classic reality gap, trivial to randomize.
- **Driver-in-the-loop data problem.** You cannot have a human drive 10^8
  training steps. Synthetic driver profiles must cover the real input
  distribution or the policy overfits (§14, §17).

Lower priority / can defer: baffles and multi-compartment tanks, mount
structural compliance, tire slip dynamics, aerodynamics, thermal/battery droop,
wave breaking / air entrainment / surface tension.

---

## 5. Must-model vs. can-skip

### Must model (v1)

- Vehicle as a sprung mass with a simple suspension → realistic 6-DOF bed motion.
- Tank as a rigid body on a 2-DOF actuated gimbal, pivot near tank CoM.
- Reduced-order slosh model with correct first-mode natural frequency + damping,
  coupled both ways to tank and joint.
- Actuator per axis: 2nd-order response OR motor+gearbox, plus torque saturation
  (peak + continuous), rate limit, travel limit, backlash dead zone, Coulomb +
  viscous friction, transport delay.
- Sensors with concrete noise/bias/delay/rate: vehicle IMU, tank IMU, 3–4 load
  cells, 2 joint encoders, 2 motor-current estimates. Optional 1–3 point
  liquid-level sensors.
- Continuous spill measure (overflow volume per step).
- Road with grade, bank, and roughness (sinusoids + discrete steps).
- Discrete-time control loop at a fixed realistic rate with zero-order hold.
- Seeded RNG for every stochastic element; fixed timestep.

### Can skip (v1)

- Particle fluid (SPH/FLIP), CFD.
- Wave breaking, splash, surface tension, air entrainment.
- Baffles, multi-compartment tanks.
- Rotary/swirl slosh (avoid via tank aspect ratio).
- Mount structural compliance.
- Detailed tire model / tire slip (use a friction-circle point model).
- Aerodynamics, temperature, battery droop.
- Photoreal liquid rendering (a simple visual proxy is fine).
- Yaw or vertical (heave) stabilization axes — 2-axis rotational is the right
  scope.

---

## 6. Liquid slosh implementation options compared

| Option | Physical validity | Compute cost | RL suitability | Impl. difficulty | Verdict |
|---|---|---|---|---|---|
| **A. Quasi-static CoM shift** (point mass at equilibrium surface) | None (no dynamics) — violates the core requirement | Negligible | N/A | Trivial | **Sanity check only**, not the model |
| **B. Equivalent mechanical model** — 1 pendulum (or spring-mass) per axis, analytically integrated, 2-way coupled | High for fill 20–80 %, low–moderate amplitude, first mode dominant. Well validated (NASA SP-106, automotive tank-slosh control literature) | Negligible (2–4 extra ODE states, closed form). 500+ Hz easily | **Excellent** — deterministic, fast, stable, parameters map 1:1 to domain randomization | Moderate | **Recommended for v1** |
| **C. Multi-mode modal ROM** — superpose first N analytic slosh modes for a rectangular tank, each a damped driven oscillator | Better for higher amplitude / multi-frequency excitation; still linear-ish | Low (N × option B) | Very good | Moderate-high (need mode shapes) | **Recommended phase-2 upgrade** — same interface as B |
| **D. Shallow-water (Saint-Venant) on a height field** — depth-averaged 2D solver, tank accel as body force | Good for shallow–moderate depth; captures traveling waves, hydraulic jumps, spatial surface, spill location. Misses vertical accel, breaking, deep-water dispersion | Low–moderate: a 64×32 grid at a few hundred Hz is cheap on CPU | Good, with care — larger state, determinism needs fixed dt and no threading races; solver can go unstable and disrupt training | Moderate-high (need a stable CFL-limited scheme) | **Recommended fidelity option for phase 2/3.** Gives spill-over-rim and level sensors directly |
| **E. SPH / FLIP particle fluid** (Obi Fluid, Zibra, custom compute) | Highest visual; noisy, non-deterministic, parameter-fragile, hard to calibrate to a real natural frequency | 10–100× everything else; destroys the RL step budget (need 10^7–10^8 steps) | **Poor** — non-determinism breaks reproducibility; GPU contention with training | High; third-party license/dependency; sim-to-real calibration is guesswork | **Reject for training.** Optionally run once, offline, to tune/validate B–D |
| **F. Many-rigidbody "ball pit"** (fill tank with small PhysX spheres) | Wrong wave physics, wrong damping, noisy; tuning natural frequency is a hack | Moderate-high | Poor (PhysX solver noise, non-determinism) | Low-moderate | **Reject** |

### Recommendation

Start with **B**. Define a `ISloshModel` interface
(`Step(tankAcceleration, tankAngularState, dt) → {reactionForce, reactionTorque,
surfacePeakHeightPerEdge}`) so **C** and **D** are drop-in replacements without
touching the actuator, sensor, or controller layers. Validate B/C/D against **E**
or against published slosh data before trusting RL numbers.

**Caveat for option B:** it does not directly know that liquid crossed the rim.
You estimate spill from `pendulum angle + fill level → surface peak at each rim
edge → overflow volume`. This mapping is approximate; decide it early and
validate it against a shallow-water run. Option D gives spill directly — that is
a real reason to move to D in phase 2.

---

## 7. (covered by the table in §6)

---

## 8. Vehicle physics model — appropriate fidelity

**Verdict: medium-low. Do not build a AAA vehicle sim.** The AI does not drive;
it only needs believable 6-DOF motion of the tank base.

Recommended:

- **A 3-DOF sprung-mass model** (heave, pitch, roll) on a simple axle/suspension,
  driven by:
  - longitudinal acceleration from a **point-mass "bicycle" model** with a
    **friction circle** (so hard braking / hard cornering plateau realistically),
  - lateral acceleration from steering angle and speed,
  - vertical excitation from the road profile,
  - load transfer between axles/sides.
- **Prefer a custom sprung-mass model over Unity `WheelCollider`s.** WheelColliders
  are finicky, version-sensitive, and a determinism risk, for a subsystem the AI
  never touches.
- **Road** as parameterized grade + bank + roughness (sum of sinusoids + discrete
  bumps / washboard).
- **Driver input** behind an `IDriverInput` interface: a human implementation
  (keyboard/gamepad) and scripted/stochastic profiles for training, plus
  recorded human traces.

Fidelity you *do* want: correct steady-state and transient magnitudes of bed
acceleration and tilt, and the suspension resonance. Fidelity you do not want:
tire thermal models, differential/driveline detail, aero.

---

## 9. Stabilizer actuator model — appropriate fidelity

**Verdict: high. This is where fidelity budget goes.** It is the thing the AI
controls and the primary sim-to-real risk, and it is cheap to model (a few
states per axis).

Per axis, model:

- **Command interface:** target angle **or** target rate (recommended) **or**
  torque. See §11.
- **Drivetrain:** either a calibrated 2nd-order closed loop (bandwidth ~2–5 Hz,
  damping ~0.7) **or** explicit motor + gearbox: `τ_motor = Kt·i`,
  current-limited, voltage-limited (back-EMF → speed-dependent torque ceiling),
  gear ratio `N`, efficiency `η`, reflected inertia.
- **Hard limits:** peak torque, **continuous (thermal) torque**, max rate
  `ω_max`, travel `±θ_max` with soft end-stops.
- **Backlash:** dead zone `±δ` on reversals; output lags command through the
  dead band.
- **Friction:** Coulomb + viscous at the joint.
- **Transport delay:** `τ_d` ≈ 10–50 ms on command receipt (bus + compute).
- **Load sensitivity:** effective inertia = tank inertia + slosh reaction. This
  emerges for free if the slosh reaction torque is applied to the joint — and it
  is exactly the "same command, different result" behavior the brief wants.
- **Sensors on the actuator:** output encoder (12–16 bit quantization, slight
  noise), motor current (noisy, ∝ commanded torque) — the actuator's "felt
  load," a proxy for slosh reaction.

Calibrate these numbers to a **real candidate servo/gearmotor datasheet** now,
before hardware exists.

---

## 10. Sensor configuration — realism assessment

The proposed set is well chosen and realistic. Per sensor:

| Sensor | Realistic? | Keep? | Notes |
|---|---|---|---|
| Vehicle IMU (accel + gyro) | Yes | Yes | Add noise/bias/random-walk/delay, ~100–400 Hz. |
| Tank IMU (accel + gyro) | Yes | Yes | Senses slosh-induced tilt + tank motion. Same imperfection model. Distinct mounting misalignment. |
| Load cell(s) | Yes | Yes — use **3–4** in a pattern | One gives total weight (easy). 3–4 give a partial CoM estimate *the way real rigs do it* — partial slosh observability without cheating. |
| Stabilizer encoder | Yes | Yes — essential | Output-side angle per axis; quantized. |
| Motor current / torque estimate | Yes | Yes | Proxy for slosh reaction load; valuable. |
| Limited liquid-level sensor | Only as **point** sensors (float / ultrasonic / capacitive rod) at fixed locations | Optional, 1–3 points | A single ultrasonic at tank center reads local surface height — heavily slosh-contaminated, which is *useful signal*. A full level field is **not** realistic — forbid it. |

**Additions to consider:**

- **Vehicle speed / wheel speed** (from vehicle CAN) — realistic, cheap, helps
  a lot (`a_lateral ≈ v²·yawrate`). **Recommend adding.**
- **Steering angle + throttle/brake position** (from vehicle CAN) — realistic on
  a real truck (OBD/CAN). Borderline: it is a partial *preview* of the
  disturbance ("driver intent"). Real deployments do have this. **Recommend an
  optional observation flag** — train with and without, measure how much the
  policy leans on it.
- **Forward-looking road/camera preview** — the brief explicitly excludes future
  road info. **Honor that for v1.** But note: a look-ahead sensor enabling
  preview control is the single largest possible performance gain. Flag it as a
  future ablation, not a v1 feature.

**Remove:** nothing. **Redundant, skip:** tank barometer/inclinometer (covered
by IMU fusion).

---

## 11. Observation / Action / Reward candidates

### Observation (actor) — all sensor-derived, noisy, delayed

- Vehicle IMU: 3-axis accel + 3-axis gyro (body frame)
- Tank IMU: 3-axis accel + 3-axis gyro (tank frame)
- Joint state: 2 angles, 2 rates (encoder-differenced), 2 motor currents
- Load cells: 3–4 scalars
- Optional: 1–3 point liquid-level readings
- Optional flag: vehicle CAN (speed, steer, throttle, brake)
- **Last action(s)** — essential for delay/backlash compensation
- Allowed derived features (computable on-board): low-passed specific-force
  direction in tank frame and its recent rate of change
- **Memory:** frame stack of the last 5–10 control steps, or a recurrent hidden
  state — required for slosh-state inference

**Never in the actor observation:** true liquid CoM, particle positions, true
slosh mode amplitudes, true attitude/velocity, future road, future driver input,
physics internals. These go to the critic and to logging only.

### Action

- **Recommended v1:** joint **angular-rate** command per axis (2 values),
  integrated by a low-level actuator controller. Smoother than step angle
  commands, avoids chatter. (Target-angle command is an acceptable alternative.)
- **Torque command:** most faithful to hardware, hardest to learn, least safe —
  an advanced option, not v1.
- **Strongly recommended: residual action.** Action = analytic-baseline command
  + learned correction. Guarantees a sane floor, focuses learning on the hard
  residual, and de-risks "RL cannot beat PID."
- Always include an **action-rate penalty**.

### Reward (per step, continuous)

```
r = − k1 · overflow_volume                     (primary)
    − k2 · near_rim_margin_cost                 (smooth cost as surface peak nears the rim)
    − k3 · slosh_kinetic_energy                 (reward actively damping the liquid)
    − k4 · |action_rate|
    − k5 · actuator_effort (|torque| or power)
    − k6 · joint_limit_proximity_cost
    − k7 · |tank_tilt_relative_to_vehicle|      (optional, small — discourages wild gymnastics)
    + k8  (small alive bonus, optional)
    − K_term  on a large spill event (terminal)
```

Curriculum: start with gentle driver profiles, low fill, generous freeboard;
widen over training.

**Reward-hacking watch:** a policy may slam the tank to a hard end-stop to
"cage" the liquid, or exploit slosh-model artifacts. Mitigate with the effort /
limit-proximity penalties and by validating the trained policy on a
higher-fidelity slosh model it never trained on.

### Critic (training only) — privileged observation

Everything the actor sees, **plus**: true slosh mode amplitudes/velocities, true
liquid CoM, true specific force, true tank attitude/velocity, true actuator
state. Asymmetric actor-critic → large training speedup, zero deployment
cheating.

---

## 12. AI architecture for temporal state

- **Memory is required** (hidden slosh state). Progression:
  1. **Frame-stack + MLP, PPO** — start here. Often sufficient.
  2. **GRU/LSTM recurrent policy** — if frame-stack plateaus.
  3. Transformer over a window — overkill for v1.
  4. Explicit learned state estimator (Kalman / learned observer) feeding an MLP
     — a strong, interpretable option; good for sim-to-real.
- **Asymmetric actor-critic** (critic sees ground truth). Strongly recommended.
- **Auxiliary self-supervised head:** predict the true slosh state from the
  observation history. Improves the learned representation at low cost.
- **Implicit system identification** (RMA-style): an encoder maps recent history
  to a latent plant-parameter vector; trained by distillation from a privileged
  encoder that sees the true randomized parameters. This is how the policy
  adapts to fill level / damping / actuator wear at deployment.
- **Algorithm:** PPO first (robust, forgiving). Consider SAC later for sample
  efficiency.

### On Unity ML-Agents

ML-Agents gives a lot for free (PPO/SAC, the Unity↔Python plumbing) and, for a
non-professional developer, is genuinely tempting. **But it couples training to
Unity, its API churns across versions, and it works against the stated
Sensor→Controller→Actuator decoupling and the ROS-2 goal.** Later, wanting an
external trainer or a real robot, you would find the environment welded to
ML-Agents.

**Recommendation:** do **not** make ML-Agents the backbone. Define a
language-agnostic env boundary:

- In-process C# `ISensorBus` / `IActuatorBus` for a C# controller now.
- A thin socket / gRPC bridge exposing a **Gymnasium-style** `reset()/step()`
  API for an external Python trainer (Stable-Baselines3, CleanRL, `rsl_rl`).

If the bridge later becomes a throughput bottleneck, *then* evaluate ML-Agents
behind an adapter. Not before.

---

## 13. Domain randomization targets

Per episode (some also as per-step noise):

- **Liquid:** fill ratio (0.3–0.8), density, first-mode natural frequency
  (±20–40 %), damping ratio (×0.3–3), participating mass fraction.
- **Tank:** empty mass, inertia tensor, freeboard / rim height.
- **Actuator:** peak & continuous torque, rate limit, bandwidth, backlash width,
  transport delay (10–50 ms), Coulomb + viscous friction, gear efficiency.
- **Sensors:** noise σ, bias, bias random-walk rate, scale-factor error, axis
  misalignment, added latency, update-rate jitter, quantization step, occasional
  dropped frame.
- **Geometry:** tank↔vehicle mounting misalignment, IMU mounting rotation/offset.
- **Vehicle:** mass, CoG height, suspension stiffness/damping.
- **Road:** grade, bank, roughness spectrum, discrete bump size/spacing.
- **Driver:** profile aggressiveness, reaction-time noise, input quantization &
  latency.
- **Control loop:** rate jitter, occasional dropped command.
- **Initial condition:** pre-excited slosh state.

Curriculum: start near nominal, widen over training. Log the sampled parameters
with every episode for later analysis.

---

## 14. Sim-to-real — what to do from day one

- **Latency budget.** Model and log every delay: sensor acquisition, bus,
  compute, actuation. Randomize them. This is the #1 reality gap for feedback
  control.
- **Discrete-time control** at a fixed realistic embedded rate (50–100 Hz),
  zero-order hold on actions. No continuous-time cheating.
- **Units, frames, timestamps everywhere.** SI units. Right-handed frames
  defined once (see `frames-units-conventions.md`). Every sensor sample carries
  a timestamp; the controller works off timestamps, not frame counts.
- **Calibrate actuator and sensor models to real datasheets** of candidate parts
  now.
- **Asymmetric info stays out of the actor**, enforced by the interface.
- **Domain randomization + implicit system-ID** as the transfer mechanism.
- **Log everything in a replayable, ROS-bag-compatible schema** even before ROS,
  so real logs can later be replayed through the same controller.
- **Controller is a separate module/process** with a defined message API; the
  same controller binary runs against sim now and a ROS 2 bridge later, no code
  change.
- **Reality-gap tests:** held-out driver traces, held-out road profiles, and —
  critically — evaluate on a *different slosh model class* (shallow-water) the
  policy never trained on. Surviving a model-class change is the best cheap
  predictor of surviving reality.
- **Safety envelope** (angle/rate/torque limiting) lives in the low-level
  actuator layer, not the policy — the policy cannot command what the real rig
  would refuse.

---

## 15. ROS 2 / minimal-restructuring plan

Do **not** build the ROS layer now. Do **not** block it either:

- **Message DTOs now**, plain serializable C# structs mapping 1:1 to future ROS 2
  messages, with field names/types matching `sensor_msgs` where possible:
  `VehicleImu` ↔ `sensor_msgs/Imu`, `TankImu` ↔ `sensor_msgs/Imu`,
  `JointState` ↔ `sensor_msgs/JointState`, plus `LoadCells`, `MotorState`,
  `LevelSensors`, `StabilizerCommand`.
- **Message bus interface** `IMessageBus` with an in-process implementation now
  and a ROS 2 implementation later (via `ros2-for-unity` or an external bridge
  process). The ROS 2 dependency stays out of the Unity project — a separate
  `ros2_ws/` appears only when hardware is connected.
- **Controller has zero `UnityEngine` references.** Plain C# library (or Python).
  Messages in, messages out. This is the one hard rule that makes the rest work.
- **`IClock` abstraction** (sim time now, wall time later); fixed-rate scheduler.
- No global mutable state; everything timestamped.

That is the entire "future-proofing" budget. Anything more is over-abstraction.

---

## 16. (merged into §15)

---

## 17. Training environment vs. human-drive test environment

One `SimulationCore`, two front-ends selected by an `IDriverInput`
implementation:

| | Human-drive / test | Training |
|---|---|---|
| Driver input | Keyboard / gamepad, real time | Scripted + stochastic driver profiles; replayed human traces |
| Rendering | On | Off (headless) |
| Time | Real time | Time-scaled, many parallel instances |
| Purpose | Qualitative testing, demos, **recording human traces** | Policy optimization |
| Ground-truth logging | Full | Full (for the critic + eval) |

- **Determinism is mandatory:** fixed timestep, seedable RNG for every
  stochastic element, no frame-rate-dependent logic. Without it you cannot debug
  RL or trust evaluation.
- **Eval env:** headless, full ground-truth logging, with the analytic baseline
  and the privileged MPC running in parallel on the same scenarios for direct
  comparison.
- **Record human driving sessions** → a fixed held-out test set *and* real
  disturbance data for training. This is the highest-value data in the project
  (§14 driver problem).

---

## 18. Additional failure modes (the "you'll have to rebuild it" list)

### "Works first, then you rebuild it"

1. **PhysX joint pendulums or particle fluid for slosh.** You will tune it, get
   something plausible, then discover it is non-deterministic / slow /
   uncalibratable and have to swap the whole slosh model — after the sensor and
   reward code already depend on its quirks. → custom analytic ROM behind
   `ISloshModel` from day one.
2. **ML-Agents as the backbone.** Deep Unity coupling + API churn + fights the
   ROS decoupling goal. → swappable trainer, clean env boundary.
3. **Determinism not designed in.** Unity + PhysX + threads + variable dt →
   non-reproducible episodes → cannot debug RL, cannot trust eval. → fixed
   timestep, seed everything, keep the slosh ODE out of PhysX, control
   parallelism carefully.
4. **Coordinate-frame / unit chaos.** Retrofitting consistent frames, units, and
   timestamps after physics + sensors + controller exist is a painful rewrite. →
   `frames-units-conventions.md` filled in *before* physics code.
5. **Spill defined only as a boolean, then reward won't train.** Adding a
   continuous overflow measure later means re-deriving it inside the slosh model
   and re-tuning every reward weight. → continuous overflow volume from the
   start.

### "Intuitive, but wrong for the goal"

6. **Handing the policy a clean gravity / attitude / true-specific-force
   vector.** Feels reasonable ("the IMU gives acceleration"). It deletes the
   gravity/motion disambiguation problem that is core to the real task and
   inflates sim performance. → raw IMU only; let the policy/estimator fuse.
7. **Reward = spill events only.** Intuitive and clean, but sparse, mostly zero,
   no gradient — the policy never learns fine control. → margin + slosh-energy
   shaping.
8. **A freely pendulous tank (pivot above CoM).** Mechanically "obviously
   right," but it passively solves the static case and makes active control
   nearly unnecessary — the learning problem evaporates. → pivot near CoM,
   neutrally stable, by choice.
9. **Full `WheelCollider` vehicle sim.** Feels like "proper" physics, but adds
   fragility and a determinism risk to a subsystem the AI never controls. →
   3-DOF sprung mass + friction-circle bicycle model.

### "Realistic-looking, but wrong conditions for RL"

10. **High-fidelity SPH fluid.** Looks great in a demo; destroys the step budget
    (need 10^7–10^8 steps), breaks determinism, and its calibration to a real
    natural frequency is guesswork anyway.
11. **Sensors modeled as clean + Gaussian-only.** Real IMUs have bias random
    walk, scale-factor error, misalignment, quantization, dropped frames. A
    policy trained on clean-plus-white-noise data overfits and fails on
    hardware.
12. **Latency added "later, for the hardware phase."** The policy trained
    effectively latency-free learns high-gain behavior that oscillates on the
    real rig. → latency in from step 1.

### Feasibility bracket

13. **The task may be too easy** → RL never beats PID. *Biggest project risk.* →
    build baselines first; tune actuator bandwidth / slosh frequency / freeboard
    / disturbance spectrum into the hard regime; use residual RL.
14. **The task may be effectively uncontrollable** → for aggressive driving with
    small freeboard, *no* controller prevents spills; reward is all negative and
    learning collapses. → verify a privileged full-state MPC (no delay, full
    observation) achieves near-zero spill on the target scenarios. That is the
    ceiling. Keep the task strictly between the PID floor and the MPC ceiling.
15. **The synthetic-driver distribution doesn't match real driving** → the
    policy overfits to scripted maneuvers. → invest in a diverse driver-profile
    generator + real recorded traces early.

---

## 19. Biggest technical risks, ranked

1. **Feasibility bracket (too easy / too hard).** If RL cannot beat a tuned PID,
   or if nothing can prevent spills, the project has no result. Mitigation:
   baselines + MPC ceiling in phase 1; deliberate plant tuning; residual RL.
2. **Slosh model choice.** Wrong choice (particle fluid) costs the step budget
   and determinism; too-simple choice (quasi-static) removes the problem.
   Mitigation: analytic ROM behind an interface, validated against a higher
   fidelity model.
3. **Sim-to-real gap.** Latency, sensor imperfections, actuator non-idealities,
   unmodeled slosh dynamics. Mitigation: model all of it from day one;
   domain-randomize; test across slosh model classes.
4. **Determinism / reproducibility.** Without it, RL debugging and evaluation are
   unreliable. Mitigation: fixed timestep, seeded RNG, slosh ODE outside PhysX.
5. **Driver data distribution.** Synthetic drivers not covering real input.
   Mitigation: driver-profile generator + recorded human traces as held-out
   data.
6. **Scope creep in the vehicle model.** Time sunk into WheelColliders / tire
   models for a subsystem the AI does not control. Mitigation: fixed
   medium-low-fidelity vehicle spec.
7. **Reward hacking / sim-artifact exploitation.** Mitigation: effort/limit
   penalties; cross-model validation.

---

## 20. Phase 1 — what to implement first

1. Unity project + this docs set + `frames-units-conventions.md` completed.
2. `SimulationCore`: fixed-timestep scheduler, `IClock`, seeded RNG service.
3. Vehicle: 3-DOF sprung mass + friction-circle bicycle model + parameterized
   road. `IDriverInput` with a human (keyboard/gamepad) impl + 2–3 scripted
   driver profiles + trace recording/playback.
4. Tank rigid body on a 2-DOF actuated gimbal, pivot near tank CoM.
5. `ISloshModel` + option B implementation (1 pendulum/axis, analytic, 2-way
   coupled). Continuous overflow-volume estimate.
6. Actuator model per axis: motor+gear OR calibrated 2nd-order, + torque
   saturation (peak + continuous) + rate limit + travel limit + backlash +
   friction + transport delay.
7. Sensor layer: vehicle IMU, tank IMU, 3–4 load cells, 2 encoders, 2
   motor-current estimates, all with concrete noise/bias/delay/rate. Message
   DTOs. Optional 1–3 point level sensors behind a flag.
8. `ISensorBus` / `IActuatorBus` interfaces + in-process implementation.
9. Baseline controllers: (a) specific-force alignment, (b) tuned PID, (c)
   privileged full-state LQR/MPC (ceiling).
10. Eval harness: fixed scenario set, spill + slosh-energy + effort metrics,
    baseline comparison, full ground-truth logging (replayable schema).
11. Playable human-drive scene with a simple visual tank + liquid proxy.
12. **One** RL integration path: Gymnasium-style socket adapter + PPO
    (SB3/CleanRL), asymmetric critic, frame-stack observation.
13. Domain-randomization config (start narrow) with per-episode parameter
    logging.
14. Keep the docs in sync as decisions change.

**Phase-1 done when:** a human can drive and see the tank stabilized by the
analytic baseline; the RL adapter can run PPO end-to-end on the headless env;
and the eval harness reports PID-floor and MPC-ceiling numbers on the fixed
scenario set.

---

## 21. Explicitly NOT in phase 1

- ROS 2 install or integration (only the message DTOs + bus interface).
- Unity ML-Agents (unless it later replaces the socket adapter, behind an
  adapter).
- SPH / particle-fluid plugin (Obi, Zibra), CFD.
- Shallow-water and multi-mode slosh models (phase 2 — interface only in
  phase 1).
- Recurrent / system-ID policy (phase 2).
- Full vehicle / tire model, WheelColliders.
- Second slosh mode, pitch-roll coupling, swirl slosh, baffles.
- Look-ahead / preview sensors.
- Photoreal rendering.
- Hardware-in-the-loop, real servo/IMU calibration, physical rig (phase 3).
- CI/CD, Docker/k8s, cloud training infrastructure.
