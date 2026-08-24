using Unity.Burst;
using Unity.Mathematics;

namespace Workshop
{
    /// <summary>
    /// Shared helpers for the spatial hash broadphase. Provided to you - the hash must
    /// agree exactly between GridBuildSystem and CollisionSystem, so it lives in one
    /// place and nobody rewrites it.
    /// </summary>
    [BurstCompile]
    public static class GridUtil
    {
        /// <summary>
        /// Cell edge length. At least twice the largest collision radius, so a circle can
        /// only ever overlap the cell it is centred in.
        /// </summary>
        public const float CellSize = 2f;

        /// <summary>Hashes a world position into a grid cell key. The Y axis is ignored.</summary>
        public static int Hash(float3 position)
        {
            var cell = (int2)math.floor(new float2(position.x, position.z) / CellSize);
            return (int)math.hash(cell);
        }

        /// <summary>
        /// A deterministic point scattered in the ring between two radii, on the XZ plane.
        /// Used for the starting population, which must be visible on screen.
        /// </summary>
        public static float3 ScatterPosition(uint seed, float minRadius, float maxRadius)
        {
            var random = Random.CreateFromIndex(seed);
            var angle = random.NextFloat(0f, 2f * math.PI);
            // Square-root keeps the scatter even in area rather than clustered at the centre.
            var t = math.sqrt(random.NextFloat(0f, 1f));
            var radius = math.lerp(minRadius, maxRadius, t);
            return new float3(math.cos(angle) * radius, 0f, math.sin(angle) * radius);
        }

        /// <summary>A deterministic point on a circle of the given radius, on the XZ plane.</summary>
        public static float3 RingPosition(uint seed, float radius)
        {
            var random = Random.CreateFromIndex(seed);
            var angle = random.NextFloat(0f, 2f * math.PI);
            return new float3(math.cos(angle) * radius, 0f, math.sin(angle) * radius);
        }
    }
}
