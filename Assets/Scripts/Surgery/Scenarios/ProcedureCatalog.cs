using System.Collections.Generic;
using SofaRobotAI.Agent;
using SofaRobotAI.Backend;
using UnityEngine;

namespace SofaRobotAI.Surgery.Scenarios
{
    /// <summary>SOFA-implementable tissue motif every catalogue procedure is
    /// reduced to: a severed tube to anastomose, or a lacerated surface to
    /// close (optionally beating). Both reuse the FEM tissue + needle-coupling
    /// + suture machinery.</summary>
    public enum Motif { Tube, Surface }

    /// <summary>Parameter set of one catalogue procedure.</summary>
    public class ProcedureDef
    {
        public string id;              // "s03" = catalogue entry #3
        public string title;           // 中文术式名
        public string refs;            // catalogue entry reference(s)
        public Motif motif;
        public float beatHz = 0f;      // >0: surface pulses (cardiac pusher)
        public float beatAmp = 0.010f;
        public bool stabilizer;        // dedicate one arm to stabilizing tissue
        public int arms = 3;
        public int dof = 5;
        public int stitches = 6;
        public float radius = 0.014f;  // tube radius (Tube motif)
        public float curveAmp = 0.03f; // tube gentle S-curve amplitude
        public int netDensity = 5;     // vascular network root count (0 = none)
        public Color tissueColor;
        public Color tubeColor = new Color(0.72f, 0.12f, 0.14f);
        public string note;            // 手术要点（目录原文摘要）
    }

    /// <summary>
    /// The surgical-procedure catalogue (from the user's 100-entry list),
    /// reduced to SOFA-trainable approximations. Every entry maps onto the
    /// verified machinery: SOFA hexa-FEM tissue pad + live deformation mesh +
    /// needle-tissue force coupling + interrupted-suture executor, with a
    /// beating-surface option driven by the SOFA cardiac pusher sphere.
    /// </summary>
    public static class ProcedureCatalog
    {
        // Category palettes (tissue look).
        private static readonly Color NeuroC = new Color(0.86f, 0.72f, 0.76f);
        private static readonly Color EyeC = new Color(0.92f, 0.94f, 0.96f);
        private static readonly Color HandC = new Color(0.90f, 0.68f, 0.62f);
        private static readonly Color CardiacC = new Color(0.62f, 0.18f, 0.22f);
        private static readonly Color ThoracicC = new Color(0.84f, 0.62f, 0.60f);
        private static readonly Color HpbC = new Color(0.78f, 0.42f, 0.34f);
        private static readonly Color GiC = new Color(0.86f, 0.58f, 0.50f);
        private static readonly Color UroC = new Color(0.82f, 0.64f, 0.60f);
        private static readonly Color OrthoC = new Color(0.88f, 0.72f, 0.70f);
        private static readonly Color EndoC = new Color(0.87f, 0.55f, 0.52f);

        private static ProcedureDef D(string id, string title, string refs, Motif m, Color c,
                                      int arms, int dof, int stitches, string note,
                                      float beatHz = 0f, bool stab = false,
                                      float radius = 0.014f, int net = 5) =>
            new ProcedureDef
            {
                id = id, title = title, refs = refs, motif = m,
                tissueColor = c, arms = arms, dof = dof, stitches = stitches,
                note = note, beatHz = beatHz, stabilizer = stab,
                radius = radius, netDensity = net,
            };

