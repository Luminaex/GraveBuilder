using GraveyardOrbit;
using HarmonyLib;
using UnityEngine;

namespace GraveBuilder
{
    // CORRECTED after testing: ColourTokens.PinkColor is NOT the actual tile/market-price
    // rendering source — set it to an unmistakable bright green, read the field back
    // (confirmed the write genuinely stuck), and the in-game colour didn't change at all.
    // The real source is RefineryPalette.Of(ColourRules.Colour c) (RefineryPalette.cs:34) —
    // a switch EXPRESSION returning inline `new Color(...)` literals per arm, not a field.
    // There's nothing to reflect-write here; it needs an actual Harmony patch on the method.
    [HarmonyPatch(typeof(RefineryPalette), nameof(RefineryPalette.Of))]
    internal static class ColourRecolorPatch
    {
        private static void Postfix(ColourRules.Colour c, ref Color __result)
        {
            if (c == ColourRules.Colour.Pink)
            {
                // Deliberately an extreme, unmistakable test colour (bright green) rather
                // than a subtle pink adjustment, so there's no ambiguity about whether this
                // patch is the real hook point.
                __result = new Color(0.1f, 1f, 0.1f);
            }
        }
    }
}
