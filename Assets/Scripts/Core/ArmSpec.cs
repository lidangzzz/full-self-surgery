using System;
using System.Collections.Generic;
using UnityEngine;

namespace SofaRobotAI.Core
{
    /// <summary>
    /// Description of one revolute joint of a robot arm.
    /// Joint rotation happens about <see cref="axis"/> expressed in the parent link's local frame.
    /// </summary>
    [Serializable]
    public class JointSpec
    {
        [Tooltip("Joint name, e.g. shoulder_yaw")]
        public string name;

        [Tooltip("Rotation axis in parent-link local space")]
        public Vector3 axis = Vector3.up;

        [Tooltip("Link length (m) that follows this joint, along the chain direction")]
        public float linkLength = 0.35f;

        [Tooltip("Link thickness (m)")]
        public float linkThickness = 0.10f;

        [Tooltip("Joint position limits (degrees)")]
        public float lowerLimitDeg = -170f;
        public float upperLimitDeg = 170f;

        [Tooltip("Maximum joint speed used by the servo (rad/s equivalent scaling)")]
        public float maxServoStepDeg = 3.0f;

        public JointSpec(string name, Vector3 axis, float linkLength,
                         float lowerLimitDeg = -170f, float upperLimitDeg = 170f)
        {
            this.name = name;
            this.axis = axis;
            this.linkLength = linkLength;
            this.lowerLimitDeg = lowerLimitDeg;
            this.upperLimitDeg = upperLimitDeg;
        }
    }

    /// <summary>
    /// Full description of a multi-DOF arm: the joint chain plus gripper geometry.
    /// Serial arms of arbitrary DOF can be described; the default is a 6-DOF arm.
    /// </summary>
    [CreateAssetMenu(fileName = "ArmSpec", menuName = "SofaRobotAI/Arm Spec")]
    public class ArmSpec : ScriptableObject
    {
        [Tooltip("Arm display name used in HUD and logs")]
        public string armName = "Arm";

        [Tooltip("Chain of revolute joints, from base to wrist")]
        public List<JointSpec> joints = new List<JointSpec>();

        [Header("Gripper")]
        public float fingerLength = 0.14f;
        public float fingerOpen = 0.07f;
        public float fingerClosed = 0.01f;
        public float graspRadius = 0.09f;

        [Header("Drives")]
        public float jointStiffness = 20000f;
        public float jointDamping = 800f;
        public float jointForceLimit = 1e6f;

        public int Dof => joints?.Count ?? 0;

        /// <summary>Approximate maximum arm reach: the sum of all link lengths.</summary>
        public float Reach
        {
            get
            {
                float s = 0f;
                if (joints != null) foreach (var j in joints) s += j.linkLength;
                return s;
            }
        }

        /// <summary>Default 6-DOF industrial-style arm: yaw, pitch, pitch, roll, pitch, roll.</summary>
        public static ArmSpec CreateDefault6Dof(string armName, float scale = 1.0f)
        {
            var spec = ScriptableObject.CreateInstance<ArmSpec>();
            spec.armName = armName;
            spec.joints = new List<JointSpec>
            {
                new JointSpec(armName + "_j1_base_yaw",      Vector3.up,    0.30f * scale, -170f, 170f),
                new JointSpec(armName + "_j2_shoulder_pitch", Vector3.right, 0.55f * scale, -120f, 120f),
                new JointSpec(armName + "_j3_elbow_pitch",    Vector3.right, 0.45f * scale, -140f, 140f),
                new JointSpec(armName + "_j4_forearm_roll",   Vector3.forward, 0.35f * scale, -180f, 180f),
                new JointSpec(armName + "_j5_wrist_pitch",    Vector3.right, 0.16f * scale, -120f, 120f),
                new JointSpec(armName + "_j6_wrist_roll",     Vector3.forward, 0.10f * scale, -180f, 180f),
            };
            return spec;
        }

        /// <summary>三段式手术工具臂规格：三大段（基座回转 / 肩俯仰 / 肘俯仰）
        /// + 器械腕旋转。腕属于工具法兰组件（短节），不算第四段臂杆——
        /// 纯俯仰平面的机械臂在伸展方向存在奇异（径向梯度消失，弯针尖到
        /// 不了切口远侧），腕转打破该奇异。</summary>
        public static ArmSpec CreateThreeSegmentToolSpec(string armName, float scale = 1.0f)
        {
            var spec = ScriptableObject.CreateInstance<ArmSpec>();
            spec.armName = armName;
            spec.joints = new List<JointSpec>
            {
                new JointSpec(armName + "_s1_base_yaw",       Vector3.up,      0.50f * scale, -170f, 170f),
                new JointSpec(armName + "_s2_shoulder_pitch", Vector3.right,   0.48f * scale, -120f, 120f),
                new JointSpec(armName + "_s3_elbow_pitch",    Vector3.right,   0.40f * scale, -140f, 140f),
                new JointSpec(armName + "_wrist_roll",        Vector3.forward, 0.09f * scale, -180f, 180f),
            };
            for (int i = 0; i < 3; i++) spec.joints[i].linkThickness = 0.09f;
            spec.joints[3].linkThickness = 0.045f;
            spec.joints[3].maxServoStepDeg = 4.5f;
            return spec;
        }