        public static readonly ProcedureDef[] All =
        {
            // ---------------- 神经外科 ----------------
            D("s01","颅内动脉瘤瘤颈夹闭重建","#1 复杂颅内动脉瘤显微夹闭术", Motif.Surface, NeuroC, 3,5,6,"分离瘤颈、保护穿支，夹闭后重建血管壁", net:6),
            D("s02","动静脉畸形供血动脉阻断","#2 颅内动静脉畸形显微切除术", Motif.Tube, NeuroC, 3,5,6,"辨认供血动脉与引流静脉并夹闭供血支", radius:0.012f, net:8),
            D("s03","颞浅动脉—大脑中动脉吻合","#3 颞浅动脉—大脑中动脉搭桥术", Motif.Tube, NeuroC, 3,5,8,"细小血管端侧吻合，通畅且不漏血", radius:0.009f, net:7),
            D("s04","高流量颅内外血管搭桥","#4 高流量颅内外血管搭桥术", Motif.Tube, NeuroC, 3,5,8,"移植血管走行设计与吻合", radius:0.012f),
            D("s05","脑干安全区进入与修补","#5 脑干海绵状血管畸形切除术", Motif.Surface, NeuroC, 2,5,4,"选择安全进入区，减少功能结构损伤", net:3),
            D("s06","面神经保护下肿瘤切除后重建","#6 巨大前庭神经鞘瘤切除术", Motif.Surface, NeuroC, 3,5,6,"切除肿瘤同时保护面神经", net:4),
            D("s07","颅底脑膜瘤分离后硬膜关闭","#7 复杂颅底脑膜瘤切除术", Motif.Surface, NeuroC, 3,5,8,"在颅神经、血管与骨性结构间精细分离", net:5),
            D("s08","内镜经鼻颅底封闭重建","#8 内皮经鼻颅底肿瘤切除及重建术", Motif.Surface, NeuroC, 2,5,6,"狭窄通道内完成可靠的颅底封闭", net:2),
            D("s09","脊髓软膜切口关闭","#9 脊髓髓内肿瘤显微切除术", Motif.Surface, NeuroC, 2,5,5,"识别肿瘤边界，保护传导束", net:3),
            D("s10","三叉神经减压垫隔固定","#10 三叉神经微血管减压术", Motif.Tube, NeuroC, 2,5,4,"分离责任血管并垫隔，避免损伤神经", radius:0.010f, net:4),

            // ---------------- 眼科 ----------------
            D("s14","视网膜裂孔封闭","#14 复杂视网膜脱离玻璃体切除术", Motif.Surface, EyeC, 2,5,5,"处理牵拉与裂孔，促进视网膜复位", net:0),
            D("s15","黄斑前膜剥除后贴附","#15 黄斑前膜／内界膜剥除术", Motif.Surface, EyeC, 2,5,4,"在视网膜表面剥除极薄膜层并控制牵拉", net:0),
            D("s17","开放性眼球伤角巩膜伤口缝合","#17 开放性眼球伤修复术", Motif.Surface, EyeC, 2,5,8,"精确对合伤口，恢复眼球结构", net:0),
            D("s18","小梁切除瓣膜控制","#18 小梁切除术", Motif.Surface, EyeC, 2,5,5,"控制房水引流，平衡眼压", net:0),
            D("s19","人工晶状体巩膜襻固定","#19 人工晶状体巩膜固定术", Motif.Tube, EyeC, 2,5,4,"晶状体居中、倾斜与固定稳定性", radius:0.008f, net:0),
            D("s20","斜视眼外肌后徙调整缝合","#20 复杂斜视再手术", Motif.Tube, EyeC, 2,5,4,"处理瘢痕与肌肉移位，精细调整眼位", radius:0.009f, net:0),

            // ---------------- 手外科与显微重建 ----------------
            D("s21","指尖动脉显微吻合","#21 指尖再植术", Motif.Tube, HandC, 3,5,6,"极细血管吻合与静脉回流处理", radius:0.007f, net:6),
            D("s22","多指离断血管重建","#22 多指离断再植术", Motif.Tube, HandC, 3,5,9,"多组血管重建与时间分配", radius:0.008f, net:7),
            D("s23","断腕桡动脉弓重建","#23 断手／断腕再植术", Motif.Tube, HandC, 3,5,8,"骨性稳定、循环与功能基础重建", radius:0.010f, net:6),
            D("s24","足趾移植血管蒂吻合","#24 足趾移植拇指再造术", Motif.Tube, HandC, 3,5,8,"血管神经吻合与运动功能设计", radius:0.009f, net:6),
            D("s26","臂丛神经束间缝合","#26 臂丛神经探查与重建术", Motif.Tube, HandC, 3,5,6,"辨认神经束、规划功能重建", radius:0.011f, net:4),
            D("s27","周围神经束间移植缝合","#27 周围神经束间缝合／神经移植术", Motif.Tube, HandC, 3,5,6,"神经结构对位与低张力连接", radius:0.010f, net:4),
            D("s28","腓骨瓣血管蒂吻合","#28 游离腓骨瓣下颌骨重建术", Motif.Tube, HandC, 3,5,7,"骨段塑形与显微血管吻合", radius:0.011f, net:5),
            D("s29","DIEP 穿支血管保护吻合","#29 腹壁下动脉穿支皮瓣乳房重建术", Motif.Tube, HandC, 3,5,7,"穿支血管解剖与血供保护", radius:0.009f, net:7),
            D("s30","淋巴管—静脉超显微吻合","#30 淋巴管—静脉吻合术", Motif.Tube, HandC, 3,5,8,"极细、壁薄管道间的超显微吻合", radius:0.006f, net:5),

            // ---------------- 心脏与大血管 ----------------
            D("s31","二尖瓣瓣环成形缝合","#31 复杂二尖瓣修复术", Motif.Surface, CardiacC, 3,5,8,"重建瓣叶、腱索与瓣环几何", beatHz:1.2f, stab:true),
            D("s32","主动脉瓣交界折叠缝合","#32 主动脉瓣修复术", Motif.Surface, CardiacC, 2,5,6,"调整瓣叶有效高度与对合", beatHz:1.3f, stab:true),
            D("s33","主动脉根部移植物吻合","#33 保留主动脉瓣的主动脉根部置换术", Motif.Tube, CardiacC, 3,5,9,"根部重建与自身瓣膜保留", beatHz:1.2f, stab:true, radius:0.016f),
            D("s35","主动脉弓人工血管吻合","#35 主动脉弓置换术", Motif.Tube, CardiacC, 3,5,9,"深部血管重建与出血控制", beatHz:1.1f, radius:0.018f),
            D("s36","胸腹主动脉瘤分支重建","#36 胸腹主动脉瘤开放修复术", Motif.Tube, CardiacC, 3,5,9,"多分支血管重建与脏器血供保护", radius:0.018f, net:7),
            D("s37","婴儿冠状动脉转移吻合","#37 大动脉转位动脉调转术", Motif.Tube, CardiacC, 3,5,8,"细小冠状动脉的转移与吻合", beatHz:1.5f, stab:true, radius:0.007f),
            D("s38","新生儿主动脉弓重建","#38 新生儿主动脉弓重建术", Motif.Tube, CardiacC, 3,5,7,"细小组织精确塑形，避免残余梗阻", beatHz:1.5f, radius:0.009f),
            D("s39","房室间隔缺损补片缝合","#39 房室间隔缺损矫治术", Motif.Surface, CardiacC, 3,5,8,"分隔重建、瓣膜修复与传导保护", beatHz:1.2f, stab:true),
            D("s40","感染灶清除后根部重建","#40 感染性心内膜炎伴主动脉根部脓肿重建术", Motif.Surface, CardiacC, 3,5,8,"清除感染组织后重建破坏结构", beatHz:1.2f, stab:true),

            // ---------------- 胸外科与气道 ----------------
            D("s41","支气管袖式吻合","#41 支气管袖式肺叶切除术", Motif.Tube, ThoracicC, 3,5,8,"断端对合、血供及吻合张力控制", radius:0.015f),
            D("s42","支气管肺动脉双袖吻合","#42 支气管与肺动脉双袖式切除术", Motif.Tube, ThoracicC, 3,5,9,"同时重建气道与肺血管", radius:0.014f, net:6),
            D("s44","隆突切除重建吻合","#44 隆突切除与重建术", Motif.Tube, ThoracicC, 3,5,8,"气道分叉处的复杂吻合", radius:0.016f),
            D("s45","气管滑行成形吻合","#45 长段气管狭窄滑行成形术", Motif.Tube, ThoracicC, 3,5,8,"气管塑形、管腔匹配及张力控制", radius:0.014f),
            D("s46","食管胃吻合重建","#46 微创食管切除及消化道重建术", Motif.Tube, ThoracicC, 3,5,8,"纵隔精细解剖、喉返神经保护", radius:0.013f),
            D("s48","气管食管瘘瘘口修补","#48 胸腔镜下复杂气管食管瘘修补术", Motif.Tube, ThoracicC, 2,5,6,"瘘口分离、双侧修补", radius:0.010f, net:4),
            D("s49","肺移植支气管吻合","#49 单肺／双肺移植术", Motif.Tube, ThoracicC, 3,5,8,"支气管、肺动脉和肺静脉吻合", radius:0.015f, net:6),
            D("s50","胸壁缺损补片重建","#50 复杂胸壁切除与重建术", Motif.Surface, ThoracicC, 3,5,9,"恢复胸壁稳定性与软组织覆盖", net:4),

            // ---------------- 肝胆胰与移植 ----------------
            D("s51","胰肠吻合重建","#51 胰十二指肠切除术", Motif.Tube, HpbC, 3,5,8,"胰、胆、消化道三重重建", radius:0.012f, net:6),
            D("s53","胰管空肠黏膜吻合","#53 胰腺中段切除及胰肠吻合术", Motif.Tube, HpbC, 3,5,6,"脆弱胰管与断端处理", radius:0.008f, net:5),
            D("s54","肝门胆管成形吻合","#54 肝门部胆管癌根治切除及重建术", Motif.Tube, HpbC, 3,5,8,"肝门血管解剖与多支细胆管重建", radius:0.009f, net:7),
            D("s56","供肝切取面血管胆管处理","#56 活体供肝切取术", Motif.Surface, HpbC, 3,5,7,"保障供者安全，保留适宜管道", net:7),
            D("s57","小儿肝移植肝静脉吻合","#57 小儿活体肝移植术", Motif.Tube, HpbC, 3,5,8,"细小血管吻合与尺寸匹配", radius:0.009f, net:6),
            D("s58","劈离式肝脏管道重建","#58 劈离式肝移植术", Motif.Tube, HpbC, 3,5,9,"一枚肝脏分为可移植的移植物", radius:0.010f, net:7),
            D("s60","小肠移植系膜血管吻合","#60 小肠移植术", Motif.Tube, HpbC, 3,5,8,"肠系膜血管吻合与连续性重建", radius:0.011f, net:6),

            // ---------------- 胃肠与盆腔 ----------------
            D("s61","低位直肠吻合","#61 超低位直肠癌保肛切除及吻合术", Motif.Tube, GiC, 3,5,8,"狭窄盆腔内切缘控制与低位吻合", radius:0.014f, net:5),
            D("s64","回肠储袋肛管吻合","#64 回肠储袋—肛管吻合术（IPAA）", Motif.Tube, GiC, 3,5,7,"储袋构建、血供及张力控制", radius:0.013f, net:5),
            D("s66","直肠阴道瘘修补","#66 复发性直肠阴道瘘修补术", Motif.Surface, GiC, 2,5,6,"瘢痕中分离、关闭并重建屏障", net:4),
            D("s67","腹壁疝补片层叠固定","#67 复杂腹壁疝修补与腹壁重建术", Motif.Surface, GiC, 3,5,10,"重建腹壁层次、恢复张力", net:3),

            // ---------------- 泌尿生殖与妇科 ----------------
            D("s72","肾盂成形离断吻合","#72 腹腔镜／机器人肾盂成形术", Motif.Tube, UroC, 3,5,7,"输尿管与肾盂低张力吻合", radius:0.011f, net:6),
            D("s73","肾移植静脉动脉吻合","#73 肾移植术", Motif.Tube, UroC, 3,5,8,"血管吻合与移植肾放置", radius:0.013f, net:6),
            D("s74","尿道狭窄端端吻合","#74 长段尿道狭窄重建术", Motif.Tube, UroC, 2,5,6,"瘢痕处理与管腔重建", radius:0.010f, net:3),
            D("s76","输精管附睾吻合","#76 显微输精管—附睾吻合术", Motif.Tube, UroC, 3,5,6,"极细生殖管道识别与吻合", radius:0.006f, net:4),
            D("s78","新膀胱输入袢吻合","#78 膀胱切除后原位新膀胱重建术", Motif.Tube, UroC, 3,5,9,"储尿结构构建与多吻合口重建", radius:0.012f, net:5),

            // ---------------- 骨科与耳鼻喉 ----------------
            D("s86","肩袖撕裂锚线缝合","#86 肩关节镜下巨大肩袖撕裂修复术", Motif.Surface, OrthoC, 3,5,8,"组织松解、缝合布局与张力平衡", net:3),
            D("s90","声带黏膜显微缝合","#90 喉显微外科声带病变切除术", Motif.Surface, OrthoC, 2,5,4,"微小病灶处理，保护振动组织", net:0),

            // ---------------- 介入与内镜 ----------------
            D("s97","ESD 黏膜缺损关闭","#97 消化道内镜黏膜下剥离术", Motif.Surface, EndoC, 2,5,6,"薄组织层剥离，兼顾完整切除与止血", net:3),
            D("s98","POEM 黏膜隧道关闭","#98 经口内镜肌切开术", Motif.Surface, EndoC, 2,5,6,"黏膜下隧道建立与肌切开层次控制", net:3),
        };
    }

