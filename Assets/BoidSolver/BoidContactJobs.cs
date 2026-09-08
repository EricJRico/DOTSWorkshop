using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Workshop
{
    // Phase 2 of the coloured pass - the contact math - measured at 0.29 ms of a 0.58 ms pass, half
    // the job, and never examined. The Burst disassembly of SeparateColouredJob says the per-keeper
    // main path is:
    //
    //     movsxd  rax, [r12]                 ; cand[q]
    //     vmovsd  xmm0, [rsi + 8*rax]        ; RELOAD pred[k] - a random read phase 1 already did
    //     vsubps  xmm1, xmm10, xmm0          ; RECOMPUTE d
    //     vmulps / vmovshdup / vaddss        ; RECOMPUTE lengthsq
    //     vrsqrtss / vmulss / vfmadd / ...   ; sqrt(r2) via rsqrt + one Newton step
    //     vucomiss / ja .LBB0_45             ; taken on every normal keeper
    // .LBB0_45:
    //     vmovss  xmm0, 1.0
    //     vdivss  xmm0, xmm0, xmm2           ; 1/dist as a FULL DIVIDE, ~11-14 cycles
    //     vbroadcastss / vmulps              ; nrm = d * (1/dist)
    //
    // Burst took `math.length(d)` then `d / dist` literally and never fused them. Two things are
    // wrong with it and they are separable, so there is one job for each:
    //
    //   variant 5, RsqrtJob - reciprocal square root instead of square root then divide.
    //     inv = rsqrt(r2); dist = r2 * inv; nrm = d * inv. Same NR chain, no divide, and the
    //     branch collapses. Costs nothing in phase 1.
    //   variant 6, FusedJob - the above, plus phase 1 hands phase 2 the d and r2 it already
    //     computed instead of throwing them away. Removes a random load into a 400 KB array, a
    //     subtract and a lengthsq per keeper, and pays for it with 12 bytes of hot stack store per
    //     CANDIDATE - and there are ~11 candidates per agent against ~6 keepers, so which way this
    //     one lands is not obvious and is the reason it is a separate row.
    //
    // Both are otherwise byte-identical to SeparateColouredRJob so a sweep row isolates one change.

    /// <summary>Variant 5: <see cref="SeparateColouredRJob"/> with the divide removed.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct SeparateColouredRsqrtJob : IJobParallelForDefer
    {
        public const int Cap = BoidSolver.MaxNeighbourStride;
        public const int MaxRadius = SeparateColouredRJob.MaxRadius;

        [ReadOnly] public NativeArray<int> Cells;
        [NativeDisableParallelForRestriction] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> ContactNormal;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> NeighbourCount;
        public float Diameter;
        public float Omega;
        public int MaxNeighbours;
        public int ScanRadius;
        public float2 PlayerPosition;
        public float PlayerReach;

        public void Execute(int m)
        {
            var g = Info[0];
            var b = Cells[m];

            var pred = (float2*)Predicted.GetUnsafePtr();
            var d2 = Diameter * Diameter;
            var cand = stackalloc int[Cap];

            var r = math.clamp(ScanRadius, 1, MaxRadius);
            var runs = 2 * r + 1;
            var runStart = stackalloc int[2 * MaxRadius + 1];
            var runEnd = stackalloc int[2 * MaxRadius + 1];
            var row = b - r * g.Cols;
            for (var q = 0; q < runs; q++, row += g.Cols)
            {
                runStart[q] = Offset[row - r];
                runEnd[q] = Offset[row + r + 1];
            }

            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++)
            {
                var pi = pred[i];

                var n = 0;
                for (var q = 0; q < runs; q++)
                {
                    var end = runEnd[q];
                    for (var k = runStart[q]; k < end; k++)
                    {
                        var d = pi - pred[k];
                        var r2 = math.lengthsq(d);
                        var w = math.min(n, Cap - 1);
                        cand[w] = k;
                        n = math.min(n + math.select(0, 1, r2 < d2 & k != i), Cap);
                    }
                }

                var sum = float2.zero;
                var normal = float2.zero;
                var found = math.min(n, MaxNeighbours);
                for (var q = 0; q < found; q++)
                {
                    var k = cand[q];
                    var d = pi - pred[k];
                    var r2 = math.lengthsq(d);
                    float2 nrm;
                    float dist;
                    // One reciprocal square root serves both the length and the normalise. The old
                    // form asked for sqrt and then divided by it, and Burst emitted exactly that.
                    if (r2 > 1e-12f)
                    {
                        var inv = math.rsqrt(r2);
                        dist = r2 * inv;
                        nrm = d * inv;
                    }
                    else
                    {
                        var h = (uint)(i * 73856093) ^ (uint)(k * 19349663);
                        var a = (h & 1023u) * (6.2831853f / 1024f);
                        nrm = new float2(math.cos(a), math.sin(a));
                        dist = 0f;
                    }

                    sum += 0.5f * (Diameter - dist) * nrm;
                    normal += nrm;
                }

                var toPlayer = pi - PlayerPosition;
                var pd2 = math.lengthsq(toPlayer);
                if (pd2 < PlayerReach * PlayerReach)
                {
                    var dist = math.sqrt(pd2);
                    var nrm = dist > 1e-6f ? toPlayer / dist : new float2(1f, 0f);
                    sum += (PlayerReach - dist) * nrm;
                    normal += nrm;
                    found++;
                }

                var div = found;
                pred[i] = found > 0 ? pi + sum * (Omega / div) : pi;
                ContactNormal[i] = normal;
                NeighbourCount[i] = found;
            }
        }
    }

    /// <summary>
    /// Variant 6: variant 5, and phase 1 keeps the delta and the squared distance it computed
    /// rather than making phase 2 fetch the neighbour again and redo both.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct SeparateColouredFusedJob : IJobParallelForDefer
    {
        public const int Cap = BoidSolver.MaxNeighbourStride;
        public const int MaxRadius = SeparateColouredRJob.MaxRadius;

        [ReadOnly] public NativeArray<int> Cells;
        [NativeDisableParallelForRestriction] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> ContactNormal;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> NeighbourCount;
        public float Diameter;
        public float Omega;
        public int MaxNeighbours;
        public int ScanRadius;
        public float2 PlayerPosition;
        public float PlayerReach;

        public void Execute(int m)
        {
            var g = Info[0];
            var b = Cells[m];

            var pred = (float2*)Predicted.GetUnsafePtr();
            var d2 = Diameter * Diameter;
            var cand = stackalloc int[Cap];
            // The two carried streams. ~1 KB of stack per work item all told, and it is hot.
            var candD = stackalloc float2[Cap];
            var candR2 = stackalloc float[Cap];

            var r = math.clamp(ScanRadius, 1, MaxRadius);
            var runs = 2 * r + 1;
            var runStart = stackalloc int[2 * MaxRadius + 1];
            var runEnd = stackalloc int[2 * MaxRadius + 1];
            var row = b - r * g.Cols;
            for (var q = 0; q < runs; q++, row += g.Cols)
            {
                runStart[q] = Offset[row - r];
                runEnd[q] = Offset[row + r + 1];
            }

            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++)
            {
                var pi = pred[i];

                var n = 0;
                for (var q = 0; q < runs; q++)
                {
                    var end = runEnd[q];
                    for (var k = runStart[q]; k < end; k++)
                    {
                        var d = pi - pred[k];
                        var r2 = math.lengthsq(d);
                        var w = math.min(n, Cap - 1);
                        cand[w] = k;
                        candD[w] = d;
                        candR2[w] = r2;
                        n = math.min(n + math.select(0, 1, r2 < d2 & k != i), Cap);
                    }
                }

                var sum = float2.zero;
                var normal = float2.zero;
                var found = math.min(n, MaxNeighbours);
                for (var q = 0; q < found; q++)
                {
                    var d = candD[q];
                    var r2 = candR2[q];
                    float2 nrm;
                    float dist;
                    if (r2 > 1e-12f)
                    {
                        var inv = math.rsqrt(r2);
                        dist = r2 * inv;
                        nrm = d * inv;
                    }
                    else
                    {
                        var k = cand[q];
                        var h = (uint)(i * 73856093) ^ (uint)(k * 19349663);
                        var a = (h & 1023u) * (6.2831853f / 1024f);
                        nrm = new float2(math.cos(a), math.sin(a));
                        dist = 0f;
                    }

                    sum += 0.5f * (Diameter - dist) * nrm;
                    normal += nrm;
                }

                var toPlayer = pi - PlayerPosition;
                var pd2 = math.lengthsq(toPlayer);
                if (pd2 < PlayerReach * PlayerReach)
                {
                    var dist = math.sqrt(pd2);
                    var nrm = dist > 1e-6f ? toPlayer / dist : new float2(1f, 0f);
                    sum += (PlayerReach - dist) * nrm;
                    normal += nrm;
                    found++;
                }

                var div = found;
                pred[i] = found > 0 ? pi + sum * (Omega / div) : pi;
                ContactNormal[i] = normal;
                NeighbourCount[i] = found;
            }
        }
    }
}
