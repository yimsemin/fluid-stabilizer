# Project goals — what this project is for

> Companion to [project-brief.md](project-brief.md). The brief is the original
> vision; this file is the **owner's clarified intent** after the plan review of
> 2026-08-29, and it is the reference for scope decisions from here on. Where this
> file and older docs disagree, this file wins.

## The real purpose

The point of this project is **to learn and understand the whole Physical AI
pipeline by building one end to end.** Buying a real robot is not practical right
now, so a simulation stands in for the robot. The finished artefact matters less
than having personally built, and understood, every layer: physics → sensors →
actuator → controller → learned policy → evaluation → (later) real hardware.

Everything below serves that purpose. If a choice makes the system more
impressive but less understandable, it is the wrong choice (see
[CLAUDE.md](../CLAUDE.md) — "The owner").

## The concrete task

An AI controls a 2-axis (pitch/roll) stabilizer under an **open liquid tank on a
human-driven truck**, to keep the liquid from spilling. The AI does not drive;
the driver is an external disturbance. The hard part is the **temporal dynamics
of sloshing** under **partial observation** with a **non-ideal actuator** — the
AI must infer the hidden liquid state and act ahead of the wave.

## The headline result we are after

A clean **three-way comparison**, same scenarios, same metrics:

| # | Setup | Status |
|---|---|---|
| ① | Tank bolted rigidly to the truck (no stabilizer) | ✅ `HoldController` = a mechanically locked joint (decision D5) |
| ② | Tank tilted by a fixed rule (specific-force alignment / PID) | ✅ `SpecificForceAlignmentController`, `PidController` |
| ③ | **Physical AI trained (partly) on the owner's own driving** | ⬜ RL not yet run; training-on-recorded-traces not yet built |

The goal is to reach a defensible conclusion that **③ beats ① and ②** in the
regime that matters (realistic driving on a real road). Findings F1–F4 show ①
and ② are already a strong bar — beating a rigid tank needs *anticipation*, which
is exactly what ③ is for. That is a feature: it makes the comparison meaningful.

If it turns out ③ cannot beat ① even with a real road and a good policy, that is
still a valid, publishable learning outcome — but the plan is tuned to keep the
task inside the bracket where a learned policy *can* win (design-review §13–§14).

## Final-product architecture (owner decision, 2026-08-29)

Three parts, cleanly separated — this mirrors the future ROS 2 split, so building
it this way now *is* the sim-to-real preparation:

```
┌─────────────────────────────┐        ┌──────────────────────────────┐
│  "REAL WORLD" — Unity app    │        │  LEARNING — Python (separate)  │
│  (a compiled, self-contained │        │                                │
│   program, not Editor-play)  │        │  • RL training (PPO, …)        │
│                              │        │  • experiment / curriculum mgmt │
│  • vehicle + road + tank +   │◄──────►│  • ideally a small GUI so many  │
│    slosh + actuator + sensors│  TCP   │    driving runs and env-var     │
│  • runs the controller OR a  │ bridge │    sweeps are easy to try       │
│    baked policy (ONNX) for   │ (the   │                                │
│    inference — no Python at   │ "ROS 2 │  Runtime dependency on Python:  │
│    run time                  │  role")│  NONE. Training only.           │
│  • ALL settings controlled   │        │                                │
│    from inside Unity (a      │        │                                │
│    control panel), not from  │        │                                │
│    terminal flags            │        │                                │
└─────────────────────────────┘        └──────────────────────────────┘
                    ▲
                    │  the bridge itself is a first-class, visual thing:
                    │  you can SEE observations leave, actions come back,
                    │  reward accumulate — so the pipeline is legible.
```

Design intent:

- **The Unity side is a finished program.** A menu build produces a standalone
  app (macOS now, Windows later) that runs the simulation, the human-drive mode,
  the baseline controllers, the evaluation harness, and inference from a trained
  policy file. No Editor, no terminal.
- **All configuration lives in a Unity control panel** — scenario, fill level,
  road severity, which controller, driver profile, seed, run/record/evaluate
  buttons, live metrics. The `-executeMethod` / `-rlPort …` command-line flags
  stay only as a scripting back door.
- **The Python side stays separate and is training-only.** It never runs when the
  finished Unity app runs. It talks to Unity over the existing TCP bridge (the
  stand-in for a ROS 2 link). A lightweight GUI (or at least a config-file +
  launcher + TensorBoard workflow) lets the owner drive many times, record those
  runs, and sweep environment variables without hand-editing scripts.
- **The bridge is made visible**, because watching data flow between "the world"
  and "the brain" is one of the best ways to understand how a Physical AI system
  actually works.

## Realism constraints (in, from day one)

Already in: fixed-step determinism, latency, sensor noise/bias, actuator
torque/rate/travel/backlash/thermal limits, two slosh models with bracket
discipline, friction-circle vehicle. **Added 2026-08-29:** the road — rolling
grade, curve bank, surface roughness, discrete bumps/potholes, plus a suspension
heave DOF (see [road-model.md](road-model.md)). A rigid tank transmits every bump
to the liquid, which is precisely the disturbance ③ has to beat.

Still deferred (unchanged): ROS 2, ML-Agents, particle/SPH fluid, full tyre
model, look-ahead sensors, photoreal rendering, hardware-in-the-loop.

## Roadmap delta from this review

1. **Finish and run RL once** (phase-1 item 12) — answer the feasibility question.
2. **Human trace record/replay** (phase-1 item 3) — so ③ is trained on *real*
   driving, not only synthetic drivers.
3. **Road** — ✅ landed 2026-08-29.
4. **Domain randomization + per-episode logging** (item 13).
5. **Phase 1.5 — the product shell:** in-Unity control panel; standalone build
   script; ONNX inference path in Unity; lightweight Python trainer GUI. Starts
   after a first RL result exists — not before (don't polish the shell around an
   unproven core).
