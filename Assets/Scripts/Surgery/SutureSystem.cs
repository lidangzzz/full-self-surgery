using System.Collections.Generic;
using SofaRobotAI.Agent;
using UnityEngine;

namespace SofaRobotAI.Surgery
{
    /// <summary>
    /// One suture: needle enters on one side of the vessel incision and exits on
    /// the other, the thread is pulled through and the two edges are cinched.
    /// </summary>
    public class StitchPoint
    {
        public Vector3 entry;      // near side of the cut (needle goes in here)
        public Vector3 exit;       // far side of the cut (needle comes out here)
        public bool completed;
        public GameObject knotVisual;

        public Vector3 Midpoint => (entry + exit) * 0.5f;
    }

    /// <summary>
    /// Suturing job: one batch of stitches closing a severed vessel. Multiple
    /// batches may share the same wound (sequential multi-arm suturing).
    /// </summary>
    public class SutureTask : RobotTask
    {
        public string organName;
        public Transform vessel;
        public GameObject incisionVisual;
        public Vector3 incisionBaseScale = Vector3.one;
        public readonly List<StitchPoint> stitches = new List<StitchPoint>();

        public int CompletedStitches
        {
            get { int n = 0; foreach (var s in stitches) if (s.completed) n++; return n; }
        }

        public override string description =>
            $"suture {organName}: wound at ({WorkPosition.x:F1},{WorkPosition.y:F1},{WorkPosition.z:F1}) " +
            $"- {CompletedStitches}/{stitches.Count} stitches";

        public override Vector3 WorkPosition => vessel != null ? vessel.position : Vector3.zero;
    }
}
