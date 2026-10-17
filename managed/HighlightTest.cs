using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace PPUpscaler
{
    // Debug: do highlights survive DLSS? Logs the camera's exposure-related post-processing, then over 8 frames (one jitter cycle; keep the camera
    // still) compares the brightest pixels of DLSS's HDR input and output in a 1024 x 1024 crop at the centre: the mean of the brightest 0.2 % and
    // the 99.9th percentile of luminance. Output lower than input means DLSS dims highlights.
    public static class HighlightTest
    {
        const int S = 1024, Frames = 8;
        static int left, got, expected;
        static RenderTexture[] ins = new RenderTexture[Frames], outs = new RenderTexture[Frames];
        static readonly List<float[]> inLum = new List<float[]>(), outLum = new List<float[]>();

        public static void Request(PostProcessLayer layer)
        {
            Recon(layer);
            left = Frames; got = 0; expected = 2 * Frames;
            inLum.Clear(); outLum.Clear();
            Main.Log("highlights: measuring over " + Frames + " frames, keep the camera still");
        }

        static void Recon(PostProcessLayer layer)
        {
            if (layer == null) { Main.Log("highlights: no camera"); return; }
            var sb = new StringBuilder("highlights: post-processing of " + layer.name + ":");
            var ae = layer.GetSettings<AutoExposure>();
            if (ae != null) sb.AppendFormat(" auto exposure {0} (luminance {1:F1}..{2:F1} EV, key {3:F2}, {4}, filtering {5})", ae.enabled.value && ae.active ? "ON" : "off",
                ae.minLuminance.value, ae.maxLuminance.value, ae.keyValue.value, ae.eyeAdaptation.value, ae.filtering.value);
            var cg = layer.GetSettings<ColorGrading>();
            if (cg != null) sb.AppendFormat("; colour grading {0} ({1}, tonemapper {2}, post exposure {3:F2} EV)", cg.enabled.value && cg.active ? "ON" : "off",
                cg.gradingMode.value, cg.tonemapper.value, cg.postExposure.value);
            var bl = layer.GetSettings<Bloom>();
            if (bl != null) sb.AppendFormat("; bloom {0} (intensity {1:F2}, threshold {2:F2})", bl.enabled.value && bl.active ? "ON" : "off", bl.intensity.value, bl.threshold.value);
            Main.Log(sb.ToString());
            // the current auto exposure value (a 1 x 1 texture inside PPv2's auto exposure renderer)
            var tex = AutoExposureTexture(layer);
            if (tex != null) AsyncGPUReadback.Request(tex, 0, TextureFormat.RFloat, r => Main.Log("highlights: current auto exposure value " + (r.hasError ? "unreadable" : r.GetData<float>()[0].ToString("F4"))));
            else Main.Log("highlights: no auto exposure texture (auto exposure has not run on this camera)");
        }

        public static RenderTexture AutoExposureTexture(PostProcessLayer layer)
        {
            var bundle = layer.GetBundle<AutoExposure>();
            if (bundle == null) return null;
            var rp = typeof(PostProcessBundle).GetProperty("renderer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var renderer = rp != null ? rp.GetValue(bundle, null) : null;
            if (renderer == null) return null;
            var f = renderer.GetType().GetField("m_CurrentAutoExposure", BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null ? f.GetValue(renderer) as RenderTexture : null;
        }

        // Called in the DLSS injection after the evaluate call.
        public static void Queue(CommandBuffer cmd, RenderTexture input, RenderTexture output, int w, int h)
        {
            if (left <= 0) return;
            int i = Frames - left--;
            if (ins[i] == null) { ins[i] = Make(); outs[i] = Make(); }
            int x0 = Mathf.Max(0, (w - S) / 2), y0 = Mathf.Max(0, (h - S) / 2);
            cmd.CopyTexture(input, 0, 0, x0, y0, Mathf.Min(S, w), Mathf.Min(S, h), ins[i], 0, 0, 0, 0);
            cmd.CopyTexture(output, 0, 0, x0, y0, Mathf.Min(S, w), Mathf.Min(S, h), outs[i], 0, 0, 0, 0);
            cmd.RequestAsyncReadback(ins[i], 0, TextureFormat.RGBAFloat, r => { if (!r.hasError) inLum.Add(Lum(r)); Done(); });
            cmd.RequestAsyncReadback(outs[i], 0, TextureFormat.RGBAFloat, r => { if (!r.hasError) outLum.Add(Lum(r)); Done(); });
        }

        static RenderTexture Make()
        {
            var rt = new RenderTexture(S, S, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "PPUpscaler highlights" };
            rt.Create();
            return rt;
        }

        static float[] Lum(AsyncGPUReadbackRequest r)
        {
            var d = r.GetData<float>();
            var l = new float[S * S];
            for (int i = 0; i < l.Length; i++) l[i] = 0.2126f * d[4 * i] + 0.7152f * d[4 * i + 1] + 0.0722f * d[4 * i + 2];
            return l;
        }

        static void Done()
        {
            if (++got < expected) return;
            if (inLum.Count == 0 || outLum.Count == 0) { Main.Log("highlights: readback failed"); return; }
            double inTop = 0, outTop = 0, inP = 0, outP = 0, inMax = 0, outMax = 0;
            foreach (var l in inLum) { float t, p, m; Stats(l, out t, out p, out m); inTop += t; inP += p; inMax += m; }
            foreach (var l in outLum) { float t, p, m; Stats(l, out t, out p, out m); outTop += t; outP += p; outMax += m; }
            int ni = inLum.Count, no = outLum.Count;
            Main.Log(string.Format("highlights: brightest 0.2 % mean: input {0:F3}, DLSS output {1:F3} (output/input {2:P0}); 99.9th percentile: input {3:F3}, output {4:F3} ({5:P0}); maximum: input {6:F2}, output {7:F2}",
                inTop / ni, outTop / no, (outTop / no) / (inTop / ni), inP / ni, outP / no, (outP / no) / (inP / ni), inMax / ni, outMax / no));
        }

        static void Stats(float[] l, out float top, out float p999, out float max)
        {
            var c = (float[])l.Clone();
            System.Array.Sort(c);
            int n = c.Length, k = Mathf.Max(1, n / 500);
            double s = 0; for (int i = n - k; i < n; i++) s += c[i];
            top = (float)(s / k); p999 = c[n - n / 1000 - 1]; max = c[n - 1];
        }
    }
}
