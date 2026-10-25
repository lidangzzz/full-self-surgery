using System.Collections.Generic;
using SofaRobotAI.Core;
using UnityEngine;

namespace SofaRobotAI.Agent
{
    /// <summary>Base of every task an arm agent can execute.</summary>
    public abstract class RobotTask
    {
        public bool assigned;
        public bool done;

        public abstract string description { get; }

        /// <summary>Representative position used for allocation and reach filtering.</summary>
        public abstract Vector3 WorkPosition { get; }
    }

    /// <summary>One pick-and-place job: fetch <see cref="payload"/>, drop it at <see cref="DropPosition"/>.</summary>
    public class ManipulationTask : RobotTask
    {
        public PayloadObject payload;
        public Vector3 DropPosition;

        public Vector3 PickPosition => payload != null ? payload.transform.position : Vector3.zero;

        public override string description =>
            payload != null
                ? $"pick {payload.name} at ({PickPosition.x:F1},{PickPosition.y:F1},{PickPosition.z:F1}), drop at ({DropPosition.x:F1},{DropPosition.y:F1},{DropPosition.z:F1})"
                : "none";

        public override Vector3 WorkPosition => PickPosition;
    }

    /// <summary>
    /// Greedy allocator with a reachability filter: each idle arm takes the
    /// nearest unassigned task whose pick AND drop positions lie inside its
    /// workspace. Good enough for the init scene and easily replaceable by a
    /// smarter policy.
    /// </summary>
    public static class TaskAllocator
    {
        public static void AssignPending(List<ArmAgent> agents, List<RobotTask> tasks)
        {
            foreach (ArmAgent agent in agents)
            {
                if (agent.Task != null) continue;

                ManipulationTask best = null;
                float bestDist = float.MaxValue;
                foreach (RobotTask task in tasks)
                {
                    // Only pick-and-place jobs flow through the allocator;
                    // suture tasks are pre-assigned to their arm at build time.
                    if (!(task is ManipulationTask mt)) continue;
                    if (mt.assigned || mt.done || mt.payload == null || mt.payload.IsGrasped) continue;
                    if (mt.payload.Delivered) continue;
                    if (!Reachable(agent.Arm, mt)) continue;

                    float d = Vector3.Distance(agent.Arm.Tcp.position, mt.PickPosition);
                    if (d < bestDist) { bestDist = d; best = mt; }
                }
                if (best != null) agent.AssignTask(best);
            }
        }

        /// <summary>Both the payload and the target bin must be within the arm's workspace.</summary>
        private static bool Reachable(RobotArmController arm, ManipulationTask task)
        {
            const float margin = 0.92f; // stay clear of the stretched-out singularity
            float reach = arm.Spec.Reach * margin;
            Vector3 basePos = arm.transform.position;

            return HorizontalDistance(basePos, task.PickPosition) < reach
                && HorizontalDistance(basePos, task.DropPosition) < reach;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b) =>
            Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
    }
}