        /// <summary>Factory used by the cell manager: 4, 5 (default), 6 or 7 DOF arms.</summary>
        public static ArmSpec CreatePreset(string armName, int dof, float scale = 1.0f)
        {
            switch (dof)
            {
                case 3:
                {
                    // 三段式手术工具臂：基座回转 + 肩俯仰 + 肘/腕俯仰，
                    // 每个关节驱动一段臂杆，末端法兰直接安装镊子/持针器。
                    var spec = ScriptableObject.CreateInstance<ArmSpec>();
                    spec.armName = armName;
                    spec.joints = new List<JointSpec>
                    {
                        new JointSpec(armName + "_s1_base_yaw",   Vector3.up,    0.50f * scale, -170f, 170f),
                        new JointSpec(armName + "_s2_shoulder_pitch", Vector3.right, 0.48f * scale, -120f, 120f),
                        new JointSpec(armName + "_s3_elbow_pitch",    Vector3.right, 0.40f * scale, -140f, 140f),
                    };
                    foreach (var js in spec.joints) js.linkThickness = 0.09f;
                    return spec;
                }
                case 4:
                {
                    var spec = ScriptableObject.CreateInstance<ArmSpec>();
                    spec.armName = armName;
                    spec.joints = new List<JointSpec>
                    {
                        new JointSpec(armName + "_j1_base_yaw",       Vector3.up,     0.30f * scale, -170f, 170f),
                        new JointSpec(armName + "_j2_shoulder_pitch", Vector3.right,  0.55f * scale, -120f, 120f),
                        new JointSpec(armName + "_j3_elbow_pitch",    Vector3.right,  0.45f * scale, -140f, 140f),
                        new JointSpec(armName + "_j4_wrist_roll",     Vector3.forward,0.14f * scale, -180f, 180f),
                    };
                    return spec;
                }

                case 5:
                {
                    var spec = ScriptableObject.CreateInstance<ArmSpec>();
                    spec.armName = armName;
                    spec.joints = new List<JointSpec>
                    {
                        new JointSpec(armName + "_j1_base_yaw",       Vector3.up,     0.30f * scale, -170f, 170f),
                        new JointSpec(armName + "_j2_shoulder_pitch", Vector3.right,  0.58f * scale, -120f, 120f),
                        new JointSpec(armName + "_j3_elbow_pitch",    Vector3.right,  0.50f * scale, -140f, 140f),
                        new JointSpec(armName + "_j4_wrist_pitch",    Vector3.right,  0.16f * scale, -120f, 120f),
                        new JointSpec(armName + "_j5_wrist_roll",     Vector3.forward,0.10f * scale, -180f, 180f),
                    };
                    return spec;
                }
                case 7:
                {
                    var spec = ScriptableObject.CreateInstance<ArmSpec>();
                    spec.armName = armName;
                    spec.joints = new List<JointSpec>
                    {
                        new JointSpec(armName + "_j1_base_yaw",       Vector3.up,     0.25f * scale, -170f, 170f),
                        new JointSpec(armName + "_j2_shoulder_pitch", Vector3.right,  0.40f * scale, -120f, 120f),
                        new JointSpec(armName + "_j3_arm_roll",       Vector3.forward,0.30f * scale, -180f, 180f),
                        new JointSpec(armName + "_j4_elbow_pitch",    Vector3.right,  0.40f * scale, -140f, 140f),
                        new JointSpec(armName + "_j5_forearm_roll",   Vector3.forward,0.28f * scale, -180f, 180f),
                        new JointSpec(armName + "_j6_wrist_pitch",    Vector3.right,  0.14f * scale, -120f, 120f),
                        new JointSpec(armName + "_j7_wrist_roll",     Vector3.forward,0.09f * scale, -180f, 180f),
                    };
                    return spec;
                }
                default:
                    // Surgical arms are 4-5 DOF by design; unknown values fall
                    // back to the 5-DOF preset, never a 6/7-DOF arm.
                    return CreatePreset(armName, 5, scale);
            }
        }
    }
}
