using UnityEngine;
using UnityEngine.InputSystem;

namespace Workshop
{
    /// <summary>
    /// WASD moves the capsule. Provided.
    ///
    /// Unbounded on purpose: enemies spawn on a ring around the player and the camera follows, so
    /// there is no edge to hold the player inside.
    /// </summary>
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] float _speed = 8f;

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
            transform.position += new Vector3(move.x * step, 0f, move.y * step);
        }
    }
}
