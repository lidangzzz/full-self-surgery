using UnityEngine;

namespace SofaRobotAI.Core
{
    /// <summary>
    /// Runtime control API of a single arm: joint servoing (Jacobian-transpose),
    /// gripper open/close with physical grasp via fixed joints, and TCP access.
    /// The AI agent layer issues high-level commands; this class executes them
    /// against the ArticulationBody physics chain.
    /// </summary>
    public class RobotArmController : MonoBehaviour
    {
        private ArmSpec _spec;
        private ArticulationBody[] _joints;
        private ArticulationBody[] _fingers;
        private Transform _tcp;
        private FixedJoint _graspJoint;

        public ArmSpec Spec => _spec;
        public Transform Tcp => _tcp;
        public int Dof => _joints?.Length ?? 0;
        public string ArmName => _spec != null ? _spec.armName : name;
        public PayloadObject GraspedPayload { get; private set; }

        public void Configure(ArmSpec spec, ArticulationBody[] joints, ArticulationBody[] fingers, Transform tcp)
        {
            _spec = spec;
            _joints = joints;
            _fingers = fingers;
            _tcp = tcp;
        }

        // ------------------------------------------------------------------ //
        //  State
        // ------------------------------------------------------------------ //

        /// <summary>Current joint positions in radians.</summary>
        public float[] JointPositions
        {
            get
            {
                var q = new float[_joints.Length];
                for (int i = 0; i < _joints.Length; i++)
                    q[i] = _joints[i].jointPosition[0];
                return q;
            }
        }

        /// <summary>Average TCP linear speed (m/s), used by skills to detect "settled".</summary>
        public float TcpSpeed { get; private set; }

        private Vector3 _lastTcpPos;
        private bool _hasLastPos;

        // ------------------------------------------------------------------ //
        //  Servo control: damped-least-squares IK on a straight-line trajectory
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Tracks a Cartesian straight-line trajectory from the current TCP to
        /// <paramref name="worldTarget"/> with damped-least-squares inverse
        /// kinematics (replaces the old Jacobian-transpose gradient step, which
        /// stagnated in twisted poses, left a 10-25 mm steady-state residual
        /// and had a dead zone for small motions).
        ///
        /// Per physics step: advance a waypoint along the straight line at a
        /// bounded tip speed, then solve dq = Jᵀ(JJᵀ + λ²I)⁻¹·e for the joint
        /// delta that realizes the waypoint motion. Damping λ keeps the step
        /// bounded through singularities instead of stalling.
        /// </summary>
        public void ServoToward(Vector3 worldTarget, float gain = 0.35f)
        {
            Vector3 tcpPos = _tcp.position;
            Vector3 error = worldTarget - tcpPos;
            if (error.magnitude < 0.0002f) return;

            // Bounded tip speed along the straight line (gain maps to speed;
            // the executor uses 0.12-0.55 -> 0.052-0.095 m/s).
            float tipSpeed = 0.04f + 0.10f * Mathf.Clamp(gain, 0f, 1f);
            float maxTipStep = tipSpeed * Time.fixedDeltaTime;
            Vector3 step = Vector3.ClampMagnitude(error, maxTipStep);

            int n = _joints.Length;

            // Per-joint TCP twists (the Jacobian columns) at the current pose.
            var twists = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                ArticulationBody joint = _joints[i];
                Transform parentT = joint.transform.parent;
                if (parentT == null) continue;
                Vector3 axis = parentT.rotation * (joint.parentAnchorRotation * Vector3.right);
                Vector3 origin = parentT.TransformPoint(joint.parentAnchorPosition);
                twists[i] = Vector3.Cross(axis, tcpPos - origin);
            }

            // Damped least squares:  dq = Jt (J Jt + l^2 I)^-1 * step
            // A = J Jt + l^2 I is 3x3; solve A*y = step via Cramer's rule.
            const float lambda = 0.012f;
            float l2 = lambda * lambda;
            float a00 = l2, a01 = 0f, a02 = 0f, a11 = l2, a12 = 0f, a22 = l2;
            for (int i = 0; i < n; i++)
            {
                Vector3 t = twists[i];
                a00 += t.x * t.x; a01 += t.x * t.y; a02 += t.x * t.z;
                a11 += t.y * t.y; a12 += t.y * t.z;
                a22 += t.z * t.z;
            }
            float det = a00 * (a11 * a22 - a12 * a12)
                       - a01 * (a01 * a22 - a12 * a02)
                       + a02 * (a01 * a12 - a11 * a02);
            if (Mathf.Abs(det) < 1e-12f) return;
            float invDet = 1f / det;
            float yx = ((a11 * a22 - a12 * a12) * step.x + (a02 * a12 - a01 * a22) * step.y + (a01 * a12 - a02 * a11) * step.z) * invDet;
            float yy = ((a02 * a12 - a01 * a22) * step.x + (a00 * a22 - a02 * a02) * step.y + (a01 * a02 - a00 * a12) * step.z) * invDet;
            float yz = ((a01 * a12 - a02 * a11) * step.x + (a01 * a02 - a00 * a12) * step.y + (a00 * a11 - a01 * a01) * step.z) * invDet;

