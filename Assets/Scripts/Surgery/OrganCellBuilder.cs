using System.Collections.Generic;
using SofaRobotAI.Agent;
using SofaRobotAI.Core;
using UnityEngine;

namespace SofaRobotAI.Surgery
{
    /// <summary>
    /// Builds the surgical cell on the Unity side:
    ///  - an operating table with ONE large tissue pad,
    ///  - a dense procedural vascular network (arteries + veins, branching
    ///    tree) covering the tissue surface,
    ///  - one TARGET vessel crossing the tissue center that is SEVERED:
    ///    cut gap, dark blood pool, pulsing marker ring and an arrow,
    ///  - stitch points along both sides of the cut, split into sequential
    ///    batches (one batch per arm; only one arm sutures at a time so the
    ///    arms never fight over the same wound), needle tools on every arm.
    ///
    /// The tissue pad and the target vessel are ALSO deformable FEM bodies in
    /// SOFA (see robot_cell.scn: TissuePad + TargetVessel) when the SofaUnity
    /// backend is active. The dense vascular network is a Unity visual layer
    /// parented to the tissue at its rest pose. Geometry mirrors the .scn -
    /// keep both files in sync.
    /// </summary>
    public static class OrganCellBuilder
    {
        // Tissue pad ------------------------------------------------------------------
        private static readonly Vector3 TissueCenter = new Vector3(0f, 0.46f, 0.90f);
        private static readonly Vector3 TissueHalf = new Vector3(0.80f, 0.07f, 0.375f);
        private const float TissueTopY = 0.53f;

        // Target vessel ---------------------------------------------------------------
        private const float TargetZ = 0.90f;
        private const float TargetHalfLen = 0.55f;
        private const float TargetRadius = 0.014f;
        private const float CutHalfGap = 0.011f;

        // Stitches: 9 sutures split into 3 batches (one per arm) -----------------------
        private const int Batches = 3;
        private const int StitchesPerBatch = 3;
        private const float StitchSpacing = 0.032f;
        private const float BiteDepth = 0.017f; // needle passes this far beside the vessel

        private static readonly Color TissueColor = new Color(0.86f, 0.60f, 0.58f);
        private static readonly Color ArteryColor = new Color(0.72f, 0.10f, 0.12f);
        private static readonly Color VeinColor = new Color(0.34f, 0.10f, 0.30f);
        private static readonly Color TargetColor = new Color(0.95f, 0.16f, 0.12f);

        private struct VesselSeg
        {
            public Vector3 a, b;
            public float radius;
            public bool artery, isTarget;
        }

        /// <summary>Grid resolution of the SOFA tissue body (robot_cell.scn
        /// TissueHexaTop n="20 4 12").</summary>
        public const int TissueNx = 20, TissueNy = 4, TissueNz = 12;

        /// <summary>Builds everything; returns the sequential suture batches.
        /// When <paramref name="sofa"/> is live, the tissue visual mirrors the
        /// SOFA FEM deformation instead of being a static primitive.</summary>
        public static List<SutureTask> Build(Transform parent, List<ArmAgent> agents,
                                             Backend.SofaUnityBackend sofa)
        {
            var root = new GameObject("SurgeryCell").transform;
            root.SetParent(parent, false);
            var rng = new System.Random(20261023); // fixed seed: reproducible anatomy

            BuildTable(root);

            // The live SOFA tissue mirror is created once by the manager and
            // survives scenario switches; only build the static fallback here.
            if (sofa?.Coupling == null)
                BuildTissuePad(root);
            bool liveTissue = sofa?.Coupling != null;

            var segments = new List<VesselSeg>();
            GenerateTargetVessel(segments);
            GenerateVascularNetwork(segments, rng);
            RenderVessels(root, segments);

            GameObject pool = BuildCutSite(root);

            var tasks = BuildStitchBatches(root, pool);

            foreach (ArmAgent agent in agents)
                agent.AttachNeedle(NeedleTool.Attach(agent.Arm.Tcp, agent.Arm.Tcp.parent));

            SutureVisuals.Reset(StitchesPerBatch * Batches);
            Debug.Log($"[OrganCellBuilder] tissue pad{(liveTissue ? " (SOFA live mesh)" : "")} + " +
                      $"{segments.Count} vessel segments; target vessel severed at center; " +
                      $"{tasks.Count} suture batches of {StitchesPerBatch} stitches");
            return tasks;
        }

