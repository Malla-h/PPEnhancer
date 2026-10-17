using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace PPUpscaler
{
    // DLAA (DLSS at native resolution) for the game's 3D cameras.
    // PPv2 records the whole post-processing stack into a command buffer in PostProcessLayer.OnPreCull (BuildCommandBuffers -> Render), starting
    // from a copy of the HDR scene in context.source. Before Render, the scene colour, depth and Unity's motion vectors are copied, DLSS runs,
    // and context.source is pointed at its output, so bloom, colour grading and the rest work on the anti-aliased HDR image.
    // The sub-pixel jitter is applied after PPv2's OnPreCull (which resets the projection matrix) and removed after the camera has rendered.
    public static class Dlaa
    {
        static readonly Dictionary<PostProcessLayer, Camera> cams = new Dictionary<PostProcessLayer, Camera>();
        static readonly HashSet<Camera> mvAdded = new HashSet<Camera>();
        public static bool Suspended;   // the benchmark's DLAA-off phases
        static readonly Dictionary<PostProcessLayer, PostProcessLayer.Antialiasing> savedAA = new Dictionary<PostProcessLayer, PostProcessLayer.Antialiasing>();

        static RenderTexture colorIn, depthRt, mvRt, outRt, finalRt, lowTarget;
        static int w, h;            // render size (what the camera draws)
        static int ow, oh;          // output size (the screen)
        public static float Scale = 1f;   // render size / output size this frame

        // This frame's decision, made before PPv2 records its command buffer.
        static PostProcessLayer activeLayer;
        static Camera activeCam;
        static int activeFrame = -1;
        static bool applied, reset, transparentsWas = true;
        static Vector2 jitterPx;
        static Matrix4x4 baseProj, vpCur, vpJit, vpJitInv, vpPrev;

        // History, for camera-cut detection and previous-frame matrices.
        static int lastCamId, lastFrame = -100, index;
        static Vector3 lastPos; static Quaternion lastRot; static float lastFov;
        static Matrix4x4 lastVP;
        static int Phases { get { return Mathf.Clamp(Mathf.CeilToInt(8f * (1f / (Scale * Scale))), 8, 96); } }   // NVIDIA: 8 * (output / render)^2
        static bool wasReady;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(PostProcessLayer), "OnPreCull"),
                prefix: new HarmonyMethod(typeof(Dlaa), nameof(PreCullPrefix)), postfix: new HarmonyMethod(typeof(Dlaa), nameof(PreCullPostfix)));
            harmony.Patch(AccessTools.Method(typeof(PostProcessLayer), nameof(PostProcessLayer.Render)),
                prefix: new HarmonyMethod(typeof(Dlaa), nameof(RenderPrefix)), postfix: new HarmonyMethod(typeof(Dlaa), nameof(RenderPostfix)));
            AoFullRes.Install(harmony);

            Camera.onPostRender += OnPostRender;
            Camera.onPreRender += OnPreRender;
            Main.Log("DLAA hooks installed");
        }

        // Hot reload: removes the hooks and puts every camera back the way the game set it up.
        public static void Uninstall(Harmony harmony)
        {
            Camera.onPostRender -= OnPostRender;
            Camera.onPreRender -= OnPreRender;
            ContactShadows.Off(null);
            RestoreRect();
            DestroyPresent();
            UnseedSeeThrough(); seeThrough = null;
            finalFrame = -1;
            DetachLate();
            if (harmony != null) harmony.UnpatchSelf();
            if (applied && activeCam != null) { activeCam.ResetProjectionMatrix(); activeCam.useJitteredProjectionMatrixForTransparentRendering = transparentsWas; }
            applied = false;
            foreach (var kv in savedAA) if (kv.Key != null) kv.Key.antialiasingMode = kv.Value;
            savedAA.Clear();
            foreach (var c in mvAdded) if (c != null) c.depthTextureMode &= ~DepthTextureMode.MotionVectors;
            mvAdded.Clear();
            Release();
            activeLayer = null; activeCam = null; activeFrame = -1;
            Main.Log("DLAA hooks removed");
        }

        public static bool DepthMotion(Camera c)
        {
            if (c == null || Personnel) return false;
            foreach (var n in Main.DepthMotionCameras.Value.Split(',')) if (n.Trim() == c.name) return true;
            return false;
        }

        // The personnel screen (the squad bay with the animated soldier) is drawn by the geoscape camera. Motion computed from depth knows only the camera,
        // so the soldier would have none; there Unity's own motion vectors (skinned meshes included) are used. The scene root exists only while it is shown.
        static bool personnel; static int personnelChecked = -1000;
        static bool Personnel
        {
            get
            {
                if (Time.frameCount - personnelChecked >= 15)
                {
                    personnelChecked = Time.frameCount;
                    bool now = GameObject.Find("SquadBay") != null;
                    if (now != personnel) { personnel = now; Main.Log("personnel screen: " + (now ? "motion vectors from Unity (animated soldier)" : "left")); }
                }
                return personnel;
            }
        }

        static Camera CamOf(PostProcessLayer l)
        {
            Camera c;
            if (!cams.TryGetValue(l, out c) || c == null) { c = l.GetComponent<Camera>(); cams[l] = c; }
            return c;
        }

        static bool IsTarget(Camera c)
        {
            if (c == null || c.targetTexture != null || c.cullingMask == 0 || c.stereoEnabled) return false;
            foreach (var n in Main.Cameras.Value.Split(',')) if (n.Trim() == c.name) return true;
            return false;
        }

        static void EnsureTargets(int rw, int rh, int outW, int outH)
        {
            if (outRt != null && w == rw && h == rh && ow == outW && oh == outH) return;
            Release();
            w = rw; h = rh; ow = outW; oh = outH;
            colorIn = Make("PPUpscaler color", RenderTextureFormat.ARGBHalf, false);
            depthRt = Make("PPUpscaler depth", RenderTextureFormat.RFloat, false);
            // 32-bit: depth-based motion is in pixels, and 16-bit floats resolve only 0.125 to 0.25 px at the hundreds of pixels a fast geoscape turn moves.
            mvRt = Make("PPUpscaler motion", RenderTextureFormat.RGFloat, true);   // written by the native depth-based pass on some cameras
            outRt = Make("PPUpscaler output", RenderTextureFormat.ARGBHalf, true, ow, oh);
            finalRt = new RenderTexture(ow, oh, 0, RenderTextureFormat.Default) { name = "PPUpscaler final", filterMode = FilterMode.Bilinear };
            if (w != ow || h != oh)
            {
                lowTarget = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR) { name = "PPUpscaler render target" };
                lowTarget.Create();
            }
            finalRt.Create();
            Main.Log("DLSS targets: render " + w + "x" + h + ", output " + ow + "x" + oh);
        }

        static RenderTexture Make(string name, RenderTextureFormat f, bool uav, int width = 0, int height = 0)
        {
            var rt = new RenderTexture(width > 0 ? width : w, height > 0 ? height : h, 0, f, RenderTextureReadWrite.Linear) { name = name, enableRandomWrite = uav, filterMode = FilterMode.Point, useMipMap = false };
            rt.Create();
            return rt;
        }

        static void Release()
        {
            RestoreRect();
            foreach (var rt in new[] { colorIn, depthRt, mvRt, outRt, finalRt, lowTarget }) if (rt != null) { rt.Release(); Object.Destroy(rt); }
            colorIn = depthRt = mvRt = outRt = finalRt = lowTarget = null;
            MvCheck.Release();
            Exposure.Release();
            AoFullRes.Release();
            ContactShadows.Release();
        }

        // Whenever DLAA does not run for a camera, the camera renders exactly as the game set it up: its own anti-aliasing, and no motion vector
        // pass unless something else asked for one.
        static void Restore(PostProcessLayer l, Camera cam)
        {
            PostProcessLayer.Antialiasing aa;
            if (savedAA.TryGetValue(l, out aa)) { l.antialiasingMode = aa; savedAA.Remove(l); }
            if (cam != null && mvAdded.Remove(cam)) cam.depthTextureMode &= ~DepthTextureMode.MotionVectors;
        }

        static float Halton(int i, int radix)
        {
            float r = 0f, f = 1f / radix;
            for (; i > 0; i /= radix) { r += f * (i % radix); f /= radix; }
            return r;
        }

        static void PreCullPrefix(PostProcessLayer __instance)
        {
            try { Begin(__instance); }
            catch (System.Exception e) { activeFrame = -1; Main.Log("DLAA begin failed: " + e); }
        }

        static void Begin(PostProcessLayer layer)
        {
            var cam = CamOf(layer);
            RestoreRect();
            if (presentCam != null) presentCam.enabled = false;
            if (!Main.Enabled.Value || Suspended || !IsTarget(cam)) { Restore(layer, cam); if (lateCbCam == cam) DetachLate(); if (IsTarget(cam)) UnseedSeeThrough(); ContactShadows.Off(cam); return; }   // gaps and camera switches are caught by the reset checks below
            // Unity renders motion vectors (and keeps previous-frame transforms) only for cameras that ask for them. Measured cost: about 0.15 ms at 4K.
            if ((cam.depthTextureMode & DepthTextureMode.MotionVectors) == 0) { cam.depthTextureMode |= DepthTextureMode.MotionVectors; mvAdded.Add(cam); }
            int outW = cam.pixelWidth, outH = cam.pixelHeight;
            float want = Mathf.Clamp(Main.ScaleNow(), 0.25f, 1f);
            int rw = want >= 0.999f ? outW : Mathf.Max(256, (Mathf.RoundToInt(outW * want) & ~1)), rh = want >= 0.999f ? outH : Mathf.Max(144, (Mathf.RoundToInt(outH * want) & ~1));
            Scale = (float)rw / outW;
            EnsureTargets(rw, rh, outW, outH);
            // Below native the camera draws into a texture of the render size for this frame (measured: shrinking its viewport on the screen makes Unity
            // size the screen's image effect buffers to it for every later camera, so the see-through camera then redraws the whole screen at the
            // render size). The screen itself is never touched at a smaller size.
            if (lowTarget != null) { cam.targetTexture = lowTarget; targetChanged = cam; }
            if (lowTarget != null) SeedSeeThrough(); else UnseedSeeThrough();
            if (Scale < 0.999f) EnsurePresent(cam);
            if (!Dlss.Tick(w, h, ow, oh, outRt)) { RestoreRect(); Restore(layer, cam); if (lateCbCam == cam) DetachLate(); UnseedSeeThrough(); ContactShadows.Off(cam); wasReady = false; return; }

            if (Main.DisableGameAA.Value && layer.antialiasingMode != PostProcessLayer.Antialiasing.None)
            {   // the game's SMAA would only soften what DLSS receives
                savedAA[layer] = layer.antialiasingMode;
                layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
            }

            cam.ResetProjectionMatrix();   // PPv2 does the same right after this; the unjittered projection is needed here already
            baseProj = cam.projectionMatrix;
            var t = cam.transform;
            float dist = Vector3.Distance(t.position, lastPos), ang = Quaternion.Angle(t.rotation, lastRot);
            string why = null;
            if (!wasReady) why = "start";
            else if (cam.GetInstanceID() != lastCamId) why = "camera changed to " + cam.name;
            else if (Time.frameCount - lastFrame > 1) why = "camera not rendered for " + (Time.frameCount - lastFrame - 1) + " frames";
            else if (dist > Main.CutDistance.Value) why = "camera moved " + dist.ToString("F2");
            else if (ang > Main.CutAngle.Value) why = "camera turned " + ang.ToString("F1") + " deg";
            else if (Mathf.Abs(cam.fieldOfView - lastFov) > 5f) why = "field of view " + lastFov.ToString("F1") + " -> " + cam.fieldOfView.ToString("F1");
            reset = why != null;
            if (reset) Main.Log("DLAA history reset: " + why);
            ObjectMotion.Begin(cam, reset);
            XformProbe.PreCull(cam);

            index = (index + 1) % Phases;
            jitterPx = Main.NoJitter.Value ? Vector2.zero : new Vector2(Halton(index + 1, 2) - 0.5f, Halton(index + 1, 3) - 0.5f);
            var jit = baseProj;
            jit[0, 2] += jitterPx.x * 2f / w;
            jit[1, 2] += jitterPx.y * 2f / h;
            var view = cam.worldToCameraMatrix;
            vpCur = GL.GetGPUProjectionMatrix(baseProj, true) * view;
            vpJit = GL.GetGPUProjectionMatrix(jit, true) * view;
            vpJitInv = vpJit.inverse;
            vpPrev = reset ? vpCur : lastVP;

            activeLayer = layer; activeCam = cam; activeFrame = Time.frameCount;
            wasReady = true;
            ContactShadows.Record(cam, w, h, vpJitInv, vpJit);
            // DLSS before the transparents draws them on a full-size image; with a smaller render size they would have to be drawn into it at a
            // different size, so that mode is for native resolution only.
            late = LateMode(cam) && Scale >= 0.999f;
            if (late) RecordLate(cam); else if (lateCbCam == cam) DetachLate();
        }

        static void RenderPrefix(PostProcessLayer __instance, PostProcessRenderContext context)
        {
            if (__instance != activeLayer || activeFrame != Time.frameCount || context.camera != activeCam) return;
            if (late) { MvTruth.FinalCapture(context.command, context.source); return; }
            try
            {
                LogAo(__instance);
                Inject(context.command, context.source, false); context.source = outRt;
                if (Scale < 0.999f)
                {
                    ContextToOutputSize(context);
                    // PPv2's final image goes to a full-size texture of its own, and a camera of full size, drawn right after, presents it.
                    context.destination = new RenderTargetIdentifier(finalRt); context.flip = false; finalFrame = Time.frameCount;
                }
            }
            catch (System.Exception e) { activeFrame = -1; Main.Log("DLAA inject failed: " + e); }
        }

        static void RenderPostfix(PostProcessLayer __instance, PostProcessRenderContext context)
        {
            if (__instance == activeLayer && activeFrame == Time.frameCount && finalFrame == Time.frameCount) Crops.QueueFinal(context.command, finalRt);
        }

        // Debug: the camera's ambient occlusion settings, once per camera and render size (it runs before DLSS, at the render size).
        static readonly HashSet<string> aoLogged = new HashSet<string>();
        static void LogAo(PostProcessLayer l)
        {
            var key = l.name + w + "x" + h;
            if (!aoLogged.Add(key)) return;
            var ao = l.GetSettings<AmbientOcclusion>();
            if (ao == null) { Main.Log("ambient occlusion on " + l.name + ": none"); return; }
            Main.Log(string.Format("ambient occlusion on {0} at {1}x{2}: {3}, mode {4}, intensity {5:F2}, radius {6:F2}, quality {7}, thickness {8:F2}, ambient only {9}, colour {10}",
                l.name, w, h, ao.enabled.value && ao.active ? "ON" : "off", ao.mode.value, ao.intensity.value, ao.radius.value, ao.quality.value, ao.thicknessModifier.value, ao.ambientOnly.value, ao.color.value));
        }

        // PPv2 sizes its temporary textures (bloom, depth of field, the final pass) from the context: from here on it works at the output size.
        static readonly BindingFlags priv = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static void ContextToOutputSize(PostProcessRenderContext c)
        {
            var t = typeof(PostProcessRenderContext);
            t.GetField("<width>k__BackingField", priv).SetValue(c, ow);
            t.GetField("<height>k__BackingField", priv).SetValue(c, oh);
            t.GetField("<screenWidth>k__BackingField", priv).SetValue(c, ow);
            t.GetField("<screenHeight>k__BackingField", priv).SetValue(c, oh);
            var f = t.GetField("m_sourceDescriptor", priv);
            object d = f.GetValue(c);
            d.GetType().GetProperty("width").SetValue(d, ow, null);
            d.GetType().GetProperty("height").SetValue(d, oh, null);
            f.SetValue(c, d);
            if (!sizeLogged)
            {   // what PPv2 will really allocate from here on
                sizeLogged = true;
                var gd = t.GetMethod("GetDescriptor", priv);
                var desc = gd != null ? (RenderTextureDescriptor)gd.Invoke(c, new object[] { 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default }) : default(RenderTextureDescriptor);
                Main.Log("post chain size after DLSS: context " + c.width + "x" + c.height + ", screen " + c.screenWidth + "x" + c.screenHeight + ", temporary textures " + desc.width + "x" + desc.height);
            }
        }
        static bool sizeLogged;

        static readonly Rect fullRect = new Rect(0f, 0f, 1f, 1f);
        // The camera draws into its own texture for the frame; this puts it back (mouse picking, world to screen conversions read the screen).
        static Camera targetChanged;

        static int finalFrame = -1;

        static Camera presentCam; static CommandBuffer presentCb;
        static void EnsurePresent(Camera main)
        {
            if (presentCam == null)
            {
                var go = new GameObject("PPUpscaler present");
                Object.DontDestroyOnLoad(go);
                presentCam = go.AddComponent<Camera>();
                presentCam.cullingMask = 0;
                presentCam.clearFlags = CameraClearFlags.Nothing;
                presentCam.allowHDR = false; presentCam.allowMSAA = false; presentCam.useOcclusionCulling = false;
                presentCam.rect = fullRect;
                presentCb = new CommandBuffer { name = "PPUpscaler present" };
                presentCam.AddCommandBuffer(CameraEvent.AfterEverything, presentCb);
            }
            presentCam.depth = main.depth + 0.5f;   // right after the 3D camera, before the see-through camera and the UI
            presentCb.Clear();
            // Measured: image effects (OnRenderImage) on any camera get buffers of the upscaled camera's size, shared across cameras, so the copy is a
            // command buffer straight to the screen, on a forward camera without image effects (no intermediate texture).
            presentCam.renderingPath = RenderingPath.Forward;
            presentCb.Blit(finalRt, BuiltinRenderTextureType.CameraTarget);
            presentCam.enabled = true;
        }

        // Tactical: the game's see-through camera (walls in front of units drawn faded, unit status bars) renders after the 3D camera without
        // clearing the colour, into the screen buffer the 3D camera filled. With the 3D camera drawing into a texture of its own, that buffer is
        // never refreshed (measured: silhouettes and status bars piled up in trails), so it is filled with the final image before it draws.
        static Camera seeThrough; static CommandBuffer seedCb; static RenderTexture seededWith;
        static void SeedSeeThrough()
        {
            if (seeThrough == null)
            {
                foreach (var c in Camera.allCameras) if (c.name == "__ProcSeeThroughCam") { seeThrough = c; break; }
                if (seeThrough == null) return;
                seededWith = null;
            }
            if (seedCb == null) seedCb = new CommandBuffer { name = "PPUpscaler see-through seed" };
            if (seededWith == finalRt) return;
            seeThrough.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, seedCb);
            seedCb.Clear();
            seedCb.Blit(finalRt, BuiltinRenderTextureType.CameraTarget);
            seedCb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);   // back to its colour and depth for drawing
            seeThrough.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, seedCb);
            seededWith = finalRt;
            Main.Log("see-through camera: filled with the final image before it draws");
        }

        static void UnseedSeeThrough()
        {
            if (seeThrough != null && seedCb != null && seededWith != null) seeThrough.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, seedCb);
            seededWith = null;
        }

        static void DestroyPresent()
        {
            if (presentCam != null) Object.Destroy(presentCam.gameObject);
            presentCam = null;
            if (presentCb != null) presentCb.Release();
            presentCb = null;
        }
        public static void RestoreRect()
        {
            if (targetChanged != null) targetChanged.targetTexture = null;
            targetChanged = null;
        }

        // Records DLSS into a command buffer: copies of the scene colour (src), depth and motion, the evaluate call, and the debug probes.
        // lateMode: the buffer runs before the transparent pass, so DLSS's output is copied back into the camera's target for the transparents to be
        // drawn on top of it.
        static void Inject(CommandBuffer cmd, RenderTargetIdentifier src, bool lateMode)
        {
            cmd.Blit(src, colorIn);
            // Measured in game: only ResolvedDepth (and the global texture object itself) holds the device depth here. A copy of _CameraDepthTexture
            // by property ID reads zeros, and BuiltinRenderTextureType.Depth is not depth at all (values up to 7).
            cmd.Blit(BuiltinRenderTextureType.ResolvedDepth, depthRt);
            // Measured in geoscape: with the camera and the globe still, Unity's motion vectors show the globe rotating (about 1.3 px a frame), and
            // when the camera orbits they carry only 20 to 60 % of the real motion. The cause is not found. Camera motion computed from depth is
            // exact there (0.00 px when still), so those cameras get that instead. It has no motion of moving objects.
            bool depthMotion = DepthMotion(activeCam);
            if (depthMotion) Dlss.QueueDepthMv(cmd, depthRt, mvRt, w, h, vpJitInv, vpCur, vpPrev, ObjectMotion.Delta, ObjectMotion.Sphere, ObjectMotion.On);
            else cmd.Blit(BuiltinRenderTextureType.MotionVectors, mvRt);
            var js = new Vector2(Main.JitterSignX.Value * jitterPx.x, Main.JitterSignY.Value * jitterPx.y);
            // Unity's motion vectors are fractions of the screen with the opposite sign; the depth-based ones are already in DLSS's convention.
            var ms = depthMotion ? Vector2.one : new Vector2(Main.MvScaleX.Value * w, Main.MvScaleY.Value * h);
            bool sameSize = w == ow && h == oh;   // the debug probes below compare images of equal size
            MvTruth.BeforeEval(cmd, mvRt);
            MvBlockTest.Queue(cmd, colorIn, depthRt, w, h, vpJitInv, vpCur, vpPrev);
            float expScale = Exposure.Queue(cmd, activeLayer);
            Dlss.QueueEval(cmd, colorIn, depthRt, mvRt, outRt, w, h, js, ms, reset, Exposure.Texture, expScale);
            if (Main.MvCheckOn.Value && !reset && !depthMotion) MvCheck.Queue(cmd, activeCam, depthRt, mvRt, w, h, vpJitInv, vpCur, vpPrev);
            if (sameSize) MvTruth.Queue(cmd, colorIn, depthMotion ? mvRt : null, depthRt, w, h, vpJitInv, vpCur, vpPrev, outRt);
            if (sameSize) HighlightTest.Queue(cmd, colorIn, outRt, w, h);
            MvImage.Queue(cmd, colorIn, mvRt, depthRt, depthMotion ? 1 : w, depthMotion ? 1 : h, vpJitInv);
            MvImage.QueueOutput(cmd, outRt);
            Crops.Queue(cmd, colorIn, outRt);
            if (lateMode)
            {
                cmd.Blit(outRt, BuiltinRenderTextureType.CameraTarget);
                cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);   // back to the camera's colour and depth for the transparents
            }
        }

        // DLSS before the transparent pass: transparent objects have no motion vectors and often change without moving (the geoscape clouds change with
        // the viewing angle, the star and nebula billboards have no depth of their own), so DLSS ghosts or blurs them. Here DLSS sees the opaque scene
        // and the sky only, and the transparents are drawn afterwards on its output, unjittered.
        static CommandBuffer lateCb;
        static Camera lateCbCam;
        static bool late;
        public static bool LateNow { get { return late; } }
        public static RenderTexture Output { get { return outRt; } }
        // True while this camera is being upscaled this frame (render size below the output size); the output size.
        public static bool UpscalingNow(Camera c, out int outW, out int outH) { outW = ow; outH = oh; return c != null && c == activeCam && activeFrame == Time.frameCount && Scale < 0.999f; }
        public static PostProcessLayer ActiveLayer { get { return activeLayer; } }
        public static bool RunningRecently { get { return !Suspended && Time.frameCount - activeFrame < 30; } }   // DLSS ran for a camera within the last half second
        public static float RenderScale { get { return Scale; } }
        public static bool LateMode(Camera c)
        {
            if (c == null) return false;
            foreach (var n in Main.LateTransparentCameras.Value.Split(',')) if (n.Trim() == c.name) return true;
            return false;
        }

        static void DetachLate()
        {
            if (lateCbCam != null && lateCb != null) lateCbCam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, lateCb);
            lateCbCam = null;
            if (lateCb != null) lateCb.Clear();
        }

        static void RecordLate(Camera cam)
        {
            if (lateCb == null) lateCb = new CommandBuffer { name = "PPUpscaler DLSS before transparents" };
            if (lateCbCam != cam) { DetachLate(); cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, lateCb); lateCbCam = cam; }
            lateCb.Clear();
            Inject(lateCb, BuiltinRenderTextureType.CameraTarget, true);
        }

        static void PreCullPostfix(PostProcessLayer __instance)
        {
            if (__instance != activeLayer || activeFrame != Time.frameCount) return;
            var jit = baseProj;
            jit[0, 2] += jitterPx.x * 2f / w;
            jit[1, 2] += jitterPx.y * 2f / h;
            activeCam.projectionMatrix = jit;
            jitSet = jit;
            activeCam.nonJitteredProjectionMatrix = baseProj;   // Unity's motion vectors use this one, so they carry no jitter
            // Transparent objects get no motion vectors, and the game's animated ones (holograms, icons, clouds) change every frame, so DLSS cannot
            // build a history for them and would show the jitter as vibration. Unity's own TAA leaves them unjittered too.
            transparentsWas = activeCam.useJitteredProjectionMatrixForTransparentRendering;
            activeCam.useJitteredProjectionMatrixForTransparentRendering = !late && Main.JitterTransparents.Value;   // drawn after DLSS: no jitter
            applied = true;
        }

        // Debug (with the motion vector check on): is the jittered projection still in place when the camera starts rendering, or did
        // something replace it after PPv2's OnPreCull?
        static int jitOk, jitBad, viewShiftN; static float jitNext, viewShiftMax, viewShiftSum; static Matrix4x4 jitSet;
        static void OnPreRender(Camera cam)
        {
            if (!applied || cam != activeCam || !Main.MvCheckOn.Value) return;
            // Does the camera still have the view we captured in OnPreCull (and built the motion vectors from)? Shift of a few screen points, in pixels.
            var vpRender = GL.GetGPUProjectionMatrix(baseProj, true) * cam.worldToCameraMatrix;
            var inv = vpCur.inverse;
            float shift = 0f;
            foreach (var nd in new[] { new Vector3(0f, 0f, 0.015f), new Vector3(-0.8f, -0.8f, 0.015f), new Vector3(0.8f, 0.8f, 0.012f) })
            {
                var wp = inv.MultiplyPoint(nd);
                var a = vpCur.MultiplyPoint(wp); var b = vpRender.MultiplyPoint(wp);
                shift = Mathf.Max(shift, new Vector2((b.x - a.x) * 0.5f * w, (b.y - a.y) * 0.5f * h).magnitude);
            }
            viewShiftMax = Mathf.Max(viewShiftMax, shift); viewShiftSum += shift; viewShiftN++;
            var p = cam.projectionMatrix;
            bool same = true;
            for (int i = 0; i < 16; i++) if (Mathf.Abs(p[i] - jitSet[i]) > 1e-7f) { same = false; break; }
            if (same) jitOk++; else { if (jitBad++ == 0) Main.Log("jitter: projection at render differs from the jittered one: [0,2]=" + p[0, 2] + " expected " + jitSet[0, 2] + ", [1,2]=" + p[1, 2] + " expected " + jitSet[1, 2] + ", [0,0]=" + p[0, 0] + " expected " + jitSet[0, 0]); }
            if (Time.unscaledTime >= jitNext)
            {
                jitNext = Time.unscaledTime + 2f;
                Main.Log("jitter: " + cam.name + " rendered with our jittered projection in " + jitOk + " of " + (jitOk + jitBad) + " frames; this frame's jitter " + jitterPx.x.ToString("F3") + "," + jitterPx.y.ToString("F3") + " px"
                    + "; view moved between our OnPreCull and the render: mean " + (viewShiftN > 0 ? viewShiftSum / viewShiftN : 0f).ToString("F2") + " px, max " + viewShiftMax.ToString("F2") + " px");
                viewShiftMax = viewShiftSum = 0f; viewShiftN = 0;
                jitOk = jitBad = 0;
            }
        }

        // Debug: compares the matrices Unity's own motion vector pass used this frame (shader globals) with ours, as the screen shift of a few
        // points on the globe, in render pixels.
        static readonly int NonJitVPId = Shader.PropertyToID("_NonJitteredVP"), PrevVPId = Shader.PropertyToID("_PreviousVP");
        static double mpCurOurs, mpCurNoFlip, mpPrevOurs, mpPrevVsCur, mpOursMotion; static int mpN; static float mpNext;
        static float Shift(Matrix4x4 a, Matrix4x4 b)
        {
            var inv = vpCur.inverse; float m = 0f;
            foreach (var nd in new[] { new Vector3(0f, 0f, 0.015f), new Vector3(-0.7f, -0.7f, 0.015f), new Vector3(0.7f, 0.7f, 0.013f) })
            {
                var wp = inv.MultiplyPoint(nd);
                var pa = a.MultiplyPoint(wp); var pb = b.MultiplyPoint(wp);
                m = Mathf.Max(m, new Vector2((pb.x - pa.x) * 0.5f * w, (pb.y - pa.y) * 0.5f * h).magnitude);
            }
            return m;
        }
        static void MatrixProbe(Camera cam)
        {
            var uCur = Shader.GetGlobalMatrix(NonJitVPId); var uPrev = Shader.GetGlobalMatrix(PrevVPId);
            var noFlip = GL.GetGPUProjectionMatrix(baseProj, false) * cam.worldToCameraMatrix;
            mpCurOurs += Shift(uCur, vpCur); mpCurNoFlip += Shift(uCur, noFlip); mpPrevOurs += Shift(uPrev, vpPrev); mpPrevVsCur += Shift(uPrev, uCur); mpOursMotion += Shift(vpPrev, vpCur); mpN++;
            if (Time.unscaledTime < mpNext) return;
            mpNext = Time.unscaledTime + 1f;
            Main.Log(string.Format("matrices ({0} frames, mean px): Unity current vs ours {1:F2} (vs ours unflipped {2:F2}); Unity previous vs ours {3:F2}; Unity's own motion {4:F2}; our motion {5:F2}",
                mpN, mpCurOurs / mpN, mpCurNoFlip / mpN, mpPrevOurs / mpN, mpPrevVsCur / mpN, mpOursMotion / mpN));
            mpCurOurs = mpCurNoFlip = mpPrevOurs = mpPrevVsCur = mpOursMotion = 0; mpN = 0;
        }

        static void OnPostRender(Camera cam)
        {
            if (!applied || cam != activeCam) return;
            XformProbe.PostRender(cam);
            if (Main.MvCheckOn.Value) MatrixProbe(cam);
            cam.ResetProjectionMatrix();   // scripts that read the projection (mouse picking) see the plain one
            cam.useJitteredProjectionMatrixForTransparentRendering = transparentsWas;
            applied = false;
            var t = cam.transform;
            lastPos = t.position; lastRot = t.rotation; lastFov = cam.fieldOfView;
            lastVP = vpCur; lastFrame = Time.frameCount; lastCamId = cam.GetInstanceID();
            ObjectMotion.End();
        }
    }
    // Debug: compares Unity's motion vectors with camera-only motion computed from depth (DLSS convention: previous minus current, pixels, y down)
    // while the camera moves, and logs the depth the camera produced, the per-axis scale that maps Unity's values onto the depth-based ones
    // (median of the ratios, in multiples of width/height: the right MvScale values) and how often the two agree in sign.
    public static class MvCheck
    {
        const int SW = 480, SH = 270;
        static RenderTexture depthMv, smallUnity, smallDepthMv, smallDepth;
        static float next;
        static float[] uni, dmv, dep; static int got, w, h; static bool failed;
        static readonly List<float> rx = new List<float>(SW * SH), ry = new List<float>(SW * SH);
        static Vector3 camPos; static Quaternion camRot; static bool stillCam;

        public static void Release()
        {
            foreach (var rt in new[] { depthMv, smallUnity, smallDepthMv, smallDepth }) if (rt != null) { rt.Release(); Object.Destroy(rt); }
            depthMv = smallUnity = smallDepthMv = smallDepth = null;
        }

        static RenderTexture Small(RenderTextureFormat f, string n)
        {
            var rt = new RenderTexture(SW, SH, 0, f, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point, name = n };
            rt.Create();
            return rt;
        }

        public static void Queue(CommandBuffer cmd, Camera cam, RenderTexture depth, RenderTexture unityMv, int rw, int rh, Matrix4x4 invVPj, Matrix4x4 vpCur, Matrix4x4 vpPrev)
        {
            var t = cam.transform;
            bool moving = Vector3.Distance(t.position, camPos) > 1e-4f || Quaternion.Angle(t.rotation, camRot) > 0.01f;
            camPos = t.position; camRot = t.rotation;
            if (Time.unscaledTime < next || got > 0 && got < 3) return;
            stillCam = !moving;
            next = Time.unscaledTime + 1f;
            if (depthMv == null || depthMv.width != rw || depthMv.height != rh)
            {
                Release();
                depthMv = new RenderTexture(rw, rh, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear) { enableRandomWrite = true, name = "PPUpscaler check" };
                depthMv.Create();
                smallUnity = Small(RenderTextureFormat.RGFloat, "PPUpscaler check u");
                smallDepthMv = Small(RenderTextureFormat.RGFloat, "PPUpscaler check d");
                smallDepth = Small(RenderTextureFormat.RFloat, "PPUpscaler check z");
            }
            w = rw; h = rh; got = 0;
            Dlss.QueueDepthMv(cmd, depth, depthMv, rw, rh, invVPj, vpCur, vpPrev);
            cmd.Blit(unityMv, smallUnity);
            cmd.Blit(depthMv, smallDepthMv);
            cmd.Blit(depth, smallDepth);
            // The readbacks are copied into arrays allocated once: allocating them per check made the garbage collector stutter the game.
            if (uni == null) { uni = new float[SW * SH * 2]; dmv = new float[SW * SH * 2]; dep = new float[SW * SH]; }
            failed = false;
            cmd.RequestAsyncReadback(smallUnity, r => { if (r.hasError) failed = true; else r.GetData<float>().CopyTo(uni); Done(); });
            cmd.RequestAsyncReadback(smallDepthMv, r => { if (r.hasError) failed = true; else r.GetData<float>().CopyTo(dmv); Done(); });
            cmd.RequestAsyncReadback(smallDepth, r => { if (r.hasError) failed = true; else r.GetData<float>().CopyTo(dep); Done(); });
        }

        static void Done()
        {
            if (++got < 3) return;
            if (failed) { Main.Log("mvcheck: readback failed"); return; }
            try { Fit(); } catch (System.Exception e) { Main.Log("mvcheck failed: " + e.Message); }
        }

        static float Median(List<float> l) { if (l.Count == 0) return float.NaN; l.Sort(); return l[l.Count / 2]; }

        static void Fit()
        {
            // The depth the camera produced (reversed Z: 1 = near plane, 0 = far plane or sky).
            int n = dep.Length, zero = 0, one = 0; float zmin = float.MaxValue, zmax = float.MinValue; double zsum = 0;
            foreach (var z in dep) { if (z <= 0f) zero++; if (z >= 0.9999f) one++; zmin = Mathf.Min(zmin, z); zmax = Mathf.Max(zmax, z); zsum += z; }
            Main.Log(string.Format("mvcheck depth: min={0:G4} max={1:G4} mean={2:G4} far/sky={3:P1} near(=1)={4:P1}", zmin, zmax, zsum / n, (float)zero / n, (float)one / n));

            rx.Clear(); ry.Clear();
            int agreeX = 0, agreeY = 0, nx = 0, ny = 0, used = 0; double mag = 0;
            for (int i = 0; i < n && 2 * i + 1 < uni.Length && 2 * i + 1 < dmv.Length; i++)
            {
                if (dep[i] <= 0f) continue;
                float dx = dmv[2 * i], dy = dmv[2 * i + 1], ux = uni[2 * i] * w, uy = uni[2 * i + 1] * h;   // depth-based in pixels; Unity's converted to pixels
                if (float.IsNaN(dx) || float.IsNaN(dy) || Mathf.Abs(dx) > 4096f || Mathf.Abs(dy) > 4096f) continue;
                used++; mag += Mathf.Sqrt(dx * dx + dy * dy);
                if (Mathf.Abs(ux) > 1f && Mathf.Abs(dx) > 1f) { rx.Add(dx / ux); nx++; if (dx * ux < 0) agreeX++; }
                if (Mathf.Abs(uy) > 1f && Mathf.Abs(dy) > 1f) { ry.Add(dy / uy); ny++; if (dy * uy < 0) agreeY++; }
            }
            if (stillCam)
            {   // camera still: the depth-based motion is about 0, so whatever Unity reports is object motion or an error
                double ua = 0, usx = 0, usy = 0, da = 0; int big = 0, m = 0;
                for (int i = 0; i < n && 2 * i + 1 < uni.Length; i++)
                {
                    if (dep[i] <= 0f) continue;
                    float ux = uni[2 * i] * w, uy = uni[2 * i + 1] * h, dx = dmv[2 * i], dy = dmv[2 * i + 1];
                    float um = Mathf.Sqrt(ux * ux + uy * uy);
                    ua += um; usx += ux; usy += uy; if (um > 0.5f) big++;
                    if (!float.IsNaN(dx) && Mathf.Abs(dx) < 4096f && Mathf.Abs(dy) < 4096f) da += Mathf.Sqrt(dx * dx + dy * dy);
                    m++;
                }
                if (m > 0) Main.Log(string.Format("mvcheck still camera: Unity motion mean |{0:F2}| px, mean ({1:F2},{2:F2}) px, over 0.5 px on {3:P1} of pixels; depth-based motion mean {4:F2} px", ua / m, usx / m, usy / m, (float)big / m, da / m));
                return;
            }
            if (nx + ny < 200) { Main.Log("mvcheck: too little camera motion (" + (nx + ny) + " samples with > 1 px)"); return; }
            Main.Log(string.Format("mvcheck: pixels={0} meanDepthMotion={1:F1}px  x: scale={2:F3}*W opposite-sign={3:P0} (n={4})  y: scale={5:F3}*H opposite-sign={6:P0} (n={7})  current MvScale {8},{9}",
                used, used > 0 ? mag / used : 0, Median(rx), nx > 0 ? (float)agreeX / nx : 0, nx, Median(ry), ny > 0 ? (float)agreeY / ny : 0, ny, Main.MvScaleX.Value, Main.MvScaleY.Value));
        }
    }
}
