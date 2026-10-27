using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SofaRobotAI.Surgery
{
    /// <summary>
    /// High-resolution anatomical liver + tumor surface skinned to the SOFA FEM.
    ///
    /// The physics body stays the proven coarse lattice (brain.vtk, 5,280
    /// nodes, stable FEM); the RENDER mesh is the 35k-vertex anatomical
    /// surface from tools/gen_brain_surface.py (two folded hemispheres with
    /// medial walls, Sylvian + central sulci, foliated cerebellum, brainstem).
    /// Every render vertex carries 4 inverse-distance bindings to FEM nodes,
    /// so the fine surface follows the SOFA deformation exactly while the
    /// solver keeps its stable resolution. Sulci are shaded darker via
    /// per-vertex fold depth.
    /// </summary>
    public class LiverDeformationSync : MonoBehaviour
    {
        public static LiverDeformationSync LastInstance { get; private set; }

        /// <summary>Largest FEM-node displacement since start (m).</summary>
        public float MaxAbsDelta { get; private set; }

        private NeedleTissueCoupling _coupling;
        private float[] _sofaBuffer;

        private Vector3[] _femRestUnity;      // warped FEM rest (Unity frame)
        private Vector3[] _femLiveUnity;      // per-frame FEM positions
        private Vector3[] _surfRest;          // render-surface rest (Unity frame)
        private Vector3[] _surfLive;          // skinned render positions
        private Vector3[] _surfDisp;          // weighted FEM displacement
        private int[][] _bindNode;            // per surface vertex: 4 FEM indices
        private float[][] _bindWeight;        // matching weights
        private float[] _foldDepth;           // shading depth
        private int[] _tumorFlag;           // 1 = tumor tissue
        private Mesh _mesh;
        private bool _pullFailed;
        private bool _loggedFirstPull;
        private float _nextBoundsLog;

        public static LiverDeformationSync Create(Transform parent, NeedleTissueCoupling coupling, Color cortex)
        {
            var go = new GameObject("Liver_SOFA");
            go.transform.SetParent(parent, false);
            var sync = go.AddComponent<LiverDeformationSync>();
            sync.Init(coupling, cortex);
            return sync;
        }

        private void Init(NeedleTissueCoupling coupling, Color cortex)
        {
            LastInstance = this;
            _coupling = coupling;

            // --- FEM rest: the OFFICIAL SofaUnity demo liver (181 nodes),
            // transformed onto the table by tools/gen_liver_sofa.py. --------
            if (!ReadVtkPoints("/StreamingAssets/SOFA/liver_sofa.vtk", out Vector3[] femRest))
            {
                Debug.LogWarning("[LiverSync] liver_sofa.vtk parse failed"); Fail(); return;
            }
            _femRestUnity = ToUnity(femRest);
            int femN = _femRestUnity.Length;
            _sofaBuffer = new float[femN * 3];
            _femLiveUnity = new Vector3[femN];

            // --- high-res render surface + bindings -------------------------
            if (!ReadObj("/StreamingAssets/SOFA/liver_sofa_surface.obj", out _surfRest, out int[] tris))
            {
                Debug.LogWarning("[LiverSync] brain_surface.obj parse failed"); Fail(); return;
            }
            if (!ReadBindings("/StreamingAssets/SOFA/liver_sofa_bind.txt", _surfRest.Length,
                              out _bindNode, out _bindWeight, out _foldDepth, out _tumorFlag))
            {
                Debug.LogWarning("[LiverSync] brain_bind.txt parse failed"); Fail(); return;
            }
            _surfLive = new Vector3[_surfRest.Length];
            _surfDisp = new Vector3[_surfRest.Length];

            _mesh = new Mesh { name = "Liver_SOFA_hires" };
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.SetVertices(_surfRest);
            _mesh.SetTriangles(tris, 0);

            // Sulcal shading: fold depth darkens toward the groove colour.
            var colors = new Color32[_surfRest.Length];
            var groove = Color.Lerp(cortex, new Color(0.45f, 0.30f, 0.30f), 0.75f);
            var tumorCol = new Color(0.58f, 0.26f, 0.18f); // 肿瘤组织: 偏亮褐红
            for (int i = 0; i < _surfRest.Length; i++)
                colors[i] = _tumorFlag[i] == 1
                    ? Color.Lerp(tumorCol, groove, _foldDepth[i] * 0.25f)
                    : Color.Lerp(cortex, groove, _foldDepth[i] * 0.55f);
            _mesh.SetColors(colors);

            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            // Standard shader ignores vertex colors; this surface shader
            // multiplies the per-vertex (fold-shaded) color into a Lambert
            // lit albedo so sulci read darker while the gyri stay lit.
            Shader vc = Shader.Find("SofaRobotAI/BrainSurface");
            if (vc == null) vc = Shader.Find("Standard");
            var mat = new Material(vc) { color = Color.white };
            renderer.sharedMaterial = mat;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            Debug.Log($"[LiverSync] hires surface: {_surfRest.Length} verts, {tris.Length / 3} tris, " +
                      $"skinned to {femN} FEM nodes; bounds {_mesh.bounds}");
        }



        private void Fail()
        {
            _pullFailed = true;
            enabled = false;
        }

        private static Vector3[] ToUnity(Vector3[] sofa)
        {
            var dst = new Vector3[sofa.Length];
            for (int i = 0; i < sofa.Length; i++)
                dst[i] = new Vector3(-sofa[i].x, sofa[i].y, sofa[i].z);
            return dst;
        }

        private void LateUpdate()
        {
            if (_pullFailed || _coupling == null) return;
            if (!_coupling.PullLiver(_sofaBuffer)) return;

            // FEM live positions (regular-grid frame) -> Unity frame.
            bool valid = false;
            for (int i = 0; i < _femLiveUnity.Length; i++)
            {
                float x = _sofaBuffer[i * 3], y = _sofaBuffer[i * 3 + 1], z = _sofaBuffer[i * 3 + 2];
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return;
                if (x != 0f || y != 0f || z != 0f) valid = true;
                _femLiveUnity[i] = new Vector3(-x, y, z);
            }
            if (!valid) return;

            // Skin: render vertex follows the weighted displacement of its
            // bound FEM nodes (rigid rest pose + local FEM deformation).
            for (int v = 0; v < _surfLive.Length; v++)
            {
                var n = _bindNode[v];
                var w = _bindWeight[v];
                Vector3 disp = (_femLiveUnity[n[0]] - _femRestUnity[n[0]]) * w[0]
                             + (_femLiveUnity[n[1]] - _femRestUnity[n[1]]) * w[1]
                             + (_femLiveUnity[n[2]] - _femRestUnity[n[2]]) * w[2]
                             + (_femLiveUnity[n[3]] - _femRestUnity[n[3]]) * w[3];
                _surfDisp[v] = disp;
                _surfLive[v] = _surfRest[v] + disp;
            }

            _mesh.SetVertices(_surfLive);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            float maxD = 0f;
            for (int i = 0; i < _femLiveUnity.Length; i++)
            {
                float d = Vector3.Distance(_femLiveUnity[i], _femRestUnity[i]);
                if (d > maxD) maxD = d;
            }
            if (maxD > MaxAbsDelta) MaxAbsDelta = maxD;

            if (!_loggedFirstPull)
            {
                _loggedFirstPull = true;
                Debug.Log($"[LiverSync] first live skin: femMaxDelta={maxD:F4} bounds={_mesh.bounds}");
            }
            if (Time.time > _nextBoundsLog)
            {
                _nextBoundsLog = Time.time + 5f;
                Debug.Log($"[LiverSync] t={Time.time:F0}s femMaxDelta={maxD:F4} bounds={_mesh.bounds}");
            }
        }

        private void OnDestroy()
        {
            if (LastInstance == this) LastInstance = null;
        }

        // ---------------- parsers ---------------- //

        private static bool ReadVtkPoints(string rel, out Vector3[] pts)
        {
            return ReadPoints(Application.dataPath + rel, out pts);
        }

        private static bool ReadPoints(string path, out Vector3[] pts)
        {
            pts = null;
            if (!File.Exists(path)) return false;
            var list = new List<Vector3>();
            bool inPoints = false;
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("POINTS")) { inPoints = true; continue; }
                if (inPoints)
                {
                    if (line.StartsWith("CELLS")) break;
                    var p = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 3 && TryParse(p[0], out float x) &&
                        TryParse(p[1], out float y) && TryParse(p[2], out float z))
                        list.Add(new Vector3(x, y, z));
                }
            }
            pts = list.ToArray();
            return pts.Length > 0;
        }

        private static bool ReadObj(string rel, out Vector3[] verts, out int[] tris)
        {
            verts = null; tris = null;
            string path = Application.dataPath + rel;
            if (!File.Exists(path)) return false;
            var v = new List<Vector3>();
            var t = new List<int>();
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("v "))
                {
                    var p = line.Substring(2).Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3 && TryParse(p[0], out float x) &&
                        TryParse(p[1], out float y) && TryParse(p[2], out float z))
                        v.Add(new Vector3(x, y, z));
                }
                else if (line.StartsWith("f "))
                {
                    var p = line.Substring(2).Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3)
                    {
                        // OBJ indices are 1-based; the generator emits plain
                        // triangles (no slashes).
                        if (int.TryParse(p[0], out int a) && int.TryParse(p[1], out int b) &&
                            int.TryParse(p[2], out int c))
                            // x is negated for the Unity frame (see ToUnity),
                            // which mirrors the mesh - flip the winding back
                            // so faces stay front-facing after backface culling.
                            t.AddRange(new[] { a - 1, c - 1, b - 1 });
                    }
                }
            }
            verts = ToUnity(v.ToArray());
            tris = t.ToArray();
            return verts.Length > 0 && tris.Length > 0;
        }

        private static bool ReadBindings(string rel, int vertCount,
                                         out int[][] nodes, out float[][] weights, out float[] depth, out int[] tumorFlag)
        {
            nodes = null; weights = null; depth = null; tumorFlag = null;
            string path = Application.dataPath + rel;
            if (!File.Exists(path)) return false;
            nodes = new int[vertCount][];
            weights = new float[vertCount][];
            depth = new float[vertCount];
            tumorFlag = new int[vertCount];
            int read = 0;
            foreach (string line in File.ReadLines(path))
            {
                var p = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 11) continue;
                if (!int.TryParse(p[0], out int vi) || vi < 0 || vi >= vertCount) continue;
                var n = new int[4];
                var w = new float[4];
                for (int k = 0; k < 4; k++)
                {
                    if (!int.TryParse(p[1 + k * 2], out n[k]) ||
                        !TryParse(p[2 + k * 2], out w[k])) { n = null; break; }
                }
                if (n == null) continue;
                TryParse(p[9], out float d); int tumor = int.TryParse(p[10], out int t) ? t : 0;
                nodes[vi] = n;
                weights[vi] = w;
                depth[vi] = d;
                tumorFlag[vi] = tumor;
                read++;
            }
            return read == vertCount;
        }

        private static bool TryParse(string s, out float v) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out v);
    }
}
