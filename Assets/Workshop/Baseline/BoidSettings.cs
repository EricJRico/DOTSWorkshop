using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Tuning for <see cref="BoidSolver"/>. Defaults are argued, not guessed; the comment on each
    /// field says where the value comes from.
    /// </summary>
    [CreateAssetMenu(menuName = "Workshop/Boid Settings")]
    public class BoidSettings : ScriptableObject
    {
        [Header("Population")]
        public int Count = 5000;

        [Tooltip("Agent radius. To keep the converged crowd the same size on screen, this must " +
                 "scale as 1/sqrt(Count): 50,000 agents at 0.0474 occupy the same disc as 5,000 at 0.15.")]
        public float Radius = 0.15f;

        [Tooltip("Weiss et al. 2017: collision radius is the agent radius expanded 5%.")]
        public float CollisionRadiusScale = 1.05f;

        [Header("Motion")]
        public float Speed = 2.5f;
        [Tooltip("How fast velocity turns toward the direction of the player. 1 = instant.")]
        public float SteerBlend = 0.15f;
        public float MaxSpeedFactor = 1f;
        public float PlayerRadius = 0.5f;

        [Header("Separation solve")]
        [Tooltip("Full pipeline repeats per frame, each advancing dt/Substeps. Macklin et al. 2019 " +
                 "'Small Steps in Physics Simulation': for a fixed budget, substeps converge far " +
                 "better than iterations, because each substep re-linearises the contact set. " +
                 "Measured here: at 50,000 agents, iterations plateau (12 is worse than 8) while " +
                 "substeps keep paying, because what limits quality is displacement per step " +
                 "relative to the collision diameter, and only substepping shrinks that.")]
        public int Substeps = 1;

        [Tooltip("Jacobi position-correction passes per frame over one grid build. " +
                 "Weiss ran 14; the whole point of this solver is that a capped, sorted scan needs far fewer.")]
        public int Iterations = 2;

        [Tooltip("Successive over-relaxation. Bender/Muller/Macklin EG2015 give 1 <= omega <= 2.")]
        public float Omega = 1.4f;

        [Tooltip("Safety valve on neighbours processed per agent, not a quality knob. At close-pack " +
                 "density a 3x3 scan at cell = collision diameter yields ~6 in-radius neighbours " +
                 "(hex packing, and the number Ballerini 2008 measured in starlings: 6.5 +/- 0.9), " +
                 "so this cap is almost never reached. It bounds the cost of a pathological pile-up. " +
                 "Unreal Mass caps at 6 because it gathers 24+ candidates from 27 cells; our grid is " +
                 "tight enough that we do not have to throw good neighbours away.")]
        public int MaxNeighbours = 8;

        [Header("Avoidance (Weiss et al. 2017 section 4.5)")]
        [Tooltip("Remove the component of velocity that drives into the contact normal, so a blocked " +
                 "agent slides around the pack instead of pushing into it. One dot product in Finalize.")]
        public bool TangentialSlide = true;

        [Tooltip("Verlet skin. Neighbours are gathered out to CollisionDiameter * (1 + this) so " +
                 "the cached list survives all the iterations. Costs a wider scan; without it the " +
                 "cache silently loses contacts that form mid-solve.")]
        public float GatherSkin = 0.5f;

        [Tooltip("Cache each agent's neighbours in a list and have the separation passes read it " +
                 "instead of re-walking the grid. MEASURED WORSE, left here because the result is " +
                 "counter-intuitive: it is 1.5x faster (5.16 ms vs 7.98 ms wall at 50k on 4 " +
                 "workers) but leaves 8x the overlap (242 pairs vs 30), because the contact set " +
                 "changes too much within a frame for a cached list to stay valid. Re-gathering " +
                 "more often does not rescue it - every 1 pass instead of every 8 was both slower " +
                 "(16.50 ms) and worse (784 pairs).")]
        public bool CacheNeighbours;

        [Tooltip("Rebuild the neighbour list every N separation iterations. 1 = rebuild every pass " +
                 "(most accurate, most expensive); Iterations = build once per frame (cheapest). " +
                 "Pairs that come into contact mid-solve beyond the skin are invisible until the " +
                 "next rebuild, which is why gathering once plateaus no matter how many passes run.")]
        public int GatherEvery = 4;

        [Header("Congestion gate")]
        [Tooltip("Neighbour count at which an agent starts losing its toward-target drive.")]
        public float CrowdFree = 4f;
        [Tooltip("Neighbour count at which toward-target drive is fully removed. Set CrowdFull <= " +
                 "CrowdFree to disable the gate entirely.")]
        public float CrowdFull = 7f;

        [Tooltip("0 = off. 1..8 run stripped variants of the separation job ALONGSIDE the real " +
                 "solve, writing to a throwaway buffer, so the inner loop's cost can be split by " +
                 "subtraction: 1 = dispatch+read+write, 2 = +grid lookup, 3 = +candidate walk, " +
                 "4 = +neighbour load, 5 = +lengthsq, 6 = +reject branch, 7 = reject as a mask " +
                 "instead of a branch, 8 = mask plus the compaction store. The simulation stays " +
                 "correct; only the frame time is inflated by whichever stage is running.")]
        public int AblationStage;

        [Header("Separation implementation")]
        [Tooltip("0 = SeparateJob, one loop that tests and accumulates per candidate. " +
                 "1 = SeparateCompactJob, which splits it: one loop records which candidates " +
                 "survive the distance test, a second does the contact math over the survivors. " +
                 "MEASURED: 0.844 -> 0.631 ms/dispatch, solver wall 7.9 -> 6.0 ms at 50k on 4 " +
                 "workers, and the positions are bit-identical (max delta exactly 0). Left as a " +
                 "switch because the reason is worth showing: rewriting the reject branchless in " +
                 "place buys nothing at all (ablation stage 7 == stage 6). The cost is the " +
                 "conditional accumulation, not the branch. " +
                 "2 = SeparateSimdJob, the compact job walking four candidates at a time. " +
                 "MEASURED WORSE at 0.879: the scalar loop was not latency-bound, and blocking " +
                 "four candidates together removes the overlap that was hiding the cursor "  +
                 "updates. Kept as the negative result.")]
        public int SeparateVariant = 1;

        [Tooltip("After the crowd converges, time both separation variants back to back on the " +
                 "SAME crowd state and log the result. Timing them on separate runs is no good - " +
                 "the crowd is never in the same place twice.")]
        public bool SeparateBenchmark;

        [Header("Verification")]
        [Tooltip("Count genuinely interpenetrating pairs (centres closer than 2*Radius) every " +
                 "frame, on the solved positions. This is the solver's correctness check, so it " +
                 "is on by default; it costs one extra grid build.")]
        public bool CheckOverlap = true;

        [Header("Jobs")]
        public int BatchSize = 128;

        [Tooltip("Cell budget as a multiple of Count. If the crowd's bounding box needs more cells " +
                 "than this, the cell grows instead - which costs candidates, never correctness.")]
        public int CellsPerAgent = 4;

        public float CollisionDiameter => Radius * 2f * CollisionRadiusScale;
        public float MaxSpeed => Speed * MaxSpeedFactor;
    }
}
