using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace PPUpscaler
{
    // Recon: turns on the built-in motion vector pass for the cameras that have a post-process layer, copies the motion vector texture to a small
    // float RT before image effects, reads it back and logs statistics once per second per camera (how much of the screen has motion, size, mean
    // signed value). Move the camera, then stand still, and compare. Changes nothing visible except the extra pass.
    public class MvProbe : MonoBehaviour
    {
        class Entry
        {
            public Camera cam; public CommandBuffer cb; public RenderTexture rt; public bool pending; public float next; public Vector3 pos; public Quaternion rot; public bool attached;
        }
        readonly Dictionary<int, Entry> map = new Dictionary<int, Entry>();
        const int W = 480, H = 270;
        const CameraEvent Ev = CameraEvent.BeforeImageEffects;

        void Update()
        {
            if (!Main.MvPass.Value || Bench.Running) return;
            foreach (var c in Camera.allCameras)
            {
                if (c.targetTexture != null || c.GetComponent<PostProcessLayer>() == null) continue;
                Entry e;
                if (!map.TryGetValue(c.GetInstanceID(), out e))
                {
                    e = new Entry { cam = c, cb = new CommandBuffer { name = "PPUpscaler MV probe" } };
                    e.rt = new RenderTexture(W, H, 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear);
                    e.rt.name = "PPUpscaler MV probe";
                    e.rt.Create();
                    e.cb.Blit(BuiltinRenderTextureType.MotionVectors, e.rt);
                    map[c.GetInstanceID()] = e;
                    Main.Log("mv probe attached to " + c.name);
                }
                if ((c.depthTextureMode & DepthTextureMode.MotionVectors) == 0) c.depthTextureMode |= DepthTextureMode.MotionVectors;
                // Attach once. (GetCommandBuffers returns new wrapper objects, so comparing them with e.cb never matches: checking that way
                // re-added the buffer every frame and stacked thousands of full-screen copies on the camera.)
                if (!e.attached) { c.AddCommandBuffer(Ev, e.cb); e.attached = true; }
                if (!Main.MvReadback.Value || e.pending || Time.unscaledTime < e.next) continue;
                e.next = Time.unscaledTime + 1f;
                e.pending = true;
                var t = c.transform;
                float moved = Vector3.Distance(t.position, e.pos), turned = Quaternion.Angle(t.rotation, e.rot);
                e.pos = t.position; e.rot = t.rotation;
                var entry = e; var cam = c; float fov = c.fieldOfView;
                AsyncGPUReadback.Request(e.rt, 0, TextureFormat.RGFloat, req =>
                {
                    entry.pending = false;
                    if (req.hasError || cam == null) { Main.Log("mv probe readback error on " + (cam ? cam.name : "?")); return; }
                    var d = req.GetData<float>();
                    int n = d.Length / 2, nz = 0; double sx = 0, sy = 0, mag = 0; float max = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float x = d[2 * i], y = d[2 * i + 1];
                        float m = Mathf.Sqrt(x * x + y * y);
                        if (m > 1e-5f) { nz++; sx += x; sy += y; mag += m; if (m > max) max = m; }
                    }
                    Main.Log(string.Format("mv {0}: nonzero={1:P1} meanMag={2:E2} max={3:E2} meanXY=({4:E2},{5:E2}) camMoved={6:F3} turned={7:F2}deg fov={8:F1}",
                        cam.name, (double)nz / n, nz > 0 ? mag / nz : 0, max, nz > 0 ? sx / nz : 0, nz > 0 ? sy / nz : 0, moved, turned, fov));
                });
            }
        }
    }
}
