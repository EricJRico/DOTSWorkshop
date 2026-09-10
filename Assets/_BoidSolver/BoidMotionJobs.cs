using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Workshop
{
    /// <summary>
    /// How far the separation solve actually moved each agent this frame.
    ///
    /// This exists to size the sleeping idea before anything is built for it. The argument is that
    /// in a jammed crowd most interior agents cannot move, so skipping them - while still letting
    /// them act as obstacles - would cost nothing in quality. That is only worth building if the
    /// fraction that cannot move is large DURING FLOW, which is when the solver is expensive. A
    /// settled crowd would obviously say yes and would be the wrong answer.
    ///
    /// Measuring it needs care, because agents have NO IDENTITY across frames: the counting sort
    /// reorders the arrays every frame, so index i is a different agent next frame and a
    /// frame-to-frame displacement is meaningless. Within one frame the order is fixed, so the
    /// comparison is snapshot-after-sort against the same index after the last separation pass -
    /// which is exactly the quantity that matters anyway. It is the solve correction, not the
    /// steering, so an agent flowing freely with the crowd registers as UNMOVED, which is the
    /// honest reading for "would sleeping have skipped useful work".
    ///
    /// Thresholds are fractions of the solve diameter, so they carry over if the agent radius or
    /// the count changes.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct MotionStatsJob : IJobParallelFor
    {
        /// <summary>Per-thread stripe, one cache line, so the counters take plain adds.</summary>
        public const int Stride = 16;
        /// <summary>Bands: &lt;0.01%, &lt;0.1%, &lt;1%, &lt;5%, &gt;=5% of the solve diameter.</summary>
        public const int Bands = 5;

        [ReadOnly] public NativeArray<float2> Before;
        [ReadOnly] public NativeArray<float2> After;
        [Unity.Collections.LowLevel.Unsafe.NativeSetThreadIndex] public int ThreadIndex;
        public float Diameter;
        [NativeDisableParallelForRestriction] [NativeDisableUnsafePtrRestriction]
        public NativeArray<int> Stats;

        public void Execute(int i)
        {
            var moved = math.length(After[i] - Before[i]) / Diameter;
            var s = (int*)Stats.GetUnsafePtr() + ThreadIndex * Stride;
            if (moved < 0.0001f) s[0]++;
            else if (moved < 0.001f) s[1]++;
            else if (moved < 0.01f) s[2]++;
            else if (moved < 0.05f) s[3]++;
            else s[4]++;
        }
    }

    /// <summary>Fold the per-thread stripes into stripe 0.</summary>
    [BurstCompile]
    public struct ReduceMotionJob : IJob
    {
        public NativeArray<int> Stats;
        public int Threads;

        public void Execute()
        {
            for (var t = 1; t < Threads; t++)
                for (var b = 0; b < MotionStatsJob.Bands; b++)
                    Stats[b] += Stats[t * MotionStatsJob.Stride + b];
        }
    }

    /// <summary>Zero every stripe before the counting pass.</summary>
    [BurstCompile]
    public struct ClearMotionJob : IJob
    {
        [WriteOnly] public NativeArray<int> Stats;

        public void Execute()
        {
            for (var i = 0; i < Stats.Length; i++) Stats[i] = 0;
        }
    }

    /// <summary>Straight copy, used to snapshot the predicted positions before the solve.</summary>
    [BurstCompile]
    public struct CopyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Source;
        [WriteOnly] public NativeArray<float2> Destination;

        public void Execute(int i) => Destination[i] = Source[i];
    }
}
