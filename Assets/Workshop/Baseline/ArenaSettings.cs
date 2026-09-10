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

        public Vector3 Clamp(Vector3 position) => new Vector3(
            Mathf.Clamp(position.x, Min.x, Max.x),
            position.y,
            Mathf.Clamp(position.z, Min.y, Max.y));
    }
}
