using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace PPUpscaler
{
    // The exposure the game applies before its tonemapper, for DLSS (instead of DLSS's own estimate). PPv2 multiplies the scene by its auto
    // exposure value (a 1 x 1 texture, when auto exposure is on) and, in the HDR colour grading modes, by 2^post exposure. Measured: geoscape has
    // no auto exposure and post exposure 0 (exposure 1), and with DLSS's own estimate its brightest pixels came out at 73 % of the input; tactical
    // has auto exposure (value 2.0) and post exposure -0.5 EV.
    public static class Exposure
    {
        public static RenderTexture Texture;
        static float logNext;

        // Records filling our 1 x 1 texture with this frame's auto exposure (or 1 without it) and returns the scale (2^post exposure).
        public static float Queue(CommandBuffer cmd, PostProcessLayer layer)
        {
            if (!Main.GameExposure.Value || layer == null) return 1f;
            if (Texture == null)
            {
                Texture = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { name = "PPUpscaler exposure" };
                Texture.Create();
            }
            var ae = layer.GetSettings<AutoExposure>();
            bool aeOn = ae != null && ae.enabled.value && ae.active;
            var aeTex = aeOn ? HighlightTest.AutoExposureTexture(layer) : null;   // last frame's value: PPv2 updates it later in the frame
            if (aeTex != null && aeTex.IsCreated()) cmd.CopyTexture(aeTex, Texture);
            else { cmd.SetRenderTarget(Texture); cmd.ClearRenderTarget(false, true, new Color(1f, 0f, 0f, 0f)); }
            var cg = layer.GetSettings<ColorGrading>();
            float scale = cg != null && cg.enabled.value && cg.active && cg.gradingMode.value != GradingMode.LowDefinitionRange ? Mathf.Pow(2f, cg.postExposure.value) : 1f;
            if (Time.unscaledTime > logNext)
            {
                logNext = Time.unscaledTime + 30f;
                Main.Log("exposure for DLSS: " + (aeTex != null ? "the game's auto exposure" : "1 (no auto exposure)") + " x " + scale.ToString("F3") + " (post exposure)");
            }
            return scale;
        }

        public static void Release()
        {
            if (Texture != null) { Texture.Release(); Object.Destroy(Texture); Texture = null; }
        }
    }
}
