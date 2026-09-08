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
                 "'Small Steps in Physics Simulation' argues that for a fixed budget substeps " +
                 "converge better than iterations. THAT DOES NOT REPRODUCE HERE - substeps are " +
                 "both worse and slower. Measured at 50,000 agents with Time.captureDeltaTime " +
                 "pinned to 1/60, quality as mean penetration against the SOLVE diameter " +
                 "(wall ms / meanPen): "                                                        +
                 "8x1 = 6.9 / 4.7%, 4x2 = 7.3 / 7.05%, 2x4 = 8.5 / 6.97%, "                     +
                 "6x1 = 5.9 / 9.0%, 5x1 = 5.4 / 10.7%, 12x1 = 9.0 / 4.7%, 16x1 = 11.2 / 3.5%. " +
                 "All three 8-pass configs cost the same 8 separation passes, and the one that " +
                 "spends them as 8 iterations over ONE grid build is 35% better than either " +
                 "substepped split as well as the cheapest - substepping only adds grid " +
                 "rebuilds. 8x1 is the knee: 6 passes doubles the penetration and 12 buys " +
                 "nothing. Leave it at 1. " +
                 "An earlier version of this table said quality tracked total passes only and " +
                 "the split did not matter. That was measured with the old overlap test, which " +
                 "counted against 2*Radius while the solver targets 2*Radius*1.35 - 35% of " +
                 "slack, so every config scored ~400 and the metric could barely fail.")]
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

        [Tooltip("Cache each agent neighbours in a list and have the separation passes read it " +
                 "instead of re-walking the grid. MEASURED NOT WORTH IT, and the reason changed. " +
                 "The old note said it was 1.5x faster but left 8x the overlap. The overlap half " +
                 "was an artefact: GatherJob wrote the GATHER count into NeighbourCount, and it " +
                 "gathers at diameter * (1 + skin), so FinalizeJob congestion gate saw ~2.25x the " +
                 "neighbours and stripped the seek drive far harder on this path than on the grid " +
                 "path. It was comparing two different simulations. With the count fixed, quality " +
                 "matches: 6.98% mean penetration against 6.61% uncached, inside the run-to-run " +
                 "noise. But the speed win is gone too, because the cost it existed to avoid was " +
                 "the old branchy grid walk, and SeparateCompactJob already removed that. " +
                 "Measured wall: 6.76 ms at GatherEvery 8 against 6.96 ms uncached - 3%, for 3.2 " +
                 "MB and an extra dispatch. Rebuilding more often only costs: 8.03 ms every 4, " +
                 "10.65 ms every 2.")]
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

        [Tooltip("Floor on the divisor used to average the contact corrections. The solver divides " +
                 "by the live contact count, which is the safe Jacobi averaging but under-corrects " +
                 "the dense clusters that dominate the penetration metric. A floor lets a sparse " +
                 "agent take a fuller step while a buried one stays damped. 1 = original behaviour. " +
                 "Only the coloured Gauss-Seidel path reads this.")]
        public int MinDivisor = 1;


        [Header("Jobs")]
        public int BatchSize = 128;

        [Tooltip("Inner-loop batch for the coloured Gauss-Seidel passes, counted in CELLS not " +
                 "agents - a cell holds about 1.3 agents at converged density, so 64 cells is " +
                 "roughly 80 agents.")]
        public int ColourBatch = 64;

        [Tooltip("Cell budget as a multiple of Count. If the crowd's bounding box needs more cells " +
                 "than this, the cell grows instead - which costs candidates, never correctness.")]
        public int CellsPerAgent = 4;

        public float CollisionDiameter => Radius * 2f * CollisionRadiusScale;
        public float MaxSpeed => Speed * MaxSpeedFactor;
    }
}
