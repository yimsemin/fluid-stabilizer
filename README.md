# fluid-stabilizer

Physical AI simulation project: an AI-controlled 2-axis stabilizer that keeps an
open liquid tank on a **human-driven** truck from spilling.

- The **human** drives the truck (accelerate, brake, corner, hills, bumps).
- The **AI** does *not* drive. It controls a 2-axis (pitch/roll) stabilizer
  between the truck bed and the tank, using only signals a real sensor could
  produce, to minimize liquid spillage.
- The driver's unpredictable input is treated as an external disturbance the AI
  must reject.

The hard part is not "lean the tank against acceleration." It is the **time
dynamics of sloshing**: liquid keeps moving after a maneuver ends, the moving
liquid changes the tank's center of mass and the actuator load, and the
stabilizer's current action shapes the liquid motion and control state that
follow.

## Repository layout

```
fluid-stabilizer/
├── Assets/            Unity project assets  (created after Unity license is activated)
├── Packages/          Unity package manifest
├── ProjectSettings/   Unity project settings
├── docs/              Design docs — read these first
│   ├── project-brief.md              The goal, in full
│   ├── design-review.md              Critical review of the idea + recommendations
│   ├── frames-units-conventions.md   Coordinate frames, units, timing (fill in before physics)
│   └── setup-status.md               Current environment + what is done + next steps
├── .gitignore         Unity + Python + ROS ignores
└── .gitattributes     Line-ending normalization + Git LFS rules
```

## Status

Environment checked, Git repository configured. **The Unity project has not been
created yet — the Unity Editor license needs to be activated first.** See
[docs/setup-status.md](docs/setup-status.md).

No feature code has been written. The design review in
[docs/design-review.md](docs/design-review.md) is awaiting the owner's decision
before implementation starts.
