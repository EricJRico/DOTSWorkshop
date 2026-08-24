using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Workshop
{
    /// <summary>
    /// LAB 1. Drifts every enemy along a fixed direction.
    ///
    /// This is your first IJobEntity. The job runs once per matching entity, in
    /// parallel, on Burst-compiled code. You do not write the loop - Execute IS the loop
    /// body, and the parameters declare which components this job reads and writes.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MoveSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new MoveJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                Direction = new float3(0f, 0f, -1f)
            }.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        public partial struct MoveJob : IJobEntity
        {
            public float DeltaTime;
            public float3 Direction;

            private void Execute(ref LocalTransform transform, in MoveSpeed speed, in EnemyTag _)
            {
                // TODO WORKSHOP 1a: move this entity along Direction by
                //   speed.Value * DeltaTime.
                // Hint: transform.Position is a float3, and Direction is already normalized.
            }
        }
    }
}
