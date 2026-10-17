using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace PPUpscaler
{
    // The game's ambient occlusion (PPv2's Multi-Scale Volumetric Obscurance) measures its reach in pixels: each level samples about 10 texels
    // (2 tan(fov/2) * 10 / width), so at a lower render size it reaches further across the screen (twice as far at Performance) and the shading
    // looks different from native. When rendering below native, this runs it at the output size instead, on an enlarged copy of the depth, and
    // shrinks the result into the render-size AO texture that PPv2 then applies before DLSS: the same reach as at native, at native's AO cost.
    public static class AoFullRes
    {
        static Type msvo;
        static FieldInfo widths, heights;
        static MethodInfo alloc, downsample, render, upsample, release, tanHalf, getSize, getSizeArray;
        static object L1, L2, L3, L4, L5, L6, Original;
        static RenderTexture fullDepth, fullAo;
        static bool logged;

        public static void Install(Harmony harmony)
        {
            msvo = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.MultiScaleVO");
            if (msvo == null) { Main.Log("AO at output size: MultiScaleVO not found"); return; }
            var bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            widths = msvo.GetField("m_ScaledWidths", bf); heights = msvo.GetField("m_ScaledHeights", bf);
            alloc = msvo.GetMethod("PushAllocCommands", bf); downsample = msvo.GetMethod("PushDownsampleCommands", bf);
            render = msvo.GetMethod("PushRenderCommands", bf); upsample = msvo.GetMethod("PushUpsampleCommands", bf);
            release = msvo.GetMethod("PushReleaseCommands", bf); tanHalf = msvo.GetMethod("CalculateTanHalfFovHeight", bf);
            getSize = msvo.GetMethod("GetSize", bf); getSizeArray = msvo.GetMethod("GetSizeArray", bf);
            var mip = msvo.GetNestedType("MipLevel", BindingFlags.NonPublic | BindingFlags.Public);
            Original = Enum.Parse(mip, "Original"); L1 = Enum.Parse(mip, "L1"); L2 = Enum.Parse(mip, "L2"); L3 = Enum.Parse(mip, "L3");
            L4 = Enum.Parse(mip, "L4"); L5 = Enum.Parse(mip, "L5"); L6 = Enum.Parse(mip, "L6");
            harmony.Patch(AccessTools.Method(msvo, "GenerateAOMap"), prefix: new HarmonyMethod(typeof(AoFullRes), nameof(Prefix)));
        }

        public static void Release()
        {
            foreach (var rt in new[] { fullDepth, fullAo }) if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            fullDepth = fullAo = null;
        }

        static bool Prefix(object __instance, CommandBuffer cmd, Camera camera, RenderTargetIdentifier destination, RenderTargetIdentifier? depthMap, bool invert, bool isMSAA)
        {
            int ow, oh;
            if (!Main.AoAtOutputSize.Value || isMSAA || depthMap.HasValue || !Dlaa.UpscalingNow(camera, out ow, out oh)) return true;
            try
            {
                if (fullDepth == null || fullDepth.width != ow || fullDepth.height != oh)
                {
                    Release();
                    fullDepth = new RenderTexture(ow, oh, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { name = "PPUpscaler AO depth", filterMode = FilterMode.Point };
                    fullDepth.Create();
                    fullAo = new RenderTexture(ow, oh, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear) { name = "PPUpscaler AO", enableRandomWrite = true, filterMode = FilterMode.Bilinear };
                    fullAo.Create();
                }
                // the scene depth, enlarged to the output size (point sampling: no in-between depths at edges)
                cmd.Blit(BuiltinRenderTextureType.ResolvedDepth, fullDepth);
                var wa = (int[])widths.GetValue(__instance); var ha = (int[])heights.GetValue(__instance);
                wa[0] = ow; ha[0] = oh;
                for (int i = 1; i < 7; i++) { int n = 1 << i; wa[i] = (wa[0] + n - 1) / n; ha[i] = (ha[0] + n - 1) / n; }
                alloc.Invoke(__instance, new object[] { cmd, false });
                downsample.Invoke(__instance, new object[] { cmd, camera, (RenderTargetIdentifier?)new RenderTargetIdentifier(fullDepth), false });
                float th = (float)tanHalf.Invoke(__instance, new object[] { camera });
                Render(__instance, cmd, "TiledDepth1", "Occlusion1", L3, th);
                Render(__instance, cmd, "TiledDepth2", "Occlusion2", L4, th);
                Render(__instance, cmd, "TiledDepth3", "Occlusion3", L5, th);
                Render(__instance, cmd, "TiledDepth4", "Occlusion4", L6, th);
                Upsample(__instance, cmd, "LowDepth4", "Occlusion4", "LowDepth3", "Occlusion3", new RenderTargetIdentifier(Id("Combined3")), L4, L3, false);
                Upsample(__instance, cmd, "LowDepth3", "Combined3", "LowDepth2", "Occlusion2", new RenderTargetIdentifier(Id("Combined2")), L3, L2, false);
                Upsample(__instance, cmd, "LowDepth2", "Combined2", "LowDepth1", "Occlusion1", new RenderTargetIdentifier(Id("Combined1")), L2, L1, false);
                Upsample(__instance, cmd, "LowDepth1", "Combined1", "LinearDepth", null, new RenderTargetIdentifier(fullAo), L1, Original, invert);
                release.Invoke(__instance, new object[] { cmd });
                cmd.Blit(fullAo, destination);   // into PPv2's render-size AO texture, which it applies to the scene before DLSS
                if (!logged) { logged = true; Main.Log("ambient occlusion: computed at the output size " + ow + "x" + oh + " for the same reach as at native"); }
                return false;
            }
            catch (Exception e)
            {
                Main.Log("ambient occlusion at output size failed, using the game's: " + e);
                Main.AoAtOutputSize.Value = false;
                return true;
            }
        }

        static Type ids;
        static int Id(string name)
        {
            if (ids == null) ids = typeof(UnityEngine.Rendering.PostProcessing.PostProcessLayer).Assembly.GetType("UnityEngine.Rendering.PostProcessing.ShaderIDs");
            return (int)ids.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null);
        }

        static void Render(object m, CommandBuffer cmd, string src, string dst, object level, float th)
        {
            render.Invoke(m, new object[] { cmd, Id(src), Id(dst), getSizeArray.Invoke(m, new[] { level }), th, false });
        }

        static void Upsample(object m, CommandBuffer cmd, string lowDepth, string lowAo, string highDepth, string highAo, RenderTargetIdentifier dest, object lowLevel, object highLevel, bool invert)
        {
            upsample.Invoke(m, new object[] { cmd, Id(lowDepth), Id(lowAo), Id(highDepth), highAo != null ? (int?)Id(highAo) : null, dest,
                (Vector3)(Vector2)getSize.Invoke(m, new[] { lowLevel }), getSize.Invoke(m, new[] { highLevel }), false, invert });
        }
    }
}
