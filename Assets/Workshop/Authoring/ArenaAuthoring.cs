using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Workshop
{
    public class ArenaAuthoring : MonoBehaviour
    {
        public Vector2 Min = new Vector2(-14f, -14f);
        public Vector2 Max = new Vector2(14f, 14f);

        private class Baker : Baker<ArenaAuthoring>
        {
            public override void Bake(ArenaAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new ArenaBounds
                {
                    Min = new float2(authoring.Min.x, authoring.Min.y),
                    Max = new float2(authoring.Max.x, authoring.Max.y)
                });
            }
        }
    }
}
