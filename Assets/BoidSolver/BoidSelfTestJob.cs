using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Workshop
{
    /// <summary>
    /// Variant 7: variant 5, with the self test removed from the two row runs that cannot contain
    /// the agent.
    ///
    /// The candidate loop rejects `k == i` on every one of its ~11.1 candidates, which the
    /// disassembly shows as `cmp r15, rax / setne dl / and dl, cl` folded into the increment. But
    /// the agent lives in cell b, on row cy, and the scan walks 2R+1 runs of which only the one at
    /// row cy contains b at all. The other 2R runs are on different rows, so `k == i` is
    /// impossible there and the test is dead work on 2R/(2R+1) of the candidates - two thirds of
    /// them at R=1.
    ///
    /// Splitting the loop on `q == r` moves the test out of the outer runs entirely and costs one
    /// predictable branch per run. Everything else is byte-identical to variant 5, so a sweep row
    /// isolates exactly this.
    ///
    /// The centre run still needs it, and it also still needs the `r2 &lt; d2` distance test: an
    /// agent is at distance zero from itself, which passes the distance test, so dropping the self
    /// test there would add a spurious contact with a degenerate normal.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct SeparateColouredSplitJob : IJobParallelForDefer
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
                    if (q == r)
                    {
                        // The agent's own row. This run contains i, so the self test stays.
                        for (var k = runStart[q]; k < end; k++)
                        {
                            var d = pi - pred[k];
                            var r2 = math.lengthsq(d);
                            var w = math.min(n, Cap - 1);
                            cand[w] = k;
                            n = math.min(n + math.select(0, 1, r2 < d2 & k != i), Cap);
                        }
                    }
                    else
                    {
                        // A different row: k can never be i, so the distance test is the whole test.
                        for (var k = runStart[q]; k < end; k++)
                        {
                            var d = pi - pred[k];
                            var r2 = math.lengthsq(d);
                            var w = math.min(n, Cap - 1);
                            cand[w] = k;
                            n = math.min(n + math.select(0, 1, r2 < d2), Cap);
                        }
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
}
