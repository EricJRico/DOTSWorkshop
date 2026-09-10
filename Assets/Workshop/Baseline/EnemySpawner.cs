using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Provided. Instantiates the enemy population at the authored spawn points and hands the
    /// Transforms back to whoever asked, so the room only ever edits the loop that moves them.
    ///
    /// Each point is a camp with its own size and its own share of the crowd, so enemies arrive
    /// from several directions in uneven groups instead of as one ring closing in together. The
    /// population is still fixed at load - the points decide where they start, not how many
    /// there are.
    /// </summary>
    public class EnemySpawner : MonoBehaviour
    {
        /// <summary>One camp: where it sits on the field, how wide it scatters, and how many
        /// enemies come from it.</summary>
        [System.Serializable]
        public struct SpawnPoint
        {
            [Tooltip("Where the camp sits on the field, in world X and Z.")]
            public Vector2 Position;

            [Tooltip("How far enemies scatter around the camp. At 0 they all start on the " +
                     "same spot and walk in as one line.")]
            [Range(0f, 30f)] public float Radius;

            [Tooltip("How many enemies come from this camp.")]
            [Min(0)] public int Count;
        }

        [SerializeField] GameObject _enemyPrefab;

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

        static readonly int ColorId = Shader.PropertyToID("_Color");

        Material[] _materials;

        [Header("Camps (out of shot, around the field)")]
        [SerializeField] SpawnPoint[] _spawnPoints =
        {
            new SpawnPoint { Position = new Vector2(-45f, -28f), Radius = 8f, Count = 900 },
            new SpawnPoint { Position = new Vector2(45f, -30f), Radius = 8f, Count = 700 },
            new SpawnPoint { Position = new Vector2(48f, 26f), Radius = 8f, Count = 1100 },
            new SpawnPoint { Position = new Vector2(-42f, 30f), Radius = 8f, Count = 800 },
            new SpawnPoint { Position = new Vector2(0f, -36f), Radius = 8f, Count = 800 },
            new SpawnPoint { Position = new Vector2(0f, 36f), Radius = 8f, Count = 700 }
        };

        [Tooltip("Each enemy sits at a random height up to this. All at zero the crowd reads as " +
                 "one moving surface instead of a lot of bodies.")]
        [Range(0f, 4f)] [SerializeField] float _maxHeight = 1f;

        [Tooltip("Fixed, so every machine in the room scatters the same way.")]
        [SerializeField] int _seed = 1;

        /// <summary>Spawns the population and returns it. Called once, by whoever moves them.</summary>
        public Transform[] Spawn()
        {
            var enemies = new Transform[TotalCount()];
            var random = new System.Random(_seed);
            var parent = transform;
            BuildMaterials();

            var next = 0;
            foreach (var point in _spawnPoints)
            {
                for (var i = 0; i < point.Count; i++)
                {
                    var angle = (float)random.NextDouble() * Mathf.PI * 2f;
                    // sqrt, or the camp bunches up in its middle: area grows with radius.
                    var radius = point.Radius * Mathf.Sqrt((float)random.NextDouble());
                    var height = (float)random.NextDouble() * _maxHeight;
                    var position = new Vector3(
                        point.Position.x + Mathf.Cos(angle) * radius,
                        height,
                        point.Position.y + Mathf.Sin(angle) * radius);

                    var enemy = Instantiate(_enemyPrefab, position, Quaternion.identity, parent);
                    enemies[next++] = enemy.transform;

                    if (_materials != null)
                        enemy.GetComponent<MeshRenderer>().sharedMaterial =
                            _materials[random.Next(_materials.Length)];
                }
            }

            return enemies;
        }

        /// <summary>The whole population, which is the camps added up.</summary>
        int TotalCount()
        {
            if (_spawnPoints == null) return 0;

            var total = 0;
            foreach (var point in _spawnPoints) total += point.Count;
            return total;
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
                _materials[i].SetColor(ColorId, _colours[i]);
            }
        }

#if UNITY_EDITOR
        /// <summary>Each camp where it sits and how wide it scatters, with its share of the
        /// crowd written beside it, so the spread can be seen against the camera's view.</summary>
        void OnDrawGizmos()
        {
            if (_spawnPoints == null) return;

            UnityEditor.Handles.color = new Color(0.95f, 0.45f, 0.12f, 0.9f);
            foreach (var point in _spawnPoints)
            {
                var centre = new Vector3(point.Position.x, 0f, point.Position.y);
                UnityEditor.Handles.DrawWireDisc(centre, Vector3.up, point.Radius);
                UnityEditor.Handles.Label(centre, point.Count.ToString());
            }
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
