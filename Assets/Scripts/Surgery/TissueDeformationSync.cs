using UnityEngine;

namespace SofaRobotAI.Surgery
{
    /// <summary>
    /// Builds the Unity tissue visual FROM the SOFA FEM grid and mirrors the
    /// native deformation every frame: the needle-tissue coupling becomes
    /// visible (the tissue dents where the needle presses, sags under
    /// gravity, recovers when the needle leaves).
    ///
    /// The mesh uses all grid vertices; only the boundary quads are
    /// triangulated. The mapping from SOFA's linear vertex index to grid
    /// (i,j,k) is detected from the rest positions, so it tolerates axis
    /// ordering differences. Triangle winding is oriented outward using the
    /// rest pose (cross product vs tissue center).
    /// </summary>
    public class TissueDeformationSync : MonoBehaviour
    {
        public static TissueDeformationSync LastInstance { get; private set; }

        /// <summary>肝样式总开关：SOFA 长方体网格经平滑形变呈现为一块肝
        /// （前后缘圆化+右叶楔形+中央隆起），材质换为肝红棕色，并附胆囊
        /// 与肝门细节。中央手术走廊恒等，耦合与场景几何不受影响。</summary>
        public static bool UseLiverStyle = true;
        private static readonly Color LiverBase = new Color(0.46f, 0.17f, 0.14f);

        /// <summary>Largest single-vertex displacement (m) seen since start -
        /// proves the readback pipeline is live (gravity sag alone trips it).</summary>
        public float MaxAbsDelta { get; private set; }

        /// <summary>Current world-space mesh bounds (diagnostics).</summary>
        public Bounds CurrentBounds => _mesh != null ? _mesh.bounds : default;

        /// <summary>Bounds of the first readback (diagnostics).</summary>
        public Bounds RestBounds { get; private set; }

        private NeedleTissueCoupling _coupling;
        private int _nx, _ny, _nz;
        private float[] _sofaBuffer;
        private Vector3[] _unityVerts;
        private Vector3[] _rest; // unity coords
        private Mesh _mesh;
        private bool _pullFailed;
        private Material _material;

        /// <summary>Restyles the live tissue (scenario switching).</summary>
        public void SetColor(Color c)
        {
            if (_material != null)
                _material.color = UseLiverStyle ? Color.Lerp(LiverBase, c, 0.18f) : c;
        }

        public static TissueDeformationSync Create(Transform parent, NeedleTissueCoupling coupling,
                                                   int nx, int ny, int nz, Color color)
        {
            var go = new GameObject("TissuePad_SOFA");
            go.transform.SetParent(parent, false);
            var sync = go.AddComponent<TissueDeformationSync>();
            sync.Init(coupling, nx, ny, nz, color);
            return sync;
        }

        private void Init(NeedleTissueCoupling coupling, int nx, int ny, int nz, Color color)
        {
            LastInstance = this;
            _coupling = coupling;
            _nx = nx; _ny = ny; _nz = nz;

            int count = nx * ny * nz;
            _sofaBuffer = new float[count * 3];
            _unityVerts = new Vector3[count];

            // The rest baseline is captured lazily: the very first readbacks
            // may still be zero before the native scene takes its first step.
            _rest = null;

            _mesh = new Mesh { name = "Tissue_SOFA_grid" };
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            _material = new Material(Shader.Find("Standard"))
            {
                color = UseLiverStyle ? Color.Lerp(LiverBase, color, 0.18f) : color,
            };
            _material.SetFloat("_Glossiness", 0.12f); // 柔和组织质感，干净直观
            renderer.sharedMaterial = _material;

            if (UseLiverStyle) BuildLiverDetails();

            if (!coupling.PullTissue(_sofaBuffer))
            {
                _pullFailed = true;
                enabled = false;
            }
            // Surface topology is built on the first valid (non-zero) readback
            // because its winding orientation needs the real rest positions.
        }

