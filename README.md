# DOTS 101 — Entities, Jobs, and Why They Are Fast

> **Status: work in progress.** The slide deck and the ECS sample are in the repo. The
> per-lab starting points and checkpoints are still being built. Expect things to move.

A hands-on Unity workshop on the Data-Oriented Tech Stack. We build the same top-down
arena three times — plain MonoBehaviours, then jobs plus Burst, then full ECS — and
profile each one, so every DOTS feature arrives attached to the problem it solves.

Runs about three hours.

## Requirements

- Unity **6000.3.22f1** (Unity 6 LTS)
- Comfort with C#. No DOTS experience needed.
- A machine with more than one core, which is all of them.

Packages are already in `Packages/manifest.json`; opening the project pulls them:

| Package | Version |
| --- | --- |
| `com.unity.entities` | 1.4.8 |
| `com.unity.entities.graphics` | 1.4.21 |
| `com.unity.render-pipelines.universal` | 17.3.0 |

## The two problems

Everything in the workshop answers one of these:

1. **One thread.** The main thread does all the work while the other cores sit idle.
   → the Job System and Burst.
2. **Scattered data.** Every enemy is its own object somewhere on the heap, so each one
   costs a trip to RAM. → ECS layout.

## Blocks

| Block | Topic | What you build |
| --- | --- | --- |
| 1 | The problem | Profile 5,000 enemies on a single thread. Find where the time goes. |
| 2 | Jobs and Burst | Move the swarm on the worker threads. Toggle `[BurstCompile]` and read the number. |
| 3 | Entities | Rebuild the arena as entities, components, and systems. Baking and sub scenes. |

## Layout

```
Assets/
  Scenes/
    Workshop.unity            main scene
    Workshop_SubScene.unity   baked entity content
  Workshop/
    Authoring/                MonoBehaviour authoring + component definitions
    Systems/                  ISystem implementations (spawn, chase, move, fire, collide)
    Editor/                   scene builder tooling
    Art/                      player, enemy, bullet prefabs and materials
presentation/
  deck.html                   the slide deck, open it in a browser
```

## Not done yet

- `Assets/Workshop/Baseline/` — the MonoBehaviour version and the Lab 1 job stub the
  deck points at.
- Checkpoint files per lab, for anyone who falls behind.
- Block 3 slides.
- Links for continued learning.
- A LICENSE file.
