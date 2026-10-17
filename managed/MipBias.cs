using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PPUpscaler
{
    // Below native, textures are sampled at the render size's mip levels; a bias of log2(render scale) brings them back to native detail.
    // NVIDIA recommends one level more (log2(render scale) - 1, -1 even at DLAA), which measured at 4K DLAA doubles the G-buffer pass (+4 ms),
    // so that is an option, not the default. Unity's built-in pipeline has no global bias, so it is set per texture; each texture's own bias
    // is kept and put back when DLAA stops (or the core is reloaded).
    public class MipBias : MonoBehaviour
    {
        public static float Current;
        static readonly Dictionary<Texture, float> original = new Dictionary<Texture, float>();
        Coroutine scan;
        float lastScan, pendingSince = -1f, pending;

        static float Target()
        {
            int m = Main.MipBiasMode.Value;
            if (m <= 0 || !Dlaa.RunningRecently) return 0f;
            float b = (Mathf.Log(Dlaa.RenderScale, 2f) - (m >= 2 ? 1f : 0f)) * Main.MipBiasStrength.Value;
            return Mathf.Round(b * 4f) / 4f;   // quarter steps: fewer distinct sampler settings for the driver to keep
        }

        void Update()
        {
            float t = Target();
            // Rewriting the bias of ~2000 textures on every step made the game crawl (measured: seconds-long slowdowns that outlasted the change after
            // several quality switches in a row), so a new value is applied only once it has stayed the same for a moment.
            if (Mathf.Abs(t - Current) <= 0.001f) pendingSince = -1f;
            else if (t == 0f && !Dlaa.RunningRecently) { pendingSince = -1f; Apply(t); }
            else
            {
                if (pendingSince < 0f || t != pending) { pending = t; pendingSince = Time.unscaledTime; }
                else if (Time.unscaledTime - pendingSince > 2f) { pendingSince = -1f; Apply(t); }
            }
            if (scan == null && Current != 0f && Time.unscaledTime - lastScan > 10f)
                scan = StartCoroutine(Scan(false));   // textures loaded since the last pass (a new scene)
        }

        void Apply(float t)
        {
            {
                Current = t;
                if (scan != null) StopCoroutine(scan);
                scan = StartCoroutine(Scan(true));
                Main.Log("texture mip bias " + t.ToString("F2"));
            }
        }

        IEnumerator Scan(bool all)
        {
            lastScan = Time.unscaledTime;
            float bias = Current;
            int changed = 0, n = 0;
            if (bias == 0f) { RestoreAll(); scan = null; yield break; }
            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture>())
            {
                if (++n % 300 == 0) yield return null;   // spread the work over frames
                if (tex == null || !(tex is Texture2D || tex is Texture2DArray)) continue;
                float orig;
                if (!original.TryGetValue(tex, out orig)) { orig = tex.mipMapBias; original[tex] = orig; }
                else if (!all) continue;
                if (tex.mipMapBias != orig + bias) { tex.mipMapBias = orig + bias; changed++; }
            }
            if (changed > 0) Main.Log("mip bias " + bias.ToString("F2") + " applied to " + changed + " textures");
            scan = null;
        }

        static void RestoreAll()
        {
            int n = 0;
            foreach (var kv in original) if (kv.Key != null) { kv.Key.mipMapBias = kv.Value; n++; }
            original.Clear();
            Current = 0f;
            if (n > 0) Main.Log("mip bias removed from " + n + " textures");
        }

        void OnDestroy() { RestoreAll(); }
    }
}
