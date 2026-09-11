using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Provided. Keeps the camera over the player, holding whatever offset and angle it was set up
    /// with, and stops following once the arena edge reaches the side of the screen.
    /// </summary>
    internal class CameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform _target;
        [SerializeField] private ArenaSettings _arena;

        [Tooltip("How far inside the arena the followed point stops, per axis. Set it to roughly " +
                 "what the camera can see either side of the player, so the wall lands at the " +
                 "edge of the screen rather than in the middle of it.")]
        [SerializeField] private Vector2 _viewMargin = new Vector2(26f, 14f);

        private Vector3 _offset;

        private void Start()
        {
            if (_target != null) _offset = transform.position - _target.position;
        }

        private void LateUpdate()
        {
            if (_target == null) return;

            var focus = _target.position;
            if (_arena != null)
            {
                focus.x = Mathf.Clamp(focus.x, _arena.Min.x + _viewMargin.x, _arena.Max.x - _viewMargin.x);
                focus.z = Mathf.Clamp(focus.z, _arena.Min.y + _viewMargin.y, _arena.Max.y - _viewMargin.y);
            }

            transform.position = focus + _offset;
        }
    }
}
