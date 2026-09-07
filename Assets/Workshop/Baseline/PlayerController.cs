using UnityEngine;
using UnityEngine.InputSystem;

namespace Workshop
{
    /// <summary>WASD moves the capsule, clamped to the arena. Provided.</summary>
    public class PlayerController : MonoBehaviour
    {
        public ArenaSettings Arena;
        public float Speed = 8f;

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

            var position = transform.position;
            position.x = Mathf.Clamp(position.x + move.x * Speed * Time.deltaTime, Arena.Min.x, Arena.Max.x);
            position.z = Mathf.Clamp(position.z + move.y * Speed * Time.deltaTime, Arena.Min.y, Arena.Max.y);
            transform.position = position;
        }
    }
}
