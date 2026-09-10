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
    /// The kill experiment for the SIMD redesign, and nothing more. It measures ONLY the candidate
    /// walk, writes a throwaway buffer, and cannot affect the simulation.
    ///
    /// Why this shape. The Burst optimisation remarks say the real loop has TWO stacked blockers:
    /// the compaction index (`n` is both a saturating reduction and a store address) and `float2`
    /// itself (LLVM will not widen a &lt;2 x float&gt; body into &lt;N x &lt;2 x float&gt;&gt;). Removing either
    /// alone changes nothing. This job removes both at once - positions arrive as two float
    /// streams, and there is no candidate list - and hand-writes the 8-wide block rather than
    /// hoping the auto-vectoriser fires, because at a 3.70-candidate run it never reaches its own
    /// &gt;= 8 guard.
    ///
    /// Three lanes of masking, and the third is a CORRECTNESS requirement, not tidiness:
    ///   r2 &lt; d2        the actual test
    ///   k &lt; end        the over-read past the end of the row run. Cells two apart are the SAME
    ///                  COLOUR and are being written concurrently by other work items, so an
    ///                  unmasked lane can read a value mid-update. Masked, the read is harmless;
    ///                  unmasked it would be a race that the colouring exists to prevent.
    ///   k != i         the self test
    ///
    /// Counting is the point: <see cref="Candidates"/> receives the number of IN-RANGE lanes, which
    /// must average the same 11.09 the scalar walk measures. If it does not, the mask is wrong and
    /// the timing means nothing - check that before believing any speed number.
    ///
    /// Note this does strictly MORE work than <see cref="ColouredWalkRJob"/>, which has no float
    /// maths in its body at all. So beating 0.278 ms is not the bar; the bar is beating it by
    /// enough to pay for the distance test it also performs.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct ColouredWalkSimdJob : IJobParallelForDefer
    {
        public const int MaxRadius = SeparateColouredRJob.MaxRadius;

        [ReadOnly] public NativeArray<int> Cells;
        [ReadOnly] public NativeArray<int> Offset;
        [ReadOnly] public NativeArray<GridInfo> Info;
        /// <summary>Positions split into two float streams. The whole point of the experiment.</summary>
        [ReadOnly] public NativeArray<float> PredX;
        [ReadOnly] public NativeArray<float> PredY;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Sink;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> Candidates;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> Hits;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> Normals;
        public float Diameter;
        public float Omega;
        public int ScanRadius;

        public void Execute(int m)
        {
            // Positive `if` WRAPPING the body. Burst's CPU-feature analysis is per
            // block: an early return does not establish the feature set, and nor does a
            // compound `a && b` condition - both report `block only supports None` and
            // fail the Arm build. Measurement probe, so non-x86 is a no-op.
            if (Avx2.IsAvx2Supported)
            {
            // and an early return does NOT satisfy it - unguarded, this breaks the build on Apple
            var g = Info[0];
            var b = Cells[m];
            var px = (float*)PredX.GetUnsafeReadOnlyPtr();
            var py = (float*)PredY.GetUnsafeReadOnlyPtr();
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
                var vxi = Avx.mm256_set1_ps(px[i]);
                var vyi = Avx.mm256_set1_ps(py[i]);
                var vi = Avx.mm256_set1_epi32(i);

                var sumx = Avx.mm256_setzero_ps();
                var sumy = Avx.mm256_setzero_ps();
                var nrmx = Avx.mm256_setzero_ps();
                var nrmy = Avx.mm256_setzero_ps();

                var cand = 0;
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
                        // k < end : masks the over-read into cells of the SAME COLOUR that other
                        // work items are writing right now. Not optional - see the class comment.
                        var inRange = Avx2.mm256_cmpgt_epi32(vend, k);
                        var isSelf = Avx2.mm256_cmpeq_epi32(k, vi);
                        var notSelf = Avx.mm256_andnot_ps(isSelf, inRange);

                        // Counted INCLUDING self so it matches the scalar walk's 11.09 exactly.
                        cand += math.countbits((uint)Avx.mm256_movemask_ps(inRange));

                        var near = Avx.mm256_cmp_ps(r2, vd2, (int)Avx.CMP.LT_OQ);
                        var keep = Avx.mm256_and_ps(near, notSelf);
                        found += math.countbits((uint)Avx.mm256_movemask_ps(keep));

                        // One raw vrsqrtps, no Newton step - 12 bits is far below what the
                        // penetration metric can resolve. Clamp first so a zero lane cannot make inf.
                        var inv = Avx.mm256_rsqrt_ps(Avx.mm256_max_ps(r2, vEps));
                        var dist = Avx.mm256_mul_ps(r2, inv);
                        var nx = Avx.mm256_mul_ps(dx, inv);
                        var ny = Avx.mm256_mul_ps(dy, inv);
                        var t = Fma.mm256_fmadd_ps(vNegHalf, dist, vHalfD);

                        // Mask every operand BEFORE the fma: 0 * NaN is NaN, so masking the
                        // product afterwards would not stop a torn read poisoning the sum.
                        t = Avx.mm256_and_ps(t, keep);
                        nx = Avx.mm256_and_ps(nx, keep);
                        ny = Avx.mm256_and_ps(ny, keep);

                        sumx = Fma.mm256_fmadd_ps(t, nx, sumx);
                        sumy = Fma.mm256_fmadd_ps(t, ny, sumy);
                        nrmx = Avx.mm256_add_ps(nrmx, nx);
                        nrmy = Avx.mm256_add_ps(nrmy, ny);
                    }
                }

                Candidates[i] = cand;
                Hits[i] = found;
                var div = math.max(found, 1);
                // Horizontal reduces inline: a helper is its own block and fails the Arm build.
                var hx = Sse.add_ps(Avx.mm256_castps256_ps128(sumx), Avx.mm256_extractf128_ps(sumx, 1));
                hx = Sse.add_ps(hx, Sse.movehl_ps(hx, hx));
                hx = Sse.add_ss(hx, Sse.shuffle_ps(hx, hx, 0x55));
                var hy = Sse.add_ps(Avx.mm256_castps256_ps128(sumy), Avx.mm256_extractf128_ps(sumy, 1));
                hy = Sse.add_ps(hy, Sse.movehl_ps(hy, hy));
                hy = Sse.add_ss(hy, Sse.shuffle_ps(hy, hy, 0x55));
                var hnx = Sse.add_ps(Avx.mm256_castps256_ps128(nrmx), Avx.mm256_extractf128_ps(nrmx, 1));
                hnx = Sse.add_ps(hnx, Sse.movehl_ps(hnx, hnx));
                hnx = Sse.add_ss(hnx, Sse.shuffle_ps(hnx, hnx, 0x55));
                var hny = Sse.add_ps(Avx.mm256_castps256_ps128(nrmy), Avx.mm256_extractf128_ps(nrmy, 1));
                hny = Sse.add_ps(hny, Sse.movehl_ps(hny, hny));
                hny = Sse.add_ss(hny, Sse.shuffle_ps(hny, hny, 0x55));
                var sx = Sse.cvtss_f32(hx) * (Omega / div);
                var sy = Sse.cvtss_f32(hy) * (Omega / div);
                Sink[i] = new float2(px[i] + sx, py[i] + sy);
                Normals[i] = new float2(Sse.cvtss_f32(hnx), Sse.cvtss_f32(hny));
            }
            }
        }

    }

    /// <summary>Deinterleave float2 positions into the two float streams the SIMD walk reads.</summary>
    [BurstCompile]
    public struct DeinterleaveJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Source;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float> X;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float> Y;

        public void Execute(int i)
        {
            var p = Source[i];
            X[i] = p.x;
            Y[i] = p.y;
        }
    }
}