            for (int i = 0; i < n; i++)
            {
                ArticulationBody joint = _joints[i];
                JointSpec js = _spec.joints[i];
                Transform parentT = joint.transform.parent;
                if (parentT == null) continue;

                float delta = twists[i].x * yx + twists[i].y * yy + twists[i].z * yz;

                // Per-step joint clamp keeps the motion smooth and bounded
                // even when the DLS solution is aggressive near a limit.
                float maxStep = js.maxServoStepDeg * Mathf.Deg2Rad;
                delta = Mathf.Clamp(delta, -maxStep, maxStep);

                float targetRad = joint.jointPosition[0] + delta;
                targetRad = Mathf.Clamp(targetRad,
                                        js.lowerLimitDeg * Mathf.Deg2Rad,
                                        js.upperLimitDeg * Mathf.Deg2Rad);

                var drive = joint.xDrive;
                drive.target = targetRad * Mathf.Rad2Deg;
                joint.xDrive = drive;
            }
        }

        /// <summary>Drives all joints back to their zero (built) configuration.</summary>
        public void GoHome()
        {
            for (int i = 0; i < _joints.Length; i++)
            {
                var drive = _joints[i].xDrive;
                drive.target = 0f;
                _joints[i].xDrive = drive;
            }
        }

        // ------------------------------------------------------------------ //
        //  Gripper
        // ------------------------------------------------------------------ //

        /// <summary>Drives the fingers. 1 = fully open, 0 = fully closed.</summary>
        public void SetGripper(float open01)
        {
            if (_fingers == null || _fingers.Length == 0) return; // tool arm: no fingers
            open01 = Mathf.Clamp01(open01);
            float travel = _spec.fingerOpen - _spec.fingerClosed;
            // Joint position 0 == built (open) pose; closing moves L to -travel and R to +travel.
            float closePos = (1f - open01) * travel;
            SetFingerTarget(_fingers[0], -closePos);
            SetFingerTarget(_fingers[1], closePos);
        }

        private static void SetFingerTarget(ArticulationBody finger, float pos)
        {
            var drive = finger.xDrive;
            drive.target = pos;
            finger.xDrive = drive;
        }

        /// <summary>
        /// Attempts to grasp a payload near the TCP. Returns true on success.
        /// The grasp is physical: a FixedJoint connects the payload to the wrist.
        /// </summary>
        public bool TryGrasp()
        {
            if (_fingers == null || _fingers.Length == 0) return false; // tool arms don't grasp
            if (GraspedPayload != null) return true;
            var hits = Physics.OverlapSphere(_tcp.position, _spec.graspRadius, ~0, QueryTriggerInteraction.Ignore);
            PayloadObject best = null;
            float bestDist = float.MaxValue;
            foreach (var hit in hits)
            {
                var payload = hit.GetComponentInParent<PayloadObject>();
                if (payload == null || payload.IsGrasped) continue;
                float d = Vector3.SqrMagnitude(payload.transform.position - _tcp.position);
                if (d < bestDist) { bestDist = d; best = payload; }
            }
            if (best == null) return false;

            var rb = best.Rigidbody;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            _graspJoint = gameObject.AddComponent<FixedJoint>();
            _graspJoint.connectedBody = rb;
            _graspJoint.breakForce = 500f;
            GraspedPayload = best;
            best.NotifyGrasped(this);
            return true;
        }

        /// <summary>Releases the grasped payload, if any.</summary>
        public void Release()
        {
            if (GraspedPayload == null) return;
            GraspedPayload.NotifyReleased();
            GraspedPayload = null;
            if (_graspJoint != null)
            {
                Destroy(_graspJoint);
                _graspJoint = null;
            }
        }

        private void FixedUpdate()
        {
            Vector3 pos = _tcp.position;
            if (_hasLastPos)
                TcpSpeed = (pos - _lastTcpPos).magnitude / Time.fixedDeltaTime;
            _lastTcpPos = pos;
            _hasLastPos = true;

            if (_graspJoint == null && GraspedPayload != null) // joint broke by physics
                Release();
        }
    }
}
