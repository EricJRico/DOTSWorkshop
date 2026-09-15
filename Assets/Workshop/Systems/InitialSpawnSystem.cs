using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
 
namespace Workshop
{
    public partial struct InitialSpawnSystem : ISystem
    {
        private static readonly float4[] Palette =
        {
            new float4(0.85f, 0.18f, 0.18f, 1f),
            new float4(0.95f, 0.45f, 0.12f, 1f),
            new float4(0.72f, 0.12f, 0.35f, 1f),
            new float4(0.55f, 0.20f, 0.75f, 1f)
        };
        
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnSettings>();
            state.RequireForUpdate<EnemyPrefab>();
        }
 
        public void OnUpdate(ref SystemState state)
        {
            var settings = SystemAPI.GetSingleton<SpawnSettings>();
            var prefab = SystemAPI.GetSingleton<EnemyPrefab>().Value;

            var size = 
                state.EntityManager.GetComponentData<LocalTransform>(prefab).Scale;
            
            var speed = state.EntityManager
                .GetComponentData<MoveSpeed>(prefab).Value;
            
            var spawned = state.EntityManager.Instantiate(
                prefab, settings.Count, Allocator.Temp);
 
            var random = Random.CreateFromIndex(1);
 
            for (var i = 0; i < spawned.Length; i++)
            {
                var a = random.NextFloat(0f, math.PI * 2f);
                var r = settings.Radius * math.sqrt(random.NextFloat());
                var position = new float3(
                    math.cos(a) * r,
                    random.NextFloat(0f, settings.Height),
                    math.sin(a) * r);
                
                state.EntityManager.SetComponentData(spawned[i],
                    LocalTransform.FromPositionRotationScale(
                        position, quaternion.identity, size));
                
                state.EntityManager.SetComponentData(spawned[i],
                    new MoveSpeed { Value = speed * (1f + settings.SpeedSpread
                        * random.NextFloat(-1f, 1f)) });
                
                state.EntityManager.SetComponentData(spawned[i],
                    new EnemyColor { Value = Palette[random.NextInt(Palette.Length)] });
                
                state.EntityManager.SetComponentData(spawned[i],
                    new RespawnOffset { Value = new float3(
                        math.cos(a) * settings.Radius,
                        position.y,
                        math.sin(a) * settings.Radius)});
            }
 
            spawned.Dispose();
 
            state.Enabled = false;
        }
    }
}