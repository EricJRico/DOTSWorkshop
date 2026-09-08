# Boid solver performance — handoff

State as of commit `c2549ac`. 50,000 agents, i7-12700H, `JobWorkerCount = 4` (so 5 executing
threads), Unity 6000.3.22f1, editor play mode.

## Where it stands

| | wall |
|---|---|
| start of session | 7.9 ms |
| now, with the overlap check on | 5.4 ms |
| now, check off — **the real solver cost** | **4.0 ms** |

The overlap check is verification, not simulation. Nothing in the sim reads it. Quote 4.0 ms as
the solver and 5.4 ms as the cost of proving it correct.

Shipping config: `SeparateVariant = 3` (coloured Gauss-Seidel), `Iterations = 6`, `Omega = 1.8`,
`MinDivisor = 1`, `MaxNeighbours = 16`, `ColourBatch = 64`, `BatchSize = 128`.

## Frame breakdown

Separation is ~2.9 ms of the 4.0 ms. The rest, measured directly by `BoidSolver.TimeStages`:

| | ms |
|---|---|
| overlap check (verification) | 1.58 |
| grid build (bounds, sizing, hash, scan, counting sort) | 0.33 |
| colour bucketing | 0.19 |
| finalize | 0.11 |
| steer | 0.06 |

The grid build being 0.33 ms kills the idea that `HashJob`'s 50k atomics or `ScanJob`'s
single-threaded prefix are worth attacking. All of it together is a tenth of one separation pass.

## Inside SeparateColouredJob

`BoidColouredAblation.cs` stages C1..C6, timed by `BoidSolver.TimeColoured`. One pass = all 50,000
agents across all 4 colours. Six passes per frame. Three samples:

| stage | ms |
|---|---|
| C1 floor: 4 dispatches, cell+agent loops, read/write | 0.071 0.072 0.079 |
| C2 + row-run bounds | 0.121 0.134 0.160 |
| C3 + walk candidates | 0.341 0.336 0.325 |
| C4 + load neighbour position | 0.324 0.311 0.301 |
| C5 + lengthsq | 0.313 0.342 0.322 |
| C6 + compare and compaction store | 0.390 0.388 0.354 |
| full job | 0.522 0.539 0.573 |

Ranked cost per pass:

1. **walking the candidate list — 0.19 ms**
2. contact math — 0.17 ms
3. grid lookup — 0.06 ms
4. compare + compaction store — 0.05 ms

**Loading the neighbour position and computing the distance are free.** Both deltas are zero or
negative in every sample — they hide under the loop that is already running. The candidate loop is
bound by its own control flow, not by memory traffic or arithmetic. That is why SIMD failed.

**Candidates per agent: 11.6, measured** (`ColouredAblate3` fills a per-agent counter). Matches the
geometry: a 3×3 scan at cell = collision diameter sweeps 9D² to find contacts inside 0.785D².

C3 − C2 works out to ~6.5 cycles per candidate for an increment, a compare, a branch and an add.
That should be one or two. **Nobody has explained this. It is the single biggest line in the
table and the most likely place a real win is hiding.**

Note the C1..C6 numbers are measured with a `Complete()` per pass, so they run higher than the
in-frame cost. The ranking between them holds; the absolute values do not.

## What was tried and rejected, with the reason

- **Branchless reject in place** — worthless. `AblateStage7` writes the same reject as
  `math.select` and costs the same as the branchy `AblateStage6` to three decimals. The branch is
  free; the cost was conditional accumulation, which is why splitting the loop worked.
- **4-wide SIMD candidate walk** (`SeparateSimdJob`, variant 2) — 0.879 vs 0.640. The scalar loop
  was never latency-bound, so blocking four candidates together replaced a full pipeline with a
  barrier every four. Confirmed later by C4/C5 being free.
- **SoA split of `Predicted`** — skipped and should stay skipped. The x/y deinterleave was never
  the problem.
- **Substepping** — 8×1 beats 4×2 and 2×4 on both quality and cost. Substeps only add grid
  rebuilds. Macklin's small-steps result is about elasticity error, which a hard inequality
  constraint does not have.
