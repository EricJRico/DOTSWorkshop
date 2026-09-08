using UnityEngine;
using UnityEngine.Serialization;

namespace Workshop
{
    /// <summary>
    /// Tuning for <see cref="BoidSolver"/>.
    ///
    /// The top-level fields change how the crowd looks and feels. Everything under Advanced is
    /// performance and solver internals - already measured, already at its best value - and
    /// changing it costs speed or quality rather than changing how the crowd behaves.
    /// </summary>
    [CreateAssetMenu(menuName = "Workshop/Boid Settings")]
    public class BoidSettings : ScriptableObject
    {
        [Header("Enemies")]

        [Tooltip("How many enemies there are.")]
        [FormerlySerializedAs("Count")] [FormerlySerializedAs("AgentCount")]
        public int EnemyCount = 5000;

        [Tooltip("How big each enemy is, measured from its centre to its edge. Bigger enemies " +
                 "take up more room and start bumping into each other sooner. This is only their " +
                 "size - it does not change how far apart they stand.")]
        [FormerlySerializedAs("Radius")] [FormerlySerializedAs("AgentRadius")]
        public float EnemyRadius = 0.15f;

        [Tooltip("How much room each enemy keeps between itself and its neighbours. Turn it up " +
                 "and the crowd spreads out; turn it down and it packs in tighter. If you set it " +
                 "smaller than an enemy is wide, they will visibly overlap.")]
        [FormerlySerializedAs("SeparationDistance")]
        public float Separation = 0.128f;

        [Header("Movement")]

        [Tooltip("How fast enemies move when they have clear space. In a crush they will move " +
                 "slower than this because the crowd is in the way.")]
        [FormerlySerializedAs("Speed")] [FormerlySerializedAs("MoveSpeed")]
        public float MoveSpeed = 2.5f;

        [Tooltip("How quickly enemies change direction. High values snap round instantly and look " +
                 "mechanical. Low values swing round in long curves and make the crowd feel heavy.")]
        [FormerlySerializedAs("SteerBlend")] [FormerlySerializedAs("TurnResponsiveness")]
        public float TurnSpeed = 0.15f;

        [Tooltip("How much space the player clears around itself as it moves through the crowd.")]
        [FormerlySerializedAs("PlayerRadius")] [FormerlySerializedAs("PlayerPushRadius")]
        public float PlayerPush = 0.5f;

        [Header("Crowd behaviour")]

        [Tooltip("How much effort goes into stopping enemies from standing inside each other. " +
                 "Turn it up for a cleaner-looking crowd; turn it down to save performance and " +
                 "accept more of them clipping through each other. Below 6 they start to overlap " +
                 "noticeably.")]
        [FormerlySerializedAs("Iterations")] [FormerlySerializedAs("SolverPasses")]
        public int OverlapCleanup = 6;

        [Tooltip("Enemies who cannot get through slip around whatever is blocking them instead of " +
                 "shoving straight into it. Turn it off and the crowd piles up and grinds.")]
        [FormerlySerializedAs("TangentialSlide")] [FormerlySerializedAs("SlideAroundBlockers")]
        public bool SlidePastOthers = true;

        [Tooltip("How many neighbours it takes before an enemy starts to give up on reaching the " +
                 "player. Lower means they surrender to the crush sooner.")]
        [FormerlySerializedAs("CrowdFree")] [FormerlySerializedAs("CrowdedAt")]
        public float StartsGivingUpAt = 4f;

        [Tooltip("How many neighbours it takes before an enemy stops trying entirely and just " +
                 "gets carried along by the crowd. Set this to the same as Starts Giving Up At, " +
                 "or lower, and enemies will never give up.")]
        [FormerlySerializedAs("CrowdFull")] [FormerlySerializedAs("FullyBlockedAt")]
        public float GivesUpEntirelyAt = 7f;

        [Header("Advanced")]

        [Tooltip("Performance and solver internals. These have all been measured and are already " +
                 "at their best values - changing them trades away speed or quality rather than " +
                 "changing how the crowd behaves.")]
        public AdvancedSettings Advanced = new AdvancedSettings();

        /// <summary>
        /// Grouped in a nested class purely so Unity draws it as one collapsed foldout. These are
        /// engineering knobs - see docs/solver-perf-handoff.md for what each was measured at.
        /// </summary>
        [System.Serializable]
        public class AdvancedSettings
        {
            [Tooltip("How hard each cleanup pass shoves overlapping enemies apart. Too low and " +
                     "the crowd stays soft and mushy; too high and it jitters. 1.8 measured best.")]
            [FormerlySerializedAs("Omega")]
            public float Relaxation = 1.8f;

            [Tooltip("Most neighbours one enemy will deal with at once. A safety valve for " +
                     "pile-ups, not a quality setting - the crowd averages about six.")]
            public int MaxNeighbours = 16;

            [Tooltip("Which separation implementation runs. 8 is the fast one and what ships. " +
                     "3 and 5 are older, slower versions kept only so performance can be compared " +
                     "against them in the same play session. On a CPU without AVX2, such as Apple " +
                     "Silicon, 8 falls back to 5 automatically.")]
            public int SeparateVariant = 8;

            [Tooltip("Threading chunk size for the per-enemy work. Measured: 64 through 512 all " +
                     "land inside the noise, so this is not a lever.")]
            [FormerlySerializedAs("BatchSize")] [FormerlySerializedAs("AgentBatchSize")]
            public int AgentBatchSize = 128;

            [Tooltip("Threading chunk size for the separation passes, counted in grid cells " +
                     "rather than enemies. Measured: not a lever either.")]
            [FormerlySerializedAs("ColourBatch")] [FormerlySerializedAs("CellBatchSize")]
            public int CellBatchSize = 64;

            [Tooltip("How much lookup grid to allocate, per enemy. If the crowd spreads wider " +
                     "than the budget allows, the grid coarsens itself, which costs speed but " +
                     "never correctness. Raise it only if the crowd covers a much bigger area.")]
            public int CellsPerAgent = 4;
        }

        /// <summary>The distance the solver drives enemies apart to. What every job calls Diameter.</summary>
        public float CollisionDiameter => Separation;

        /// <summary>The physical body. Only the drawing and the overlap metric care about this.</summary>
        public float BodyDiameter => EnemyRadius * 2f;

        public float MaxSpeed => MoveSpeed;

#if UNITY_EDITOR
        void OnValidate()
        {
            // Not clamped, just reported: overlapping on purpose is a legitimate thing to ask for
            // and the overlap readout will show it honestly.
            if (Separation < BodyDiameter)
                Debug.LogWarning(
                    $"{name}: Separation ({Separation:F4}) is smaller than an enemy is wide " +
                    $"({BodyDiameter:F4}), so enemies will overlap.", this);
        }
#endif
    }
}
