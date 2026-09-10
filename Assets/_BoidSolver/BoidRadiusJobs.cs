using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Workshop
{
    /// <summary>
    /// The coloured Gauss-Seidel separation pass with the scan radius as a parameter instead of a
    /// hard-coded 3x3.
    ///
    /// The geometry. The cell is the collision diameter divided by R, and the scan reaches R cells,
    /// so the reach from any point in a cell is exactly one diameter whatever R is - no contact is
    /// ever missed and no quality is traded. What changes is the swept area: (2R+1)^2 cells of
    /// (D/R)^2 each is D^2 (2R+1)^2 / R^2, which is 9 D^2 at R=1, 6.25 at R=2, 5.44 at R=3, against
    /// the 0.785 D^2 that actually holds contacts. Candidates scanned per agent fall in the same
    /// proportion.
    ///
    /// The price. Two same-colour cells must not run at once if one reads what the other writes. A
    /// work item writes only its own cell and reads R cells out, so same-colour cells have to be at
    /// least R+1 apart on each axis - which is (R+1)^2 colours, hence (R+1)^2 dispatches and
    /// barriers per pass instead of 4. It also multiplies the cell count by R^2, which the grid
    /// build, the prefix scan and the colour bucketing all pay for.
    ///
    /// Whether the smaller sweep beats the extra dispatches is a measurement, not an argument. Run
    /// it through BoidSwarm.SweepConfigs.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct SeparateColouredRJob : IJobParallelForDefer
    {
        public const int Cap = BoidSolver.MaxNeighbourStride;
        /// <summary>Largest scan radius the stack buffers are sized for.</summary>
        public const int MaxRadius = 3;

        /// <summary>Cell indices of this colour. One work item per CELL, not per agent.</summary>
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

            // The three row-run bounds used to be hoisted out of the agent loop by Burst, because
            // the pass loop was unrolled at a fixed radius. A runtime radius stops that happening,
            // so hoist them by hand - otherwise this measures the loss of the unroll, not R.
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
                    var dist = math.length(d);
                    float2 nrm;
                    if (dist > 1e-6f)
                    {
                        nrm = d / dist;
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
    /// Bucket the cells of ONE colour, for a colour spacing of S = R + 1.
    ///
    /// One job per colour rather than one job over the whole grid. A colour owns the cells whose
    /// (cx % S, cy % S) matches, which is a fixed 1/S^2 slice of the grid, so each job walks its
    /// own slice with a stride of S and the S^2 jobs run in parallel. The single-threaded version
    /// this replaces cost 0.19 ms at 4 colours, and the cell count grows as R^2, so scanning the
    /// whole grid on one core once per grid build stops being cheap the moment R goes up.
    /// </summary>
    [BurstCompile]
    public struct ColourCellsRJob : IJob
    {
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        public NativeList<int> Cells;
        /// <summary>Colour index in [0, S*S).</summary>
        public int Colour;
        /// <summary>Colour spacing, R + 1.</summary>
        public int Spacing;

        public void Execute()
        {
            var g = Info[0];
            Cells.Clear();
            var s = math.max(2, Spacing);
            var lo = g.Pad;
            var hiX = g.Cols - 1 - g.Pad;
            var hiY = g.Rows - 1 - g.Pad;

            // First cell of this colour at or after the padded interior. The border ring is skipped
            // rather than tested: the hash clamps every agent into [Pad, Cols-1-Pad], so those cells
            // are empty and a radius-R scan from an interior cell never leaves the grid.
            var x0 = lo + Mod(Colour % s - lo, s);
            var y0 = lo + Mod(Colour / s - lo, s);

            for (var cy = y0; cy <= hiY; cy += s)
            {
                var rowBase = cy * g.Cols;
                for (var cx = x0; cx <= hiX; cx += s)
                {
                    var b = rowBase + cx;
                    if (Offset[b] != Offset[b + 1]) Cells.Add(b);
                }
            }
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;
    }

    /// <summary>
    /// The candidate walk alone, at a runtime radius: the same loop nest as
    /// <see cref="SeparateColouredRJob"/> with the body reduced to the loop counter and the self
    /// test, plus a per-agent candidate counter.
    ///
    /// This is <see cref="ColouredAblate3"/> generalised, and it is what turns "6.5 cycles per
    /// candidate" into two numbers instead of one. Time it at two radii and the candidates per
    /// agent come with it, so the cost splits into a fixed part per ROW RUN - loop entry, the
    /// vectoriser trip-count guard, the mispredicted exit - and a variable part per CANDIDATE.
    /// R=1 to R=2 cuts candidates by ~31% and raises row runs from 3 to 5, so the two parts move
    /// in opposite directions and can be solved for.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct ColouredWalkRJob : IJobParallelForDefer
    {
        public const int MaxRadius = SeparateColouredRJob.MaxRadius;

        [ReadOnly] public NativeArray<int> Cells;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [ReadOnly] public NativeArray<float2> Predicted;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Sink;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> Candidates;
        public int ScanRadius;

        public void Execute(int m)
        {
            var g = Info[0];
            var pred = (float2*)Predicted.GetUnsafeReadOnlyPtr();
            var b = Cells[m];

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
                var acc = 0;
                var seen = 0;
                for (var q = 0; q < runs; q++)
                {
                    var end = runEnd[q];
                    for (var k = runStart[q]; k < end; k++)
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
}
