using System;
using SofaUnityAPI;
using UnityEngine;

namespace SofaRobotAI.Surgery
{
    /// <summary>
    /// Native bridge for the needle-tissue force coupling. Uses the generic
    /// mesh-data P/Invoke entry points directly (public in the SofaUnity API)
    /// because the DAGNode object-discovery API is unavailable on this 25.12
    /// native build:
    ///  - pushes the kinematic needle-tip sphere position into SOFA
    ///    (world -> sofa frame: x negated, same convention as
    ///    SofaCustomMeshAPI.UpdateMesh),
    ///  - reads the tissue FEM vertex buffer back for the Unity-side
    ///    deformation visual.
    /// The sphere vs tissue surface contacts are solved inside SOFA
    /// (FreeMotionAnimationLoop + LCPConstraintSolver in robot_cell.scn),
    /// so the tissue is genuinely indented by the passing needle.
    /// </summary>
    public class NeedleTissueCoupling
    {
        public const string NeedleDofName = "NeedleTipDOFs";
        public const string CardiacDofName = "CardiacPusherDOFs";
        public const string TissueDofName = "TissueDOFs";

        private readonly IntPtr _simu;
        private readonly Transform _sofaRoot; // SofaContext transform (Unity<->SOFA frame)

        public bool LastPushOk { get; private set; }
        public bool LastPullOk { get; private set; }

        public NeedleTissueCoupling(IntPtr simu, Transform sofaRoot)
        {
            _simu = simu;
            _sofaRoot = sofaRoot;

            // Name-resolution probes: the mesh APIs address scene objects by
            // name; log what the native side sees so mismatches are obvious.
            int tissueVerts = SofaBaseMeshAPI.sofaMeshAPI_getNbVertices(simu, TissueDofName);
            int needleVerts = SofaBaseMeshAPI.sofaMeshAPI_getNbVertices(simu, NeedleDofName);
            Debug.Log($"[NeedleTissueCoupling] probes: {TissueDofName}={tissueVerts} verts, " +
                      $"{NeedleDofName}={needleVerts} verts");

            PushNeedleTip(Vector3.zero, active: false);
        }

        /// <summary>Drives the SOFA needle-tip collision sphere (or parks it
        /// far below when no arm is suturing).</summary>
        public void PushNeedleTip(Vector3 worldPos, bool active) =>
            PushSphere(NeedleDofName, worldPos, active);

        /// <summary>Drives any kinematic coupling sphere registered in the
        /// SOFA scene (needle tip, cardiac pusher, ...).</summary>
        public void PushSphere(string dofName, Vector3 worldPos, bool active)
        {
            float x = 0f, y = -2f, z = 0f; // parked far below the scene
            if (active)
            {
                Vector3 sofa = _sofaRoot.InverseTransformPoint(worldPos);
                x = -sofa.x; // Unity (left-handed) -> SOFA (right-handed)
                y = sofa.y;
                z = sofa.z;
            }

            var arr = new float[3] { x, y, z };
            int res = SofaCustomMeshAPI.sofaMeshAPI_setVertices(_simu, dofName, arr);
            LastPushOk = res >= 0;
            if (!LastPushOk)
                Debug.LogWarning($"[NeedleTissueCoupling] setVertices({dofName}) returned {res}");
        }

        /// <summary>Reads the tissue FEM positions into <paramref name="dst"/>
        /// (SOFA coordinates, x needs negating to get Unity world).</summary>
        public bool PullTissue(float[] dst)
        {
            int res = SofaBaseMeshAPI.sofaMeshAPI_getVertices(_simu, TissueDofName, dst);
            LastPullOk = res >= 0;
            if (!LastPullOk)
                Debug.LogWarning($"[NeedleTissueCoupling] getVertices({TissueDofName}) returned {res}");
            return LastPullOk;
        }
    }
}
