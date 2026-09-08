# Boid solver performance — handoff

State as of commit `c2549ac`. 50,000 agents, i7-12700H, `JobWorkerCount = 4` (so 5 executing
threads), Unity 6000.3.22f1, editor play mode.

## Where it stands

| | check on | check off |
|---|---|---|
| start of session | 7.9 ms | |
| variant 3, the job this session started with | 5.50 / 5.60 ms | 4.06 / 4.01 / 3.97 ms |
| variant 5, rsqrt in the contact math | 5.12 ms | 3.62 / 3.50 / 3.51 ms |
| **now, variant 8, 8-wide masked SoA** | **3.78 ms** | **2.25 ms** |

**3.97 -> 2.25 ms over the session, 43%, with the quality metric slightly BETTER at the end than
at the start.**

Reproduced in a second session, same shape: variant 3 bookends 3.94 / 4.03 check off, variant 5 at
**3.50**. So the win is 0.42 and 0.49 ms across two independent sessions - call it **~0.45 ms,
about 11%** - and the solver is **~3.5-3.6 ms**.

Both columns come from ONE session, every config entered twice - once with the overlap jobs on so
it can carry a quality number, once with them off so it is the cost that would actually ship. The
check-off bookends agree to 0.05 and 0.09 ms and land on the 4.0 ms this document has recorded
since the start, so the harness agrees with the old measurement.

**Quality is unchanged**: variant 5 reads 7.29% and 7.20% mean penetration in the two sessions,
against bookends of 6.73 / 7.46% and 6.77 / 7.53% - inside them both times. Two changes got the
win, a reciprocal square root in the contact math and the colour bucketing done in parallel; both
are below.

The overlap check is verification, not simulation. Nothing in the sim reads it, so the check-off
column is the number to quote as the solver and the check-on column is the cost of proving it
correct - about 1.45-1.50 ms either side of the change, measured as the gap between each config's
own two rows rather than across runs.

Put both in the SAME sweep. `SweepConfig.Overlap` exists so a config can appear twice in one
session; a check-off row logs `pen=n/a` rather than the stale counters the last checked frame left
behind. An earlier version of this document guessed the check-off number by subtracting 1.58 ms
measured in a different run - the real answer came out 0.2 ms away from that guess.

Config: `Iterations = 6`, `Omega = 1.8`, `MinDivisor = 1`, `MaxNeighbours = 16`,
`ColourBatch = 64`, `BatchSize = 128`. `SeparateVariant = 5`; 3 is the previous shipping job and
is kept as the bookend every sweep is measured against.

## Frame breakdown

**This table is variant 3, at 4.0 ms.** It has not been re-measured against variant 5 and two of
its rows have since moved: colour bucketing is now parallel, and the overlap check is measured at
1.45-1.50 ms as the gap between a config's own check-on and check-off rows. Re-run
`BoidSolver.TimeStages` before quoting any of it.

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
That verdict is worth revisiting at 3.62 ms, where the same 0.5 ms of non-separation work is 14%
of the frame rather than 11%.

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

C3 - C2 works out to ~6.5 cycles per candidate for an increment, a compare, a branch and an add.
That should be one or two. **It is not a per-candidate cost. See "Where the candidate walk
actually goes" below - most of it is charged once per ROW RUN, and dividing it by candidates is
what made it look impossible.**

Note the C1..C6 numbers are measured with a `Complete()` per pass, so they run higher than the
in-frame cost. The ranking between them holds; the absolute values do not.

## What was tried and rejected, with the reason

- **Branchless reject in place** — worthless. `AblateStage7` writes the same reject as
  `math.select` and costs the same as the branchy `AblateStage6` to three decimals. The branch is
  free; the cost was conditional accumulation, which is why splitting the loop worked.
- **4-wide SIMD candidate walk** (`SeparateSimdJob`, variant 2) — 0.879 vs 0.640. The scalar loop
  was never latency-bound, so blocking four candidates together replaced a full pipeline with a
  barrier every four. Confirmed later by C4/C5 being free.
- **SoA split of `Predicted`** — ~~skipped and should stay skipped, the x/y deinterleave was never
  the problem.~~ **THIS ENTRY IS WRONG AND IS NOW DISPROVEN.** LLVM will not widen a `<2 x float>`
  loop body into `<N x <2 x float>>`, so `float2` is a HARD PREREQUISITE BLOCKER on vectorising the
  candidate loop — see "What Burst actually says" below. The deinterleave is not a micro-optimisation
  here, it is the thing standing between this loop and any SIMD at all. Treat as open.
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
  worse. `BatchSize` is now a `SweepConfig` field too; see the sweep below for the result.
