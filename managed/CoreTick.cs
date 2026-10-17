using UnityEngine;

namespace PPUpscaler
{
    // Per-frame work of the core that is not tied to a feature (config file reload).
    [DefaultExecutionOrder(-32000)]
    public class CoreTick : MonoBehaviour
    {
        // The camera's render size is a smaller rect while it draws; every script then sees the full screen again (mouse picking, world to screen).
        void Update() { Dlaa.RestoreRect(); Main.Tick(); }
        System.Collections.IEnumerator Start()
        {
            while (true) { yield return new WaitForEndOfFrame(); Dlaa.RestoreRect(); }
        }
    }
}
