using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Workshop
{
    /// <summary>
    /// LAB 2b. Steers every enemy toward the player.
    ///
    /// Note where the player position is read: on the MAIN THREAD, in OnUpdate, then
    /// handed to the job as a plain float3. Jobs cannot call SystemAPI.GetSingleton -
    /// they receive values, not access to the world.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ChaseSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PlayerTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // TODO WORKSHOP 2b-1: find the player with SystemAPI.GetSingletonEntity<PlayerTag>()
            //   and read its LocalTransform.Position - ON THE MAIN THREAD.
            //
            // TODO WORKSHOP 2b-2: schedule ChaseJob with DeltaTime and PlayerPosition,
            //   assigning the returned handle back to state.Dependency.
        }

        [BurstCompile]
        public partial struct ChaseJob : IJobEntity
        {
            public float DeltaTime;
            public float3 PlayerPosition;

            private void Execute(ref LocalTransform transform, in MoveSpeed speed, in EnemyTag _)
            {
                // TODO WORKSHOP 2b-3: build the vector from this enemy to PlayerPosition and
                //   flatten it by setting .y to 0.
                //
                // TODO WORKSHOP 2b-4: return early if the squared length is near zero -
                //   normalizing a zero vector produces NaN and the enemy vanishes.
                //
                // TODO WORKSHOP 2b-5: normalize and move by speed.Value * DeltaTime.
                //   Hint: math.rsqrt(lengthSq) is a cheap reciprocal square root.
            }
        }
    }
}
