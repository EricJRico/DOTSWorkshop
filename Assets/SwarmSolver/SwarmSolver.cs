using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Workshop
{
    /// <summary>
    /// Owns the swarm state and runs Weiss et al. 2017 Algorithm 1 once per call.
    /// Enemies are added with <see cref="Register"/>; arrays are allocated on the next Step.
    /// </summary>
    public class SwarmSolver
    {
        readonly SwarmSettings _settings;
        readonly ArenaSettings _arena;
        readonly List<Transform> _pending = new List<Transform>();

        NativeArray<float3> _position;    // x^n
        NativeArray<float3> _predicted;   // x*
        NativeArray<float3> _velocity;    // v^n, then v^{n+1}
        NativeArray<float3> _smoothed;    // after XSPH
        NativeArray<float3> _delta;
        NativeArray<int> _count;
        NeighbourGrid _contactGrid;       // cell = collision diameter
        NeighbourGrid _xsphGrid;          // cell = h
        TransformAccessArray _transforms;
        bool _allocated;

        public int Count => _allocated ? _position.Length : _pending.Count;
        public IReadOnlyList<Transform> Transforms => _pending;
        public NativeArray<float3>.ReadOnly Velocities => _velocity.AsReadOnly();
        public NativeArray<float3>.ReadOnly Positions => _position.AsReadOnly();

        public SwarmSolver(SwarmSettings settings, ArenaSettings arena)
        {
            _settings = settings;
            _arena = arena;
        }

        public void Register(Transform enemy)
        {
            _pending.Add(enemy);
            if (_allocated) Release();
        }

        public void Step(float3 target)
        {
            if (!_allocated) Allocate();
            if (_position.Length == 0) return;

            var s = _settings;
            var n = _position.Length;
            var dt = s.Advanced.StepSeconds;
            var batch = s.Advanced.BatchSize;
            var handle = default(JobHandle);

            for (var sub = 0; sub < s.Advanced.Substeps; sub++)
            {
                handle = new PredictJob
                {
                    Position = _position, Velocity = _velocity, Predicted = _predicted,
                    Target = target, Speed = s.MoveSpeed, Blend = s.TurnSpeed, DeltaTime = dt
                }.Schedule(n, batch, handle);

                handle = new BuildGridJob { Positions = _predicted, Grid = _contactGrid }.Schedule(handle);

                for (var it = 0; it < s.Advanced.StabilityIterations; it++)
                {
                    handle = Contact(_position, friction: false, target, handle);
                    handle = Apply(alsoPosition: true, handle);
                }

                for (var it = 0; it < s.OverlapCleanup; it++)
                {
                    handle = Contact(_predicted, friction: true, target, handle);
                    handle = Apply(alsoPosition: false, handle);
                }

                handle = new VelocityJob
                {
                    Position = _position, Predicted = _predicted, Velocity = _smoothed, DeltaTime = dt
                }.Schedule(n, batch, handle);

                handle = new BuildGridJob { Positions = _predicted, Grid = _xsphGrid }.Schedule(handle);

                // _smoothed holds raw v; XSPH writes the smoothed result into _delta (reused).
                handle = new XsphJob
                {
                    Predicted = _predicted, Velocity = _smoothed, Grid = _xsphGrid, Smoothed = _delta,
                    H = s.XsphRadius, C = s.XsphC
                }.Schedule(n, batch, handle);

                handle = new CommitJob
                {
                    Position = _position, Velocity = _velocity, Smoothed = _delta, Predicted = _predicted,
                    MaxSpeed = s.MaxSpeed, MaxAcceleration = s.MaxAcceleration, DeltaTime = dt
                }.Schedule(n, batch, handle);
            }

            handle = new WriteTransformsJob { Position = _position }.Schedule(_transforms, handle);
            handle.Complete();
        }

        JobHandle Contact(NativeArray<float3> solve, bool friction, float3 target, JobHandle after)
        {
            var s = _settings;
            return new ContactJob
            {
                Solve = solve, Start = _position, Grid = _contactGrid, Delta = _delta, Count = _count,
                Diameter = s.CollisionDiameter, Radius = s.EnemyRadius,
                PlayerPosition = target, PlayerRadius = s.PlayerPush,
                StaticFriction = s.Grip, KineticFriction = s.Drag, Friction = friction
            }.Schedule(_position.Length, s.Advanced.BatchSize, after);
        }

        JobHandle Apply(bool alsoPosition, JobHandle after)
        {
            return new ApplyJob
            {
                Predicted = _predicted, Position = _position, Delta = _delta, Count = _count,
                Omega = _settings.Advanced.Relaxation, AlsoPosition = alsoPosition
            }.Schedule(_position.Length, _settings.Advanced.BatchSize, after);
        }

        public void Dispose()
        {
            if (_allocated) Release();
            _pending.Clear();
        }

        void Allocate()
        {
            var n = _pending.Count;
            _position = new NativeArray<float3>(n, Allocator.Persistent);
            _predicted = new NativeArray<float3>(n, Allocator.Persistent);
            _velocity = new NativeArray<float3>(n, Allocator.Persistent);
            _smoothed = new NativeArray<float3>(n, Allocator.Persistent);
            _delta = new NativeArray<float3>(n, Allocator.Persistent);
            _count = new NativeArray<int>(n, Allocator.Persistent);
            _contactGrid = new NeighbourGrid(_arena.WorldMin, _arena.WorldMax, _settings.CollisionDiameter, n);
            _xsphGrid = new NeighbourGrid(_arena.WorldMin, _arena.WorldMax, _settings.XsphRadius, n);
            _transforms = new TransformAccessArray(_pending.ToArray());
            for (var i = 0; i < n; i++) _position[i] = _pending[i].position;
            _allocated = true;
        }

        void Release()
        {
            _position.Dispose();
            _predicted.Dispose();
            _velocity.Dispose();
            _smoothed.Dispose();
            _delta.Dispose();
            _count.Dispose();
            _contactGrid.Dispose();
            _xsphGrid.Dispose();
            _transforms.Dispose();
            _allocated = false;
        }

        // ---- verification only -------------------------------------------------------

        /// <summary>Pairs closer than the given fraction of the collision diameter.</summary>
        public int CountOverlaps(float fraction = 0.9f)
        {
            var limit = _settings.CollisionDiameter * fraction;
            var overlaps = 0;
            for (var i = 0; i < _position.Length; i++)
            for (var j = i + 1; j < _position.Length; j++)
            {
                var d = _position[i] - _position[j];
                d.y = 0f;
                if (math.lengthsq(d) < limit * limit) overlaps++;
            }
            return overlaps;
        }

        public string Describe(float3 target)
        {
            var n = _position.Length;
            float sumDist = 0f, sumSpeed = 0f, maxDist = 0f;
            var onEdge = 0;
            var min = _arena.WorldMin;
            var max = _arena.WorldMax;
            for (var i = 0; i < n; i++)
            {
                var p = _position[i];
                var dist = math.distance(p.xz, target.xz);
                sumDist += dist;
                maxDist = math.max(maxDist, dist);
                sumSpeed += math.length(_velocity[i]);
                if (p.x <= min.x + 1e-3f || p.x >= max.x - 1e-3f ||
                    p.z <= min.y + 1e-3f || p.z >= max.y - 1e-3f) onEdge++;
            }
            return $"n={n} meanDist={sumDist / n:F2} maxDist={maxDist:F2} meanSpeed={sumSpeed / n:F2} onEdge={onEdge}";
        }
    }
}
