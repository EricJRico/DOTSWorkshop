using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// All swarm tuning in one asset. Values follow Weiss et al. 2017 "Position-Based
    /// Multi-Agent Dynamics" section 5.1 unless marked otherwise. Nothing in code is a literal.
    /// </summary>
    [CreateAssetMenu(menuName = "Workshop/Swarm Settings")]
    public class SwarmSettings : ScriptableObject
    {
        [Header("Agents")]
        public float Speed = 2.5f;
        public float Radius = 0.15f;
        [Tooltip("Paper: radius expanded 5% during collision checks.")]
        public float CollisionRadiusScale = 1.05f;
        public float PlayerRadius = 0.5f;

        [Header("Time stepping (paper: 1/48 s, 2 substeps per frame)")]
        public float StepSeconds = 1f / 48f;
        public int Substeps = 2;

        [Header("Solver (paper: 1 stability, 6 solver, omega 1.2, blend 0.0385)")]
        public int StabilityIterations = 1;
        public int SolverIterations = 6;
        public float Omega = 1.2f;
        public float VelocityBlend = 0.0385f;

        [Header("Friction (Macklin 2014 eq. 24; coefficients not given in either paper)")]
        public float StaticFriction = 0.5f;
        public float KineticFriction = 0.3f;

        [Header("Cohesion, XSPH (paper: h = 7 radii, c = 217 at radius 1; c scales with h^3)")]
        public float XsphRadiusFactor = 7f;
        public float XsphCAtRadiusOne = 217f;

        [Header("Limits (paper clamps speed and acceleration; values not given)")]
        public float MaxSpeedFactor = 1f;
        public float MaxAcceleration = 50f;

        [Header("Jobs")]
        public int BatchSize = 64;

        public float CollisionDiameter => Radius * 2f * CollisionRadiusScale;
        public float XsphRadius => Radius * XsphRadiusFactor;

        /// <summary>Poly6 peak scales with 1/h^3, so c must scale with h^3 to keep c*W the same.</summary>
        public float XsphC
        {
            get
            {
                var hPaper = 7f;
                var ratio = XsphRadius / hPaper;
                return XsphCAtRadiusOne * ratio * ratio * ratio;
            }
        }
    }
}
