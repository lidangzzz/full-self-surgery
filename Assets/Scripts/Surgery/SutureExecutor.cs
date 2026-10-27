using SofaRobotAI.Agent;
using SofaRobotAI.Core;
using UnityEngine;

namespace SofaRobotAI.Surgery
{
    /// <summary>
    /// Drives one arm through the suturing sequence of a <see cref="SutureTask"/>:
    /// for every stitch - approach above the entry, pierce down into the entry,
    /// pass under the incision, come out at the exit, pull the thread up, then
    /// cinch and lock the stitch (edges drawn together, knot visual).
    /// The needle follows the TCP rigidly, so every phase servos the TCP to
    /// (desired tip position - current tip offset).
    /// </summary>
    public class SutureExecutor
    {
        private readonly RobotArmController _arm;
        private readonly NeedleTool _needle;
        private SutureTask _task;
        private int _stitchIndex;
        private Phase _phase = Phase.Idle;
        private float _phaseHoldUntil;
        private Vector3 _phaseTarget;
        private string _status = "idle";

        private enum Phase { Idle, Approach, Pierce, Through, Pull, Cinch, Finished }

        /// <summary>Per-scenario tolerances (a beating surface needs coarser
        /// arrival radii and longer dwell than a static one).</summary>
        public class Config
        {
            public float coarseRadius = 0.035f; // free-space phases (approach/pull)
            public float fineRadius = 0.022f;   // piercing phases (5-DOF residual ~2 cm)
            public float dwellSeconds = 1.2f;   // inside-radius grace before "arrived"
            public float arriveSpeed = 0.06f;
        }

        public Config Settings { get; set; } = new Config();

        public string Status => _status;
        public SutureTask Task => _task;

        /// <summary>True while the needle tip is at/under the tissue surface
        /// (piercing or passing under the incision) - the phases where the
        /// SOFA contact coupling should be active.</summary>
        public bool TipInTissue => _phase == Phase.Pierce || _phase == Phase.Through;

        public SutureExecutor(RobotArmController arm, NeedleTool needle)
        {
            _arm = arm;
            _needle = needle;
        }

        public void Begin(SutureTask task)
        {
            _task = task;
            _stitchIndex = 0;
            _phase = task.stitches.Count > 0 ? Phase.Approach : Phase.Finished;
            _arm.SetGripper(0.35f); // fingers half-closed around the needle
            EnterPhase(Phase.Approach);
        }

        public void Abandon()
        {
            _task = null;
            _phase = Phase.Idle;
        }

        /// <summary>Cinch the current stitch on behalf of an EXTERNAL API
        /// agent that drove the needle through entry/exit itself (the FSM is
        /// suspended while the arm is under API control). The needle tip must
        /// actually be at the stitch before the request is honoured - the
        /// external agent cannot cinch a bite it never took.</summary>
        public bool CompleteCurrentStitchExternally(float maxTipDistance = 0.05f)
        {
            if (_task == null || _phase == Phase.Finished) return false;
            if (_stitchIndex >= _task.stitches.Count) return false;
            StitchPoint s = _task.stitches[_stitchIndex];
            if (Vector3.Distance(_needle.Tip.position, s.exit) > maxTipDistance) return false;
            // The external agent owns the arm: do NOT GoHome on finish (the
            // joint-target reset fights the API servo and flings the needle
            // ~130 mm off the wound before the agent's next command).
            _suppressFinishHome = true;
            CompleteStitch(s); // knot + band + thread anchors + advance/finish
            _suppressFinishHome = false;
            return true;
        }

        /// <summary>Called from the agent's FixedUpdate; advances the FSM.</summary>
        public void Step()
        {
            if (_task == null || _phase == Phase.Finished) return;

            if (_stitchIndex >= _task.stitches.Count)
            {
                FinishTask();
                return;
            }

            StitchPoint stitch = _task.stitches[_stitchIndex];
            ServoTip(_phaseTarget);

            switch (_phase)
            {
                case Phase.Approach:
                    if (Arrived()) EnterPhase(Phase.Pierce);
                    break;

                case Phase.Pierce:
                    if (Arrived()) EnterPhase(Phase.Through);
                    break;

                case Phase.Through:
                    if (Arrived()) EnterPhase(Phase.Pull);
                    break;

                case Phase.Pull:
                    if (Arrived()) EnterPhase(Phase.Cinch);
                    break;

                case Phase.Cinch:
                    if (Time.time >= _phaseHoldUntil)
                    {
                        CompleteStitch(stitch);
                        return; // CompleteStitch may finish the task (nulls it)
                    }
                    break;
            }

            _status = Describe(stitch);
        }

