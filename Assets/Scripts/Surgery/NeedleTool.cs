using System.Collections.Generic;
using UnityEngine;

namespace SofaRobotAI.Surgery
{
    /// <summary>
    /// Curved suture needle + thread held by a robot arm's gripper.
    /// The needle is a visual/kinematic tool anchored to the TCP; the thread is
    /// a line that runs from the wrist spool through every completed knot to
    /// the needle tail, so it follows the arm while stitching.
    /// </summary>
    public class NeedleTool : MonoBehaviour
    {
        /// <summary>Tip of the needle (piercing end) in world space.</summary>
        public Transform Tip { get; private set; }
        /// <summary>Tail of the needle (where the thread attaches).</summary>
        public Transform Tail { get; private set; }
        /// <summary>Spool origin of the thread, on the wrist.</summary>
        public Transform Spool { get; private set; }

        /// <summary>World offset from the TCP to the needle tip.</summary>
        public Vector3 TipOffsetFromTcp { get; private set; }

        private LineRenderer _thread;
        private readonly List<Vector3> _anchors = new List<Vector3>();

        private const float NeedleThickness = 0.0022f;
        private float _needleRadius = 0.045f;

        /// <summary>Builds the tool as a child of the arm's TCP frame.</summary>
        public static NeedleTool Attach(Transform tcp, Transform wrist, float radius = 0.045f)
        {
            var go = new GameObject("NeedleTool");
            go.transform.SetParent(tcp, false);
            var tool = go.AddComponent<NeedleTool>();
            tool.Build(wrist, radius);
            return tool;
        }

        private void Build(Transform wrist, float radius)
        {
            _needleRadius = radius;
            // Needle: half-circle arc in the local X-Y plane. The shank is held
            // between the fingers at the TCP, the arc curves down and forward,
            // the tip ends up below-front of the TCP.
            var needle = new GameObject("needle");
            needle.transform.SetParent(transform, false);
            needle.transform.localRotation = Quaternion.identity;

            var mesh = BuildNeedleMesh();
            needle.AddComponent<MeshFilter>().sharedMesh = mesh;
            needle.AddComponent<MeshRenderer>().sharedMaterial = MakeMat(new Color(0.85f, 0.87f, 0.92f));

            // Key frames along the arc.
            Tip = MakeFrame("tip", needle.transform, new Vector3(0f, -_needleRadius * 1.05f, _needleRadius * 0.35f));
            Tail = MakeFrame("tail", needle.transform, new Vector3(0f, 0f, -_needleRadius * 0.15f));

            Spool = MakeFrame("spool", wrist, Vector3.zero);

            TipOffsetFromTcp = transform.InverseTransformDirection(Tip.position - transform.position);

            _thread = gameObject.AddComponent<LineRenderer>();
            _thread.startWidth = 0.0028f;
            _thread.endWidth = 0.0028f;
            _thread.material = MakeMat(new Color(0.95f, 0.9f, 0.75f));
            _thread.positionCount = 2;
            _thread.useWorldSpace = true;
        }

        private static Transform MakeFrame(string name, Transform parent, Vector3 localPos)
        {
            var f = new GameObject(name).transform;
            f.SetParent(parent, false);
            f.localPosition = localPos;
            return f;
        }

        /// <summary>Sets the fixed thread path points (completed knots).</summary>
        public void SetAnchors(IEnumerable<Vector3> anchors) => _anchors.ClearAdd(anchors);

        /// <summary>Thread: spool -> anchors (knots) -> needle tail.</summary>
        public void UpdateThread()
        {
            if (_thread == null || Tip == null) return;
            var path = new List<Vector3> { Spool.position };
            path.AddRange(_anchors);
            path.Add(Tail.position);
            _thread.positionCount = path.Count;
            _thread.SetPositions(path.ToArray());
        }

        /// <summary>
        /// Procedural half-torus needle mesh: a tube swept along a semicircular
        /// arc in the X-Y plane, from the tail (top) around to the tip (front).
        /// </summary>
        private Mesh BuildNeedleMesh()
        {
            const int segments = 14;
            const int sides = 6;

            // Arc from angle 180° (tail, +X side up) through 90° (front) to ~0° (bottom tip).
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var ring = new Vector3[sides];
            for (int s = 0; s <= segments; s++)
            {
                float t = s / (float)segments;
                float ang = Mathf.PI * (1f - t) * 0.92f; // slightly less than半圆 so the tip points forward-down
                Vector3 center = new Vector3(Mathf.Cos(ang) * _needleRadius - _needleRadius * 0.15f,
                                             -Mathf.Sin(ang) * _needleRadius + _needleRadius * 0.2f,
                                             0f);
                Vector3 tangent = new Vector3(-Mathf.Sin(ang), -Mathf.Cos(ang), 0f);

                // Ring of the tube around the arc point.
                Vector3 n1 = new Vector3(0f, 0f, 1f);
                Vector3 n2 = Vector3.Cross(tangent, n1).normalized;
                for (int i = 0; i < sides; i++)
                {
                    float a = i / (float)sides * Mathf.PI * 2f;
                    ring[i] = center + (Mathf.Cos(a) * n2 + Mathf.Sin(a) * n1) * NeedleThickness;
                    verts.Add(ring[i]);
                }
            }
            for (int s = 0; s < segments; s++)
            {
                for (int i = 0; i < sides; i++)
                {
                    int j = (i + 1) % sides;
                    int a = s * sides, b = (s + 1) * sides;
                    tris.Add(a + i); tris.Add(b + i); tris.Add(a + j);
                    tris.Add(a + j); tris.Add(b + i); tris.Add(b + j);
                }
            }

            var mesh = new Mesh { name = "SutureNeedle" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material MakeMat(Color c)
        {
            var m = new Material(Shader.Find("Standard")) { color = c };
            m.SetFloat("_Glossiness", 0.7f);
            return m;
        }
    }

    internal static class AnchorListExtensions
    {
        public static void ClearAdd(this List<Vector3> list, IEnumerable<Vector3> items)
        {
            list.Clear();
            list.AddRange(items);
        }
    }
}
