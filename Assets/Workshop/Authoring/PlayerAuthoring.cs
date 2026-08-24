using Unity.Entities;
using UnityEngine;

namespace Workshop
{
    public class PlayerAuthoring : MonoBehaviour
    {
        public float MoveSpeed = 8f;
        public float FireInterval = 0.1f;

        private class Baker : Baker<PlayerAuthoring>
        {
            public override void Bake(PlayerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<PlayerTag>(entity);
                AddComponent(entity, new MoveSpeed { Value = authoring.MoveSpeed });
                AddComponent(entity, new FireCooldown
                {
                    Interval = authoring.FireInterval,
                    Timer = 0f
                });
                AddComponent<PlayerInput>(entity);
            }
        }
    }
}
