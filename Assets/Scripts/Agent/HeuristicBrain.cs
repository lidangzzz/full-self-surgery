using UnityEngine;

namespace SofaRobotAI.Agent
{
    /// <summary>
    /// Deterministic pick-and-place policy: approach above the payload, descend,
    /// grasp, lift, carry above the drop zone, descend, release. Runs instantly
    /// with no external calls and doubles as the fallback for the LLM brain.
    /// </summary>
    public class HeuristicBrain : IAgentBrain
    {
        public string Name => "Heuristic";

        public void Initialize(Core.RobotArmController arm) { }

        public AgentDecision Decide(AgentObservation obs)
        {
            if (obs.holdingPayload)
            {
                // Carry to drop zone; release once the TCP has settled over it.
                if (obs.arrivedAtTarget && obs.distanceToDrop < 0.35f)
                    return AgentDecision.Do(AgentSkill.Release, "over drop zone");
                return AgentDecision.Move(obs.dropPosition, "carry to drop zone");
            }

            if (obs.distanceToPick < 0.6f && obs.arrivedAtTarget)
                return AgentDecision.Do(AgentSkill.Grasp, "at payload");

            return AgentDecision.Move(obs.pickPosition, "approach payload");
        }
    }
}