- **Distance LOD** — does not apply. The whole crowd is on screen at one camera distance.

## Variant 8: 8-wide masked accumulation over SoA - 3.66 -> 2.25 ms

The one that worked, and it only worked because BOTH blockers went at once. Burst's remarks (below)
say the scalar loop is stopped by the compaction index AND by `float2`, separately - probes that
removed only one changed nothing. Variant 8 removes both and hand-writes the 8-wide block rather
than trusting the auto-vectoriser, which at 3.70 candidates per run never reaches its own >= 8 guard.

The candidate list is gone. Phase 2 is fused into phase 1 and the contact math runs on all 8 lanes
under a mask, so ~24 lanes of masked work replaces ~11 candidates of scalar collection plus ~6
keepers of scalar contact math. That trade is only favourable because a masked lane costs under a
cycle while a scalar keeper measured ~16.

One session, interleaved so each row inherits the same crowd:

| config | wall, check off | mean pen | body overlaps |
|---|---|---|---|
| variant 5 (reference A) | 3.63 ms | 6.65% | 464 |
| **variant 8** | **2.25 ms** | 7.22% | **402** |
| variant 5 (reference B) | 3.69 ms | 7.28% | 558 |
| variant 3 | 3.97 ms | | |

The two references agree to **0.06 ms** against a 1.41 ms effect. **Quality improved**: body
overlaps 402 against 464 and 558, below both, with mean penetration between them. That is the
correctness check that matters - a job quietly missing neighbours would read WORSE, not better.
Better is what removing the `MaxNeighbours` cap should do, since more contacts get counted.

Confirmed in a second session, and the quality question settled:

| config | wall, check off | mean pen | body overlaps |
|---|---|---|---|
| **variant 8** | **2.25 / 2.26 ms** | 6.54% | **422** |
| variant 5 | 3.59 ms | 7.33% | **422** |

Body overlaps identical, penetration slightly better, and 2.25 / 2.26 / 2.25 across three
measurements in two sessions.

### Portability: variant 8 is AVX2, and guarding it correctly is fiddly

Apple Silicon is Arm and has no AVX2. Burst does not silently fall back - it validates instructions
against the target CPU and **emits a compiler error**, so an unguarded AVX2 job means the project
FAILS TO COMPILE on an M-series Mac, in the editor as much as in a player. Intel Macs are fine.

Four guard formulations were tried and **all four failed**, each caught only by actually compiling
the job for `ARMV8A_AARCH64` and counting `BC1200` errors:

| guard | result |
|---|---|
| test in the caller, intrinsics in a helper | 49 errors - analysis is per BLOCK, does not follow a call |
| `[MethodImpl(AggressiveInlining)]` on that helper | 37 errors |
| `if (!supported) return;` early return | 4 errors |
| `if (IsAvx2Supported && IsFmaSupported) { ... }` | 49 errors, all reporting `block only supports None` |
| **`if (IsAvx2Supported) { ... }`, body inline** | **0 errors** |

Two things to remember. The condition must be a **single property** - a compound `a && b` splits the
basic block and the feature set comes back as `None`. And the intrinsics must be **inline in that
block**, which is why the horizontal reduces are written out longhand four times instead of going
through a `HSum` helper; the helper is its own block and fails.

Burst's AVX2 target implies FMA, so testing `IsAvx2Supported` alone covers `mm256_fmadd_ps`.

The scalar `else` branch is the Arm path and doubles as the readable statement of what the 8-wide
block does. Verified: 0 `BC1200` for both SIMD jobs at `ARMV8A_AARCH64`, and x86 performance
unchanged after the restructure (2.26 / 2.30 ms against variant 5 at 3.60).

### The AVX2 guard that silently measured the wrong job

Worth recording because it is the same failure mode as the rest of this document. Variant 8 needs
AVX2 + FMA, so it got a fallback to variant 5 on machines without them:

```csharp
if (variant == 8 && !(X86.Avx2.IsAvx2Supported && X86.Fma.IsFmaSupported)) variant = 5;   // WRONG
```

**Read from managed code those properties return FALSE on a machine that plainly has AVX2.** They
are Burst compile-time constants and only evaluate correctly inside Burst-compiled code. So the
guard downgraded every frame to variant 5, and the sweep reported variant 5's 3.63 ms under variant
8's name - a perfectly clean-looking result that was measuring the wrong job.

The tell was that variant 8 and variant 5 agreed to 0.01 ms, which for two completely different
inner loops should have been impossible. **Two configs agreeing far more closely than the noise
floor is evidence of a harness bug, not of a null result.**

