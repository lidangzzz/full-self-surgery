using UnityEngine;

namespace SofaRobotAI.Core
{
    /// <summary>
    /// Builds a multi-DOF serial arm procedurally as an ArticulationBody chain.
    /// Every joint is a 1-DoF revolute ArticulationBody driven by its xDrive,
    /// so the arm is fully physics-simulated and controllable by joint targets.
    /// </summary>
    public static class RobotArmBuilder
    {
        public enum SurgicalTool { None, Tweezers, NeedleHolder }

        /// <summary>
        /// Creates the arm under <paramref name="parent"/> at <paramref name="basePosition"/>.
        /// Link i sits at the pivot of joint i and extends along +Y by spec.joints[i].linkLength.
        /// The last link carries a wrist with two prismatic gripper fingers and the TCP frame.
        /// </summary>
        public static RobotArmController Build(ArmSpec spec, Transform parent, Vector3 basePosition, Color color)
        {
            var root = new GameObject(spec.armName + "_Root");
            root.transform.SetParent(parent, false);
            root.transform.position = basePosition;

            // Root (base) body: immovable anchor of the articulation.
            var baseBody = root.AddComponent<ArticulationBody>();
            baseBody.immovable = true;
            baseBody.jointType = ArticulationJointType.FixedJoint;
            baseBody.useGravity = false;
            AddCollider(root, new Vector3(0.26f, 0.06f, 0.26f), Vector3.zero);

            ArticulationBody parentBody = baseBody;
            Transform parentLink = root.transform;
            float parentLinkLength = 0.03f; // base plate to first joint
            var jointBodies = new ArticulationBody[spec.Dof];

            // Anchor frames: child origin at joint pivot, X axis aligned with the joint axis.
            // parentAnchor (parent local) and anchor (child local) share the same rotation so
            // the zero configuration reproduces the built hierarchy exactly.
            for (int i = 0; i < spec.Dof; i++)
            {
                JointSpec js = spec.joints[i];
                var link = new GameObject(js.name);
                link.transform.SetParent(parentLink, false);
                link.transform.localPosition = Vector3.up * parentLinkLength;
                link.transform.localRotation = Quaternion.identity;

                Quaternion anchorRot = Quaternion.FromToRotation(Vector3.right, js.axis.normalized);

                var body = link.AddComponent<ArticulationBody>();
                body.jointType = ArticulationJointType.RevoluteJoint;
                body.twistLock = ArticulationDofLock.LimitedMotion;
                body.anchorPosition = Vector3.zero;          // pivot at this link's origin
                body.anchorRotation = anchorRot;
                body.parentAnchorPosition = link.transform.localPosition;
                body.parentAnchorRotation = anchorRot;
                body.useGravity = false;

                body.xDrive = new ArticulationDrive
                {
                    stiffness = spec.jointStiffness,
                    damping = spec.jointDamping,
                    forceLimit = spec.jointForceLimit,
                    lowerLimit = js.lowerLimitDeg,
                    upperLimit = js.upperLimitDeg,
                    target = 0f,
                };

                // Visual + collision of the link segment pointing to the next joint (capsule is Y-aligned).
                float len = js.linkLength;
                float th = js.linkThickness;
                var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                visual.name = "link";
                Object.Destroy(visual.GetComponent<Collider>()); // chain collider below is cheaper
                visual.transform.SetParent(link.transform, false);
                visual.transform.localPosition = new Vector3(0f, len * 0.5f, 0f);
                visual.transform.localScale = new Vector3(th, len * 0.5f, th);
                visual.GetComponent<Renderer>().sharedMaterial = MakeMat(color);

                AddCollider(link, new Vector3(th, len, th), new Vector3(0f, len * 0.5f, 0f));

                jointBodies[i] = body;
                parentBody = body;
                parentLink = link.transform;
                parentLinkLength = len;
            }

            Transform tcp = BuildGripper(spec, parentLink, color, out ArticulationBody[] fingers);

            var arm = root.AddComponent<RobotArmController>();
            arm.Configure(spec, jointBodies, fingers, tcp);
            return arm;
        }