    /// <summary>A surgical scenario instantiated from a <see cref="ProcedureDef"/>:
    /// builds its tissue motif on the shared SOFA FEM tissue and runs the
    /// suture pipeline with per-procedure arm configuration.</summary>
    public class CatalogScenario : SurgicalScenario
    {
        private readonly ProcedureDef _d;
        public CatalogScenario(ProcedureDef d) => _d = d;

        public override string Id => _d.id;
        public override string Title => _d.title;
        public override string[] SurgeryRefs => new[] { _d.refs };
        public override string Description => $"{_d.note}（{_d.motif} 基元" +
            (_d.beatHz > 0 ? $"，搏动 {_d.beatHz:F1}Hz" : "") + "）";
        public override int ArmCount => _d.arms;
        public override int DofPerArm => _d.dof;
        public override Vector3 StabilizePoint => new Vector3(0.16f, 0.585f, 0.90f);

        private int SuturerCount => _d.stabilizer ? 1 : Mathf.Max(1, _d.arms - 0);

        public override ArmRole RoleFor(int armIndex)
        {
            if (armIndex == 0) return ArmRole.Suturer;
            if (_d.stabilizer && armIndex == 1) return ArmRole.Stabilizer;
            return armIndex < SuturerCount ? ArmRole.Suturer : ArmRole.Idle;
        }

