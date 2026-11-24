using System;
using System.IO;
using System.Reflection;
using GraveyardOrbit;
using HarmonyLib;
using UnityEngine.UIElements;

namespace GraveBuilder
{
    // SandboxMap.ActiveOverride is a hardcoded stub ("return false;") in the shipped build.
    // Re-point it at whether our override file actually exists.
    [HarmonyPatch(typeof(SandboxMap), "get_ActiveOverride")]
    internal static class ActiveOverridePatch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = File.Exists(Plugin.OverrideFilePath);
            return false; // skip original stub body
        }
    }

    // SandboxMap.ActivePath is a hardcoded stub ("return null;") in the shipped build.
    [HarmonyPatch(typeof(SandboxMap), "get_ActivePath")]
    internal static class ActivePathPatch
    {
        private static bool Prefix(ref string __result)
        {
            __result = File.Exists(Plugin.OverrideFilePath) ? Plugin.OverrideFilePath : null;
            return false;
        }
    }

    // Log the outcome every time the game checks for a sandbox override (sortie start),
    // so success/failure is visible in BepInEx/LogOutput.log without needing to reach
    // the in-game sortie screen.
    [HarmonyPatch(typeof(SandboxMap), "LoadActive")]
    internal static class LoadActivePatch
    {
        private static void Postfix(SandboxMap __result)
        {
            if (__result != null)
            {
                Plugin.Log.LogInfo($"LoadActive() -> override map loaded: {__result.cells.Count} cells, radius {__result.radius}.");
            }
            else
            {
                Plugin.Log.LogInfo("LoadActive() -> null (no override file present, game will use its normal structure).");
            }
        }
    }

    // Title-screen entry point: a fifth MAP BUILDER item just above QUIT. MainMenu.uxml has
    // only four button slots, so build our own Button after BuildUI has run and push it
    // through the controller's private MenuItem(Button, string) so it gets the same
    // title/subtitle labels, slab frame, hover-select and sfx as the stock items. The
    // UIDocument is the private field MainMenuController._doc (set at the top of BuildUI).
    [HarmonyPatch(typeof(MainMenuController), "BuildUI")]
    internal static class MainMenuBuilderButtonPatch
    {
        private const string ButtonName = "MapBuilderBtn";
        private static readonly FieldInfo DocField = AccessTools.Field(typeof(MainMenuController), "_doc");
        private static readonly MethodInfo MenuItemMethod = AccessTools.Method(typeof(MainMenuController), "MenuItem", new[] { typeof(Button), typeof(string) });
        // Guards devtest-boot firing more than once per process: BuildUI can run again later
        // (returning to the title screen from a run), and re-triggering every time would be
        // surprising rather than helpful.
        private static bool _devTestTriggered;

        private static void Postfix(MainMenuController __instance)
        {
            try
            {
                if (DocField == null || MenuItemMethod == null)
                {
                    Plugin.Log.LogError("MapBuilder: MainMenuController._doc or MenuItem(Button, string) not found; no title-screen button.");
                    return;
                }
                VisualElement root = (DocField.GetValue(__instance) as UIDocument)?.rootVisualElement;
                // BuildUI returns early (no _doc) if its panel or uxml is missing.
                Button quit = root?.Q<Button>("QuitBtn");
                if (quit?.parent == null)
                {
                    Plugin.Log.LogWarning("MapBuilder: main menu has no QuitBtn; no title-screen button.");
                    return;
                }
                if (root.Q<Button>(ButtonName) != null)
                {
                    return;
                }

                Button button = new Button { name = ButtonName, text = "MAP BUILDER" };
                // Same USS classes as the stock item (whatever the uxml gave QuitBtn), minus
                // the selected-state class; MenuItem adds menu-item / sfx-owns-hover itself.
                foreach (string cls in quit.GetClasses())
                {
                    if (cls != "menu-item--on")
                    {
                        button.AddToClassList(cls);
                    }
                }
                MenuItemMethod.Invoke(__instance, new object[] { button, "PAINT · SAVE · EXPORT" });
                // A button that opens a screen only ever opens it (the hotkey toggles).
                button.clicked += () => MapBuilderScreen.Open();
                quit.parent.Insert(quit.parent.IndexOf(quit), button);
                Plugin.Log.LogInfo("MapBuilder: added MAP BUILDER to the title screen.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"MapBuilder: adding the title-screen button failed: {e}");
            }

            // Dev-test auto-open, fired from here rather than a frame-counted guess in
            // Plugin.Update(): BuildUI finishing is the actual signal that the title screen
            // scene is done constructing. Triggering any earlier raced the scene's own load —
            // confirmed live (2026-11-24): a screen opened from Plugin.Update() at first-flag-
            // detection could get torn down moments later when the title screen scene settled,
            // landing back on a plain main menu with no error logged.
            if (!_devTestTriggered && File.Exists(Plugin.DevTestBootFilePath))
            {
                _devTestTriggered = true;
                try
                {
                    Plugin.Log.LogWarning($"MapBuilder: DEV TEST BOOT ({Plugin.DevTestBootFileName} found next to the DLL) — auto-opening the map builder now that the title screen has finished building.");
                    MapBuilderScreen.PrepareDevTestOpen();
                    Plugin.DevTestOpening = true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"MapBuilder: dev-test auto-open failed: {e}");
                }
            }
        }
    }
}