        /// <summary>
        /// 三段式手术工具臂：每关节一段臂杆（基座回转段 / 肩段 / 肘-腕段），
        /// 末端法兰直接安装器械（镊子或持针器）——没有二指机械手爪架。
        /// 持针器上再装医用弯针与缝线。
        /// 返回的 controller 的 TCP 即器械工作端（镊尖 / 持针器钳口）。
        /// </summary>
        public static RobotArmController BuildThreeSegmentToolArm(
            ArmSpec spec, Transform parent, Vector3 basePosition, Color color, SurgicalTool tool)
        {
            var root = new GameObject(spec.armName + "_Root3S");
            root.transform.SetParent(parent, false);
            root.transform.position = basePosition;

            var baseBody = root.AddComponent<ArticulationBody>();
            baseBody.immovable = true;
            baseBody.jointType = ArticulationJointType.FixedJoint;
            baseBody.useGravity = false;
            AddCollider(root, new Vector3(0.22f, 0.06f, 0.22f), Vector3.zero);

            ArticulationBody parentBody = baseBody;
            Transform parentLink = root.transform;
            float parentLinkLength = 0.03f;
            var jointBodies = new ArticulationBody[spec.Dof];

            for (int i = 0; i < spec.Dof; i++)
            {
                JointSpec js = spec.joints[i];
                var link = new GameObject(js.name);
                link.transform.SetParent(parentLink, false);
                link.transform.localPosition = Vector3.up * parentLinkLength;
                link.transform.localRotation = Quaternion.identity;

                Quaternion anchorRot = Quaternion.FromToRotation(Vector3.right, js.axis.normalized);

                var body = link.AddComponent<ArticulationBody>();
                body.jointType = ArticulationJointType.RevoluteJoint;
                body.twistLock = ArticulationDofLock.LimitedMotion;
                body.anchorPosition = Vector3.zero;
                body.anchorRotation = anchorRot;
                body.parentAnchorPosition = link.transform.localPosition;
                body.parentAnchorRotation = anchorRot;
                body.useGravity = false;
                body.xDrive = new ArticulationDrive
                {
                    stiffness = spec.jointStiffness,
                    damping = spec.jointDamping,
                    forceLimit = spec.jointForceLimit,
                    lowerLimit = js.lowerLimitDeg,
                    upperLimit = js.upperLimitDeg,
                    target = 0f,
                };

                float len = js.linkLength;
                float th = js.linkThickness;
                if (i == spec.Dof - 1 && len < 0.15f)
                {
                    // 器械腕（短节）：属于工具法兰组件，视觉上不是第四段臂杆。
                    var stub = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    stub.name = "wrist_roll";
                    Object.Destroy(stub.GetComponent<Collider>());
                    stub.transform.SetParent(link.transform, false);
                    stub.transform.localPosition = new Vector3(0f, len * 0.5f, 0f);
                    stub.transform.localScale = new Vector3(th, len * 0.5f, th);
                    stub.GetComponent<Renderer>().sharedMaterial = MakeMat(Color.Lerp(color, Color.white, 0.3f));
                }
                else
                {
                    var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    visual.name = "segment";
                    Object.Destroy(visual.GetComponent<Collider>());
                    visual.transform.SetParent(link.transform, false);
                    visual.transform.localPosition = new Vector3(0f, len * 0.5f, 0f);
                    visual.transform.localScale = new Vector3(th, len * 0.5f, th);
                    visual.GetComponent<Renderer>().sharedMaterial = MakeMat(color);
                }
                AddCollider(link, new Vector3(th, len, th), new Vector3(0f, len * 0.5f, 0f));

                jointBodies[i] = body;
                parentBody = body;
                parentLink = link.transform;
                parentLinkLength = len;
            }

            Transform tcp = BuildToolMount(spec, parentLink, tool);

            var arm = root.AddComponent<RobotArmController>();
            arm.Configure(spec, jointBodies, new ArticulationBody[0], tcp);
            return arm;
        }

