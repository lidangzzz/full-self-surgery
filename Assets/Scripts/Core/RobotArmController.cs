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
        //  Servo control (Jacobian-transpose gradient step toward a target)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Moves the TCP toward <paramref name="worldTarget"/> by one servo step.
        /// Call from FixedUpdate. Uses the Jacobian-transpose method: each revolute
        /// joint is nudged proportionally to how much its twist moves the TCP toward
        /// the target. Stable without an explicit IK solver and respects joint limits.
        /// </summary>
        public void ServoToward(Vector3 worldTarget, float gain = 0.35f)
        {
            Vector3 tcpPos = _tcp.position;
            Vector3 error = worldTarget - tcpPos;
            if (error.magnitude < 0.001f) return;
            if (error.magnitude > 1.5f) error = error.normalized * 1.5f; // stay in linear region

            for (int i = 0; i < _joints.Length; i++)
            {
                ArticulationBody joint = _joints[i];
                JointSpec js = _spec.joints[i];

                Transform parentT = joint.transform.parent;
                if (parentT == null) continue;

                // World axis & origin of joint i from the parent-side anchor frame.
                Vector3 axis = parentT.rotation * (joint.parentAnchorRotation * Vector3.right);
                Vector3 origin = parentT.TransformPoint(joint.parentAnchorPosition);
                Vector3 r = tcpPos - origin;
                Vector3 twistVel = Vector3.Cross(axis, r); // TCP velocity per unit joint speed
                float denom = twistVel.sqrMagnitude + 1e-4f; // damped least squares
                float delta = gain * Vector3.Dot(twistVel, error) / denom;

                // Clamp per-step step so the servo stays smooth and respects limits.
                float maxStep = js.maxServoStepDeg * Mathf.Deg2Rad;
                delta = Mathf.Clamp(delta, -maxStep, maxStep);

                float currentRad = joint.jointPosition[0];
                float targetRad = currentRad + delta;
                float lo = js.lowerLimitDeg * Mathf.Deg2Rad;
                float hi = js.upperLimitDeg * Mathf.Deg2Rad;
                targetRad = Mathf.Clamp(targetRad, lo, hi);

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
