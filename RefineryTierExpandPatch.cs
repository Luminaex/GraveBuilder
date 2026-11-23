using System;
using System.Reflection;
using GraveyardOrbit;
using HarmonyLib;

namespace SandboxMapUnlock
{
    // Proves out the Refinery/Extractor tier cap can be lifted. RefineryLine.Tiers is a
    // public static readonly TierDef[] (5 entries) — readonly only blocks reassignment from
    // normal code, reflection can still replace it. The actual gate, RefineryLine.CanExpand(),
    // is one small clean method (unlike the scattered const-based caps elsewhere), so we
    // replace its logic via prefix to check against the new Tiers.Length instead of the
    // hardcoded 5. NOT yet handling the two literal Clamp(tier, 1, 5) call sites in
    // DevSetTier/Load() — those only matter across a save/reload, this proves the live,
    // in-session case first.
    [HarmonyPatch(typeof(GameConfig), "Load")]
    internal static class RefineryTierExpandPatch
    {
        private static bool _done;

        private static void Postfix()
        {
            if (_done)
            {
                return;
            }
            _done = true;

            try
            {
                FieldInfo tiersField = AccessTools.Field(typeof(RefineryLine), "Tiers");
                var oldTiers = (RefineryLine.TierDef[])tiersField.GetValue(null);
                var newTiers = new RefineryLine.TierDef[oldTiers.Length + 1];
                Array.Copy(oldTiers, newTiers, oldTiers.Length);
                newTiers[oldTiers.Length] = new RefineryLine.TierDef
                {
                    lanes = 4,
                    sockets = 10,
                    scale = 0.4f,
                    price = 1800,
                    name = "A FOURTH LANE (MODDED)"
                };
                tiersField.SetValue(null, newTiers);

                Plugin.Log.LogInfo($"RefineryTierExpand: Tiers extended to {newTiers.Length} entries (was {oldTiers.Length}).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"RefineryTierExpand setup failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(RefineryLine), nameof(RefineryLine.CanExpand))]
    internal static class CanExpandPatch
    {
        private static bool Prefix(RefineryLine __instance, ref string why, ref bool __result)
        {
            why = null;
            if (__instance.Tier >= RefineryLine.Tiers.Length)
            {
                why = "The line is as big as it gets (modded cap)";
                __result = false;
                return false;
            }
            if (__instance.FreeSockets() > 0)
            {
                why = "Fill the sockets first";
                __result = false;
                return false;
            }
            __result = true;
            return false;
        }
    }
}
