# Frames, units, and timing conventions

Status: **locked (v1).** Agreed 2026-08-27 as part of the design-review approval.
Change only by an explicit decision recorded here with a date.

## Units — SI everywhere

| Quantity | Unit |
|---|---|
| Length | metre (m) |
| Mass | kilogram (kg) |
| Time | second (s) |
| Angle | radian (rad) internally; degrees only at UI / inspector boundaries |
| Force | newton (N) |
| Torque | newton-metre (N·m) |
| Linear acceleration | m/s² |
| Angular rate | rad/s |

The simulation core and every message use **right-handed, SI, radians**. Unity's
transforms (left-handed, Y-up, degrees) are converted **only** at the
`FluidStabilizer.Sim` boundary — never leaked into `Contracts` or `Controller`.

## Coordinate frames — right-handed

| Frame | Origin | Axes | Notes |
|---|---|---|---|
| **World (W)** | Fixed point on the ground at episode start | X = east, Y = north, Z = up (ENU) | Inertial. Gravity `g_W = (0, 0, −9.80665)` m/s². |
| **Vehicle (V)** | Rear-axle centre, at ground level | X = forward, Y = left, Z = up | Rigidly attached to the sprung mass; moves with body heave/pitch/roll. |
| **Tank (T)** | Gimbal pivot point (placed at the tank's nominal CoM) | X = forward, Y = left, Z = up when the gimbal is at zero | Rotates relative to V by the two stabilizer joint angles. |
| **Sensor frames (S\*)** | Each sensor's mounting point | Nominal = parent body frame; each carries a small fixed **mounting misalignment** (rotation + offset) sampled per episode | IMU frame ≈ body frame ∘ mounting error. |

### Gimbal kinematics

- Two revolute joints in series: **pitch about V's Y axis**, then **roll about
  the once-pitched X axis**. Rotation order is fixed: `R_VT = Rx(roll) · Ry(pitch)`.
- Travel limits: `|pitch| ≤ 30°`, `|roll| ≤ 30°` (well clear of kinematic
  degeneracy; exact values are randomised within a band ≤ 30° — see
  design-review §13).
- Positive pitch raises the tank's +X (forward) edge; positive roll raises the
  tank's +Y (left) edge. (Right-hand rule about the respective axis.)

## Timing / update rates — locked defaults

| Loop | Rate | Period | Notes |
|---|---|---|---|
| Physics + slosh integration | **600 Hz** | 1.667 ms | Fixed `dt`. Sub-stepping allowed inside a step if a model needs it. |
| Sensor sampling | **200 Hz** | 5 ms | Physics-step decimation ×3. Every sample carries its sim-time stamp. |
| Controller / policy | **100 Hz** | 10 ms | Physics-step decimation ×6. Zero-order hold on the action between ticks. |
| Command transport delay | **10–50 ms** | — | Modelled + randomised per episode (design-review §9, §14). |
| Episode length | **20–40 s** sim time | — | Long enough to contain post-maneuver slosh ring-down. |
| Rendering (human-drive mode only) | display rate | — | Never in the physics path. |

Rates are chosen so the decimations are integers and a control tick always
coincides with a sensor tick: physics:sensor:control = 6:2:1. 600 Hz was picked
over 500 Hz precisely so 200 Hz sensors divide evenly.

The managed simulation loop owns timing; it does **not** use Unity's
`FixedUpdate`/`Time` for anything physical (decision D1 in `docs/decisions.md` —
the mechanical sim runs in managed code, not PhysX).

## Time and determinism

- One monotonic simulation clock, `IClock.SimTimeSeconds` (`double`), zero at
  episode start.
- Every sensor/command message carries `StampSeconds` (`double`) = the sim time
  it represents.
- The controller consumes messages **by timestamp**, never by frame index.
- Wall-clock time is used only by the (future) real-time / ROS 2 adapter.
- All stochastic elements draw from a single seeded `IRandomSource` tree
  (one root seed per episode → named sub-streams). No `UnityEngine.Random`, no
  `System.Random` without an explicit seed, in the sim path.
- The slosh ODE is integrated in managed code with a fixed step — **not** by
  PhysX — so episodes are bit-reproducible given a seed.

## Message DTOs (v1 set)

Plain serializable C# structs in `FluidStabilizer.Contracts`, no `UnityEngine`
types, right-handed SI, each with a `double StampSeconds` field. Field layout
mirrors the matching ROS 2 message so a future adapter is a straight copy.

| DTO | Mirrors | Carries |
|---|---|---|
| `ImuSample` | `sensor_msgs/Imu` | linear acceleration (specific force, m/s²), angular velocity (rad/s), in the sensor frame |
| `JointStateSample` | `sensor_msgs/JointState` | pitch & roll angle (rad), angular rate (rad/s) |
| `MotorStateSample` | — | per-axis estimated motor current (A) and estimated output torque (N·m) |
| `LoadCellSample` | — | 3–4 scalar vertical forces (N) at fixed mount points |
| `LevelSample` *(optional)* | — | 1–3 point liquid-surface heights (m) at fixed probe locations |
| `VehicleCanSample` *(optional, behind a flag)* | — | speed (m/s), steer angle (rad), throttle [0,1], brake [0,1] |
| `StabilizerCommand` | — | per-axis commanded joint **angular rate** (rad/s); plus a sequence number and the stamp it was computed from |

`SensorBundle` groups one synchronized set of samples for a control tick.
