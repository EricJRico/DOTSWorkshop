using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Provided. Draws the edge of the field, in the game with a line and in the editor with a
    /// gizmo, so the wall the player is clamped to is something you can see.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class Arena : MonoBehaviour
    {
        [SerializeField] ArenaSettings _settings;

        [Tooltip("Fills the field, so inside the wall looks different from outside it.")]
        [SerializeField] Transform _floor;

        [Tooltip("Height of the border line. Above the floor and below the enemies, or it " +
                 "z-fights the floor and draws over the crowd.")]
        [SerializeField] float _lineHeight = 0.02f;

        void Start()
        {
            if (_settings == null) return;

            var line = GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = true;
            line.positionCount = 4;
            line.SetPositions(Corners(_lineHeight));

            if (_floor == null) return;
            var size = _settings.Max - _settings.Min;
            var centre = (_settings.Min + _settings.Max) * 0.5f;
            _floor.position = new Vector3(centre.x, 0f, centre.y);
            // A default quad is one unit across and lies in XY, so it is turned to face up.
            _floor.rotation = Quaternion.Euler(90f, 0f, 0f);
            _floor.localScale = new Vector3(size.x, size.y, 1f);
        }

        Vector3[] Corners(float height)
        {
            var min = _settings.Min;
            var max = _settings.Max;
            return new[]
            {
                new Vector3(min.x, height, min.y),
                new Vector3(max.x, height, min.y),
                new Vector3(max.x, height, max.y),
                new Vector3(min.x, height, max.y)
            };
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (_settings == null) return;

            var corners = Corners(0f);
            UnityEditor.Handles.color = new Color(0.35f, 0.85f, 1f, 0.9f);
            for (var i = 0; i < corners.Length; i++)
                UnityEditor.Handles.DrawLine(corners[i], corners[(i + 1) % corners.Length]);
        }
#endif
    }
}
