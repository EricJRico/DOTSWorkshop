using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;

namespace Workshop
{
    /// <summary>
    /// LAB 2a. Spawns a wave of enemies on a timer.
    ///
    /// Instantiating an entity is a STRUCTURAL CHANGE - it moves entities between chunks
    /// and invalidates every array a running job is holding. That is why you record into
    /// an EntityCommandBuffer instead of calling EntityManager, and why the ECB comes
    /// from a singleton rather than being allocated fresh each frame.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct SpawnSystem : ISystem
    {
        private uint _seed;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnSettings>();
            state.RequireForUpdate<EnemyPrefab>();
            _seed = 1u;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // TODO WORKSHOP 2a-1: get SpawnSettings with SystemAPI.GetSingletonRW so you can
            //   write Timer back, and the prefab with SystemAPI.GetSingleton<EnemyPrefab>().
            //
            // TODO WORKSHOP 2a-2: add SystemAPI.Time.DeltaTime to Timer. Return early if
            //   Timer is still below Interval, otherwise subtract Interval from it.
            //
            // TODO WORKSHOP 2a-3: get an EntityCommandBuffer from the
            //   BeginSimulationEntityCommandBufferSystem.Singleton.
            //
            // TODO WORKSHOP 2a-4: loop WaveSize times, ecb.Instantiate the prefab, and
            //   ecb.SetComponent a LocalTransform at GridUtil.RingPosition(_seed++, radius).
        }
    }
}
