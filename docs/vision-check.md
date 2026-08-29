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
| B · residual RL wiring | 2026-08-29 | `RlServerLauncher.Serve` with `-rlBaseline hold` — server boots; the composed name reads "RL residual on Hold …" in the log | ⬜ | |
| C · BC warm start | 2026-08-29 | `FluidStabilizer ▸ Export BC Dataset`, then `python bc_pretrain.py`, then `train_ppo.py --init-actor runs/bc/actor.pt --total-steps 20000` — trains, no crash, checkpoint written | ⬜ | draft ran: return flat ≈ −2280, stable (not the −3200 from-scratch cliff). Real training is a later compute step. |
| D · policy as a separate process | 2026-08-29 | `python serve_policy.py --checkpoint runs/bc/actor.pt --port 5601`, then `FluidStabilizer ▸ Evaluate Learned Policy` (or play scene, key `4`) — the world connects to the brain over TCP, spill printed per drive | ⬜ | end-to-end ran. Honest result: ③ (BC policy) 24–30 L vs ① 19–22 L → **rigid wins here** (F5). Architecture revised: no baked ONNX. |
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
