using UnityEngine;

namespace PPUpscaler
{
    // In-game settings window (default F11). Every change applies at once and is saved to the BepInEx config file.
    public class Menu : MonoBehaviour
    {
        static readonly string[] PresetNames = { "Automatic", "J", "K", "L", "M" };
        static readonly int[] PresetValues = { 0, 10, 11, 12, 13 };
        static readonly string[] SignNames = { "-1", "+1" };
        bool show;
        Rect win = new Rect(40, 60, 460, 10);

        string toast = ""; float toastUntil;

        void Update()
        {
            if (Main.MenuKey.Value.IsDown()) show = !show;
            if (Main.ContactShadowsKey.Value.MainKey != KeyCode.None && Main.ContactShadowsKey.Value.IsDown())
            {
                Main.ContactShadowsOn.Value = !Main.ContactShadowsOn.Value;
                toast = "Contact shadows " + (Main.ContactShadowsOn.Value ? "ON" : "OFF"); toastUntil = Time.unscaledTime + 1.5f;
            }
        }

        void OnGUI()
        {
            if (Time.unscaledTime < toastUntil)
            {
                var st = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
                GUI.Label(new Rect(Screen.width / 2 - 200, 70, 400, 40), toast, st);
            }
            if (!show) return;
            win = GUILayout.Window(0x5050, win, Draw, "PP Enhancer - NVIDIA DLSS   (" + Main.MenuKey.Value + " to close)");
        }

        static GUIContent Tip(string text, string tip) { return new GUIContent(text, tip); }

        static void Slider(string label, string tip, BepInEx.Configuration.ConfigEntry<float> e, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Tip(label + " " + e.Value.ToString("F2"), tip), GUILayout.Width(130));
            float v = GUILayout.HorizontalSlider(e.Value, min, max);
            if (Mathf.Abs(v - e.Value) > 0.004f) e.Value = Mathf.Round(v * 100f) / 100f;
            if (GUILayout.Button(Tip("Reset", "Back to the default (" + ((float)e.DefaultValue).ToString("F2") + ")."), GUILayout.Width(50))) e.Value = (float)e.DefaultValue;
            GUILayout.EndHorizontal();
        }

