# Vision check — "is this going where I want?"

Owner-maintained. The problem this solves: the project keeps going deep on one
layer (slosh fidelity, the feasibility ceiling) and it's hard to feel whether the
*whole thing* is on track. So: after each **draft (vertical slice)** step
(experiment-plan.md → "The draft"), Claude posts a short demo and adds a row
here. The owner marks it and writes one line.

How to read a demo: it should be something you can **run yourself** (a menu item,
the play scene, a build) or a screenshot/short clip — not just a log file.

| step | date | demo (what to run) | owner verdict | owner note |
|---|---|---|---|---|
| A · experiment profile | 2026-08-29 | edit `experiment.txt`, press Play — the scene should follow the file | ⬜ ok / ⬜ off | |
| B · residual RL wiring | | | ⬜ | |
| C · BC warm start | | | ⬜ | |
| D · ONNX in Unity | | | ⬜ | |
| E · 3-way comparison in app | | | ⬜ | |
| F · standalone build | | | ⬜ | |

## Open questions for the owner to answer whenever

- **The headline result.** If the honest draft shows "① rigid tank wins", is that
  an acceptable headline for the learning project, with "here's the regime where
  ③ would win" as the follow-up? (Current lean: yes — see experiment-plan.md
  "The draft".)
- **Fidelity vs. breadth.** After the draft, where does effort go first — a
  harder/again RL push, higher physics fidelity, or the product shell polish?
- **The app.** What does "done" look like for you — a thing you demo to someone,
  a thing you keep tinkering with, or a portfolio artifact?

## Where the knobs are

`experiment.txt` at the repo root — fill, road, driver, episode length, slosh
model, seed. One file, read by the play scene, RL training, evaluation and the
comparison. Everything else is code defaults in
`Assets/FluidStabilizer/Core/Sim/SimulationConfig.cs`.
