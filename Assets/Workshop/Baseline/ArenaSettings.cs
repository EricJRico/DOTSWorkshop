using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// The playable field on the XZ plane. The camera shows a slice of it, so it is several
    /// screens across and the player can be cornered against an edge.
    ///
    /// The same two numbers are baked into ArenaBounds on the entities side later, so both halves
    /// of the day read one authored value.
    /// </summary>
    [CreateAssetMenu(menuName = "Workshop/Arena Settings")]
    public class ArenaSettings : ScriptableObject
    {
        [Tooltip("The near corner of the field, in world X and Z.")]
        [SerializeField] private Vector2 _min = new Vector2(-60f, -40f);

        [Tooltip("The far corner of the field, in world X and Z.")]
        [SerializeField] private Vector2 _max = new Vector2(60f, 40f);

        internal Vector2 Min => _min;
        internal Vector2 Max => _max;

        /// <summary>
        /// Holds a position inside the field, keeping <paramref name="inset"/> clear of the edge so
        /// a body stops short of the wall instead of straddling it.
        /// </summary>
        internal Vector3 Clamp(Vector3 position, float inset = 0f) => new Vector3(
            Mathf.Clamp(position.x, _min.x + inset, _max.x - inset),
            position.y,
            Mathf.Clamp(position.z, _min.y + inset, _max.y - inset));
    }
}
