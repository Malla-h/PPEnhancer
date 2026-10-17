using System.Collections.Generic;
using UnityEngine;

namespace PPUpscaler
{
    // Debug: hides the geoscape's transparent atmosphere and cloud layers while switched on (not saved), to test whether they are what
    // jiggles when the view moves (they have no depth, so the depth-based motion moves them like the ground underneath).
    public static class HideLayers
    {
        static readonly List<Renderer> hidden = new List<Renderer>();
        public static bool On { get { return hidden.Count > 0; } }

        public static void Set(bool on)
        {
            if (!on) { foreach (var r in hidden) if (r != null) r.enabled = true; hidden.Clear(); Main.Log("hide layers: restored"); return; }
            if (On) return;
            var root = GameObject.Find("Geoscape");
            if (root == null) { Main.Log("hide layers: no geoscape"); return; }
            var names = new System.Text.StringBuilder();
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                string n = r.name.ToLowerInvariant();
                if (!r.enabled || !(n.Contains("atmosphere") || n.Contains("cloud"))) continue;
                r.enabled = false; hidden.Add(r);
                names.Append(r.name).Append(' ');
            }
            Main.Log("hide layers: hid " + hidden.Count + ": " + names);
        }
    }
}
