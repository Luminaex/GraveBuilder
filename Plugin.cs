using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using GraveyardOrbit;
using HarmonyLib;
using UnityEngine;

namespace GraveBuilder
{
    // Content plugin built on GraveFramework: the framework owns mod
    // discovery, the generic content/<type>/ injectors, the load report and Ctrl+F6
    // hot-reload; this plugin holds only the map builder's own patches (the builder UI, the
    // sandbox-override unlock and the Contracts list's CUSTOM MAPS section).
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(GraveFramework.Plugin.PluginGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.luminaex.gravebuilder";
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
