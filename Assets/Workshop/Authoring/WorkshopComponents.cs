using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Workshop
{
    /// <summary>Zero-size marker baked onto the single player entity. Never mutated.</summary>
    public struct PlayerTag : IComponentData { }

    public struct EnemyTag : IComponentData { }

    public struct BulletTag : IComponentData { }

    /// <summary>Units per second.</summary>
    public struct MoveSpeed : IComponentData
    {
        public float Value;
    }

    /// <summary>Collision radius, used by the circle-vs-circle test.</summary>
    public struct Radius : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// Written every frame by PlayerInputSystem, the only managed system in the project.
    /// Read by Burst-compiled systems. Blittable so it can cross that boundary.
    /// </summary>
    public struct PlayerInput : IComponentData
    {
        public float2 Move;
        public float2 Aim;
    }

    public struct ArenaBounds : IComponentData
    {
        public float2 Min;
        public float2 Max;
    }

    public struct EnemyPrefab : IComponentData
    {
        public Entity Value;
    }

    public struct BulletPrefab : IComponentData
    {
        public Entity Value;
    }

    public struct SpawnSettings : IComponentData
    {
        public int InitialCount;
        public int WaveSize;
        public float Interval;
        public float SpawnRadius;
        public float Timer;
        public bool UseNaiveCollision;
    }

    public struct FireCooldown : IComponentData
    {
        public float Interval;
        public float Timer;
    }

    /// <summary>Seconds remaining before the bullet despawns.</summary>
    public struct Lifetime : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// The spatial hash rebuilt every frame by GridBuildSystem and read by CollisionSystem.
    /// The map is Allocator.Persistent, allocated once in OnCreate and only Clear()ed per
    /// frame - reallocating every frame is the exact habit this workshop teaches against.
    /// </summary>
    public struct EnemyGrid : IComponentData
    {
        public NativeParallelMultiHashMap<int, Entity> Value;
    }
}
