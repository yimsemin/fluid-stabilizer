# Frames, units, and timing conventions

> **Fill this in and agree on it before writing any physics code.** Retrofitting
> consistent frames/units/timestamps later is a painful rewrite (design-review
> §18.4). Values below are *proposed defaults* — confirm or change, then treat
> as fixed.

## Units — SI everywhere

| Quantity | Unit |
|---|---|
| Length | metre (m) |
| Mass | kilogram (kg) |
| Time | second (s) |
| Angle | radian (rad) internally; degrees only at UI boundaries |
| Force | newton (N) |
| Torque | newton-metre (N·m) |
| Linear accel | m/s² |
| Angular rate | rad/s |

Note: Unity's default is metres and its physics is SI, but its transforms use
**left-handed Y-up degrees**. Convert at the Unity boundary only; keep the
simulation core and all messages right-handed SI radians.

## Coordinate frames — right-handed

| Frame | Origin | Axes | Notes |
|---|---|---|---|
| **World (W)** | Fixed start point on the ground | X = east, Y = north, Z = up (ENU) | Inertial. Gravity = (0, 0, −9.80665). |
| **Vehicle (V)** | Rear axle centre, at ground level | X = forward, Y = left, Z = up | Attached to the sprung mass (moves with body roll/pitch/heave). |
| **Tank (T)** | Gimbal pivot point (near tank CoM) | X = forward, Y = left, Z = up when level | Rotates relative to V by the two stabilizer joint angles. |
| **Sensor frames** | Each sensor's mounting point | Per-sensor, with a randomized small misalignment vs. its parent body frame | IMU frames ≈ body frame + mounting error. |

Rotation order for the 2-axis gimbal: **pitch (about V's Y) then roll (about the
pitched X)**. Confirm this matches the intended mechanical stack (which axis is
mounted on which). Keep `|pitch|, |roll| ≤ ~30°` to stay away from kinematic
degeneracy.

## Timing / update rates — proposed

| Loop | Rate | Notes |
|---|---|---|
| Physics + slosh integration | 200–500 Hz (fixed `dt`) | `dt` fixed for determinism. Sub-stepping allowed inside. |
| Sensor sampling | 100–200 Hz | Per-sensor; may differ. Each sample carries a timestamp. |
| Controller / policy | 50–100 Hz | Zero-order hold on the action between ticks. |
| Command transport delay | 10–50 ms | Modeled + randomized (design-review §9, §14). |
| Rendering (human mode only) | display rate | Never in the physics path. |

## Timestamps

- One monotonic simulation clock (`IClock`), seconds since episode start,
  `double`.
- Every sensor message carries the sim time at which it was sampled.
- The controller consumes messages by timestamp, never by frame index.
- Wall-clock time is only used by the (future) real-time / ROS 2 adapter.

## Message DTOs (draft — see design-review §15)

`VehicleImu`, `TankImu` (↔ `sensor_msgs/Imu`), `JointState`
(↔ `sensor_msgs/JointState`), `LoadCells`, `MotorState`, `LevelSensors`,
`StabilizerCommand`. Plain serializable structs, no `UnityEngine` types,
right-handed SI, each with a `stamp` field.
