using UnityEngine;
using UnityEngine.Serialization;

namespace Workshop
{
    /// <summary>
    /// Tuning for <see cref="BoidSolver"/>.
    ///
    /// The top-level fields are the ones worth changing to make the crowd look and feel different.
    /// Everything under Advanced is performance and solver internals: it has all been measured, the
    /// defaults are the measured best, and changing it will cost speed or quality rather than
    /// change how the crowd behaves.
    /// </summary>
    [CreateAssetMenu(menuName = "Workshop/Boid Settings")]
    public class BoidSettings : ScriptableObject
    {
        [Header("Crowd")]

        [Tooltip("How many agents to spawn. Cost is roughly linear in this - 50,000 is the " +
                 "benchmark. Changing it does not change how the crowd behaves, only how much of " +
                 "it there is; if you want the crowd to cover the same ground on screen, scale " +
                 "Agent Radius and Separation Distance by 1/sqrt of the change.")]
        [FormerlySerializedAs("Count")]
        public int AgentCount = 5000;

        [Tooltip("How big one agent is. This is what you SEE - it sets the drawn size - and it is " +
                 "what the overlap readout calls a body. Bigger agents look chunkier and start " +
                 "overlapping each other sooner. It does NOT change how far apart they stand; " +
                 "that is Separation Distance.")]
        [FormerlySerializedAs("Radius")]
        public float AgentRadius = 0.15f;

        [Tooltip("How far apart agents stand, centre to centre, in world units. Turn it up and the " +
                 "crowd spreads out and takes more room; turn it down and it packs tighter. " +
                 "Independent of Agent Radius on purpose - set it below twice the radius and the " +
                 "bodies will visibly overlap, which is allowed and the overlap readout will show " +
                 "it.")]
        public float SeparationDistance = 0.128f;

        [Header("Movement")]

        [Tooltip("How fast agents move toward the target, in world units per second. This is a " +
                 "flat-out speed - a jammed agent will not reach it because the crowd is in the way.")]
        [FormerlySerializedAs("Speed")]
        public float MoveSpeed = 2.5f;

        [Tooltip("How sharply agents turn toward the target. 1 turns on a coin and looks robotic; " +
                 "small values make wide, drifting arcs and a crowd that takes a while to change " +
                 "direction. Around 0.05 reads as heavy and momentum-driven.")]
        [FormerlySerializedAs("SteerBlend")]
        public float TurnResponsiveness = 0.15f;

        [Tooltip("How far the player shoves agents away, in world units. Turn it up for a bigger " +
                 "bubble of clear space around the player. Purely cosmetic - it does not affect " +
                 "how agents treat each other.")]
        [FormerlySerializedAs("PlayerRadius")]
        public float PlayerPushRadius = 0.5f;

        [Header("Crowd behaviour")]

        [Tooltip("How hard the solver works each frame to push overlapping agents apart. More " +
                 "passes means a tidier crowd with fewer agents clipping through each other, and " +
                 "costs proportionally more time - each pass is about 0.4 ms at 50,000 agents. " +
                 "Six is the knee: five nearly doubles the number of overlapping bodies, and more " +
                 "than six buys very little.")]
        [FormerlySerializedAs("Iterations")]
        public int SolverPasses = 6;

        [Tooltip("Blocked agents slide around whatever is in front of them instead of shoving " +
                 "straight into it. Off, the crowd piles up and shoves; on, it flows around " +
                 "obstacles and looks far more deliberate.")]
        [FormerlySerializedAs("TangentialSlide")]
        public bool SlideAroundBlockers = true;

        [Tooltip("How many neighbours an agent tolerates before it starts giving up on reaching " +
                 "the target. Lower makes agents surrender to the crush sooner, so the crowd " +
                 "settles rather than grinding forward. Has no effect unless Fully Blocked At is " +
                 "higher than this.")]
        [FormerlySerializedAs("CrowdFree")]
        public float CrowdedAt = 4f;

        [Tooltip("How many neighbours it takes before an agent stops pushing toward the target " +
                 "entirely and just gets carried by the crowd. Set this at or below Crowded At to " +
                 "switch the whole behaviour off and have every agent push regardless.")]
        [FormerlySerializedAs("CrowdFull")]
        public float FullyBlockedAt = 7f;

        [Header("Advanced")]

        [Tooltip("Performance and solver internals. All measured, all already at their best " +
                 "value. Changing anything here trades speed or quality; it will not make the " +
                 "crowd behave differently.")]
        public AdvancedSettings Advanced = new AdvancedSettings();

        /// <summary>
        /// Grouped in a nested class purely so Unity draws it as one collapsed foldout. Nothing in
        /// here is a design knob - see docs/solver-perf-handoff.md for what each was measured at.
        /// </summary>
        [System.Serializable]
        public class AdvancedSettings
        {
            [Tooltip("How aggressively each solver pass corrects an overlap. Below 1 under-corrects " +
                     "and the crowd stays mushy; above 2 overshoots and jitters. 1.8 measured best " +
                     "- lower values were worse at every setting tried.")]
            public float Relaxation = 1.8f;

            [Tooltip("Ceiling on how many neighbours one agent will resolve against in a pass. A " +
                     "safety valve for pile-ups, not a quality knob: the crowd averages about 6, " +
                     "so this is almost never reached. The shipping solver ignores it entirely.")]
            public int MaxNeighbours = 16;

            [Tooltip("Which separation implementation runs. 8 is the one that ships - 8-wide SIMD " +
                     "over split position streams, 2.25 ms at 50,000 agents. 3 and 5 are the older " +
                     "scalar versions, kept because every performance measurement is made by " +
                     "comparing against them in the same play session. Falls back to 5 on a CPU " +
                     "without AVX2, such as Apple Silicon.")]
            public int SeparateVariant = 8;

            [Tooltip("Work-chunk size for the per-agent jobs. Measured: 64, 128, 256 and 512 all " +
                     "land inside the noise. Not a lever.")]
            [FormerlySerializedAs("BatchSize")]
            public int AgentBatchSize = 128;

            [Tooltip("Work-chunk size for the separation passes, counted in grid CELLS rather than " +
                     "agents - a cell holds about 1.3 agents. Measured: 16, 32 and 64 are within " +
                     "noise of each other, larger is slightly worse.")]
            [FormerlySerializedAs("ColourBatch")]
            public int CellBatchSize = 64;

            [Tooltip("How much grid to allocate, as a multiple of Agent Count. If the crowd spreads " +
                     "wider than the budget the grid coarsens itself, which costs speed but never " +
                     "correctness. Raise it only if the crowd covers a much larger area.")]
            public int CellsPerAgent = 4;
        }

        /// <summary>The distance the solver drives agents apart to. What every job calls Diameter.</summary>
        public float CollisionDiameter => SeparationDistance;

        /// <summary>The physical body. Only the drawing and the overlap metric care about this.</summary>
        public float BodyDiameter => AgentRadius * 2f;

        public float MaxSpeed => MoveSpeed;

#if UNITY_EDITOR
        void OnValidate()
        {
            // Not clamped, just reported: a separation under the body diameter is a legitimate
            // thing to ask for and the overlap readout will show it honestly.
            if (SeparationDistance < BodyDiameter)
                Debug.LogWarning(
                    $"{name}: Separation Distance ({SeparationDistance:F4}) is under the body " +
                    $"diameter ({BodyDiameter:F4}), so agents will overlap on purpose.", this);
        }
#endif
    }
}
