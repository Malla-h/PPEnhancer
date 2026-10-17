using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace PPUpscaler
{
    // Debug: a sensitive check of the motion vectors against the images, at any render size. With the camera jitter off, it takes two consecutive
    // frames of DLSS's input (render size, a 512 x 512 crop at the centre) and, for each 32 x 32 block, searches the sub-pixel correction (quarter
    // pixels, up to +-2 px) that best maps the previous frame onto the current one on top of the motion vectors. Correct vectors need no
    // correction; a wrong scale shows as a correction that grows with the motion; a constant error as a fixed shift. Unity's motion vectors and
    // the depth-based ones are tested on the same frames. Keep the camera moving.
    public static class MvBlockTest
    {
        const int S = 512, B = 32, Pairs = 6;
        static int pairsLeft, wait, stage;
        static bool keepNoJitter;
        static RenderTexture cropA, cropB, unityFull, unityCrop, depthFull, depthCrop;
        static float[] la, lb, mu, md; static int got; static int rw, rh;
        static Vector2 msUnity;
        static bool mapSaved;
        static readonly List<string> results = new List<string>();

        public static void Request()
        {
            if (pairsLeft > 0) return;
            keepNoJitter = Main.NoJitter.Value;
            Main.NoJitter.Value = true;
            pairsLeft = Pairs; stage = 0; wait = 90; mapSaved = false; results.Clear();
            Main.Log("mv block test: starts in about 1.5 s, jitter off; keep the camera moving (pan, also turn and zoom)");
        }

        static RenderTexture Rt(int w, int h, RenderTextureFormat f, bool uav)
        {
            var t = new RenderTexture(w, h, 0, f, RenderTextureReadWrite.Linear) { enableRandomWrite = uav, filterMode = FilterMode.Point };
            t.Create();
            return t;
        }

        // Called in the DLSS injection each frame, after the inputs were copied.
        public static void Queue(CommandBuffer cmd, RenderTexture color, RenderTexture depth, int w, int h, Matrix4x4 invVPj, Matrix4x4 vpCur, Matrix4x4 vpPrev)
        {
            if (pairsLeft <= 0) return;
            if (wait > 0) { wait--; return; }
            if (w < S || h < S) { Main.Log("mv block test: render size too small"); pairsLeft = 0; Main.NoJitter.Value = keepNoJitter; return; }
            int x0 = (w - S) / 2, y0 = (h - S) / 2;
            if (cropA == null) { cropA = Rt(S, S, RenderTextureFormat.ARGBHalf, false); cropB = Rt(S, S, RenderTextureFormat.ARGBHalf, false); unityCrop = Rt(S, S, RenderTextureFormat.RGFloat, false); depthCrop = Rt(S, S, RenderTextureFormat.RGFloat, false); }
            if (unityFull == null || unityFull.width != w || unityFull.height != h)
            {
                if (unityFull != null) { unityFull.Release(); Object.Destroy(unityFull); depthFull.Release(); Object.Destroy(depthFull); }
                unityFull = Rt(w, h, RenderTextureFormat.RGFloat, false); depthFull = Rt(w, h, RenderTextureFormat.RGFloat, true);
            }
            if (stage == 0)
            {
                cmd.CopyTexture(color, 0, 0, x0, y0, S, S, cropA, 0, 0, 0, 0);
                stage = 1;
                return;
            }
            rw = w; rh = h;
            msUnity = new Vector2(Main.MvScaleX.Value * w, Main.MvScaleY.Value * h);
            cmd.CopyTexture(color, 0, 0, x0, y0, S, S, cropB, 0, 0, 0, 0);
            cmd.Blit(BuiltinRenderTextureType.MotionVectors, unityFull);
            cmd.CopyTexture(unityFull, 0, 0, x0, y0, S, S, unityCrop, 0, 0, 0, 0);
            Dlss.QueueDepthMv(cmd, depth, depthFull, w, h, invVPj, vpCur, vpPrev, ObjectMotion.Delta, ObjectMotion.Sphere, ObjectMotion.On);
            cmd.CopyTexture(depthFull, 0, 0, x0, y0, S, S, depthCrop, 0, 0, 0, 0);
            got = 0;
            cmd.RequestAsyncReadback(cropA, 0, TextureFormat.RGBAFloat, r => { la = Lum(r); Done(); });
            cmd.RequestAsyncReadback(cropB, 0, TextureFormat.RGBAFloat, r => { lb = Lum(r); Done(); });
            cmd.RequestAsyncReadback(unityCrop, r => { mu = r.hasError ? null : r.GetData<float>().ToArray(); Done(); });
            cmd.RequestAsyncReadback(depthCrop, r => { md = r.hasError ? null : r.GetData<float>().ToArray(); Done(); });
            stage = 0; wait = 20;
        }

        static float[] Lum(AsyncGPUReadbackRequest r)
        {
            if (r.hasError) return null;
            var d = r.GetData<float>();
            var l = new float[S * S];
            for (int i = 0; i < l.Length; i++) { float v = 0.2126f * d[4 * i] + 0.7152f * d[4 * i + 1] + 0.0722f * d[4 * i + 2]; l[i] = v / (1f + v); }
            return l;
        }

        static void Done()
        {
            if (++got < 4) return;
            pairsLeft--;
            if (la == null || lb == null || mu == null || md == null) results.Add("readback failed");
            else
            {
                // DLSS's convention: previous minus current position, render pixels, rows down in texture memory (the readback order).
                var vu = new float[S * S * 2]; var vd = new float[S * S * 2];
                for (int i = 0; i < S * S; i++) { vu[2 * i] = mu[2 * i] * msUnity.x; vu[2 * i + 1] = mu[2 * i + 1] * msUnity.y; vd[2 * i] = md[2 * i]; vd[2 * i + 1] = md[2 * i + 1]; }
                var sb = new StringBuilder();
                sb.Append("pair ").Append(Pairs - pairsLeft).Append(" (render ").Append(rw).Append("x").Append(rh).Append("):\n    Unity's      ").Append(Analyse(vu, !mapSaved ? "mvtest_unity.png" : null));
                sb.Append("\n    depth-based  ").Append(Analyse(vd, !mapSaved ? "mvtest_depth.png" : null));
                results.Add(sb.ToString());
            }
            if (pairsLeft <= 0)
            {
                Main.NoJitter.Value = keepNoJitter;
                Main.Log("mv block test (blocks of " + B + " px with texture and over 0.5 px of motion; correction = what has to be added to the vectors):\n  " + string.Join("\n  ", results.ToArray()));
            }
        }

        static float Sample(float[] l, float x, float y)
        {
            int x0 = (int)x, y0 = (int)y; float fx = x - x0, fy = y - y0;
            int i = y0 * S + x0;
            return Mathf.Lerp(Mathf.Lerp(l[i], l[i + 1], fx), Mathf.Lerp(l[i + S], l[i + S + 1], fx), fy);
        }

        // Per block: the correction (dx, dy) on top of the block's own vectors that best maps A onto B.
        static string Analyse(float[] v, string mapName)
        {
            int nb = S / B, used = 0;
            var corrX = new List<float>(); var corrY = new List<float>(); var motion = new List<float>(); var errRatio = new List<float>();
            double sxm = 0, sxx = 0, sym = 0, syy = 0;   // correction against motion, per axis: slope = scale error
            var map = mapName != null ? new Texture2D(S, S, TextureFormat.RGB24, false) : null;
            if (map != null) for (int y = 0; y < S; y++) for (int x = 0; x < S; x++) { float g = lb[y * S + x] * 0.6f; map.SetPixel(x, S - 1 - y, new Color(g, g, g)); }
            for (int by = 0; by < nb; by++)
                for (int bx = 0; bx < nb; bx++)
                {
                    // texture in the block (to tell a correction from noise) and its mean motion
                    double tex = 0, mx = 0, my = 0;
                    for (int y = by * B; y < by * B + B; y++) for (int x = bx * B; x < bx * B + B; x++)
                    {
                        int i = y * S + x;
                        if (x + 1 < S) tex += Mathf.Abs(lb[i + 1] - lb[i]);
                        if (y + 1 < S) tex += Mathf.Abs(lb[i + S] - lb[i]);
                        mx += v[2 * i]; my += v[2 * i + 1];
                    }
                    tex /= B * B; mx /= B * B; my /= B * B;
                    float mag = Mathf.Sqrt((float)(mx * mx + my * my));
                    if (tex < 0.01 || mag < 0.5f) continue;
                    float best = float.MaxValue, bdx = 0, bdy = 0, zero = 0;
                    bool ok = true;
                    for (int qy = -8; qy <= 8 && ok; qy++)
                        for (int qx = -8; qx <= 8; qx++)
                        {
                            float dx = qx * 0.25f, dy = qy * 0.25f, e = 0; int n = 0;
                            for (int y = by * B + 2; y < by * B + B - 2; y++)
                                for (int x = bx * B + 2; x < bx * B + B - 2; x++)
                                {
                                    int i = y * S + x;
                                    float px = x + v[2 * i] + dx, py = y + v[2 * i + 1] + dy;
                                    if (px < 0 || py < 0 || px >= S - 1 || py >= S - 1) continue;
                                    e += Mathf.Abs(Sample(la, px, py) - lb[i]); n++;
                                }
                            if (n < B * B / 2) { if (qx == 0 && qy == 0) ok = false; continue; }
                            e /= n;
                            if (qx == 0 && qy == 0) zero = e;
                            if (e < best) { best = e; bdx = dx; bdy = dy; }
                        }
                    if (!ok || best == float.MaxValue) continue;
                    used++;
                    corrX.Add(bdx); corrY.Add(bdy); motion.Add(mag); errRatio.Add(zero / Mathf.Max(best, 1e-6f));
                    sxm += bdx * mx; sxx += mx * mx; sym += bdy * my; syy += my * my;
                    if (map != null)
                    {
                        float c = Mathf.Sqrt(bdx * bdx + bdy * bdy);
                        var col = c <= 0.125f ? new Color(0f, 0.8f, 0f) : c <= 0.5f ? new Color(0.9f, 0.8f, 0f) : new Color(1f, 0.1f, 0f);
                        for (int y = by * B; y < by * B + B; y++) for (int x = bx * B; x < bx * B + B; x++)
                        {
                            if (x % B != 0 && y % B != 0 && x % B != B - 1 && y % B != B - 1) continue;   // outline the block
                            map.SetPixel(x, S - 1 - y, col);
                        }
                        // the correction as a line from the block centre, 8x
                        int cx = bx * B + B / 2, cy = by * B + B / 2;
                        for (int k = 0; k <= 16; k++) { int px = Mathf.Clamp(cx + Mathf.RoundToInt(bdx * 8f * k / 16f), 0, S - 1), py = Mathf.Clamp(cy + Mathf.RoundToInt(bdy * 8f * k / 16f), 0, S - 1); map.SetPixel(px, S - 1 - py, col); }
                    }
                }
            if (map != null) { File.WriteAllBytes(Path.Combine(Main.Dir, mapName), map.EncodeToPNG()); Object.Destroy(map); mapSaved = mapName.Contains("depth"); }
            if (used == 0) return "no usable blocks (too little motion or texture)";
            var mags = new List<float>();
            for (int i = 0; i < corrX.Count; i++) mags.Add(Mathf.Sqrt(corrX[i] * corrX[i] + corrY[i] * corrY[i]));
            int within = 0; foreach (var m in mags) if (m <= 0.25f) within++;
            return string.Format("blocks {0}, motion median {1:F1} px; correction median {2:F2} px, mean ({3:F2},{4:F2}) px, within 1/4 px on {5:P0}; scale error x {6:+0.0%;-0.0%} y {7:+0.0%;-0.0%}; error with no correction / best {8:F2}",
                used, Median(motion), Median(mags), Mean(corrX), Mean(corrY), (float)within / used, sxx > 0 ? sxm / sxx : 0, syy > 0 ? sym / syy : 0, Median(errRatio));
        }

        static float Median(List<float> l) { var c = new List<float>(l); c.Sort(); return c[c.Count / 2]; }
        static float Mean(List<float> l) { double s = 0; foreach (var v in l) s += v; return (float)(s / l.Count); }
    }
}