The fix is `CpuFeatureJob`, a one-element `[BurstCompile] IJob` that reads the flags where they are
real and writes the answer out, run once in `Allocate`.

### What made it work where three previous SIMD attempts failed

- The 4-wide attempt kept the compaction store, so it kept the barrier. This deletes it.
- `ColouredAblate3` vectorized but its 32-wide path never ran. This is unconditional, no guard.
- SoA was dismissed on its own and does nothing on its own. It is a PREREQUISITE, not a fix.

### The pipeline stays AoS

Only the separation loop is SoA: one deinterleave before the six passes, one interleave after, about
0.06 ms of linear work. `ScatterJob`, `SteerJob`, `FinalizeJob` and all ten variant jobs are
untouched - which is what keeps variants 3 and 5 alive as bookends. Converting the whole pipeline
would have put the measurement methodology at risk for no extra speed.

### Two deliberate behaviour changes

`MaxNeighbours` is not applied - a masked accumulation has no "first 16" - and the rsqrt is raw
`vrsqrtps` with no Newton step. Both are judged by the body-overlap column above, which improved.

### Where it does NOT help

The SIMD walk LOSES at larger scan radii: 0.94x at R=2 and 0.76x at R=3, because runs there hold
1.52 and 1.01 candidates and eight lanes are mostly waste. R=1 packs 3.70 into a block. This is
more evidence that the tight 3x3 grid is the right structure, not just the default.

## What Burst actually says, and how to ask it

Burst will tell you why a loop did not vectorize. Nobody in this project had asked. The recipe:
`BurstCompilerService.GetDisassembly` with **`dump=128` (IRPassAnalysis) ALONE and `debug=1`**.
`dump=64` (Analysis) returns an empty string, and `dump=128|16` degrades to plain asm. Note that
both harnesses copied from `BurstInspectorGUI.cs` pass `disable-warnings=BC1370;BC1322`, which
suppresses the loop diagnostics — drop it.

`Loop.ExpectVectorized()` is the worse tool: it needs the `UNITY_BURST_EXPERIMENTAL_LOOP_INTRINSICS`
scripting define (a global Player-settings edit) and only emits BC1321, a yes/no with no reason.
The remarks give the reason and the source line.

Verbatim, on the shipping job:

```
BoidContactJobs.cs:88:0: loop not vectorized: value that could not be identified as
                         reduction is used outside the loop    [NonReductionValueUsedOutsideLoop]
BoidContactJobs.cs:101:0: loop not vectorized: loop control flow is not understood by
                          vectorizer                           [CFGNotUnderstood]
```

Line 88 is the candidate loop; line 101 is the phase-2 contact loop, where `CFGNotUnderstood` is
the `if (r2 > 1e-12f)` degenerate-normal branch. `ColouredWalkRJob`'s inner loop draws no remark at
all — it vectorizes.

### Two stacked blockers, and fixing either one alone does nothing

A probe matrix of seven job variants, each compiled and read back as remarks plus a vector-op count:

| what was changed | remark on the candidate loop | vectorized |
|---|---|---|
| baseline | NonReductionValueUsedOutsideLoop | no |
| store removed, `math.min(n + sel, Cap)` kept | NonReductionValueUsedOutsideLoop | no |
| clamp removed, `n` still the store address | NonReductionValueUsedOutsideLoop | no |
| clamp AND store both removed | **CantVectorizeInstructionReturnType** | no |
| `[NoAlias]` on every field | NonReductionValueUsedOutsideLoop | no |
| SoA floats + dense `mask[k]` + `n += hit` | **none** | **yes, 2x8-wide** |

1. **The compaction index.** The saturating `math.min(..., Cap)` breaks reduction recognition on
   its own, and so does using `n` as the store address, because LLVM requires the reduction phi's
   only use to be the reduction op. They are separately disqualifying.
2. **`float2`.** With the compaction gone entirely it STILL fails, on a different remark, at
   `var d = pi - pred[k]`. This is the one nobody suspected and it is why the rejected-ideas entry
   above is wrong.

`[NoAlias]` changed nothing — byte-identical remark and vector-op count. Aliasing was never involved.

The combination that does vectorize is SoA positions + a dense `mask[k]` store + a plain
`n += hit` reduction:

```asm
vsubps      ymm12, ymm2, ymmword ptr [r11 + 4*r14 - 32]
vfmadd231ps ymm12, ymm14, ymm14
vcmpltps    ymm12, ymm12, ymm1
vpaddd      ymm5,  ymm11, ymm5
```

