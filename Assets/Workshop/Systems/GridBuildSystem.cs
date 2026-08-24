using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Workshop
{
    /// <summary>
    /// LAB 3b. Rebuilds the enemy spatial hash every frame.
    ///
    /// The map is Allocator.Persistent, allocated ONCE in OnCreate and Clear()ed per
    /// frame, growing only when the enemy count exceeds its capacity. Allocating a fresh
    /// TempJob map every frame would also work - and would be the habit this workshop
    /// teaches against. Watch the GC Alloc column.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ChaseSystem))]
    public partial struct GridBuildSystem : ISystem
    {
        private NativeParallelMultiHashMap<int, Entity> _grid;
        private EntityQuery _enemyQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _grid = new NativeParallelMultiHashMap<int, Entity>(4096, Allocator.Persistent);

            // Built once. Never construct a query in OnUpdate.
            _enemyQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EnemyTag, LocalTransform>()
                .Build(ref state);

            var singleton = state.EntityManager.CreateEntity(typeof(EnemyGrid));
            state.EntityManager.SetComponentData(singleton, new EnemyGrid { Value = _grid });
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_grid.IsCreated)
            {
                _grid.Dispose();
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // TODO WORKSHOP 3b-1: Clear() the grid. Do NOT allocate a new one -
            //   _grid is Allocator.Persistent and is reused every frame.
            //
            // TODO WORKSHOP 3b-2: if _grid.Capacity is smaller than
            //   _enemyQuery.CalculateEntityCount(), grow it, then re-publish the singleton
            //   with SystemAPI.SetSingleton(new EnemyGrid { Value = _grid }).
            //
            // TODO WORKSHOP 3b-3: schedule BuildGridJob over _enemyQuery with
            //   _grid.AsParallelWriter(), assigning the handle back to state.Dependency.
        }

        [BurstCompile]
        public partial struct BuildGridJob : IJobEntity
        {
            public NativeParallelMultiHashMap<int, Entity>.ParallelWriter Writer;

            private void Execute(Entity entity, in LocalTransform transform, in EnemyTag _)
            {
                // TODO WORKSHOP 3b-4: add this entity to the map under its cell key.
                //   Hint: GridUtil.Hash(transform.Position) gives you the key.
            }
        }
    }
}
