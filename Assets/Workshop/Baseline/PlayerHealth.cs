using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Provided. Owns the player's health and nothing else: it listens for enemies getting
    /// through, regenerates when they are not, and announces its own value. It never touches the
    /// HUD, and nothing outside it writes its health.
    /// </summary>
    internal class PlayerHealth : MonoBehaviour
    {
        /// <summary>Current health as a fraction of the maximum, raised whenever it changes.</summary>
        internal event System.Action<float> Changed;

        [Tooltip("Where hits are reported from.")]
        [SerializeField] private EnemyMover _enemies;

        [SerializeField] private float _maxHealth = 100f;
        [SerializeField] private float _damagePerHit = 0.35f;

        [Header("Debug")]
        [Tooltip("Not gameplay. On, the player heals back up so the scene can be left running " +
                 "on the projector without the bar bottoming out.")]
        [SerializeField] private bool _regenerate;

        [SerializeField] private float _regenPerSecond = 4f;

        private float _health;

        internal float Normalised => _maxHealth > 0f ? _health / _maxHealth : 0f;

        private void Awake()
        {
            _health = _maxHealth;
        }

        private void OnEnable()
        {
            if (_enemies != null) _enemies.PlayerHit += OnPlayerHit;
        }

        private void OnDisable()
        {
            if (_enemies != null) _enemies.PlayerHit -= OnPlayerHit;
        }

        private void Start()
        {
            Changed?.Invoke(Normalised);
        }

        private void Update()
        {
            if (!_regenerate || _health >= _maxHealth) return;
            Set(_health + _regenPerSecond * Time.deltaTime);
        }

        private void OnPlayerHit(int hits)
        {
            Set(_health - hits * _damagePerHit);
        }

        private void Set(float value)
        {
            var clamped = Mathf.Clamp(value, 0f, _maxHealth);
            if (Mathf.Approximately(clamped, _health)) return;

            _health = clamped;
            Changed?.Invoke(Normalised);
        }
    }
}