        static void IntSlider(string label, string tip, BepInEx.Configuration.ConfigEntry<int> e, int min, int max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Tip(label + " " + e.Value, tip), GUILayout.Width(130));
            int v = Mathf.RoundToInt(GUILayout.HorizontalSlider(e.Value, min, max));
            if (v != e.Value) e.Value = v;
            if (GUILayout.Button(Tip("Reset", "Back to the default (" + (int)e.DefaultValue + ")."), GUILayout.Width(50))) e.Value = (int)e.DefaultValue;
            GUILayout.EndHorizontal();
        }

        static void LateToggle(string cam, GUIContent label)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var n in Main.LateTransparentCameras.Value.Split(',')) if (n.Trim().Length > 0) list.Add(n.Trim());
            bool on = list.Contains(cam);
            bool non = GUILayout.Toggle(on, label);
            if (non == on) return;
            if (non) list.Add(cam); else list.Remove(cam);
            Main.LateTransparentCameras.Value = string.Join(",", list.ToArray());
        }

        static int Sign(float v) { return v > 0f ? 1 : 0; }
        static float FromSign(int i) { return i == 1 ? 1f : -1f; }

        void Draw(int id)
        {
            bool on = GUILayout.Toggle(Main.Enabled.Value, Tip("NVIDIA DLSS (off: the game renders as usual)",
                "Master switch. Off: the game renders exactly as it normally does, with its own anti-aliasing and no upscaling."));
            if (on != Main.Enabled.Value) Main.Enabled.Value = on;

            GUILayout.Space(6);
            GUILayout.Label(Tip("Quality mode: the 3D view is drawn at " + Mathf.RoundToInt(Main.ScaleNow() * Screen.width) + " x " + Mathf.RoundToInt(Main.ScaleNow() * Screen.height) + " and upscaled to " + Screen.width + " x " + Screen.height,
                "Native (DLAA): full resolution, DLSS only anti-aliases (sharpest, most GPU time). Quality, Balanced, Performance and Ultra Perf. draw fewer pixels (67%, 58%, 50%, 33% of the screen's width) and DLSS rebuilds the rest: faster, a little softer. Custom: pick the scale yourself."));
            int qi = Mathf.Clamp(Main.Quality.Value, 0, Main.QualityNames.Length - 1);
            int nqi = GUILayout.SelectionGrid(qi, Main.QualityNames, 4);
            if (nqi != qi) Main.Quality.Value = nqi;
            if (Main.Quality.Value == 6) Slider("Render scale", "Share of the screen's width and height the 3D view is drawn at (0.33 to 1).", Main.CustomScale, 0.33f, 1f);

            GUILayout.Space(6);
            GUILayout.Label(Tip("Frame rate limit (the game is always limited to 60)",
                "The game caps itself at 60 frames per second whatever its VSync setting. 'No limit' lifts that; VSync still applies if it is on in the game's video settings."));
            int[] fv = { -1, 0, 90, 120, 144, 165 };
            int fi = System.Array.IndexOf(fv, Main.FrameRateLimit.Value);
            int nfi = GUILayout.SelectionGrid(fi, new[] { "No limit", "Game (60)", "90", "120", "144", "165" }, 6);
            if (nfi != fi && nfi >= 0) Main.FrameRateLimit.Value = fv[nfi];

            GUILayout.Space(6);
            GUILayout.Label(Tip("DLSS preset (Automatic lets NVIDIA choose; L and M cost much more GPU time)",
                "Which DLSS model runs. Automatic is NVIDIA's pick (usually K, the best balance). L and M can look a bit better at low quality modes but cost two to three times more GPU time."));
            int pi = System.Array.IndexOf(PresetValues, Main.Preset.Value);
            int npi = GUILayout.SelectionGrid(pi, PresetNames, 5);
            if (npi != pi && npi >= 0) Main.Preset.Value = PresetValues[npi];

            GUILayout.Space(6);
            GUILayout.Label(Tip("Texture mip map bias (now " + MipBias.Current.ToString("F2") + ")",
                "Off: textures as the game draws them. On: below native, textures are sampled with a negative mip map bias so they stay as sharp as at native. It costs nothing at native resolution."));
            int mbm = GUILayout.SelectionGrid(Mathf.Clamp(Main.MipBiasMode.Value, 0, 1), new[] { "Off", "On" }, 2);
            if (mbm != Main.MipBiasMode.Value) Main.MipBiasMode.Value = mbm;

            if (Main.DeveloperOptions.Value)
            {
                GUILayout.Label("Technical options (leave as they are unless you are testing)");
            LateToggle("GeoscapeCamera", Tip("Geoscape: DLSS before transparent objects (clouds, atmosphere, stars, icons)",
                "Draws clouds, the atmosphere and icons after DLSS, so DLSS cannot smear them. Works at native resolution only."));
            LateToggle("MainCamera", Tip("Tactical: DLSS before transparent objects (smoke, fire, effects)",
                "Draws smoke, fire and effects after DLSS, so DLSS cannot smear them. Works at native resolution only. Off by default."));
            bool gd = Main.DepthMotionCameras.Value.Contains("GeoscapeCamera");
            bool ngd = GUILayout.Toggle(gd, Tip("Geoscape: motion computed from depth instead of Unity's (Unity's blurs the land)",
                "How DLSS is told how the picture moves. The depth-based motion is exact for the globe; Unity's own smears the land. Leave on."));
            if (ngd != gd)
            {
                var gl = new System.Collections.Generic.List<string>();
                foreach (var n in Main.DepthMotionCameras.Value.Split(',')) if (n.Trim().Length > 0 && n.Trim() != "GeoscapeCamera") gl.Add(n.Trim());
                if (ngd) gl.Add("GeoscapeCamera");
                Main.DepthMotionCameras.Value = string.Join(",", gl.ToArray());
            }
            bool td = Main.DepthMotionCameras.Value.Contains("MainCamera");
            bool ntd = GUILayout.Toggle(td, Tip("Tactical: motion computed from depth instead of Unity's (test)",
                "Test option. Unity's own motion data is exact in tactical, and it also covers moving units, which depth alone does not. Leave off."));
            if (ntd != td)
            {
                var l = new System.Collections.Generic.List<string>();
                foreach (var n in Main.DepthMotionCameras.Value.Split(',')) if (n.Trim().Length > 0 && n.Trim() != "MainCamera") l.Add(n.Trim());
                if (ntd) l.Add("MainCamera");
                Main.DepthMotionCameras.Value = string.Join(",", l.ToArray());
            }
            bool cm = GUILayout.Toggle(Main.CoreMotion.Value, Tip("Geoscape: include the turning of the starry layer (with motion from depth)",
                "The starry layer seen through the oceans turns on its own; this tells DLSS, so it stays sharp. Leave on."));
            if (cm != Main.CoreMotion.Value) Main.CoreMotion.Value = cm;
            bool ao = GUILayout.Toggle(Main.AoAtOutputSize.Value, Tip("Ambient occlusion at the output size (looks as at native below native)",
                "The game's ambient occlusion (the soft shading in corners) measures its reach in pixels, so below native it would spread too far. This keeps it looking as at native, at native's cost for that effect."));
            if (ao != Main.AoAtOutputSize.Value) Main.AoAtOutputSize.Value = ao;
            bool ge = GUILayout.Toggle(Main.GameExposure.Value, Tip("Give DLSS the game's exposure (keeps highlights bright)",
                "Tells DLSS how bright the game's own camera exposure makes the scene, so bright highlights are not dimmed. Leave on."));
            if (ge != Main.GameExposure.Value) Main.GameExposure.Value = ge;
            bool aa = GUILayout.Toggle(Main.DisableGameAA.Value, Tip("Switch the game's SMAA off while DLSS runs",
                "DLSS does its own anti-aliasing, and the game's SMAA on top would only soften the picture. Leave on."));
            if (aa != Main.DisableGameAA.Value) Main.DisableGameAA.Value = aa;
            }

            GUILayout.Space(8);
            string csKey = Main.ContactShadowsKey.Value.MainKey != KeyCode.None ? "  (key: " + Main.ContactShadowsKey.Value + ")" : "";
            bool csOn = GUILayout.Toggle(Main.ContactShadowsOn.Value, Tip("Contact shadows (tactical)" + csKey,
                "Short, fine shadows where objects touch the ground or each other (debris, props, grass, feet), which the game's own shadows are too coarse to draw. Drawn before smoke and effects. Costs about 1 ms on a 4K screen."));
            if (csOn != Main.ContactShadowsOn.Value) Main.ContactShadowsOn.Value = csOn;
            if (Main.ContactShadowsOn.Value)
            {
                Slider("Strength", "How dark a contact shadow gets. Higher: shadows read more, but shaded areas can look too dark. It cannot safely go to 1, because the sun's share of the light cannot be separated from the scene colour here.", Main.CsStrength, 0f, 1f);
                Slider("Distance", "How far toward the sun a surface looks for something blocking it (world units). Higher: longer shadows, but needs more steps to stay smooth.", Main.CsDistance, 0.1f, 2f);
                Slider("Thickness", "Things further in front than this do not cast a shadow. Raise it if thin objects (poles, fences, plants) cast none; lower it if objects leave dark outlines on what is behind them.", Main.CsThickness, 0.1f, 2f);
                Slider("Bias", "Lifts the start of the check off the surface. Raise it if flat surfaces show speckles; lower it if shadows float away from where objects touch.", Main.CsBias, 0f, 0.2f);
                Slider("Screen reach", "The longest a shadow may reach on the screen, as a share of the screen height. Zoomed in, shadows are cut to this so the few steps do not skip thin objects. Raise it for longer shadows up close (needs more steps).", Main.CsReach, 0.02f, 0.2f);
                IntSlider("Steps", "How many points are checked along the way to the sun. More: smoother, more complete shadows, and more GPU time. 8 looks natural with DLSS; raise it together with Distance.", Main.CsSteps, 4, 24);
                if (GUILayout.Button(Tip("Reset contact shadow settings", "Puts strength, distance, thickness, bias and steps back to their defaults.")))
                    Main.ResetSettings(true);
            }

            if (Main.DeveloperOptions.Value)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Tip("Contact shadow stage", "After lighting (default): right after the scene is lit, before fog, ambient occlusion and fog of war, which then cover the shadows. After fog and fog of war: on top of them (darkens them too). After forward objects: Unity skips this stage when no forward-rendered object is visible, so shadows vanish at times."), GUILayout.Width(150));
                int cst = GUILayout.SelectionGrid(Mathf.Clamp(Main.CsStage.Value, 0, 2), new[] { "After lighting (default)", "After fog and fog of war", "After forward objects" }, 3);
                if (cst != Main.CsStage.Value) Main.CsStage.Value = cst;
                GUILayout.EndHorizontal();
                bool cst2 = GUILayout.Toggle(Main.CsStats.Value, "Log contact shadow coverage with the camera position (for diagnosing)");
                if (cst2 != Main.CsStats.Value) Main.CsStats.Value = cst2;
                bool cd = GUILayout.Toggle(Main.CsDebug.Value, "Show the contact shadow term only");
                if (cd != Main.CsDebug.Value) Main.CsDebug.Value = cd;
                GUILayout.Space(8);
                GUILayout.Label("Developer options");
                GUILayout.BeginHorizontal();
                GUILayout.Label("Jitter sign X", GUILayout.Width(110));
                int jx = GUILayout.SelectionGrid(Sign(Main.JitterSignX.Value), SignNames, 2);
                if (jx != Sign(Main.JitterSignX.Value)) Main.JitterSignX.Value = FromSign(jx);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Jitter sign Y", GUILayout.Width(110));
                int jy = GUILayout.SelectionGrid(Sign(Main.JitterSignY.Value), SignNames, 2);
                if (jy != Sign(Main.JitterSignY.Value)) Main.JitterSignY.Value = FromSign(jy);
                GUILayout.EndHorizontal();
                bool jt = GUILayout.Toggle(Main.JitterTransparents.Value, "Jitter transparent objects too (holograms, icons, clouds)");
                if (jt != Main.JitterTransparents.Value) Main.JitterTransparents.Value = jt;
                bool nj = GUILayout.Toggle(Main.NoJitter.Value, "No jitter (reference: smooth but less detailed)");
                if (nj != Main.NoJitter.Value) Main.NoJitter.Value = nj;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Motion vector scale X", GUILayout.Width(150));
                int mx = GUILayout.SelectionGrid(Sign(Main.MvScaleX.Value), SignNames, 2);
                if (mx != Sign(Main.MvScaleX.Value)) Main.MvScaleX.Value = FromSign(mx);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Motion vector scale Y", GUILayout.Width(150));
                int my = GUILayout.SelectionGrid(Sign(Main.MvScaleY.Value), SignNames, 2);
                if (my != Sign(Main.MvScaleY.Value)) Main.MvScaleY.Value = FromSign(my);
                GUILayout.EndHorizontal();
                GUILayout.Label("(measured in tactical: motion vector scale -1, -1 is correct)");
                bool mc = GUILayout.Toggle(Main.MvCheckOn.Value, "Motion vector check (logs once per second while the camera moves)");
                if (mc != Main.MvCheckOn.Value) Main.MvCheckOn.Value = mc;
                bool hl = GUILayout.Toggle(HideLayers.On, "Hide the geoscape atmosphere and clouds (test, not saved)");
                if (hl != HideLayers.On) HideLayers.Set(hl);
                if (GUILayout.Button("Test motion vectors against the image (close this window, then keep moving the camera)")) { MvTruth.Request(); show = false; }
                if (GUILayout.Button("Log what turns, moves or scrolls on its own (1 s, keep the camera still)")) MotionCensus.Run();
                if (GUILayout.Button("Highlights test: do bright pixels survive DLSS? (keep the camera still)")) HighlightTest.Request(Dlaa.ActiveLayer);
                if (!QualityTest.Running && GUILayout.Button("Quality test: Native vs Quality, Performance and jitter variants (about 20 s; close this window, keep the camera still)")) { QualityTest.Run(); show = false; }
                if (GUILayout.Button("Motion vector block test (close this window, then keep the camera moving ~5 s)")) { MvBlockTest.Request(); show = false; }
                if (GUILayout.Button("Save 1:1 crops: DLSS input, DLSS output, screen (close this window first)")) { Crops.Request(); show = false; }
                if (!Bench.Running && GUILayout.Button("Benchmark contact shadows off/on (about 80 s, keep the camera still)")) { show = false; Bench.RunContactBench(); }
                if (!Bench.Running && GUILayout.Button("Benchmark texture mip bias off/on (about 80 s, keep the camera still)")) { show = false; Bench.RunMipBench(); }
                if (!MipStress.Running && GUILayout.Button("Mip bias stress test: many quality switches, then compare frame time (about 4 min, keep the camera still)")) { show = false; MipStress.Run(); }
                if (GUILayout.Button("Dump camera and scene to the log")) SceneDump.Run();
                if (GUILayout.Button("Save what DLSS receives as images (colour, motion, depth)")) MvImage.Request();
                if (!Bench.Running && GUILayout.Button("Run DLAA benchmark (about 80 s, keep the camera still)")) { show = false; Bench.RunBench(); }
            }

            GUILayout.Space(8);
            var helpStyle = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.UpperLeft, fontSize = 12 };
            GUILayout.Box(GUI.tooltip.Length > 0 ? GUI.tooltip : "Hover over a setting for help.", helpStyle, GUILayout.Height(78));
            GUILayout.Label(Dlss.Status());
            GUILayout.Space(4);
            GUILayout.Label("Uses NVIDIA DLSS. Unofficial mod, not sponsored or endorsed by NVIDIA.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Tip("Reset all settings", "Puts every setting back to its default (the keys and the developer options switch are kept).")) ) Main.ResetSettings(false);
            if (GUILayout.Button("Close")) show = false;
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }
    }
}