And it answers the trip-count objection that killed the idea twice before. Its entry guard is
`cmp rdx, 3 / ja`, with a 4-wide xmm remainder path below the 16-wide one — not the
`cmp 7 / jbe` that makes `ColouredWalkRJob`'s vector path unreachable at a 3.70-candidate run.
**At this run length the vector path is reachable.**

This is asm evidence, not a wall-clock measurement. Nothing here is a win until it goes through
`SweepConfigs` bookended.

## Where the candidate walk actually goes

Answered two ways: by reading the Burst disassembly of the inner loop, and by measuring the same
walk at three scan radii, which moves row runs and candidates in opposite directions and so
separates them. `BoidSwarm.RadiusBenchmark` -> `BoidSolver.MeasureRadius` is the harness; it logs
`COLR|`.

### What Burst emits

`SeparateColouredJob`'s phase-1 loop is NOT vectorised and NOT unrolled. Burst unrolls the `pass`
loop into three identical scalar copies and leaves each one as this, per candidate:

```
.LBB0_13:  inc     rax                        ; k++
           cmp     r12, rax
           je      .LBB0_14                   ; loop exit, mispredicted once per run
.LBB0_9:   cmp     edi, 63                    ; w = min(n, Cap-1)
           mov     ecx, edi
           jl      .LBB0_11
           mov     ecx, 63
.LBB0_11:  vmovsd  xmm0, [rsi + 8*rax]        ; pred[k]
           vsubps / vmulps / vmovshdup / vaddss    ; lengthsq
           movsxd  rcx, ecx
           mov     [rbp + 4*rcx - 96], eax    ; cand[w] = k, a store on EVERY candidate
           vucomiss xmm0, xmm2
           setb    cl
           cmp     r15, rax
           setne   dl
           and     dl, cl
           movzx   ecx, dl
           add     edi, ecx                   ; n += ...
           cmp     edi, 64                    ; n = min(n + inc, Cap)
           jl      .LBB0_13
           mov     edi, 64
           jmp     .LBB0_13
```

Twenty instructions and three branches, not four instructions. The two `math.min` clamps became
control flow rather than `cmov` - correctly, because `n` is loop-carried through the whole float
compare chain and LLVM broke the dependency with a perfectly-predicted branch. `cand[w] = k` is a
store on every candidate whether it survives or not, which is the price of the compaction split
that made this job fast in the first place.

`ColouredAblate3` is a different shape and worth knowing about, because it is the job the 6.5
cycle figure was computed from. Its body has no memory access, so LLVM vectorised it: a 32-wide
AVX2 unrolled reduction, then an 8-wide remainder, then a scalar tail. **None of the vector paths
ever execute.** The guard is `cmp dword ptr [rsp+44], 7 / jbe`, and a row run holds ~3.9
candidates, so every candidate goes down the scalar fall-through. What C3 - C2 measured was three
loop entries per agent, each dragging a vectoriser trip-count guard and a mispredicted exit, with
the actual work spread over 11.6 candidates.

### The split, measured

`walk(R) = (2R+1) * Fixed + candidates(R) * PerCandidate`, three radii on one crowd state:

| R | row runs | cand/agent | cand/run | walk ms/pass |
|---|---|---|---|---|
| 1 | 3 | 11.11 | 3.70 | 0.290 |
| 2 | 5 | 7.62 | 1.52 | 0.381 |
| 3 | 7 | 7.09 | 1.01 | 0.437 |

Least squares: **Fixed = 0.051 ms per row run, PerCandidate = 0.013 ms per candidate.** At 5
executing threads and 4.6 GHz over 50,000 agents that is **24 cycles per row-run entry and 6.0
cycles per candidate**.

So at R=1 the walk is ~137 cycles per agent: **70 of them are the three loop entries and 66 are the
11.09 candidates** - 51% fixed cost that the candidate count has nothing to do with, which is
exactly the gap between the 6.5 that was measured and the one or two the instructions can cost.

(An earlier revision of this paragraph said 85 and 48. That was arithmetic error, not a different
measurement: 3 x 23.5 = 70 and 11.09 x 5.98 = 66. The 51/49 split is what the coefficients give
and it agrees with the ~53% quoted from the least-squares fit.)

The 24 cycles per entry are the two `Offset` loads for the run bounds - scattered reads into a
223 KB array at R=1 and a 2 MB array at R=3 - the loop guard, and one mispredicted exit at ~17
cycles on a run holding under four candidates. Hoisting the bounds to once per CELL barely helps,
because a cell holds 1.23 agents at R=1 and exactly 1.00 at R>=2.

