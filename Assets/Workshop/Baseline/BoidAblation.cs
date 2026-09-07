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
}
