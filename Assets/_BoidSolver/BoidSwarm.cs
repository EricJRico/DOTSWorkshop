using System.Diagnostics;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Workshop
{
    /// <summary>
    /// Drives <see cref="BoidSolver"/> and draws the result. No enemy GameObjects exist; the whole
    /// swarm is one <see cref="Graphics.RenderMeshIndirect"/> call reading the solver's instance
    /// array straight out of native memory.
    /// </summary>
    public class BoidSwarm : MonoBehaviour
    {
        [Header("Setup")]
        public BoidSettings Settings;
        public Transform Player;
        [Tooltip("Job worker threads. 0 leaves Unity default, one per logical core. Set to 4 to " +
                 "measure what a modest machine would see.")]
        public int WorkerThreads;

        [Header("Spawn ring (outside the camera view)")]
        public float SpawnMinRadius = 34f;
        public float SpawnMaxRadius = 44f;
        public int Seed = 1;

        [Header("Draw")]
        public Mesh Mesh;
        public Material Material;
        public bool Draw = true;
        public Color ColorSlow = new Color(0.20f, 0.45f, 1.00f);
        public Color ColorMid = new Color(1.00f, 0.90f, 0.20f);
        public Color ColorFast = new Color(0.95f, 0.15f, 0.15f);

        // Everything below is measurement. None of it changes how the crowd moves except
        // AutoTarget, which changes where it is TOLD to move, and none of it would ship.
        //
        // It all lives on this component rather than on the BoidSettings asset on purpose:
        // a ScriptableObject edited at runtime keeps the change after play mode exits, with
        // nothing in git to show for it, which is how Iterations silently became 0 mid-session.
        // Fields on a component revert when play mode ends.

        [Header("Debug - scenario")]
        [Tooltip("Drive the target on a circle instead of following the player, so the crowd keeps " +
                 "flowing. A crowd converged on a stationary player reaches a static equilibrium, " +
                 "which is the easy case and not what the solver has to survive - and comparing a " +
                 "settled crowd against a flowing one is how two runs stop meaning anything.")]
        public bool AutoTarget;
        public float AutoRadius = 8f;
        public float AutoSpeed = 1.2f;

        [Header("Debug - readout")]
        public bool ShowStats = true;
        [Tooltip("Count pairs closer than the constraint the solver targets, every frame, on the " +
                 "solved positions. Nothing in the simulation reads it - it only feeds the readout. " +
                 "MEASURED 1.58 ms of a 5.4 ms frame at 50,000 agents, because it needs a SECOND " +
                 "full grid build on the solved positions and then scans at the solve diameter, " +
                 "which fires on ~137,000 pairs. Off takes the frame to 4.02 ms, so quote 4.02 as " +
                 "the solver and 5.4 as the cost of proving it correct.")]
        public bool CheckOverlap;
        [Tooltip("Log a BOID| line every second after the crowd converges. For player runs.")]
        public bool LogBenchmark;
        public float BenchmarkWarmupSeconds = 70f;
        public int BenchmarkSamples = 10;

        [Tooltip("Scan radius for SeparateVariant 4. Cell = collision diameter / R and the scan " +
                 "reaches R cells, so the reach is one diameter at any R and no contact is lost. " +
                 "R=2 sweeps 6.25 D^2 instead of 9 but needs 9 colours instead of 4. Set by the " +
                 "sweep; it lives on the component, not on BoidSettings, so it dies with play mode.")]
        public int ScanRadius = 1;

        [Tooltip("Cell size multiplier for variants 4-6, on top of the divide by ScanRadius. The " +
                 "scan still reaches ScanRadius cells, so reach = diameter * this and nothing is " +
                 "missed above 1. Bigger cells hold more agents, which amortises the per-cell row " +
                 "bounds over more of them, and scan more candidates. Set by the sweep.")]
        public float CellScale = 1f;

        [Tooltip("Count how far the separation solve moved each agent, and log a MOTION| line. " +
                 "Sizes the sleeping idea before anything is built for it: skipping agents that " +
                 "cannot move only pays if most of them cannot move WHILE THE CROWD IS FLOWING, " +
                 "which is when the solver is expensive. Costs a 50k copy and a 50k compare.")]
        public bool MeasureMotion;

        [Header("Debug - solver cost")]
        [Tooltip("0 = off. 1..9 run stripped variants of the separation job ALONGSIDE the real " +
                 "solve, writing to a throwaway buffer, so the inner loop cost can be split by " +
                 "subtraction: 1 = dispatch+read+write, 2 = +grid lookup, 3 = +candidate walk, " +
                 "4 = +neighbour load, 5 = +lengthsq, 6 = +reject branch, 7 = reject as a mask " +
                 "instead of a branch, 8 = mask plus the compaction store, 9 = stage 8 four at a " +
                 "time. The simulation stays correct; only the frame time is inflated.")]
        public int AblationStage;
        [Tooltip("After the crowd converges, time every separation variant back to back on the " +
                 "SAME crowd state, then time every non-separation job. Timing variants on " +
                 "separate runs is no good - the crowd is never in the same place twice.")]
        public bool SeparateBenchmark;

        [Tooltip("Once, on one crowd state, time the candidate walk and the whole separation pass " +
                 "at scan radius 1, 2 and 3, and report the candidates per agent each one scans. " +
                 "This is what splits the walk cost into a fixed part per ROW RUN and a variable " +
                 "part per CANDIDATE: R=2 cuts candidates ~31% and raises row runs from 3 to 5, so " +
                 "two radii give two equations. Runs before the sweep starts, and reorders the " +
                 "crowd without moving it.")]
        public bool RadiusBenchmark;
        [Tooltip("Largest scan radius the radius benchmark and the sweep may ask for.")]
        public int MaxRadius = 3;
        [Tooltip("Repetitions per stage in the radius benchmark. 8 was too few - the run-to-run " +
                 "spread on one config was 0.027 ms, the same size as the difference it was being " +
                 "used to measure, and the sign flipped between sessions.")]
        public int RadiusBenchmarkReps = 32;

        static readonly ProfilerMarker UploadMarker = new ProfilerMarker("Boid.Upload");
        static readonly ProfilerMarker DrawMarker = new ProfilerMarker("Boid.Draw");
        static readonly int AgentsId = Shader.PropertyToID("_Agents");
        static readonly int ScaleId = Shader.PropertyToID("_Scale");
        static readonly int MaxSpeedId = Shader.PropertyToID("_MaxSpeed");
        static readonly int ColorSlowId = Shader.PropertyToID("_ColorSlow");
        static readonly int ColorMidId = Shader.PropertyToID("_ColorMid");
        static readonly int ColorFastId = Shader.PropertyToID("_ColorFast");

        public BoidSolver Solver { get; private set; }

        GraphicsBuffer _agentBuffer;
        GraphicsBuffer _argsBuffer;
        MaterialPropertyBlock _props;
        JobHandle _handle;
        int _allocatedFor = -1;
        readonly Stopwatch _watch = new Stopwatch();
        double _solveMs;
        double _worstMs;
        int _frames;
        float _lastLog;
        // BenchmarkSeparate needs its own timer. It used to share _lastLog with
        // LogForBenchmark, which runs first and resets it, so SEP|/ABL| never printed
        // when LogBenchmark was also on.
        float _lastSepLog;
        int _logs;
        float2 _lastTarget;
        int _benchRuns;

        void Start()
        {
            if (WorkerThreads > 0)
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = WorkerThreads;
            else
            {
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount =
                    Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount;
            }
            Solver = new BoidSolver(Settings);
            Rebuild();
        }

        public void Rebuild()
        {
            Solver.Allocate(Settings.EnemyCount, SpawnMinRadius, SpawnMaxRadius, Seed);
            ReleaseBuffers();

            _agentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Settings.EnemyCount, sizeof(float) * 4);
            _argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1,
                GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _props = new MaterialPropertyBlock();
            _allocatedFor = Settings.EnemyCount;
            PushArgs();
        }

        void PushArgs()
        {
            if (Mesh == null || _argsBuffer == null) return;
            var args = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
            args[0].indexCountPerInstance = Mesh.GetIndexCount(0);
            args[0].instanceCount = (uint)Settings.EnemyCount;
            args[0].startIndex = Mesh.GetIndexStart(0);
            args[0].baseVertexIndex = Mesh.GetBaseVertex(0);
            args[0].startInstance = 0;
            _argsBuffer.SetData(args);
        }

        void Update()
        {
            if (Solver == null) return;
            if (_allocatedFor != Settings.EnemyCount) Rebuild();

            // The sweep needs a flowing crowd, so it forces the circling target while it runs -
            // as an override, not by writing the field.
            var autoTarget = AutoTarget || _sweepActive;

            float2 target;
            if (autoTarget)
            {
                var a = Time.time * AutoSpeed;
                target = new float2(math.cos(a), math.sin(a)) * AutoRadius;
                if (Player != null) Player.position = new Vector3(target.x, Player.position.y, target.y);
            }
            else
            {
                target = Player != null
                    ? new float2(Player.position.x, Player.position.z)
                    : float2.zero;
            }

            _lastTarget = target;
            Solver.CheckOverlap = _sweepActive ? _sweepOverlap : CheckOverlap;
            Solver.AblationStage = AblationStage;
            Solver.ScanRadius = _sweepActive ? _sweepScanRadius : ScanRadius;
            Solver.CellScale = _sweepActive ? _sweepCellScale : CellScale;
            Solver.MeasureMotion = MeasureMotion;
            _handle = Solver.Schedule(target, Time.deltaTime);
        }

        void LateUpdate()
        {
            if (Solver == null || Solver.Count == 0) return;

            BoidSolver.CompleteMarker.Begin();
            _watch.Restart();
            _handle.Complete();
            _watch.Stop();
            BoidSolver.CompleteMarker.End();

            // Wall time the main thread actually waited for the whole solver, this frame.
            var ms = _watch.Elapsed.TotalMilliseconds;
            _solveMs = _frames++ < 30 ? ms : _solveMs * 0.95 + ms * 0.05;
            if (_frames > 60 && ms > _worstMs) _worstMs = ms;

            LogForBenchmark();
            BenchmarkSeparate();
            BenchmarkRadius();
            RunSweep();

            if (!Draw || Mesh == null || Material == null) return;

            UploadMarker.Begin();
            _agentBuffer.SetData(Solver.Instances);
            UploadMarker.End();

            DrawMarker.Begin();
            _props.SetBuffer(AgentsId, _agentBuffer);
            _props.SetFloat(ScaleId, Settings.BodyDiameter);
            _props.SetFloat(MaxSpeedId, Settings.MaxSpeed);
            _props.SetVector(ColorSlowId, ColorSlow);
            _props.SetVector(ColorMidId, ColorMid);
            _props.SetVector(ColorFastId, ColorFast);

            var rp = new RenderParams(Material)
            {
                worldBounds = new Bounds(Vector3.zero, new Vector3(200f, 10f, 200f)),
                matProps = _props,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };
            Graphics.RenderMeshIndirect(rp, Mesh, _argsBuffer);
            DrawMarker.End();
        }


        /// <summary>
        /// Time every separation variant against the others on the crowd exactly as it stands.
        /// Waits for the same warmup the throughput benchmark uses, because the cost of the job
        /// is set by how densely the crowd has packed and a crowd still flying in from the spawn
        /// ring is the easy case.
        /// </summary>
        void BenchmarkSeparate()
        {
            if (!SeparateBenchmark || _benchRuns >= 5) return;
            if (Time.time < BenchmarkWarmupSeconds) return;
            if (Time.time - _lastSepLog < 1f) return;
            _lastSepLog = Time.time;
            _benchRuns++;

            var ms = new double[3];
            var delta = new float[3];
            Solver.CompareSeparate(_lastTarget, 8, ms, delta);
            var threads = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + 1;
            UnityEngine.Debug.Log(
                $"SEP| branchy={ms[0]:F3} compact={ms[1]:F3} simd={ms[2]:F3} ms wall/dispatch"
              + $" | frame {ms[0] * threads * Settings.OverlapCleanup:F2}"
              + $" -> {ms[1] * threads * Settings.OverlapCleanup:F2}"
              + $" -> {ms[2] * threads * Settings.OverlapCleanup:F2} ms all threads"
              + $" | {ms[0] / math.max(1e-9, ms[1]):F2}x then {ms[1] / math.max(1e-9, ms[2]):F2}x"
              + $" | delta compact={delta[1]:E2} simd={delta[2]:E2}"
              + $" | pairs={Solver.OverlapPairs}");

            // Cost breakdown of the job that actually ships. Warm each stage first so Burst
            // compilation and cold caches land outside the timing.
            for (var c = 0; c <= 6; c++) Solver.TimeColoured(c, 2);
            var col = new double[7];
            for (var c = 0; c <= 6; c++) col[c] = Solver.TimeColoured(c, 8);
            UnityEngine.Debug.Log(
                $"COL| C1={col[1]:F3} C2={col[2]:F3} C3={col[3]:F3} C4={col[4]:F3} C5={col[5]:F3}"
              + $" C6={col[6]:F3} full={col[0]:F3} ms/pass"
              + $" | gridLookup={col[2] - col[1]:F3} loopControl={col[3] - col[2]:F3}"
              + $" load={col[4] - col[3]:F3} lengthsq={col[5] - col[4]:F3}"
              + $" compare+store={col[6] - col[5]:F3} contactMath={col[0] - col[6]:F3}"
              + $" | candidates/agent={Solver.MeanCandidates():F1}");

            var stage = new double[5];
            Solver.TimeStages(stage, 6);
            var names = new[] { "steer", "gridBuild", "colour", "finalize", "overlapCheck" };
            var line = "STAGE|";
            var total = 0d;
            for (var q = 0; q < stage.Length; q++) { line += $" {names[q]}={stage[q]:F3}"; total += stage[q]; }
            UnityEngine.Debug.Log(line + $" | total={total:F3} ms");

            // Where the 0.46 ms step between stage 5 and stage 6 actually goes. 7 is the same
            // reject written as a mask, 8 adds the compaction store, 9 walks it four at a time.
            for (var st = 5; st <= 9; st++) Solver.TimeAblation(st, 2);
            UnityEngine.Debug.Log(
                $"ABL| 5={Solver.TimeAblation(5, 6):F3} 6={Solver.TimeAblation(6, 6):F3}"
              + $" 7={Solver.TimeAblation(7, 6):F3} 8={Solver.TimeAblation(8, 6):F3}"
              + $" 9={Solver.TimeAblation(9, 6):F3} ms wall/dispatch");
        }

        int _radiusRuns;

        // Sweep overrides. The sweep used to assign AutoTarget / CheckOverlap / ScanRadius /
        // CellScale directly and restore them afterwards, which meant a play session stopped at the
        // wrong moment left the scene with the check off and the target parked - exactly the state
        // someone hitting play to look at the crowd does not want. These are private, so a normal
        // play session reads the inspector values and nothing can be left behind.
        bool _sweepActive;
        bool _sweepOverlap;
        int _sweepScanRadius = 1;
        float _sweepCellScale = 1f;

        /// <summary>
        /// The measurement that decides lead 2 before the sweep even runs, and explains the walk
        /// cost that the ablation table could only report.
        ///
        /// walk(R) = (2R+1) * Fixed + candidates(R) * PerCandidate. Radius 1 and radius 2 give two
        /// equations in two unknowns, and radius 3 checks the fit. If Fixed is large the extra row
        /// runs eat the smaller candidate list and no amount of shrinking the cell can win.
        ///
        /// All three radii are measured on ONE crowd state, back to back, for the same reason the
        /// sweep exists: a crowd is never in the same place twice.
        ///
        /// READ THE NUMBERS FOR SHAPE, NOT FOR SMALL DIFFERENCES. Each stage is a Complete() per
        /// pass over the whole crowd, which is a barrier the real frame never pays, and the
        /// run-to-run spread on ONE config was measured at 0.027 ms across three sessions. That is
        /// the same size as the contact-math change this was used to look at, and on the third
        /// session the sign of that difference flipped. The ratios between radii are large enough
        /// to survive it - 0.294 against 0.446 - and the candidate counts are exact. Anything
        /// smaller than about 0.05 ms here is not a result; take it to SweepConfigs, which samples
        /// 240 frames per config and bookends itself.
        /// </summary>
        void BenchmarkRadius()
        {
            if (!RadiusBenchmark || _radiusRuns > 0) return;
            if (Time.time < BenchmarkWarmupSeconds) return;
            _radiusRuns++;

            var maxR = math.clamp(MaxRadius, 1, 3);
            for (var r = 1; r <= maxR; r++)
            {
                // 8 reps could not resolve 0.035 ms against its own 0.027 ms of run-to-run
                // spread. This is still not the harness to settle small differences with, but at
                // least the number it prints is stable.
                Solver.MeasureRadius(r, RadiusBenchmarkReps, out var walk, out var full,
                                     out var rsq, out var fus, out var simd,
                                     out var cand, out var simdCand,
                                     out var cells, out var items);
                var runs = 2 * r + 1;
                var colours = (r + 1) * (r + 1);
                UnityEngine.Debug.Log(
                    $"COLR| R={r} runs={runs} colours={colours} cells={cells:N0} workItems={items:N0}"
                  + $" | cand/agent={cand:F2} candPerRun={cand / runs:F2}"
                  + $" | walk={walk:F3} full={full:F3} contactMath={full - walk:F3} ms/pass"
                  + $" | rsqrt={rsq:F3} fused={fus:F3}"
                  + $" | saved vs full: rsqrt={full - rsq:F3} fused={full - fus:F3}"
                  + $" | walkPerCand={walk / math.max(1e-9, cand):F4} walkPerRun={walk / runs:F4}"
                  + $" | SIMDwalk={simd:F3} vs scalar {walk:F3}"
                  + $" ({walk / math.max(1e-9, simd):F2}x)"
                  + $" maskCheck cand={simdCand:F2} vs {cand:F2}"
                  + $" {(math.abs(simdCand - cand) < 0.05 ? "OK" : "MASK WRONG - IGNORE TIMING")}");
            }
        }

        [System.Serializable]
        public struct SweepConfig
        {
            public int Variant;
            public int Iterations;
            public float Omega;
            [Tooltip("Inner-loop batch in CELLS for the coloured passes. 0 leaves it alone.")]
            public int ColourBatch;
            [Tooltip("Scan radius. Variants 4, 5 and 6 read it. 0 means 1.")]
            public int ScanRadius;
            [Tooltip("Run the overlap verification jobs during this config. OFF gives the wall " +
                     "time that would actually ship, but leaves the penetration reading stale, so " +
                     "it is logged as 'pen=n/a'. Put each config in TWICE - once each way - rather " +
                     "than running the sweep twice, because two sessions are not comparable.")]
            public bool Overlap;
            [Tooltip("Agent-loop batch for everything EXCEPT the coloured passes - steer, hash, " +
                     "scatter, finalize, overlap. 0 leaves it alone. The last knob this project " +
                     "never swept.")]
            public int BatchSize;
            [Tooltip("Cell size multiplier for variants 4-6. 0 means 1.")]
            public float CellScale;
            [Tooltip("Dwell on this config but do not log it. The first config in a sweep reads " +
                     "systematically better than the rest - measured at ~1.4 points of penetration " +
                     "across two runs - because AutoTarget has only just come on and the crowd is " +
                     "still settling into the flowing regime. One discarded row at the front " +
                     "absorbs that, which is what makes the bookends mean anything.")]
            public bool Discard;
        }

        [Header("Debug - config sweep")]
        [Tooltip("Step through SweepConfigs in ONE play session and log wall time and mean " +
                 "penetration for each. Measuring configs in separate sessions is worthless: the " +
                 "crowd is never in the same place twice, and a settled crowd and a flowing one " +
                 "give completely different penetration for the same solver.")]
        public bool SweepConfigs;
        [Tooltip("Simulated seconds to dwell on each config before reading it. The crowd needs " +
                 "time to re-settle after the solver changes.")]
        public float SweepDwell = 8f;
        public SweepConfig[] Sweep =
        {
            // Discarded: absorbs the settling transient so the bookends compare like with like.
            // Measured at ~1.4 points of penetration on the first row across three runs.
            new SweepConfig { Variant = 3, Iterations = 6, Omega = 1.8f, Overlap = true, Discard = true },

            // Every config twice, check on then check off, in ONE session. The check-on row is the
            // only one that can carry a quality number; the check-off row is the only one that is
            // the cost that would actually ship.
            new SweepConfig { Variant = 3, Iterations = 6, Omega = 1.8f, Overlap = true },
            new SweepConfig { Variant = 3, Iterations = 6, Omega = 1.8f, Overlap = false },

            new SweepConfig { Variant = 5, Iterations = 6, Omega = 1.8f, ScanRadius = 1, CellScale = 1.0f, Overlap = true },
            new SweepConfig { Variant = 5, Iterations = 6, Omega = 1.8f, ScanRadius = 1, CellScale = 1.0f, Overlap = false },

            // Fewer row-run entries per agent by holding more agents per cell. Costs candidates as
            // the square, so this is where the two terms cross.
            new SweepConfig { Variant = 5, Iterations = 6, Omega = 1.8f, ScanRadius = 1, CellScale = 1.4f, Overlap = true },
            new SweepConfig { Variant = 5, Iterations = 6, Omega = 1.8f, ScanRadius = 1, CellScale = 1.4f, Overlap = false },

            new SweepConfig { Variant = 5, Iterations = 6, Omega = 1.8f, ScanRadius = 1, CellScale = 2.0f, Overlap = true },
            new SweepConfig { Variant = 5, Iterations = 6, Omega = 1.8f, ScanRadius = 1, CellScale = 2.0f, Overlap = false },

            // Bookend B. Wall against bookend A is the noise floor for every number above.
            new SweepConfig { Variant = 3, Iterations = 6, Omega = 1.8f, Overlap = true },
            new SweepConfig { Variant = 3, Iterations = 6, Omega = 1.8f, Overlap = false },
        };

        int _sweepIndex = -1;
        float _sweepStarted;
        double _sweepWall;
        double _sweepPen;
        int _sweepSamples;
        bool _sweepSaved;
        bool _sweepDone;
        int _savedVariant, _savedIterations, _savedColourBatch;
        int _savedBatchSize;
        float _savedOmega;
        float _savedCaptureDt;

        /// <summary>
        /// Walk every configuration inside one play session, against one scenario, and log a
        /// comparable row for each.
        ///
        /// Two things here are the whole point. The target is driven on a circle, so every config
        /// meets a crowd that is still flowing rather than one that has settled into a static
        /// equilibrium - a settled crowd makes every solver look good. And deltaTime is pinned, so
        /// a slower config does not get handed a bigger step and score worse for that reason
        /// alone. Without both, the numbers compare the scenario rather than the solver.
        ///
        /// Everything touched is restored in RestoreSweep, which OnDisable also calls: leaving
        /// Iterations at a swept value silently changes the sim with nothing on disk to show it.
        /// </summary>
        void RunSweep()
        {
            if (!SweepConfigs || _sweepDone || Sweep == null || Sweep.Length == 0) return;
            if (Time.time < BenchmarkWarmupSeconds) return;

            if (!_sweepSaved)
            {
                _savedVariant = Settings.Advanced.SeparateVariant;
                _savedIterations = Settings.OverlapCleanup;
                _savedOmega = Settings.Advanced.Relaxation;
                _savedColourBatch = Settings.Advanced.CellBatchSize;
                _savedBatchSize = Settings.Advanced.AgentBatchSize;
                _savedCaptureDt = Time.captureDeltaTime;
                _sweepSaved = true;
                _sweepActive = true;
                Time.captureDeltaTime = 1f / 60f;
                _sweepIndex = 0;
                ApplySweep(0);
                _sweepStarted = Time.time;
                return;
            }

            if (_sweepIndex < 0 || _sweepIndex >= Sweep.Length) return;

            // Sample only the second half of the dwell, so the readings exclude the transient
            // while the crowd adapts to the new solver.
            var elapsed = Time.time - _sweepStarted;
            if (elapsed > SweepDwell * 0.5f)
            {
                _sweepWall += _solveMs;
                _sweepPen += Solver.MeanPenetration;
                _sweepSamples++;
            }
            if (elapsed < SweepDwell) return;

            var c = Sweep[_sweepIndex];
            var n = math.max(1, _sweepSamples);
            if (c.Discard)
            {
                // Dwelt on, deliberately not logged. See SweepConfig.Discard.
                UnityEngine.Debug.Log($"SWEEP|discard variant={c.Variant} R={_sweepScanRadius}"
                                    + $" cellScale={_sweepCellScale:F2}"
                                    + $" wall={_sweepWall / n:F2}ms (settling row, not a result)");
            }
            else
            {
                // With the check off the penetration counters are whatever the last checked frame
                // left behind, so print them as absent rather than as a number someone will quote.
                var quality = c.Overlap
                    ? $"meanPen={_sweepPen / n * 100f:F2}%"
                    + $" | pairs={Solver.OverlapPairs} body={Solver.BodyOverlapPairs}"
                    : "meanPen=n/a | pairs=n/a body=n/a";
                if (MeasureMotion)
                {
                    var tot = math.max(1, Solver.Count);
                    UnityEngine.Debug.Log(
                        $"MOTION| variant={c.Variant} cellScale={CellScale:F2}"
                      + $" | solve moved the agent less than, as a fraction of the solve diameter:"
                      + $" 0.01%={100f * Solver.MotionBand(0) / tot:F1}%"
                      + $" 0.1%={100f * Solver.MotionBand(1) / tot:F1}%"
                      + $" 1%={100f * Solver.MotionBand(2) / tot:F1}%"
                      + $" 5%={100f * Solver.MotionBand(3) / tot:F1}%"
                      + $" more={100f * Solver.MotionBand(4) / tot:F1}%");
                }
                UnityEngine.Debug.Log(
                    $"SWEEP| variant={c.Variant} R={_sweepScanRadius} it={c.Iterations} omega={c.Omega:F2}"
                  + $""
                  + $" colourBatch={Settings.Advanced.CellBatchSize}"
                  + $" batchSize={Settings.Advanced.AgentBatchSize}"
                  + $" cellScale={_sweepCellScale:F2} overlap={c.Overlap}"
                  + $" | wall={_sweepWall / n:F2}ms {quality} samples={n}");
            }

            _sweepWall = 0;
            _sweepPen = 0;
            _sweepSamples = 0;
            _sweepIndex++;
            if (_sweepIndex >= Sweep.Length)
            {
                UnityEngine.Debug.Log("SWEEP|done");
                // Without this the sweep restarts, and the second lap inherits a crowd left
                // badly penetrated by the worst config rather than the warmed-up one - the same
                // Jacobi row read 6.80% on lap one and 10.54% on lap two.
                _sweepDone = true;
                RestoreSweep();
                return;
            }
            ApplySweep(_sweepIndex);
            _sweepStarted = Time.time;
        }

        void ApplySweep(int index)
        {
            var c = Sweep[index];
            Settings.Advanced.SeparateVariant = c.Variant;
            Settings.OverlapCleanup = c.Iterations;
            Settings.Advanced.Relaxation = c.Omega;
            if (c.ColourBatch > 0) Settings.Advanced.CellBatchSize = c.ColourBatch;
            if (c.BatchSize > 0) Settings.Advanced.AgentBatchSize = c.BatchSize;
            // Overrides, not the serialized fields - see the _sweep* declarations.
            _sweepScanRadius = math.clamp(c.ScanRadius <= 0 ? 1 : c.ScanRadius, 1, MaxRadius);
            _sweepCellScale = c.CellScale <= 0f ? 1f : c.CellScale;
            _sweepOverlap = c.Overlap;
        }

        void RestoreSweep()
        {
            if (!_sweepSaved) return;
            Settings.Advanced.SeparateVariant = _savedVariant;
            Settings.OverlapCleanup = _savedIterations;
            Settings.Advanced.Relaxation = _savedOmega;
            Settings.Advanced.CellBatchSize = _savedColourBatch;
            Settings.Advanced.AgentBatchSize = _savedBatchSize;
            Time.captureDeltaTime = _savedCaptureDt;
            _sweepActive = false;
            _sweepSaved = false;
            _sweepIndex = -1;
        }

        void LogForBenchmark()
        {
            if (!LogBenchmark || Time.time < BenchmarkWarmupSeconds) return;
            if (Time.time - _lastLog < 1f) return;
            _lastLog = Time.time;

            var s = Solver;
            UnityEngine.Debug.Log($"BOID| n={s.Count} workers={Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount}"
                + $" wall={_solveMs:F2}ms worst={_worstMs:F2}ms fps={1f / Time.smoothDeltaTime:F0}"
                + $" pairs={s.OverlapPairs} body={s.BodyOverlapPairs} agents={s.OverlapAgents}"
                + $" meanPen={s.MeanPenetration * 100f:F2}% worstPen={s.WorstPenetration * 100f:F1}%"
                + $" deep={s.PenetrationBand(5)}");

            if (++_logs >= BenchmarkSamples)
            {
                UnityEngine.Debug.Log("BOID|done");
                Application.Quit();
            }
        }
        void OnGUI()
        {
            if (!ShowStats || Solver == null || Solver.Count == 0) return;

            var pairs = Solver.OverlapPairs;
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                normal = { textColor = pairs == 0 ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.45f, 0.4f) }
            };

            var text =
                $"agents             {Solver.Count:N0}\n"
              + $"solver wall        {_solveMs:F2} ms  (worst {_worstMs:F2})\n"
              + $"solve diameter     {Settings.CollisionDiameter:F5}  (body {Settings.BodyDiameter:F5})\n"
              + $"FAILING PAIRS      {pairs:N0}  (body {Solver.BodyOverlapPairs:N0})\n"
              + $"agents overlapping {Solver.OverlapAgents:N0} ({100f * Solver.OverlapAgents / Solver.Count:F2}%)\n"
              + $"worst penetration  {Solver.WorstPenetration * 100f:F2}% of solve dia\n"
              + $"mean penetration   {Solver.MeanPenetration * 100f:F2}% of solve dia";

            GUI.Box(new Rect(8, 8, 420, 150), GUIContent.none);
            GUI.Label(new Rect(16, 12, 410, 145), text, style);
        }

        void OnDisable()
        {
            // A swept Iterations left behind changes the sim with nothing on disk to show it.
            RestoreSweep();
        }

        void OnDestroy()
        {
            _handle.Complete();
            Solver?.Release();
            ReleaseBuffers();
        }

        void ReleaseBuffers()
        {
            _agentBuffer?.Release();
            _argsBuffer?.Release();
            _agentBuffer = null;
            _argsBuffer = null;
        }
    }
}
