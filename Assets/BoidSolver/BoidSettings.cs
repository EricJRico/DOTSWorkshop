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

        [Tooltip("The gap each enemy keeps between its edge and its neighbours' edges. Turn it up " +
                 "and the crowd spreads out; set it to 0 and they pack in shoulder to shoulder. " +
                 "This is space ON TOP OF their size, so changing Enemy Radius does not change " +
                 "the gap and changing the gap does not change their size.")]
        [FormerlySerializedAs("SeparationDistance")]
        public float Separation = 0.033f;

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

        /// <summary>
        /// Centre-to-centre distance the solver drives enemies apart to. What every job calls
        /// Diameter.
        ///
        /// Separation is a GAP between edges, not this number. An earlier version exposed the
        /// centre distance directly and it was unusable: setting a radius of 0.5 and a separation
        /// of 0.25 asked for centres a quarter apart inside bodies a full unit wide, so the crowd
        /// piled into itself. Adding the gap to the body means the two settings genuinely do not
        /// affect each other, which is the whole point of splitting them.
        /// </summary>
        public float CollisionDiameter => BodyDiameter + Mathf.Max(0f, Separation);

        /// <summary>The physical body. Only the drawing and the overlap metric care about this.</summary>
        public float BodyDiameter => EnemyRadius * 2f;

        public float MaxSpeed => MoveSpeed;

#if UNITY_EDITOR
        void OnValidate()
        {
            if (Separation < 0f)
                Debug.LogWarning(
                    $"{name}: Separation is a gap and cannot be negative - treating it as 0, " +
                    "which packs enemies shoulder to shoulder.", this);
        }
#endif
    }
}
