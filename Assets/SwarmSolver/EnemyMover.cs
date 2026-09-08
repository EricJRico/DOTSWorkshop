using UnityEngine;

namespace Workshop
{
    /// <summary>Runs the swarm solver once per frame against the player's position.</summary>
    public class EnemyMover : MonoBehaviour
    {
        public SwarmSettings Settings;
        public ArenaSettings Arena;
        public Transform Player;

        public SwarmSolver Solver { get; private set; }

        void Awake()
        {
            Solver = new SwarmSolver(Settings, Arena);
        }

        void Update()
        {
            Solver.Step(Player.position);
        }

        void OnDestroy()
        {
            Solver?.Dispose();
        }
    }
}
