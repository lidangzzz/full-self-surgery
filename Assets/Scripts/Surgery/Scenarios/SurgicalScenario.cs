using System;
using System.Collections.Generic;
using SofaRobotAI.Agent;
using SofaRobotAI.Backend;
using SofaRobotAI.Core;
using UnityEngine;

namespace SofaRobotAI.Surgery.Scenarios
{
    /// <summary>How one arm participates in a scenario.</summary>
    public enum ArmRole
    {
        Suturer,    // holds a needle, closes its batch of stitches
        Stabilizer, // presses near the wound to steady the tissue
        Idle,       // parked
    }

    /// <summary>Physical arm structure used by a scenario.</summary>
    public enum ArmStructure
    {
        StandardGripper, // multi-DOF chain + two-finger gripper (default)
        ThreeSegmentTool // 三段式臂 + 末端直接安装手术器械（无手指爪架）
    }

    /// <summary>Surgical instrument mounted at a three-segment arm's flange.</summary>
    public enum ArmTool { None, Tweezers, NeedleHolder }

    /// <summary>
    /// One selectable surgical scenario: tissue structure, target wound,
    /// stitch layout, arm configuration and per-scenario runtime behaviour
    /// (e.g. the beating-heart surface). Scenarios share the SOFA scene
    /// (tissue FEM pad + coupling spheres) and the suture machinery.
    /// </summary>
    public abstract class SurgicalScenario
    {
        public abstract string Id { get; }
        public abstract string Title { get; }
        /// <summary>Entries from the surgical-procedure catalogue this
        /// scenario trains (for the HUD/API).</summary>
        public abstract string[] SurgeryRefs { get; }
        public abstract string Description { get; }

        public virtual int ArmCount => 3;
        public virtual int DofPerArm => 5;
        public virtual ArmStructure Structure => ArmStructure.StandardGripper;
        public virtual ArmTool ToolFor(int armIndex) => ArmTool.None;
        /// <summary>Base spacing override (0 = manager default).</summary>
        public virtual float BaseSpacing => 0f;
        /// <summary>Working surface height for needle-tissue coupling
        /// (0.53 = tissue pad; the brain scenario raises it to the cortex).</summary>
        public virtual float TissueSurfaceY => 0.53f;
        public virtual ArmRole RoleFor(int armIndex) => ArmRole.Suturer;
        public virtual SutureExecutor.Config ExecutorConfig => new SutureExecutor.Config();
        /// <summary>World point a Stabilizer arm presses toward.</summary>
        public virtual Vector3 StabilizePoint => new Vector3(0.18f, 0.585f, 0.90f);

        /// <summary>Creates the Unity visuals under <paramref name="root"/> and
        /// returns the sequential suture batches. The SOFA tissue mesh is
        /// created once by the manager and can be recolored via
        /// <see cref="TissueDeformationSync"/>.</summary>
        public abstract List<SutureTask> Build(Transform root, List<ArmAgent> agents,
                                               SofaUnityBackend sofa);

        /// <summary>Per-physics-step scenario logic (heartbeat, ...).</summary>
        public virtual void Tick(MultiArmAgentManager manager, SofaUnityBackend sofa) { }

        /// <summary>Parks any scenario-driven coupling sphere.</summary>
        public virtual void Park(SofaUnityBackend sofa) { }

        public override string ToString() => $"{Id}: {Title}";
    }

    /// <summary>Shared procedural builders for the scenarios.</summary>
    internal static class SurgeryKit
    {
        public const float TableTopY = 0.40f;
        public const float TableZ = 0.90f;
        public const float TissueTopY = 0.53f;

        /// <summary>肝成形：把 SOFA 长方体组织网格经平滑形变映射为肝形——
        /// 前后缘圆化、右叶向后楔形收窄、中央（走廊外）隆起增厚。
        /// 中央手术走廊（|x|&lt;0.16）保持恒等，保证针-组织耦合与所有
        /// 场景伤口几何不变。</summary>
        public static Vector3 LiverWarp(Vector3 v)
        {
            float zn = Mathf.Clamp01((v.z - 0.53f) / 0.75f);   // 0 前缘, 1 后缘
            float corridor = Smooth01((Mathf.Abs(v.x) - 0.16f) / 0.26f);
            float edge = Mathf.Sin(zn * Mathf.PI);              // 1 中部, 0 前后缘
            float width = 0.72f + 0.28f * edge;                 // 前后缘收窄（肝叶轮廓）
            if (v.x > 0f) width *= 1f - 0.32f * zn;             // 右叶向后楔形
            float x = v.x * Mathf.Lerp(1f, width, corridor);
            float y = v.y + LiverDome(v.x, v.z);
            return new Vector3(x, y, v.z);
        }

        /// <summary>肝表面隆起高度（走廊外最高 3cm，中央伤口区为 0）。
        /// 肝上血管按此抬升，贴在肝表面。</summary>
        public static float LiverDome(float x, float z)
        {
            float zn = Mathf.Clamp01((z - 0.53f) / 0.75f);
            float corridor = Smooth01((Mathf.Abs(x) - 0.16f) / 0.26f);
            return corridor * 0.030f * Mathf.Sin(zn * Mathf.PI);
        }