        private void LateUpdate()
        {
            if (_pullFailed || _coupling == null) return;
            if (!_coupling.PullTissue(_sofaBuffer)) return;

            // Validity gate: skip frames that are still zeroed or carry NaNs
            // (an unstable native solve produces NaNs before settling).
            bool valid = false;
            for (int i = 0; i < _sofaBuffer.Length; i++)
            {
                float f = _sofaBuffer[i];
                if (float.IsNaN(f) || float.IsInfinity(f)) return;
                if (f != 0f) valid = true;
            }
            if (!valid) return; // scene not stepped yet

            ConvertToUnity(_sofaBuffer, _unityVerts);

            if (_rest == null)
            {
                _rest = (Vector3[])_unityVerts.Clone();
                var rb = new Bounds(_rest[0], Vector3.zero);
                foreach (Vector3 v in _rest) rb.Encapsulate(v);
                RestBounds = rb;
                Debug.Log($"[TissueDeformationSync] rest captured: " +
                          $"x[{rb.min.x:F2}..{rb.max.x:F2}] " +
                          $"y[{rb.min.y:F2}..{rb.max.y:F2}] " +
                          $"z[{rb.min.z:F2}..{rb.max.z:F2}] | " +
                          $"v0=({_sofaBuffer[0]:F2},{_sofaBuffer[1]:F2},{_sofaBuffer[2]:F2}) " +
                          $"v1=({_sofaBuffer[3]:F2},{_sofaBuffer[4]:F2},{_sofaBuffer[5]:F2}) " +
                          $"v959=({_sofaBuffer[2874]:F2},{_sofaBuffer[2875]:F2},{_sofaBuffer[2876]:F2})");
                BuildSurfaceTopology();
                ApplyToMesh();
                return;
            }

            ApplyToMesh();

            for (int i = 0; i < _unityVerts.Length; i++)
            {
                float d = Vector3.Distance(_unityVerts[i], _rest[i]);
                if (d > MaxAbsDelta) MaxAbsDelta = d;
            }

            // Periodic worst-offender dump: how many verts strayed and which.
            if (Time.time - _lastDump > 3f)
            {
                _lastDump = Time.time;
                int beyond = 0, worstIdx = -1;
                float worst = 0f;
                for (int i = 0; i < _unityVerts.Length; i++)
                {
                    float d = Vector3.Distance(_unityVerts[i], _rest[i]);
                    if (d > 0.05f) beyond++;
                    if (d > worst) { worst = d; worstIdx = i; }
                }
                if (worstIdx >= 0 && worst > 0.05f)
                {
                    Vector3 w = _unityVerts[worstIdx], r = _rest[worstIdx];
                    Debug.Log($"[TissueDeformationSync] t={Time.time:F0}s beyond5cm={beyond}/{_unityVerts.Length} " +
                              $"worst v{worstIdx} rest({r.x:F2},{r.y:F2},{r.z:F2}) now({w.x:F2},{w.y:F2},{w.z:F2})");
                }
            }
        }

        private float _lastDump;

        // ------------------------------------------------------------------ //

        private void ConvertToUnity(float[] sofa, Vector3[] dst)
        {
            for (int i = 0; i < dst.Length; i++)
                dst[i] = new Vector3(-sofa[i * 3], sofa[i * 3 + 1], sofa[i * 3 + 2]);
            if (UseLiverStyle)
                for (int i = 0; i < dst.Length; i++)
                    dst[i] = Surgery.Scenarios.SurgeryKit.LiverWarp(dst[i]);
        }

        /// <summary>肝的解剖细节：胆囊（前缘右侧橄榄绿椭球）与肝门暗带
        /// （前缘左侧，肝血管入肝处）。静态装饰，不参与 FEM。</summary>
        private void BuildLiverDetails()
        {
            var gb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(gb.GetComponent<Collider>());
            gb.name = "Gallbladder";
            gb.transform.SetParent(transform, false);
            gb.transform.position = new Vector3(0.34f, 0.547f, 0.615f);
            gb.transform.localScale = new Vector3(0.085f, 0.050f, 0.060f);
            var gbMat = new Material(Shader.Find("Standard")) { color = new Color(0.36f, 0.45f, 0.18f) };
            gbMat.SetFloat("_Glossiness", 0.30f);
            gb.GetComponent<Renderer>().sharedMaterial = gbMat;

            var porta = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(porta.GetComponent<Collider>());
            porta.name = "PortaHepatis";
            porta.transform.SetParent(transform, false);
            porta.transform.position = new Vector3(-0.40f, 0.536f, 0.565f);
            porta.transform.localScale = new Vector3(0.34f, 0.006f, 0.030f);
            porta.GetComponent<Renderer>().sharedMaterial =
                new Material(Shader.Find("Standard")) { color = new Color(0.30f, 0.11f, 0.10f) };
        }

