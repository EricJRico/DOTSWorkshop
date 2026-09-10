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

        void Start()
        {
            if (_settings == null) return;

            var line = GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = true;
            line.positionCount = 4;
            line.SetPositions(Corners());
        }

        Vector3[] Corners()
        {
            var min = _settings.Min;
            var max = _settings.Max;
            return new[]
            {
                new Vector3(min.x, 0f, min.y),
                new Vector3(max.x, 0f, min.y),
                new Vector3(max.x, 0f, max.y),
                new Vector3(min.x, 0f, max.y)
            };
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (_settings == null) return;

            var corners = Corners();
            UnityEditor.Handles.color = new Color(0.35f, 0.85f, 1f, 0.9f);
            for (var i = 0; i < corners.Length; i++)
                UnityEditor.Handles.DrawLine(corners[i], corners[(i + 1) % corners.Length]);
        }
#endif
    }
}
