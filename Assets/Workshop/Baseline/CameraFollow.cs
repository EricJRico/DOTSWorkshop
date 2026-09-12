using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Provided. Keeps the camera over the player, holding whatever offset and angle it was set up
    /// with, and stops following once the arena edge reaches the side of the screen.
    ///
    /// How much ground the camera sees depends on its height, its angle, its field of view and the
    /// shape of the window, so the point where it should stop is measured from the camera rather
    /// than authored: the four corners of the view are traced down to the ground and the arena is
    /// held outside that patch.
    /// </summary>
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform _target;
        [SerializeField] private ArenaSettings _arena;

        [Tooltip("Extra ground kept in shot past the arena edge, in world units. At 0 the wall " +
                 "sits exactly on the edge of the screen.")]
        [SerializeField] private Vector2 _inset = Vector2.zero;

        [Tooltip("How far a corner of the view is allowed to reach when it points at or above " +
                 "the horizon, which happens if the camera is pitched down only slightly.")]
        [Min(1f)] [SerializeField] private float _maxViewDistance = 500f;

        private Camera _camera;
        private Vector3 _offset;

        // The visible patch of ground, as distances from the camera's own position, and the
        // camera state it was measured at. Only remeasured when one of those changes.
        private float _left, _right, _back, _forward;
        private float _measuredAspect, _measuredFov, _measuredGround;
        private Quaternion _measuredRotation;
        private bool _measured;

        private void Start()
        {
            _camera = GetComponent<Camera>();
            if (_target != null) _offset = transform.position - _target.position;
        }

        private void LateUpdate()
        {
            if (_target == null) return;

            var focus = _target.position;
            if (_arena != null && _camera != null)
            {
                Measure(focus.y);

                focus.x = Hold(focus.x, _arena.Min.x + _left - _inset.x, _arena.Max.x - _right + _inset.x);
                focus.z = Hold(focus.z, _arena.Min.y + _back - _inset.y, _arena.Max.y - _forward + _inset.y);
            }

            transform.position = focus + _offset;
        }

        /// <summary>
        /// Keeps a value between two edges, and centres it when the arena is narrower than the
        /// view, where the two edges have crossed over.
        /// </summary>
        private static float Hold(float value, float min, float max) =>
            min > max ? (min + max) * 0.5f : Mathf.Clamp(value, min, max);

        /// <summary>
        /// Traces the four corners of the view down to the ground and records how far the patch
        /// reaches either side of the camera. Pitched back, the far edge reaches further than the
        /// near one, so the four distances are kept apart rather than as one margin.
        /// </summary>
        private void Measure(float groundHeight)
        {
            if (_measured
                && Mathf.Approximately(_measuredAspect, _camera.aspect)
                && Mathf.Approximately(_measuredFov, _camera.fieldOfView)
                && Mathf.Approximately(_measuredGround, groundHeight)
                && transform.rotation == _measuredRotation)
                return;

            var ground = new Plane(Vector3.up, new Vector3(0f, groundHeight, 0f));

            // Measured from where the camera will be once it has followed, not where it still is
            // this frame, and as distances from the followed point, because that is what gets
            // clamped. Pitched back, the two are not above each other.
            var eye = new Vector3(0f, groundHeight, 0f) + _offset;

            var minX = 0f;
            var maxX = 0f;
            var minZ = 0f;
            var maxZ = 0f;

            for (var corner = 0; corner < 4; corner++)
            {
                var viewport = new Vector3(corner % 2, corner / 2, 0f);
                var ray = new Ray(eye, _camera.ViewportPointToRay(viewport).direction);

                // At or above the horizon there is no ground to hit, so the corner is taken as
                // far as it is allowed to reach instead.
                var distance = ground.Raycast(ray, out var hit) ? Mathf.Min(hit, _maxViewDistance) : _maxViewDistance;
                var point = ray.GetPoint(distance) - new Vector3(0f, groundHeight, 0f);

                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minZ = Mathf.Min(minZ, point.z);
                maxZ = Mathf.Max(maxZ, point.z);
            }

            _left = -minX;
            _right = maxX;
            _back = -minZ;
            _forward = maxZ;

            _measuredAspect = _camera.aspect;
            _measuredFov = _camera.fieldOfView;
            _measuredGround = groundHeight;
            _measuredRotation = transform.rotation;
            _measured = true;
        }
    }
}