That kills the SIMD idea for good and it kills lead 2, below, before it is even built: anything
that raises the row-run count to buy fewer candidates is trading a 24-cycle item for a 6-cycle one.

## Scan radius: built, parameterised, measured, rejected

`SeparateColouredRJob` + `ColourCellsRJob` (`SeparateVariant = 4`, `BoidSwarm.ScanRadius`). Cell =
collision diameter / R, scan reaches R cells, colour spacing R+1, (R+1)^2 colours. The reach from
any point in a cell is exactly one diameter at every R, so no contact is ever missed and nothing is
traded for the speed - the swept area falls from 9 D^2 to 6.25 to 5.44, and the measured candidates
per agent fall with it, 11.11 -> 7.62 -> 7.09, within 2% of the geometry.

One play session, bookended:

| config | wall | mean pen |
|---|---|---|
| variant 3, hard-coded 3x3, 4 colours (bookend) | **5.37 ms** | 5.37% |
| variant 4, R=1, 4 colours | **5.12 ms** | 6.68% |
| variant 4, R=2, 5x5, 9 colours | **6.06 ms** | 7.20% |
| variant 4, R=3, 7x7, 16 colours | **7.23 ms** | 7.17% |
| variant 3, hard-coded 3x3, 4 colours (bookend) | **5.37 ms** | 7.02% |

The bookends land on the same wall to two decimals and 1.65 points apart on penetration, so the
penetration noise floor on this run is ~1.7 points and every quality number above is the same
number. Quality was never the question here; the geometry says it cannot change.

**R=2 costs 0.94 ms more than R=1 and R=3 costs 2.11 ms more.** The rough arithmetic that made this
lead look close (-0.9 ms of candidates against +0.5 ms of barriers) was wrong on both sides:

- The candidates it saves are the 6-cycle item, not the 12-cycle apparent one. 3.5 fewer candidates
  per agent per pass is worth ~0.045 ms, not 0.9.
- Row runs go 3 -> 5, and that is the 24-cycle item: +0.10 ms per pass, more than the candidates
  save on its own.
- Cells go 55,692 -> 219,945 -> 492,768. The grid build, the prefix scan and the bucketing all pay
  for that, and it was not in the estimate at all.
- Work items go 40,708 -> 50,000. At R=1 a cell holds 1.23 agents so the per-cell prologue is
  amortised; at R>=2 every agent is alone in its cell and it is not.
- Dispatches per pass go 4 -> 9 -> 16, six passes a frame.

The parameterised job is kept, at `SeparateVariant = 4`. It is the negative result, and it is also
0.25 ms FASTER than the shipping job at R=1 - see below.

## Cell density: built, measured, rejected

The cycle split says the walk costs ~24 cycles per ROW-RUN ENTRY against ~6 per candidate, and
there are 3 entries per AGENT. The obvious read is to hold more agents per cell so the run bounds,
which are hoisted once per cell, amortise over more of them - a bigger cell at the same 3x3 scan.
`BoidSolver.CellScale` does that; reach stays diameter * scale, so nothing is missed above 1.

It loses immediately, check off, same session:

| cell scale | agents/cell | wall (check off) | mean pen |
|---|---|---|---|
| 1.0 | 1.23 | **3.62 ms** | 7.29% |
| 1.4 | ~2.4 | 4.17 ms | 7.28% |
| 2.0 | ~4.9 | 5.91 ms | 7.63% |

Candidates grow as scale^2 while the entries saved go as 1/scale^2, and the candidate term is on
11.1 items per agent against 3 entries. The minimum is at or below scale 1 and the tight grid is
already there. Together with the scan-radius result above, both directions out of the current cell
size are now measured and both are worse - the 3x3 scan at cell = collision diameter is the
optimum, not just the default.

## Contact math: the divide

Phase 2 was 0.29 ms of a 0.58 ms pass - half the job - and had never been looked at. The
disassembly of the per-keeper main path says why:

```
    movsxd  rax, [r12]                 ; cand[q]
    vmovsd  xmm0, [rsi + 8*rax]        ; RELOAD pred[k] - a random read phase 1 already did
    vsubps  xmm1, xmm10, xmm0          ; RECOMPUTE d
    vmulps / vmovshdup / vaddss        ; RECOMPUTE lengthsq
    vrsqrtss / vmulss / vfmadd / ...   ; sqrt(r2) via rsqrt + one Newton step
    vucomiss / ja .LBB0_45             ; taken on every normal keeper
.LBB0_45:
    vmovss  xmm0, 1.0
    vdivss  xmm0, xmm0, xmm2           ; 1/dist as a FULL DIVIDE
    vbroadcastss / vmulps              ; nrm = d * (1/dist)
```

