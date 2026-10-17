using UnityEngine;

namespace PPUpscaler
{
    // Geoscape: the depth-based motion vectors only know the camera, but the "Core" sphere inside the globe turns on its own (about 0.7 degrees a
    // second with the game clock running; it is the starry layer seen through the oceans). Measured: rebuilt from depth, core pixels lie at 6.372 to
    // 6.384 from the centre and land at 6.392 to 6.400, with nothing in between, so a sphere halfway between the two radii tells them apart.
    // Core pixels get the core's own movement since the last rendered frame on top of the camera's. (Unity's motion vectors do include the core's
    // turning, but they also put it on the land, which then blurs.)
    public static class ObjectMotion
    {
        public static bool On;
        public static Matrix4x4 Delta = Matrix4x4.identity;
        public static Vector4 Sphere;
        static Transform core, globe;
        static float coreR, globeR;
        static Matrix4x4 prev, current;
        static bool havePrev;
        static int loggedFor;

        static float Radius(Transform t)
        {
            var mf = t.GetComponent<MeshFilter>();
            return mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.extents.x * t.lossyScale.x : -1f;
        }

        static void Find()
        {
            var g = GameObject.Find("Geoscape");
            var show = g != null ? g.transform.Find("XRotation/YRotation/GeoscapeVisual_Showroom") : null;
            core = show != null ? show.Find("Core") : null;
            globe = show != null ? show.Find("8k8kGlobe/Sphere001") : null;
            coreR = core != null ? Radius(core) : -1f;
            globeR = globe != null ? Radius(globe) : -1f;
        }

        public static void Begin(Camera cam, bool reset)
        {
            On = false;
            if (!Main.CoreMotion.Value || !Dlaa.DepthMotion(cam)) { havePrev = false; return; }
            if (core == null || globe == null) Find();
            if (core == null || globe == null || coreR <= 0f || globeR <= coreR) { havePrev = false; return; }
            current = core.localToWorldMatrix;
            float thr = (coreR + globeR) * 0.5f;
            var c = core.position;
            Sphere = new Vector4(c.x, c.y, c.z, thr * thr);
            Delta = havePrev && !reset ? prev * current.inverse : Matrix4x4.identity;
            On = true;
            if (loggedFor != cam.GetInstanceID()) { loggedFor = cam.GetInstanceID(); Main.Log("object motion: core radius " + coreR.ToString("F3") + ", globe " + globeR.ToString("F3") + ", split at " + thr.ToString("F3")); }
        }

        // After the camera rendered: this frame's core matrix is the next frame's previous one.
        public static void End()
        {
            if (On) { prev = current; havePrev = true; }
        }
    }
}
