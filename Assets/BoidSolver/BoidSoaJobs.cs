using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using static Unity.Burst.Intrinsics.X86;

namespace Workshop
{
    /// <summary>
    /// Variant 8. The coloured Gauss-Seidel pass rewritten as a hand-written 8-wide masked
    /// accumulation over SoA positions, with the candidate list deleted entirely.
    ///
    /// WHY THIS SHAPE. Burst's optimisation remarks (dump=128, debug=1) say the scalar job has two
    /// stacked blockers and that fixing either alone does nothing:
    ///   1. `n` is both a saturating reduction and the store address of `cand[n]`, so LLVM cannot
    ///      recognise it as a reduction - `NonReductionValueUsedOutsideLoop`.
    ///   2. Even with the whole candidate list removed it still fails, on
    ///      `CantVectorizeInstructionReturnType`: the body's scalar type is `float2`, and LLVM will
    ///      not widen a &lt;2 x float&gt; body into &lt;N x &lt;2 x float&gt;&gt;.
    /// So this job removes both at once: positions arrive as two float streams, and there is no
    /// candidate list because phase 2 is fused into phase 1 and runs on all 8 lanes under a mask.
    ///
    /// It is hand-written rather than left to the auto-vectoriser on purpose. A row run holds 3.70
    /// candidates and the vectoriser's own guard needs 8, so it emits a wide loop that never
    /// executes - which is exactly what `ColouredAblate3` does and why the 4-wide attempt lost.
    /// An unconditional 8-wide block has no guard to fail.
    ///
    /// THE MASK IS LOAD-BEARING, NOT TIDINESS. The 8-wide load runs past the end of a row run into
    /// cells that are the SAME COLOUR and are being written by other work items right now. Two
    /// guards make that safe:
    ///   - `k &lt; end` drops those lanes, so a racing value can never contribute. The shortcut
    ///     "the distance test rejects them anyway" is FALSE: a solver correction can move an agent
    ///     into range mid-pass (MeasureMotion measures 0.1% of agents moving &gt;5% of a diameter).
    ///   - every operand is masked BEFORE the fma, because 0 * NaN is NaN. Masking the product
    ///     afterwards would not stop a torn read poisoning the accumulator.
    ///
    /// TWO DELIBERATE BEHAVIOUR CHANGES, both of which need the body-overlap metric to judge:
    ///   - `MaxNeighbours` is not applied. A masked accumulation has no "first 16". The cap is
    ///     almost never reached (11.09 candidates and ~6 keepers per agent) but it is reachable in
    ///     a jam, and there it makes the solve stronger rather than weaker.
    ///   - raw `vrsqrtps`, no Newton step. ~12 bits of mantissa against a metric that cannot
    ///     resolve better than about half a percentage point of penetration.
    ///
    /// Determinism holds: accumulation order is fixed by (run, lane) and does not depend on thread
    /// count. It is NOT bit-identical to variant 5 - float addition is not associative and the lane
    /// order differs - so it must be judged on quality, not on a position delta.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct SeparateSoaMaskedJob : IJobParallelForDefer
    {
        public const int MaxRadius = SeparateColouredRJob.MaxRadius;

        [ReadOnly] public NativeArray<int> Cells;
        /// <summary>Read AND written in place - the colouring is what makes that safe.</summary>
        [NativeDisableParallelForRestriction] public NativeArray<float> PredX;
        [NativeDisableParallelForRestriction] public NativeArray<float> PredY;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> ContactNormal;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> NeighbourCount;
        public float Diameter;
        public float Omega;
        public int MinDivisor;
        public int ScanRadius;
        public float2 PlayerPosition;
        public float PlayerReach;

        public void Execute(int m)
        {
            var g = Info[0];
            var b = Cells[m];
            var px = (float*)PredX.GetUnsafePtr();
            var py = (float*)PredY.GetUnsafePtr();
            var d2 = Diameter * Diameter;

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

            var lanes = Avx.mm256_setr_epi32(0, 1, 2, 3, 4, 5, 6, 7);
            var vd2 = Avx.mm256_set1_ps(d2);
            var vEps = Avx.mm256_set1_ps(1e-12f);
            var vHalfD = Avx.mm256_set1_ps(0.5f * Diameter);
            var vNegHalf = Avx.mm256_set1_ps(-0.5f);

            var last = Offset[b + 1];
            for (var i = Offset[b]; i < last; i++)
            {
                // Reloaded per agent, not hoisted: earlier agents in this cell have already been
                // written, and seeing those writes IS the Gauss-Seidel step.
                var pix = px[i];
                var piy = py[i];
                var vxi = Avx.mm256_set1_ps(pix);
                var vyi = Avx.mm256_set1_ps(piy);
                var vi = Avx.mm256_set1_epi32(i);

                var sumx = Avx.mm256_setzero_ps();
                var sumy = Avx.mm256_setzero_ps();
                var nrmx = Avx.mm256_setzero_ps();
                var nrmy = Avx.mm256_setzero_ps();
                var found = 0;

                for (var q = 0; q < runs; q++)
                {
                    var start = runStart[q];
                    var end = runEnd[q];
                    var vend = Avx.mm256_set1_epi32(end);

                    for (var o = start; o < end; o += 8)
                    {
                        var xk = Avx.mm256_loadu_ps(px + o);
                        var yk = Avx.mm256_loadu_ps(py + o);
                        var dx = Avx.mm256_sub_ps(vxi, xk);
                        var dy = Avx.mm256_sub_ps(vyi, yk);
                        var r2 = Fma.mm256_fmadd_ps(dy, dy, Avx.mm256_mul_ps(dx, dx));

                        var k = Avx2.mm256_add_epi32(Avx.mm256_set1_epi32(o), lanes);
                        var inRange = Avx2.mm256_cmpgt_epi32(vend, k);
                        var isSelf = Avx2.mm256_cmpeq_epi32(k, vi);
                        var notSelf = Avx.mm256_andnot_ps(isSelf, inRange);
                        var near = Avx.mm256_cmp_ps(r2, vd2, (int)Avx.CMP.LT_OQ);
                        var keep = Avx.mm256_and_ps(near, notSelf);

                        found += math.countbits((uint)Avx.mm256_movemask_ps(keep));

                        var inv = Avx.mm256_rsqrt_ps(Avx.mm256_max_ps(r2, vEps));
                        var dist = Avx.mm256_mul_ps(r2, inv);
                        var nx = Avx.mm256_and_ps(Avx.mm256_mul_ps(dx, inv), keep);
                        var ny = Avx.mm256_and_ps(Avx.mm256_mul_ps(dy, inv), keep);
                        var t = Avx.mm256_and_ps(Fma.mm256_fmadd_ps(vNegHalf, dist, vHalfD), keep);

                        sumx = Fma.mm256_fmadd_ps(t, nx, sumx);
                        sumy = Fma.mm256_fmadd_ps(t, ny, sumy);
                        nrmx = Avx.mm256_add_ps(nrmx, nx);
                        nrmy = Avx.mm256_add_ps(nrmy, ny);
                    }
                }

                var sx = HSum(sumx);
                var sy = HSum(sumy);
                var nX = HSum(nrmx);
                var nY = HSum(nrmy);

                // Player repulsion, scalar - it is one test per agent, not per candidate.
                var toPlayerX = pix - PlayerPosition.x;
                var toPlayerY = piy - PlayerPosition.y;
                var pd2 = toPlayerX * toPlayerX + toPlayerY * toPlayerY;
                if (pd2 < PlayerReach * PlayerReach)
                {
                    var pdist = math.sqrt(pd2);
                    float ux, uy;
                    if (pdist > 1e-6f) { ux = toPlayerX / pdist; uy = toPlayerY / pdist; }
                    else { ux = 1f; uy = 0f; }
                    var push = PlayerReach - pdist;
                    sx += push * ux;
                    sy += push * uy;
                    nX += ux;
                    nY += uy;
                    found++;
                }

                var div = math.max(found, MinDivisor);
                if (found > 0)
                {
                    var scale = Omega / div;
                    px[i] = pix + sx * scale;
                    py[i] = piy + sy * scale;
                }
                ContactNormal[i] = new float2(nX, nY);
                NeighbourCount[i] = found;
            }
        }

        /// <summary>Horizontal sum of a ymm, 4 of these per agent.</summary>
        static float HSum(v256 v)
        {
            var hi = Avx.mm256_extractf128_ps(v, 1);
            var lo = Avx.mm256_castps256_ps128(v);
            var s = Sse.add_ps(lo, hi);
            s = Sse.add_ps(s, Sse.movehl_ps(s, s));
            s = Sse.add_ss(s, Sse.shuffle_ps(s, s, 0x55));
            return Sse.cvtss_f32(s);
        }
    }