- **Neighbour caching** (`GatherJob` + `SeparateCachedJob`) — 6.76 vs 6.96 ms, 3%, for 3.2 MB and
  an extra dispatch. Rebuilding more often only costs more. Note the old "8x the overlap" verdict
  was an artefact and is fixed: `GatherJob` was writing the gather count into `NeighbourCount`, so
  the congestion gate saw ~2.25x the neighbours on that path only.
- **Lower Omega under Gauss-Seidel** — the research said it was necessary; it is worse at every
  value tried. 1.4 → 9.72%, 1.0 → 11.35%, against 1.8.
- **`MinDivisor` floor on the averaging divisor** — worse at every setting. Kept as a knob only.
- **Batch size** — not a lever. `ColourBatch` 16/32/64 all land at 5.51–5.54 ms; larger is slightly
  worse. `BatchSize` (agents, everything except the coloured passes) is still unswept.
- **Distance LOD** — does not apply. The whole crowd is on screen at one camera distance.

## Open leads

1. **The ~6.5 cycles per candidate.** Read the Burst Inspector output for
   `SeparateColouredJob`'s inner loop and for `ColouredAblate3`. If the loop is not being unrolled
   or the trip count is preventing pipelining, that is a large win on the largest line.
2. **Cut the 11.6 candidates.** Halve the cell and scan 5×5: sweeps 6.25D² instead of 9D², ~31%
   fewer candidates, hitting the biggest line directly. Cost: a 5-cell reach needs same-colour
   cells 3 apart, so 9 colours instead of 4, tripling the dispatches. Rough arithmetic is −0.9 ms
   of candidates against +0.5 ms of barriers. Too close to call — build it parameterised
   (scan radius R, colour spacing R+1, R² colours) and measure.
3. **Sleeping.** In a jammed crowd most interior agents cannot move. Skip agents whose
   neighbourhood did not move; a skipped agent still acts as an obstacle so quality holds. Worth
   one number first: what fraction of agents move more than a hair per frame. Weak during spawn
   and flow, which is when the cost matters, so measure before building.
4. **Contact math, 0.17 ms.** Second largest line, never examined. ~6 keepers per agent with a
   `sqrt` each.
5. **`BatchSize` sweep** for the non-coloured jobs, mainly the 1.58 ms overlap check.

## How to measure — read this before touching anything

The harness is in the code, not in scratch scripts.

- **`BoidSwarm.SweepConfigs`** walks a list of configs in ONE play session and logs `SWEEP|` rows.
  Use it for anything comparative. It forces `AutoTarget` on and pins `Time.captureDeltaTime`.
- **`SeparateBenchmark`** logs `SEP|` (variant A/B on one crowd state), `ABL|` (old Jacobi
  stages), `COL|` (coloured stages, the useful one) and `STAGE|` (non-separation jobs).
- All debug switches live on **BoidSwarm** under the `Debug — *` headers. `BoidSettings` holds only
  what the jobs read.

Three traps that cost real time this session:

1. **Never compare runs from different play sessions.** A settled crowd and a flowing one give
   completely different penetration for the same solver. Everything comparative goes through the
   sweep.
2. **Pin `Time.captureDeltaTime`.** Otherwise a slower config gets a bigger step and scores worse
   for that reason alone.
3. **`unity command eval` edits the in-memory ScriptableObject.** The change survives exiting play
   mode, the .asset on disk is untouched, and `git status` stays clean. A partially-executed
   command left `Iterations = 0` and I bisected through good commits hunting a code bug that did
   not exist. If behaviour looks wrong, dump the live settings object FIRST.

Quality metric is **mean penetration as a fraction of the solve diameter**, not the pair count. The
old check counted against `2*Radius` while the solver targets `2*Radius*1.35` — 35% of slack, so
every config scored ~400 pairs and the metric could barely fail. Run-to-run drift is about one
percentage point; put the same config at both ends of a sweep as a bookend and ignore any
difference smaller than that.

## Context worth keeping

No shipping engine does what this does. Unreal Mass claims 35,000 agents but excludes medium, low
and off-LOD agents from avoidance entirely — out of the box at most ~100 get it. UE's older
`UCrowdManager` ships `bResolveCollisions = false` with `MaxAgents = 50`. Detour runs 4 Jacobi
passes with a 0.7 *under*-relaxation factor and caps neighbours at 6 out of the first 32 found.

Separating all 50,000 every frame is the unusual thing here. The big win in shipping games is not
a faster solver, it is not solving most agents at all.
