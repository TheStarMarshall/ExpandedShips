using PavonisInteractive.TerraInvicta;
using System;
using System.Text;
using HarmonyLib;

namespace BetterKinetics
{
    /// <summary>
    /// Postfix on the shared benefits/costs text builder used by the hab-screen
    /// tooltips (build + installed lists) and the research-screen module preview.
    ///
    /// 1) Station income repair: with no hab context (research screen), vanilla
    ///    returns 0 for Water/Volatiles/Metals/NobleMetals/Fissiles income,
    ///    hiding flat station incomes. Splices those flat values into the native
    ///    "Incomes and Bonuses" line. Display-only.
    /// 2) Custom field block: BodyBuildLimit / FactionBuildLimit / MiningBonus /
    ///    MiningCapBonus exist only in ConfigReader.habConfigs, never on the
    ///    template, so no vanilla path can display them. Appended here.
    /// </summary>
    [HarmonyPatch(typeof(TIHabModuleTemplate), nameof(TIHabModuleTemplate.benefitsAndCostsDescription))]
    public static class HabDescriptionPatch
    {
        public static void Postfix(TIHabModuleTemplate __instance, TIHabState hab, ref string __result)
        {
            if (!Main.enabled || __result == null)
                return;

            try
            {
                if (hab == null && __instance.habType == HabType.Station)
                    __result = SpliceStationIncome(__instance, __result);

                if (__instance.dataName != null
                    && ConfigReader.habConfigs.TryGetValue(__instance.dataName, out HabModuleConfig cfg))
                    __result = AppendCustomBlock(cfg, __result);
            }
            catch (Exception ex)
            {
                // Never let a display patch break tooltip rendering.
                Main.Error($"HabDescriptionPatch failed for '{__instance?.dataName ?? "null"}'", ex);
            }
        }

        /// <summary>
        /// Flat material incomes, formatted like vanilla's income entries.
        /// Antimatter/Exotics excluded: vanilla shows them even with hab == null.
        /// </summary>
        private static string SpliceStationIncome(TIHabModuleTemplate t, string result)
        {
            StringBuilder entries = new StringBuilder();
            AppendIncomeEntry(entries, FactionResource.Water,       t.incomeWater_month);
            AppendIncomeEntry(entries, FactionResource.Volatiles,   t.incomeVolatiles_month);
            AppendIncomeEntry(entries, FactionResource.Metals,      t.incomeMetals_month);
            AppendIncomeEntry(entries, FactionResource.NobleMetals, t.incomeNobles_month);
            AppendIncomeEntry(entries, FactionResource.Fissiles,    t.incomeFissiles_month);

            if (entries.Length == 0)
                return result;

            // Anchor on the localized header vanilla writes when it has income entries.
            // Vanilla's entry line ends with a trailing space, so no separator is needed.
            string header = Loc.T("UI.Habs.IncomeAndBonuses");
            int headerAt = string.IsNullOrEmpty(header) ? -1 : result.IndexOf(header, StringComparison.Ordinal);
            if (headerAt >= 0)
            {
                int headerLineEnd = result.IndexOf('\n', headerAt);
                if (headerLineEnd >= 0)
                {
                    // Insert at the end of the entry line that follows the header.
                    int entryLineEnd = result.IndexOf('\n', headerLineEnd + 1);
                    int insertAt = (entryLineEnd >= 0) ? entryLineEnd : result.Length;
                    if (insertAt > 0 && result[insertAt - 1] == '\r')
                        insertAt--;
                    return result.Insert(insertAt, entries.ToString());
                }
            }

            // Module has no vanilla income section: create one at the end.
            return EnsureTrailingNewline(new StringBuilder(result))
                .AppendLine(header)
                .AppendLine(entries.ToString())
                .ToString();
        }

        private static void AppendIncomeEntry(StringBuilder sb, FactionResource resource, float value)
        {
            if (value == 0f)
                return;
            sb.Append(TIUtilities.InlineResourceStr(resource))
              .Append(TIUtilities.FormatBigOrSmallNumber(value, 1, 3, 0, true, false))
              .Append(' ');
        }

        /// <summary>
        /// Limits are per-body (BuildLimitPatch scans one body's habs):
        /// BodyBuildLimit counts all factions, FactionBuildLimit one faction.
        /// Zero limits are meaningful (unbuildable) and display; zero bonuses do not.
        /// </summary>
        private static string AppendCustomBlock(HabModuleConfig cfg, string result)
        {
            StringBuilder sb = new StringBuilder();
            if (cfg.MiningBonus.HasValue && cfg.MiningBonus.Value != 0)
                sb.AppendLine($"Mining Output: +{cfg.MiningBonus.Value}%");
            if (cfg.MiningCapBonus.HasValue && cfg.MiningCapBonus.Value != 0)
                sb.AppendLine($"Mining Cap: +{cfg.MiningCapBonus.Value}");
            if (cfg.BodyBuildLimit.HasValue)
                sb.AppendLine($"Limit: {cfg.BodyBuildLimit.Value} per body (all factions)");
            if (cfg.FactionBuildLimit.HasValue)
                sb.AppendLine($"Limit: {cfg.FactionBuildLimit.Value} per faction per body");

            if (sb.Length == 0)
                return result;

            return EnsureTrailingNewline(new StringBuilder(result))
                .AppendLine("Module Rules")
                .Append(sb)
                .ToString();
        }

        /// <summary>Guards against gluing onto a final line if a future game
        /// patch stops terminating the builder output with a newline.</summary>
        private static StringBuilder EnsureTrailingNewline(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
                sb.AppendLine();
            return sb;
        }
    }
}
