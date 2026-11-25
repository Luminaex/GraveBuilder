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
    // discovery, the generic content/<type>/ injectors, the load report and the RELOAD MODS
    // button; this plugin holds only the map builder's own patches (the builder UI, the
    // sandbox-override unlock and the Contracts list's CUSTOM MAPS section).
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(GraveFramework.Plugin.PluginGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.luminaex.gravebuilder";
        public const string PluginName = "GraveBuilder";
        public const string PluginVersion = PluginVersionInfo.Version; // set in mods\Versions.props

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
            Log.LogInfo($"Hold Ctrl and press {ShortcutBindings.Get(ShortcutBindings.ToggleBuilder)} to open/close the map builder.");
        }

        private void Update()
        {
            // Ctrl + the toggle key (F7 by default, rebindable in Settings > Controls, see
            // ShortcutBindings; KeyCode.None = unbound) toggles the map builder (the framework's
            // hot-reload is now a RELOAD MODS button on the main menu's Mods page, not a hotkey).
            KeyCode toggleKey = ShortcutBindings.Get(ShortcutBindings.ToggleBuilder);
            if (toggleKey != KeyCode.None && (In.KeyHeld(KeyCode.LeftControl) || In.KeyHeld(KeyCode.RightControl)) && In.KeyDown(toggleKey))
            {
                MapBuilderScreen.Toggle();
            }
        }
    }
}
