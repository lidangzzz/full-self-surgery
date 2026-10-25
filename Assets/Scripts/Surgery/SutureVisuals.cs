using System.Collections.Generic;
using UnityEngine;

namespace SofaRobotAI.Surgery
{
    /// <summary>Visual bookkeeping of the suturing: knots, cinching bands and
    /// the shrinking blood pool, plus the thread path over completed stitches.
    /// Stitch counting is global so batches sharing one wound shrink it
    /// together.</summary>
    public static class SutureVisuals
    {
        private static Material _knotMat;
        private static int _completedGlobal;
        private static int _registeredTotal = 1;

        /// <summary>Called by the cell builder after all stitches exist.</summary>
        public static void Reset(int totalStitches)
        {
            _completedGlobal = 0;
            _registeredTotal = Mathf.Max(1, totalStitches);
        }

        /// <summary>Called when a stitch is cinched: knot + edge band + wound shrink.</summary>
        public static void MarkStitch(StitchPoint stitch, SutureTask task)
        {
            Vector3 mid = stitch.Midpoint;

            // Knot: small sphere where the thread is tied off.
            stitch.knotVisual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(stitch.knotVisual.GetComponent<Collider>());
            stitch.knotVisual.name = $"knot_{task.organName}_{task.stitches.IndexOf(stitch) + 1}";
            stitch.knotVisual.transform.position = mid;
            stitch.knotVisual.transform.localScale = Vector3.one * 0.008f;
            stitch.knotVisual.GetComponent<Renderer>().sharedMaterial = KnotMat();

            // Cinch band: the thread segment pulling the two edges together.
            var band = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(band.GetComponent<Collider>());
            band.name = stitch.knotVisual.name + "_band";
            Vector3 a = stitch.entry; Vector3 b = stitch.exit;
            band.transform.position = (a + b) * 0.5f;
            band.transform.rotation = Quaternion.LookRotation(b - a);
            band.transform.localScale = new Vector3(0.004f, 0.004f, Vector3.Distance(a, b));
            band.GetComponent<Renderer>().sharedMaterial = KnotMat();
            band.transform.SetParent(stitch.knotVisual.transform, true);

            // Shrink the blood pool as the wound closes (absolute scale, shared
            // across every batch working on this wound).
            _completedGlobal++;
            if (task.incisionVisual != null)
            {
                float s = Mathf.Max(1f - _completedGlobal / (float)_registeredTotal, 0.08f);
                task.incisionVisual.transform.localScale =
                    Vector3.Scale(task.incisionBaseScale, new Vector3(s, 1f, s));
            }
        }

        /// <summary>Thread path anchors: every completed knot, in sewing order.</summary>
        public static List<Vector3> AnchorPath(SutureTask task)
        {
            var path = new List<Vector3>();
            if (task == null) return path;
            foreach (var s in task.stitches)
                if (s.completed) path.Add(s.Midpoint);
            return path;
        }

        private static Material KnotMat()
        {
            if (_knotMat == null)
            {
                _knotMat = new Material(Shader.Find("Standard")) { color = new Color(0.95f, 0.9f, 0.75f) };
            }
            return _knotMat;
        }
    }
}
