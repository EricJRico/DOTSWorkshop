using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Workshop
{
    /// <summary>
    /// Spawns the starting enemy population once, on the first frame. Provided to you.
    ///
    /// This runs on the main thread in OnStartRunning, so EntityManager.Instantiate is
    /// legal here - no job is in flight for it to invalidate. Contrast with SpawnSystem
    /// (Lab 2), which spawns from OnUpdate every frame and therefore MUST use an
    /// EntityCommandBuffer.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct InitialSpawnSystem : ISystem, ISystemStartStop
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SpawnSettings>();
            state.RequireForUpdate<EnemyPrefab>();
        }

        /// <summary>
        /// Only called because this struct implements ISystemStartStop. An ISystem that
        /// declares OnStartRunning WITHOUT that interface compiles fine and is silently
        /// never called - a classic first-day DOTS trap.
        /// </summary>
        [BurstCompile]
        public void OnStartRunning(ref SystemState state)
        {
            var settings = SystemAPI.GetSingleton<SpawnSettings>();
            var prefab = SystemAPI.GetSingleton<EnemyPrefab>().Value;

            if (settings.InitialCount <= 0)
            {
                return;
            }

            var spawned = state.EntityManager.Instantiate(
                prefab, settings.InitialCount, Allocator.Temp);

            for (var i = 0; i < spawned.Length; i++)
            {
                // Scattered across the arena, not on the far spawn ring, so the
                // starting population is actually on screen.
                var position = GridUtil.ScatterPosition((uint)(i + 1), 5f, 13f);
                state.EntityManager.SetComponentData(
                    spawned[i], LocalTransform.FromPosition(position));
            }

            spawned.Dispose();
        }

        [BurstCompile]
        public void OnStopRunning(ref SystemState state) { }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Spawning happens once in OnStartRunning. Nothing to do per frame.
            state.Enabled = false;
        }
    }
}
