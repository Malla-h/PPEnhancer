using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace PPUpscaler
{
    // Debug: saves what DLSS receives in one frame as images in the mod folder: the scene colour (dlss_color.png), the motion vectors
    // (dlss_motion.png: red and green = x and y motion centred on grey = no motion, 1 px = 32 levels; Unity's sign, or DLSS's for depth-based motion) and the depth (dlss_depth.png),
    // and logs a 16 x 9 grid of the average motion in pixels.
    public static class MvImage
    {
        const int W = 960, H = 540;
        static bool pending;
        static bool pendingOut, pendingFinal;
        public static void Request() { pending = true; pendingOut = true; pendingFinal = true; }

        // The DLSS output (HDR, before the rest of post-processing) and the final image, as PNGs: the stages after the input.
        public static void QueueOutput(CommandBuffer cmd, RenderTexture output)
        {
            if (!pendingOut) return;
            pendingOut = false;
            Save(cmd, output, "dlss_output", true);
        }

        public static void QueueFinal(CommandBuffer cmd, RenderTexture final)
        {
            if (!pendingFinal) return;
            pendingFinal = false;
            Save(cmd, final, "dlss_final", false);
        }

        static void Save(CommandBuffer cmd, RenderTexture src, string name, bool hdr)
        {
            // HDR images are tone mapped here for viewing; the final image (an sRGB texture) is read back as the 8-bit values the screen gets.
            var t = hdr ? RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) : RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.Default);
            cmd.Blit(src, t);
            cmd.RequestAsyncReadback(t, 0, hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, r =>
            {
                if (!r.hasError)
                {
                    var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                    if (hdr)
                    {
                        var d = r.GetData<float>().ToArray();
                        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i2 = (y * W + x) * 4; tex.SetPixel(x, H - 1 - y, new Color(Tone(d[i2]), Tone(d[i2 + 1]), Tone(d[i2 + 2]))); }
                    }
                    else
                    {
                        var d = r.GetData<byte>().ToArray();
                        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i2 = (y * W + x) * 4; tex.SetPixel(x, H - 1 - y, new Color32(d[i2], d[i2 + 1], d[i2 + 2], 255)); }
                    }
                    File.WriteAllBytes(Path.Combine(Main.Dir, name + suffix + ".png"), tex.EncodeToPNG());
                    Object.Destroy(tex);
                    Main.Log("mvimage: saved " + name + suffix + ".png");
                }
                RenderTexture.ReleaseTemporary(t);
            });
        }

        static Matrix4x4 invVPj;
        static string suffix = "";

        public static void Queue(CommandBuffer cmd, RenderTexture color, RenderTexture mv, RenderTexture depth, int rw, int rh, Matrix4x4 invVPjittered)
        {
            if (!pending) return;
            invVPj = invVPjittered;
            suffix = Dlaa.LateNow ? "_opaque_only" : "_all";   // DLSS before transparents: the capture holds the opaque scene only
            pending = false;
            var c = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var m = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear);
            var d = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            m.filterMode = FilterMode.Point; d.filterMode = FilterMode.Point;
            cmd.Blit(color, c); cmd.Blit(mv, m); cmd.Blit(depth, d);
            float[] col = null, mot = null, dep = null; int got = 0;
            System.Action done = () =>
            {
                if (++got < 3) return;
                RenderTexture.ReleaseTemporary(c); RenderTexture.ReleaseTemporary(m); RenderTexture.ReleaseTemporary(d);
                if (col == null || mot == null || dep == null) { Main.Log("mvimage: readback failed"); return; }
                Save(col, mot, dep, rw, rh);
            };
            cmd.RequestAsyncReadback(c, 0, TextureFormat.RGBAFloat, r => { if (!r.hasError) col = r.GetData<float>().ToArray(); done(); });
            cmd.RequestAsyncReadback(m, r => { if (!r.hasError) mot = r.GetData<float>().ToArray(); done(); });
            cmd.RequestAsyncReadback(d, r => { if (!r.hasError) dep = r.GetData<float>().ToArray(); done(); });
        }

        static void Save(float[] col, float[] mot, float[] dep, int rw, int rh)
        {
            var ct = new Texture2D(W, H, TextureFormat.RGB24, false);
            var mt = new Texture2D(W, H, TextureFormat.RGB24, false);
            var dt = new Texture2D(W, H, TextureFormat.RGB24, false);
            float dmin = float.MaxValue, dmax = float.MinValue;
            foreach (var z in dep) if (z > 0f) { if (z < dmin) dmin = z; if (z > dmax) dmax = z; }
            var gx = new double[16 * 9]; var gy = new double[16 * 9]; var gn = new int[16 * 9];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    // Rows of the readback run in the texture's memory order; on D3D11 that is top-down, so row y is image row y from the top.
                    int py = H - 1 - y;   // Texture2D rows go bottom-up
                    float r = col[4 * i], g = col[4 * i + 1], b = col[4 * i + 2];
                    ct.SetPixel(x, py, new Color(Tone(r), Tone(g), Tone(b)));
                    float mx = mot[2 * i] * rw, my = mot[2 * i + 1] * rh;   // render pixels (rw = rh = 1 when the texture already holds pixels)
                    mt.SetPixel(x, py, new Color(Mathf.Clamp01(0.5f + mx / 8f), Mathf.Clamp01(0.5f + my / 8f), Mathf.Clamp01(Mathf.Sqrt(mx * mx + my * my) / 4f)));
                    float z = dep[i];
                    float zn = z <= 0f ? 0f : (dmax > dmin ? (z - dmin) / (dmax - dmin) : 1f);
                    dt.SetPixel(x, py, new Color(zn, zn, zn));
                    int cell = (y * 9 / H) * 16 + x * 16 / W;
                    gx[cell] += mx; gy[cell] += my; gn[cell]++;
                }
            GlobeCheck(dep);
            File.WriteAllBytes(Path.Combine(Main.Dir, "dlss_color" + suffix + ".png"), ct.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(Main.Dir, "dlss_motion" + suffix + ".png"), mt.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(Main.Dir, "dlss_depth" + suffix + ".png"), dt.EncodeToPNG());
            Object.Destroy(ct); Object.Destroy(mt); Object.Destroy(dt);
            var sb = new StringBuilder("mvimage: saved dlss_color/motion/depth" + suffix + ".png; depth range " + dmin.ToString("G4") + ".." + dmax.ToString("G4") + "; average Unity motion per cell in render pixels (x,y), rows top to bottom:\n");
            for (int row = 0; row < 9; row++)
            {
                sb.Append("  ");
                for (int cx = 0; cx < 16; cx++) { int k = row * 16 + cx; sb.AppendFormat("{0,5:F1},{1,5:F1} ", gx[k] / gn[k], gy[k] / gn[k]); }
                sb.Append('\n');
            }
            Main.Log(sb.ToString());
        }

        // Geoscape: rebuild world positions from the depth and compare their distance from the globe's centre with the globe's radius.
        // If the depth really is the globe's, every point on it lies at the radius.
        static void GlobeCheck(float[] dep)
        {
            var g = GameObject.Find("Geoscape");
            var t = g != null ? g.transform.Find("XRotation/YRotation/GeoscapeVisual_Showroom/8k8kGlobe/Sphere001") : null;
            if (t == null) return;
            var r = t.GetComponent<Renderer>();
            var mf = t.GetComponent<MeshFilter>();
            Vector3 c = r.bounds.center;
            float meshR = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.extents.x * t.lossyScale.x : -1f;
            var sb = new StringBuilder();
            float mn = float.MaxValue, mx = float.MinValue; double sum = 0; int n = 0;
            for (int gy = 1; gy < 8; gy++)
                for (int gx = 1; gx < 8; gx++)
                {
                    int x = gx * W / 8, y = gy * H / 8;
                    float z = dep[y * W + x];
                    if (z <= 0f) continue;
                    var ndc = new Vector3((x + 0.5f) / W * 2f - 1f, 1f - (y + 0.5f) / H * 2f, z);
                    var wp = invVPj.MultiplyPoint(ndc);
                    float d = Vector3.Distance(wp, c);
                    mn = Mathf.Min(mn, d); mx = Mathf.Max(mx, d); sum += d; n++;
                }
            Main.Log(string.Format("globecheck: globe centre {0} bounds radius {1:F3} mesh radius {2:F3}; points rebuilt from depth (n={3}) lie at {4:F3}..{5:F3} (mean {6:F3}) from the centre; camera at {7:F3} from the centre",
                c, r.bounds.extents.x, meshR, n, mn, mx, n > 0 ? sum / n : 0, CamDist(c)));
        }

        static float CamDist(Vector3 c)
        {
            foreach (var cam in Camera.allCameras) if (cam.name == "GeoscapeCamera") return Vector3.Distance(cam.transform.position, c);
            return -1f;
        }

        static float Tone(float v) { v = Mathf.Max(0f, v); return Mathf.Pow(v / (1f + v), 1f / 2.2f); }
    }
}
