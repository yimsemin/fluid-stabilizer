# Project brief — fluid stabilizer

> This is the owner's original vision, captured so future sessions share the same
> starting point. It is **not** a locked specification. See `design-review.md`
> for where the team recommends deviating.

## Goal

Simulate an AI that actively stabilizes an **open (lidless) liquid tank** mounted
on a **moving truck** that a **human drives**.

- The truck carries a lidless tank filled to some fraction with liquid.
- When the truck accelerates, brakes hard, corners, climbs/descends hills, or
  hits bumps, the truck's real motion plus gravity cause liquid **sloshing**.
  Under harsh enough conditions liquid must be able to **spill over the rim**.
- The Physical AI does **not** drive the truck. It controls a **2-axis
  stabilizer** (pitch + roll) between truck and tank, actively commanding the
  tank's attitude to minimize spillage.
- The driver acts independently of the tank state and the AI. The driver's
  unpredictable input is part of the disturbance the AI must handle.

## Physical realism requirements

The stabilizer is **not** an ideal device. The same command can produce
different tank motion depending on physical state. Candidate variables the owner
listed (not a locked list — the review decides what actually matters):

- Tank mass; amount of liquid; instantaneous CoM shift as liquid moves
- Actuator response change under load and inertia
- Max torque, max angular rate, max travel angle
- Gear ratio, gear backlash, friction / mechanical efficiency
- Control latency; optionally power / actuator performance variation

Example: the AI commands `Roll +10°`; the time to reach the target and the angle
actually reachable can differ with current liquid position and load.

## Sensor principle

The AI may only observe values obtainable from a **real sensor**. Candidates:

- Vehicle IMU **and** Tank IMU (kept separate — e.g. on a hill, both attitude
  vs. gravity and vehicle motion must come from realistic sensing)
- Load cell(s)
- Stabilizer encoder
- Motor current / torque estimate
- Optionally a limited liquid-level sensor

Unity ground truth may be used for **debugging and evaluation only**. The AI
observation must **not** include information that a real sensor could not give,
e.g.: every liquid particle position, the exact instantaneous liquid CoM, the
truck's future motion, the driver's future input, upcoming road, or physics
engine internal state.

## What makes it a real Physical AI problem

Not just learning a rule like "truck accelerates forward → tilt tank back." The
key is the **temporal dynamics of sloshing**: liquid keeps moving after braking
or a curve ends; the stabilizer's current action affects later liquid motion and
the next control state; the moving liquid itself changes the tank CoM and
actuator load. The AI must infer the system's dynamic state from limited
observation and perform continuous control — not merely react to the current
instant's acceleration.

## Future: ROS 2 / real hardware

Long term: connect to ROS 2 and, if possible, a small hardware experiment. **Not
installing ROS 2 now** and not making it a Unity dependency. But keep these
boundaries clear from the start:

```
Simulation Physics
   ↕
Sensors / Actuators
   ↕
Controller / Physical AI
   ↕
(future) ROS 2 Adapter
```

The AI must not be tightly coupled to Unity GameObjects / physics components.
Sensors and actuators should be replaceable: Unity sim → ROS 2 topic → real
sensor / motor. Define early: World / Vehicle / Tank coordinate frames;
timestamps; physics update rate; sensor update rate; controller update rate.
Do **not** over-abstract for the future.

## Design principles

- Physical **causal consistency** over visual realism.
- Do not pick an expensive method just because it is more precise.
- RL must be able to collect enough simulation steps.
- Keep AI observation and simulation ground truth clearly separated.
- Keep the Sensor → Controller → Actuator boundary.
- Consider interfaces replaceable by real hardware.
- Do not tightly couple the controller to Unity-specific implementation.
- Do not adopt the owner's named technologies uncritically — propose better fits.
- Keep the first implementation simple, but do not build something that must be
  thrown away wholesale later; also do not over-abstract for the future.

## Not in this stage

No full feature development yet. Do not reflexively install ROS 2, Unity
ML-Agents, a fluid-simulation plugin, or other large dependencies. No
"install it now because we'll probably need it later."
