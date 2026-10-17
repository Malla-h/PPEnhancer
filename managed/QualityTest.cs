using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace PPUpscaler
{
    // Debug: objective image quality per quality mode, in the same still view. Captures a 768 x 768 crop of the screen at Native (DLAA) as the
    // reference, then in each mode after DLSS has settled: how much of the reference's fine detail it reproduces (correlation of the detail, each
    // image minus a 5 x 5 blur of itself), how strong its detail is relative to the reference, and how much it flickers between two frames
    // (difference of the detail of consecutive frames relative to its strength). Keep the camera still and the mouse away from the 3D view.
    public class QualityTest : MonoBehaviour
    {
        const int C = 768, Settle = 150;
        static QualityTest inst;
        public static bool Running;
        void Awake() { inst = this; }
        public static void Run() { if (inst != null && !Running) inst.StartCoroutine(inst.Test()); }

        IEnumerator Test()
        {
            Running = true;
            int keep = Main.Quality.Value;
            bool keepNoJit = Main.NoJitter.Value; float keepSx = Main.JitterSignX.Value, keepSy = Main.JitterSignY.Value;
            // mode, no jitter, jitter sign x, jitter sign y
            var runs = new[] { new[] { 0, 0, -1, -1 }, new[] { 2, 0, -1, -1 }, new[] { 4, 0, -1, -1 }, new[] { 4, 1, -1, -1 }, new[] { 4, 0, 1, -1 }, new[] { 4, 0, -1, 1 }, new[] { 4, 0, 1, 1 } };
            float[] refDetail = null;
            var sb = new StringBuilder("quality test (" + C + "x" + C + " screen crop at the centre):");
            foreach (var run in runs)
            {
                int m = run[0];
                Main.Quality.Value = m; Main.NoJitter.Value = run[1] == 1; Main.JitterSignX.Value = run[2]; Main.JitterSignY.Value = run[3];
                for (int i = 0; i < Settle; i++) yield return null;
                float[] a = null, b = null;
                yield return Grab(r => a = r);
                yield return Grab(r => b = r);
                var da = Detail(a); var db = Detail(b);
                if (refDetail == null) refDetail = da;
                float corr = Corr(da, refDetail), strength = Rms(da) / Rms(refDetail), flicker = RmsDiff(da, db) / Rms(da);
                string label = Main.QualityNames[m] + (run[1] == 1 ? ", no jitter" : run[2] != -1 || run[3] != -1 ? ", jitter sign " + run[2] + "," + run[3] : "");
                sb.AppendFormat("\n  {0,-34} render {1,4}x{2,-4}  detail vs native {3:F3}  detail strength {4:P0}  frame-to-frame change {5:P1}",
                    label, Mathf.RoundToInt(Screen.width * Main.ScaleNow()), Mathf.RoundToInt(Screen.height * Main.ScaleNow()), corr, strength, flicker);
                if (m == 4 && run[1] == 0 && run[2] == -1 && run[3] == -1) SaveMap(da, refDetail, a);
            }
            Main.Quality.Value = keep; Main.NoJitter.Value = keepNoJit; Main.JitterSignX.Value = keepSx; Main.JitterSignY.Value = keepSy;
            Main.Log(sb.ToString());
            Running = false;
        }

        // DLSS's own output (before post-processing, so the game's film grain and dithering do not count), centre crop, tone mapped.
        IEnumerator Grab(System.Action<float[]> done)
        {
            yield return new WaitForEndOfFrame();
            var rt = Dlaa.Output;
            if (rt == null) { done(new float[C * C]); yield break; }
            float[] l = null;
            AsyncGPUReadback.Request(rt, 0, (rt.width - C) / 2, C, (rt.height - C) / 2, C, 0, 1, TextureFormat.RGBAFloat, r =>
            {
                l = new float[C * C];
                if (r.hasError) return;
                var d = r.GetData<float>();
                for (int i = 0; i < l.Length; i++) l[i] = 0.2126f * T(d[4 * i]) + 0.7152f * T(d[4 * i + 1]) + 0.0722f * T(d[4 * i + 2]);
            });
            while (l == null) yield return null;
            done(l);
        }

        static float T(float v) { v = Mathf.Max(0f, v); return Mathf.Pow(v / (1f + v), 1f / 2.2f); }

        // Where detail is lost: local correlation (9 x 9) of Performance's detail with Native's. Red: lost, green: kept, blue: the image.
        static void SaveMap(float[] d, float[] r, float[] img)
        {
            var t = new Texture2D(C, C, TextureFormat.RGB24, false);
            for (int y = 0; y < C; y++) for (int x = 0; x < C; x++)
            {
                double ab = 0, aa = 0, bb = 0;
                for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                {
                    int xx = Mathf.Clamp(x + dx, 0, C - 1), yy = Mathf.Clamp(y + dy, 0, C - 1), j = yy * C + xx;
                    ab += d[j] * r[j]; aa += d[j] * d[j]; bb += r[j] * r[j];
                }
                float energy = (float)System.Math.Sqrt(bb / 81.0);
                float c = energy > 0.004f ? (float)(ab / System.Math.Sqrt(aa * bb + 1e-12)) : 1f;
                t.SetPixel(x, y, new Color(Mathf.Clamp01(1f - c), energy > 0.004f ? Mathf.Clamp01(c) : 0f, img[y * C + x] * 0.7f));
            }
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(Main.Dir, "quality_map.png"), t.EncodeToPNG());
            Destroy(t);
        }

        static float[] Detail(float[] a)
        {
            var o = new float[C * C];
            for (int y = 0; y < C; y++) for (int x = 0; x < C; x++)
            {
                float s = 0; int n = 0;
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) { int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= C || yy >= C) continue; s += a[yy * C + xx]; n++; }
                o[y * C + x] = a[y * C + x] - s / n;
            }
            return o;
        }

        static float Rms(float[] a) { double s = 0; foreach (var v in a) s += v * v; return (float)System.Math.Sqrt(s / a.Length); }
        static float RmsDiff(float[] a, float[] b) { double s = 0; for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; s += d * d; } return (float)System.Math.Sqrt(s / a.Length); }
        static float Corr(float[] a, float[] b)
        {
            double ab = 0, aa = 0, bb = 0;
            for (int i = 0; i < a.Length; i++) { ab += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
            return (float)(ab / System.Math.Sqrt(aa * bb + 1e-12));
        }
    }
}
