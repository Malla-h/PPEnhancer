using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace PPUpscaler
{
    // Managed side of PPUpscalerNative.dll (native NGX bridge, ported from WotRUpscaler). All D3D11 work happens on the render thread via plugin events.
    public static class Dlss
    {
        [DllImport("PPUpscalerNative", CharSet = CharSet.Unicode)] static extern void PPUpscaler_SetLogPath(string path);
        [DllImport("PPUpscalerNative")] static extern IntPtr PPUpscaler_GetEventFunc();
        [DllImport("PPUpscalerNative")] static extern int PPUpscaler_GetStatusSlot(int slot);
        [DllImport("PPUpscalerNative")] static extern uint PPUpscaler_GetLastResult();
        [DllImport("PPUpscalerNative")] static extern int PPUpscaler_GetEvalCount();
        [DllImport("PPUpscalerNative")] static extern int PPUpscaler_StructSizes(int which);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CreateData
        {
            public IntPtr anyTexture;
            public int renderW, renderH, outW, outH, quality, flags, preset, slot;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string dir;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EvalData
        {
            public IntPtr color, depth, motion, output;
            public float jitterX, jitterY, mvScaleX, mvScaleY;
            public int reset, renderW, renderH;
            public float sharpness, preExposure;
            public float frameTimeMs;
            public int slot;
            public IntPtr exposure;
            public float exposureScale;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MvData
        {
            public IntPtr depth, mv;
            public int w, h;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] invVPj;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] vpCur;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] vpPrev;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] objDelta;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] objSphere;
            public int objOn;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CompData { public IntPtr obj, mv; public int w, h; public float scaleX, scaleY; }

        [StructLayout(LayoutKind.Sequential)]
        struct ContactData
        {
            public IntPtr color, depth, normal, output;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] invVPj;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] vpJit;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] camPos;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] camFwd;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] lightDir;
            public int w, h, frame, steps;
            public float maxDist, thickness, strength, bias;
            public int debugView, pad0, pad1, pad2;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] fogColor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] fogPrm;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] reach;
        }
        static readonly int ContactSize = Marshal.SizeOf(typeof(ContactData));
        static IntPtr csRing;
        static bool contactOk;
        // False when the native plugin in memory is older than the mod (a game restart loads the new one).
        public static bool ContactReady { get { return loaded && contactOk; } }

        // NVSDK_NGX_DLSS_Feature_Flags
        const int FlagHDR = 1 << 0, FlagMVLowRes = 1 << 1, FlagDepthInverted = 1 << 3, FlagAutoExposure = 1 << 6;

        enum St { Off, Creating, Ready, Failed }
        static St state = St.Off;
        static int cw, ch, ow, oh, cq, cp, cf, createFrame;
        static bool loaded, loadFailed;
        static IntPtr eventFn, evalRing, createRing, mvRing;
        static int ring;
        static readonly int CreateSize = Marshal.SizeOf(typeof(CreateData)), EvalSize = Marshal.SizeOf(typeof(EvalData)), MvSize = Marshal.SizeOf(typeof(MvData)), CompSize = Marshal.SizeOf(typeof(CompData));

        public static string LastFailure = "";
        public static bool Ready { get { return state == St.Ready; } }
        public static bool Failed { get { return state == St.Failed; } }
        // What the player should read about DLSS.
        public static string Status()
        {
            if (!Main.Enabled.Value) return "DLSS is off: the game renders as usual.";
            switch (state)
            {
                case St.Ready: return "DLSS is running.";
                case St.Failed: return "DLSS is NOT available: " + LastFailure + ". The game renders as usual. DLSS needs an NVIDIA RTX graphics card with a recent driver, and nvngx_dlss.dll in the mod folder. Contact shadows need DLSS to be running.";
                default: return "DLSS starts once the 3D view (tactical or geoscape) is shown.";
            }
        }

        public static int EvalCount { get { try { return loaded ? PPUpscaler_GetEvalCount() : 0; } catch { return 0; } } }

        static bool Load()
        {
            try
            {
                // The DLL itself is loaded from the mod folder by Main (LoadLibrary), so DllImport finds it by name.
                PPUpscaler_SetLogPath(Path.Combine(Main.Dir, "PPUpscaler.native.log"));
                int sc = PPUpscaler_StructSizes(0), se = PPUpscaler_StructSizes(1), sm = PPUpscaler_StructSizes(2), sp = PPUpscaler_StructSizes(3);
                if (sc != CreateSize || se != EvalSize || sm != MvSize || sp != CompSize)
                {
                    LastFailure = "struct size mismatch native " + sc + "/" + se + "/" + sm + "/" + sp + " managed " + CreateSize + "/" + EvalSize + "/" + MvSize + "/" + CompSize;
                    Main.Log(LastFailure);
                    return false;
                }
                eventFn = PPUpscaler_GetEventFunc();
                evalRing = Marshal.AllocHGlobal(EvalSize * 16);
                createRing = Marshal.AllocHGlobal(CreateSize * 4);
                mvRing = Marshal.AllocHGlobal(MvSize * 16);
                csRing = Marshal.AllocHGlobal(ContactSize * 16);
                contactOk = PPUpscaler_StructSizes(4) == ContactSize;
                if (!contactOk) Main.Log("contact shadows unavailable: the native plugin in memory is older than the mod (restart the game)");
                loaded = true;
                Main.Log("native plugin ready");
                return true;
            }
            catch (Exception e) { LastFailure = "PPUpscalerNative.dll could not be used (" + e.Message + ")"; Main.Log("native load exception: " + e); return false; }
        }

        public static bool EnsureLoaded()
        {
            if (loaded) return true;
            if (loadFailed) return false;
            if (Load()) return true;
            loadFailed = true;
            return false;
        }

        static void Issue(CommandBuffer c, int id, IntPtr data) { c.IssuePluginEventAndData(eventFn, id, data); }

        public static int QualityFor(float scale)
        {
            if (scale >= 0.999f) return 5;       // DLAA
            if (scale >= 0.62f) return 2;        // quality (also used for the 0.77 step, as NGX rejects the ultra quality value)
            if (scale >= 0.54f) return 1;        // balanced
            if (scale >= 0.4f) return 0;         // performance
            return 3;                            // ultra performance
        }

        // Drives load, (re)creation and readiness of the feature. Returns true when it can be evaluated this frame.
        public static bool Tick(int rw, int rh, int outW, int outH, RenderTexture anyTexture)
        {
            if (state == St.Failed) return false;
            if (!File.Exists(Path.Combine(Main.Dir, "nvngx_dlss.dll")))
            {
                LastFailure = "nvngx_dlss.dll is missing from the mod folder";
                Main.Log("DLSS cannot start: " + LastFailure + ". The game renders normally.");
                state = St.Failed;
                return false;
            }
            if (!EnsureLoaded()) { state = St.Failed; Main.Log("DLSS cannot start: " + LastFailure + ". The game renders normally."); return false; }
            int q = QualityFor((float)rw / outW), preset = Main.Preset.Value;
            // With the game's exposure passed in, DLSS's own auto exposure is off (it would ignore the texture anyway).
            int flags = FlagMVLowRes | FlagDepthInverted | FlagHDR | (Main.GameExposure.Value ? 0 : FlagAutoExposure);

            if (state == St.Creating)
            {
                if (Time.frameCount - createFrame < 3) return false;
                int st = PPUpscaler_GetStatusSlot(0);
                if (st == 1) { state = St.Ready; Main.Log("DLSS ready"); }
                else if (st < 0)
                {
                    state = St.Failed;
                    LastFailure = "NGX status " + st + " result 0x" + PPUpscaler_GetLastResult().ToString("X8");
                    Main.Log("DLSS cannot start: " + LastFailure + " (see PPUpscaler.native.log). The game renders normally.");
                }
                return false;
            }
            if (state == St.Ready && cw == rw && ch == rh && ow == outW && oh == outH && cq == q && cp == preset && cf == flags) return true;

            var cd = new CreateData
            {
                anyTexture = anyTexture.GetNativeTexturePtr(),
                renderW = rw, renderH = rh, outW = outW, outH = outH,
                quality = q,
                flags = flags,
                preset = preset,
                slot = 0,
                dir = Main.Dir
            };
            var p = createRing + (ring++ & 3) * CreateSize;
            Marshal.StructureToPtr(cd, p, false);
            var c = new CommandBuffer { name = "PPUpscaler create" };
            Issue(c, 1, p);
            Graphics.ExecuteCommandBuffer(c);
            c.Release();
            cw = rw; ch = rh; ow = outW; oh = outH; cq = q; cp = preset; cf = flags;
            createFrame = Time.frameCount;
            state = St.Creating;
            Main.Log("DLSS create issued " + rw + "x" + rh + " -> " + outW + "x" + outH + " quality " + q + " preset " + preset + (Main.GameExposure.Value ? ", game exposure" : ", DLSS auto exposure"));
            return false;
        }

        static float[] Flat(Matrix4x4 m)
        {
            var f = new float[16];
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) f[r * 4 + c] = m[r, c];
            return f;
        }

        // Camera-only motion vectors computed from depth (DLSS convention: previous minus current position, render pixels, y down).
        // Used by the debug motion vector check to measure the convention of Unity's own motion vectors.
        public static void QueueContact(CommandBuffer c, RenderTexture color, RenderTexture depth, RenderTexture normal, RenderTexture output, int w, int h,
            Matrix4x4 invVPj, Matrix4x4 vpJit, Vector3 camPos, Vector3 camFwd, Vector3 toSun, int frame, int steps, float maxDist, float thickness, float strength, float bias, bool debug, Color fogColor, float fogDensity, float fogStart, float fogEnd, int fogMode, float reachPx)
        {
            if (!ContactReady) return;
            var d = new ContactData
            {
                color = color.GetNativeTexturePtr(), depth = depth.GetNativeTexturePtr(), normal = normal.GetNativeTexturePtr(), output = output.GetNativeTexturePtr(),
                invVPj = Flat(invVPj), vpJit = Flat(vpJit),
                camPos = new[] { camPos.x, camPos.y, camPos.z, 0f }, camFwd = new[] { camFwd.x, camFwd.y, camFwd.z, 0f }, lightDir = new[] { toSun.x, toSun.y, toSun.z, 0f },
                w = w, h = h, frame = frame, steps = steps, maxDist = maxDist, thickness = thickness, strength = strength, bias = bias, debugView = debug ? 1 : 0,
                fogColor = new[] { fogColor.r, fogColor.g, fogColor.b, 1f }, fogPrm = new[] { fogDensity, fogStart, fogEnd, (float)fogMode }, reach = new[] { reachPx, 0f, 0f, 0f }
            };
            var p = csRing + (ring++ & 15) * ContactSize;
            Marshal.StructureToPtr(d, p, false);
            Issue(c, 12, p);
        }

        public static void QueueDepthMv(CommandBuffer c, RenderTexture depth, RenderTexture mv, int w, int h, Matrix4x4 invVPj, Matrix4x4 vpCur, Matrix4x4 vpPrev)
        {
            QueueDepthMv(c, depth, mv, w, h, invVPj, vpCur, vpPrev, Matrix4x4.identity, Vector4.zero, false);
        }

        // objDelta / objSphere / objOn: one object that moves on its own (see ObjectMotion).
        public static void QueueDepthMv(CommandBuffer c, RenderTexture depth, RenderTexture mv, int w, int h, Matrix4x4 invVPj, Matrix4x4 vpCur, Matrix4x4 vpPrev,
            Matrix4x4 objDelta, Vector4 objSphere, bool objOn)
        {
            if (!loaded) return;
            var d = new MvData { depth = depth.GetNativeTexturePtr(), mv = mv.GetNativeTexturePtr(), w = w, h = h, invVPj = Flat(invVPj), vpCur = Flat(vpCur), vpPrev = Flat(vpPrev),
                objDelta = Flat(objDelta), objSphere = new[] { objSphere.x, objSphere.y, objSphere.z, objSphere.w }, objOn = objOn ? 1 : 0 };
            var p = mvRing + (ring++ & 15) * MvSize;
            Marshal.StructureToPtr(d, p, false);
            Issue(c, 4, p);
        }

        public static void QueueEval(CommandBuffer c, RenderTexture color, RenderTexture depth, RenderTexture motion, RenderTexture output, int rw, int rh,
            Vector2 jitterPx, Vector2 mvScale, bool reset, RenderTexture exposure, float exposureScale)
        {
            var ed = new EvalData
            {
                color = color.GetNativeTexturePtr(), depth = depth.GetNativeTexturePtr(),
                motion = motion.GetNativeTexturePtr(), output = output.GetNativeTexturePtr(),
                jitterX = jitterPx.x, jitterY = jitterPx.y,
                mvScaleX = mvScale.x, mvScaleY = mvScale.y,
                reset = reset ? 1 : 0, renderW = rw, renderH = rh,
                sharpness = 0f, preExposure = 1f, frameTimeMs = Mathf.Clamp(Time.unscaledDeltaTime * 1000f, 1f, 200f),
                slot = 0,
                exposure = exposure != null && cf == (FlagMVLowRes | FlagDepthInverted | FlagHDR) ? exposure.GetNativeTexturePtr() : IntPtr.Zero,
                exposureScale = exposureScale
            };
            var p = evalRing + (ring++ & 15) * EvalSize;
            Marshal.StructureToPtr(ed, p, false);
            Issue(c, 2, p);
        }
    }
}
