using System;
using System.Collections.Generic;
using HarmonyLib;
using PavonisInteractive.TerraInvicta;

namespace BetterKinetics
{
    // Postfix on TIDriveTemplate.modelResource(hull, appearanceIndex) — replaces the
    // vanilla-computed drive prefab path when needed.
    //
    // Vanilla path computation (TIDriveTemplate.cs:88-115):
    //   non-alien idx 0:  "ships/Earth_<dataName>_<nozzleStr>x<thrusters>"
    //   non-alien idx 1:  "ships/Earth_<path1[1] transformed>_<nozzleStr>x<thrusters>"
    //   non-alien idx 2+: "ships_prm/Earth_<path1[idx] transformed>_<nozzleStr>x<thrusters>"
    //   alien:            "ships/Thruster_<dataName>x<thrusters>"
    //
    // dataName is embedded in idx 0 paths and alien paths. idx 1+ paths use path1
    // and don't contain dataName, so substitution naturally no-ops for those.
    //
    // Resolution rules — driven by the hull's HullEntry (or its absence):
    //
    //   No HullEntry registered:
    //     Vanilla path used as-is. Custom dataNames without a HullEntry are
    //     unsupported — modders must declare aliases explicitly via
    //     vanillaDriveDataName in HullDefinitions.cfg.
    //
    //   FullCustom (bundleName + drivePrefab):
    //     Replace __result entirely with "<bundleName>/<drivePrefab>". Drive
    //     prefab lives in the mod's bundle.
    //
    //   Hybrid / PatchOnly with vanillaDriveDataName:
    //     Substitute hull.dataName → vanillaDriveDataName inside __result, scoped
    //     to the boundary patterns vanilla TI produces. Both vanilla path formats
    //     (chem and alien) are covered.
    //
    //   Hybrid / PatchOnly without vanillaDriveDataName:
    //     dataName matches a vanilla hull whose drive assets exist directly. No
    //     substitution needed; vanilla path is correct as-is.
    [HarmonyPatch(typeof(TIDriveTemplate), "modelResource",
        new[] { typeof(TIShipHullTemplate), typeof(int) })]
    public static class DriveVisualPatch
    {
        // One-time log gate: confirms remapping fires without spamming on repeat queries.
        static readonly HashSet<string> _loggedRemaps = new HashSet<string>(StringComparer.Ordinal);

        static void Postfix(ref string __result, TIShipHullTemplate hull)
        {
            if (!Main.enabled || hull == null || string.IsNullOrEmpty(__result))
                return;

            try
            {
                var entry = HullRegistry.GetByDataName(hull.dataName);
                if (entry == null)
                    return;   // unregistered → vanilla path used as-is

                // FullCustom: replace path entirely with the mod's bundle drive prefab.
                if (entry.Mode == HullRegistry.HullMode.FullCustom)
                {
                    string overridden = entry.bundleName + "/" + entry.drivePrefab;
                    if (overridden != __result)
                    {
                        string logKey = __result + "|" + overridden;
                        if (_loggedRemaps.Add(logKey))
                            Main.Log($"DriveVisualPatch (full-custom): {__result} -> {overridden}");
                        __result = overridden;
                    }
                    return;
                }

                // Hybrid / PatchOnly: substitute via vanillaDriveDataName when set.
                if (string.IsNullOrEmpty(entry.vanillaDriveDataName))
                    return;
                if (entry.vanillaDriveDataName == hull.dataName)
                    return;   // self-alias would be a no-op anyway

                string substituted = SubstituteDataNameInPath(__result,
                    hull.dataName, entry.vanillaDriveDataName);
                if (substituted == null)
                    return;   // dataName not present (e.g. idx ≥1 paths use path1, not dataName)

                string remapKey = __result + "|" + substituted;
                if (_loggedRemaps.Add(remapKey))
                {
                    string tag = entry.Mode.ToString().ToLowerInvariant();
                    Main.Log($"DriveVisualPatch ({tag}): {__result} -> {substituted}");
                }

                __result = substituted;
            }
            catch (Exception ex)
            {
                Main.Error("DriveVisualPatch failed", ex);
            }
        }

        /// <summary>
        /// Substitutes hull.dataName with the alias inside the two vanilla TI drive path
        /// formats. Boundary characters (_ on both sides for chem, _ and x for alien)
        /// prevent substring collisions — e.g. oldName "Cruiser" cannot match inside
        /// "_HeavyCruiser_" because the leading "_" requires "_Cruiser_" exactly.
        /// Returns null if no substitution occurred.
        /// </summary>
        static string SubstituteDataNameInPath(string path, string oldName, string newName)
        {
            // Pattern 1: "_<oldName>_" → "_<newName>_"  (chem path: ships/Earth_<dataName>_<nozzle>x<thrusters>)
            // Pattern 2: "_<oldName>x" → "_<newName>x"  (alien path: ships/Thruster_<dataName>x<thrusters>)
            // For any one path only one pattern can match (chem and alien paths are
            // mutually exclusive), so the two Replace calls are non-interfering.
            string result = path
                .Replace("_" + oldName + "_", "_" + newName + "_")
                .Replace("_" + oldName + "x", "_" + newName + "x");

            return result == path ? null : result;
        }
    }
}
