using UnityEngine;
using UnityEngine.Rendering;

namespace PPUpscaler
{
    // Contact shadows: a short screen-space march from every lit surface toward the sun (native compute pass, ContactData), darkening the lit scene
    // where a nearby surface blocks the sun. It adds the small-scale shadowing the game's shadow maps miss (debris, grass, props, units' feet).
    //
    // It runs on the lit opaque scene right after the game's opaque image effects (AO, fog of war), BEFORE the transparent objects are drawn, so
    // smoke, fire and other effects are drawn on top of the shadowed ground instead of being shadowed as if they were it. DLSS (which runs later,
    // on the jittered render-size image) averages the dithered march over frames.
    //
    // Limit: the game's lighting is already combined into one colour here, so the darkening cannot touch only the sun's share; it is applied at a
    // moderate strength instead, so that surfaces already in the shadow maps' shadow do not turn black.
    public static class ContactShadows
    {
        static CommandBuffer cb;
        static Camera attached;
        static CameraEvent attachedEvent;
        static RenderTexture tmpIn, tmpOut, depthTex, normTex, statTex, statSmall;
        static int tw, th;
        static Light sun;
        static float sunCheck;
        static string loggedSun = "";
        static float fogLogged = -1f;
        static float nextStat, nextBeat; static int recorded, statRequests, statReturned, statErrors, lastRendered, lastFrame;

        static bool Wanted(Camera cam)
        {
            if (!Main.ContactShadowsOn.Value || cam == null) return false;
            foreach (var n in Main.ContactShadowCameras.Value.Split(',')) if (n.Trim() == cam.name) return true;
            return false;
        }

        static Light FindSun()
        {
            var s = RenderSettings.sun;
            if (s != null && s.isActiveAndEnabled && s.type == LightType.Directional) return s;
            foreach (var l in Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Directional && l.isActiveAndEnabled && l.shadows != LightShadows.None) return l;
            return null;
        }

        static RenderTexture Make(string name, bool uav, RenderTextureFormat f, int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, f, RenderTextureReadWrite.Linear) { name = name, enableRandomWrite = uav, filterMode = FilterMode.Point, useMipMap = false };
            rt.Create();
            return rt;
        }