        /// <summary>End flange + the mounted surgical instrument. Returns the
        /// instrument's working frame (tweezer tip / needle-holder jaw).</summary>
        private static Transform BuildToolMount(ArmSpec spec, Transform wrist, SurgicalTool tool)
        {
            float wristLen = spec.joints[spec.Dof - 1].linkLength;

            // Tool flange at the wrist tip (rotated so tools point along +Y
            // of the wrist, same convention as the gripper arms).
            var flange = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            flange.name = "tool_flange";
            flange.transform.SetParent(wrist, false);
            flange.transform.localPosition = new Vector3(0f, wristLen + 0.015f, 0f);
            flange.transform.localScale = new Vector3(0.06f, 0.015f, 0.06f);
            flange.GetComponent<Renderer>().sharedMaterial = MakeMat(Color.Lerp(Color.gray, Color.white, 0.35f));

            var metallic = new Material(Shader.Find("Standard")) { color = new Color(0.78f, 0.80f, 0.85f) };
            metallic.SetFloat("_Glossiness", 0.85f);

            if (tool == SurgicalTool.Tweezers)
            {
                // 镊子：两条细锥形臂，从法兰向下汇聚到镊尖。
                var holder = new GameObject("tweezers");
                holder.transform.SetParent(wrist, false);
                holder.transform.localPosition = new Vector3(0f, wristLen + 0.02f, 0f);

                for (int s = 0; s < 2; s++)
                {
                    float side = s == 0 ? 1f : -1f;
                    var prong = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    prong.name = s == 0 ? "prong_L" : "prong_R";
                    Object.Destroy(prong.GetComponent<Collider>());
                    prong.transform.SetParent(holder.transform, false);
                    // 细长锥形（用两段近似）：根部段 + 尖端段，向中线内收。
                    prong.transform.localPosition = new Vector3(side * 0.012f, 0.045f, 0f);
                    prong.transform.localRotation = Quaternion.Euler(0f, 0f, -side * 7f);
                    prong.transform.localScale = new Vector3(0.006f, 0.09f, 0.010f);
                    prong.GetComponent<Renderer>().sharedMaterial = metallic;

                    var point = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    point.name = prong.name + "_tip";
                    Object.Destroy(point.GetComponent<Collider>());
                    point.transform.SetParent(holder.transform, false);
                    point.transform.localPosition = new Vector3(side * 0.0022f, 0.096f, 0f);
                    point.transform.localRotation = Quaternion.Euler(0f, 0f, -side * 2f);
                    point.transform.localScale = new Vector3(0.0035f, 0.024f, 0.005f);
                    point.GetComponent<Renderer>().sharedMaterial = metallic;
                }

                var tip = new GameObject("tweezer_tip").transform;
                tip.SetParent(holder.transform, false);
                tip.localPosition = new Vector3(0f, 0.108f, 0f);
                return tip;
            }

            if (tool == SurgicalTool.NeedleHolder)
            {
                // 持针器：细杆 + 钳口，钳口处装医用弯针（NeedleTool 由场景侧
                // 通过 NeedleTool.Attach 挂载，半径更接近血管缝合的比例）。
                var holder = new GameObject("needle_holder");
                holder.transform.SetParent(wrist, false);
                holder.transform.localPosition = new Vector3(0f, wristLen + 0.02f, 0f);

                var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                shaft.name = "shaft";
                Object.Destroy(shaft.GetComponent<Collider>());
                shaft.transform.SetParent(holder.transform, false);
                shaft.transform.localPosition = new Vector3(0f, 0.035f, 0f);
                shaft.transform.localScale = new Vector3(0.014f, 0.035f, 0.014f);
                shaft.GetComponent<Renderer>().sharedMaterial = metallic;

                var jaw = GameObject.CreatePrimitive(PrimitiveType.Cube);
                jaw.name = "jaw";
                Object.Destroy(jaw.GetComponent<Collider>());
                jaw.transform.SetParent(holder.transform, false);
                jaw.transform.localPosition = new Vector3(0f, 0.078f, 0f);
                jaw.transform.localScale = new Vector3(0.016f, 0.026f, 0.018f);
                jaw.GetComponent<Renderer>().sharedMaterial = metallic;

                var tip = new GameObject("jaw_tip").transform;
                tip.SetParent(holder.transform, false);
                tip.localPosition = new Vector3(0f, 0.092f, 0f);
                return tip;
            }

            // No tool: TCP stays at the flange.
            var plain = new GameObject("TCP").transform;
            plain.SetParent(wrist, false);
            plain.localPosition = new Vector3(0f, wristLen + 0.03f, 0f);
            return plain;
        }

