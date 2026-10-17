using UnityEngine;

namespace PPUpscaler
{
    // Debug (with the motion vector check on, in geoscape): Unity's motion vectors show the still globe rotating about 0.05 degrees per frame.
    // This records the world rotation of the globe, its root and the camera at three points of every frame (PPv2's OnPreCull, the start and the
    // end of the camera's render) and logs once per second the largest change between consecutive frames and within a frame.
    public static class XformProbe
    {
        static Transform globe, yRoot, xRoot;
        static Camera camSeen;
        static Quaternion gPre, gPost, yPre, xPre, cPre; static Vector3 cPosPre;
        static bool have;
        static float maxFrameG, maxFrameY, maxFrameX, maxFrameC, maxFrameCPos, maxPostToPreG, maxInRenderG, next;
        static int frames;

        static void Find(Camera cam)
        {
            if (globe != null && camSeen == cam) return;
            camSeen = cam; have = false;
            var root = GameObject.Find("Geoscape");
            if (root == null) { globe = null; return; }
            xRoot = root.transform.Find("XRotation");
            yRoot = xRoot != null ? xRoot.Find("YRotation") : null;
            globe = yRoot != null ? yRoot.Find("GeoscapeVisual_Showroom/8k8kGlobe/Sphere001") : null;
            Main.Log("xform probe: globe " + (globe != null ? "found" : "not found"));
        }

        public static void PreCull(Camera cam)
        {
            if (!Main.MvCheckOn.Value) return;
            Find(cam);
            if (globe == null) return;
            var g = globe.rotation; var y = yRoot.rotation; var x = xRoot.rotation; var c = cam.transform.rotation; var cp = cam.transform.position;
            if (have)
            {
                maxFrameG = Mathf.Max(maxFrameG, Quaternion.Angle(g, gPre));
                maxFrameY = Mathf.Max(maxFrameY, Quaternion.Angle(y, yPre));
                maxFrameX = Mathf.Max(maxFrameX, Quaternion.Angle(x, xPre));
                maxFrameC = Mathf.Max(maxFrameC, Quaternion.Angle(c, cPre));
                maxFrameCPos = Mathf.Max(maxFrameCPos, Vector3.Distance(cp, cPosPre));
                maxPostToPreG = Mathf.Max(maxPostToPreG, Quaternion.Angle(g, gPost));
                frames++;
            }
            gPre = g; yPre = y; xPre = x; cPre = c; cPosPre = cp;
        }

        public static void PostRender(Camera cam)
        {
            if (!Main.MvCheckOn.Value || globe == null || cam != camSeen) return;
            gPost = globe.rotation; have = true;
            maxInRenderG = Mathf.Max(maxInRenderG, Quaternion.Angle(gPost, gPre));
            if (Time.unscaledTime >= next)
            {
                next = Time.unscaledTime + 1f;
                Main.Log(string.Format("xform probe ({0} frames, degrees): globe per frame {1:F4}, globe root Y {2:F4}, X {3:F4}, camera turn {4:F4} move {5:F5}; globe changed between render and next frame {6:F4}, during render {7:F4}",
                    frames, maxFrameG, maxFrameY, maxFrameX, maxFrameC, maxFrameCPos, maxPostToPreG, maxInRenderG));
                maxFrameG = maxFrameY = maxFrameX = maxFrameC = maxFrameCPos = maxPostToPreG = maxInRenderG = 0; frames = 0;
            }
        }
    }
}
