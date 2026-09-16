# DOTS 101 — Entities, Jobs, and Why They Are Fast

A hands-on Unity workshop on the Data-Oriented Tech Stack. One top-down arena with 20,000
enemies chasing you, built twice: first as GameObjects with a job, then as entities. The
Profiler is open the whole time, so every DOTS feature arrives attached to the problem it
solves.

You type all of it. Nothing is handed over except the branch you start from.

## The deck

`presentation/deck.html` — open it in a browser, arrow keys to move. 96 slides. Every card
tells you the file, the lines to type, and what you should see afterwards.

## Requirements

- Unity **6000.3.22f1** (Unity 6 LTS)
- Comfort with C#. No DOTS experience needed.
- A machine with more than one core.

Packages come with the project; opening it pulls them.

| Package | Version |
| --- | --- |
| `com.unity.entities` | 1.4.8 |
| `com.unity.entities.graphics` | 1.4.21 |
| `com.unity.render-pipelines.universal` | 17.3.0 |
| `com.unity.inputsystem` | 1.20.0 |

## Checkpoints

Each branch is a starting line, not a finish line: it holds the room's own work up to that
point, in canonical form. If you fall behind, or you want to pick the day back up later,
take the branch for the stretch you want and carry on from there.

```
git stash
git checkout checkpoint-3
```

| Branch | What it holds |
| --- | --- |
| `checkpoint-0` | The GameObject game. 20,000 enemies, one `foreach` on the main thread, 5.10 ms. |
| `checkpoint-1` | That loop as a Bursted `IJob`, with the two copy loops it costs. |
| `checkpoint-2` | `IJobParallelForTransform` — the Transforms go into the job, the copies go. |
| `checkpoint-3` | The first entity: a subscene, a Baker, `EnemyTag` and `MoveSpeed`. |
| `checkpoint-4` | `MoveSystem` — a system moving everything its query finds. |
| `checkpoint-5` | The crowd. A Spawner, `InitialSpawnSystem`, `MoveJob` as `IJobEntity`, and the bridge that hands the job the player's position. |
| `checkpoint-6` | Reaching you is spent: destroy through an `EntityCommandBuffer`, then `Alive` as an enableable component, and the health bar moving again. |

`develop` is the working branch and is not a checkpoint.

## Layout

```
Assets/Workshop/
  Scenes/
    GameObjectBaseline.unity              the scene, all day
    GameObjectBaseline/
      Workshop_SubScene.unity             the baked entity content
  Baseline/                               the GameObject game: spawner, mover, player, health, arena
  Authoring/                              MonoBehaviour authoring + the components their Bakers write
  Systems/                                ISystem implementations
  Bridge/                                 PlayerBridge — GameObject data crossing into ECS
  Art/                                    prefabs, materials, the enemy shader graph
presentation/
  deck.html                               the slide deck
  images/                                 the Profiler and Editor captures it shows
```

## What this is not

A game. There is no score, no waves, and once an enemy reaches you it is spent — walk in
circles long enough and the crowd runs out. It is a benchmark with a player in it, which is
what makes the Profiler numbers mean something.

## Where to go next

Dynamic buffers, shared components, blob assets, component lookups, Unity Physics and
Netcode for Entities — named on the last slide, covered in the follow-up workshop.
