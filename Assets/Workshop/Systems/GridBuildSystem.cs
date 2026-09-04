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
    /// frame, growing only when the enemy count exceeds its capacity. We never re-allocate.
    ///
    /// The other correct answer is a fresh map each frame from state.WorldUpdateAllocator,
    /// which is what Unity's own Boids sample does. That allocator rewinds every world
    /// update, so it costs almost nothing and never needs Dispose. Neither choice shows up
    /// in the Profiler's GC Alloc column - native allocations are not GC allocations. What
    /// you are avoiding here is the re-allocation work itself, not garbage.
    /// </summary>
    [BurstCompile]
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
            _enemyQuery = SystemAPI.QueryBuilder()
                .WithAll<EnemyTag, LocalTransform>()
                .Build();

            // Publish the map so CollisionSystem can read it. CreateSingleton is the
            // generic, Burst-friendly form - CreateEntity(typeof(T)) would build a managed
            // ComponentType[], which Burst rejects (BC1028).
            state.EntityManager.CreateSingleton(new EnemyGrid { Value = _grid }, "EnemyGrid");
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
        [WithAll(typeof(EnemyTag))]
        public partial struct BuildGridJob : IJobEntity
        {
            public NativeParallelMultiHashMap<int, Entity>.ParallelWriter Writer;

            // LocalTransform is not just data we need - it is what makes ECS order this
            // job against CollisionSystem's. ECS chains jobs on the COMPONENTS they touch;
            // it does not know the two systems share the map. Drop LocalTransform from this
            // signature and the ordering guarantee goes with it.
            private void Execute(Entity entity, in LocalTransform transform)
            {
                // TODO WORKSHOP 3b-4: add this entity to the map under its cell key.
                //   Hint: GridUtil.Hash(transform.Position) gives you the key.
            }
        }
    }
}
