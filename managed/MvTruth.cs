using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace PPUpscaler
{
    // Debug: tests motion vectors against the rendered images. It captures two consecutive frames A and B (scene colour, before DLSS) and the motion
    // vectors of frame B, both the depth-based ones and Unity's. For each pixel of B it looks up where the vectors say it was in A and measures how well
    // the colours match, for a range of scales. Correct vectors in DLSS's convention (previous minus current, render pixels) give the smallest error at
    // scale 1; anything else shows which scale or sign they really have. Five pairs, a few frames apart, while the camera moves.
    public static class MvTruth
    {
        const int W = 960, H = 540;
        static readonly float[] Scales = { -2f, -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f, 3f };
        static int pairsLeft, wait, stage;   // stage 0: capture A at the next frame, 1: capture B
        static RenderTexture a, b, md, mu, full, dz, dzA, mf, mpre, cloudRt, outSmall, finalSmall;
        static float[] lf; static bool finalPending, finalExpected;
        static float[] cw, lo; static bool maskSaved;
        static Matrix4x4 cloudA, cloudB; static float cloudR; static Vector3 cloudC;
        static Matrix4x4 coreA, coreB; static float coreR = -1f; static Vector3 coreC; static string coreInfo = "";
        static float[] vpre;
        static float[] vf;
        static Matrix4x4 vpA, vpB, invB, vpPrevB;
        static float[] zb, za;
        static float[] la, lb, vd, vu; static int got; static int rw, rh;

        public static void Request() { pairsLeft = 5; stage = 0; wait = 120; Main.Log("mvtruth: starts in about 2 s, keep moving the camera"); }   // time to start moving after the click

        static RenderTexture Rt(RenderTextureFormat f)
        {
            var rt = new RenderTexture(W, H, 0, f, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            rt.Create();
            return rt;
        }

        // The motion texture DLSS reads, copied before the DLSS evaluate call (frame B only).
        public static void BeforeEval(CommandBuffer cmd, RenderTexture mv)
        {
            if (pairsLeft <= 0 || wait > 0 || stage != 1 || a == null) return;
            if (mpre == null) mpre = Rt(RenderTextureFormat.RGFloat);
            cmd.Blit(mv, mpre);
            cmd.RequestAsyncReadback(mpre, r => { vpre = r.hasError ? null : r.GetData<float>().ToArray(); Done(); });
        }

        // Called in the DLSS injection, every frame. depthMv: the depth-based motion (DLSS convention, pixels), or null when this camera uses Unity's.
        public static void Queue(CommandBuffer cmd, RenderTexture color, RenderTexture depthMv, RenderTexture depth, int w, int h, Matrix4x4 invVPj, Matrix4x4 vpCur, Matrix4x4 vpPrev, RenderTexture output)
        {
            if (pairsLeft <= 0) return;
            if (wait > 0) { wait--; return; }
            if (a == null) { a = Rt(RenderTextureFormat.ARGBHalf); b = Rt(RenderTextureFormat.ARGBHalf); md = Rt(RenderTextureFormat.RGFloat); mu = Rt(RenderTextureFormat.RGFloat); }
            if (stage == 0)
            {
                cmd.Blit(color, a);
                vpA = vpCur;
                var crA = CloudRenderer(); if (crA != null) cloudA = crA.transform.localToWorldMatrix;
                var coA = CoreRenderer(); if (coA != null) coreA = coA.transform.localToWorldMatrix;
                if (dzA == null) dzA = Rt(RenderTextureFormat.RFloat);
                cmd.Blit(depth, dzA);
                stage = 1;
                return;
            }
            // frame B: colour, the depth-based motion (computed now if this camera normally uses Unity's), and Unity's motion
            rw = w; rh = h;
            vpB = vpCur; invB = invVPj; vpPrevB = vpPrev;
            var coB = CoreRenderer(); if (coB != null) { coreB = coB.transform.localToWorldMatrix; var cmf = coB.GetComponent<MeshFilter>(); coreR = cmf != null && cmf.sharedMesh != null ? cmf.sharedMesh.bounds.extents.x * coB.transform.lossyScale.x : -1f; coreC = coB.transform.position; coreInfo = coB.name + " shader " + (coB.sharedMaterial != null ? coB.sharedMaterial.shader.name : "?") + " queue " + (coB.sharedMaterial != null ? coB.sharedMaterial.renderQueue : -1); }
            var crB = CloudRenderer(); if (crB != null) { cloudB = crB.transform.localToWorldMatrix; var mf = crB.GetComponent<MeshFilter>(); cloudR = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.extents.x * crB.transform.lossyScale.x : -1f; cloudC = crB.transform.position; }
            if (dz == null) dz = Rt(RenderTextureFormat.RFloat);
            cmd.Blit(depth, dz);
            cmd.Blit(color, b);
            // The native pass is run once more here into a texture of its own (never read by DLSS), next to the one DLSS uses.
            if (full == null || full.width != w || full.height != h)
            {
                if (full != null) { full.Release(); Object.Destroy(full); }
                full = new RenderTexture(w, h, 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear) { enableRandomWrite = true, name = "PPUpscaler mvtruth" };
                full.Create();
            }
            if (mf == null) mf = Rt(RenderTextureFormat.RGFloat);
            Dlss.QueueDepthMv(cmd, depth, full, w, h, invVPj, vpCur, vpPrev);
            cmd.Blit(full, mf);
            cmd.Blit(depthMv != null ? depthMv : full, md);
            cmd.Blit(BuiltinRenderTextureType.MotionVectors, mu);
            // Geoscape clouds: the cloud layer drawn alone with its own material onto black gives where the clouds are and how dense.
            if (cloudRt == null) cloudRt = Rt(RenderTextureFormat.ARGBHalf);
            cmd.SetRenderTarget(cloudRt);
            cmd.ClearRenderTarget(false, true, Color.clear);
            var cr = CloudRenderer();
            if (cr != null) for (int sm = 0; sm < cr.sharedMaterials.Length; sm++) if (cr.sharedMaterials[sm] != null) cmd.DrawRenderer(cr, cr.sharedMaterials[sm], sm, 0);
            cmd.RequestAsyncReadback(cloudRt, 0, TextureFormat.RGBAFloat, r => { cw = Lum(r); Done(); });
            if (outSmall == null) outSmall = Rt(RenderTextureFormat.ARGBHalf);
            cmd.Blit(output, outSmall);
            cmd.RequestAsyncReadback(outSmall, 0, TextureFormat.RGBAFloat, r => { lo = Lum(r); Done(); });
            finalExpected = Dlaa.LateNow; finalPending = finalExpected; lf = null;
            got = 0;
            cmd.RequestAsyncReadback(a, 0, TextureFormat.RGBAFloat, r => { la = Lum(r); Done(); });
            cmd.RequestAsyncReadback(b, 0, TextureFormat.RGBAFloat, r => { lb = Lum(r); Done(); });
            cmd.RequestAsyncReadback(md, r => { vd = r.hasError ? null : r.GetData<float>().ToArray(); Done(); });
            cmd.RequestAsyncReadback(mu, r => { vu = r.hasError ? null : r.GetData<float>().ToArray(); Done(); });
            cmd.RequestAsyncReadback(dz, r => { zb = r.hasError ? null : r.GetData<float>().ToArray(); Done(); });
            cmd.RequestAsyncReadback(dzA, r => { za = r.hasError ? null : r.GetData<float>().ToArray(); Done(); });
            cmd.RequestAsyncReadback(mf, r => { vf = r.hasError ? null : r.GetData<float>().ToArray(); Done(); });
            stage = 0; pairsLeft--; wait = 20;
        }

        static float[] Lum(AsyncGPUReadbackRequest r)
        {
            if (r.hasError) return null;
            var d = r.GetData<float>();
            var l = new float[W * H];
            for (int i = 0; i < l.Length; i++) { float v = 0.2126f * d[4 * i] + 0.7152f * d[4 * i + 1] + 0.0722f * d[4 * i + 2]; l[i] = v / (1f + v); }
            return l;
        }

        static void Done()
        {
            if (++got < (finalExpected ? 11 : 10)) return;
            if (la == null || lb == null || vd == null || vu == null || zb == null || za == null || vf == null || vpre == null || cw == null || lo == null) { Main.Log("mvtruth: readback failed"); return; }
            float k = (float)W / rw;   // render pixels -> capture pixels
            var sb = new StringBuilder("mvtruth: mean colour error of B against A moved by the vectors, per scale (lowest = the vectors' real scale)\n");
            double meanD = 0; for (int i = 0; i < W * H; i++) meanD += Mathf.Sqrt(vd[2 * i] * vd[2 * i] + vd[2 * i + 1] * vd[2 * i + 1]);
            sb.AppendFormat("  depth-based motion mean {0:F1} render px\n", meanD / (W * H));
            sb.Append("  depth-based:   "); Row(sb, vd, k, 1f, 1f);
            // Unity's: screen fractions, previous = current - motion, so in DLSS's convention it is -motion * (width, height)
            sb.Append("  Unity's:       "); Row(sb, vu, k, -rw, -rh);
            // The same motion recomputed here from frame A's and B's matrices and B's depth, and the "previous" matrix the native pass got.
            var vc = new float[W * H * 2];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    var wp = invB.MultiplyPoint(new Vector3((x + 0.5f) / W * 2f - 1f, 1f - (y + 0.5f) / H * 2f, zb[i]));
                    var pa = vpA.MultiplyPoint(wp); var pb = vpB.MultiplyPoint(wp);
                    vc[2 * i] = (pa.x - pb.x) * 0.5f * rw; vc[2 * i + 1] = -(pa.y - pb.y) * 0.5f * rh;
                }
            sb.Append("  CPU from A/B:  "); Row(sb, vc, k, 1f, 1f);
            sb.Append("  native, own texture: "); Row(sb, vf, k, 1f, 1f);
            double dfc = 0, dfd = 0; int dfn = 0;
            for (int i = 0; i < W * H; i += 7)
            {
                float ax = vf[2 * i] - vc[2 * i], ay = vf[2 * i + 1] - vc[2 * i + 1], bx = vf[2 * i] - vd[2 * i], by = vf[2 * i + 1] - vd[2 * i + 1];
                if (float.IsNaN(ax) || float.IsNaN(ay) || float.IsNaN(bx) || float.IsNaN(by)) continue;
                dfc += Mathf.Sqrt(ax * ax + ay * ay); dfd += Mathf.Sqrt(bx * bx + by * by); dfn++;
            }
            sb.AppendFormat("  native own texture vs CPU: {0:F2} px; vs the texture DLSS reads: {1:F2} px", dfn > 0 ? dfc / dfn : -1, dfn > 0 ? dfd / dfn : -1).AppendLine();
            double pc = 0, pd = 0; int pn = 0;
            for (int i = 0; i < W * H; i += 7)
            {
                float ax = vpre[2 * i] - vc[2 * i], ay = vpre[2 * i + 1] - vc[2 * i + 1], bx = vpre[2 * i] - vd[2 * i], by = vpre[2 * i + 1] - vd[2 * i + 1];
                if (float.IsNaN(ax) || float.IsNaN(ay) || float.IsNaN(bx) || float.IsNaN(by)) continue;
                pc += Mathf.Sqrt(ax * ax + ay * ay); pd += Mathf.Sqrt(bx * bx + by * by); pn++;
            }
            sb.AppendFormat("  DLSS's motion texture BEFORE the DLSS call vs CPU: {0:F2} px; before vs after the DLSS call: {1:F2} px", pn > 0 ? pc / pn : -1, pn > 0 ? pd / pn : -1).AppendLine();
            // Same, but with frame A's depth (what the native pass would compute if it ran before this frame's depth copy).
            var vs = new float[W * H * 2];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    var wp = invB.MultiplyPoint(new Vector3((x + 0.5f) / W * 2f - 1f, 1f - (y + 0.5f) / H * 2f, za[i]));
                    var pa = vpA.MultiplyPoint(wp); var pb = vpB.MultiplyPoint(wp);
                    vs[2 * i] = (pa.x - pb.x) * 0.5f * rw; vs[2 * i + 1] = -(pa.y - pb.y) * 0.5f * rh;
                }
            double ds = 0; int dsn = 0;
            for (int i = 0; i < W * H; i += 7) { float dx = vs[2 * i] - vd[2 * i], dy = vs[2 * i + 1] - vd[2 * i + 1]; if (!float.IsNaN(dx) && !float.IsNaN(dy)) { ds += Mathf.Sqrt(dx * dx + dy * dy); dsn++; } }
            sb.AppendFormat("  native depth-based vs CPU with frame A's (previous) depth: mean difference {0:F2} render px", dsn > 0 ? ds / dsn : -1).AppendLine();
            double diff = 0; int dn = 0;
            for (int i = 0; i < W * H; i += 7) { float dx = vc[2 * i] - vd[2 * i], dy = vc[2 * i + 1] - vd[2 * i + 1]; if (!float.IsNaN(dx) && !float.IsNaN(dy)) { diff += Mathf.Sqrt(dx * dx + dy * dy); dn++; } }
            sb.AppendFormat("  native depth-based vs CPU: mean difference {0:F2} render px; previous matrix given to the native pass vs frame A's: {1}", dn > 0 ? diff / dn : -1, Same(vpPrevB, vpA) ? "identical" : "DIFFERENT").AppendLine();
            Clouds(sb, k);
            LateCheck(sb);
            FitMap(sb, k);
            Core(sb, k);
            Main.Log(sb.ToString());
        }

        // Scans horizontal and vertical scales separately, since a flip may affect only one axis.
        static readonly float[] Axis = { -1.5f, -1f, -0.5f, 0f, 0.5f, 1f, 1.5f };
        static void Row(StringBuilder sb, float[] v, float k, float sx, float sy)
        {
            int bx = 0, by = 0; double bestErr = double.MaxValue;
            var errs = new double[Axis.Length, Axis.Length];
            for (int a = 0; a < Axis.Length; a++)
                for (int c = 0; c < Axis.Length; c++)
                {
                    double err = 0; int n = 0;
                    for (int y = 8; y < H - 8; y += 3)
                        for (int x = 8; x < W - 8; x += 3)
                        {
                            int i = y * W + x;
                            float px = x + Axis[a] * v[2 * i] * sx * k, py = y + Axis[c] * v[2 * i + 1] * sy * k;
                            if (px < 0 || py < 0 || px >= W - 1 || py >= H - 1) continue;
                            err += Mathf.Abs(Sample(la, px, py) - lb[i]); n++;
                        }
                    err = n > 0 ? err / n : double.MaxValue;
                    errs[a, c] = err;
                    if (err < bestErr) { bestErr = err; bx = a; by = c; }
                }
            sb.AppendFormat("best x scale {0}, y scale {1} (error {2:F4}; at x 1, y 1: {3:F4}; at x 0, y 0: {4:F4})\n", Axis[bx], Axis[by], bestErr, errs[5, 5], errs[3, 3]);
            sb.Append("                 x scan at best y: ");
            for (int a = 0; a < Axis.Length; a++) sb.AppendFormat("{0}:{1:F4} ", Axis[a], errs[a, by]);
            sb.Append("\n                 y scan at best x: ");
            for (int c = 0; c < Axis.Length; c++) sb.AppendFormat("{0}:{1:F4} ", Axis[c], errs[bx, c]);
            sb.Append("\n");
        }

        // Per pixel (over a 5x5 neighbourhood): does the image change of A to B fit the depth-based surface motion, or no motion at all
        // (content that stays in place on screen while the surface under it moves, like a reflection)? Saved once, for the first pair with motion.
        // Red: moves with the surface. Green: stays on screen. Dark: no texture to tell. Blue: the frame, for orientation.
        static bool fitSaved;
        static void FitMap(StringBuilder sb, float k)
        {
            double meanMv = 0; int c = 0;
            for (int i = 0; i < W * H; i += 13) { meanMv += Mathf.Sqrt(vd[2 * i] * vd[2 * i] + vd[2 * i + 1] * vd[2 * i + 1]); c++; }
            meanMv /= c;
            if (fitSaved || meanMv < 4.0) return;
            fitSaved = true;
            var es = new float[W * H]; var e0 = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    float px = Mathf.Clamp(x + vd[2 * i] * k, 0, W - 2), py = Mathf.Clamp(y + vd[2 * i + 1] * k, 0, H - 2);
                    es[i] = Mathf.Abs(Sample(la, px, py) - lb[i]);
                    e0[i] = Mathf.Abs(la[i] - lb[i]);
                }
            var t = new Texture2D(W, H, TextureFormat.RGB24, false);
            int surf = 0, stay = 0, globe = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float ss = 0, s0 = 0;
                    for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = Mathf.Clamp(x + dx, 0, W - 1), yy = Mathf.Clamp(y + dy, 0, H - 1), j = yy * W + xx;
                        ss += es[j]; s0 += e0[j];
                    }
                    float tot = ss + s0, r = 0f, g = 0f;
                    if (tot > 0.05f)   // enough change to tell the two apart
                    {
                        float d = (s0 - ss) / tot;   // > 0: surface motion fits better
                        if (d > 0.15f) r = Mathf.Clamp01(d * 2f); else if (d < -0.15f) g = Mathf.Clamp01(-d * 2f);
                    }
                    int i = y * W + x;
                    if (zb[i] > 0f) { globe++; if (r > 0f) surf++; if (g > 0f) stay++; }
                    t.SetPixel(x, H - 1 - y, new Color(r, g, lb[i] * 0.6f));
                }
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(Main.Dir, "motion_fit.png"), t.EncodeToPNG());
            Object.Destroy(t);
            sb.AppendFormat("  saved motion_fit.png (surface motion {0:F1} px): of the globe pixels, {1:P0} move with the surface, {2:P0} stay in place on screen",
                meanMv, globe > 0 ? (float)surf / globe : 0, globe > 0 ? (float)stay / globe : 0).AppendLine();
        }

        // DLSS before transparents: the scene just before post-processing (DLSS's output plus the transparents drawn on it), captured in the same frame.
        public static void FinalCapture(CommandBuffer cmd, RenderTargetIdentifier source)
        {
            if (!finalPending) return;
            finalPending = false;
            if (finalSmall == null) finalSmall = Rt(RenderTextureFormat.ARGBHalf);
            cmd.Blit(source, finalSmall);
            cmd.RequestAsyncReadback(finalSmall, 0, TextureFormat.RGBAFloat, r => { lf = Lum(r); Done(); });
        }

        // Where DLSS's output and the raw input differ (edges), does the final image follow DLSS's output (it reached the screen) or the raw input?
        static void LateCheck(StringBuilder sb)
        {
            if (!finalExpected) return;
            if (lf == null) { sb.Append("  before-transparents check: no final capture").AppendLine(); return; }
            int edges = 0, followOut = 0, followIn = 0;
            for (int i = 0; i < W * H; i += 3)
            {
                float dOI = Mathf.Abs(lo[i] - lb[i]);
                if (dOI < 0.02f) continue;
                edges++;
                float dFO = Mathf.Abs(lf[i] - lo[i]), dFI = Mathf.Abs(lf[i] - lb[i]);
                if (dFO < dFI * 0.5f) followOut++; else if (dFI < dFO * 0.5f) followIn++;
            }
            sb.AppendFormat("  before-transparents check: of {0} pixels where DLSS changed the image, the final image follows DLSS's output on {1:P0}, the raw input on {2:P0}",
                edges, edges > 0 ? (float)followOut / edges : 0, edges > 0 ? (float)followIn / edges : 0).AppendLine();
        }

        static Renderer CoreRenderer()
        {
            var g = GameObject.Find("Geoscape");
            var t = g != null ? g.transform.Find("XRotation/YRotation/GeoscapeVisual_Showroom/Core") : null;
            return t != null ? t.GetComponent<Renderer>() : null;
        }

        // Geoscape "Core": an opaque object that turns on its own. Which pixels lie on its surface (from the depth), and how well camera-only motion
        // fits them compared with motion that includes its own turning.
        static void Core(StringBuilder sb, float k)
        {
            if (coreR <= 0f) { sb.Append("  core: not found").AppendLine(); return; }
            float rot = Quaternion.Angle(coreA.rotation, coreB.rotation);
            var toLocal = coreB.inverse;
            double eCam = 0, eObj = 0, eNone = 0; int n = 0, onGlobe = 0, other = 0, sky = 0;
            for (int y = 8; y < H - 8; y += 2)
                for (int x = 8; x < W - 8; x += 2)
                {
                    int i = y * W + x;
                    float z = zb[i];
                    if (z <= 0f) { sky++; continue; }
                    var P = invB.MultiplyPoint(new Vector3((x + 0.5f) / W * 2f - 1f, 1f - (y + 0.5f) / H * 2f, z));
                    float dG = Mathf.Abs(Vector3.Distance(P, Vector3.zero) - 6.401f), dC = Mathf.Abs(Vector3.Distance(P, coreC) - coreR);
                    if (dG < 0.05f) { onGlobe++; continue; }
                    if (dC > 0.02f * coreR) { other++; continue; }
                    var Pprev = coreA.MultiplyPoint(toLocal.MultiplyPoint(P));
                    var pa = vpA.MultiplyPoint(Pprev); var pb = vpB.MultiplyPoint(P);
                    float ox = (pa.x - pb.x) * 0.5f * rw, oy = -(pa.y - pb.y) * 0.5f * rh;
                    float cx = x + vd[2 * i] * k, cy = y + vd[2 * i + 1] * k, qx = x + ox * k, qy = y + oy * k;
                    if (cx < 0 || cy < 0 || cx >= W - 1 || cy >= H - 1 || qx < 0 || qy < 0 || qx >= W - 1 || qy >= H - 1) continue;
                    eCam += Mathf.Abs(Sample(la, cx, cy) - lb[i]); eObj += Mathf.Abs(Sample(la, qx, qy) - lb[i]); eNone += Mathf.Abs(la[i] - lb[i]); n++;
                }
            int tot = n + onGlobe + other + sky;
            // Distances from the globe centre rebuilt from depth: the core (radius about 6.38) and the globe surface (6.401) should form two peaks
            // if both write depth.
            var bins = new int[25];   // 6.360 .. 6.410 in steps of 0.002
            int below = 0, above = 0;
            for (int y = 8; y < H - 8; y += 2)
                for (int x = 8; x < W - 8; x += 2)
                {
                    int i = y * W + x;
                    if (zb[i] <= 0f) continue;
                    float d = Vector3.Distance(invB.MultiplyPoint(new Vector3((x + 0.5f) / W * 2f - 1f, 1f - (y + 0.5f) / H * 2f, zb[i])), Vector3.zero);
                    int bi = Mathf.FloorToInt((d - 6.36f) / 0.002f);
                    if (bi < 0) below++; else if (bi >= bins.Length) above++; else bins[bi]++;
                }
            sb.Append("  distance from the globe centre (depth): <6.360:").Append(below);
            for (int bi = 0; bi < bins.Length; bi++) if (bins[bi] > 0) sb.AppendFormat(" {0:F3}:{1}", 6.36f + bi * 0.002f, bins[bi]);
            sb.Append(" >6.410:").Append(above).AppendLine();
            sb.AppendFormat("  core: {0}, radius {1:F2} at {2}, turned {3:F4} degrees A to B; pixels: on core {4:P0}, on globe {5:P0}, other {6:P0}, no depth {7:P0}; fit on core pixels: camera-only {8:F4}, with its turning {9:F4}, no motion {10:F4}",
                coreInfo, coreR, coreC, rot, tot > 0 ? (float)n / tot : 0, tot > 0 ? (float)onGlobe / tot : 0, tot > 0 ? (float)other / tot : 0, tot > 0 ? (float)sky / tot : 0,
                n > 0 ? eCam / n : -1, n > 0 ? eObj / n : -1, n > 0 ? eNone / n : -1).AppendLine();
        }

        static Renderer CloudRenderer()
        {
            var g = GameObject.Find("Geoscape");
            var t = g != null ? g.transform.Find("XRotation/YRotation/GeoscapeVisual_Showroom/Atmosphere_4_clouds") : null;
            return t != null ? t.GetComponent<Renderer>() : null;
        }

        // Cloudy against clear globe pixels: how well the motion DLSS gets fits the image (at its own scale and with no motion at all), and how far
        // DLSS's output is from the current input frame (ghosting shows as output lagging behind the input).
        static void Clouds(StringBuilder sb, float k)
        {
            float cmax = 0f; foreach (var v in cw) if (v > cmax) cmax = v;
            if (cmax <= 0f) { sb.Append("  clouds: none drawn (not in geoscape?)").AppendLine(); return; }
            double[] e1 = new double[2], e0 = new double[2], gh = new double[2]; int[] n = new int[2]; int globe = 0;
            for (int y = 8; y < H - 8; y += 2)
                for (int x = 8; x < W - 8; x += 2)
                {
                    int i = y * W + x;
                    if (zb[i] <= 0f) continue;
                    globe++;
                    float c = cw[i] / cmax;
                    int reg = c > 0.3f ? 0 : c < 0.05f ? 1 : -1;
                    if (reg < 0) continue;
                    float px = x + vd[2 * i] * k, py = y + vd[2 * i + 1] * k;
                    if (px < 0 || py < 0 || px >= W - 1 || py >= H - 1) continue;
                    e1[reg] += Mathf.Abs(Sample(la, px, py) - lb[i]);
                    e0[reg] += Mathf.Abs(la[i] - lb[i]);
                    gh[reg] += Mathf.Abs(lo[i] - lb[i]);
                    n[reg]++;
                }
            // Cloud motion: the point where the view ray meets the cloud sphere, carried by the cloud layer's own movement from frame A to B.
            var vcl = new float[W * H * 2]; int hit = 0;
            var toLocal = cloudB.inverse;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    float nx = (x + 0.5f) / W * 2f - 1f, ny = 1f - (y + 0.5f) / H * 2f;
                    var o = invB.MultiplyPoint(new Vector3(nx, ny, 1f));
                    var dir = (invB.MultiplyPoint(new Vector3(nx, ny, 0.5f)) - o).normalized;
                    var oc = o - cloudC; float bq = Vector3.Dot(oc, dir), cq = oc.sqrMagnitude - cloudR * cloudR, disc = bq * bq - cq;
                    if (cloudR <= 0f || disc < 0f) { vcl[2 * i] = vd[2 * i]; vcl[2 * i + 1] = vd[2 * i + 1]; continue; }
                    float t = -bq - Mathf.Sqrt(disc); if (t < 0f) t = -bq + Mathf.Sqrt(disc);
                    var P = o + dir * t;
                    var Pprev = cloudA.MultiplyPoint(toLocal.MultiplyPoint(P));
                    var pa = vpA.MultiplyPoint(Pprev); var pb = vpB.MultiplyPoint(P);
                    vcl[2 * i] = (pa.x - pb.x) * 0.5f * rw; vcl[2 * i + 1] = -(pa.y - pb.y) * 0.5f * rh; hit++;
                }
            double ec = 0; int en = 0;
            for (int y = 8; y < H - 8; y += 2)
                for (int x = 8; x < W - 8; x += 2)
                {
                    int i = y * W + x;
                    if (zb[i] <= 0f || cw[i] / cmax <= 0.3f) continue;
                    float px = x + vcl[2 * i] * k, py = y + vcl[2 * i + 1] * k;
                    if (px < 0 || py < 0 || px >= W - 1 || py >= H - 1) continue;
                    ec += Mathf.Abs(Sample(la, px, py) - lb[i]); en++;
                }
            float rot = Quaternion.Angle(cloudA.rotation, cloudB.rotation);
            sb.AppendFormat("  cloud sphere radius {0:F3} (globe 6.401), cloud layer turned {1:F4} degrees from A to B; motion fit on cloudy pixels with cloud motion: {2:F4}",
                cloudR, rot, en > 0 ? ec / en : -1).AppendLine();
            sb.AppendFormat("  clouds cover {0:P0} of the globe; motion fit with DLSS's vectors: cloudy {1:F4}, clear {2:F4} (with no motion: cloudy {3:F4}, clear {4:F4}); DLSS output vs current input: cloudy {5:F4}, clear {6:F4}",
                globe > 0 ? (float)n[0] / (globe / 1f) : 0f, n[0] > 0 ? e1[0] / n[0] : -1, n[1] > 0 ? e1[1] / n[1] : -1, n[0] > 0 ? e0[0] / n[0] : -1, n[1] > 0 ? e0[1] / n[1] : -1,
                n[0] > 0 ? gh[0] / n[0] : -1, n[1] > 0 ? gh[1] / n[1] : -1).AppendLine();
            if (!maskSaved)
            {
                maskSaved = true;
                var t = new Texture2D(W, H, TextureFormat.RGB24, false);
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { float c = cw[y * W + x] / cmax; float g = lb[y * W + x]; t.SetPixel(x, H - 1 - y, new Color(c, g, g)); }
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(Main.Dir, "clouds_mask.png"), t.EncodeToPNG());
                Object.Destroy(t);
                sb.Append("  saved clouds_mask.png (red = clouds, green/blue = the frame)").AppendLine();
            }
        }

        static bool Same(Matrix4x4 p, Matrix4x4 q) { for (int i = 0; i < 16; i++) if (Mathf.Abs(p[i] - q[i]) > 1e-6f) return false; return true; }

        static float Sample(float[] l, float x, float y)
        {
            int x0 = (int)x, y0 = (int)y; float fx = x - x0, fy = y - y0;
            int i = y0 * W + x0;
            return Mathf.Lerp(Mathf.Lerp(l[i], l[i + 1], fx), Mathf.Lerp(l[i + W], l[i + W + 1], fx), fy);
        }
    }
}
