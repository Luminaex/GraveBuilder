using System;
using System.Reflection;
using GraveyardOrbit;
using GraveyardOrbit.UI;
using HarmonyLib;

namespace SandboxMapUnlock
{
    // The real gate on the Expand button turned out to be RefineryScreen.State(CardKind)'s
    // own independently-hardcoded "line.Tier >= 5" (RefineryScreen.cs:791) — not
    // RefineryLine.CanExpand() (which we patched first and which IS correct, but isn't what
    // RefineryScreen.Buy() actually checks before deciding whether to call Expand() at all).
    // Three separate hardcoded "5"s exist in RefineryScreen.cs for this one card
    // (State()'s .maxed, State()'s "TIER {tier} / {max}" progress text, and PaintExpand()'s
    // own "int num = 5" for the pip count/label) — this patch fixes the one that actually
    // blocks the purchase. Always compares against RefineryLine.Tiers.Length, never a fixed
    // number, so it keeps working even if the devs add an official 6th tier later.
    [HarmonyPatch(typeof(RefineryScreen), nameof(RefineryScreen.State))]
    internal static class RefineryStatePatch
    {
        private static void Postfix(RefineryScreen.CardKind kind, ref RefineryScreen.CardState __result)
        {
            if (kind != RefineryScreen.CardKind.Expand)
            {
                return;
            }

            RefineryLine line = RefineryLine.Board;
            if (line == null)
            {
                return;
            }

            int maxTier = RefineryLine.Tiers.Length;
            __result.maxed = line.Tier >= maxTier;
            __result.progress = $"TIER {line.Tier} / {maxTier}";
        }
    }

    // The third hardcoded "5" — PaintExpand()'s own "int num = 5" used for the visible
    // "TIER {tier} / {max}" label and the pip-dot count, independent of State() entirely.
    // Purely cosmetic (doesn't block the purchase, RefineryStatePatch above handles that),
    // but shows the wrong max once the cap is lifted (e.g. "6 / 5").
    [HarmonyPatch(typeof(RefineryScreen), "PaintExpand")]
    internal static class PaintExpandPatch
    {
        private static readonly FieldInfo XCardField = AccessTools.Field(typeof(RefineryScreen), "_xCard");

        private static void Postfix(RefineryScreen __instance)
        {
            try
            {
                RefineryLine line = RefineryLine.Board;
                if (line == null)
                {
                    return;
                }

                var xCard = (TipCardKit.Card)XCardField.GetValue(__instance);
                if (xCard == null)
                {
                    return;
                }

                int maxTier = RefineryLine.Tiers.Length;
                xCard.lv.text = $"TIER {line.Tier} / {maxTier}";
                if (xCard.pips != null)
                {
                    xCard.pips.Count = maxTier;
                    xCard.pips.Filled = line.Tier;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"PaintExpand fixup failed: {e}");
            }
        }
    }
}
