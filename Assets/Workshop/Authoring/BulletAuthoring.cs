using Unity.Entities;
using UnityEngine;

namespace Workshop
{
    public class BulletAuthoring : MonoBehaviour
    {
        public float MoveSpeed = 25f;
        public float Radius = 0.2f;
        public float Lifetime = 2f;

        private class Baker : Baker<BulletAuthoring>
        {
            public override void Bake(BulletAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<BulletTag>(entity);
                AddComponent(entity, new MoveSpeed { Value = authoring.MoveSpeed });
                AddComponent(entity, new Radius { Value = authoring.Radius });
                AddComponent(entity, new Lifetime { Value = authoring.Lifetime });
            }
        }
    }
}
