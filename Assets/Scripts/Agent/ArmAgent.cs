using SofaRobotAI.Core;
using SofaRobotAI.Surgery;
using SofaRobotAI.Surgery.Scenarios;
using UnityEngine;

namespace SofaRobotAI.Agent
{
    /// <summary>
    /// Embodied agent for one arm. Owns the decision cadence (asks its brain),
    /// executes skills on the <see cref="RobotArmController"/> and reports
    /// progress to the <see cref="MultiArmAgentManager"/>. Suturing tasks are
    /// delegated to a <see cref="SutureExecutor"/> driving the same servo.
    /// An external AI agent can override autonomy through the Api* methods.
    /// </summary>
    public class ArmAgent : MonoBehaviour
    {
        public RobotArmController Arm { get; private set; }
        public IAgentBrain Brain { get; private set; }
        public RobotTask Task { get; private set; }
        public NeedleTool Needle { get; private set; }
        public ArmRole Role { get; private set; } = ArmRole.Idle;
        public bool ApiControl { get; private set; }
        public string Status { get; private set; } = "init";

        private MultiArmAgentManager _manager;
        private SutureExecutor _sutureExecutor;
        private AgentSkill _skill = AgentSkill.Idle;
        private Vector3 _skillTarget;
        private float _nextDecisionTime;
        private float _gripperOpen01 = 1f;
        private float _gripperTarget01 = 1f;
        private float _gripperDoneTime;
        private bool _gripperBusy;

        private enum MovePhase { None, Hover, Descend }
        private MovePhase _movePhase;

        private const float HoverHeight = 0.35f;
        private const float ArriveRadius = 0.07f;
        private const float ArriveSpeed = 0.10f;
        private const float DecisionPeriod = 0.5f;

        public void Configure(RobotArmController arm, IAgentBrain brain, MultiArmAgentManager manager)
        {
            Arm = arm;
            Brain = brain;
            _manager = manager;
            brain.Initialize(arm);
            Status = "ready";
        }

        public void AssignTask(ManipulationTask task)
        {
            Task = task;
            if (task != null) task.assigned = true;
            _nextDecisionTime = 0f; // decide immediately
            Status = task != null ? "task assigned" : "idle";
        }

        /// <summary>Mounts a needle on this arm without a task (arm holds it parked).</summary>
        public void AttachNeedle(NeedleTool needle) => Needle = needle;

        /// <summary>Removes the needle (role change to non-suturer).</summary>
        public void ClearNeedle()
        {
            if (Needle != null) Destroy(Needle.gameObject);
            Needle = null;
        }

        public void SetRole(ArmRole role) => Role = role;

        // ------------------------- external-agent API ------------------------- //

        private Vector3 _apiTarget;
        private float _apiGripper = 1f;
        private Vector3 _stabilizePoint;

        public void SetStabilizePoint(Vector3 worldPoint) => _stabilizePoint = worldPoint;

        /// <summary>Direct TCP move (world space) - suspends autonomy until Resume/timeout.</summary>
        public void ApiMoveTcp(Vector3 worldTarget)
        {
            ApiControl = true;
            _holdHome = false;
            _apiTarget = worldTarget;
        }

        /// <summary>Direct needle-tip move - reuses the tip offset servo.</summary>
        public void ApiMoveTip(Vector3 worldTarget)
        {
            if (Needle == null) { ApiMoveTcp(worldTarget); return; }
            ApiControl = true;
            _holdHome = false;
            _apiTarget = worldTarget;
            _tipMode = true;
        }

        private bool _tipMode;
        private bool _holdHome;

        public void ApiGripper(float open01)
        {
            _apiGripper = open01;
            if (ApiControl) Arm.SetGripper(open01);
        }

        public void ApiHome()
        {
            ApiControl = true; // Home is a takeover command: hold zero pose
                              // until Resume/next API move, even from auto
            Arm.GoHome();
            Arm.SetGripper(1f);
            _apiGripper = 1f;
            _tipMode = false;
            _holdHome = true; // keep driving zero pose until the next move
        }

        /// <summary>Hands control back to the scenario executor.</summary>
        public void ApiResume()
        {
            ApiControl = false;
            _tipMode = false;
            _holdHome = false;
        }

