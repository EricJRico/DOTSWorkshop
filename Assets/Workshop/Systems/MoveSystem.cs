using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
 
namespace Workshop
{
    [BurstCompile]
    public partial struct MoveSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnSettings>();
            state.RequireForUpdate<ArenaBounds>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<PlayerPosition>(out var player)) return;
            
            var dt = SystemAPI.Time.DeltaTime;
            var settings = SystemAPI.GetSingleton<SpawnSettings>();
            var arena = SystemAPI.GetSingleton<ArenaBounds>();
            state.Dependency = new MoveJob()
            {
                DeltaTime = dt,
                Target = player.Value,
                HitRadiusSq = settings.HitRadius * settings.HitRadius,
                ArenaMin =  arena.Min,
                ArenaMax = arena.Max
            }.ScheduleParallel(state.Dependency);
        }
        
        [BurstCompile]
        [WithAll(typeof(EnemyTag))]
        private partial struct MoveJob : IJobEntity
        {
            public float DeltaTime;
            public float3 Target;
            public float HitRadiusSq;
            public float2 ArenaMin;
            public float2 ArenaMax;
        
            private void Execute(ref LocalTransform transform, in MoveSpeed speed, in RespawnOffset respawn)
            {
                var dir = Target - transform.Position;
                dir.y = 0f;
                if (math.lengthsq(dir) < HitRadiusSq)
                {
                    var respawnPoint = Target + respawn.Value;
                    transform.Position = new float3(
                        math.clamp(respawnPoint.x, ArenaMin.x, ArenaMax.x),
                        respawnPoint.y,
                        math.clamp(respawnPoint.z, ArenaMin.y, ArenaMax.y));
                    return;
                }
                
                transform.Position += math.normalize(dir) * speed.Value * DeltaTime;
            }
        }
    }
}