using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Two rectangles on the XZ plane. Min/Max pens the player inside the camera's view;
    /// WorldMin/WorldMax is the simulation extent, big enough to hold the enemy spawn ring,
    /// and sizes the neighbour grids. Enemies are not clamped to either.
    /// </summary>
    [CreateAssetMenu(menuName = "Workshop/Arena Settings")]
    public class ArenaSettings : ScriptableObject
    {
        [Header("Player bounds")]
        public Vector2 Min = new Vector2(-24f, -14f);
        public Vector2 Max = new Vector2(24f, 14f);

        [Header("Simulation extent (neighbour grid); must cover the enemy spawn ring)")]
        public Vector2 WorldMin = new Vector2(-46f, -46f);
        public Vector2 WorldMax = new Vector2(46f, 46f);
    }
}