        /// <returns>The TCP (tool center point) transform at the wrist tip.</returns>
        private static Transform BuildGripper(ArmSpec spec, Transform wrist, Color color, out ArticulationBody[] fingers)
        {
            float wristLen = spec.joints[spec.Dof - 1].linkLength;

            // TCP frame at the tip of the wrist link.
            var tcp = new GameObject("TCP");
            tcp.transform.SetParent(wrist, false);
            tcp.transform.localPosition = new Vector3(0f, wristLen, 0f);

            var palm = new GameObject("gripper");
            palm.transform.SetParent(wrist, false);
            palm.transform.localPosition = new Vector3(0f, wristLen * 0.55f, 0f);
            AddCollider(palm, new Vector3(0.12f, 0.05f, 0.12f), Vector3.zero);

            // Two prismatic fingers closing symmetrically along local X.
            // Joint position 0 == built (open) pose, so L closes toward -X and R toward +X.
            float travel = spec.fingerOpen - spec.fingerClosed;
            var fingerBodies = new ArticulationBody[2];
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? 1f : -1f; // L at +X, R at -X
                var finger = new GameObject(s == 0 ? "finger_L" : "finger_R");
                finger.transform.SetParent(palm.transform, false);
                finger.transform.localPosition = new Vector3(side * spec.fingerOpen, 0.05f, 0f);

                var fb = finger.AddComponent<ArticulationBody>();
                fb.jointType = ArticulationJointType.PrismaticJoint;
                fb.linearLockX = ArticulationDofLock.LimitedMotion;
                fb.anchorPosition = Vector3.zero;
                fb.anchorRotation = Quaternion.identity;
                fb.parentAnchorPosition = finger.transform.localPosition;
                fb.parentAnchorRotation = Quaternion.identity;
                fb.useGravity = false;
                fb.xDrive = new ArticulationDrive
                {
                    stiffness = spec.jointStiffness,
                    damping = spec.jointDamping * 0.5f,
                    forceLimit = spec.jointForceLimit,
                    lowerLimit = side > 0 ? -travel : 0f,
                    upperLimit = side > 0 ? 0f : travel,
                    target = 0f, // open
                };
                fingerBodies[s] = fb;

                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "finger_visual";
                Object.Destroy(visual.GetComponent<Collider>());
                visual.transform.SetParent(finger.transform, false);
                visual.transform.localScale = new Vector3(0.02f, spec.fingerLength, 0.05f);
                visual.transform.localPosition = new Vector3(0f, spec.fingerLength * 0.5f + 0.03f, 0f);
                visual.GetComponent<Renderer>().sharedMaterial = MakeMat(Color.Lerp(color, Color.white, 0.4f));
            }

            fingers = fingerBodies;
            return tcp.transform;
        }

        private static void AddCollider(GameObject go, Vector3 size, Vector3 center)
        {
            var box = go.AddComponent<BoxCollider>();
            box.size = size;
            box.center = center;
        }

        private static Material _sharedMat;
        private static Color _matColor;
        private static Material MakeMat(Color c)
        {
            // One shared material per color keeps draw calls low when spawning N arms.
            if (_sharedMat == null || _matColor != c)
            {
                _sharedMat = new Material(Shader.Find("Standard"));
                _sharedMat.color = c;
                _matColor = c;
            }
            return _sharedMat;
        }
    }
}