    /// <summary>
    /// Fold the two float streams back into the float2 array the rest of the frame reads.
    ///
    /// The pipeline stays AoS and only the separation loop is SoA: one deinterleave before the six
    /// passes and one interleave after, about 0.06 ms of linear work, instead of converting
    /// ScatterJob, SteerJob, FinalizeJob and ten variant jobs and putting the bookends at risk.
    /// </summary>
    [BurstCompile]
    public struct InterleaveJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> X;
        [ReadOnly] public NativeArray<float> Y;
        [WriteOnly] public NativeArray<float2> Destination;

        public void Execute(int i) => Destination[i] = new float2(X[i], Y[i]);
    }

    /// <summary>
    /// Asks Burst whether this CPU has AVX2 + FMA, FROM INSIDE A BURST JOB.
    ///
    /// This job exists because the obvious version does not work. Read from managed code,
    /// `X86.Avx2.IsAvx2Supported` returns FALSE on a machine that plainly has AVX2 - the
    /// properties are Burst compile-time constants and only evaluate correctly in Burst-compiled
    /// code. A managed-side guard using them silently downgraded variant 8 to variant 5 on every
    /// frame, and the sweep dutifully reported variant 5's number under variant 8's name.
    /// </summary>
    [BurstCompile]
    public struct CpuFeatureJob : IJob
    {
        [WriteOnly] public NativeArray<bool> Supported;

        public void Execute()
        {
            Supported[0] = X86.Avx2.IsAvx2Supported && X86.Fma.IsFmaSupported;
        }
    }
}
