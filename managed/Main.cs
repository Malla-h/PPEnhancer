using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PPUpscaler
{
    // The mod's core. It is not a BepInEx plugin itself: the small loader plugin (PPUpscaler.dll) reads PPUpscaler.Core.dll from bytes and calls
    // Start, and with HotReload on it calls Stop and starts a new build whenever the file changes, so code changes need no game restart.
    public static class Main
    {
        public const string Guid = "mallahowl.ppenhancer";
        public static string Dir;
        public static ConfigEntry<bool> MvPass, MvReadback;
        public static ConfigEntry<KeyboardShortcut> BenchKey, MenuKey, ContactShadowsKey;
        public static ConfigEntry<bool> DeveloperOptions;
        public static ConfigEntry<bool> Enabled, DisableGameAA, NoJitter, MvCheckOn, JitterTransparents, CoreMotion, GameExposure, AoAtOutputSize, ContactShadowsOn, CsDebug, CsStats;
        public static ConfigEntry<int> Preset, Quality, MipBiasMode, FrameRateLimit, CsStage;
        public static ConfigEntry<float> CustomScale;
        public static ConfigEntry<string> Cameras, DepthMotionCameras, LateTransparentCameras, ContactShadowCameras;
        public static ConfigEntry<float> JitterSignX, JitterSignY, MvScaleX, MvScaleY, CutDistance, CutAngle, MipBiasStrength, CsStrength, CsDistance, CsThickness, CsBias, CsReach;
        public static ConfigEntry<int> CsSteps;
        static string logPath;
        static DateTime cfgTime; static float cfgNext;
        static ConfigFile Config;
        static GameObject root;
        static Harmony harmony;

        public static readonly string[] QualityNames = { "Native (DLAA)", "Ultra Quality", "Quality", "Balanced", "Performance", "Ultra Perf.", "Custom" };
        static readonly float[] QualityScales = { 1f, 0.77f, 0.6667f, 0.58f, 0.5f, 0.3333f };
        public static float ScaleNow()
        {
            int q = Quality.Value;
            return q >= 0 && q < QualityScales.Length ? QualityScales[q] : Mathf.Clamp(CustomScale.Value, 0.33f, 1f);
        }

        // Picks up edits of the config file while the game runs (checked once per second), so settings can be tried without a restart.
        public static void Tick()
        {
            // The game sets 60 when it applies its video settings; keep the chosen limit (the benchmark lifts it on its own while it runs).
            int fl = FrameRateLimit.Value;
            int want = fl < 0 ? -1 : fl == 0 ? 60 : fl;
            if (!Bench.Running && Application.targetFrameRate != want) Application.targetFrameRate = want;
            if (Time.unscaledTime < cfgNext) return;
            cfgNext = Time.unscaledTime + 1f;
            try
            {
                var t = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                if (cfgTime == default(DateTime)) { cfgTime = t; return; }
                if (t == cfgTime) return;
                cfgTime = t;
                Config.Reload();
                Log(string.Format("config reloaded: enabled={0} preset={1} jitterSign=({2},{3}) mvScale=({4},{5}) noJitter={6}",
                    Enabled.Value, Preset.Value, JitterSignX.Value, JitterSignY.Value, MvScaleX.Value, MvScaleY.Value, NoJitter.Value));
            }
            catch (Exception e) { Log("config reload failed: " + e.Message); }
        }

        // Puts settings back to their defaults. Keys, the developer switch and the hot reload switch are left alone.
        public static void ResetSettings(bool contactShadowsOnly)
        {
            var keep = new System.Collections.Generic.HashSet<string> { "MenuKey", "ToggleKey", "BenchmarkKey", "DeveloperOptions", "HotReload" };
            var contact = new System.Collections.Generic.HashSet<string> { "Strength", "Distance", "Thickness", "Bias", "Steps", "ScreenReach" };
            foreach (var def in new System.Collections.Generic.List<ConfigDefinition>(Config.Keys))
            {
                if (keep.Contains(def.Key)) continue;
                if (contactShadowsOnly && !(def.Section == "ContactShadows" && contact.Contains(def.Key))) continue;
                var e = Config[def];
                e.BoxedValue = e.DefaultValue;
            }
            Log(contactShadowsOnly ? "contact shadow settings reset" : "all settings reset to their defaults");
        }

        public static void Start(BaseUnityPlugin host, string dir)
        {
            Config = host.Config;
            Dir = dir;
            logPath = Path.Combine(Dir, "PPUpscaler.log");
            try   // keep the log small: the previous one is kept as .old once it passes 1 MB
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024) { var old = logPath + ".old"; if (File.Exists(old)) File.Delete(old); File.Move(logPath, old); }
            }
            catch { }
            MvPass = Config.Bind("Probe", "MotionVectorPass", false, "Recon: turn on the game's motion vector pass and copy it (false = no extra rendering at all)");
            MvReadback = Config.Bind("Probe", "MotionVectorReadback", false, "Recon: read the copy back once per second and log statistics");
            FrameRateLimit = Config.Bind("General", "FrameRateLimit", 0, "The game limits itself to 60 frames per second, whatever its VSync setting. 0 = leave the game's 60, -1 = no limit (VSync still applies when it is on), or a number of frames per second.");
            ContactShadowsKey = Config.Bind("ContactShadows", "ToggleKey", KeyboardShortcut.Empty, "Key that switches contact shadows on and off, to compare. Not bound by default (the game uses many keys).");
            MenuKey = Config.Bind("General", "MenuKey", new KeyboardShortcut(KeyCode.F11), "Opens and closes the settings window.");
            DeveloperOptions = Config.Bind("Debug", "DeveloperOptions", false, "Shows the developer options (jitter and motion vector tuning, checks, benchmark) in the settings window.");
            Enabled = Config.Bind("DLSS", "Enabled", true, "Anti-alias the 3D view with NVIDIA DLAA (DLSS at native resolution). Off: the game renders as usual.");
            Preset = Config.Bind("DLSS", "Preset", 0, "DLSS preset: 0 = automatic (NVIDIA decides), 10 = J, 11 = K, 12 = L, 13 = M. L and M cost much more GPU time.");
            Quality = Config.Bind("DLSS", "QualityMode", 0, "Render resolution of the 3D view as a fraction of the screen: 0 = Native (DLAA, anti-aliasing only), 1 = Ultra Quality (0.77), 2 = Quality (0.67), 3 = Balanced (0.58), 4 = Performance (0.5), 5 = Ultra Performance (0.33), 6 = Custom (CustomScale).");
            CustomScale = Config.Bind("DLSS", "CustomScale", 0.75f, "Render scale for quality mode 6 (Custom), 0.33 to 1.");
            DisableGameAA = Config.Bind("DLSS", "DisableGameAA", true, "Switch the game's SMAA off while DLSS runs (it would only soften what DLSS receives).");
            Cameras = Config.Bind("DLSS", "Cameras", "MainCamera,GeoscapeCamera", "Names of the game cameras DLSS applies to, separated by commas.");
            DepthMotionCameras = Config.Bind("DLSS", "DepthMotionCameras", "GeoscapeCamera", "Cameras that get camera motion computed from depth instead of Unity's motion vectors (measured wrong in geoscape), separated by commas.");
            LateTransparentCameras = Config.Bind("DLSS", "DlssBeforeTransparentsCameras", "GeoscapeCamera", "Cameras on which DLSS runs before transparent objects (clouds, atmosphere, stars, icons, particles), which are then drawn on its output unjittered, separated by commas.");
            MipBiasMode = Config.Bind("DLSS", "TextureMipBiasMode", 1, "Texture sharpness while DLSS renders below native: 0 = off, 1 = match native (bias log2(render scale): 0 at DLAA, -1 at Performance), 2 = NVIDIA's recommendation (one level sharper still: -1 at DLAA, -2 at Performance). Measured at 4K DLAA: level 2 doubles the G-buffer pass (+4 ms).");
            MipBiasStrength = Config.Bind("Debug", "MipBiasStrength", 1f, "Debug: fraction of the recommended texture mip bias (1 = log2(render scale) - 1).");
            CoreMotion = Config.Bind("DLSS", "GeoscapeCoreMotion", true, "Geoscape: give the turning starry layer inside the globe (the core, seen through the oceans) its own motion.");
            GameExposure = Config.Bind("DLSS", "GameExposure", true, "Give DLSS the exposure the game applies before tonemapping (its auto exposure and post exposure) instead of letting DLSS estimate it. Keeps highlights bright.");
            AoAtOutputSize = Config.Bind("DLSS", "AmbientOcclusionAtOutputSize", true, "Below native, compute the game's ambient occlusion at the output size, so it reaches as far as at native (it measures its reach in pixels).");
            ContactShadowsOn = Config.Bind("ContactShadows", "Enabled", true, "Contact shadows: short screen-space shadows toward the sun that the game's shadow maps miss (debris, grass, props, feet). Drawn before smoke and effects.");
            ContactShadowCameras = Config.Bind("ContactShadows", "Cameras", "MainCamera", "Cameras that get contact shadows (the tactical camera), separated by commas.");
            CsStrength = Config.Bind("ContactShadows", "Strength", 0.5f, "How dark a contact shadow gets, 0 to 1. Not 1: the sun's share of the light cannot be separated here, so the shadow is a moderate darkening.");
            CsDistance = Config.Bind("ContactShadows", "Distance", 0.7f, "How far toward the sun a surface looks for something blocking it, in world units.");
            CsThickness = Config.Bind("ContactShadows", "Thickness", 0.5f, "Surfaces further in front of the ray than this do not cast a shadow (in world units), so a faraway foreground object does not shadow the ground behind it.");
            CsBias = Config.Bind("ContactShadows", "Bias", 0.03f, "Lifts the start of the march off the surface, to avoid self-shadowing (world units).");
            CsReach = Config.Bind("ContactShadows", "ScreenReach", 0.06f, "The longest a contact shadow may reach on the screen, as a share of the screen height. When you zoom in, Distance would cover too many pixels for the few steps, so the reach is shortened to this.");
            CsSteps = Config.Bind("ContactShadows", "Steps", 8, "Samples along the march. More is smoother and costs more.");
            CsStage = Config.Bind("ContactShadows", "Stage", 0, "0 = right after lighting, before the fog, ambient occlusion and fog of war (they then cover the shadows correctly), 1 = after them (the shadows darken the fog and the fog of war too), 2 = after the forward-rendered objects (Unity skips that stage on frames without any, so the shadows vanish at times).");
            CsStats = Config.Bind("Debug", "ContactShadowStats", false, "Debug: twice a second, log how much of the screen the contact shadows cover, with the camera position and field of view.");
            CsDebug = Config.Bind("Debug", "ContactShadowsOnly", false, "Debug: show the contact shadow term alone (white = lit, dark = shadow).");
            JitterSignX = Config.Bind("Debug", "JitterSignX", -1f, "Debug: sign of the horizontal jitter passed to DLSS.");
            JitterSignY = Config.Bind("Debug", "JitterSignY", -1f, "Debug: sign of the vertical jitter passed to DLSS.");
            MvScaleX = Config.Bind("Debug", "MvScaleX", -1f, "Debug: horizontal motion vector scale, in multiples of the width (Unity's motion vectors are in screen fractions).");
            MvScaleY = Config.Bind("Debug", "MvScaleY", -1f, "Debug: vertical motion vector scale, in multiples of the height.");
            JitterTransparents = Config.Bind("Debug", "JitterTransparents", true, "Debug: jitter transparent objects too (holograms, icons, clouds). They have no motion vectors, so the jitter can show as vibration.");
            NoJitter = Config.Bind("Debug", "NoJitter", false, "Debug: no camera jitter (DLSS then cannot recover sub-pixel detail).");
            MvCheckOn = Config.Bind("Debug", "MotionVectorCheck", false, "Debug: once per second while the camera moves, compare Unity's motion vectors with ones computed from depth and log the result.");
            CutDistance = Config.Bind("Debug", "CutDistance", 3f, "Camera movement in one frame (world units) treated as a cut, which restarts the DLSS history.");
            CutAngle = Config.Bind("Debug", "CutAngle", 15f, "Camera rotation in one frame (degrees) treated as a cut.");
            BenchKey = Config.Bind("Debug", "BenchmarkKey", KeyboardShortcut.Empty, "Debug: starts the DLAA on/off benchmark (about 80 s, keep the camera still). Not bound by default.");
            try
            {
                Log("core started: " + typeof(Main).Assembly.GetName().Name + " (file written " + File.GetLastWriteTime(Path.Combine(Dir, "PPUpscaler.Core.dll")).ToString("HH:mm:ss") + ")");
                cfgTime = default(DateTime);
                root = new GameObject("PPUpscaler core");
                root.transform.SetParent(host.transform, false);   // the BepInEx manager object lives for the whole game
                root.AddComponent<CoreTick>();
                root.AddComponent<CamLog>();
                root.AddComponent<MvProbe>();
                var native = Path.Combine(Dir, "PPUpscalerNative.dll");
                if (LoadLibrary(native) == IntPtr.Zero) Log("native plugin not loaded: " + native + " (error " + Marshal.GetLastWin32Error() + ")");
                root.AddComponent<Bench>();
                root.AddComponent<Menu>();
                root.AddComponent<SceneDump>();
                root.AddComponent<MipBias>();
                root.AddComponent<MotionCensus>();
                root.AddComponent<Crops>();
                root.AddComponent<QualityTest>();
                root.AddComponent<MipStress>();
                harmony = new Harmony(Guid + ".core." + DateTime.Now.Ticks);
                Dlaa.Install(harmony);
                Log("loaded");
            }
            catch (Exception ex) { Log("load failed: " + ex); }
        }

        // Undoes everything Start did, so a new build can take over in the running game.
        public static void Stop()
        {
            try { HideLayers.Set(false); } catch (Exception e) { Log("stop: " + e.Message); }
            try { Dlaa.Uninstall(harmony); } catch (Exception e) { Log("stop: " + e.Message); }
            if (root != null) UnityEngine.Object.Destroy(root);   // its components clean up in OnDestroy (the benchmark restores vsync)
            root = null; harmony = null;
            Log("core stopped");
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);

        public static void Log(string s)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); } catch { }
        }
    }
}
