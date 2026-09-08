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
        public BoidSettings Settings;
        public Transform Player;

        [Header("Spawn ring (outside the camera's view)")]
        public float SpawnMinRadius = 34f;
        public float SpawnMaxRadius = 44f;
        public int Seed = 1;

        [Header("Benchmark target")]
        [Tooltip("Drive the target on a circle instead of following the player, so the crowd keeps " +
                 "flowing. A crowd converged on a stationary player reaches a static equilibrium, " +
                 "which is the easy case and not what the solver has to survive.")]
        public bool AutoTarget;
        public float AutoRadius = 8f;
        public float AutoSpeed = 1.2f;

        [Header("Draw")]
        public Mesh Mesh;
        public Material Material;
        public float DrawScale = 0.3f;
        public bool Draw = true;
        public Color ColorSlow = new Color(0.20f, 0.45f, 1.00f);
        public Color ColorMid = new Color(1.00f, 0.90f, 0.20f);
        public Color ColorFast = new Color(0.95f, 0.15f, 0.15f);

        [Header("On-screen readout")]
        public bool ShowStats = true;

        [Header("Benchmark")]
        [Tooltip("Job worker threads. 0 leaves Unity's default (one per logical core). Set to 4 " +
                 "to measure what a modest machine would see.")]
        public int WorkerThreads;
        [Tooltip("Log a BOID| line every second after the crowd converges, then quit. For player runs.")]
        public bool LogBenchmark;
        public float BenchmarkWarmupSeconds = 70f;
        public int BenchmarkSamples = 10;

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
            if (!Settings.SeparateBenchmark || _benchRuns >= 5) return;
            if (Time.time < BenchmarkWarmupSeconds) return;
            if (Time.time - _lastLog < 1f) return;
            _lastLog = Time.time;
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

            // Where the 0.46 ms step between stage 5 and stage 6 actually goes. 7 is the same
            // reject written as a mask, 8 adds the compaction store, 9 walks it four at a time.
            for (var st = 5; st <= 9; st++) Solver.TimeAblation(st, 2);
            UnityEngine.Debug.Log(
                $"ABL| 5={Solver.TimeAblation(5, 6):F3} 6={Solver.TimeAblation(6, 6):F3}"
              + $" 7={Solver.TimeAblation(7, 6):F3} 8={Solver.TimeAblation(8, 6):F3}"
              + $" 9={Solver.TimeAblation(9, 6):F3} ms wall/dispatch");
        }

        void LogForBenchmark()
        {
            if (!LogBenchmark || Time.time < BenchmarkWarmupSeconds) return;
            if (Time.time - _lastLog < 1f) return;
            _lastLog = Time.time;

            var s = Solver;
            UnityEngine.Debug.Log($"BOID| n={s.Count} workers={Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount}"
                + $" wall={_solveMs:F2}ms worst={_worstMs:F2}ms fps={1f / Time.smoothDeltaTime:F0}"
                + $" pairs={s.OverlapPairs} agents={s.OverlapAgents} worstPen={s.WorstPenetration * 100f:F1}%"
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
              + $"body diameter      {Settings.Radius * 2f:F5}\n"
              + $"OVERLAPPING PAIRS  {pairs:N0}\n"
              + $"agents overlapping {Solver.OverlapAgents:N0} ({100f * Solver.OverlapAgents / Solver.Count:F2}%)\n"
              + $"worst penetration  {Solver.WorstPenetration * 100f:F2}% of body\n"
              + $"mean penetration   {Solver.MeanPenetration * 100f:F2}% of body";

            GUI.Box(new Rect(8, 8, 420, 150), GUIContent.none);
            GUI.Label(new Rect(16, 12, 410, 145), text, style);
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
