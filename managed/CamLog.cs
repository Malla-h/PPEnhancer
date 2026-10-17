using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace PPUpscaler
{
    // Recon: logs every enabled camera and its render setup whenever any of it changes (scene, resolution, target, post stack, AA mode).
    public class CamLog : MonoBehaviour
    {
        readonly Dictionary<int, string> last = new Dictionary<int, string>();
        float next, fpsStart, worst; int frames, hitches, gcStart;

        void Update()
        {
            if (!Main.DeveloperOptions.Value) return;   // diagnostics only: nothing is logged for players
            frames++;
            float dt = Time.unscaledDeltaTime * 1000f;
            if (dt > worst) worst = dt;
            if (dt > 33f) hitches++;
            if (Time.unscaledTime - fpsStart >= 5f)
            {
                int gc = System.GC.CollectionCount(0);
                Main.Log(string.Format("fps {0:F1} ({1:F1} ms) worst={2:F0}ms hitches(>33ms)={3} gc={4} dlaa={5} mvcheck={6}", frames / (Time.unscaledTime - fpsStart), 1000f * (Time.unscaledTime - fpsStart) / frames,
                    worst, hitches, gc - gcStart, Main.Enabled.Value, Main.MvCheckOn.Value));
                worst = 0; hitches = 0; gcStart = gc;
                fpsStart = Time.unscaledTime; frames = 0;
            }
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f;
            foreach (var c in Camera.allCameras)
            {
                var sb = new StringBuilder();
                sb.Append(c.name).Append(" depth=").Append(c.depth)
                  .Append(" px=").Append(c.pixelWidth).Append('x').Append(c.pixelHeight)
                  .Append(" rect=").Append(c.rect)
                  .Append(" target=").Append(c.targetTexture ? c.targetTexture.width + "x" + c.targetTexture.height + " " + c.targetTexture.format : "screen")
                  .Append(" path=").Append(c.actualRenderingPath).Append(" hdr=").Append(c.allowHDR).Append(" msaa=").Append(c.allowMSAA)
                  .Append(" dynres=").Append(c.allowDynamicResolution).Append(" depthMode=").Append(c.depthTextureMode)
                  .Append(" clear=").Append(c.clearFlags).Append(" mask=").Append(c.cullingMask).Append(" fov=").Append(c.fieldOfView)
                  .Append(" ortho=").Append(c.orthographic).Append(" buffers=").Append(c.commandBufferCount);
                var l = c.GetComponent<PostProcessLayer>();
                if (l != null) sb.Append(" ppl(enabled=").Append(l.enabled).Append(" aa=").Append(l.antialiasingMode).Append(" layer=").Append(l.volumeLayer.value).Append(')');
                var s = sb.ToString();
                string old;
                if (!last.TryGetValue(c.GetInstanceID(), out old) || old != s) { last[c.GetInstanceID()] = s; Main.Log("camera " + s); }
            }
        }
    }
}
