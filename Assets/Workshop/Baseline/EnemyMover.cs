using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// LAB 1, the file the room edits, and the only enemy code in the scene. Every enemy is a
    /// GameObject, so this walks a Transform[] and moves each one toward the player on the main
    /// thread.
    ///
    /// An enemy that reaches the player hits it and dies doing it, which sends it back to the ring
    /// it came from. Without that the whole crowd ends up standing inside itself on top of the
    /// player; with it the crowd is a stream, and no crowd code is needed to keep it looking right.
    ///
    /// Block A turns this loop into a job, then Bursts it, then runs it across the worker
    /// threads. Nothing here touches Entities - a job runs on plain arrays.
    /// </summary>
    internal class EnemyMover : MonoBehaviour
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
        private Vector3[] _respawnOffsets;
        private float[] _speeds;

        private void Start()
        {
            _enemies = _spawner.Spawn();

            // Each enemy's spot on the ring, held as an offset from the player rather than a world
            // point, so it comes back in from off screen however far the player has walked.
            _respawnOffsets = new Vector3[_enemies.Length];
            _speeds = new float[_enemies.Length];
            var random = new System.Random(_seed);
            var origin = _player.position;

            for (var i = 0; i < _enemies.Length; i++)
            {
                _respawnOffsets[i] = _enemies[i].position - origin;
                _speeds[i] = _speed * (1f + _speedSpread * (2f * (float)random.NextDouble() - 1f));
            }
        }

        private void Update()
        {
            if (_enemies == null || _player == null) return;

            var target = _player.position;
            var dt = Time.deltaTime;
            var hitSq = _hitRadius * _hitRadius;
            var hits = 0;

            for (var i = 0; i < _enemies.Length; i++)
            {
                var dir = target - _enemies[i].position;
                dir.y = 0f;

                if (dir.sqrMagnitude < hitSq)
                {
                    // Clamped, or a cornered player would send them back outside the arena.
                    _enemies[i].position = _arena.Clamp(target + _respawnOffsets[i]);
                    hits++;
                    continue;
                }

                _enemies[i].position += dir.normalized * (_speeds[i] * dt);
            }

            // Once a frame with the total, not once per enemy: a job can count into an int the
            // same way, and nothing in the loop has to touch another component.
            if (hits > 0) PlayerHit?.Invoke(hits);
        }
    }
}
