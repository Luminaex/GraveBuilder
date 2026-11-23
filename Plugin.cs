using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using GraveyardOrbit;
using HarmonyLib;
using UnityEngine;

namespace SandboxMapUnlock
{
    // Content plugin built on GraveyardOrbitModdingFramework: the framework owns mod
    // discovery, the generic content/<type>/ injectors, the load report and Ctrl+F6
    // hot-reload; this plugin keeps only its own bespoke patches (whose static Reload()
    // methods, if any, the framework's hot-reload still finds by reflection).
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(GraveyardOrbitModdingFramework.Plugin.PluginGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.luminaex.sandboxmapunlock";
        public const string PluginName = "GraveBuilder";
        public const string PluginVersion = "1.0.0";

        private const KeyCode MapBuilderKey = KeyCode.F7;

        internal static ManualLogSource Log;
        internal static string OverrideFilePath;

        private void Awake()
        {
            Log = Logger;

            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            OverrideFilePath = Path.Combine(pluginDir, "sandbox_map.json");

            Log.LogInfo($"Watching for sandbox map override at: {OverrideFilePath}");

            Harmony harmony = new Harmony(PluginGuid);
            harmony.PatchAll();

            Log.LogInfo("Harmony patches applied — SandboxMap.ActiveOverride/ActivePath unlocked.");
            Log.LogInfo($"Hold Ctrl and press {MapBuilderKey} to open/close the map builder.");
        }

        // Ctrl+F7 toggles the map builder (the framework owns Ctrl+F6 for hot-reload).
        private void Update()
        {
            if ((In.KeyHeld(KeyCode.LeftControl) || In.KeyHeld(KeyCode.RightControl)) && In.KeyDown(MapBuilderKey))
            {
                MapBuilderScreen.Toggle();
            }
        }
    }
}
