using System;
using System.Collections.Generic;
using System.Reflection;
using GraveyardOrbit;
using GraveyardOrbit.UI;
using HarmonyLib;
using UnityEngine.UIElements;
using StructuresInjectPatch = GraveFramework.StructuresInjectPatch;

namespace GraveBuilder
{
    // Contracts screen: a "CUSTOM MAPS" section after "ALL CONTRACTS", listing every structure
    // the framework injected from content/structures/. Purely additive: the stock method runs
    // untouched, then this appends its own header and rows through the controller's private
    // ListHeader(string) and AddRow(VisualElement, StructureDef, bool), so the rows get the
    // same look, selection, pad-nav and bookkeeping (_defs/_rowCurrent/_rowPaint/_rowEls) as
    // the stock ones. Custom maps also still appear in the stock sections; that duplication
    // is deliberate (deduplicating would mean editing UI the stock method already built).
    [HarmonyPatch(typeof(ModeSelectController), "FillOvertimeList")]
    internal static class ContractsCustomMapsPatch
    {
        private static readonly FieldInfo ListField = AccessTools.Field(typeof(ModeSelectController), "_list");
        private static readonly MethodInfo ListHeaderMethod = AccessTools.Method(typeof(ModeSelectController), "ListHeader", new[] { typeof(string) });
        private static readonly MethodInfo AddRowMethod = AccessTools.Method(typeof(ModeSelectController), "AddRow", new[] { typeof(VisualElement), typeof(StructureDef), typeof(bool) });
        private static readonly MethodInfo PaintRowsMethod = AccessTools.Method(typeof(ModeSelectController), "PaintRows");

        private static void Postfix(ModeSelectController __instance)
        {
            try
            {
                if (ListField == null || ListHeaderMethod == null || AddRowMethod == null)
                {
                    Plugin.Log.LogError("CustomMaps: ModeSelectController._list, ListHeader(string) or AddRow(VisualElement, StructureDef, bool) not found; no CUSTOM MAPS section.");
                    return;
                }
                // The stock method returns early (list not built yet, or no catalogue).
                VisualElement list = ListField.GetValue(__instance) as VisualElement;
                StructureDef[] contracts = StructureCatalog.I?.Contracts;
                if (list == null || contracts == null)
                {
                    return;
                }

                List<StructureDef> custom = new List<StructureDef>();
                foreach (StructureDef def in contracts)
                {
                    if (def != null && StructuresInjectPatch.IsInjected(def.id))
                    {
                        custom.Add(def);
                    }
                }
                if (custom.Count == 0)
                {
                    return; // never show an empty header
                }

                list.Add((VisualElement)ListHeaderMethod.Invoke(null, new object[] { "CUSTOM MAPS" }));
                foreach (StructureDef def in custom)
                {
                    AddRowMethod.Invoke(__instance, new object[] { list, def, false });
                }
                // The stock method's closing Select(0) painted the rows that existed then;
                // repaint so ours get the same unselected styling straight away.
                PaintRowsMethod?.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"CustomMaps: adding the CUSTOM MAPS section failed: {e}");
            }
        }
    }
}