        /// <summary>API: tie off (cinch) the stitch the external agent just
        /// drove the needle through. Validates the needle-tip position, so an
        /// agent cannot mark stitches it never actually sewed.</summary>
        public bool ApiCinch(float maxTipDistance = 0.05f) =>
            _sutureExecutor != null &&
            _sutureExecutor.CompleteCurrentStitchExternally(maxTipDistance);

        /// <summary>Hands this arm its suture batch; the
        /// <see cref="SutureExecutor"/> takes over from the brain.</summary>
        public void EquipNeedle(NeedleTool needle, SutureTask task,
                                SutureExecutor.Config config = null)
        {
            Needle = needle;
            _sutureExecutor = new SutureExecutor(Arm, needle) { Settings = config ?? new SutureExecutor.Config() };
            Task = task;
            if (task != null) task.assigned = true;
            _sutureExecutor.Begin(task);
            _skill = AgentSkill.Idle;
        }

        private bool Suturing => _sutureExecutor != null && Task is SutureTask;

        /// <summary>Needle tip currently piercing / passing through tissue.</summary>
        /// <summary>Needle-tissue coupling gate: the autonomy executor's
        /// pierce/through phases, OR an API-driven needle that has physically
        /// descended below the tissue surface (manual API suturing must bend
        /// the SOFA tissue exactly like the autonomous one).</summary>
        public bool NeedleInTissue =>
            Suturing && _sutureExecutor.TipInTissue ||
            ApiControl && Needle != null && Needle.Tip.position.y < 0.548f;

        private void Update()
        {
            if (Arm == null || Brain == null || _manager == null) return;

            // Animate the gripper toward its commanded opening.
            _gripperOpen01 = Mathf.MoveTowards(_gripperOpen01, _gripperTarget01, Time.deltaTime * 2.5f);
            Arm.SetGripper(_gripperOpen01);

            if (_gripperBusy && Time.time > _gripperDoneTime) _gripperBusy = false;

            if (Needle != null) Needle.UpdateThread(); // thread always follows the tool

            if (_manager.PublicPaused) { Status = "paused"; return; }

            if (ApiControl)
            {
                Status = _holdHome
                    ? "api home -> zero pose"
                    : $"api move -> ({_apiTarget.x:F2},{_apiTarget.y:F2},{_apiTarget.z:F2})";
                return;
            }

            if (Suturing)
            {
                if (Task.done) Task = null; // executor finished the batch
                Status = "suture: " + _sutureExecutor.Status;
                return;
            }

            if (Role == ArmRole.Stabilizer) { Status = "stabilizing tissue"; return; }

            if (Time.time >= _nextDecisionTime)
            {
                _nextDecisionTime = Time.time + DecisionPeriod;
                AgentDecision decision = Brain.Decide(BuildObservation());
                if (decision != null) ApplyDecision(decision);
            }

            Status = DescribeState();
        }

        // ------------------------------------------------------------------ //
        //  Skill execution
        // ------------------------------------------------------------------ //

        private void ApplyDecision(AgentDecision d)
        {
            switch (d.skill)
            {
                case AgentSkill.MoveTo:
                    _skill = AgentSkill.MoveTo;
                    _skillTarget = d.target;
                    _movePhase = MovePhase.Hover;
                    break;
                case AgentSkill.Grasp:
                    _skill = AgentSkill.Grasp;
                    _gripperTarget01 = 0f; // close
                    _gripperBusy = true;
                    _gripperDoneTime = Time.time + 0.5f;
                    break;
                case AgentSkill.Release:
                    _skill = AgentSkill.Release;
                    _gripperTarget01 = 1f; // open
                    _gripperBusy = true;
                    _gripperDoneTime = Time.time + 0.4f;
                    break;
                case AgentSkill.Home:
                    _skill = AgentSkill.Home;
                    Arm.GoHome();
                    break;
                default:
                    _skill = AgentSkill.Idle;
                    break;
            }
        }

