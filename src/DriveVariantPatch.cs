using System;
using HarmonyLib;
using PavonisInteractive.TerraInvicta;
using UnityEngine;

namespace BetterKinetics
{
    // Postfix on ShipModelController.BuildDrives — selects which baked drive variant
    // child to make visible, based on the drive equipped on the ship.
    //
    // BK hull prefabs (post-bake) have variant child GameObjects under "Drive", each
    // with a specific variant's mesh and faction-baked material. Parent Drive's
    // MeshRenderer is disabled. We toggle SetActive on the matching variant child.
    //
    // Why BuildDrives and not SetDrive? AssetCacheManager has a static initializer
    // that fails at mod load time (game state not ready), and SetDrive references
    // AssetCacheManager.thrusterFXPrefabs. Harmony's JIT trips on that → crash.
    // BuildDrives is the caller, doesn't directly reference AssetCacheManager, and
    // gives us the same access (the ship's drive template) plus a clean __instance.
    //
    // Variant name format: "<nozzleStr>x<thrusters>" (e.g. "DeLavalx5", "Magneticx3"),
    // with "_ALT1" suffix when hullAppearanceIndex == 1.
    [HarmonyPatch(typeof(ShipModelController), "BuildDrives")]
    public static class DriveVariantPatch
    {
        static void Postfix(ShipModelController __instance, TISpaceShipTemplate ship)
        {
            if (!Main.enabled) return;
            if (ship?.driveTemplate == null) return;
            if (__instance?.thrusterModel == null) return;

            try
            {
                Transform driveT = __instance.thrusterModel.transform;
                string variantName = $"{ship.driveTemplate.nozzleStr}x{ship.driveTemplate.thrusters}" +
                                     (ship.hullAppearanceIndex == 1 ? "_ALT1" : "");

                Transform match = null;
                int siblingsExamined = 0;
                for (int i = 0; i < driveT.childCount; i++)
                {
                    Transform c = driveT.GetChild(i);
                    if (c == null) continue;
                    if (!IsVariantName(c.gameObject.name)) continue;

                    siblingsExamined++;
                    if (c.gameObject.name == variantName) match = c;
                    else c.gameObject.SetActive(false);
                }

                if (siblingsExamined == 0) return;   // not a BK hull; skip silently

                if (match != null)
                {
                    match.gameObject.SetActive(true);
                    Main.Log($"DriveVariantPatch: activated '{variantName}' on " +
                             $"{driveT.parent?.name ?? "?"}");
                }
                else
                {
                    Main.Warning($"DriveVariantPatch: no baked variant '{variantName}' " +
                                 $"on {driveT.parent?.name ?? "?"} (drive will render white)");
                }
            }
            catch (Exception ex) { Main.Error("DriveVariantPatch failed", ex); }
        }

        // True if `name` matches our baked-variant naming convention
        // (DeLavalx<n>, Magneticx<n>, Pulsex<n>, with optional _ALT1).
        static bool IsVariantName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("DeLavalx", StringComparison.Ordinal)
                || name.StartsWith("Magneticx", StringComparison.Ordinal)
                || name.StartsWith("Pulsex", StringComparison.Ordinal);
        }
    }
}
