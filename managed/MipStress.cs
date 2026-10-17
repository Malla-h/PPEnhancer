using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PPUpscaler
{
    // Debug: does changing the quality mode over and over leave the game slower afterwards? Measures the frame time at one quality (vsync off), runs
    // many quality switches (each held long enough for the texture bias to be applied), returns to that quality and measures again. Round 1 runs with the
    // texture mip bias on (as configured), round 2 with it off as a control, so a slowdown that only round 1 shows belongs to the bias. Keep the camera still.
    public class MipStress : MonoBehaviour
    {
        const float Hold = 3.5f, Warm = 4f, Measure = 8f;
        static MipStress inst;
        public static bool Running;
        string status = "";
        void Awake() { inst = this; }
        public static void Run() { if (inst != null && !Running) inst.StartCoroutine(inst.Test()); }

        IEnumerator Test()
        {
            Running = true;
            int keepQ = Main.Quality.Value, keepM = Main.MipBiasMode.Value, keepV = QualitySettings.vSyncCount, keepT = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            var sb = new StringBuilder("mip stress test:");
            int[] cycle = { 3, 2, 1, 2, 4, 2, 0, 2 };   // Performance, Quality, Balanced, ... as Main.Quality values (see QualityNames)
            foreach (int mode in new[] { Main.MipBiasMode.Value > 0 ? Main.MipBiasMode.Value : 1, 0 })
            {
                Main.MipBiasMode.Value = mode;
                Main.Quality.Value = 2;
                status = "mode " + mode + ": baseline";
                float[] before = null, after = null;
                yield return Sample(r => before = r);
                for (int round = 0; round < 3; round++)
                    foreach (int q in cycle) { Main.Quality.Value = q; status = "mode " + mode + ": switching"; yield return new WaitForSecondsRealtime(Hold); }
                Main.Quality.Value = 2;
                status = "mode " + mode + ": after";
                yield return Sample(r => after = r);
                sb.AppendFormat("\n  mip bias mode {0}: before p50 {1:F2} ms p95 {2:F2} ms mean {3:F2} | after {4} switches p50 {5:F2} ms p95 {6:F2} ms mean {7:F2} | textures {8}, managed {9} MB",
                    mode, before[0], before[1], before[2], cycle.Length * 3, after[0], after[1], after[2], Resources.FindObjectsOfTypeAll<Texture>().Length, System.GC.GetTotalMemory(false) >> 20);
            }
            Main.MipBiasMode.Value = keepM; Main.Quality.Value = keepQ;
            QualitySettings.vSyncCount = keepV; Application.targetFrameRate = keepT;
            Main.Log(sb.ToString());
            status = ""; Running = false;
        }

        // p50, p95, mean of the frame time in ms after a warm-up.
        IEnumerator Sample(System.Action<float[]> done)
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < Warm) yield return null;
            var f = new List<float>(2048); float sum = 0; t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < Measure) { float d = Time.unscaledDeltaTime * 1000f; f.Add(d); sum += d; yield return null; }
            f.Sort();
            done(new[] { f[f.Count / 2], f[f.Count * 95 / 100], sum / f.Count });
        }

        void OnGUI() { if (Running) GUI.Label(new Rect(20, 20, 900, 30), "PPUpscaler mip stress test: " + status + ". Keep the camera still. About 4 minutes."); }
        void OnDestroy() { if (Running) { Running = false; } }
    }
}
