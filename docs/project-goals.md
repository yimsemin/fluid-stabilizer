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
| ③ | **Physical AI trained (partly) on the owner's own driving** | 🟡 residual-RL + BC-warm-start pipeline built and runs end-to-end (draft steps B–C); not yet a trained-and-winning policy |
| ④ | Offline hindsight ceiling — best achievable spill with full future knowledge | ✅ `HindsightOptimizer` (decision gate, finding F5) |

The goal is a defensible conclusion about **how ③ compares to ①, ②, and the ④
ceiling** in the regime that matters (realistic driving on a real road).

**Reframed 2026-08-29 (owner):** the goal is *not* "prove ③ wins" — it is to
build and understand the whole pipeline and report what we find honestly.
Findings F1–F5 show ① (rigid) is a very strong bar: even the ④ hindsight ceiling,
with full future knowledge, does not beat a locked tank on the owner's recorded
drives at the current fill/road. So the honest draft result may well be "① wins
here", with "the regime where ③ would win" as the documented follow-up. That is a
valid learning outcome. The plan (experiment-plan.md → "The draft") builds the
whole thing first, then adjusts the premise.

## Final-product architecture (owner decision 2026-08-29, revised 2026-08-29)

**Revision:** the earlier version had the Unity app run a *baked ONNX policy with
no Python at runtime*. The owner's call is that this collapses "the AI" into "the
world" and hurts the learning goal. A real robot keeps its brain, its body, and
its training rig as **separate things that talk over a link**. So the sim does
too. Building it this way *is* the sim-to-real preparation.

```
┌──────────────────────────┐   ┌───────────────────────┐   ┌────────────────────┐
│  THE WORLD + BODY         │   │  THE BRAIN            │   │  THE TRAINING RIG   │
│  Unity app (compiled)     │   │  policy inference     │   │  Python, offline    │
│                           │   │  process (Python)     │   │                     │
│  • vehicle + road + tank  │◄─►│  • loads a trained    │   │  • PPO / residual RL │
│    + slosh + actuator     │TCP│    checkpoint         │◄─►│  • BC warm-start     │
│  • sensors OUT, commands  │(=  │  • obs in → action    │TCP│  • experiment mgmt   │
│    IN                     │ROS│    out, deterministic  │   │  • ideally a small   │
│  • ① rigid = a mechanical │ 2) │  • this is "onboard   │   │    GUI               │
│    config, lives here     │   │    compute"           │   │                     │
│  • ② rule-based = engine-  │   └───────────────────────┘   └────────────────────┘
│    free C# IController,    │        ▲ the bridge is a first-class visual thing:
│    portable (in-process    │        │ you SEE obs leave, actions return,
│    for now)                │        │ reward accumulate — the pipeline is legible
│  • control panel for all   │
│    settings                │   ONNX (or a plain weights file) is the INTERCHANGE
└──────────────────────────┘   FORMAT that hands a trained policy to the brain —
                               it is not compiled into the Unity binary.
```

Design intent:

- **Unity = the plant.** The compiled app is the world and the robot body:
  physics, road, tank, slosh, actuator, sensors, the human-drive mode, the
  evaluation harness, and a control panel. It publishes sensor messages and
  consumes command messages. ① rigid is a *mechanical* configuration and lives
  here. It does **not** contain the learned policy.
- **The policy runs as its own process** (`serve_policy.py` for now), connected
  over the bridge — the stand-in for the onboard-compute ↔ sensors/actuators link
  a real robot has. For the comparison, ② (rule-based C# `IController`, zero Unity
  refs) runs in-process for simplicity but is portable to the same external shape;
  ③ (learned) is always the external inference process.
- **The trainer stays separate and offline.** It talks to a headless Unity
  instance over the bridge. Never runs alongside the finished demo.
- **ONNX is an interchange format**, not a runtime target inside Unity: the thing
  you hand from "training rig" to "brain". A hand-rolled weights file is an
  acceptable draft substitute.
- **All configuration lives in a Unity control panel** — scenario, fill, road
  severity, controller, driver, seed, run/record/evaluate, live metrics. The
  `-executeMethod` / `-rl*` flags and `experiment.txt` stay as the scripting back
  door.
- **The bridge is made visible** — watching data flow between the world and the
  brain is one of the best ways to understand a Physical AI system.

Trade-off accepted: the finished demo is "run the Unity app **and** the policy
process", not a single self-contained `.exe`. For a learning / portfolio artefact
that is a feature — it shows the architecture — not a regression.

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

> **Updated 2026-08-29 (step-back + draft-first).** The first RL run's lesson is
> the F1/F4 cliff; the feasibility ceiling (④) does not beat rigid (finding F5).
> The owner's call: build the whole pipeline as a **draft (vertical slice)**
> first, then adjust the premise. Live plan: **[experiment-plan.md](experiment-plan.md)**.

1. **Road** — ✅ 2026-08-29.
2. **Human trace record/replay** — ✅ 2026-08-29 (`DriverTrace`).
3. **Rebuild evaluation around recorded drives** — ✅ 2026-08-29 (`TraceEvaluator`,
   `ComparisonRunner`).
4. **Feasibility ceiling** — ✅ 2026-08-29 (`HindsightOptimizer`). **Gate result:
   does not beat rigid (F5)** → premise adjustment deferred until after the draft.
5. **The draft (vertical slice)** — experiment profile ✅ · residual-RL wiring ✅ ·
   BC warm-start ✅ · policy-serving process (this section's architecture) ⬜ ·
   3-way comparison in the app ⬜ · standalone build ⬜.
6. **Then:** premise adjustment (F5), a real RL run, domain randomization,
   per-episode logging, the control panel and trainer GUI — as refinement passes
   over a working whole.
