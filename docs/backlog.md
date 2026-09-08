# Backlog

Things parked deliberately, with enough context to pick up cold. Performance history and the
reasoning behind the solver lives in `solver-perf-handoff.md`; this is only the open work.

## Apple Silicon: the solver falls back to the slow path, and nobody has run it there

**Status:** compiles and routes correctly, never executed on Arm hardware.

The shipping separation job (`SeparateSoaMaskedJob`, `SeparateVariant = 8`) is hand-written AVX2 +
FMA. On a CPU without them `CpuFeatureJob` reports false and `BoidSolver.Schedule` routes to
variant 5 before the job is scheduled, so an M-series Mac runs the older scalar path. Intel Macs
get the fast one.

What is verified: it builds for `ARMV8A_AARCH64` with zero `BC1200` errors, and the routing is
correct. What is NOT verified: how fast anything is on Apple Silicon. **The 43% win this project
records is an x86 result.** Even variant 5's numbers do not transfer - different CPU, clocks and
memory system.

The opportunity is a NEON path. Groundwork is done: the SoA position streams, the masked
accumulation and the fused contact math are all in place and the algorithm is width-agnostic, so it
is the same job with 4-wide `float32x4_t` in place of 8-wide `ymm`. Burst exposes
`Unity.Burst.Intrinsics.Arm.Neon`.

Do NOT write it blind. Every SIMD prediction in this project was wrong until measured - the 4-wide
attempt lost, an estimate of the fused design was 2x optimistic, and the AVX2 guard took four
attempts. Get an M-series machine, run `BoidSwarm.SweepConfigs` to establish where variant 5
actually lands there, and only then decide whether NEON is worth it.

Read the "Portability" notes on `SeparateSoaMaskedJob` before touching the guards. Burst's
CPU-feature analysis is per basic block: the test must be a single property in a positive `if`
wrapping the intrinsics in the same block. A compound condition, an early return, a helper method
or `[MethodImpl(AggressiveInlining)]` all fail to build for Arm.

## GameObjectBaseline.unity has no enemies in it any more

**Status:** deliberate, but it leaves the scene without content.

The swarm system moved to `Assets/SwarmSolver/` with its own assembly, and the `Enemies` object was
removed from `GameObjectBaseline.unity`. That scene now holds only a camera, a light and the
player, so if it existed to show the naive GameObject-per-enemy approach against the DOTS one, that
comparison is currently gone. The code is intact and parked, not deleted, so it can be put back.

## Dead files in the workshop assembly

`SwarmSolver`, `NeighbourGrid`, `SwarmJobs` and `ArenaSettings` were checked against every scene and
prefab in `Assets/` and only `ArenaSettings` is still referenced (by `PlayerController`, which the
boid benchmark scene uses). The other three are referenced by nothing. They moved to
`Assets/SwarmSolver/` with the rest of the swarm rather than being deleted, which was the cautious
call, not necessarily the right one.

## TurnSpeed was bit-identical to the old EnemyRadius

`BoidSettings.TurnSpeed` read 0.0474339984357357 - the same value as `EnemyRadius` was, to all
sixteen digits. A turn rate has no reason to equal a radius, so it is almost certainly a stray
paste rather than a tuned value. Harmless, but worth setting deliberately.
