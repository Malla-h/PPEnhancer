using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PPUpscaler
{
    // Debug: over one second, which renderers under the geoscape (or the camera) turn, move or scroll their textures on their own.
    // Anything that does gets no motion of its own from the depth-based motion vectors.
    public class MotionCensus : MonoBehaviour
    {
        static MotionCensus inst;
        void Awake() { inst = this; }
        public static void Run() { if (inst != null) inst.StartCoroutine(inst.Census()); }

        static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        IEnumerator Census()
        {
            var list = new List<Renderer>();
            foreach (var r in FindObjectsOfType<Renderer>()) if (r.enabled && r.gameObject.activeInHierarchy && r.isVisible) list.Add(r);
            var m0 = new Dictionary<Renderer, Matrix4x4>();
            var uv0 = new Dictionary<Material, Vector4>();
            foreach (var r in list)
            {
                m0[r] = r.localToWorldMatrix;
                foreach (var m in r.sharedMaterials) if (m != null && m.HasProperty("_MainTex") && !uv0.ContainsKey(m)) uv0[m] = ST(m);
            }
            float t0 = Time.unscaledTime;
            yield return new WaitForSecondsRealtime(1f);
            float dt = Time.unscaledTime - t0;
            int shown = 0;
            Main.Log("census: " + list.Count + " visible renderers, over " + dt.ToString("F2") + " s:");
            foreach (var r in list)
            {
                if (r == null) continue;
                var a = m0[r]; var b = r.localToWorldMatrix;
                float rot = Quaternion.Angle(a.rotation, b.rotation) / dt, move = Vector3.Distance(a.GetColumn(3), b.GetColumn(3)) / dt;
                var scroll = new StringBuilder();
                foreach (var m in r.sharedMaterials)
                {
                    Vector4 s0;
                    if (m == null || !uv0.TryGetValue(m, out s0)) continue;
                    var s1 = ST(m);
                    if ((s1 - s0).sqrMagnitude > 1e-10f) scroll.AppendFormat(" {0} texture offset {1:F4},{2:F4}/s", m.name, (s1.z - s0.z) / dt, (s1.w - s0.w) / dt);
                }
                if (rot < 1e-4f && move < 1e-5f && scroll.Length == 0) continue;
                if (shown++ > 40) { Main.Log("census: (more not listed)"); break; }
                var mat = r.sharedMaterial;
                Main.Log(string.Format("census:   {0} [{1} q{2}] turns {3:F3} deg/s, moves {4:F4}/s{5}", PathOf(r.transform),
                    mat != null && mat.shader != null ? mat.shader.name : "?", mat != null ? mat.renderQueue : -1, rot, move, scroll));
            }
            Main.Log("census: done");
        }

        static Vector4 ST(Material m)
        {
            var o = m.mainTextureOffset; var s = m.mainTextureScale;
            return new Vector4(s.x, s.y, o.x, o.y);
        }
    }
}
