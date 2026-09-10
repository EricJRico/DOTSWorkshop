using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Provided. Instantiates the enemy population on a ring and hands the Transforms back to
    /// whoever asked, so the room only ever edits the loop that moves them.
    ///
    /// The ring sits outside the view on every axis - the camera is orthographic at size 16 on a
    /// 16:9 frame, so its corner reaches about 32.7 units - which is what makes the crowd walk in
    /// instead of popping into shot.
    /// </summary>
    public class EnemySpawner : MonoBehaviour
    {
        [SerializeField] GameObject _enemyPrefab;
        [SerializeField] int _count = 5000;

        [Tooltip("One colour is picked per enemy at spawn, so the crowd is not all one shade. " +
                 "Each colour becomes one material at load and enemies share it, which keeps the " +
                 "SRP batcher working - a per-enemy property block would break batching, and " +
                 "tinting every frame would cost more than the loop the room is here to profile.")]
        [SerializeField] Color[] _colours =
        {
            new Color(0.85f, 0.18f, 0.18f),
            new Color(0.95f, 0.45f, 0.12f),
            new Color(0.72f, 0.12f, 0.35f),
            new Color(0.55f, 0.20f, 0.75f)
        };

        static readonly int SpeedColorId = Shader.PropertyToID("_SpeedColor");

        Material[] _materials;

        [Header("Spawn ring (outside the camera view)")]
        [Range(0f, 80f)] [SerializeField] float _minRadius = 34f;
        [Range(0f, 80f)] [SerializeField] float _maxRadius = 44f;

        [Tooltip("Fixed, so every machine in the room scatters the same way.")]
        [SerializeField] int _seed = 1;

        /// <summary>Spawns the population and returns it. Called once, by whoever moves them.</summary>
        public Transform[] Spawn()
        {
            var enemies = new Transform[_count];
            var random = new System.Random(_seed);
            var parent = transform;
            BuildMaterials();

            for (var i = 0; i < _count; i++)
            {
                var angle = (float)random.NextDouble() * Mathf.PI * 2f;
                // sqrt, or the ring bunches up against its inner edge: area grows with radius.
                var radius = Mathf.Lerp(_minRadius, _maxRadius, Mathf.Sqrt((float)random.NextDouble()));
                var position = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                var enemy = Instantiate(_enemyPrefab, position, Quaternion.identity, parent);
                enemies[i] = enemy.transform;

                if (_materials != null)
                    enemy.GetComponent<MeshRenderer>().sharedMaterial =
                        _materials[random.Next(_materials.Length)];
            }

            return enemies;
        }

        /// <summary>
        /// One material per colour, copied from whatever the prefab ships with, so the colours
        /// live in the inspector instead of in a folder of near-identical material assets.
        /// </summary>
        void BuildMaterials()
        {
            if (_colours == null || _colours.Length == 0) return;

            var source = _enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial;
            _materials = new Material[_colours.Length];

            for (var i = 0; i < _colours.Length; i++)
            {
                _materials[i] = new Material(source);
                _materials[i].SetColor(SpeedColorId, _colours[i]);
            }
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (_maxRadius < _minRadius) _maxRadius = _minRadius;
        }

        /// <summary>The band enemies spawn in, so it can be seen against the camera's view.</summary>
        void OnDrawGizmos()
        {
            UnityEditor.Handles.color = new Color(0.95f, 0.45f, 0.12f, 0.9f);
            UnityEditor.Handles.DrawWireDisc(Vector3.zero, Vector3.up, _minRadius);
            UnityEditor.Handles.DrawWireDisc(Vector3.zero, Vector3.up, _maxRadius);
        }
#endif

        void OnDestroy()
        {
            if (_materials == null) return;

            // Created at runtime, so nothing else will clean them up.
            foreach (var material in _materials) Destroy(material);
            _materials = null;
        }
    }
}