        private void ApplyToMesh()
        {
            _mesh.vertices = _unityVerts;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        /// <summary>Boundary quads of the (i,j,k) grid as triangles. Axis
        /// mapping is detected from the rest buffer; winding oriented outward.</summary>
        private void BuildSurfaceTopology()
        {
            // SOFA grid ordering: index = i + j*nx + k*nx*ny with i the fastest
            // axis. Detect which world axis each of i/j/k steps along.
            int aStep = 1, bStep = _nx, cStep = _nx * _ny;
            Axis axisI = DetectAxis(0, aStep), axisJ = DetectAxis(0, bStep), axisK = DetectAxis(0, cStep);

            var tris = new System.Collections.Generic.List<int>();
            Vector3 center = ComputeCenter();

            for (int k = 0; k < _nz; k++)
            for (int j = 0; j < _ny; j++)
            for (int i = 0; i < _nx; i++)
            {
                bool bi = i == 0 || i == _nx - 1;
                bool bj = j == 0 || j == _ny - 1;
                bool bk = k == 0 || k == _nz - 1;
                if (!bi && !bj && !bk) continue;

                int v000 = Idx(i, j, k);

                // Quads along the (i,k) plane on the j faces.
                if (bj && i + 1 < _nx && k + 1 < _nz)
                    AddQuad(tris, v000, Idx(i + 1, j, k), Idx(i + 1, j, k + 1), Idx(i, j, k + 1), center);
                // Quads along the (j,k) plane on the i faces.
                if (bi && j + 1 < _ny && k + 1 < _nz)
                    AddQuad(tris, v000, Idx(i, j + 1, k), Idx(i, j + 1, k + 1), Idx(i, j, k + 1), center);
                // Quads along the (i,j) plane on the k faces.
                if (bk && i + 1 < _nx && j + 1 < _ny)
                    AddQuad(tris, v000, Idx(i + 1, j, k), Idx(i + 1, j + 1, k), Idx(i, j + 1, k), center);
            }

            _mesh.SetVertices(_unityVerts);
            _mesh.SetTriangles(tris, 0);
            _ = axisI; _ = axisJ; _ = axisK;
        }

        private int Idx(int i, int j, int k) => i + j * _nx + k * _nx * _ny;

        /// <summary>Dominant world axis of the step between two grid indices.</summary>
        private Axis DetectAxis(int a, int b)
        {
            Vector3 d = _unityVerts[b] - _unityVerts[a];
            if (Mathf.Abs(d.x) >= Mathf.Abs(d.y) && Mathf.Abs(d.x) >= Mathf.Abs(d.z)) return Axis.X;
            if (Mathf.Abs(d.y) >= Mathf.Abs(d.z)) return Axis.Y;
            return Axis.Z;
        }

        private enum Axis { X, Y, Z }

        private Vector3 ComputeCenter()
        {
            var c = Vector3.zero;
            foreach (Vector3 v in _unityVerts) c += v;
            return c / _unityVerts.Length;
        }

        /// <summary>Adds a quad as two triangles with outward winding (checked
        /// against the tissue center at rest, so it stays correct while
        /// deformed).</summary>
        private void AddQuad(System.Collections.Generic.List<int> tris,
                             int v0, int v1, int v2, int v3, Vector3 center)
        {
            Vector3 e1 = _unityVerts[v1] - _unityVerts[v0];
            Vector3 e2 = _unityVerts[v3] - _unityVerts[v0];
            Vector3 n = Vector3.Cross(e1, e2);
            Vector3 faceMid = (_unityVerts[v0] + _unityVerts[v1] + _unityVerts[v2] + _unityVerts[v3]) * 0.25f;
            bool flip = Vector3.Dot(n, faceMid - center) < 0f;

            if (!flip)
            {
                tris.AddRange(new[] { v0, v1, v2, v0, v2, v3 });
            }
            else
            {
                tris.AddRange(new[] { v0, v2, v1, v0, v3, v2 });
            }
        }
    }
}
