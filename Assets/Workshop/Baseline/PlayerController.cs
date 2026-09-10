using UnityEngine;
using UnityEngine.InputSystem;

namespace Workshop
{
    /// <summary>
    /// WASD moves the capsule, held inside the arena. Provided.
    ///
    /// A clamp rather than a collider: the player is moved by writing its position, which goes
    /// straight through a collider, and the entities half clamps against the same two numbers.
    /// </summary>
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] ArenaSettings _arena;
        [SerializeField] float _speed = 8f;

        [Tooltip("How far the player's centre stops short of the wall, so its body does not " +
                 "straddle the edge.")]
        [SerializeField] float _radius = 0.5f;

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            var move = Vector2.zero;
            if (keyboard.wKey.isPressed) move.y += 1f;
            if (keyboard.sKey.isPressed) move.y -= 1f;
            if (keyboard.dKey.isPressed) move.x += 1f;
            if (keyboard.aKey.isPressed) move.x -= 1f;
            if (move.sqrMagnitude > 1f) move.Normalize();

            var step = _speed * Time.deltaTime;
            var position = transform.position + new Vector3(move.x * step, 0f, move.y * step);
            transform.position = _arena != null ? _arena.Clamp(position, _radius) : position;
        }
    }
}
