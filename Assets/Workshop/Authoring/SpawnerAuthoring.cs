using Unity.Entities;
using UnityEngine;

namespace Workshop
{
    public class SpawnerAuthoring : MonoBehaviour
    {
        public GameObject EnemyPrefab;
        public GameObject BulletPrefab;

        public int InitialCount = 1000;
        public int WaveSize = 200;
        public float Interval = 0.5f;
        public float SpawnRadius = 20f;
        public bool UseNaiveCollision;

        private class Baker : Baker<SpawnerAuthoring>
        {
            public override void Bake(SpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                AddComponent(entity, new EnemyPrefab
                {
                    Value = GetEntity(authoring.EnemyPrefab, TransformUsageFlags.Dynamic)
                });
                AddComponent(entity, new BulletPrefab
                {
                    Value = GetEntity(authoring.BulletPrefab, TransformUsageFlags.Dynamic)
                });
                AddComponent(entity, new SpawnSettings
                {
                    InitialCount = authoring.InitialCount,
                    WaveSize = authoring.WaveSize,
                    Interval = authoring.Interval,
                    SpawnRadius = authoring.SpawnRadius,
                    Timer = 0f,
                    UseNaiveCollision = authoring.UseNaiveCollision
                });
            }
        }
    }
}
