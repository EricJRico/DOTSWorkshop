using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
 
namespace Workshop
{
    [BurstCompile]
    public partial struct MoveSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var dt = SystemAPI.Time.DeltaTime;
            var direction = new float3(0f, 0f, -1f);
            
            foreach (var (transform, speed) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRO<MoveSpeed>>()
                         .WithAll<EnemyTag>())
            {
                transform.ValueRW.Position += direction * speed.ValueRO.Value * dt;
            }
        }
    }
}