        // ------------------------------------------------------------------ //

        private void EnterPhase(Phase p)
        {
            _phase = p;
            _phaseEnteredAt = Time.time;
            if (_task == null || _stitchIndex >= _task.stitches.Count) return;
            StitchPoint s = _task.stitches[_stitchIndex];

            switch (p)
            {
                case Phase.Approach:
                    _phaseTarget = s.entry + new Vector3(0f, 0.10f, 0f);
                    break;
                case Phase.Pierce:
                    _phaseTarget = s.entry + new Vector3(0f, 0.004f, 0.002f); // touch the near edge
                    break;
                case Phase.Through:
                    // Dive under the cut line and resurface at the far edge.
                    _phaseTarget = s.exit + new Vector3(0f, -0.006f, 0f);
                    break;
                case Phase.Pull:
                    _phaseTarget = s.exit + new Vector3(0.02f, 0.10f, 0f);
                    break;
                case Phase.Cinch:
                    _phaseHoldUntil = Time.time + 0.45f; // pull the thread taut, tie off
                    break;
            }
        }

        private void CompleteStitch(StitchPoint stitch)
        {
            stitch.completed = true;
            SutureVisuals.MarkStitch(stitch, _task);
            _needle.SetAnchors(SutureVisuals.AnchorPath(_task));

            _stitchIndex++;
            if (_stitchIndex >= _task.stitches.Count)
            {
                FinishTask();
                return;
            }
            EnterPhase(Phase.Approach);
        }

        private void FinishTask()
        {
            _phase = Phase.Finished;
            if (_task != null) _task.done = true;
            _status = "suture complete";
            if (!_suppressFinishHome)
            {
                _arm.GoHome();
                _arm.SetGripper(1f);
            }
            _task = null;
        }

        private bool _suppressFinishHome;

        /// <summary>Servos the arm so the needle TIP reaches the given world point.
        /// Convergence gates and gain are on the tip (the real objective): full
        /// gain far away, gentle near the target to avoid a limit cycle.</summary>
        private void ServoTip(Vector3 tipTarget)
        {
            float err = Vector3.Distance(_needle.Tip.position, tipTarget);
            if (err < 0.003f) return;
            float gain = Mathf.Clamp(err * 6f, 0.12f, 0.55f);
            Vector3 tipOffset = _needle.Tip.position - _arm.Tcp.position;
            _arm.ServoToward(tipTarget - tipOffset, gain: gain);
        }

        private float _withinRadiusSince = -1f;

        /// <summary>Free-space phases only need coarse arrival; the piercing
        /// phases need the tip close to the stitch point.</summary>
        private float PhaseRadius =>
            _phase == Phase.Approach || _phase == Phase.Pull ? Settings.coarseRadius : Settings.fineRadius;

        /// <summary>Arrived = inside the phase radius AND either settled or
        /// dwelling there - the servo's limit cycle near the target can keep the
        /// TCP speed above the threshold forever. A per-phase timeout also
        /// advances phases that settle just outside the radius (reduced-DOF
        /// arms can't null the tip residual exactly).</summary>
        private float _phaseEnteredAt;

        private bool Arrived()
        {
            float dist = Vector3.Distance(_needle.Tip.position, _phaseTarget);
            bool inside = dist < PhaseRadius;
            if (!inside)
            {
                _withinRadiusSince = -1f;
                return Time.time - _phaseEnteredAt > 3f && dist < PhaseRadius * 2f;
            }
            if (_withinRadiusSince < 0f) _withinRadiusSince = Time.time;
            return _arm.TcpSpeed < Settings.arriveSpeed ||
                   Time.time - _withinRadiusSince > Settings.dwellSeconds;
        }

        private string Describe(StitchPoint s) =>
            $"{_phase} stitch {_stitchIndex + 1}/{_task.stitches.Count} " +
            $"(tip {Vector3.Distance(_needle.Tip.position, _phaseTarget):F2} m, v {_arm.TcpSpeed:F2})";
    }
}