        private static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        public static void BuildTable(Transform parent, float width, Color color)
        {
            // 重新设计：浅色手术台 + 无菌铺巾（中央开窗露出组织垫），
            // 干净直观的手术室观感。
            var table = GameObject.CreatePrimitive(PrimitiveType.Cube);
            table.name = "OperatingTable";
            table.transform.SetParent(parent, false);
            table.transform.localPosition = new Vector3(0f, TissueTopY - 0.14f, TableZ);
            table.transform.localScale = new Vector3(width, 0.08f, 0.7f);
            table.GetComponent<Renderer>().sharedMaterial =
                Mat(new Color(0.58f, 0.62f, 0.66f)); // 浅钢色台面

            // 无菌铺巾：略大于台面的薄层，组织垫从中"开窗"穿出。
            var drape = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(drape.GetComponent<Collider>());
            drape.name = "SurgicalDrape";
            drape.transform.SetParent(parent, false);
            drape.transform.localPosition = new Vector3(0f, TissueTopY - 0.088f, TableZ);
            drape.transform.localScale = new Vector3(width * 1.04f, 0.012f, 0.82f);
            drape.GetComponent<Renderer>().sharedMaterial =
                Mat(new Color(0.10f, 0.42f, 0.38f)); // 手术青绿色

            // 铺巾前缘短垂帘（点睛，不添乱）。
            var skirt = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(skirt.GetComponent<Collider>());
            skirt.name = "DrapeSkirt";
            skirt.transform.SetParent(parent, false);
            skirt.transform.localPosition = new Vector3(0f, TissueTopY - 0.115f, TableZ + 0.41f);
            skirt.transform.localScale = new Vector3(width * 1.04f, 0.05f, 0.006f);
            skirt.GetComponent<Renderer>().sharedMaterial =
                Mat(new Color(0.09f, 0.38f, 0.35f));

            for (int i = 0; i < 4; i++)
            {
                var leg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(leg.GetComponent<Collider>());
                leg.name = $"tableLeg_{i}";
                leg.transform.SetParent(parent, false);
                float lx = (i % 2 == 0 ? -1 : 1) * (width * 0.5f - 0.12f);
                float lz = TableZ + (i < 2 ? -0.26f : 0.26f);
                float legH = (TissueTopY - 0.14f) * 0.5f;
                leg.transform.localPosition = new Vector3(lx, legH, lz);
                leg.transform.localScale = new Vector3(0.05f, legH, 0.05f);
                leg.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.55f, 0.58f, 0.62f));
            }
        }

        /// <summary>A vessel/tendon tube between two world points.</summary>
        public static GameObject Tube(Transform parent, string name, Vector3 a, Vector3 b,
                                      float radius, Color color)
        {
            var tube = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(tube.GetComponent<Collider>());
            tube.name = name;
            tube.transform.SetParent(parent, false);
            Vector3 dir = b - a;
            tube.transform.position = (a + b) * 0.5f;
            tube.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
            tube.transform.localScale = new Vector3(radius * 2f, dir.magnitude * 0.5f, radius * 2f);
            tube.GetComponent<Renderer>().sharedMaterial = Glossy(color);
            return tube;
        }

        /// <summary>肝血管：单支肝血管树，从肝门（前左缘暗带处）入肝，
        /// 主干向右后平滑走行贴于肝表面（按隆起高度抬升），发出等间距
        /// 分支。始终避开中央目标走廊；右叶保持干净。rootCount 决定分支
        /// 数（&gt;=6:2支, 否则1支）。</summary>
        public static void VascularNetwork(Transform parent, System.Random rng, int rootCount,
                                           float rMin, float rMax, float halfX, float halfZ,
                                           Color artery, Color vein, float corridorZ, float corridorHalfWidth)
        {
            float mainR = Mathf.Clamp((rMin + rMax) * 0.5f, 0.008f, 0.011f);
            int branches = rootCount >= 6 ? 2 : 1;

            // 主干：肝门 (-0.55, 0.56) -> 左叶深部 (-0.16, 1.02)，平滑S形。
            var path = new List<Vector3>();
            for (int i = 0; i <= 6; i++)
            {
                float t = i / 6f;
                float x = Mathf.Lerp(-0.55f, -0.16f, t);
                float z = 0.56f + 0.46f * t + 0.06f * Mathf.Sin(t * Mathf.PI);
                float y = TissueTopY + LiverDome(x, z) + mainR * 0.6f;
                path.Add(new Vector3(x, y, z));
            }
            for (int i = 0; i + 1 < path.Count; i++)
                Tube(parent, "hepatic_trunk", path[i], path[i + 1], mainR, artery);

            // 分支：等间距、朝左后/左前，贴肝表面。
            for (int b = 0; b < branches; b++)
            {
                float t = 0.34f + 0.34f * b;
                Vector3 baseP = SamplePath(path, t);
                var dir = new Vector2(-0.62f, b == 0 ? 0.78f : -0.70f).normalized;
                var end2 = new Vector3(
                    baseP.x + dir.x * 0.20f, 0f, Mathf.Clamp(baseP.z + dir.y * 0.20f, 0.60f, 1.22f));
                end2.y = TissueTopY + LiverDome(end2.x, end2.z) + mainR * 0.45f;
                baseP.y = TissueTopY + LiverDome(baseP.x, baseP.z) + mainR * 0.6f;
                Tube(parent, "hepatic_branch", baseP, end2, mainR * 0.55f, artery);

                // 每支一条末梢细枝。
                var twigDir = new Vector2(-0.3f, b == 0 ? 0.95f : -0.95f).normalized;
                var twig = new Vector3(
                    end2.x + twigDir.x * 0.10f, 0f, Mathf.Clamp(end2.z + twigDir.y * 0.10f, 0.60f, 1.24f));
                twig.y = TissueTopY + LiverDome(twig.x, twig.z) + mainR * 0.35f;
                Tube(parent, "hepatic_tw", end2, twig, mainR * 0.33f, artery);
            }
        }

        private static Vector3 SamplePath(List<Vector3> path, float t)
        {
            float f = Mathf.Clamp01(t) * (path.Count - 1);
            int i = Mathf.Min((int)f, path.Count - 2);
            return Vector3.Lerp(path[i], path[i + 1], f - i);
        }

        /// <summary>Severance visuals: dark cut end caps, optional blood pool,
        /// pulsing ring and floating arrow. Returns the pool (or the ring when
        /// the scenario does not bleed) as the shrinkable wound visual.</summary>
        public static GameObject CutSite(Transform parent, Vector3 center, float radius,
                                         float halfGap, float poolScale, bool withPool,
                                         Color ringColor)
        {
            float y = center.y;
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                var cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(cap.GetComponent<Collider>());
                cap.name = s == 0 ? "cutEnd_L" : "cutEnd_R";
                cap.transform.SetParent(parent, false);
                cap.transform.position = new Vector3(center.x + side * halfGap, y, center.z);
                cap.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
                cap.transform.localScale = new Vector3(radius * 2.15f, 0.003f, radius * 2.15f);
                cap.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.28f, 0.01f, 0.02f));
            }

            GameObject wound = null;
            if (withPool)
            {
                var pool = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(pool.GetComponent<Collider>());
                pool.name = "BloodPool";
                pool.transform.SetParent(parent, false);
                pool.transform.position = center - new Vector3(0f, y - (TissueTopY + 0.0015f), 0f);
                pool.transform.localScale = new Vector3(0.10f * poolScale, 0.0015f, 0.075f * poolScale);
                pool.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.32f, 0.015f, 0.03f));
                wound = pool;
            }

            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(ring.GetComponent<Collider>());
            ring.name = "SutureTargetRing";
            ring.transform.SetParent(parent, false);
            ring.transform.position = center - new Vector3(0f, y - (TissueTopY + 0.001f), 0f);
            ring.transform.localScale = new Vector3(0.13f, 0.001f, 0.10f);
            ring.GetComponent<Renderer>().sharedMaterial = Mat(ringColor);
            var pulse = ring.AddComponent<Pulse>();
            pulse.BaseScale = ring.transform.localScale;

            var arrow = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            UnityEngine.Object.Destroy(arrow.GetComponent<Collider>());
            arrow.name = "SutureTargetArrow";
            arrow.transform.SetParent(parent, false);
            arrow.transform.position = center + new Vector3(0f, 0.30f, 0f);
            arrow.transform.localScale = new Vector3(0.012f, 0.05f, 0.012f);
            arrow.GetComponent<Renderer>().sharedMaterial = Mat(new Color(1f, 0.8f, 0.1f));

            if (wound == null) wound = ring;
            return wound;
        }

        /// <summary>直接切开的血管切口：血管连续不断，顶部一条纵向切口缝
        /// （暗红切缘+外翻唇），伴血池/脉冲环/箭头。返回血池（随缝针收缩）。
        /// 与 CutSite（横断）相对，这里血管没有断成两截。</summary>
        public static GameObject IncisionSite(Transform parent, Vector3 center, float radius,
                                              float halfLen, float poolScale, Color ringColor)
        {
            // 切口缝：血管顶部沿 X 的暗色细槽。
            var slit = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(slit.GetComponent<Collider>());
            slit.name = "incision_slit";
            slit.transform.SetParent(parent, false);
            slit.transform.position = center + new Vector3(0f, radius * 0.92f, 0f);
            slit.transform.localScale = new Vector3(halfLen * 2f, 0.0035f, 0.0065f);
            slit.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.26f, 0.01f, 0.02f));

            // 外翻的切口唇：缝两侧各一条深红薄条。
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                var lip = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(lip.GetComponent<Collider>());
                lip.name = s == 0 ? "incision_lip_near" : "incision_lip_far";
                lip.transform.SetParent(parent, false);
                lip.transform.position = center + new Vector3(0f, radius * 0.95f, side * 0.0075f);
                lip.transform.localScale = new Vector3(halfLen * 2f, 0.0022f, 0.005f);
                lip.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.52f, 0.09f, 0.11f));
            }

            var pool = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(pool.GetComponent<Collider>());
            pool.name = "BloodPool";
            pool.transform.SetParent(parent, false);
            pool.transform.position = new Vector3(center.x, TissueTopY + 0.0015f, center.z);
            pool.transform.localScale = new Vector3(0.085f * poolScale, 0.0015f, 0.06f * poolScale);
            pool.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.32f, 0.015f, 0.03f));

            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(ring.GetComponent<Collider>());
            ring.name = "SutureTargetRing";
            ring.transform.SetParent(parent, false);
            ring.transform.position = new Vector3(center.x, TissueTopY + 0.001f, center.z);
            ring.transform.localScale = new Vector3(0.12f, 0.001f, 0.09f);
            ring.GetComponent<Renderer>().sharedMaterial = Mat(ringColor);
            var pulse = ring.AddComponent<Pulse>();
            pulse.BaseScale = ring.transform.localScale;

            var arrow = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            UnityEngine.Object.Destroy(arrow.GetComponent<Collider>());
            arrow.name = "SutureTargetArrow";
            arrow.transform.SetParent(parent, false);
            arrow.transform.position = center + new Vector3(0f, 0.30f, 0f);
            arrow.transform.localScale = new Vector3(0.012f, 0.05f, 0.012f);
            arrow.GetComponent<Renderer>().sharedMaterial = Mat(new Color(1f, 0.8f, 0.1f));

            return pool;
        }

        public static void Marker(Transform parent, Vector3 pos, Color color, float size)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.Destroy(marker.GetComponent<Collider>());
            marker.name = "stitchTarget";
            marker.transform.SetParent(parent, false);
            marker.transform.position = pos;
            marker.transform.localScale = Vector3.one * size;
            marker.GetComponent<Renderer>().sharedMaterial = Mat(color);
        }

        /// <summary>Splits <paramref name="stitches"/> into sequential batches
        /// (one batch per suturer arm) sharing one wound visual.</summary>
        public static List<SutureTask> MakeBatches(Transform parent, string id, Transform woundAnchor,
                                                   GameObject woundVisual, List<StitchPoint> stitches,
                                                   int suturerCount)
        {
            var tasks = new List<SutureTask>();
            suturerCount = Mathf.Max(1, suturerCount);
            int per = Mathf.CeilToInt(stitches.Count / (float)suturerCount);
            for (int b = 0; b * per < stitches.Count; b++)
            {
                var task = new SutureTask
                {
                    organName = $"{id}_batch{b + 1}",
                    vessel = woundAnchor,
                    incisionVisual = woundVisual,
                    incisionBaseScale = woundVisual.transform.localScale,
                };
                for (int k = b * per; k < Mathf.Min((b + 1) * per, stitches.Count); k++)
                {
                    task.stitches.Add(stitches[k]);
                    Marker(parent, stitches[k].entry, new Color(0.2f, 0.9f, 0.9f, 0.8f), 0.006f);
                }
                tasks.Add(task);
            }
            return tasks;
        }

        public static float Lerp(System.Random rng, float a, float b) =>
            Mathf.Lerp(a, b, (float)rng.NextDouble());

        public static Material Mat(Color c) => new Material(Shader.Find("Standard")) { color = c };

        public static Material Glossy(Color c)
        {
            var m = new Material(Shader.Find("Standard")) { color = c };
            m.SetFloat("_Glossiness", 0.55f);
            return m;
        }

        private class Pulse : MonoBehaviour
        {
            public Vector3 BaseScale = Vector3.one;
            private void Update() =>
                transform.localScale = BaseScale * (1f + 0.14f * Mathf.Sin(Time.time * 4f));
        }
    }

    // ---------------------------------------------------------------------- //
    //  Scenario 1: microvascular anastomosis (current tissue + vessel field)
    // ---------------------------------------------------------------------- //

    /// <summary>
    /// Microvascular anastomosis on a vascularized tissue pad - the machinery
    /// of catalogue entries #3 (STA-MCA bypass), #21/#22 (replantation) and
    /// #30 (lymphatic-venous anastomosis): a main vessel is severed over a
    /// dense vascular network and closed with interrupted sutures.
    /// </summary>
    public class VesselAnastomosisScenario : SurgicalScenario
    {
        public override string Id => "vessel";
        public override string Title => "显微血管吻合";
        public override string[] SurgeryRefs => new[]
        {
            "#3 颞浅动脉—大脑中动脉搭桥术",
            "#21 指尖再植术 / #22 多指离断再植术",
            "#30 淋巴管—静脉吻合术",
        };
        public override string Description =>
            "血运丰富的组织垫上一根主干血管断裂：入针-潜行-出针-收拢，逐针关闭切口";
        public override int ArmCount => 3;
        public override int DofPerArm => 5;

        public override List<SutureTask> Build(Transform root, List<ArmAgent> agents, SofaUnityBackend sofa)
        {
            // Reuses the original builder: table, SOFA-mirrored tissue, dense
            // vascular network, severed target vessel, 9 stitches in 3 batches.
            return OrganCellBuilder.Build(root, agents, sofa);
        }
    }

    // ---------------------------------------------------------------------- //
    //  Scenario 2: beating-heart coronary bypass
    // ---------------------------------------------------------------------- //

    /// <summary>
    /// Catalogue #34 (off-pump CABG): suturing on a surface that keeps moving.
    /// A cardiac pusher sphere in SOFA pulses the tissue rhythmically; one arm
    /// stabilizes the epicardium while the other closes the coronary arteriotomy.
    /// </summary>
    public class BeatingHeartBypassScenario : SurgicalScenario
    {
        public override string Id => "heart";
        public override string Title => "不停跳冠状动脉搭桥";
        public override string[] SurgeryRefs => new[] { "#34 不停跳冠状动脉搭桥术" };
        public override string Description =>
            "心脏表面持续搏动（SOFA 推压球模拟心跳）：一臂稳定心外膜，一臂在运动血管上缝合";
        public override int ArmCount => 2;
        public override int DofPerArm => 5;
        public override ArmRole RoleFor(int armIndex) =>
            armIndex == 0 ? ArmRole.Suturer : ArmRole.Stabilizer;
        public override Vector3 StabilizePoint => new Vector3(0.16f, 0.585f, 0.90f);

        public override SutureExecutor.Config ExecutorConfig => new SutureExecutor.Config
        {
            coarseRadius = 0.042f, // the target keeps moving under the needle
            fineRadius = 0.020f,
            dwellSeconds = 0.8f,
            arriveSpeed = 0.09f,
        };

        private const float BeatHz = 1.2f;
        private const float BeatAmp = 0.012f;

        public override List<SutureTask> Build(Transform root, List<ArmAgent> agents, SofaUnityBackend sofa)
        {
            var cell = new GameObject("HeartCell").transform;
            cell.SetParent(root, false);

            TissueDeformationSync.LastInstance?.SetColor(new Color(0.62f, 0.18f, 0.22f));
            SurgeryKit.BuildTable(cell, 2.6f, new Color(0.20f, 0.24f, 0.28f));

            var rng = new System.Random(20261024);
            SurgeryKit.VascularNetwork(cell, rng, 4, 0.006f, 0.010f, 0.80f, 0.375f,
                new Color(0.80f, 0.12f, 0.14f), new Color(0.40f, 0.10f, 0.26f),
                corridorZ: 0.90f, corridorHalfWidth: 0.13f);

            // Coronary target vessel: gentle S-curve, severed at the center.
            const float r = 0.018f;
            float topY = SurgeryKit.TissueTopY + r * 0.3f;
            float Z(float x) => 0.90f + 0.08f * Mathf.Sin(x * 3.1f);
            var points = new List<Vector3>();
            for (float x = -0.5f; x <= 0.5001f; x += 0.1f)
                points.Add(new Vector3(x, topY, Z(x)));

            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (points[i].x < -0.012f && points[i + 1].x > 0.012f) continue; // the cut
                if (points[i].x > 0.012f && points[i + 1].x < -0.012f) continue;
                SurgeryKit.Tube(cell, "coronary", points[i], points[i + 1], r,
                                new Color(0.95f, 0.18f, 0.14f));
            }

            GameObject wound = SurgeryKit.CutSite(cell, new Vector3(0f, topY, Z(0f)), r,
                                                  halfGap: 0.012f, poolScale: 0.8f, withPool: true,
                                                  ringColor: new Color(1f, 0.45f, 0.08f));

            // 4 interrupted sutures along the arteriotomy (2 batches x 2).
            var stitches = new List<StitchPoint>();
            for (int k = 0; k < 4; k++)
            {
                float x = (k - 1.5f) * 0.035f;
                float z = Z(x);
                stitches.Add(new StitchPoint
                {
                    entry = new Vector3(x, topY, z - 0.016f),
                    exit = new Vector3(x, topY, z + 0.016f),
                });
            }
            var batches = SurgeryKit.MakeBatches(cell, Id, wound.transform, wound, stitches,
                                                 suturerCount: 1); // one suturer; batches follow it
            foreach (ArmAgent a in agents)
                if (RoleFor(agents.IndexOf(a)) == ArmRole.Suturer)
                    a.AttachNeedle(NeedleTool.Attach(a.Arm.Tcp, a.Arm.Tcp.parent));
            return batches;
        }

        public override void Tick(MultiArmAgentManager manager, SofaUnityBackend sofa)
        {
            if (sofa?.Coupling == null) return;
            // Cardiac pusher: rhythmic pressure on the epicardium next to the wound.
            float beat = 0.5f + 0.5f * Mathf.Sin(Time.time * BeatHz * 2f * Mathf.PI);
            Vector3 pos = new Vector3(-0.06f, 0.565f + BeatAmp * beat, 0.90f);
            sofa.Coupling.PushSphere(NeedleTissueCoupling.CardiacDofName, pos, true);
        }

        public override void Park(SofaUnityBackend sofa) =>
            sofa?.Coupling?.PushSphere(NeedleTissueCoupling.CardiacDofName, Vector3.zero, false);
    }

    // ---------------------------------------------------------------------- //
    //  Scenario 3: flexor tendon repair (end-to-end)
    // ---------------------------------------------------------------------- //

    /// <summary>
    /// Catalogue #25 (zone-II flexor tendon repair): a severed tendon tube on
    /// a tissue bed is rejoined end-to-end with circumferential sutures.
    /// Two 4-DOF arms alternate the bite pattern.
    /// </summary>
    public class TendonRepairScenario : SurgicalScenario
    {
        public override string Id => "tendon";
        public override string Title => "屈肌腱端端修复";
        public override string[] SurgeryRefs => new[] { "#25 屈肌腱二区损伤修复术" };
        public override string Description =>
            "肌腱于组织床上断裂：环绕断端环形缝合，两臂轮流入针，兼顾缝合强度与滑动空间";
        public override int ArmCount => 2;
        public override int DofPerArm => 4;

        public override SutureExecutor.Config ExecutorConfig => new SutureExecutor.Config
        {
            coarseRadius = 0.035f,
            fineRadius = 0.014f,
            dwellSeconds = 1.0f,
        };

        public override List<SutureTask> Build(Transform root, List<ArmAgent> agents, SofaUnityBackend sofa)
        {
            var cell = new GameObject("TendonCell").transform;
            cell.SetParent(root, false);

            TissueDeformationSync.LastInstance?.SetColor(new Color(0.88f, 0.72f, 0.70f));
            SurgeryKit.BuildTable(cell, 2.0f, new Color(0.24f, 0.27f, 0.30f));

            const float r = 0.016f;
            const float y = SurgeryKit.TissueTopY + r * 0.4f;
            var c = new Vector3(0f, y, 0.90f);
            Color tendonColor = new Color(0.92f, 0.90f, 0.80f);

            SurgeryKit.Tube(cell, "tendon_L", new Vector3(-0.30f, y, 0.90f),
                            new Vector3(-0.013f, y, 0.90f), r, tendonColor);
            SurgeryKit.Tube(cell, "tendon_R", new Vector3(0.013f, y, 0.90f),
                            new Vector3(0.30f, y, 0.90f), r, tendonColor);

            GameObject wound = SurgeryKit.CutSite(cell, c, r, halfGap: 0.013f, poolScale: 0f,
                                                  withPool: false, ringColor: new Color(0.3f, 0.8f, 1f));

            // Four circumferential bites: needle crosses the gap between the
            // two stumps at 45/135/225/315 degrees.
            var stitches = new List<StitchPoint>();
            for (int k = 0; k < 4; k++)
            {
                float th = Mathf.Deg2Rad * (45f + 90f * k);
                float yy = y + r * Mathf.Cos(th) * 0.85f;
                float zz = 0.90f + r * Mathf.Sin(th) * 0.85f;
                stitches.Add(new StitchPoint
                {
                    entry = new Vector3(-0.015f, yy, zz),
                    exit = new Vector3(0.015f, yy, zz),
                });
            }

            var batches = SurgeryKit.MakeBatches(cell, Id, wound.transform, wound, stitches,
                                                 suturerCount: 2);
            foreach (ArmAgent a in agents)
                a.AttachNeedle(NeedleTool.Attach(a.Arm.Tcp, a.Arm.Tcp.parent));
            return batches;
        }
    }

    // ---------------------------------------------------------------------- //
    //  Scenario 4: eight-arm replantation (all arms, one table)
    // ---------------------------------------------------------------------- //

    /// <summary>
    /// Catalogue #22 (multi-digit replantation) at full scale: EIGHT 5-DOF
    /// arms arc around the operating table and rebuild a severed vessel with
    /// twelve interrupted sutures handed out one batch per arm - the showcase
    /// for multi-agent API orchestration.
    /// </summary>
    public class OctoReplantScenario : SurgicalScenario
    {
        public override string Id => "octo";
        public override string Title => "八臂多指离断血管重建";
        public override string[] SurgeryRefs => new[] { "#22 多指离断再植术（多组血管重建与时间分配）" };
        public override string Description => "8 台 5-DOF 机械臂围台协作，12 针逐臂轮转完成再植血管吻合";
        public override int ArmCount => 8;
        public override int DofPerArm => 5;
        public override Vector3 StabilizePoint => new Vector3(0.18f, 0.585f, 0.90f);

        public override SutureExecutor.Config ExecutorConfig => new SutureExecutor.Config
        {
            coarseRadius = 0.035f,
            fineRadius = 0.022f,
            dwellSeconds = 1.0f,
        };

        public override List<SutureTask> Build(Transform root, List<ArmAgent> agents, SofaUnityBackend sofa)
        {
            var cell = new GameObject("OctoCell").transform;
            cell.SetParent(root, false);

            TissueDeformationSync.LastInstance?.SetColor(new Color(0.88f, 0.64f, 0.60f));
            SurgeryKit.BuildTable(cell, 2.6f, new Color(0.20f, 0.24f, 0.28f));

            var rng = new System.Random(20261025);
            SurgeryKit.VascularNetwork(cell, rng, 6, 0.007f, 0.011f, 0.80f, 0.375f,
                new Color(0.75f, 0.11f, 0.13f), new Color(0.34f, 0.10f, 0.30f),
                corridorZ: 0.90f, corridorHalfWidth: 0.11f);

            // Severed replantation vessel: r=13mm, gentle curve, cut at center.
            const float r = 0.013f;
            float y = SurgeryKit.TissueTopY + r * 0.3f;
            System.Func<float, float> zc = x => 0.90f + 0.05f * Mathf.Sin(x * 2.6f);
            var pts = new List<Vector3>();
            for (float x = -0.36f; x <= 0.3601f; x += 0.09f)
                pts.Add(new Vector3(x, y, zc(x)));
            const float gap = 0.012f;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                if ((pts[i].x < -gap && pts[i + 1].x > gap) ||
                    (pts[i].x > gap && pts[i + 1].x < -gap)) continue;
                SurgeryKit.Tube(cell, "replant_vessel", pts[i], pts[i + 1], r,
                                new Color(0.95f, 0.16f, 0.12f));
            }

            GameObject wound = SurgeryKit.CutSite(cell, new Vector3(0f, y, zc(0f)), r, gap,
                                                  poolScale: 1f, withPool: true,
                                                  ringColor: new Color(1f, 0.45f, 0.08f));

            // 12 stitches, 8 batches (arms 1-8 in order, 1-2 stitches each).
            var stitches = new List<StitchPoint>();
            for (int k = 0; k < 12; k++)
            {
                float x = (k - 5.5f) * 0.028f;
                float z = zc(x);
                stitches.Add(new StitchPoint
                {
                    entry = new Vector3(x, SurgeryKit.TissueTopY + r - 0.002f, z - 0.017f),
                    exit = new Vector3(x, SurgeryKit.TissueTopY + r - 0.002f, z + 0.017f),
                });
            }

            foreach (ArmAgent a in agents)
                a.AttachNeedle(NeedleTool.Attach(a.Arm.Tcp, a.Arm.Tcp.parent));

            return SurgeryKit.MakeBatches(cell, Id, wound.transform, wound, stitches,
                                  suturerCount: 8);
        }
    }

    // ---------------------------------------------------------------------- //
    //  Scenario 5: small vessel tear - manual API suturing showcase
    // ---------------------------------------------------------------------- //

    /// <summary>
    /// A vessel directly incised (纵向切开) on the vascular pad, closed by four
    /// interrupted sutures with proper two-instrument technique: one 三段式 arm
    /// carries TWEEZERS (counter-traction on the wound edges), the other a
    /// NEEDLE HOLDER with a curved suture needle + thread. Purpose-built for
    /// the manual API workflow - the external agent reads each stitch's
    /// entry/exit from /api/state, drives both instruments with MoveTip and
    /// cinches each bite with the Cinch command.
    /// </summary>
    public class VesselTearScenario : SurgicalScenario
    {
        public override string Id => "tear";
        public override string Title => "血管切开显微缝合";
        public override string[] SurgeryRefs => new[]
        { "#1 复杂颅内动脉瘤显微夹闭术（血管切开与关闭）", "#3 颞浅动脉—大脑中动脉搭桥术（吻合技术）" };
        public override string Description =>
            "血管直接纵向切开：三段式工具臂一台装镊子、一台装持针器（弯针+缝线），API 逐步双器械协作缝合 4 针";
        public override int ArmCount => 2;
        public override int DofPerArm => 4; // 三段 + 器械腕转（腕属法兰组件）
        public override ArmStructure Structure => ArmStructure.ThreeSegmentTool;
        public override ArmTool ToolFor(int armIndex) =>
            armIndex == 0 ? ArmTool.NeedleHolder : ArmTool.Tweezers;
        public override float BaseSpacing => 0.8f;
        public override ArmRole RoleFor(int armIndex) =>
            armIndex == 0 ? ArmRole.Suturer : ArmRole.Stabilizer;
        public override Vector3 StabilizePoint => new Vector3(-0.05f, 0.585f, 0.90f);

        public override SutureExecutor.Config ExecutorConfig => new SutureExecutor.Config
        {
            coarseRadius = 0.030f,
            fineRadius = 0.020f,
            dwellSeconds = 0.8f,
        };

        public override List<SutureTask> Build(Transform root, List<ArmAgent> agents, SofaUnityBackend sofa)
        {
            var cell = new GameObject("TearCell").transform;
            cell.SetParent(root, false);

            TissueDeformationSync.LastInstance?.SetColor(new Color(0.87f, 0.63f, 0.61f));
            SurgeryKit.BuildTable(cell, 2.2f, new Color(0.20f, 0.24f, 0.28f));

            // Sparse vascular bed around the target corridor.
            var rng = new System.Random(20261024);
            SurgeryKit.VascularNetwork(cell, rng, 4, 0.006f, 0.010f, 0.80f, 0.36f,
                new Color(0.74f, 0.10f, 0.12f), new Color(0.33f, 0.10f, 0.29f),
                corridorZ: 0.90f, corridorHalfWidth: 0.10f);

            // The injured vessel: r=11mm, gently curved, CONTINUOUS - the wound
            // is a direct longitudinal incision on top (血管直接切开).
            const float r = 0.011f;
            float y = SurgeryKit.TissueTopY + r * 0.3f;
            System.Func<float, float> zc = x => 0.90f + 0.028f * Mathf.Sin(x * 3.4f);
            var pts = new List<Vector3>();
            for (float x = -0.22f; x <= 0.2201f; x += 0.055f)
                pts.Add(new Vector3(x, y, zc(x)));
            for (int i = 0; i + 1 < pts.Count; i++)
                SurgeryKit.Tube(cell, "incised_vessel", pts[i], pts[i + 1], r,
                                new Color(0.93f, 0.15f, 0.12f));

            GameObject wound = SurgeryKit.IncisionSite(cell, new Vector3(0f, y, zc(0f)), r,
                                                        halfLen: 0.055f, poolScale: 0.8f,
                                                        ringColor: new Color(1f, 0.45f, 0.08f));

            // Four interrupted stitches across the tear (entry near side,
            // exit far side; bite ~1.5x vessel radius).
            const float bite = 0.016f;
            var stitches = new List<StitchPoint>();
            for (int k = 0; k < 4; k++)
            {
                float x = (k - 1.5f) * 0.024f;
                float z = zc(x);
                stitches.Add(new StitchPoint
                {
                    entry = new Vector3(x, SurgeryKit.TissueTopY + r - 0.002f, z - bite),
                    exit = new Vector3(x, SurgeryKit.TissueTopY + r - 0.002f, z + bite),
                });
            }

            // NOTE: the curved needle + thread comes mounted on the needle
            // holder tool (attached by the manager when building the
            // three-segment tool arms) - nothing to attach here.

            return SurgeryKit.MakeBatches(cell, Id, wound.transform, wound, stitches,
                                          suturerCount: 1);
        }
    }

    // ---------------------------------------------------------------------- //
    //  Scenario 6: SOFA-simulated brain (life-size FEM cerebrum)
    // ---------------------------------------------------------------------- //

    /// <summary>
    /// 真实尺寸的 SOFA 仿真大脑：双半球 + 纵裂 + 脑沟回 + 小脑隆起 + 脑干，
    /// 六面体 FEM（E=10kPa, ν=0.49 近不可压缩, 质量 1.4kg，脑干底部固定）。
    /// 血管为脑底的 Willis 环 + 大脑前/中/后动脉皮层支；顶部一条软脑膜
    /// 血管破裂，4 针双器械（镊子+持针器/弯针）缝合。
    /// </summary>
    public class BrainScenario : SurgicalScenario
    {
        private const float CortexY = 0.625f;   // settled cortex top (brain sags ~1cm under gravity)

        public override string Id => "brain";
        public override string Title => "脑组织显微手术（SOFA 仿真大脑）";
        public override string[] SurgeryRefs => new[]
        { "#1 复杂颅内动脉瘤显微夹闭术", "#10 三叉神经微血管减压术（脑底血管解剖）" };
        public override string Description =>
            "真实尺寸 SOFA FEM 大脑（双半球/纵裂/脑沟回/小脑/脑干，E=10kPa ν=0.49）+ Willis 环与皮层动脉，脑膜血管破裂 4 针缝合";
        public override int ArmCount => 2;
        public override int DofPerArm => 4;
        public override ArmStructure Structure => ArmStructure.ThreeSegmentTool;
        public override ArmTool ToolFor(int armIndex) =>
            armIndex == 0 ? ArmTool.NeedleHolder : ArmTool.Tweezers;
        public override float BaseSpacing => 0.8f;
        public override ArmRole RoleFor(int armIndex) =>
            armIndex == 0 ? ArmRole.Suturer : ArmRole.Stabilizer;
        public override Vector3 StabilizePoint => new Vector3(-0.04f, CortexY + 0.012f, 0.88f);
        public override float TissueSurfaceY => CortexY;

        public override SutureExecutor.Config ExecutorConfig => new SutureExecutor.Config
        {
            coarseRadius = 0.030f,
            fineRadius = 0.020f,
            dwellSeconds = 0.8f,
        };

        public override List<SutureTask> Build(Transform root, List<ArmAgent> agents, SofaUnityBackend sofa)
        {
            var cell = new GameObject("BrainCell").transform;
            cell.SetParent(root, false);

            // 手术台 + 铺巾；隐藏肝/组织垫（SOFA 中仍在仿真，仅不显示）。
            SurgeryKit.BuildTable(cell, 2.2f, new Color(0.20f, 0.24f, 0.28f));
            if (TissueDeformationSync.LastInstance != null)
            {
                var padRenderer = TissueDeformationSync.LastInstance.GetComponent<Renderer>();
                if (padRenderer != null) padRenderer.enabled = false;
            }

            // ===== SOFA 仿真大脑（顶点每帧回读，真实 FEM 形变）=====
            BrainDeformationSync.Create(cell, sofa.Coupling, new Color(0.83f, 0.68f, 0.63f));

            // ===== 脑血管：Willis 环 + ACA / MCA / PCA 皮层支 =====
            BuildVasculature(cell);

            // ===== 伤口：顶部软脑膜血管纵向破裂，4 针 =====
            const float r = 0.010f;
            float vy = CortexY + r * 0.6f;
            System.Func<float, float> zc = x => 0.90f + 0.012f * Mathf.Sin(x * 4.0f);
            var pts = new List<Vector3>();
            for (float x = -0.055f; x <= 0.0551f; x += 0.022f)
                pts.Add(new Vector3(x, vy, zc(x)));
            for (int i = 0; i + 1 < pts.Count; i++)
                SurgeryKit.Tube(cell, "pial_vessel", pts[i], pts[i + 1], r,
                                new Color(0.92f, 0.14f, 0.12f));

            GameObject wound = BuildCorticalWound(cell, new Vector3(0f, vy, zc(0f)), r);

            const float bite = 0.016f;
            var stitches = new List<StitchPoint>();
            for (int k = 0; k < 4; k++)
            {
                float x = (k - 1.5f) * 0.024f;
                float z = zc(x);
                stitches.Add(new StitchPoint
                {
                    entry = new Vector3(x, CortexY + r - 0.002f, z - bite),
                    exit = new Vector3(x, CortexY + r - 0.002f, z + bite),
                });
            }

            foreach (ArmAgent a in agents)
                a.AttachNeedle(NeedleTool.Attach(a.Arm.Tcp, a.Arm.Tcp.parent, radius: 0.030f));

            return SurgeryKit.MakeBatches(cell, Id, wound.transform, wound, stitches,
                                          suturerCount: 1);
        }

        /// <summary>近似脑表面顶高（血管贴附用）。</summary>
        private static float SurfaceY(float x, float z)
        {
            float fx = Mathf.Clamp01(Mathf.Abs(x) / 0.080f);
            float fz = Mathf.Clamp01(Mathf.Abs(z - 0.90f) / 0.069f);
            return 0.632f - 0.022f * fx * fx - 0.030f * fz * fz;
        }

        /// <summary>完整脑血管树：Willis 环（ICA/ACA-A1/前交通/PCA-P1/基底动脉尖/
        /// 后交通）+ MCA 外侧裂扇形分支 + ACA-A2/胼缘支 + PCA 皮层支 +
        /// 上矢状窦与皮层桥静脉。</summary>
        private static void BuildVasculature(Transform parent)
        {
            var artery = new Color(0.90f, 0.12f, 0.10f);
            var vein = new Color(0.30f, 0.14f, 0.55f);
            float yb = 0.548f; // 脑底动脉环高度

            // ---- Willis 环主干（围绕下丘脑底面）----
            Vector3 A2L = new Vector3(0f, yb + 0.004f, 0.930f);   // 左ACA起点(前)
            Vector3 A2R = new Vector3(0f, yb + 0.004f, 0.930f);
            Vector3 ICA_L = new Vector3(-0.018f, yb, 0.908f), ICA_R = new Vector3(0.018f, yb, 0.908f);
            Vector3 PCoP_L = new Vector3(-0.020f, yb - 0.002f, 0.884f), PCoP_R = new Vector3(0.020f, yb - 0.002f, 0.884f);
            Vector3 BA_tip = new Vector3(0f, yb - 0.004f, 0.872f); // 基底动脉尖
            // 前交通动脉（连接双侧A1）
            SurgeryKit.Tube(parent, "ACoA", new Vector3(-0.006f, yb + 0.003f, 0.929f),
                            new Vector3(0.006f, yb + 0.003f, 0.929f), 0.0022f, artery);
            for (int s = 0; s < 2; s++)
            {
                float sd = s == 0 ? -1f : 1f;
                // ICA 终末段 -> A1 -> 前交通
                SurgeryKit.Tube(parent, "ica", new Vector3(sd * 0.030f, yb, 0.902f),
                                new Vector3(sd * 0.018f, yb, 0.908f), 0.0038f, artery);
                SurgeryKit.Tube(parent, "aca_A1", new Vector3(sd * 0.018f, yb + 0.002f, 0.908f),
                                new Vector3(sd * 0.006f, yb + 0.003f, 0.929f), 0.0028f, artery);
                // 后交通动脉 ICA -> PCA-P1
                SurgeryKit.Tube(parent, "pcoA", new Vector3(sd * 0.018f, yb, 0.908f),
                                new Vector3(sd * 0.020f, yb - 0.002f, 0.884f), 0.0018f, artery);
                // PCA P1: 基底尖 -> 后交通
                SurgeryKit.Tube(parent, "pca_P1", BA_tip,
                                new Vector3(sd * 0.020f, yb - 0.002f, 0.884f), 0.0024f, artery);
                // 基底动脉（双侧合并成一条下行干）
                SurgeryKit.Tube(parent, "basilar", BA_tip,
                                new Vector3(0f, yb - 0.012f, 0.866f), 0.0034f, artery);

                // ---- MCA: M1 外行 + M2 扇形三支（沿外侧裂表面）----
                Vector3 m1a = new Vector3(sd * 0.030f, yb + 0.002f, 0.902f);
                Vector3 m1b = new Vector3(sd * 0.052f, SurfaceY(sd * 0.052f, 0.896f) - 0.004f, 0.896f);
                SurgeryKit.Tube(parent, "mca_M1", m1a, m1b, 0.0030f, artery);
                for (int b = 0; b < 3; b++)
                {
                    float t = (b - 1) / 2f;
                    Vector3 m2 = new Vector3(sd * 0.052f + (0.018f - 0.010f * Mathf.Abs(t)) * (b == 1 ? 0.4f : 1f),
                                             SurfaceY(sd * 0.052f, 0.896f) - 0.002f + t * 0.008f,
                                             0.896f - 0.014f * t - 0.008f * Mathf.Abs(t));
                    SurgeryKit.Tube(parent, "mca_M2", m1b, m2, 0.0020f, artery);
                    Vector3 m3 = m2 + new Vector3(sd * 0.006f + 0.008f * t, 0.004f, -0.010f * t);
                    SurgeryKit.Tube(parent, "mca_M3", m2, m3, 0.0013f, artery);
                }

                // ---- ACA A2: 沿中线后上行 + 胼缘支 ----
                Vector3 a2a = new Vector3(sd * 0.004f, yb + 0.006f, 0.930f);
                Vector3 a2b = new Vector3(sd * 0.003f, SurfaceY(sd * 0.010f, 0.910f), 0.910f);
                SurgeryKit.Tube(parent, "aca_A2", a2a, a2b, 0.0024f, artery);
                Vector3 a2c = new Vector3(sd * 0.002f, SurfaceY(-0.045f, 0.902f), 0.902f);
                SurgeryKit.Tube(parent, "aca_A2b", a2b, a2c, 0.0020f, artery);
                for (int b = 0; b < 3; b++)
                {
                    float t = (b + 1) / 4f;
                    Vector3 cal = new Vector3(Mathf.Lerp(0.030f, -0.050f, t), a2c.y + 0.004f - t * 0.008f,
                                              0.902f + sd * 0.004f);
                    SurgeryKit.Tube(parent, "callosomarginal",
                                    Vector3.Lerp(a2b, a2c, t), cal, 0.0012f, artery);
                }

                // ---- PCA P2: 绕脑干后外行 + 颞/枕支 ----
                Vector3 p2a = new Vector3(sd * 0.020f, yb, 0.884f);
                Vector3 p2b = new Vector3(sd * 0.040f, SurfaceY(sd * 0.052f, 0.866f) - 0.006f, 0.866f);
                SurgeryKit.Tube(parent, "pca_P2", p2a, p2b, 0.0022f, artery);
                SurgeryKit.Tube(parent, "pca_temporal", p2b,
                                p2b + new Vector3(0.008f, -0.006f, -0.012f), 0.0013f, artery);
                SurgeryKit.Tube(parent, "pca_occipital", p2b,
                                p2b + new Vector3(-0.016f, 0.002f, -0.008f), 0.0014f, artery);
            }

            // ---- 静脉：上矢状窦（中线顶部纵行）+ 皮层桥静脉 ----
            Vector3 sssA = new Vector3(0.058f, SurfaceY(0.058f, 0.900f) + 0.001f, 0.900f);
            Vector3 sssB = new Vector3(-0.058f, SurfaceY(-0.058f, 0.900f) + 0.001f, 0.900f);
            SurgeryKit.Tube(parent, "superior_sagittal", sssA, sssB, 0.0045f, vein);
            for (int b = 0; b < 5; b++)
            {
                float t = (b + 0.5f) / 5f;
                float x = Mathf.Lerp(0.048f, -0.048f, t);
                Vector3 top = new Vector3(0f, SurfaceY(x, 0.900f), 0.900f);
                for (int s = 0; s < 2; s++)
                {
                    float sd = s == 0 ? -1f : 1f;
                    Vector3 cv = new Vector3(x, SurfaceY(x, 0.900f), 0.900f);
                    Vector3 up = new Vector3(x * 0.4f, SurfaceY(x, 0.900f) + 0.001f, 0.900f);
                    SurgeryKit.Tube(parent, "cortical_vein",
                                    new Vector3(x, SurfaceY(x, 0.900f), 0.900f),
                                    up, 0.0011f, vein);
                }
            }
        }

        /// <summary>脑表面伤口视觉：暗红切口缝 + 出血池 + 脉冲环（位置在脑顶）。</summary>
        private static GameObject BuildCorticalWound(Transform parent, Vector3 center, float r)
        {
            var slit = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(slit.GetComponent<Collider>());
            slit.name = "cortical_slit";
            slit.transform.SetParent(parent, false);
            slit.transform.position = center + new Vector3(0f, r * 0.92f, 0f);
            slit.transform.localScale = new Vector3(0.10f, 0.0035f, 0.0065f);
            slit.GetComponent<Renderer>().sharedMaterial =
                SurgeryKit.Mat(new Color(0.26f, 0.01f, 0.02f));

            var pool = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(pool.GetComponent<Collider>());
            pool.name = "BloodPool";
            pool.transform.SetParent(parent, false);
            pool.transform.position = center + new Vector3(0f, 0.0015f, 0f);
            pool.transform.localScale = new Vector3(0.068f, 0.0015f, 0.048f);
            pool.GetComponent<Renderer>().sharedMaterial =
                SurgeryKit.Mat(new Color(0.32f, 0.015f, 0.03f));

            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(ring.GetComponent<Collider>());
            ring.name = "SutureTargetRing";
            ring.transform.SetParent(parent, false);
            ring.transform.position = center + new Vector3(0f, 0.001f, 0f);
            ring.transform.localScale = new Vector3(0.10f, 0.001f, 0.075f);
            ring.GetComponent<Renderer>().sharedMaterial =
                SurgeryKit.Mat(new Color(1f, 0.45f, 0.08f));
            return pool;
        }
    }

    // ---------------------------------------------------------------------- //
    //  Scenario 7: SOFA-simulated liver with tumor
    // ---------------------------------------------------------------------- //

    /// <summary>
    /// 真实尺寸 SOFA 仿真肝脏（右叶大圆凸/左叶楔形/肝门/镰状切迹/胆囊窝）+
    /// **右叶顶部分叶状肿瘤（直径 ~36mm，独立肿瘤组织着色，随肝形变）**。
    /// 肝组织 E=8kPa（弹性成像健康肝区间）、ν=0.40、质量 1.5kg，后部裸区固定。
    /// 肿瘤旁肝被膜切口 4 针（肿瘤活检/切除后创面），双三段式工具臂缝合。
    /// </summary>
    public class LiverScenario : SurgicalScenario
    {
        private const float TopY = 0.632f; // official-liver dome top (transformed)

        public override string Id => "liver";
        public override string Title => "肝脏肿瘤显微手术（SOFA 仿真肝）";
        public override string[] SurgeryRefs => new[]
        { "#56 活体供肝切取术（肝门解剖）", "#57 小儿活体肝移植术（管道重建）" };
        public override string Description =>
            "真实尺寸 SOFA FEM 肝脏（右/左叶、肝门、镰状切迹、胆囊）+ 右叶顶部分叶状肿瘤，肿瘤旁被膜切口 4 针缝合";
        public override int ArmCount => 2;
        public override int DofPerArm => 4;
        public override ArmStructure Structure => ArmStructure.ThreeSegmentTool;
        // Tool/role split by REACHABILITY (measured): the +x base (arm 1)
        // reaches the wound corridor with ~1 mm servo accuracy, so it takes
        // the needle holder; arm 0 holds the tweezers.
        public override ArmTool ToolFor(int armIndex) =>
            armIndex == 1 ? ArmTool.NeedleHolder : ArmTool.Tweezers;
        public override float BaseSpacing => 0.8f;
        public override ArmRole RoleFor(int armIndex) =>
            armIndex == 1 ? ArmRole.Suturer : ArmRole.Stabilizer;
        public override Vector3 StabilizePoint => new Vector3(-0.02f, TopY + 0.006f, 0.812f);
        public override float TissueSurfaceY => TopY;

        public override SutureExecutor.Config ExecutorConfig => new SutureExecutor.Config
        {
            coarseRadius = 0.030f,
            fineRadius = 0.020f,
            dwellSeconds = 0.8f,
        };

        public override List<SutureTask> Build(Transform root, List<ArmAgent> agents, SofaUnityBackend sofa)
        {
            var cell = new GameObject("LiverCell").transform;
            cell.SetParent(root, false);

            SurgeryKit.BuildTable(cell, 2.2f, new Color(0.20f, 0.24f, 0.28f));
            if (TissueDeformationSync.LastInstance != null)
            {
                var padRenderer = TissueDeformationSync.LastInstance.GetComponent<Renderer>();
                if (padRenderer != null) padRenderer.enabled = false;
            }

            // ===== SOFA 仿真肝脏 + 肿瘤（高分辨蒙皮，每帧回读）=====
            LiverDeformationSync.Create(cell, sofa.Coupling, new Color(0.46f, 0.17f, 0.14f));

            // 胆囊（肝前下缘右侧）
            var gb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.Destroy(gb.GetComponent<Collider>());
            gb.name = "Gallbladder";
            gb.transform.SetParent(cell, false);
            gb.transform.position = new Vector3(-0.055f, 0.552f, 0.945f);
            gb.transform.localScale = new Vector3(0.070f, 0.040f, 0.045f);
            var gbMat = new Material(Shader.Find("Standard")) { color = new Color(0.36f, 0.45f, 0.18f) };
            gbMat.SetFloat("_Glossiness", 0.30f);
            gb.GetComponent<Renderer>().sharedMaterial = gbMat;

            // ===== 肝血管：肝门入肝，右/左主支 + 肿瘤供养支 =====
            var artery = new Color(0.90f, 0.12f, 0.10f);
            var vein = new Color(0.30f, 0.14f, 0.55f);
            Vector3 porta = new Vector3(-0.020f, 0.556f, 0.836f);
            // 右主支（向右叶顶）
            SurgeryKit.Tube(cell, "hepatic_R", porta, new Vector3(-0.075f, TopY - 0.006f, 0.878f), 0.0040f, artery);
            // 左主支（向左叶）
            SurgeryKit.Tube(cell, "hepatic_L", porta, new Vector3(0.075f, TopY - 0.014f, 0.885f), 0.0032f, artery);
            // 门静脉右支（偏暗）
            SurgeryKit.Tube(cell, "portal_R", porta, new Vector3(-0.060f, 0.575f, 0.860f), 0.0045f, vein);
            // 肿瘤供养动脉（肿瘤供血支）
            SurgeryKit.Tube(cell, "tumor_feeder", new Vector3(-0.040f, TopY - 0.012f, 0.874f),
                            new Vector3(-0.052f, TopY + 0.006f, 0.878f), 0.0022f, artery);

            // ===== 伤口：肿瘤旁肝被膜纵向切口（左前方），4 针 =====
            const float r = 0.010f;
            float wy = TopY - 0.006f;
            Vector3 wc = new Vector3(0.020f, wy, 0.848f);
            System.Func<float, float> zc = x => wc.z + 0.008f * Mathf.Sin((x - wc.x) * 30f);
            var pts = new List<Vector3>();
            for (float x = wc.x - 0.040f; x <= wc.x + 0.0401f; x += 0.016f)
                pts.Add(new Vector3(x, wy, zc(x)));
            for (int i = 0; i + 1 < pts.Count; i++)
                SurgeryKit.Tube(cell, "capsule_vessel", pts[i], pts[i + 1], r,
                                new Color(0.60f, 0.14f, 0.12f));

            GameObject wound = BuildLiverWound(cell, wc, r);

            const float bite = 0.016f;
            var stitches = new List<StitchPoint>();
            for (int k = 0; k < 4; k++)
            {
                float x = wc.x + (k - 1.5f) * 0.020f;
                float z = zc(x);
                stitches.Add(new StitchPoint
                {
                    entry = new Vector3(x, TopY - 0.008f, z - bite),
                    exit = new Vector3(x, TopY - 0.008f, z + bite),
                });
            }

            foreach (ArmAgent a in agents)
                a.AttachNeedle(NeedleTool.Attach(a.Arm.Tcp, a.Arm.Tcp.parent, radius: 0.030f));

            return SurgeryKit.MakeBatches(cell, Id, wound.transform, wound, stitches,
                                          suturerCount: 1);
        }

        /// <summary>肝被膜伤口视觉：切口缝 + 出血池 + 脉冲环（贴肝表面）。</summary>
        private static GameObject BuildLiverWound(Transform parent, Vector3 center, float r)
        {
            var slit = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(slit.GetComponent<Collider>());
            slit.name = "capsular_slit";
            slit.transform.SetParent(parent, false);
            slit.transform.position = center + new Vector3(0f, r * 0.8f, 0f);
            slit.transform.localScale = new Vector3(0.082f, 0.0035f, 0.0065f);
            slit.GetComponent<Renderer>().sharedMaterial =
                SurgeryKit.Mat(new Color(0.30f, 0.01f, 0.02f));

            var pool = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(pool.GetComponent<Collider>());
            pool.name = "BloodPool";
            pool.transform.SetParent(parent, false);
            pool.transform.position = center + new Vector3(0f, 0.0015f, 0f);
            pool.transform.localScale = new Vector3(0.062f, 0.0015f, 0.044f);
            pool.GetComponent<Renderer>().sharedMaterial =
                SurgeryKit.Mat(new Color(0.32f, 0.015f, 0.03f));

            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(ring.GetComponent<Collider>());
            ring.name = "SutureTargetRing";
            ring.transform.SetParent(parent, false);
            ring.transform.position = center + new Vector3(0f, 0.001f, 0f);
            ring.transform.localScale = new Vector3(0.095f, 0.001f, 0.070f);
            ring.GetComponent<Renderer>().sharedMaterial =
                SurgeryKit.Mat(new Color(1f, 0.45f, 0.08f));
            return pool;
        }
    }


    /// <summary>The selectable scenario catalogue: the three handcrafted
    /// showcases plus every entry of the 100-procedure surgical catalogue
    /// (62 parametrized procedures across all 10 departments). Hotkeys 1..9
    /// address the first nine; PageUp/PageDown walk the whole list.</summary>
    public static class ScenarioRegistry
    {
        public static readonly SurgicalScenario[] All = BuildAll();

        private static SurgicalScenario[] BuildAll()
        {
            var list = new List<SurgicalScenario>
            {
                new VesselAnastomosisScenario(),
                new BeatingHeartBypassScenario(),
                new TendonRepairScenario(),
                new OctoReplantScenario(),
                new VesselTearScenario(),
                new BrainScenario(),
                new LiverScenario(),
            };
            foreach (ProcedureDef d in ProcedureCatalog.All)
                list.Add(new CatalogScenario(d));
            return list.ToArray();
        }

        public static SurgicalScenario Find(string id)
        {
            foreach (var s in All) if (s.Id == id) return s;
            return null;
        }

        public static int IndexOf(string id)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].Id == id) return i;
            return -1;
        }
    }
}
