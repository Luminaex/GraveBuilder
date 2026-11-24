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

        private const KeyCode MapBuilderKey = KeyCode.F7;
        // Dev-only auto-open: drop this file next to the DLL and the next launch opens straight
        // into the map builder (Advanced mode, library open on LOAD STRUCTURE/EXPORT AS
        // STRUCTURE, every stage's ADVANCED OPTIONS expanded) with no clicks needed — for
        // eyeballing a UI change, by a human or by Claude's computer-use, right after a
        // build-deploy-relaunch cycle. Never created automatically; delete it to go back to a
        // normal boot. See MapBuilderScreen.PrepareDevTestOpen/FinishDevTestOpen, and
        // Patches.MainMenuBuilderButtonPatch (actually triggers it — see that file for why).
        internal const string DevTestBootFileName = "devtest_boot.flag";

        internal static ManualLogSource Log;
        internal static string OverrideFilePath;
        internal static string DevTestBootFilePath;
        // Set by MainMenuBuilderButtonPatch once it has called PrepareDevTestOpen(); this
        // Update() loop just waits for Open()'s wipe animation to land, then finishes the job.
        internal static bool DevTestOpening;

        private void Awake()
        {
            Log = Logger;

            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            OverrideFilePath = Path.Combine(pluginDir, "sandbox_map.json");
            DevTestBootFilePath = Path.Combine(pluginDir, DevTestBootFileName);

            Log.LogInfo($"Watching for sandbox map override at: {OverrideFilePath}");

            Harmony harmony = new Harmony(PluginGuid);
            harmony.PatchAll();

            Log.LogInfo("Harmony patches applied — SandboxMap.ActiveOverride/ActivePath unlocked.");
            Log.LogInfo($"Hold Ctrl and press {MapBuilderKey} to open/close the map builder.");
        }

        private void Update()
        {
            // Ctrl+F7 toggles the map builder (the framework's hot-reload is now a RELOAD MODS
            // button on the Contracts page, not a hotkey).
            if ((In.KeyHeld(KeyCode.LeftControl) || In.KeyHeld(KeyCode.RightControl)) && In.KeyDown(MapBuilderKey))
            {
                MapBuilderScreen.Toggle();
            }

            if (DevTestOpening && MapBuilderScreen.IsOpen)
            {
                DevTestOpening = false;
                MapBuilderScreen.FinishDevTestOpen();
            }
        }
    }
}