`math.length(d)` then `d / dist` is what the source asks for and Burst emitted exactly that: a
square root followed by a divide, when one reciprocal square root does both.

```csharp
var inv = math.rsqrt(r2);
dist = r2 * inv;      // = sqrt(r2)
nrm  = d * inv;       // = d / dist
```

Two things were wrong and they are separable, so each got its own sweep row:

| config | wall | mean pen |
|---|---|---|
| variant 3, shipping (bookend) | 5.36 ms | 5.53% |
| variant 4, parameterised R=1 | 5.05 ms | 6.81% |
| variant 5, + rsqrt for sqrt-then-divide | **4.95 ms** | 7.29% |
| variant 6, + phase 1 carries d and r2 forward | 5.00 ms | 7.26% |
| variant 3, shipping (bookend) | 5.40 ms | 7.11% |

Bookends 0.04 ms apart on wall. **rsqrt is worth ~0.4 ms against the shipping bookend**, reproduced
across three sessions (5.36/5.40 -> 5.05, 5.49/5.55 -> 5.05, 5.50/5.60 -> 5.12 check-on; and
4.06/4.01 -> 3.62 check-off).

The penetration column in the table above is NOT evidence on its own, and an earlier version of
this section leaned on it wrongly. The first config in a sweep reads systematically better than the
rest - 1.4 points, in every run measured - because `AutoTarget` has only just come on and the crowd
is still settling into the flowing regime. That bias landed on the bookend and made the noise floor
look like 1.6 points. With a discarded row at the front absorbing it the bookends come in 0.5-0.7
points apart, and variant 5 lands between them. `SweepConfig.Discard` exists for this.

**Carrying the delta forward is a negative result.** Variant 6 hands phase 2 the `d` and `r2` phase
1 already computed, which removes a random load into a 400 KB array, a subtract and a lengthsq per
KEEPER - and it is 0.05 ms slower than variant 5, because it pays 12 bytes of stack store per
CANDIDATE and there are 11.1 candidates per agent against 6 keepers. Almost twice as many stores
added as loads removed. Kept as variant 6 for the reason.

Per pass on one crowd state, `COLR|` at R=1: full 0.510, rsqrt 0.476, fused 0.487 - **but do not
lean on those.** Across three sessions the same `full` config read 0.510, 0.508 and 0.483, a spread
of 0.027 ms, and on the third the rsqrt difference came out at -0.013 instead of +0.035. The 8-rep
`Complete()`-per-pass harness cannot resolve a 0.035 ms effect against its own 0.027 ms of drift.
Reps are now 32 and the doc comment says what the number is good for: ratios between radii, and
the candidate counts, which are exact. Small differences go to `SweepConfigs`, 240 frames a config
and bookended.

## Where the pass goes now, and why 6 iterations stays

Re-measured against the job that ships, at 32 reps rather than the 8 that could not resolve
anything. Per pass at R=1: walk 0.278 ms, full 0.492 ms.

| | ms/pass | share |
|---|---|---|
| contact math | 0.214 | **44%** |
| walk: row-run entry (3 per agent, ~24 cycles each) | 0.143 | 29% |
| walk: per candidate (11.1 per agent, ~6 cycles each) | 0.135 | 27% |

Contact math is the largest single bucket even after the divide came out of it. None of the three
has more than about 0.1 ms of plausible headroom, and every structural way of moving the middle row
is measured and losing.

**So the lever is the pass count, not the pass.** Each iteration is worth ~0.46 ms of frame,
measured check off: 6 -> 3.62, 5 -> 3.14, 4 -> 2.71. That is larger than anything left inside a
pass. It is also not available:

| config | mean pen vs ref | body overlaps vs ref |
|---|---|---|
| it 5 | +0.29 points | **1.86x** |
| it 4 | +1.52 points | **5.77x** |

Dropping one iteration nearly doubles the number of pairs whose bodies actually interpenetrate, and
dropping two quintuples it. **6 is genuinely the knee, now confirmed against the new pass cost and
the corrected metric.** The only way to spend fewer passes is a solver that converges in fewer -
Chebyshev acceleration on the SOR, or a better ordering - not a cheaper pass.

### Two methodology notes, both of which cost a run to learn

