using PavonisInteractive.TerraInvicta;
using System;
using HarmonyLib;

namespace BetterKinetics
{
    public static class MiningPatchHelper
    {
        /// <summary>
        /// Sums a configured per-module integer across every active module in every
        /// hab owned by the faction. Cumulative by design: more modules = more bonus.
        /// Iterates sectors/modules directly to avoid the per-call List allocation
        /// that hab.ActiveModules() performs.
        /// </summary>
        public static int SumActiveModuleBonus(TIFactionState faction, Func<HabModuleConfig, int?> selector)
        {
            int sum = 0;
            var habs = faction?.habs;
            if (habs == null)
                return sum;

            foreach (var hab in habs)
            {
                if (hab?.sectors == null)
                    continue;

                foreach (var sector in hab.sectors)
                {
                    if (sector?.habModules == null)
                        continue;

                    foreach (var mod in sector.habModules)
                    {
                        if (mod == null || !mod.active)
                            continue;

                        string dataName = mod.moduleTemplate?.dataName;
                        if (dataName == null)
                            continue;

                        if (ConfigReader.habConfigs.TryGetValue(dataName, out var cfg))
                        {
                            int? val = selector(cfg);
                            if (val.HasValue)
                                sum += val.Value;
                        }
                    }
                }
            }

            return sum;
        }
    }

    [HarmonyPatch(typeof(TIFactionState), nameof(TIFactionState.GetCurrentMiningMultiplierFromOrgsAndEffects))]
    public static class MiningBonusPatch
    {
        static void Postfix(ref float __result, TIFactionState __instance, FactionResource resource)
        {
            if (!Main.enabled)
                return;

            try
            {
                // Config values are integer percentage points; divide to add to the multiplier.
                int raw = MiningPatchHelper.SumActiveModuleBonus(__instance, c => c.MiningBonus);
                if (raw != 0)
                    __result += raw / 100f;
            }
            catch (Exception ex)
            {
                Main.Error("MiningBonusPatch failed", ex);
            }
        }
    }

    // Target property's name ("Nextwork") is spelled this way in the base game.
    // If TI ever corrects the typo, nameof() below will fail to compile — intentional early warning.
    [HarmonyPatch(typeof(TIFactionState), nameof(TIFactionState.SafeMineNextworkSize), MethodType.Getter)]
    public static class MiningCapBonusPatch
    {
        static void Postfix(ref int __result, TIFactionState __instance)
        {
            if (!Main.enabled)
                return;

            try
            {
                int raw = MiningPatchHelper.SumActiveModuleBonus(__instance, c => c.MiningCapBonus);
                if (raw != 0)
                    __result += raw;
            }
            catch (Exception ex)
            {
                Main.Error("MiningCapBonusPatch failed", ex);
            }
        }
    }
}
