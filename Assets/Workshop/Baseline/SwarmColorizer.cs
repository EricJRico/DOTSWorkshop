using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Draws the swarm with GPU instancing and tints each enemy by its speed. The gradient is
    /// baked once into a lookup table, so the per-frame work is a Burst job over the solver's
    /// arrays; the draw is one <see cref="Graphics.DrawMeshInstanced"/> per 1023 enemies with a
    /// per-instance colour array, instead of one engine call per renderer.
    /// </summary>
    public class SwarmColorizer : MonoBehaviour
    {
        public const int LutSize = 256;
        public const int ChunkSize = 1023;   // Graphics.DrawMeshInstanced's per-call limit

        public EnemyMover Mover;
        public Gradient SpeedGradient;
        public bool Enabled = true;

        [Header("Instanced draw")]
        public Mesh Mesh;
        public Material Material;
        public float Scale = 0.3f;

        /// <summary>Last frame's split, for measurement: fill ms / draw ms.</summary>
        public string LastTiming { get; private set; } = "";

        static readonly ProfilerMarker FillMarker = new ProfilerMarker("Colorizer.Fill");
        static readonly ProfilerMarker DrawMarker = new ProfilerMarker("Colorizer.Draw");
        static readonly int SpeedColorId = Shader.PropertyToID("_SpeedColor");

        NativeArray<float4> _lut;
        NativeArray<Matrix4x4> _matrices;
        NativeArray<Vector4> _colors;
        Matrix4x4[][] _matrixChunks;
        Vector4[][] _colorChunks;
        MaterialPropertyBlock[] _blocks;
        int _count = -1;

        void OnEnable()
        {
            BakeGradient();
        }

        void OnDisable()
        {
            Release();
        }

        void BakeGradient()
        {
            if (!_lut.IsCreated) _lut = new NativeArray<float4>(LutSize, Allocator.Persistent);
            for (var i = 0; i < LutSize; i++)
            {
                var c = SpeedGradient.Evaluate(i / (float)(LutSize - 1));
                _lut[i] = new float4(c.r, c.g, c.b, 1f);
            }
        }

        void LateUpdate()
        {
            if (!Enabled || Mover == null || Mover.Solver == null || Mesh == null || Material == null) return;

            var solver = Mover.Solver;
            var n = solver.Count;
            if (n == 0) return;
            if (n != _count) Allocate(n);

            var maxSpeed = Mover.Settings.Speed * Mover.Settings.MaxSpeedFactor;

            FillMarker.Begin();
            new FillJob
            {
                Position = solver.Positions, Velocity = solver.Velocities, Lut = _lut,
                Matrices = _matrices, Colors = _colors,
                Scale = Scale, MaxSpeed = maxSpeed
            }.Schedule(n, Mover.Settings.BatchSize).Complete();
            FillMarker.End();

            DrawMarker.Begin();
            for (int start = 0, chunk = 0; start < n; start += ChunkSize, chunk++)
            {
                var length = math.min(ChunkSize, n - start);
                _matrices.GetSubArray(start, length).CopyTo(_matrixChunks[chunk]);
                _colors.GetSubArray(start, length).CopyTo(_colorChunks[chunk]);
                _blocks[chunk].SetVectorArray(SpeedColorId, _colorChunks[chunk]);
                Graphics.DrawMeshInstanced(Mesh, 0, Material, _matrixChunks[chunk], length, _blocks[chunk]);
            }
            DrawMarker.End();

            LastTiming = $"n={n} chunks={(n + ChunkSize - 1) / ChunkSize}";
        }

        void Allocate(int n)
        {
            Release(keepLut: true);
            _matrices = new NativeArray<Matrix4x4>(n, Allocator.Persistent);
            _colors = new NativeArray<Vector4>(n, Allocator.Persistent);

            var chunks = (n + ChunkSize - 1) / ChunkSize;
            _matrixChunks = new Matrix4x4[chunks][];
            _colorChunks = new Vector4[chunks][];
            _blocks = new MaterialPropertyBlock[chunks];
            for (var c = 0; c < chunks; c++)
            {
                var length = math.min(ChunkSize, n - c * ChunkSize);
                _matrixChunks[c] = new Matrix4x4[length];
                _colorChunks[c] = new Vector4[length];
                _blocks[c] = new MaterialPropertyBlock();
            }
            _count = n;
        }

        void Release(bool keepLut = false)
        {
            if (_matrices.IsCreated) _matrices.Dispose();
            if (_colors.IsCreated) _colors.Dispose();
            if (!keepLut && _lut.IsCreated) _lut.Dispose();
            _count = -1;
        }

        /// <summary>Builds the instance matrix and looks the colour up; no managed calls, so Burst compiles it.</summary>
        [BurstCompile]
        struct FillJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3>.ReadOnly Position;
            [ReadOnly] public NativeArray<float3>.ReadOnly Velocity;
            [ReadOnly] public NativeArray<float4> Lut;
            [WriteOnly] public NativeArray<Matrix4x4> Matrices;
            [WriteOnly] public NativeArray<Vector4> Colors;
            public float Scale;
            public float MaxSpeed;

            public void Execute(int i)
            {
                Matrices[i] = float4x4.TRS(Position[i], quaternion.identity, Scale);

                var t = math.saturate(math.length(Velocity[i]) / MaxSpeed);
                var index = (int)math.round(t * (LutSize - 1));
                Colors[i] = Lut[index];
            }
        }
    }
}
