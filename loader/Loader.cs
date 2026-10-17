using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace PPUpscaler
{
    // Loads the mod (PPUpscaler.Core.dll) from bytes, so the file is never locked. With HotReload on, a changed file is picked up in the running
    // game: the old core's Stop undoes its patches and camera changes, and the new one starts.
    [BepInPlugin("mallahowl.ppenhancer", "PP Enhancer", "0.9.0")]
    public class Loader : BaseUnityPlugin
    {
        ConfigEntry<bool> hotReload;
        string dir, corePath;
        DateTime loaded;
        float next;
        MethodInfo stop;

        void Awake()
        {
            dir = Path.GetDirectoryName(Info.Location);
            corePath = Path.Combine(dir, "PPUpscaler.Core.dll");
            hotReload = Config.Bind("Debug", "HotReload", false, "Debug: load a new PPUpscaler.Core.dll as soon as it is replaced, without restarting the game.");
            Load();
        }

        void Update()
        {
            if (!hotReload.Value || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f;
            DateTime t;
            try { t = File.GetLastWriteTimeUtc(corePath); } catch { return; }
            // wait until the file has been left alone for a moment, so a copy in progress is not read half-written
            if (t == loaded || (DateTime.UtcNow - t).TotalSeconds < 1.0) return;
            Logger.LogInfo("PPUpscaler.Core.dll changed, reloading");
            if (stop != null) { try { stop.Invoke(null, null); } catch (Exception e) { Logger.LogError("core stop failed: " + e); } }
            stop = null;
            Load();
        }

        void Load()
        {
            try
            {
                loaded = File.GetLastWriteTimeUtc(corePath);
                // Mono hands back the already loaded assembly when the name matches, so each build gets a name of its own first
                // (as BepInEx's ScriptEngine does).
                Assembly asm;
                using (var src = new MemoryStream(File.ReadAllBytes(corePath)))
                using (var def = Mono.Cecil.AssemblyDefinition.ReadAssembly(src))
                using (var dst = new MemoryStream())
                {
                    def.Name.Name = def.Name.Name + "-" + DateTime.Now.Ticks;
                    def.Write(dst);
                    asm = Assembly.Load(dst.ToArray());
                }
                var main = asm.GetType("PPUpscaler.Main", true);
                main.GetMethod("Start").Invoke(null, new object[] { this, dir });
                stop = main.GetMethod("Stop");
            }
            catch (Exception e) { Logger.LogError("could not start PPUpscaler.Core.dll: " + e); }
        }
    }
}
