using UnityEngine;

namespace SofaRobotAI.Backend
{
    /// <summary>
    /// Default backend: arms are ArticulationBody chains simulated by Unity PhysX.
    /// No external dependencies, works on macOS/Windows/Linux and in the editor.
    /// </summary>
    public class UnityPhysicsBackend : IRobotPhysicsBackend
    {
        public string Name => "Unity PhysX (ArticulationBody)";
        public string Status { get; private set; } = "not initialized";

        public bool Initialize(MultiArmAgentManager manager)
        {
            Time.fixedDeltaTime = 0.01f; // 100 Hz solver for stable joint drives
            Physics.defaultSolverIterations = 16;
            Status = $"running, fixed step {Time.fixedDeltaTime * 1000:F0} ms";
            return true;
        }
    }
}
