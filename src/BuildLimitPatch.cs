using PavonisInteractive.TerraInvicta;
using System.Collections.Generic;
using HarmonyLib;

namespace BetterKinetics
{
    /// <summary>
    /// Postfix on TIHabState.IsModuleAllowedForHab.
    /// Enforces configurable per-body and per-faction-per-body build limits
    /// on modules that share an upgrade path with the target module.
    ///
    /// Limit semantics (tier cascade): a limit on tier T means "at most N modules
    /// in this family at EffectiveTier >= T on this body." So with T1/T2/T3 limits
    /// of 6/4/1, at most 6 refineries total, at most 4 at T2+, at most 1 at T3.
    ///
    /// New builds must satisfy every family tier's limit (the full cascade).
    /// Upgrades only check tiers at or above the target tier: upgrading an existing
    /// T1 to T2 does not increase the total family count, so the T1-level "total"
    /// cap is not re-checked. The module being upgraded is excluded from faction
    /// counts (but not body counts — body count reflects total module occupancy
    /// regardless of who owns the slot).
    ///
    /// Only narrows the base-game result (sets __result = false).
    /// Never permits a module the base game denied.
    /// </summary>
    [HarmonyPatch(typeof(TIHabState), nameof(TIHabState.IsModuleAllowedForHab))]
    public static class BuildLimitPatch
    {
        // Tiny fixed-size arrays are cheaper than lists for the tier cascade.
        // TI modules use tiers 1..3; index 0 unused. The +1 slot simplifies cumulative
        // sum bounds (factionAtOrAbove[maxTier+1] == 0 as a sentinel).
        private const int MaxTier = 3;

