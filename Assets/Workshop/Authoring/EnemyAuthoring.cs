using Unity.Entities;
using UnityEngine;

namespace Workshop
{
    public class EnemyAuthoring : MonoBehaviour
    {
        public float MoveSpeed = 2.5f;
        public float Radius = 0.5f;

        private class Baker : Baker<EnemyAuthoring>
        {
            public override void Bake(EnemyAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<EnemyTag>(entity);
                AddComponent(entity, new MoveSpeed { Value = authoring.MoveSpeed });
                AddComponent(entity, new Radius { Value = authoring.Radius });
            }
        }
    }
}
