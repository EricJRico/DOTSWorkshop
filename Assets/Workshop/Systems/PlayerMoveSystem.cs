using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Workshop
{
    /// <summary>
    /// Moves the player from the PlayerInput singleton and clamps them to the arena.
    /// Provided to you - this one is not a lab.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PlayerMoveSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ArenaBounds>();
            state.RequireForUpdate<PlayerInput>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var input = SystemAPI.GetSingleton<PlayerInput>();
            var bounds = SystemAPI.GetSingleton<ArenaBounds>();
            var dt = SystemAPI.Time.DeltaTime;

            var move = input.Move;
            if (math.lengthsq(move) > 1f)
            {
                move = math.normalize(move);
            }

            foreach (var (transform, speed) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRO<MoveSpeed>>()
                              .WithAll<PlayerTag>())
            {
                var position = transform.ValueRO.Position;
                position.xz += move * speed.ValueRO.Value * dt;
                position.xz = math.clamp(position.xz, bounds.Min, bounds.Max);
                transform.ValueRW.Position = position;
            }
        }
    }
}
