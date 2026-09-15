using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Workshop
{
    public class SpawnerAuthoring : MonoBehaviour
    {
        [SerializeField] private GameObject _enemyPrefab;
        [SerializeField] private int _count = 20000;
        [SerializeField] private float _radius = 55f;
        [SerializeField] private float _maxHeight = 1f;
        [SerializeField] private float _speedSpread = 0.4f;
        [SerializeField] private float _hitRadius = 0.6f;
        [SerializeField] private ArenaSettings _arena;
        
        private class Baker : Baker<SpawnerAuthoring>
        {
            public override void Bake(SpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
 
                AddComponent(entity, new EnemyPrefab
                {
                    Value = GetEntity(authoring._enemyPrefab, TransformUsageFlags.Dynamic)
                });
                AddComponent(entity, new SpawnSettings
                {
                    Count = authoring._count,
                    Radius = authoring._radius,
                    Height = authoring._maxHeight,
                    SpeedSpread = authoring._speedSpread,
                    HitRadius = authoring._hitRadius
                });
                
                AddComponent(entity, new ArenaBounds
                {
                    Min = authoring._arena.Min,
                    Max = authoring._arena.Max
                });
            }
        }
    }

    public struct EnemyPrefab : IComponentData
    {
        public Entity Value;
    }
 
    public struct SpawnSettings : IComponentData
    {
        public int Count;
        public float Radius;
        public float Height;
        public float SpeedSpread;
        public float HitRadius;
    }
    
    public struct ArenaBounds : IComponentData
    {
        public float2 Min;
        public float2 Max;
    }
}