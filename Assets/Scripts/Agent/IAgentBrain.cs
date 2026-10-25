using UnityEngine;

namespace SofaRobotAI.Agent
{
    /// <summary>High-level skills an agent brain can command.</summary>
    public enum AgentSkill
    {
        Idle,    // wait / no-op
        MoveTo,  // servo TCP toward decision.target
        Grasp,   // close gripper and try to grab
        Release, // open gripper and let go
        Home     // return to zero configuration
    }

    /// <summary>Everything a brain observes about its arm at a decision point.</summary>
    public class AgentObservation
    {
        public string armName;
        public int dof;
        public Vector3 tcpPosition;
        public float tcpSpeed;
        public bool holdingPayload;
        public string heldObjectName;

        public string taskDescription;
        public Vector3 pickPosition;     // where the payload to fetch is
        public Vector3 dropPosition;     // where to deliver it
        public float distanceToPick;
        public float distanceToDrop;
        public bool arrivedAtTarget;     // servo reached its current target

        public int payloadsRemaining;    // workload left in the cell (all arms)
        public float simulationTime;
    }

    /// <summary>A brain's decision at a decision point.</summary>
    public class AgentDecision
    {
        public AgentSkill skill = AgentSkill.Idle;
        public Vector3 target;
        public string reason = "";

        public static AgentDecision Move(Vector3 pos, string why = null) =>
            new AgentDecision { skill = AgentSkill.MoveTo, target = pos, reason = why ?? "" };

        public static AgentDecision Do(AgentSkill skill, string why = null) =>
            new AgentDecision { skill = skill, reason = why ?? "" };
    }

    /// <summary>
    /// Pluggable decision core of an arm agent. Implementations range from a
    /// deterministic heuristic policy to an LLM-backed planner; the arm agent
    /// runs the resulting skills on the physics chain.
    /// </summary>
    public interface IAgentBrain
    {
        string Name { get; }
        void Initialize(Core.RobotArmController arm);
        /// <summary>Returns the next decision, or null to keep waiting (async brains).</summary>
        AgentDecision Decide(AgentObservation observation);
    }
}
