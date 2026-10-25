using UnityEngine;

namespace SofaRobotAI.Backend
{
    /// <summary>Which physics engine drives the cell.</summary>
    public enum PhysicsBackendMode
    {
        /// <summary>Unity PhysX ArticulationBody. Runs on every platform, zero setup.</summary>
        UnityPhysX,

        /// <summary>
        /// SOFA co-simulation via the SofaUnity plugin (github.com/InfinyTech3D/SofaUnity).
        /// Requires the SofaVerseAPI native library - Windows x64 only - plus the
        /// SOFA scene file. Falls back to UnityPhysX automatically when unavailable.
        /// </summary>
        SofaUnity,
    }

    /// <summary>
    /// Physics backend of the robot cell. The agent layer talks only to this
    /// interface, so the same AI code runs on Unity PhysX or on SOFA.
    /// </summary>
    public interface IRobotPhysicsBackend
    {
        string Name { get; }
        string Status { get; }
        /// <returns>false when the backend is unavailable on this machine.</returns>
        bool Initialize(MultiArmAgentManager manager);
        void Step() { }
        void Shutdown() { }
    }
}