        private void FixedUpdate()
        {
            if (Arm == null || (_manager != null && _manager.PublicPaused)) return;

            if (ApiControl)
            {
                if (_holdHome)
                {
                    Arm.GoHome(); // hold the zero pose until the next API move
                    return;
                }
                if (_tipMode && Needle != null)
                {
                    float err = Vector3.Distance(Needle.Tip.position, _apiTarget);
                    if (err > 0.003f)
                    {
                        Vector3 offset = Needle.Tip.position - Arm.Tcp.position;
                        Arm.ServoToward(_apiTarget - offset, Mathf.Clamp(err * 6f, 0.12f, 0.55f));
                    }
                }
                else
                {
                    Arm.ServoToward(_apiTarget);
                }
                return;
            }

            if (Role == ArmRole.Stabilizer && !Suturing)
            {
                Arm.ServoToward(_stabilizePoint);
                return;
            }

            if (Suturing)
            {
                _sutureExecutor.Step();
                return;
            }

            switch (_skill)
            {
                case AgentSkill.MoveTo:
                    ServoWithPhases();
                    break;

                case AgentSkill.Grasp:
                    if (!_gripperBusy && Arm.GraspedPayload == null) Arm.TryGrasp();
                    break;

                case AgentSkill.Release:
                    if (!_gripperBusy && Arm.GraspedPayload != null)
                    {
                        Arm.Release();
                        if (Task is ManipulationTask mt) _manager.NotifyReleasedNearDrop(this, mt);
                    }
                    break;
            }
        }

        /// <summary>
        /// Hover-then-descend navigation: first fly above the target, then sink
        /// onto it. Keeps payloads from being swept sideways across the floor.
        /// </summary>
        private void ServoWithPhases()
        {
            Vector3 tcp = Arm.Tcp.position;
            float horizDist = Vector2.Distance(new Vector2(tcp.x, tcp.z),
                                               new Vector2(_skillTarget.x, _skillTarget.z));

            if (_movePhase == MovePhase.Hover)
            {
                Arm.ServoToward(new Vector3(_skillTarget.x, Mathf.Max(_skillTarget.y + HoverHeight, HoverHeight + 0.15f), _skillTarget.z));
                if (horizDist < 0.12f) _movePhase = MovePhase.Descend;
            }
            else
            {
                Arm.ServoToward(_skillTarget);
            }
        }

        // ------------------------------------------------------------------ //
        //  Observation
        // ------------------------------------------------------------------ //

        private bool Arrived(Vector3 target) =>
            Vector3.Distance(Arm.Tcp.position, target) < ArriveRadius && Arm.TcpSpeed < ArriveSpeed;

        private AgentObservation BuildObservation()
        {
            Vector3 pick = Arm.Tcp.position;
            Vector3 drop = Arm.Tcp.position;
            if (Task is ManipulationTask mt)
            {
                pick = mt.PickPosition;
                drop = mt.DropPosition;
            }

            var obs = new AgentObservation
            {
                armName = Arm.ArmName,
                dof = Arm.Dof,
                tcpPosition = Arm.Tcp.position,
                tcpSpeed = Arm.TcpSpeed,
                holdingPayload = Arm.GraspedPayload != null,
                heldObjectName = Arm.GraspedPayload?.name,
                payloadsRemaining = _manager.RemainingTaskCount,
                simulationTime = Time.time,
                taskDescription = Task != null ? Task.description : "none",
                pickPosition = pick,
                dropPosition = drop,
                arrivedAtTarget = _skill == AgentSkill.MoveTo && Arrived(_skillTarget),
            };
            obs.distanceToPick = Vector3.Distance(obs.tcpPosition, obs.pickPosition);
            obs.distanceToDrop = Vector3.Distance(obs.tcpPosition, obs.dropPosition);
            return obs;
        }

        private string DescribeState()
        {
            if (Task == null) return "waiting for task";
            string held = Arm.GraspedPayload != null ? "carrying " + Arm.GraspedPayload.name : "empty hand";
            return $"{_skill} ({held})";
        }

        public void NotifyTaskCompleted()
        {
            if (Task != null) Task.done = true;
            Task = null;
            _skill = AgentSkill.Idle;
            _gripperTarget01 = 1f;
        }
    }
}