        public override SutureExecutor.Config ExecutorConfig => new SutureExecutor.Config
        {
            coarseRadius = _d.beatHz > 0 ? 0.042f : 0.035f,
            fineRadius = _d.beatHz > 0 ? 0.020f : 0.022f,
            dwellSeconds = _d.beatHz > 0 ? 0.8f : 1.2f,
            arriveSpeed = _d.beatHz > 0 ? 0.09f : 0.06f,
        };

        public override List<SutureTask> Build(Transform root, List<ArmAgent> agents, SofaUnityBackend sofa)
        {
            var cell = new GameObject("ProcCell").transform;
            cell.SetParent(root, false);

            TissueDeformationSync.LastInstance?.SetColor(_d.tissueColor);
            SurgeryKit.BuildTable(cell, 2.4f, new Color(0.20f, 0.24f, 0.28f));

            var rng = new System.Random(_d.id.GetHashCode());
            if (_d.netDensity > 0)
                SurgeryKit.VascularNetwork(cell, rng, _d.netDensity, 0.006f, 0.010f, 0.80f, 0.375f,
                    new Color(0.72f, 0.10f, 0.12f), new Color(0.34f, 0.10f, 0.30f),
                    corridorZ: 0.90f, corridorHalfWidth: 0.12f);

            var stitches = new List<StitchPoint>();
            GameObject wound;
            const float topY = SurgeryKit.TissueTopY;

            if (_d.motif == Motif.Tube)
            {
                float y = topY + _d.radius * 0.3f;
                float halfLen = 0.28f + _d.stitches * 0.012f;
                System.Func<float, float> zc = x => 0.90f + _d.curveAmp * Mathf.Sin(x * 3.1f);

                var pts = new List<Vector3>();
                for (float x = -halfLen; x <= halfLen + 0.001f; x += halfLen / 4f)
                    pts.Add(new Vector3(x, y, zc(x)));
                const float gap = 0.012f;
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    if ((pts[i].x < -gap && pts[i + 1].x > gap) ||
                        (pts[i].x > gap && pts[i + 1].x < -gap)) continue; // severed
                    SurgeryKit.Tube(cell, "target", pts[i], pts[i + 1], _d.radius, _d.tubeColor);
                }

                wound = SurgeryKit.CutSite(cell, new Vector3(0f, y, zc(0f)), _d.radius, gap,
                                           poolScale: 1f, withPool: true,
                                           ringColor: new Color(1f, 0.45f, 0.08f));

                for (int k = 0; k < _d.stitches; k++)
                {
                    float x = (k - (_d.stitches - 1) * 0.5f) * 0.030f;
                    float z = zc(x);
                    float bite = Mathf.Max(0.015f, _d.radius + 0.004f);
                    stitches.Add(new StitchPoint
                    {
                        entry = new Vector3(x, topY + _d.radius - 0.002f, z - bite),
                        exit = new Vector3(x, topY + _d.radius - 0.002f, z + bite),
                    });
                }
            }
            else // Surface laceration
            {
                // Wound lips: two dark strips either side of the cut line.
                float len = _d.stitches * 0.030f + 0.02f;
                for (int s = 0; s < 2; s++)
                {
                    var lip = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityEngine.Object.Destroy(lip.GetComponent<Collider>());
                    lip.name = s == 0 ? "lipL" : "lipR";
                    lip.transform.SetParent(cell, false);
                    lip.transform.position = new Vector3(0f, topY + 0.001f,
                                                         0.90f + (s == 0 ? -0.012f : 0.012f));
                    lip.transform.localScale = new Vector3(len, 0.0016f, 0.006f);
                    lip.GetComponent<Renderer>().sharedMaterial =
                        SurgeryKit.Mat(new Color(0.55f, 0.12f, 0.14f));
                }

                wound = SurgeryKit.CutSite(cell, new Vector3(0f, topY + 0.0015f, 0.90f),
                                           0.010f, halfGap: 0.011f, poolScale: 0.9f,
                                           withPool: true, ringColor: new Color(1f, 0.45f, 0.08f));

                for (int k = 0; k < _d.stitches; k++)
                {
                    float x = (k - (_d.stitches - 1) * 0.5f) * 0.030f;
                    stitches.Add(new StitchPoint
                    {
                        entry = new Vector3(x, topY + 0.001f, 0.90f - 0.016f),
                        exit = new Vector3(x, topY + 0.001f, 0.90f + 0.016f),
                    });
                }
            }

            foreach (ArmAgent a in agents)
                a.AttachNeedle(NeedleTool.Attach(a.Arm.Tcp, a.Arm.Tcp.parent));

            return SurgeryKit.MakeBatches(cell, Id, wound.transform, wound, stitches, SuturerCount);
        }

        public override void Tick(MultiArmAgentManager manager, SofaUnityBackend sofa)
        {
            if (_d.beatHz <= 0f || sofa?.Coupling == null) return;
            float beat = 0.5f + 0.5f * Mathf.Sin(Time.time * _d.beatHz * 2f * Mathf.PI);
            Vector3 pos = new Vector3(-0.06f, 0.565f + _d.beatAmp * beat, 0.90f);
            sofa.Coupling.PushSphere(NeedleTissueCoupling.CardiacDofName, pos, true);
        }

        public override void Park(SofaUnityBackend sofa)
        {
            if (_d.beatHz <= 0f) return;
            sofa?.Coupling?.PushSphere(NeedleTissueCoupling.CardiacDofName, Vector3.zero, false);
        }
    }
}
