using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Workshop
{
    /// <summary>
    /// LAB 3a. Auto-fires bullets along the aim vector, advances live bullets, and
    /// destroys expired ones.
    ///
    /// The bullet's travel direction is stored in LocalTransform.Rotation, so the job
    /// needs no extra component - math.forward(rotation) recovers it.
    /// </summary>
    [BurstCompile]
    public partial struct FireSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PlayerTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // TODO WORKSHOP 3a-1: get the ECB from the BeginSimulation singleton.
            //   var ecb = SystemAPI
            //       .GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
            //       .CreateCommandBuffer(state.WorldUnmanaged);
            //
            // TODO WORKSHOP 3a-2: tick FireCooldown.Timer by SystemAPI.Time.DeltaTime.
            //   Use SystemAPI.GetComponentRW<FireCooldown>(player) so the write sticks.
            //
            // TODO WORKSHOP 3a-3: when Timer >= Interval and the aim vector is non-zero,
            //   reset the timer and ecb.Instantiate the bullet prefab at the player position,
            //   rotated with quaternion.LookRotationSafe(aim, math.up()).
            //
            // TODO WORKSHOP 3a-4: schedule BulletJob so existing bullets advance and expire.
        }

        [BurstCompile]
        [WithAll(typeof(BulletTag))]
        public partial struct BulletJob : IJobEntity
        {
            public float DeltaTime;
            public EntityCommandBuffer.ParallelWriter Ecb;

            private void Execute(
                [ChunkIndexInQuery] int chunkIndex,
                Entity entity,
                ref LocalTransform transform,
                ref Lifetime lifetime,
                in MoveSpeed speed)
            {
                // TODO WORKSHOP 3a-5: subtract DeltaTime from lifetime.Value.
                //   If it has reached zero, Ecb.DestroyEntity(chunkIndex, entity) and return.
                //
                // TODO WORKSHOP 3a-6: otherwise move the bullet forward.
                //   Hint: math.forward(transform.Rotation) is the direction it is facing.
            }
        }
    }
}
