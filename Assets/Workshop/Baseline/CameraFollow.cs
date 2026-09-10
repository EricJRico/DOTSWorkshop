using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Provided. Keeps the camera over the player, holding whatever offset and angle it was set
    /// up with, so the view can be changed without touching this.
    /// </summary>
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] Transform _target;

        Vector3 _offset;

        void Start()
        {
            if (_target != null) _offset = transform.position - _target.position;
        }

        void LateUpdate()
        {
            if (_target == null) return;
            transform.position = _target.position + _offset;
        }
    }
}
