using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Workshop
{
    // Cost breakdown of SeparateJob's candidate loop. Per-element ProfilerMarkers would fire
    // hundreds of thousands of times a frame and cost more than the code they measure, so this
    // is the same loop with successive pieces added back, and the cost of each piece is the
    // difference between consecutive stages.
    //
    // These run ALONGSIDE the real solve, writing to throwaway buffers. Running them instead of
    // it was wrong: with no separation the crowd collapses denser, every cell holds more
    // candidates, and the stripped job then measures a different simulation - which is how a
    // stripped variant came out SLOWER than the full job.
    //
    // Stages 2..5 deliberately have NO early exit, so they all scan the same candidate set and
    // the deltas between them are clean. The real job's cap therefore shows up in the last delta.
    //
    //   1 dispatch + read own position + write        floor
    //   2 + grid lookup (cell index, 3 run bounds)    - 1  = grid lookup
    //   3 + walk candidates, touch index k only       - 2  = loop overhead + k==i branch
    //   4 + load Predicted[k]                         - 3  = the neighbour position load
    //   5 + lengthsq                                  - 4  = the distance arithmetic
    //   6 + reject branch and neighbour count         - 5  = the branch
    //   SeparateJob                                   - 6  = contact math + early exit

    /// <summary>Stage 1: dispatch, read own position, write it back.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct AblateStage1 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int i)
        {
            Sink[i] = Predicted[i];
        }
    }

    /// <summary>Stage 2: + cell index and the three row-run bounds reads.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct AblateStage2 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Predicted[i];
            var cx = math.clamp((int)((pi.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((pi.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);

            var acc = 0;
            for (var pass = 0; pass < 3; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                acc += Offset[b + 2] - Offset[b - 1];
            }
            Sink[i] = pi + new float2(acc * 1e-9f, 0f);
        }
    }

    /// <summary>Stage 3: + walk every candidate, touching only the loop counter and the self test.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct AblateStage3 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Predicted[i];
            var cx = math.clamp((int)((pi.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((pi.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);

            var acc = 0;
            for (var pass = 0; pass < 3; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                var end = Offset[b + 2];
                for (var k = Offset[b - 1]; k < end; k++)
                {
                    if (k == i) continue;
                    acc += k;
                }
            }
            Sink[i] = pi + new float2(acc * 1e-9f, 0f);
        }
    }

    /// <summary>Stage 4: + load the neighbour's position.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct AblateStage4 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Predicted[i];
            var cx = math.clamp((int)((pi.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((pi.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);

            var acc = float2.zero;
            for (var pass = 0; pass < 3; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                var end = Offset[b + 2];
                for (var k = Offset[b - 1]; k < end; k++)
                {
                    if (k == i) continue;
                    acc += Predicted[k];
                }
            }
            Sink[i] = pi + acc * 1e-9f;
        }
    }

    /// <summary>Stage 5: + the squared distance.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct AblateStage5 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Predicted[i];
            var cx = math.clamp((int)((pi.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((pi.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);

            var acc = 0f;
            for (var pass = 0; pass < 3; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                var end = Offset[b + 2];
                for (var k = Offset[b - 1]; k < end; k++)
                {
                    if (k == i) continue;
                    var d = pi - Predicted[k];
                    acc += math.lengthsq(d);
                }
            }
            Sink[i] = pi + new float2(acc * 1e-9f, 0f);
        }
    }

    /// <summary>Stage 6: + the reject branch and the neighbour count. Still no early exit.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct AblateStage6 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Sink;
        public float Diameter;

        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Predicted[i];
            var d2 = Diameter * Diameter;
            var cx = math.clamp((int)((pi.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((pi.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);

            var acc = 0f;
            var found = 0;
            for (var pass = 0; pass < 3; pass++)
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
                    acc += r2;
                    found++;
                }
            }
            Sink[i] = pi + new float2(acc * 1e-9f, found * 1e-9f);
        }
    }

    /// <summary>
    /// Stage 7: stage 6 with the reject as a mask instead of a branch. Same arithmetic, same
    /// candidates, no data-dependent control flow. The gap to stage 6 is the branch's own cost.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct AblateStage7 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Sink;
        public float Diameter;

        public void Execute(int i)
        {
            var g = Info[0];
            var pi = Predicted[i];
            var d2 = Diameter * Diameter;
            var cx = math.clamp((int)((pi.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((pi.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);

            var acc = 0f;
            var found = 0;
            for (var pass = 0; pass < 3; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                var end = Offset[b + 2];
                for (var k = Offset[b - 1]; k < end; k++)
                {
                    var d = pi - Predicted[k];
                    var r2 = math.lengthsq(d);
                    var hit = r2 < d2 & k != i;
                    acc += math.select(0f, r2, hit);
                    found += math.select(0, 1, hit);
                }
            }
            Sink[i] = pi + new float2(acc * 1e-9f, found * 1e-9f);
        }
    }

    /// <summary>
    /// Stage 8: phase 1 of <see cref="SeparateCompactJob"/> on its own - masked reject plus the
    /// unconditional store and saturating cursor. The gap to stage 7 is what compaction costs, and
    /// the gap from stage 8 to the full compact job is its contact math.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct AblateStage8 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Sink;
        public float Diameter;

        public void Execute(int i)
        {
            const int cap = SeparateCompactJob.Cap;
            var g = Info[0];
            var pi = Predicted[i];
            var d2 = Diameter * Diameter;
            var cx = math.clamp((int)((pi.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((pi.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);

            var cand = stackalloc int[cap];
            var n = 0;
            for (var pass = 0; pass < 3; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                var end = Offset[b + 2];
                for (var k = Offset[b - 1]; k < end; k++)
                {
                    var d = pi - Predicted[k];
                    var r2 = math.lengthsq(d);
                    var w = math.min(n, cap - 1);
                    cand[w] = k;
                    n = math.min(n + math.select(0, 1, r2 < d2 & k != i), cap);
                }
            }
            // Touch the buffer so the stores cannot be dead-code eliminated.
            Sink[i] = pi + new float2(n * 1e-9f, cand[math.max(0, n - 1)] * 1e-9f);
        }
    }

    /// <summary>
    /// Stage 9: stage 8 four candidates at a time - two float4 loads and two swizzles to split
    /// x from y, then the same four scalar store-and-advance steps. The gap to stage 8 is what
    /// the 4-wide walk actually buys, and it is the number that decides whether
    /// <see cref="SeparateSimdJob"/> is worth keeping over <see cref="SeparateCompactJob"/>.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct AblateStage9 : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Predicted;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [WriteOnly] public NativeArray<float2> Sink;
        public float Diameter;

        public void Execute(int i)
        {
            const int cap = SeparateCompactJob.Cap;
            var g = Info[0];
            var pred = (float2*)Predicted.GetUnsafeReadOnlyPtr();
            var pi = pred[i];
            var d2 = Diameter * Diameter;
            var cx = math.clamp((int)((pi.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((pi.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);

            var pxi = new float4(pi.x);
            var pyi = new float4(pi.y);
            var d24 = new float4(d2);
            var i4 = new int4(i);
            var lane = new int4(0, 1, 2, 3);

            var cand = stackalloc int[cap];
            var n = 0;
            for (var pass = 0; pass < 3; pass++)
            {
                var y = cy + (pass == 0 ? 0 : (pass == 1 ? -1 : 1));
                var b = y * g.Cols + cx;
                var end = Offset[b + 2];
                var k = Offset[b - 1];

                for (; k + 4 <= end; k += 4)
                {
                    var a = *(float4*)(pred + k);
                    var c = *(float4*)(pred + k + 2);
                    var dx = pxi - new float4(a.xz, c.xz);
                    var dy = pyi - new float4(a.yw, c.yw);
                    var hit = (dx * dx + dy * dy < d24) & (k + lane != i4);

                    cand[math.min(n, cap - 1)] = k;
                    n = math.min(n + math.select(0, 1, hit.x), cap);
                    cand[math.min(n, cap - 1)] = k + 1;
                    n = math.min(n + math.select(0, 1, hit.y), cap);
                    cand[math.min(n, cap - 1)] = k + 2;
                    n = math.min(n + math.select(0, 1, hit.z), cap);
                    cand[math.min(n, cap - 1)] = k + 3;
                    n = math.min(n + math.select(0, 1, hit.w), cap);
                }

                for (; k < end; k++)
                {
                    var d = pi - pred[k];
                    var w = math.min(n, cap - 1);
                    cand[w] = k;
                    n = math.min(n + math.select(0, 1, math.lengthsq(d) < d2 & k != i), cap);
                }
            }
            Sink[i] = pi + new float2(n * 1e-9f, cand[math.max(0, n - 1)] * 1e-9f);
        }
    }
}