**Penetration carries over between sweep rows.** The first attempt put it 6, 5, 4 in order and the
two it=6 bookends came out 6.62% and 9.01% - 2.4 points apart, because the it=4 row left the crowd
wrecked and 8 s of dwell could not recover it. The numbers for it=5 and it=4 were readings on a
progressively degrading crowd, not on the configs. The fix is an it=6 REFERENCE ROW BETWEEN EVERY
MEASURED CONFIG and a longer dwell, so each row inherits the same crowd; even then the reference
drifted 7.22 -> 7.86 -> 8.03% over the run, so compare each row against the reference next to it
rather than against a single bookend.

**Mean penetration is nearly blind at this quality level.** Across every config in that sweep it
spans 7.2-9.2%, while body overlaps - pairs closer than `2 * Radius`, i.e. bodies genuinely
interpenetrating - span 376 to 3130. `meanPen` averages over ~142,000 barely-touching pairs and
drowns the few hundred that are actually wrong. **Rank configs on `body`, and use `meanPen` only to
confirm nothing moved.** This is the same failure the old overlap test had, one level down.

## BatchSize: not a lever either

The last knob this document listed as unswept. `SweepConfig.BatchSize` now drives it; check off,
variant 5, one session:

| BatchSize | wall (check off) |
|---|---|
| 64 | 3.63 ms |
| 128 (bookend A) | 3.64 ms |
| 256 | 3.54 ms |
| 512 | 3.55 ms |
| 128 (bookend B) | 3.51 ms |

The two bookends for the SAME config disagree by 0.13 ms and every other row sits inside that, so
the whole spread is noise. Same verdict `ColourBatch` already had. Note the noise floor on this
run was 0.13 ms against 0.05 and 0.09 in the two before it - which is the argument for bookending
every sweep rather than assuming a fixed tolerance.

Third confirmation of the main result in passing: variant 3 at 3.97 ms against variant 5 at ~3.53.

## The self test on the outer rows: a null result

The candidate loop rejects `k == i` on all ~11.1 candidates, but the agent lives on row cy and only
one of the 2R+1 row runs is on that row - the other two cannot contain it, so the test is dead work
on two thirds of the candidates. Splitting the loop on `q == r` removes it from the outer runs for
one predictable branch per run. That is `SeparateColouredSplitJob`, variant 7.

It buys nothing: **3.54 ms against variant 5's 3.50**, check off, same session, inside the 0.09 ms
the two bookends disagree by. Check on, 5.08 against 5.15 - also inside the noise, and the other
way round.

This is the third time this project has measured the same thing: `AblateStage7` found the
branchless reject worthless, the 4-wide SIMD walk lost, and now removing three ALU ops from 7.4
candidates per agent is invisible. The small integer work in that loop is free. What costs is the
loop control and the memory - which is exactly what the 24-cycles-per-row-run-entry number says.
Kept as variant 7 for the reason.

## Sleeping: measured first, and the premise is false

The idea was that in a jammed crowd most interior agents cannot move, so skipping them - while
still letting them act as obstacles for everyone else - would be close to free. This document
already said to get one number before building it. That number is now in, from
`BoidSolver.MeasureMotion` -> `MOTION|`.

Measuring it needs care, because agents have NO IDENTITY across frames: the counting sort reorders
the arrays every frame, so index i is a different agent next frame and a frame-to-frame
displacement means nothing. The comparison is therefore snapshot-after-sort against the same index
after the last separation pass, within one frame, where the order is fixed. That also happens to be
the right quantity: it is the SOLVE correction, not the steering, so an agent flowing freely with
the crowd counts as unmoved - the reading most favourable to the idea.

How far the solve moved each agent, as a fraction of the solve diameter, during flow:

| moved | variant 3 | variant 5 |
|---|---|---|
| < 0.01% | 0.2% | 0.3% |
| 0.01 - 0.1% | 0.9% | 0.9% |
| 0.1 - 1% | 90.9% | **93.3%** |
| 1 - 5% | 7.9% | 5.3% |
| > 5% | 0.1% | 0.1% |

**Only 1.2% of agents are effectively immobile.** 93% of the crowd is moving 0.1-1% of a diameter
per frame - small, but not zero, and it is the correction that holds the penetration where it is.
There is no large stuck population to skip. A threshold low enough to be safe skips ~1% of the
crowd and saves nothing; a threshold high enough to skip 93% is not sleeping, it is turning the
solver off.

The premise held for a crowd converged on a stationary target, which is the easy case and the
reason this had to be measured under flow. Dead.

## Colour bucketing in parallel

`ColourCellsJob` walks all ~56,000 cells on one core, once per grid build, for 0.19 ms.
`ColourCellsRJob` does the same work as one job per colour: a colour owns the cells whose
`(cx % S, cy % S)` matches, which is a fixed 1/S^2 stride of the grid, so the S^2 jobs walk
disjoint slices in parallel.

