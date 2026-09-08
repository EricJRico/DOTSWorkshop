using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Workshop
{
    // Cost breakdown of SeparateColouredJob, the job that actually ships.
    //
    // The existing stages 1..9 in BoidAblation profile SeparateCompactJob, which is the Jacobi
    // path we no longer run. Its work item is one AGENT; this job's work item is one CELL, it
    // writes in place instead of ping-ponging, and it runs as four dispatches. None of that
    // carries over, so the old table cannot be used to say where this job spends its time.
    //
    // Same method as before: the same loop with successive pieces added back, and the cost of a
    // piece is the difference between consecutive stages. Every stage is scheduled over all four
    // colour lists exactly like the real job, so the dispatch and barrier count matches too.
    //
    //   C1 cells, agents, read own position, write it back   floor, incl. 4 dispatches
    //   C2 + the three row-run bound reads                   - C1 = grid lookup
    //   C3 + walk candidates, loop counter and k != i        - C2 = loop control
    //   C4 + load the neighbour position                     - C3 = the neighbour load
    //   C5 + lengthsq                                        - C4 = the distance arithmetic
    //   C6 + compare and compaction store                    - C5 = phase 1 complete
    //   SeparateColouredJob                                  - C6 = phase 2 contact math

    /// <summary>Stage C1: the cell and agent loops, a read and a write. The floor.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct ColouredAblate1 : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int> Cells;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [ReadOnly] public NativeArray<float2> Predicted;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int m)
        {
            var pred = (float2*)Predicted.GetUnsafeReadOnlyPtr();
            var b = Cells[m];
            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++) Sink[i] = pred[i];
        }
    }

    /// <summary>Stage C2: + the three row-run bound reads.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct ColouredAblate2 : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int> Cells;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [ReadOnly] public NativeArray<float2> Predicted;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int m)
        {
            var g = Info[0];
            var pred = (float2*)Predicted.GetUnsafeReadOnlyPtr();
            var b = Cells[m];
            var cy = b / g.Cols;
            var cx = b - cy * g.Cols;
            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++)
            {
                var acc = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var bb = (cy + dy) * g.Cols + cx;
                    acc += Offset[bb + 2] - Offset[bb - 1];
                }
                Sink[i] = pred[i] + new float2(acc * 1e-9f, 0f);
            }
        }
    }

    /// <summary>
    /// Stage C3: + walk every candidate, touching only the loop counter and the self test. Also
    /// records how many candidates each agent actually scanned, which is the number every
    /// per-candidate cost estimate has been guessing at.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct ColouredAblate3 : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int> Cells;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [ReadOnly] public NativeArray<float2> Predicted;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Sink;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> Candidates;

        public void Execute(int m)
        {
            var g = Info[0];
            var pred = (float2*)Predicted.GetUnsafeReadOnlyPtr();
            var b = Cells[m];
            var cy = b / g.Cols;
            var cx = b - cy * g.Cols;
            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++)
            {
                var acc = 0;
                var seen = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var bb = (cy + dy) * g.Cols + cx;
                    var end = Offset[bb + 2];
                    for (var k = Offset[bb - 1]; k < end; k++)
                    {
                        acc += math.select(0, k, k != i);
                        seen++;
                    }
                }
                Candidates[i] = seen;
                Sink[i] = pred[i] + new float2(acc * 1e-9f, 0f);
            }
        }
    }

    /// <summary>Stage C4: + load the neighbour position.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct ColouredAblate4 : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int> Cells;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [ReadOnly] public NativeArray<float2> Predicted;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int m)
        {
            var g = Info[0];
            var pred = (float2*)Predicted.GetUnsafeReadOnlyPtr();
            var b = Cells[m];
            var cy = b / g.Cols;
            var cx = b - cy * g.Cols;
            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++)
            {
                var acc = float2.zero;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var bb = (cy + dy) * g.Cols + cx;
                    var end = Offset[bb + 2];
                    for (var k = Offset[bb - 1]; k < end; k++) acc += pred[k];
                }
                Sink[i] = pred[i] + acc * 1e-9f;
            }
        }
    }

    /// <summary>Stage C5: + the squared distance.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct ColouredAblate5 : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int> Cells;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [ReadOnly] public NativeArray<float2> Predicted;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Sink;

        public void Execute(int m)
        {
            var g = Info[0];
            var pred = (float2*)Predicted.GetUnsafeReadOnlyPtr();
            var b = Cells[m];
            var cy = b / g.Cols;
            var cx = b - cy * g.Cols;
            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++)
            {
                var pi = pred[i];
                var acc = 0f;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var bb = (cy + dy) * g.Cols + cx;
                    var end = Offset[bb + 2];
                    for (var k = Offset[bb - 1]; k < end; k++) acc += math.lengthsq(pi - pred[k]);
                }
                Sink[i] = pi + new float2(acc * 1e-9f, 0f);
            }
        }
    }

    /// <summary>Stage C6: + the compare and the compaction store. This is phase 1 complete.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct ColouredAblate6 : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int> Cells;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [ReadOnly] public NativeArray<float2> Predicted;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Sink;
        public float Diameter;

        public void Execute(int m)
        {
            const int cap = SeparateCompactJob.Cap;
            var g = Info[0];
            var pred = (float2*)Predicted.GetUnsafeReadOnlyPtr();
            var d2 = Diameter * Diameter;
            var b = Cells[m];
            var cy = b / g.Cols;
            var cx = b - cy * g.Cols;
            var cand = stackalloc int[cap];
            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++)
            {
                var pi = pred[i];
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var bb = (cy + dy) * g.Cols + cx;
                    var end = Offset[bb + 2];
                    for (var k = Offset[bb - 1]; k < end; k++)
                    {
                        var r2 = math.lengthsq(pi - pred[k]);
                        var w = math.min(n, cap - 1);
                        cand[w] = k;
                        n = math.min(n + math.select(0, 1, r2 < d2 & k != i), cap);
                    }
                }
                Sink[i] = pi + new float2(n * 1e-9f, cand[math.max(0, n - 1)] * 1e-9f);
            }
        }
    }
}
