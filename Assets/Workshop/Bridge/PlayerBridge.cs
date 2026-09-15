using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
 
namespace Workshop
{
    public struct PlayerPosition : IComponentData { public float3 Value; }
 
    [DefaultExecutionOrder(100)]
    public class PlayerBridge : MonoBehaviour
    {
        private World _world;
        private Entity _entity;
 
        private void Start()
        {
            _world = World.DefaultGameObjectInjectionWorld;
                _entity = _world.EntityManager.CreateEntity(typeof(PlayerPosition));
        }
 
        private void Update()
        {
            if (_world == null || !_world.IsCreated) return;
 
                _world.EntityManager.SetComponentData(
                _entity, new PlayerPosition { Value = transform.position });
        }
    }
}