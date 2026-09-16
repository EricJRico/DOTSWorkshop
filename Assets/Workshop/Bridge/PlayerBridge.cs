using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
 
namespace Workshop
{
    public struct PlayerPosition : IComponentData { public float3 Value; }

    public struct PlayerHits : IComponentData { public int Value; }
 
    [DefaultExecutionOrder(100)]
    public class PlayerBridge : MonoBehaviour
    {
        /// <summary>Raised once a frame with the number of enemies that reached the player.</summary>
        internal event System.Action<int> PlayerHit;

        private World _world;
        private Entity _entity;
 
        private void Start()
        {
            _world = World.DefaultGameObjectInjectionWorld;
            _entity = _world.EntityManager.CreateEntity(
                typeof(PlayerPosition), typeof(PlayerHits));
        }
 
        private void Update()
        {
            if (_world == null || !_world.IsCreated) return;
 
                _world.EntityManager.SetComponentData(
                _entity, new PlayerPosition { Value = transform.position });

            var hits = _world.EntityManager.GetComponentData<PlayerHits>(_entity).Value;
            if (hits > 0) PlayerHit?.Invoke(hits);
        }
    }
}