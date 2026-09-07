using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Workshop
{
    /// <summary>
    /// Grid dimensions, recomputed every frame from the crowd's bounding box. Kept in a
    /// one-element NativeArray so the jobs downstream can read it without a main-thread sync.
    /// </summary>
    public struct GridInfo
    {
        public float2 Min;
        public float InvCell;
        public int Cols;
        public int Rows;
        public int Cells;
    }

    /// <summary>
    /// Per-slice bounding box, so the reduction is parallel rather than one pass on one core.
    /// Slice i covers [i * Stride, min(n, (i+1) * Stride)).
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct BoundsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Position;
        [WriteOnly] public NativeArray<float4> Partial;   // xy = min, zw = max
        public int Stride;

        public void Execute(int slice)
        {
            var start = slice * Stride;
            var end = math.min(Position.Length, start + Stride);
            var lo = new float2(float.MaxValue);
            var hi = new float2(float.MinValue);
            for (var i = start; i < end; i++)
            {
                var p = Position[i];
                lo = math.min(lo, p);
                hi = math.max(hi, p);
            }
            Partial[slice] = new float4(lo, hi);
        }
    }

    /// <summary>
    /// Folds the slice boxes together and sizes the grid. Cell size is the collision diameter,
    /// which is what makes a 3x3 scan sufficient and keeps the candidate/keeper ratio near 2.
    /// If that many cells would blow the budget the cell grows instead - more candidates scanned,
    /// never a missed neighbour.
    /// </summary>
    [BurstCompile]
    public struct GridSetupJob : IJob
    {
        [ReadOnly] public NativeArray<float4> Partial;
        [WriteOnly] public NativeArray<GridInfo> Info;
        public float CellSize;
        public int MaxCells;
        public int Count;

        public void Execute()
        {
            var lo = new float2(float.MaxValue);
            var hi = new float2(float.MinValue);
            for (var i = 0; i < Partial.Length; i++)
            {
                lo = math.min(lo, Partial[i].xy);
                hi = math.max(hi, Partial[i].zw);
            }
            if (lo.x > hi.x) { lo = float2.zero; hi = float2.zero; }

            var cell = CellSize;
            var size = hi - lo;
            // 2 cells of padding each side so a 3x3 scan never needs a bounds test in the inner loop.
            var maxDim = (int)math.floor(math.sqrt((float)MaxCells));
            for (var guard = 0; guard < 32; guard++)
            {
                var cols = (int)math.ceil(size.x / cell) + 5;
                var rows = (int)math.ceil(size.y / cell) + 5;
                if (cols <= maxDim && rows <= maxDim) break;
                cell *= 2f;
            }

            var c = (int)math.ceil(size.x / cell) + 5;
            var r = (int)math.ceil(size.y / cell) + 5;
            c = math.min(c, maxDim);
            r = math.min(r, maxDim);

            Info[0] = new GridInfo
            {
                Min = lo - cell * 2f,
                InvCell = 1f / cell,
                Cols = c,
                Rows = r,
                Cells = c * r
            };
        }
    }

    /// <summary>Counting sort, pass 1: which cell is each agent in, and how full is each cell.</summary>
    [BurstCompile]
    public unsafe struct HashJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<int> CellOf;
        [NativeDisableParallelForRestriction] [NativeDisableUnsafePtrRestriction]
        public NativeArray<int> CellCount;

        public void Execute(int i)
        {
            var g = Info[0];
            var c = BoidGrid.CellIndex(Position[i], g);
            CellOf[i] = c;
            Interlocked.Increment(ref *((int*)CellCount.GetUnsafePtr() + c));
        }
    }

    /// <summary>
    /// Counting sort, pass 2. Writes the INCLUSIVE prefix into Offset, so Offset[c] is the end of
    /// cell c. ScatterJob then decrements it down to the start, which leaves exactly the
    /// [Offset[c], Offset[c+1]) layout the solve wants - with no separate clear pass, because this
    /// job zeroes CellCount for the next frame while it is already touching that memory.
    /// </summary>
    [BurstCompile]
    public struct ScanJob : IJob
    {
        public NativeArray<int> CellCount;
        [WriteOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        public int Count;

        public void Execute()
        {
            var cells = Info[0].Cells;
            var running = 0;
            for (var c = 0; c < cells; c++)
            {
                running += CellCount[c];
                Offset[c] = running;
                CellCount[c] = 0;
            }
            Offset[cells] = Count;
        }
    }

    /// <summary>
    /// Counting sort, pass 3: move every agent into cell order. The whole point is that after this
    /// the solve reads neighbours from CONTIGUOUS memory - Ihmsen et al. 2011 measured 2.77x on the
    /// neighbour query from this reorder alone. Agents have no identity here, so the sorted arrays
    /// simply become the new canonical arrays; there is no indirection left to pay for.
    /// </summary>
    [BurstCompile]
    public unsafe struct ScatterJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> CellOf;
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<float2> Velocity;
        [NativeDisableParallelForRestriction] [NativeDisableUnsafePtrRestriction]
        public NativeArray<int> Offset;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> SortedPosition;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> SortedPredicted;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> SortedVelocity;

        public void Execute(int i)
        {
            var c = CellOf[i];
            var slot = Interlocked.Decrement(ref *((int*)Offset.GetUnsafePtr() + c));
            SortedPosition[slot] = Position[i];
            SortedPredicted[slot] = Predicted[i];
            SortedVelocity[slot] = Velocity[i];
        }
    }

    /// <summary>Steer toward the player and predict where that puts each agent this frame.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct SteerJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Position;
        public NativeArray<float2> Velocity;
        [WriteOnly] public NativeArray<float2> Predicted;
        public float2 Target;
        public float Speed;
        public float Blend;
        public float DeltaTime;

        public void Execute(int i)
        {
            var toTarget = Target - Position[i];
            var preferred = math.lengthsq(toTarget) > 1e-8f
                ? math.normalize(toTarget) * Speed
                : float2.zero;
            var v = math.lerp(Velocity[i], preferred, Blend);
            Velocity[i] = v;
            Predicted[i] = Position[i] + v * DeltaTime;
        }
    }

    /// <summary>
    /// The whole cost of the solver. One Jacobi pass of pairwise separation over the 3x3 cells
    /// around each agent, read as THREE CONTIGUOUS RUNS rather than nine cells: cells (x-1,y),
    /// (x,y), (x+1,y) are adjacent in memory, so the run is [Offset[base-1], Offset[base+2]).
    /// Three sequential streams instead of nine random ones is what makes the prefetcher work.
    ///
    /// This is a real per-neighbour loop, not a per-cell aggregate. An aggregate cannot do this
    /// job: normalize(pos*n - sum) reduces to normalize(pos - centroid), which is identical for
    /// two overlapping agents and therefore can never push them apart.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct SeparateJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Result;
        [WriteOnly] public NativeArray<float2> ContactNormal;
        [WriteOnly] public NativeArray<int> NeighbourCount;
        public float Diameter;
        public float Omega;
        public int MaxNeighbours;
        public float2 PlayerPosition;
        public float PlayerReach;

        // A branchless variant of this loop (math.select masks instead of `continue`, no early
        // exit) measured SLOWER: 0.937 ms/dispatch against 0.844. It did not vectorise - the
        // trip count is a runtime value and the neighbour read is a gather, so Burst keeps the
        // loop scalar either way, and the masked form then pays rsqrt on every candidate rather
        // than only the ~1 in 3 that are actually in contact. It also raised overlap 130 -> 321
        // because dropping the cap changes the Jacobi averaging. Vectorising needs an SoA x/z
        // layout and fixed-width chunks, not a rewrite of the conditionals.
        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Predicted[i];
            var d2 = Diameter * Diameter;
            var sum = float2.zero;
            var normal = float2.zero;
            var found = 0;

            var cx = (int)((pi.x - g.Min.x) * g.InvCell);
            var cy = (int)((pi.y - g.Min.y) * g.InvCell);
            cx = math.clamp(cx, 1, g.Cols - 2);
            cy = math.clamp(cy, 1, g.Rows - 2);

            // Centre row first: it holds the closest neighbours, so if the cap ever does bite it
            // truncates the far ones. Same ordering trick Unreal Mass uses (nearest cell first).
            for (var pass = 0; pass < 3 && found < MaxNeighbours; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                var end = Offset[b + 2];
                for (var k = Offset[b - 1]; k < end; k++)
                {
                    if (k == i) continue;
                    var d = pi - Predicted[k];
                    var r2 = math.lengthsq(d);
                    if (r2 >= d2) continue;

                    var dist = math.sqrt(r2);
                    float2 n;
                    if (dist > 1e-6f)
                    {
                        n = d / dist;
                    }
                    else
                    {
                        // Coincident agents: pick a deterministic direction from the pair so they
                        // do not stay welded together. Cheaper and steadier than a random number.
                        var h = (uint)(i * 73856093) ^ (uint)(k * 19349663);
                        var a = (h & 1023u) * (6.2831853f / 1024f);
                        n = new float2(math.cos(a), math.sin(a));
                        dist = 0f;
                    }

                    sum += 0.5f * (Diameter - dist) * n;   // equal mass, half each
                    normal += n;
                    if (++found >= MaxNeighbours) break;
                }
            }

            // The player is an obstacle of infinite mass, so the agent takes the whole correction.
            var toPlayer = pi - PlayerPosition;
            var pd2 = math.lengthsq(toPlayer);
            if (pd2 < PlayerReach * PlayerReach)
            {
                var dist = math.sqrt(pd2);
                var n = dist > 1e-6f ? toPlayer / dist : new float2(1f, 0f);
                sum += (PlayerReach - dist) * n;
                normal += n;
                found++;
            }

            Result[i] = found > 0 ? pi + sum * (Omega / found) : pi;
            ContactNormal[i] = normal;
            NeighbourCount[i] = found;
        }
    }

    /// <summary>
    /// Derive velocity from the solved positions and commit. With TangentialSlide on, the part of
    /// velocity that drives straight into the contact normal is removed, so a blocked agent slides
    /// around the pack rather than shoving into it - Weiss et al. 2017 section 4.5, reduced to one
    /// dot product by applying it to the contact normal we already accumulated.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct FinalizeJob : IJobParallelFor
    {
        public NativeArray<float2> Position;
        public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<float2> ContactNormal;
        [ReadOnly] public NativeArray<int> NeighbourCount;
        [WriteOnly] [NativeDisableParallelForRestriction] public NativeArray<float4> Instances;
        public float2 Target;
        public float InvDeltaTime;
        public float MaxSpeed;
        public bool TangentialSlide;
        public bool WriteInstances;
        public float CrowdFree;
        public float CrowdFull;

        public void Execute(int i)
        {
            var p = Predicted[i];
            var v = (p - Position[i]) * InvDeltaTime;

            if (TangentialSlide)
            {
                var n = ContactNormal[i];
                var len = math.length(n);
                if (len > 1e-4f)
                {
                    n /= len;
                    // n points away from the neighbours. Velocity heading into them has a negative
                    // component along n; drop exactly that part and the rest is the slide.
                    var into = math.dot(v, n);
                    if (into < 0f) v -= into * n;
                }
            }

            // Congestion gate. An agent walled in on every side cannot make progress toward the
            // target; all its seek does is add pressure that the Jacobi solve then has to fight,
            // and that pressure is what compresses the pack past close packing. So the deeper an
            // agent is buried, the more of its TOWARD-TARGET velocity is removed - tangential
            // motion is untouched, so the crowd still flows around instead of freezing.
            // Same idea as Narain et al. 2009 eq. 3, which blends the aggregate field back toward
            // the agent's own preferred velocity as density falls. The edge of the pack is where
            // progress is actually available, and the edge is exactly where this gate does nothing.
            if (CrowdFull > CrowdFree)
            {
                var crowd = math.saturate((NeighbourCount[i] - CrowdFree) / (CrowdFull - CrowdFree));
                if (crowd > 0f)
                {
                    var toT = Target - p;
                    var lenT = math.length(toT);
                    if (lenT > 1e-5f)
                    {
                        toT /= lenT;
                        var into = math.dot(v, toT);
                        if (into > 0f) v -= toT * (into * crowd);
                    }
                }
            }

            var speed = math.length(v);
            if (speed > MaxSpeed) v *= MaxSpeed / speed;

            Position[i] = p;
            Velocity[i] = v;
            // Only the last substep's positions are drawn, so the upload buffer is written once.
            if (WriteInstances) Instances[i] = new float4(p.x, p.y, speed, 0f);
        }
    }

    /// <summary>
    /// Walk the grid ONCE per frame and record each agent's neighbours. Every separation pass
    /// after this reads the list instead of re-traversing 3x3 cells, which is where nearly all
    /// the cost was: 8 iterations meant 8 full grid traversals to solve the same contact set.
    /// Building the list once per frame and reusing it across the iterations is the standard
    /// PBD arrangement (Macklin et al. 2014 do exactly this).
    ///
    /// The list is gathered on the predicted positions and the solve then moves agents by at
    /// most the overlap depth, so a pair that was outside the radius at gather time cannot
    /// become deeply overlapped within the frame - the next frame's gather catches it.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct GatherJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> Neighbours;
        [WriteOnly] public NativeArray<int> NeighbourCount;
        /// <summary>Gather radius: the solve diameter plus a skin, so the list stays valid as the
        /// iterations move agents. Straight out of Verlet neighbour lists in molecular dynamics -
        /// without it, pairs that come into contact during the solve are never recorded and the
        /// overlap count explodes (measured: 30 pairs -> 11,062).</summary>
        public float GatherDiameter;
        public int MaxNeighbours;
        public int Stride;

        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Predicted[i];
            var d2 = GatherDiameter * GatherDiameter;
            var found = 0;
            var b0 = i * Stride;

            var cx = (int)((pi.x - g.Min.x) * g.InvCell);
            var cy = (int)((pi.y - g.Min.y) * g.InvCell);
            cx = math.clamp(cx, 1, g.Cols - 2);
            cy = math.clamp(cy, 1, g.Rows - 2);

            for (var pass = 0; pass < 3 && found < MaxNeighbours; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                var end = Offset[b + 2];
                for (var k = Offset[b - 1]; k < end; k++)
                {
                    if (k == i) continue;
                    var d = pi - Predicted[k];
                    if (math.lengthsq(d) >= d2) continue;
                    Neighbours[b0 + found] = k;
                    if (++found >= MaxNeighbours) break;
                }
            }

            NeighbourCount[i] = found;
        }
    }

    /// <summary>
    /// One Jacobi separation pass over the cached neighbour list. No grid traversal and no
    /// distance culling of candidates that were never going to be neighbours - just the pairs
    /// that actually touch.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct SeparateCachedJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Neighbours;
        [ReadOnly] public NativeArray<int> NeighbourCount;
        [WriteOnly] public NativeArray<float2> Result;
        [WriteOnly] public NativeArray<float2> ContactNormal;
        public float Diameter;
        public float Omega;
        public int Stride;
        public float2 PlayerPosition;
        public float PlayerReach;

        public void Execute(int i)
        {
            var pi = Predicted[i];
            var sum = float2.zero;
            var normal = float2.zero;
            var used = 0;

            var count = NeighbourCount[i];
            var b0 = i * Stride;
            for (var m = 0; m < count; m++)
            {
                var k = Neighbours[b0 + m];
                var d = pi - Predicted[k];
                var r2 = math.lengthsq(d);
                if (r2 >= Diameter * Diameter) continue;

                var dist = math.sqrt(r2);
                float2 n;
                if (dist > 1e-6f)
                {
                    n = d / dist;
                }
                else
                {
                    var h = (uint)(i * 73856093) ^ (uint)(k * 19349663);
                    var a = (h & 1023u) * (6.2831853f / 1024f);
                    n = new float2(math.cos(a), math.sin(a));
                    dist = 0f;
                }

                sum += 0.5f * (Diameter - dist) * n;
                normal += n;
                used++;
            }

            var toPlayer = pi - PlayerPosition;
            var pd2 = math.lengthsq(toPlayer);
            if (pd2 < PlayerReach * PlayerReach)
            {
                var dist = math.sqrt(pd2);
                var n = dist > 1e-6f ? toPlayer / dist : new float2(1f, 0f);
                sum += (PlayerReach - dist) * n;
                normal += n;
                used++;
            }

            Result[i] = used > 0 ? pi + sum * (Omega / used) : pi;
            ContactNormal[i] = normal;
        }
    }

    /// <summary>Zero the overlap counters before the parallel pass fills them.</summary>
    [BurstCompile]
    public struct ClearStatsJob : IJob
    {
        [WriteOnly] public NativeArray<int> Stats;

        public void Execute()
        {
            for (var i = 0; i < Stats.Length; i++) Stats[i] = 0;
        }
    }

    /// <summary>
    /// The correctness check, not a debug aid: counts pairs that are ACTUALLY interpenetrating,
    /// meaning centre distance below 2*Radius. Note this is a stricter test than the constraint
    /// the solver targets - the solver pushes to 2*Radius*1.05, so the 5% margin is slack that
    /// costs nothing here. Runs over the same flat grid, in Burst, in parallel, so it is cheap
    /// enough to leave on.
    ///
    /// Stats layout: 0 = overlapping pairs, 1 = agents with at least one overlap,
    /// 2 = worst penetration (ppm of body diameter), 3 = summed penetration in units of 0.01%
    /// (NOT ppm: at 65k pairs a ppm sum overflows int32, which showed up as a negative mean),
    /// 4..9 = histogram of penetration depth: &lt;0.1%, 0.1-1%, 1-5%, 5-10%, 10-25%, &gt;25%.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct OverlapJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        public float BodyDiameter;
        [NativeDisableParallelForRestriction] [NativeDisableUnsafePtrRestriction]
        public NativeArray<int> Stats;

        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Position[i];
            var b2 = BodyDiameter * BodyDiameter;

            var cx = (int)((pi.x - g.Min.x) * g.InvCell);
            var cy = (int)((pi.y - g.Min.y) * g.InvCell);
            cx = math.clamp(cx, 1, g.Cols - 2);
            cy = math.clamp(cy, 1, g.Rows - 2);

            var pairs = 0;
            var worst = 0;
            var sum = 0;
            var any = false;
            int h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0, h5 = 0;

            for (var dy = -1; dy <= 1; dy++)
            {
                var b = (cy + dy) * g.Cols + cx;
                var end = Offset[b + 2];
                for (var k = Offset[b - 1]; k < end; k++)
                {
                    if (k == i) continue;
                    var d = pi - Position[k];
                    var r2 = math.lengthsq(d);
                    if (r2 >= b2) continue;
                    any = true;
                    if (k < i) continue;                       // count each pair once
                    pairs++;
                    var frac = (BodyDiameter - math.sqrt(r2)) / BodyDiameter;
                    var pen = (int)(frac * 1e6f);
                    sum += (int)(frac * 1e4f);
                    if (pen > worst) worst = pen;
                    if (frac < 0.001f) h0++;
                    else if (frac < 0.01f) h1++;
                    else if (frac < 0.05f) h2++;
                    else if (frac < 0.10f) h3++;
                    else if (frac < 0.25f) h4++;
                    else h5++;
                }
            }

            var s = (int*)Stats.GetUnsafePtr();
            if (pairs > 0)
            {
                Interlocked.Add(ref *(s + 0), pairs);
                Interlocked.Add(ref *(s + 3), sum);
                int seen;
                do
                {
                    seen = System.Threading.Volatile.Read(ref *(s + 2));
                    if (worst <= seen) break;
                } while (Interlocked.CompareExchange(ref *(s + 2), worst, seen) != seen);
            }
            if (h0 != 0) Interlocked.Add(ref *(s + 4), h0);
            if (h1 != 0) Interlocked.Add(ref *(s + 5), h1);
            if (h2 != 0) Interlocked.Add(ref *(s + 6), h2);
            if (h3 != 0) Interlocked.Add(ref *(s + 7), h3);
            if (h4 != 0) Interlocked.Add(ref *(s + 8), h4);
            if (h5 != 0) Interlocked.Add(ref *(s + 9), h5);
            if (any) Interlocked.Increment(ref *(s + 1));
        }
    }
}
