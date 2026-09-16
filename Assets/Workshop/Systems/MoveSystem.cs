using Unity.Burst;
using Unity.Collections;
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
            state.Dependency = new MoveJob()
            {
                DeltaTime = dt,
                Target = player.Value
            }.ScheduleParallel(state.Dependency);

            state.Dependency.Complete();

            var hitSq = settings.HitRadius * settings.HitRadius;
            var hits = 0;

            foreach (var (transform, entity) in
                     SystemAPI.Query<RefRO<LocalTransform>>()
                         .WithAll<EnemyTag, Alive>()
                         .WithEntityAccess())
            {
                if (math.distancesq(transform.ValueRO.Position, player.Value) < hitSq)
                {
                    state.EntityManager.SetComponentEnabled<Alive>(entity, false);
                    hits++;
                }
            }

            SystemAPI.SetSingleton(new PlayerHits { Value = hits });
        }
        
        [BurstCompile]
        [WithAll(typeof(EnemyTag), typeof(Alive))]
        private partial struct MoveJob : IJobEntity
        {
            public float DeltaTime;
            public float3 Target;

            private void Execute(ref LocalTransform transform, in MoveSpeed speed)
            {
                var dir = Target - transform.Position;
                dir.y = 0f;
                if (math.lengthsq(dir) < 0.0001f) return;

                transform.Position += math.normalize(dir) * speed.Value * DeltaTime;
            }
        }
    }
}