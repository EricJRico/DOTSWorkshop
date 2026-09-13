using Unity.Entities;
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
                AddComponent(entity, new MoveSpeed{ Value = authoring._moveSpeed });
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
}
