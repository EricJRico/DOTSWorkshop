using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Workshop
{
    /// <summary>
    /// LAB 3c. Destroys bullet/enemy pairs that overlap.
    ///
    /// Two paths, identical results, very different frame times:
    ///   - the grid path tests each bullet against only the enemies in its own cell
    ///   - the naive path tests every bullet against every enemy - O(n*m)
    ///
    /// NaiveCollisionJob below is written for you, so you have something to measure
    /// against. Toggle SpawnSettings.UseNaiveCollision on the Spawner to switch.
    /// </summary>
    [BurstCompile]
    [UpdateAfter(typeof(GridBuildSystem))]
    public partial struct CollisionSystem : ISystem
    {
        private EntityQuery _enemyQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<EnemyGrid>();
            state.RequireForUpdate<SpawnSettings>();

            _enemyQuery = SystemAPI.QueryBuilder()
                .WithAll<EnemyTag, LocalTransform, Radius>()
                .Build();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var settings = SystemAPI.GetSingleton<SpawnSettings>();

            var ecb = SystemAPI
                .GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged)
                .AsParallelWriter();

            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true);
            var radiusLookup = SystemAPI.GetComponentLookup<Radius>(true);

            if (settings.UseNaiveCollision)
            {
                state.Dependency = new NaiveCollisionJob
                {
                    Enemies = _enemyQuery.ToEntityArray(state.WorldUpdateAllocator),
                    TransformLookup = transformLookup,
                    RadiusLookup = radiusLookup,
                    Ecb = ecb
                }.ScheduleParallel(state.Dependency);
            }
            else
            {
                state.Dependency = new GridCollisionJob
                {
                    Grid = SystemAPI.GetSingleton<EnemyGrid>().Value,
                    TransformLookup = transformLookup,
                    RadiusLookup = radiusLookup,
                    Ecb = ecb
                }.ScheduleParallel(state.Dependency);
            }
        }

        [BurstCompile]
        [WithAll(typeof(BulletTag))]
        public partial struct GridCollisionJob : IJobEntity
        {
            [ReadOnly] public NativeParallelMultiHashMap<int, Entity> Grid;
            [ReadOnly] public ComponentLookup<LocalTransform> TransformLookup;
            [ReadOnly] public ComponentLookup<Radius> RadiusLookup;
            public EntityCommandBuffer.ParallelWriter Ecb;

            private void Execute(
                [ChunkIndexInQuery] int chunkIndex,
                Entity bullet,
                in LocalTransform transform,
                in Radius radius)
            {
                // TODO WORKSHOP 3c-1: hash this bullet's position to a cell key and call
                //   Grid.TryGetFirstValue(key, out var enemy, out var iterator).
                //   Return early if the cell is empty.
                //
                // TODO WORKSHOP 3c-2: loop the cell with Grid.TryGetNextValue. Skip entities
                //   where TransformLookup.HasComponent(enemy) is false - another bullet may
                //   already have queued their destruction this frame.
                //
                // TODO WORKSHOP 3c-3: if the circles overlap, destroy both through Ecb and
                //   return. Compare squared distances against the squared radius sum;
                //   math.distancesq avoids a square root.
                //
                // Look at NaiveCollisionJob below - it does the same test, just against
                // every enemy instead of only the ones in this cell.
            }
        }

        /// <summary>
        /// Provided for comparison. Correct, and hopeless at scale.
        /// </summary>
        [BurstCompile]
        [WithAll(typeof(BulletTag))]
        public partial struct NaiveCollisionJob : IJobEntity
        {
            [ReadOnly] public NativeArray<Entity> Enemies;
            [ReadOnly] public ComponentLookup<LocalTransform> TransformLookup;
            [ReadOnly] public ComponentLookup<Radius> RadiusLookup;
            public EntityCommandBuffer.ParallelWriter Ecb;

            private void Execute(
                [ChunkIndexInQuery] int chunkIndex,
                Entity bullet,
                in LocalTransform transform,
                in Radius radius)
            {
                for (var i = 0; i < Enemies.Length; i++)
                {
                    var enemy = Enemies[i];
                    if (!TransformLookup.HasComponent(enemy))
                    {
                        continue;
                    }

                    var enemyPosition = TransformLookup[enemy].Position;
                    var reach = radius.Value + RadiusLookup[enemy].Value;

                    if (math.distancesq(transform.Position, enemyPosition) <= reach * reach)
                    {
                        Ecb.DestroyEntity(chunkIndex, enemy);
                        Ecb.DestroyEntity(chunkIndex, bullet);
                        return;
                    }
                }
            }
        }
    }
}
