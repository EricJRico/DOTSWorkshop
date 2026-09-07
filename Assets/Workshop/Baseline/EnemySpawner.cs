using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Spawns the enemy cubes once, in a ring outside the camera's view, and registers each
    /// with the mover. The camera is orthographic at size 16 with a 16:9 aspect, so the far
    /// corner of the view sits about 32.7 units out; the ring starts past that.
    /// </summary>
    public class EnemySpawner : MonoBehaviour
    {
        public GameObject EnemyPrefab;
        public EnemyMover Mover;
        public int Count = 5000;
        public float MinRadius = 34f;
        public float MaxRadius = 44f;
        public int Seed = 1;

        void Start()
        {
            Random.InitState(Seed);
            for (var i = 0; i < Count; i++)
            {
                var angle = Random.Range(0f, Mathf.PI * 2f);
                var t = Mathf.Sqrt(Random.value);
                var radius = Mathf.Lerp(MinRadius, MaxRadius, t);
                var position = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                var enemy = Instantiate(EnemyPrefab, position, Quaternion.identity, transform);
                // SwarmColorizer draws the swarm instanced, so the per-object renderers stay off.
                var renderer = enemy.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.enabled = false;
                Mover.Solver.Register(enemy.transform);
            }
        }
    }
}
