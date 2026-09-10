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
        public Vector2 Min = new Vector2(-60f, -40f);
        public Vector2 Max = new Vector2(60f, 40f);

        /// <summary>
        /// Holds a position inside the field, keeping <paramref name="inset"/> clear of the edge so
        /// a body stops short of the wall instead of straddling it.
        /// </summary>
        public Vector3 Clamp(Vector3 position, float inset = 0f) => new Vector3(
            Mathf.Clamp(position.x, Min.x + inset, Max.x - inset),
            position.y,
            Mathf.Clamp(position.z, Min.y + inset, Max.y - inset));
    }
}