        // ------------------------------------------------------------------ //
        //  Table & tissue
        // ------------------------------------------------------------------ //

        private static void BuildTable(Transform parent)
        {
            var table = GameObject.CreatePrimitive(PrimitiveType.Cube);
            table.name = "OperatingTable";
            table.transform.SetParent(parent, false);
            table.transform.localPosition = new Vector3(0f, TissueCenter.y - TissueHalf.y - 0.04f, TissueCenter.z);
            table.transform.localScale = new Vector3(2.6f, 0.08f, 0.7f);
            table.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.20f, 0.24f, 0.28f));

            for (int i = 0; i < 4; i++)
            {
                var leg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.Destroy(leg.GetComponent<Collider>());
                leg.name = $"tableLeg_{i}";
                leg.transform.SetParent(parent, false);
                float lx = (i % 2 == 0 ? -1 : 1) * 1.18f;
                float lz = TissueCenter.z + (i < 2 ? -0.26f : 0.26f);
                float legH = (TissueCenter.y - TissueHalf.y - 0.08f) * 0.5f;
                leg.transform.localPosition = new Vector3(lx, legH, lz);
                leg.transform.localScale = new Vector3(0.05f, legH, 0.05f);
                leg.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.55f, 0.58f, 0.62f));
            }
        }

        private static GameObject BuildTissuePad(Transform parent)
        {
            var tissue = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tissue.name = "TissuePad";
            Object.Destroy(tissue.GetComponent<Collider>()); // SOFA-side body only
            tissue.transform.SetParent(parent, false);
            tissue.transform.position = TissueCenter;
            tissue.transform.localScale = TissueHalf * 2f;
            var mat = new Material(Shader.Find("Standard")) { color = TissueColor };
            mat.SetFloat("_Glossiness", 0.28f);
            tissue.GetComponent<Renderer>().sharedMaterial = mat;
            return tissue;
        }

        // ------------------------------------------------------------------ //
        //  Vascular network
        // ------------------------------------------------------------------ //

        /// <summary>Target vessel: straight main trunk across the tissue center,
        /// split into two halves by the severance gap.</summary>
        private static void GenerateTargetVessel(List<VesselSeg> segments)
        {
            float y = TissueTopY + TargetRadius * 0.3f;
            // left half: -HalfLen .. -CutHalfGap ; right half: +CutHalfGap .. +HalfLen
            segments.Add(MakeSeg(new Vector3(-TargetHalfLen, y, TargetZ),
                                 new Vector3(-CutHalfGap, y, TargetZ), TargetRadius, true, true));
            segments.Add(MakeSeg(new Vector3(CutHalfGap, y, TargetZ),
                                 new Vector3(TargetHalfLen, y, TargetZ), TargetRadius, true, true));
        }

        /// <summary>Recursive branching tree: ~5 roots wander across the tissue
        /// splitting into thinner children, skipping the target corridor.</summary>
        private static void GenerateVascularNetwork(List<VesselSeg> segments, System.Random rng)
        {
            for (int root = 0; root < 5; root++)
            {
                bool fromLeft = root % 2 == 0;
                float sx = fromLeft ? -TissueHalf.x * 0.92f : TissueHalf.x * 0.92f;
                float sz = Lerp(rng, 0.58f, 1.24f);
                var dir = new Vector3(fromLeft ? 1f : -1f, 0f, Lerp(rng, -0.5f, 0.5f)).normalized;
                Grow(segments, rng, new Vector3(sx, 0f, sz), dir, Lerp(rng, 0.008f, 0.012f),
                     depth: 0, maxDepth: 3, artery: root % 3 != 1);
            }
        }

        private static void Grow(List<VesselSeg> segments, System.Random rng,
                                 Vector3 pos, Vector3 dir, float radius, int depth, int maxDepth, bool artery)
        {
            const float len = 0.14f;
            var cur = pos;
            var curDir = dir;
            for (int step = 0; step < 3; step++)
            {
                curDir = (curDir + new Vector3(Lerp(rng, -0.45f, 0.45f), 0f, Lerp(rng, -0.45f, 0.45f))).normalized;
                Vector3 next = cur + curDir * len;

                // Keep on the tissue (elliptic bounds); reflect inward when leaving.
                next.x = Mathf.Clamp(next.x, -TissueHalf.x * 0.95f, TissueHalf.x * 0.95f);
                next.z = Mathf.Clamp(next.z, TissueCenter.z - TissueHalf.z * 0.9f,
                                             TissueCenter.z + TissueHalf.z * 0.9f);

                Vector3 mid = (cur + next) * 0.5f;
                if (!InTargetCorridor(mid))
                    segments.Add(MakeSeg(cur, next, radius, artery, false));

                cur = next;
            }

            if (depth >= maxDepth || radius < 0.0042f) return;

            // Two children with fanned directions and thinner walls.
            for (int c = 0; c < 2; c++)
            {
                float fan = (c == 0 ? 1f : -1f) * Lerp(rng, 0.45f, 0.9f);
                var child = Quaternion.Euler(0f, fan * Mathf.Rad2Deg, 0f) * curDir;
                Grow(segments, rng, cur, child, radius * 0.72f, depth + 1, maxDepth, artery);
            }
        }

        private static bool InTargetCorridor(Vector3 p) =>
            Mathf.Abs(p.x) < 0.34f && Mathf.Abs(p.z - TargetZ) < 0.09f;

        private static VesselSeg MakeSeg(Vector3 a, Vector3 b, float r, bool artery, bool isTarget) =>
            new VesselSeg { a = a, b = b, radius = r, artery = artery, isTarget = isTarget };

        private static void RenderVessels(Transform parent, List<VesselSeg> segments)
        {
            Material arteryMat = TubeMat(ArteryColor), veinMat = TubeMat(VeinColor),
                     targetMat = TubeMat(TargetColor);
            foreach (VesselSeg s in segments)
            {
                var tube = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.Destroy(tube.GetComponent<Collider>());
                tube.name = s.isTarget ? "TargetVessel_seg" : (s.artery ? "artery" : "vein");
                tube.transform.SetParent(parent, false);
                Vector3 a = s.a, b = s.b;
                a.y = TissueTopY + s.radius * 0.25f;
                b.y = TissueTopY + s.radius * 0.25f;
                Vector3 dir = b - a;
                tube.transform.position = (a + b) * 0.5f;
                tube.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
                tube.transform.localScale = new Vector3(s.radius * 2f, dir.magnitude * 0.5f, s.radius * 2f);
                tube.GetComponent<Renderer>().sharedMaterial =
                    s.isTarget ? targetMat : (s.artery ? arteryMat : veinMat);
            }
        }

        // ------------------------------------------------------------------ //
        //  Severance (cut site) visuals
        // ------------------------------------------------------------------ //

        /// <returns>The blood pool object (used as the incision visual that
        /// shrinks as stitches close the wound).</returns>
        private static GameObject BuildCutSite(Transform parent)
        {
            float y = TissueTopY + TargetRadius * 0.3f;

            // Dark cut ends of the two vessel halves.
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                var cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.Destroy(cap.GetComponent<Collider>());
                cap.name = s == 0 ? "cutEnd_L" : "cutEnd_R";
                cap.transform.SetParent(parent, false);
                cap.transform.position = new Vector3(side * CutHalfGap, y, TargetZ);
                cap.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
                cap.transform.localScale = new Vector3(TargetRadius * 2.15f, 0.003f, TargetRadius * 2.15f);
                cap.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.28f, 0.01f, 0.02f));
            }

            // Blood pool under the cut.
            var pool = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(pool.GetComponent<Collider>());
            pool.name = "BloodPool";
            pool.transform.SetParent(parent, false);
            pool.transform.position = new Vector3(0f, TissueTopY + 0.0015f, TargetZ);
            pool.transform.localScale = new Vector3(0.10f, 0.0015f, 0.075f);
            pool.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.32f, 0.015f, 0.03f));

            // Pulsing warning ring around the cut.
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(ring.GetComponent<Collider>());
            ring.name = "SutureTargetRing";
            ring.transform.SetParent(parent, false);
            ring.transform.position = new Vector3(0f, TissueTopY + 0.001f, TargetZ);
            ring.transform.localScale = new Vector3(0.13f, 0.001f, 0.10f);
            ring.GetComponent<Renderer>().sharedMaterial = Mat(new Color(1f, 0.45f, 0.08f));
            ring.AddComponent<PulsingMarker>();

            // Floating arrow pointing at the wound.
            var arrow = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(arrow.GetComponent<Collider>());
            arrow.name = "SutureTargetArrow";
            arrow.transform.SetParent(parent, false);
            arrow.transform.position = new Vector3(0f, TissueTopY + 0.30f, TargetZ);
            arrow.transform.localScale = new Vector3(0.012f, 0.05f, 0.012f);
            arrow.GetComponent<Renderer>().sharedMaterial = Mat(new Color(1f, 0.8f, 0.1f));

            return pool;
        }

        // ------------------------------------------------------------------ //
        //  Stitches
        // ------------------------------------------------------------------ //

        private static List<SutureTask> BuildStitchBatches(Transform parent, GameObject pool)
        {
            var tasks = new List<SutureTask>();
            int total = Batches * StitchesPerBatch;
            float topY = TissueTopY + TargetRadius - 0.002f;

            for (int b = 0; b < Batches; b++)
            {
                var task = new SutureTask
                {
                    organName = $"TargetVessel_batch{b + 1}",
                    vessel = pool.transform,
                    incisionVisual = pool,
                    incisionBaseScale = pool.transform.localScale,
                };
                for (int k = 0; k < StitchesPerBatch; k++)
                {
                    int i = b * StitchesPerBatch + k;
                    float x = (i - (total - 1) * 0.5f) * StitchSpacing;
                    var stitch = new StitchPoint
                    {
                        entry = new Vector3(x, topY, TargetZ - BiteDepth),
                        exit = new Vector3(x, topY, TargetZ + BiteDepth),
                    };
                    task.stitches.Add(stitch);

                    var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Object.Destroy(marker.GetComponent<Collider>());
                    marker.name = $"stitchTarget_{i + 1}";
                    marker.transform.SetParent(parent, false);
                    marker.transform.position = stitch.entry;
                    marker.transform.localScale = Vector3.one * 0.006f;
                    marker.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.2f, 0.9f, 0.9f, 0.8f));
                }
                tasks.Add(task);
            }
            return tasks;
        }

        // ------------------------------------------------------------------ //

        private static float Lerp(System.Random rng, float a, float b) =>
            Mathf.Lerp(a, b, (float)rng.NextDouble());

        private static Material Mat(Color c) => new Material(Shader.Find("Standard")) { color = c };

        private static Material TubeMat(Color c)
        {
            var m = new Material(Shader.Find("Standard")) { color = c };
            m.SetFloat("_Glossiness", 0.55f);
            return m;
        }

        /// <summary>Slow scale pulse so the suture target is obvious at a glance.</summary>
        private class PulsingMarker : MonoBehaviour
        {
            private Vector3 _base;
            private void Start() => _base = transform.localScale;
            private void Update() =>
                transform.localScale = _base * (1f + 0.14f * Mathf.Sin(Time.time * 4f));
        }
    }
}
