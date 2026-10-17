using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace PPUpscaler
{
    // Debug: logs how the current 3D camera renders (components, command buffers, projection) and every renderer it can see: shader, render
    // queue, motion vector mode, and whether it moved between two frames (moving objects need motion vectors, transparent ones get none).
    public class SceneDump : MonoBehaviour
    {
        static SceneDump inst;
        void Awake() { inst = this; }
        public static void Run() { if (inst != null) inst.StartCoroutine(inst.Dump()); }

        static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        IEnumerator Dump()
        {
            Camera cam = null;
            foreach (var c in Camera.allCameras)
                if (c.targetTexture == null && c.cullingMask != 0 && c.GetComponent<PostProcessLayer>() != null && (cam == null || c.depth > cam.depth)) cam = c;
            if (cam == null) { Main.Log("dump: no 3D camera"); yield break; }

            Main.Log("dump: camera " + cam.name + " path " + Path(cam.transform) + " near " + cam.nearClipPlane + " far " + cam.farClipPlane + " fov " + cam.fieldOfView
                + " physical " + cam.usePhysicalProperties + " ortho " + cam.orthographic + " depthMode " + cam.depthTextureMode + " jitteredTransparents " + cam.useJitteredProjectionMatrixForTransparentRendering);
            foreach (var comp in cam.GetComponents<Component>())
            {
                var b = comp as Behaviour;
                Main.Log("dump:   component " + comp.GetType().FullName + (b != null ? (b.enabled ? "" : " (disabled)") : ""));
            }
            foreach (CameraEvent ev in System.Enum.GetValues(typeof(CameraEvent)))
                foreach (var cb in cam.GetCommandBuffers(ev)) Main.Log("dump:   command buffer at " + ev + ": " + cb.name + " (" + cb.sizeInBytes + " bytes)");
            foreach (var c in Camera.allCameras) if (c != cam) Main.Log("dump:   other camera " + c.name + " depth " + c.depth + " mask " + c.cullingMask + " target " + (c.targetTexture ? c.targetTexture.name : "screen"));

            var list = new List<Renderer>();
            foreach (var r in FindObjectsOfType<Renderer>())
                if (r.enabled && r.gameObject.activeInHierarchy && (cam.cullingMask & (1 << r.gameObject.layer)) != 0) list.Add(r);
            var before = new Dictionary<Renderer, Matrix4x4>();
            foreach (var r in list) before[r] = r.localToWorldMatrix;
            yield return null; yield return null;

            Main.Log("dump: " + list.Count + " renderers on the camera's layers");
            int n = 0;
            foreach (var r in list)
            {
                if (r == null) continue;
                if (++n > 400) { Main.Log("dump: (more renderers not listed)"); break; }
                bool moved = false;
                var m = r.localToWorldMatrix; var o = before[r];
                for (int i = 0; i < 16; i++) if (Mathf.Abs(m[i] - o[i]) > 1e-6f) { moved = true; break; }
                var sb = new StringBuilder();
                sb.Append("dump: ").Append(r.isVisible ? "VIS " : "    ").Append(moved ? "MOVES " : "      ").Append(r.GetType().Name).Append(" ").Append(Path(r.transform))
                  .Append(" layer ").Append(LayerMask.LayerToName(r.gameObject.layer)).Append(" mv ").Append(r.motionVectorGenerationMode);
                var ps = r as ParticleSystemRenderer;
                if (ps != null) sb.Append(" particles");
                foreach (var mat in r.sharedMaterials)
                    if (mat != null) sb.Append(" | ").Append(mat.name).Append(" [").Append(mat.shader != null ? mat.shader.name : "?").Append(" q").Append(mat.renderQueue).Append("]");
                Main.Log(sb.ToString());
            }
            foreach (var cv in FindObjectsOfType<Canvas>())
                if (cv.isRootCanvas && cv.gameObject.activeInHierarchy && cv.renderMode != RenderMode.ScreenSpaceOverlay)
                    Main.Log("dump: canvas " + Path(cv.transform) + " mode " + cv.renderMode + " camera " + (cv.worldCamera ? cv.worldCamera.name : "none") + " layer " + LayerMask.LayerToName(cv.gameObject.layer));
            Main.Log("dump: done");
        }
    }
}