At R=1 variant 4 is 5.12 ms against variant 3's 5.37 ms - same geometry, same colouring, same
contact math, 0.25 ms apart. The parallel bucketing is the obvious candidate at 0.19 ms of measured
single-threaded work, and the run-bound hoist in `SeparateColouredRJob` is the other one, but the
sweep does not separate them: it measures the two jobs, not the two changes. Split them before
quoting a cause. The 0.25 ms itself is solid - the bookends agree to two decimals.

This is the one part of this work that should ship.

## Open leads

1. ~~The ~6.5 cycles per candidate.~~ Answered above. It is 24 cycles per row-run entry and 6.0
   per candidate; the fixed half is 53% of the walk at R=1. The lever it points at is FEWER ROW
   RUNS, not fewer candidates - which is the opposite direction from lead 2, and is why lead 2
   lost.
2. ~~Cut the 11.6 candidates by halving the cell and scanning 5×5.~~ Built, measured, rejected:
   6.06 ms against 5.12 ms. Kept in the tree as `SeparateVariant = 4`.
3. ~~Fewer row runs, not fewer candidates - a larger cell scanned 3×3.~~ Built as
   `BoidSolver.CellScale`, measured, rejected: 3.62 / 4.17 / 5.91 ms at scale 1.0 / 1.4 / 2.0.
   Both directions out of the current cell size are now closed, so the 3×3 scan at cell =
   collision diameter is the optimum rather than the default.
4. ~~Sleeping.~~ Measured before building, and the premise is false: only 1.2% of agents are
   effectively immobile during flow. See above. In a jammed crowd most interior agents cannot move. Skip agents whose
   neighbourhood did not move; a skipped agent still acts as an obstacle so quality holds. Worth
   one number first: what fraction of agents move more than a hair per frame. Weak during spawn
   and flow, which is when the cost matters, so measure before building.
5. ~~Contact math, 0.17 ms.~~ It was 0.29 ms, not 0.17, and it was a `vdivss`. Fixed - see above,
   0.43 ms. What is left of it is 0.20 ms per pass and it is the one line that does NOT fall with
   the candidate count (0.205 at R=1, 0.179 at R=2, 0.202 at R=3), because it is driven by keepers.
   The remaining shape is a taken branch and an rsqrt + Newton chain per keeper; dropping the
   Newton step is the next thing to try, and the penetration metric is what says whether 12 bits
   of mantissa is enough.
6. ~~`BatchSize` sweep for the non-coloured jobs.~~ Swept 64/128/256/512, all inside the bookend
   noise. Not a lever.

**Every lead this document has ever listed is now closed.** What is left, in rough order of how
much is plausibly in them:

7. **The 24 cycles per row-run entry.** Still the largest single line, still unattacked, and both
   obvious ways of trading it (fewer runs via a bigger cell, more runs via a smaller one) are
   measured and both lose. It is two dependent `Offset` loads, a loop guard and a mispredicted
   exit on a run of under four candidates. The loads are already hoisted per cell and a cell holds
   1.23 agents, so there is nothing left to amortise them over. Attacking it means changing what
   the grid stores, not how it is scanned.
8. **Non-separation work, ~0.5 ms.** Was 11% of the frame when the frame was 4.5 ms and is 14% of
   it now. `ScanJob`'s single-threaded prefix over ~56,000 cells is the obvious piece. The old
   verdict that it is not worth attacking was correct at the time and is worth re-testing at 3.5.
9. **Dropping the Newton step on the rsqrt.** `math.rsqrt` emits `vrsqrtss` plus one refinement;
   the raw instruction is ~12 bits, which is far below what the penetration metric can resolve.
   Small - maybe 3 ops per keeper on ~6 keepers - and it needs an x86 intrinsic, so it is a
   portability question as much as a perf one.
10. **Fewer passes.** `Iterations = 6` is the quality knob and the only one with real ms in it:
    the whole solver is six repeats of a 0.5 ms pass. Everything above is shaving the pass; this
    is the one that removes them. It was swept before and 6 is the knee, but it was swept against
    the OLD pass cost.

## How to measure — read this before touching anything

The harness is in the code, not in scratch scripts.

- **`BoidSwarm.RadiusBenchmark`** measures the candidate walk, the full pass and the candidates per
  agent at scan radius 1, 2 and 3 on ONE crowd state, and logs `COLR|`. Two radii give two
  equations in the fixed and per-candidate cost; the third checks the fit.
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