        static void Postfix(
            ref bool __result,
            TIFactionState faction,
            TIGameState location,
            TIHabModuleTemplate moduleTemplate,
            IEnumerable<TIHabModuleTemplate> existingModules,
            bool skipOnePerHabUpgradeCheckForDowngrade)            // Ignored: downgrades out of scope.
        {
            // Fast paths: mod disabled, or base game already denied.
            if (!Main.enabled || !__result)
                return;

            if (moduleTemplate == null)
                return;

            try
            {
                string dataName = moduleTemplate.dataName;

                // Look up target's config. Absent = not a family we track; bail cheap.
                if (!ConfigReader.habConfigs.TryGetValue(dataName, out var targetCfg))
                    return;

                bool targetHasBodyLimit    = targetCfg.BodyBuildLimit.HasValue;
                bool targetHasFactionLimit = targetCfg.FactionBuildLimit.HasValue;
                if (!targetHasBodyLimit && !targetHasFactionLimit)
                    return;

                // Limits apply per-body. Station/orbit hab locations resolve via
                // ref_naturalSpaceObject (planet/moon), which TI populates consistently.
                if (!(location is TISpaceGameState spaceLocation))
                    return;

                var body = spaceLocation.ref_naturalSpaceObject;
                if (body?.habs == null)
                    return;

                int targetTier = moduleTemplate.tier;
                if (targetTier < 1 || targetTier > MaxTier)
                    return;

                // Upgrade detection: if any template in the target hab's existing modules
                // shares this family, we're upgrading that module in place. The module
                // being replaced must not count against the faction total (otherwise a
                // 1-per-body faction cap would block all upgrades).
                //
                // Collect the family-member templates in the target hab. During the body
                // scan below, any module whose template is in this set is the upgrade
                // target — excluded from the faction cumulative count.
                HashSet<TIHabModuleTemplate> upgradeTargetTemplates = null;
                if (existingModules != null)
                {
                    foreach (var em in existingModules)
                    {
                        if (em == null) continue;
                        if (!em.SharesUpgradePath(moduleTemplate)) continue;
                        if (upgradeTargetTemplates == null)
                            upgradeTargetTemplates = new HashSet<TIHabModuleTemplate>();
                        upgradeTargetTemplates.Add(em);
                    }
                }
                bool isUpgrade = upgradeTargetTemplates != null;

                // Per-tier count buckets. bodyAtTier[t] = modules at exactly EffectiveTier t
                // regardless of ownership. factionAtTier[t] = same, restricted to this
                // faction, with upgrade-target modules excluded.
                //
                // We collect limits (body + faction) from every family template we
                // encounter plus the target template. Templates absent from the body
                // contribute no modules — their limits can still bind via cumulative
                // counts from lower-tier instances (e.g., 6×T1 hits T1 cap of 6 even
                // if no T2 exists yet). But if no instance at all exists at tier N or
                // above, the at-or-above count is 0 and the limit cannot fire. So we
                // only need limits for tiers we actually encounter (plus the target).
                var bodyAtTier    = new int[MaxTier + 2];
                var factionAtTier = new int[MaxTier + 2];

                // Per-tier limits. Null = no limit at that tier.
                var bodyLimitAtTier    = new int?[MaxTier + 2];
                var factionLimitAtTier = new int?[MaxTier + 2];

                // Target template's own limits always apply (target tier is always
                // encountered conceptually, even before the new module exists).
                if (targetHasBodyLimit)    bodyLimitAtTier[targetTier]    = targetCfg.BodyBuildLimit.Value;
                if (targetHasFactionLimit) factionLimitAtTier[targetTier] = targetCfg.FactionBuildLimit.Value;

                foreach (var hab in body.habs)
                {
                    if (hab == null || hab.IsAlien())
                        continue;

                    bool habBelongsToFaction = hab.faction == faction;

                    foreach (var mod in hab.OkayModules())
                    {
                        if (mod?.moduleTemplate == null) continue;
                        if (!mod.moduleTemplate.SharesUpgradePath(moduleTemplate)) continue;

                        int eff = EffectiveTier(mod);
                        if (eff < 1 || eff > MaxTier) continue;   // Defensive: out-of-range tier.

                        // Body count: every family module counts regardless of ownership
                        // or upgrade state.
                        bodyAtTier[eff]++;

                        // Faction count: faction-owned only, exclude the module being
                        // upgraded in place (its template is in upgradeTargetTemplates).
                        bool excludeFromFaction = isUpgrade
                            && upgradeTargetTemplates.Contains(mod.moduleTemplate);
                        if (habBelongsToFaction && !excludeFromFaction)
                            factionAtTier[eff]++;

                        // Pick up this encountered template's limits (first occurrence wins;
                        // all instances of a given template share the same config entry).
                        if (ConfigReader.habConfigs.TryGetValue(
                                mod.moduleTemplate.dataName, out var encounteredCfg))
                        {
                            int encTier = mod.moduleTemplate.tier;
                            if (encTier >= 1 && encTier <= MaxTier)
                            {
                                if (encounteredCfg.BodyBuildLimit.HasValue
                                    && !bodyLimitAtTier[encTier].HasValue)
                                    bodyLimitAtTier[encTier] = encounteredCfg.BodyBuildLimit.Value;
                                if (encounteredCfg.FactionBuildLimit.HasValue
                                    && !factionLimitAtTier[encTier].HasValue)
                                    factionLimitAtTier[encTier] = encounteredCfg.FactionBuildLimit.Value;
                            }
                        }
                    }
                }

                // Cumulative "at or above" counts: atOrAbove[t] = atTier[t] + atTier[t+1] + ...
                // Compute descending so each entry sums itself + the higher ones.
                var bodyAtOrAbove    = new int[MaxTier + 2];
                var factionAtOrAbove = new int[MaxTier + 2];
                for (int t = MaxTier; t >= 1; t--)
                {
                    bodyAtOrAbove[t]    = bodyAtOrAbove[t + 1]    + bodyAtTier[t];
                    factionAtOrAbove[t] = factionAtOrAbove[t + 1] + factionAtTier[t];
                }

                // Determine which tiers to check.
                //   New build: every tier with a defined limit (full cascade).
                //   Upgrade:   only tiers >= targetTier (the lower-tier "total" cap is not
                //              re-checked because the upgrade doesn't increase total count).
                int lowestTierToCheck = isUpgrade ? targetTier : 1;

                for (int t = lowestTierToCheck; t <= MaxTier; t++)
                {
                    if (bodyLimitAtTier[t].HasValue
                        && bodyAtOrAbove[t] >= bodyLimitAtTier[t].Value)
                    {
                        __result = false;
                        return;
                    }
                    if (factionLimitAtTier[t].HasValue
                        && factionAtOrAbove[t] >= factionLimitAtTier[t].Value)
                    {
                        __result = false;
                        return;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Main.Error($"BuildLimitPatch failed for '{moduleTemplate?.dataName ?? "null"}'", ex);
            }
        }

        /// <summary>
        /// Effective tier of an existing module for limit counting.
        /// Returns max(current tier, prior tier) so that in-progress transitions
        /// continue to count at the higher tier until construction completes.
        /// </summary>
        private static int EffectiveTier(TIHabModuleState mod)
        {
            int tier = mod.moduleTemplate.tier;
            var prior = mod.priorModuleTemplate;
            if (prior != null && prior.tier > tier)
                tier = prior.tier;
            return tier;
        }
    }
}
