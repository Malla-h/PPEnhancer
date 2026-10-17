using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace PPUpscaler
{
    // Debug: 1:1 crops from the image centre, to judge sharpness at full resolution: DLSS's input (render size), DLSS's output and the screen
    // (output size). Saved as crop_input.png, crop_output.png and crop_screen.png in the mod folder.
    public class Crops : MonoBehaviour
    {
        const int C = 768;   // crop of the output-size images; the input crop covers the same area at render size
        static Crops inst;
        static bool pending, pendingFinal;
        static float[] lumIn, lumOut; static int sizeIn;
        void Awake() { inst = this; }
        public static void Request() { pending = true; }

        // Called in the DLSS injection after the evaluate call.
        public static void Queue(CommandBuffer cmd, RenderTexture input, RenderTexture output)
        {
            if (!pending) return;
            pending = false; pendingFinal = true;
            int ci = Mathf.Max(16, Mathf.RoundToInt(C * (float)input.width / output.width));
            lumIn = lumOut = null; sizeIn = ci;
            Crop(cmd, input, ci, "crop_input");
            Crop(cmd, output, C, "crop_output");
            if (inst != null) inst.StartCoroutine(inst.Screen());
        }

        // PPv2's final image (when it goes into a texture), recorded at the end of PPv2's command buffer. Stored as 8-bit sRGB like the screen.
        public static void QueueFinal(CommandBuffer cmd, RenderTexture final)
        {
            if (!pendingFinal || final == null) return;
            pendingFinal = false;
            var rt = new RenderTexture(C, C, 0, RenderTextureFormat.Default);
            rt.Create();
            cmd.CopyTexture(final, 0, 0, (final.width - C) / 2, (final.height - C) / 2, C, C, rt, 0, 0, 0, 0);
            cmd.RequestAsyncReadback(rt, 0, TextureFormat.RGBA32, r =>
            {
                if (!r.hasError)
                {
                    var d = r.GetData<byte>().ToArray();
                    var t = new Texture2D(C, C, TextureFormat.RGB24, false);
                    for (int y = 0; y < C; y++) for (int x = 0; x < C; x++) { int i = (y * C + x) * 4; t.SetPixel(x, C - 1 - y, new Color32(d[i], d[i + 1], d[i + 2], 255)); }
                    File.WriteAllBytes(Path.Combine(Main.Dir, "crop_final.png"), t.EncodeToPNG());
                    Destroy(t);
                    Main.Log("crops: saved crop_final.png (" + C + "x" + C + " of " + final.width + "x" + final.height + ")");
                }
                rt.Release(); Destroy(rt);
            });
        }

        static void Crop(CommandBuffer cmd, RenderTexture src, int size, string name)
        {
            var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            rt.Create();
            cmd.CopyTexture(src, 0, 0, (src.width - size) / 2, (src.height - size) / 2, size, size, rt, 0, 0, 0, 0);
            cmd.RequestAsyncReadback(rt, 0, TextureFormat.RGBAFloat, r =>
            {
                if (!r.hasError)
                {
                    var d = r.GetData<float>().ToArray();
                    // luminance in display terms, rows top to bottom (the readback is bottom-up on this API: flip like the PNG)
                    var lum = new float[size * size];
                    for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { int i = (y * size + x) * 4; lum[(size - 1 - y) * size + x] = 0.2126f * Tone(d[i]) + 0.7152f * Tone(d[i + 1]) + 0.0722f * Tone(d[i + 2]); }
                    if (name == "crop_input") lumIn = lum; else lumOut = lum;
                    var t = new Texture2D(size, size, TextureFormat.RGB24, false);
                    for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                    {
                        int i = (y * size + x) * 4;
                        t.SetPixel(x, size - 1 - y, new Color(Tone(d[i]), Tone(d[i + 1]), Tone(d[i + 2])));
                    }
                    File.WriteAllBytes(Path.Combine(Main.Dir, name + ".png"), t.EncodeToPNG());
                    Destroy(t);
                    Main.Log("crops: saved " + name + ".png (" + size + "x" + size + " of " + src.width + "x" + src.height + ")");
                }
                rt.Release(); Destroy(rt);
            });
        }

        IEnumerator Screen()
        {
            yield return new WaitForEndOfFrame();
            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            int w = shot.width, h = shot.height;
            var t = new Texture2D(C, C, TextureFormat.RGB24, false);
            var px = shot.GetPixels((w - C) / 2, (h - C) / 2, C, C);
            t.SetPixels(px);
            // rows of GetPixels go bottom-up: rebuild top to bottom
            var scr = new float[C * C];
            for (int y = 0; y < C; y++) for (int x = 0; x < C; x++) { var c = px[y * C + x]; scr[(C - 1 - y) * C + x] = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; }
            for (int k = 0; k < 60 && (lumIn == null || lumOut == null); k++) yield return null;
            if (lumIn != null && lumOut != null) Compare(scr);
            File.WriteAllBytes(Path.Combine(Main.Dir, "crop_screen.png"), t.EncodeToPNG());
            Destroy(t); Destroy(shot);
            Main.Log("crops: saved crop_screen.png (" + C + "x" + C + " of " + w + "x" + h + ")");
        }

        // Which does the screen resemble: DLSS's output, or the input enlarged? Correlation of the detail (each image minus a 5 x 5 blur of itself),
        // so brightness and contrast differences (tone mapping, post-processing) do not count; a small shift search absorbs half-pixel offsets.
        static void Compare(float[] scr)
        {
            var up = new float[C * C];
            float k = (float)sizeIn / C;
            for (int y = 0; y < C; y++) for (int x = 0; x < C; x++)
            {
                float sx = Mathf.Clamp((x + 0.5f) * k - 0.5f, 0, sizeIn - 1.001f), sy = Mathf.Clamp((y + 0.5f) * k - 0.5f, 0, sizeIn - 1.001f);
                int x0 = (int)sx, y0 = (int)sy; float fx = sx - x0, fy = sy - y0;
                float a = lumIn[y0 * sizeIn + x0], b = lumIn[y0 * sizeIn + x0 + 1], c = lumIn[(y0 + 1) * sizeIn + x0], d = lumIn[(y0 + 1) * sizeIn + x0 + 1];
                up[y * C + x] = Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
            }
            var dS = Detail(scr); var dO = Detail(lumOut); var dU = Detail(up);
            Main.Log(string.Format("crops: screen detail vs DLSS output {0:F3}, vs input enlarged {1:F3}; DLSS output vs input enlarged {2:F3}; detail strength: output {3:F4}, screen {4:F4}, input enlarged {5:F4}",
                Best(dS, dO), Best(dS, dU), Best(dO, dU), Rms(dO), Rms(dS), Rms(dU)));
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

        static float Best(float[] a, float[] b)
        {
            float best = -2f;
            for (int sy = -2; sy <= 2; sy++) for (int sx = -2; sx <= 2; sx++)
            {
                double ab = 0, aa = 0, bb = 0;
                for (int y = 8; y < C - 8; y += 2) for (int x = 8; x < C - 8; x += 2)
                {
                    float va = a[y * C + x], vb = b[(y + sy) * C + x + sx];
                    ab += va * vb; aa += va * va; bb += vb * vb;
                }
                float c = (float)(ab / System.Math.Sqrt(aa * bb + 1e-12));
                if (c > best) best = c;
            }
            return best;
        }

        static float Tone(float v) { v = Mathf.Max(0f, v); return Mathf.Pow(v / (1f + v), 1f / 2.2f); }
    }
}
