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
        public float DrawScale = 0.3f;
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
        public bool CheckOverlap = true;
        [Tooltip("Log a BOID| line every second after the crowd converges. For player runs.")]
        public bool LogBenchmark;
        public float BenchmarkWarmupSeconds = 70f;
        public int BenchmarkSamples = 10;

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
            Solver = new BoidSolver(Settings);
            Rebuild();
        }

        public void Rebuild()
        {
            Solver.Allocate(Settings.Count, SpawnMinRadius, SpawnMaxRadius, Seed);
            ReleaseBuffers();

            _agentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Settings.Count, sizeof(float) * 4);
            _argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1,
                GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _props = new MaterialPropertyBlock();
            _allocatedFor = Settings.Count;
            PushArgs();
        }

        void PushArgs()
        {
            if (Mesh == null || _argsBuffer == null) return;
            var args = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
            args[0].indexCountPerInstance = Mesh.GetIndexCount(0);
            args[0].instanceCount = (uint)Settings.Count;
            args[0].startIndex = Mesh.GetIndexStart(0);
            args[0].baseVertexIndex = Mesh.GetBaseVertex(0);
            args[0].startInstance = 0;
            _argsBuffer.SetData(args);
        }

        void Update()
        {
            if (Solver == null) return;
            if (_allocatedFor != Settings.Count) Rebuild();

            float2 target;
            if (AutoTarget)
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
            Solver.CheckOverlap = CheckOverlap;
            Solver.AblationStage = AblationStage;
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
            RunSweep();

            if (!Draw || Mesh == null || Material == null) return;

            UploadMarker.Begin();
            _agentBuffer.SetData(Solver.Instances);
            UploadMarker.End();

            DrawMarker.Begin();
            _props.SetBuffer(AgentsId, _agentBuffer);
            _props.SetFloat(ScaleId, DrawScale);
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
              + $" | frame {ms[0] * threads * Settings.Iterations:F2}"
              + $" -> {ms[1] * threads * Settings.Iterations:F2}"
              + $" -> {ms[2] * threads * Settings.Iterations:F2} ms all threads"
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

        [System.Serializable]
        public struct SweepConfig
        {
            public int Variant;
            public int Iterations;
            public float Omega;
            public int MinDivisor;
            [Tooltip("Use the cached neighbour list instead of re-walking the grid each pass.")]
            public bool Cache;
            [Tooltip("Rebuild the cached list every N passes. Ignored unless Cache is on.")]
            public int GatherEvery;
            [Tooltip("Inner-loop batch in CELLS for the coloured passes. 0 leaves it alone.")]
            public int ColourBatch;
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
            new SweepConfig { Variant = 3, Iterations = 6, Omega = 1.8f, MinDivisor = 1 },
            new SweepConfig { Variant = 1, Iterations = 8, Omega = 1.8f, MinDivisor = 1 },
            new SweepConfig { Variant = 1, Iterations = 8, Omega = 1.8f, MinDivisor = 1, Cache = true, GatherEvery = 8 },
            new SweepConfig { Variant = 1, Iterations = 8, Omega = 1.8f, MinDivisor = 1, Cache = true, GatherEvery = 4 },
            new SweepConfig { Variant = 1, Iterations = 8, Omega = 1.8f, MinDivisor = 1, Cache = true, GatherEvery = 2 },
            new SweepConfig { Variant = 1, Iterations = 12, Omega = 1.8f, MinDivisor = 1, Cache = true, GatherEvery = 4 },
            new SweepConfig { Variant = 3, Iterations = 6, Omega = 1.8f, MinDivisor = 1 },
        };

        int _sweepIndex = -1;
        float _sweepStarted;
        double _sweepWall;
        double _sweepPen;
        int _sweepSamples;
        bool _sweepSaved;
        bool _sweepDone;
        int _savedVariant, _savedIterations, _savedMinDivisor, _savedGatherEvery, _savedColourBatch;
        float _savedOmega;
        bool _savedCache;
        bool _savedAutoTarget;
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
                _savedVariant = Settings.SeparateVariant;
                _savedIterations = Settings.Iterations;
                _savedOmega = Settings.Omega;
                _savedMinDivisor = Settings.MinDivisor;
                _savedCache = Settings.CacheNeighbours;
                _savedGatherEvery = Settings.GatherEvery;
                _savedColourBatch = Settings.ColourBatch;
                _savedAutoTarget = AutoTarget;
                _savedCaptureDt = Time.captureDeltaTime;
                _sweepSaved = true;
                AutoTarget = true;
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
            UnityEngine.Debug.Log(
                $"SWEEP| variant={c.Variant} it={c.Iterations} omega={c.Omega:F2} minDiv={c.MinDivisor}"
              + $" cache={c.Cache} every={c.GatherEvery} batch={Settings.ColourBatch}"
              + $" | wall={_sweepWall / n:F2}ms meanPen={_sweepPen / n * 100f:F2}%"
              + $" | pairs={Solver.OverlapPairs} body={Solver.BodyOverlapPairs} samples={n}");

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
            Settings.SeparateVariant = c.Variant;
            Settings.Iterations = c.Iterations;
            Settings.Omega = c.Omega;
            Settings.MinDivisor = math.max(1, c.MinDivisor);
            Settings.CacheNeighbours = c.Cache;
            if (c.Cache) Settings.GatherEvery = math.max(1, c.GatherEvery);
            if (c.ColourBatch > 0) Settings.ColourBatch = c.ColourBatch;
        }

        void RestoreSweep()
        {
            if (!_sweepSaved) return;
            Settings.SeparateVariant = _savedVariant;
            Settings.Iterations = _savedIterations;
            Settings.Omega = _savedOmega;
            Settings.MinDivisor = _savedMinDivisor;
            Settings.CacheNeighbours = _savedCache;
            Settings.GatherEvery = _savedGatherEvery;
            Settings.ColourBatch = _savedColourBatch;
            AutoTarget = _savedAutoTarget;
            Time.captureDeltaTime = _savedCaptureDt;
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
              + $"solve diameter     {Settings.CollisionDiameter:F5}  (body {Settings.Radius * 2f:F5})\n"
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
