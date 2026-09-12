using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Provided. Instantiates the enemy population at the authored spawn points and hands the
    /// Transforms back to whoever asked, so the room only ever edits the loop that moves them.
    ///
    /// Enemies start at the spawn points, so they arrive from several directions instead of as
    /// one ring closing in together. Drag empty GameObjects in as the points and move them in
    /// the scene view; Total Enemies is split evenly between them.
    /// </summary>
    public class EnemySpawner : MonoBehaviour
    {

        [SerializeField] private GameObject _enemyPrefab;

        [Tooltip("One colour is picked per enemy at spawn, so the crowd is not all one shade. " +
                 "Each colour becomes one material at load and enemies share it, which keeps the " +
                 "SRP batcher working - a per-enemy property block would break batching, and " +
                 "tinting every frame would cost more than the loop the room is here to profile.")]
        [SerializeField] private Color[] _colours =
        {
            new Color(0.85f, 0.18f, 0.18f),
            new Color(0.95f, 0.45f, 0.12f),
            new Color(0.72f, 0.12f, 0.35f),
            new Color(0.55f, 0.20f, 0.75f)
        };

        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private Material[] _materials;

        [Tooltip("The whole crowd, split evenly between the spawn points. Lower it if this " +
                 "machine cannot keep the scene watchable.")]
        [Min(0)] [SerializeField] private int _totalEnemies = 20000;

        [Header("Spawn points (out of shot, around the field)")]
        [Tooltip("One empty GameObject per point. Move them in the scene view to change where " +
                 "the crowd comes from.")]
        [SerializeField] private Transform[] _spawnPoints;

        [Tooltip("How far enemies scatter around a point. At 0 they all start on the same spot " +
                 "and walk in as one line.")]
        [Range(0f, 30f)] [SerializeField] private float _scatterRadius = 8f;

        [Tooltip("Each enemy sits at a random height up to this. All at zero the crowd reads as " +
                 "one moving surface instead of a lot of bodies.")]
        [Range(0f, 4f)] [SerializeField] private float _maxHeight = 1f;

        [Tooltip("Fixed, so every machine in the room scatters the same way.")]
        [SerializeField] private int _seed = 1;

        /// <summary>Spawns the population and returns it. Called once, by whoever moves them.</summary>
        internal Transform[] Spawn()
        {
            var points = FilledPoints();
            if (points.Length == 0) return System.Array.Empty<Transform>();

            var enemies = new Transform[_totalEnemies];
            var random = new System.Random(_seed);
            var parent = transform;
            BuildMaterials();

            var next = 0;
            for (var point = 0; point < points.Length; point++)
            {
                var centre = points[point].position;
                for (var i = 0; i < PointCount(point, points.Length); i++)
                {
                    var angle = (float)random.NextDouble() * Mathf.PI * 2f;
                    // sqrt, or the crowd bunches up in the middle: area grows with radius.
                    var radius = _scatterRadius * Mathf.Sqrt((float)random.NextDouble());
                    var height = (float)random.NextDouble() * _maxHeight;
                    var position = new Vector3(
                        centre.x + Mathf.Cos(angle) * radius,
                        height,
                        centre.z + Mathf.Sin(angle) * radius);

                    var enemy = Instantiate(_enemyPrefab, position, Quaternion.identity, parent);
                    enemies[next++] = enemy.transform;

                    if (_materials != null)
                        enemy.GetComponent<MeshRenderer>().sharedMaterial =
                            _materials[random.Next(_materials.Length)];
                }
            }

            return enemies;
        }

        /// <summary>The spawn points that are actually filled in, so an empty slot in the list
        /// costs nothing.</summary>
        private Transform[] FilledPoints()
        {
            if (_spawnPoints == null) return System.Array.Empty<Transform>();

            var filled = 0;
            foreach (var point in _spawnPoints) if (point != null) filled++;

            var points = new Transform[filled];
            var next = 0;
            foreach (var point in _spawnPoints) if (point != null) points[next++] = point;
            return points;
        }

        /// <summary>
        /// This point's share of an even split. The first points take the remainder one each, so
        /// the points add up to Total Enemies however the division falls.
        /// </summary>
        private int PointCount(int point, int points) =>
            _totalEnemies / points + (point < _totalEnemies % points ? 1 : 0);

        /// <summary>
        /// One material per colour, copied from whatever the prefab ships with, so the colours
        /// live in the inspector instead of in a folder of near-identical material assets.
        /// </summary>
        private void BuildMaterials()
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
        /// <summary>Each spawn point where it sits and how wide it scatters, so the spread can be seen
        /// against the camera's view.</summary>
        private void OnDrawGizmos()
        {
            if (_spawnPoints == null) return;

            UnityEditor.Handles.color = new Color(0.95f, 0.45f, 0.12f, 0.9f);
            foreach (var point in _spawnPoints)
            {
                if (point == null) continue;

                var centre = new Vector3(point.position.x, 0f, point.position.z);
                UnityEditor.Handles.DrawWireDisc(centre, Vector3.up, _scatterRadius);
            }
        }
#endif

        private void OnDestroy()
        {
            if (_materials == null) return;

            // Created at runtime, so nothing else will clean them up.
            foreach (var material in _materials) Destroy(material);
            _materials = null;
        }
    }
}
