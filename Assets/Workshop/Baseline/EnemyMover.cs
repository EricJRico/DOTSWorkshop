using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Workshop
{
    [BurstCompile]
    public struct MoveJob : IJobParallelForTransform
    {
        public NativeArray<float3> RespawnOffsets;
        public NativeArray<float> Speeds;
        public NativeArray<int> Hit;

        public float3 Target;
        public float DeltaTime;
        public float HitRadiusSq;
        public float2 ArenaMin;
        public float2 ArenaMax;

        public void Execute(int i, TransformAccess transform)
        {
            float3 position = transform.position;

            var dir = Target - position;
            dir.y = 0f;
            Hit[i] = 0;

            if (math.lengthsq(dir) < HitRadiusSq)
            {
                var respawnPoint = Target + RespawnOffsets[i];
                position = new float3(
                    math.clamp(respawnPoint.x, ArenaMin.x, ArenaMax.x),
                    respawnPoint.y,
                    math.clamp(respawnPoint.z, ArenaMin.y, ArenaMax.y));
                Hit[i] = 1;
                transform.position = position;
                return;
            }

            position += math.normalize(dir) * (Speeds[i] * DeltaTime);
            transform.position = position;
        }
    }

    /// <summary>
    /// LAB 1, the file the room edits, and the only enemy code in the scene. Every enemy is a
    /// GameObject, so the job is handed the Transforms and moves them on the worker threads.
    ///
    /// An enemy that reaches the player hits it and dies doing it, which sends it back to the ring
    /// it came from. Without that the whole crowd ends up standing inside itself on top of the
    /// player; with it the crowd is a stream, and no crowd code is needed to keep it looking right.
    ///
    /// Block A turns this loop into a job, then Bursts it, then runs it across the worker
    /// threads. Nothing here touches Entities - a job runs on plain arrays.
    /// </summary>
    public class EnemyMover : MonoBehaviour
    {
        /// <summary>
        /// Raised once a frame with the number of enemies that reached the player this frame.
        /// This file knows nothing about health or the HUD; whatever cares about being hit
        /// subscribes.
        /// </summary>
        internal event System.Action<int> PlayerHit;

        [SerializeField] private Transform _player;
        [SerializeField] private EnemySpawner _spawner;
        [SerializeField] private ArenaSettings _arena;
        [SerializeField] private float _speed = 2.5f;

        [Tooltip("How close an enemy has to get to hit the player and die doing it.")]
        [SerializeField] private float _hitRadius = 0.6f;

        [Tooltip("How much enemy speeds vary either side of Speed. At 0 the whole crowd starts " +
                 "on the ring together, arrives together and dies together, and the horde " +
                 "pulses instead of streaming.")]
        [SerializeField] private float _speedSpread = 0.4f;

        [SerializeField] private int _seed = 1;

        private Transform[] _enemies;
        private TransformAccessArray _access;
        private NativeArray<float3> _respawnOffsets;
        private NativeArray<float> _speeds;
        private NativeArray<int> _hit;

        private void Start()
        {
            _enemies = _spawner.Spawn();
            var n = _enemies.Length;

            // Each enemy's spot on the ring, held as an offset from the player rather than a world
            // point, so it comes back in from off screen however far the player has walked.
            _access = new TransformAccessArray(_enemies);
            _respawnOffsets = new NativeArray<float3>(n, Allocator.Persistent);
            _speeds = new NativeArray<float>(n, Allocator.Persistent);
            _hit = new NativeArray<int>(n, Allocator.Persistent);

            var random = new System.Random(_seed);
            var origin = _player.position;

            for (var i = 0; i < n; i++)
            {
                _respawnOffsets[i] = (float3)(_enemies[i].position - origin);
                _speeds[i] = _speed * (1f + _speedSpread * (2f * (float)random.NextDouble() - 1f));
            }
        }

        private void OnDestroy()
        {
            // A NativeArray is not garbage collected.
            if (_access.isCreated) _access.Dispose();
            if (_respawnOffsets.IsCreated) _respawnOffsets.Dispose();
            if (_speeds.IsCreated) _speeds.Dispose();
            if (_hit.IsCreated) _hit.Dispose();
        }

        private void Update()
        {
            if (_enemies == null || _player == null) return;

            var target = _player.position;
            var dt = Time.deltaTime;
            var hitSq = _hitRadius * _hitRadius;

            new MoveJob
            {
                RespawnOffsets = _respawnOffsets,
                Speeds = _speeds,
                Hit = _hit,
                Target = target,
                DeltaTime = dt,
                HitRadiusSq = hitSq,
                ArenaMin = _arena.Min,
                ArenaMax = _arena.Max
            }.Schedule(_access).Complete();

            var hits = 0;
            for (var i = 0; i < _hit.Length; i++) hits += _hit[i];

            // Once a frame with the total, not once per enemy: a job can count into an int the
            // same way, and nothing in the loop has to touch another component.
            if (hits > 0) PlayerHit?.Invoke(hits);
        }
    }
}
