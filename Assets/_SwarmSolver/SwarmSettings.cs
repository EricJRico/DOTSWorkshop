using UnityEngine;
using UnityEngine.Serialization;

namespace Workshop
{
    /// <summary>
    /// Tuning for <see cref="SwarmSolver"/>, the GameObject-per-enemy swarm.
    ///
    /// This is a DIFFERENT algorithm from the boid solver: position-based dynamics with friction
    /// and velocity cohesion, following Weiss et al. 2017 "Position-Based Multi-Agent Dynamics"
    /// section 5.1, with friction from Macklin et al. 2014. The boid solver shares none of it.
    ///
    /// Top-level fields change how the crowd looks and feels. Advanced is the solver itself - the
    /// defaults come from the papers, and changing them trades stability or speed rather than
    /// changing how the crowd behaves.
    /// </summary>
    [CreateAssetMenu(menuName = "Workshop/Swarm Settings")]
    public class SwarmSettings : ScriptableObject
    {
        [Header("Enemies")]

        [Tooltip("How big each enemy is, measured from its centre to its edge. Bigger enemies " +
                 "take up more room and bump into each other sooner. This is only their size - " +
                 "it does not change how far apart they stand.")]
        [FormerlySerializedAs("Radius")]
        public float EnemyRadius = 0.15f;

        [Tooltip("The gap each enemy keeps between its edge and its neighbours' edges. Turn it " +
                 "up and the crowd spreads out; set it to 0 and they pack in shoulder to " +
                 "shoulder. This is space ON TOP OF their size, so changing Enemy Radius does not " +
                 "change the gap and changing the gap does not change their size.")]
        public float Separation = 0.015f;

        [Header("Movement")]

        [Tooltip("How fast enemies move when they have clear space. In a crush they will move " +
                 "slower than this because the crowd is in the way.")]
        [FormerlySerializedAs("Speed")]
        public float MoveSpeed = 2.5f;

        [Tooltip("How quickly enemies change direction. High values snap round instantly and look " +
                 "mechanical. Low values swing round in long curves and make the crowd feel heavy.")]
        [FormerlySerializedAs("VelocityBlend")]
        public float TurnSpeed = 0.0385f;

        [Tooltip("How much space the player clears around itself as it moves through the crowd.")]
        [FormerlySerializedAs("PlayerRadius")]
        public float PlayerPush = 0.5f;

        [Tooltip("A ceiling on how sharply an enemy can change speed. Lower makes the crowd " +
                 "sluggish and heavy to get moving; very high lets enemies snap to full speed and " +
                 "can look twitchy.")]
        public float MaxAcceleration = 50f;

        [Header("Crowd behaviour")]

        [Tooltip("How much effort goes into stopping enemies from standing inside each other. " +
                 "Turn it up for a cleaner-looking crowd; turn it down to save performance and " +
                 "accept more of them clipping through each other.")]
        [FormerlySerializedAs("SolverIterations")]
        public int OverlapCleanup = 6;

        [Tooltip("How much enemies drag along the ones they are touching. Higher makes the crowd " +
                 "move as a single sticky mass; lower lets enemies slide past each other freely.")]
        [FormerlySerializedAs("StaticFriction")]
        public float Grip = 0.5f;

        [Tooltip("How much enemies slow each other down once they are already sliding past. " +
                 "Higher makes the crowd feel like it is wading through treacle.")]
        [FormerlySerializedAs("KineticFriction")]
        public float Drag = 0.3f;

        [Tooltip("How far away an enemy looks to match its neighbours' direction, as a multiple " +
                 "of its own size. Higher makes the crowd move in big co-ordinated shoals; lower " +
                 "makes enemies act more individually.")]
        [FormerlySerializedAs("XsphRadiusFactor")]
        public float FollowTheCrowdRange = 7f;

        [Tooltip("How strongly enemies copy their neighbours' direction. Turn it up and the crowd " +
                 "flows as one; turn it down to zero and every enemy makes its own way.")]
        [FormerlySerializedAs("XsphCAtRadiusOne")]
        public float FollowTheCrowdStrength = 217f;

        [Header("Advanced")]

        [Tooltip("The solver itself. These come from the papers this is built on - changing them " +
                 "trades stability or speed rather than changing how the crowd behaves.")]
        public AdvancedSettings Advanced = new AdvancedSettings();

        /// <summary>
        /// Grouped in a nested class purely so Unity draws it as one collapsed foldout. Values
        /// follow Weiss et al. 2017 section 5.1 unless noted; friction is Macklin et al. 2014.
        /// </summary>
        [System.Serializable]
        public class AdvancedSettings
        {
            [Tooltip("How hard each cleanup pass shoves overlapping enemies apart. Too low and the " +
                     "crowd stays soft and mushy; too high and it jitters. The paper uses 1.2.")]
            public float Relaxation = 1.2f;

            [Tooltip("Extra passes run before the main ones that move enemies out of deep overlaps. " +
                     "Mainly matters at spawn, when enemies can start on top of each other.")]
            public int StabilityIterations = 1;

            [Tooltip("How long one simulated step lasts. The paper uses 1/48 of a second. Shorter " +
                     "steps are steadier and cost more.")]
            public float StepSeconds = 1f / 48f;

            [Tooltip("How many times the whole simulation runs per step. More is steadier under " +
                     "heavy crowding and costs proportionally more. The paper uses 2.")]
            public int Substeps = 2;

            [Tooltip("Threading chunk size. Performance only - it does not change behaviour.")]
            public int BatchSize = 64;
        }

        /// <summary>
        /// Centre-to-centre distance the solver drives enemies apart to. Separation is a GAP
        /// between edges, not this number - see BoidSettings.CollisionDiameter for why.
        /// </summary>
        public float CollisionDiameter => BodyDiameter + Mathf.Max(0f, Separation);

        /// <summary>The physical body. Only drawing and overlap reporting care about this.</summary>
        public float BodyDiameter => EnemyRadius * 2f;

        public float MaxSpeed => MoveSpeed;

        /// <summary>How far the velocity-matching looks, in world units.</summary>
        public float XsphRadius => EnemyRadius * FollowTheCrowdRange;

        /// <summary>
        /// Velocity-matching strength, corrected for range. The smoothing kernel's peak scales
        /// with 1/h^3, so the strength has to scale with h^3 for the effect to stay the same when
        /// the range changes - otherwise widening the range would silently weaken it.
        /// </summary>
        public float XsphC
        {
            get
            {
                const float hPaper = 7f;
                var ratio = XsphRadius / hPaper;
                return FollowTheCrowdStrength * ratio * ratio * ratio;
            }
        }
    }
}