        // Called from Dlaa.Begin each frame the camera is upscaled/anti-aliased by the mod (render size w x h, the jittered matrices of this frame).
        public static void Record(Camera cam, int w, int h, Matrix4x4 invVPj, Matrix4x4 vpJit)
        {
            if (!Wanted(cam) || !Dlss.ContactReady) { Off(cam); return; }
            if (sun == null || Time.unscaledTime > sunCheck)
            {
                sun = FindSun(); sunCheck = Time.unscaledTime + 5f;
                string d = sun != null ? sun.name + " (" + sun.type + ", shadows " + sun.shadows + ")" : "none";
                if (d != loggedSun) { loggedSun = d; Main.Log("contact shadows: sun light " + d); }
            }
            if (sun == null) { Off(cam); return; }
            if (tmpIn == null || tw != w || th != h)
            {
                Release();
                tw = w; th = h;
                tmpIn = Make("PPUpscaler cs color", false, RenderTextureFormat.ARGBHalf, w, h);
                tmpOut = Make("PPUpscaler cs out", true, RenderTextureFormat.ARGBHalf, w, h);
                depthTex = Make("PPUpscaler cs depth", false, RenderTextureFormat.RFloat, w, h);
                normTex = Make("PPUpscaler cs normals", false, RenderTextureFormat.ARGBHalf, w, h);
                statTex = Make("PPUpscaler cs stats", true, RenderTextureFormat.ARGBHalf, w, h);
                statSmall = new RenderTexture(256, 144, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "PPUpscaler cs stats small", filterMode = FilterMode.Bilinear };
                statSmall.Create();
            }
            if (cb == null) cb = new CommandBuffer { name = "PPUpscaler contact shadows" };
            if (Main.CsStats.Value && Time.unscaledTime >= nextBeat)
            {
                nextBeat = Time.unscaledTime + 2f;
                Main.Log(string.Format("contact shadows heartbeat: recorded {0} frames, {1} stat requests, attached={2} at {3}, buffers on camera={4}, sun={5}, camera layer mask={6}, path={7}",
                    recorded, statRequests, attached != null, attachedEvent, cam.commandBufferCount, sun != null, cam.cullingMask, cam.actualRenderingPath)
                    + string.Format("; readbacks returned {0}, errors {1}; frames simulated {2}, rendered {3} since the last heartbeat; camera enabled {4}",
                        statReturned, statErrors, Time.frameCount - lastFrame, Time.renderedFrameCount - lastRendered, cam.enabled));
                lastFrame = Time.frameCount; lastRendered = Time.renderedFrameCount;
            }
            // Stage 0 (default): right after the opaque scene is drawn, BEFORE the post-processing effects that apply fog, ambient occlusion and the
            // fog of war (in deferred rendering PPv2's Fog is what applies Unity's fog), so those are applied on top of the shadowed scene.
            // Stage 1: after them (the first version: it darkened the fog and the fog of war's darkness too).
            // Stage 0 uses AfterLighting: deferred rendering always runs it. (AfterForwardOpaque, used first, is skipped by Unity on frames where no
            // forward-rendered opaque object is visible, which made the shadows vanish when zoomed in: measured, the pass did not run at all.)
            var ev = Main.CsStage.Value == 0 ? CameraEvent.AfterLighting : Main.CsStage.Value == 2 ? CameraEvent.AfterForwardOpaque : CameraEvent.AfterImageEffectsOpaque;
            if (attached != cam || attachedEvent != ev) { Off(null); cam.AddCommandBuffer(ev, cb); attached = cam; attachedEvent = ev; Main.Log("contact shadows: attached to " + cam.name + " at " + ev); }
            bool fogAlreadyApplied = Main.CsStage.Value == 1;
            cb.Clear();
            cb.Blit(BuiltinRenderTextureType.CameraTarget, tmpIn);
            cb.Blit(BuiltinRenderTextureType.ResolvedDepth, depthTex);
            cb.Blit(BuiltinRenderTextureType.GBuffer2, normTex);   // world-space normals, still valid here (PPv2's reflections read them at this stage too)
            var toSun = -sun.transform.forward;
            if (RenderSettings.fog && fogLogged != (int)RenderSettings.fogMode + RenderSettings.fogDensity)
            {
                fogLogged = (int)RenderSettings.fogMode + RenderSettings.fogDensity;
                Main.Log("contact shadows: fog " + RenderSettings.fogMode + ", density " + RenderSettings.fogDensity.ToString("F4") + ", start " + RenderSettings.fogStartDistance + ", end " + RenderSettings.fogEndDistance + ", colour " + RenderSettings.fogColor);
            }
            Dlss.QueueContact(cb, tmpIn, depthTex, normTex, tmpOut, w, h, invVPj, vpJit, cam.transform.position, cam.transform.forward, toSun,
                Time.frameCount, Main.CsSteps.Value, Main.CsDistance.Value, Main.CsThickness.Value, Main.CsStrength.Value, Main.CsBias.Value, Main.CsDebug.Value,
                QualitySettings.activeColorSpace == ColorSpace.Linear ? RenderSettings.fogColor.linear : RenderSettings.fogColor,
                RenderSettings.fogDensity, RenderSettings.fogStartDistance, RenderSettings.fogEndDistance, RenderSettings.fog && fogAlreadyApplied ? (int)RenderSettings.fogMode : 0, Main.CsReach.Value * h);
            cb.Blit(tmpOut, BuiltinRenderTextureType.CameraTarget);
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);   // back to the camera's colour and depth
            recorded++;
            if (Main.CsStats.Value && Time.unscaledTime >= nextStat)
            {
                nextStat = Time.unscaledTime + 0.5f; statRequests++;
                // a second run of the pass into its own texture with the debug view (white = lit, dark = contact shadow), counted on the CPU
                Dlss.QueueContact(cb, tmpIn, depthTex, normTex, statTex, w, h, invVPj, vpJit, cam.transform.position, cam.transform.forward, toSun,
                    Time.frameCount, Main.CsSteps.Value, Main.CsDistance.Value, Main.CsThickness.Value, Main.CsStrength.Value, Main.CsBias.Value, true,
                    Color.black, 0f, 0f, 0f, 0, Main.CsReach.Value * h);
                cb.Blit(statTex, statSmall);
                cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);
                var pos = cam.transform.position; float fov = cam.fieldOfView; int frame = Time.frameCount; int size = w;
                cb.RequestAsyncReadback(statSmall, 0, TextureFormat.RGBA32, r =>
                {
                    if (r.hasError) { statErrors++; Main.Log("contact shadow stats: readback error (frame " + frame + ")"); return; }
                    statReturned++;
                    var d = r.GetData<byte>();
                    int n = d.Length / 4, dark = 0; double sum = 0;
                    for (int i = 0; i < n; i++) { float v = d[4 * i] / 255f; sum += 1f - v; if (v < 0.9f) dark++; }
                    Main.Log(string.Format("contact shadow stats frame {0}: {1:P1} of the screen shaded (mean term {2:F3}); camera at ({3:F1}, {4:F1}, {5:F1}), fov {6:F1}, render width {7}",
                        frame, (float)dark / n, sum / n, pos.x, pos.y, pos.z, fov, size));
                });
            }
        }

        // Detaches from cam (null: from whichever camera it is on).
        public static void Off(Camera cam)
        {
            if (attached == null || cb == null || (cam != null && cam != attached)) return;
            attached.RemoveCommandBuffer(attachedEvent, cb);
            Main.Log("contact shadows: detached from " + attached.name);
            attached = null;
            cb.Clear();
        }

        public static void Release()
        {
            foreach (var rt in new[] { tmpIn, tmpOut, depthTex, normTex, statTex, statSmall }) if (rt != null) { rt.Release(); Object.Destroy(rt); }
            tmpIn = tmpOut = depthTex = normTex = statTex = statSmall = null;
            tw = th = 0;
        }
    }
}
