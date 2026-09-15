using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

namespace Workshop
{
    public class EnemyAuthoring : MonoBehaviour
    {
        [SerializeField] private float _moveSpeed = 2.5f;

        private class Baker : Baker<EnemyAuthoring>
        {
            public override void Bake(EnemyAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<EnemyTag>(entity);
                AddComponent(entity, new MoveSpeed { Value = authoring._moveSpeed });
                AddComponent(entity, new EnemyColor { Value = new float4(1f, 1f, 1f, 1f) });
                AddComponent(entity, new RespawnOffset());
            }
        }
    }

    public struct EnemyTag : IComponentData
    {
    }

    public struct MoveSpeed : IComponentData
    {
        public float Value;
    }

    [MaterialProperty("_Color")]
    public struct EnemyColor : IComponentData
    {
        public float4 Value;
    }

    public struct RespawnOffset : IComponentData
    {
        public float3 Value;
    }
}