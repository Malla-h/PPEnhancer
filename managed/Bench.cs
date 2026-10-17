using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace PPUpscaler
{
    // Debug benchmark: alternates DLAA off and on in the same view, with vsync off, and logs for each phase the frame time, the GPU time per
    // section of the main camera (native timestamp queries), the GPU time of the DLSS evaluate call itself, and VRAM. Keep the camera still.
    // "off" is the game's own rendering (its anti-aliasing, no motion vector pass), "dlaa" is the mod's.
    public class Bench : MonoBehaviour
    {
        [DllImport("PPUpscalerNative")] static extern IntPtr PPUpscaler_GetEventFunc();
        [DllImport("PPUpscalerNative")] static extern void PPUpscaler_SetFrameTiming(int on);
        [DllImport("PPUpscalerNative")] static extern void PPUpscaler_ResetFrameTiming();
        [DllImport("PPUpscalerNative")] static extern int PPUpscaler_GetFrameTiming(int slot, out ulong us, out ulong n);
        [DllImport("PPUpscalerNative")] static extern void PPUpscaler_ResetEvalTiming();
        [DllImport("PPUpscalerNative")] static extern int PPUpscaler_GetEvalTiming(out ulong us, out ulong n);
        [DllImport("PPUpscalerNative")] static extern int PPUpscaler_GetVramMB(out ulong usageMB, out ulong budgetMB);

        public static bool Running;
        static Bench inst;
        void Awake() { inst = this; }
        public static void RunBench() { if (inst != null && !Running) { mipMode = false; contactMode = false; inst.Begin(); } }
        // Variant: DLSS stays on, the phases switch the contact shadows off and on.
        public static void RunContactBench() { if (inst != null && !Running) { mipMode = false; contactMode = true; inst.Begin(); } }
        static bool contactMode, csWas;
        // Variant: DLSS stays on, the phases switch the texture mip bias off and on instead.
        public static void RunMipBench() { if (inst != null && !Running) { mipMode = true; contactMode = false; inst.Begin(); } }
        static bool mipMode; static int mipWas;
        static void Apply(bool on) { if (contactMode) Main.ContactShadowsOn.Value = on; else if (mipMode) Main.MipBiasMode.Value = on ? 2 : 0; else Dlaa.Suspended = !on; }
        const float Warmup = 3f, Measure = 8f;
        static readonly bool[] PhaseOn = { false, true, false, true, false, true, false };
        // Timestamp marks on the main camera; the GPU time between two marks is reported under the later one.
        static readonly CameraEvent[] MarkEvents = { CameraEvent.BeforeGBuffer, CameraEvent.AfterGBuffer, CameraEvent.AfterLighting, CameraEvent.AfterForwardOpaque,
            CameraEvent.BeforeImageEffectsOpaque, CameraEvent.BeforeImageEffects, CameraEvent.AfterImageEffects, CameraEvent.AfterEverything };
        static readonly string[] MarkNames = { "start", "gbuffer", "lighting", "fwdOpaque", "->imgFxOpaque", "->imgFx", "imgFx", "->end", "total" };

        int phase; float phaseStart; bool measuring;
        readonly List<float> frames = new List<float>(4096);
        Camera cam;
        readonly CommandBuffer[] markCbs = new CommandBuffer[8];
        RenderTexture initRt;
        int oldVsync, oldTarget;
        bool nativeOk;

        void Update()
        {
            if (!Running)
            {
                if (Main.BenchKey.Value.IsDown()) Begin();
                return;
            }
            var c = MainCam();
            if (c != cam) { Main.Log("bench: main camera changed (" + Name(cam) + " -> " + Name(c) + "), aborted"); End(); return; }
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;   // the game may set it back
            if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
            float t = Time.unscaledTime - phaseStart;
            if (!measuring && t >= Warmup)
            {
                measuring = true; frames.Clear();
                if (nativeOk) { PPUpscaler_ResetFrameTiming(); PPUpscaler_ResetEvalTiming(); }
            }
            else if (measuring) frames.Add(Time.unscaledDeltaTime * 1000f);
            if (t >= Warmup + Measure)
            {
                Report();
                phase++;
                if (phase >= PhaseOn.Length) { Main.Log("bench: done"); End(); return; }
                phaseStart = Time.unscaledTime; measuring = false;
                Apply(PhaseOn[phase]);
            }
        }

        static string Name(Camera c) { return c ? c.name : "none"; }

        static Camera MainCam()
        {
            Camera best = null;
            foreach (var c in Camera.allCameras)
                if (c.targetTexture == null && c.cullingMask != 0 && c.GetComponent<PostProcessLayer>() != null && (best == null || c.depth > best.depth)) best = c;
            return best;
        }

        void Begin()
        {
            cam = MainCam();
            if (cam == null) { Main.Log("bench: no 3D camera in this view"); return; }
            if (!Dlss.Ready) { Main.Log("bench: DLSS is not running in this view (" + (Dlss.Failed ? Dlss.LastFailure : "not started") + "), nothing to compare"); return; }
            Running = true;
            oldVsync = QualitySettings.vSyncCount; oldTarget = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            try
            {
                var func = PPUpscaler_GetEventFunc();
                initRt = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32) { name = "PPUpscaler bench" };
                initRt.Create();
                var init = new CommandBuffer { name = "PPUpscaler bench init" };
                init.IssuePluginEventAndData(func, 11, initRt.GetNativeTexturePtr());
                Graphics.ExecuteCommandBuffer(init);
                init.Release();
                PPUpscaler_SetFrameTiming(1);
                for (int i = 0; i < 8; i++)
                {
                    markCbs[i] = new CommandBuffer { name = "PPUpscaler mark " + i };
                    markCbs[i].IssuePluginEventAndData(func, 9, (IntPtr)i);
                    cam.AddCommandBuffer(MarkEvents[i], markCbs[i]);
                }
                nativeOk = true;
            }
            catch (Exception ex) { nativeOk = false; Main.Log("bench: native timing unavailable: " + ex.Message); }
            if (mipMode) mipWas = Main.MipBiasMode.Value;
            if (contactMode) csWas = Main.ContactShadowsOn.Value;
            Main.Log(string.Format("bench: {6} on {0} {1}x{2}, preset {3}, vsync was {4}, targetFrameRate was {5}", cam.name, cam.pixelWidth, cam.pixelHeight, Main.Preset.Value, oldVsync, oldTarget, contactMode ? "contact shadows off/on (DLSS " + Main.QualityNames[Mathf.Clamp(Main.Quality.Value, 0, 6)] + ")" : mipMode ? "texture mip bias off/on (DLSS " + Main.QualityNames[Mathf.Clamp(Main.Quality.Value, 0, 6)] + ")" : "DLAA on/off"));
            phase = 0; phaseStart = Time.unscaledTime; measuring = false;
            Apply(PhaseOn[0]);
        }

        void End()
        {
            Dlaa.Suspended = false;
            if (mipMode) Main.MipBiasMode.Value = mipWas;
            if (contactMode) Main.ContactShadowsOn.Value = csWas;
            if (cam != null) for (int i = 0; i < 8; i++) if (markCbs[i] != null) cam.RemoveCommandBuffer(MarkEvents[i], markCbs[i]);
            for (int i = 0; i < 8; i++) { markCbs[i]?.Release(); markCbs[i] = null; }
            if (nativeOk) PPUpscaler_SetFrameTiming(0);
            if (initRt != null) { initRt.Release(); Destroy(initRt); initRt = null; }
            QualitySettings.vSyncCount = oldVsync; Application.targetFrameRate = oldTarget;
            Running = false; cam = null;
        }

        void Report()
        {
            frames.Sort();
            float sum = 0; foreach (var f in frames) sum += f;
            int n = frames.Count;
            var layer = cam.GetComponent<PostProcessLayer>();
            string s = string.Format("bench {0} ({1}/{2}): frames={3} mean={4:F2}ms fps={5:F1} p50={6:F2} p95={7:F2} max={8:F2} gameAA={9} mv={10}",
                contactMode ? (PhaseOn[phase] ? "contact" : "no cs  ") : mipMode ? (PhaseOn[phase] ? "mip bias" : "no bias ") : PhaseOn[phase] ? "dlaa" : "off ", phase + 1, PhaseOn.Length, n, n > 0 ? sum / n : 0, n > 0 ? 1000f * n / sum : 0, n > 0 ? frames[n / 2] : 0, n > 0 ? frames[n * 95 / 100] : 0, n > 0 ? frames[n - 1] : 0,
                layer != null ? layer.antialiasingMode.ToString() : "-", (cam.depthTextureMode & DepthTextureMode.MotionVectors) != 0);
            if (nativeOk)
            {
                var g = new System.Text.StringBuilder(" gpu[ms]:");
                for (int i = 1; i <= 8; i++)
                {
                    ulong us, cnt;
                    if (PPUpscaler_GetFrameTiming(i, out us, out cnt) != 0 && cnt > 0) g.AppendFormat(" {0}={1:F2}", MarkNames[i], us / 1000.0 / cnt);
                }
                ulong eus, en;
                if (PPUpscaler_GetEvalTiming(out eus, out en) != 0 && en > 0) g.AppendFormat(" dlssEval={0:F2}", eus / 1000.0 / en);
                ulong use, bud;
                if (PPUpscaler_GetVramMB(out use, out bud) != 0) g.AppendFormat(" vram={0}/{1}MB", use, bud);
                s += g.ToString();
            }
            Main.Log(s);
        }

        void OnGUI()
        {
            if (!Running) return;
            GUI.Label(new Rect(20, 20, 900, 30), string.Format("PPUpscaler benchmark: phase {0}/{1} (DLAA {2}), keep the camera still. About {3:F0} s left.",
                phase + 1, PhaseOn.Length, PhaseOn[phase] ? "on" : "off", (PhaseOn.Length - phase) * (Warmup + Measure) - (Time.unscaledTime - phaseStart)));
        }

        void OnDestroy() { if (Running) End(); }
    }
}
