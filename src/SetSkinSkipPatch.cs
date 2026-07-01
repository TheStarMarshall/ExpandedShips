using System;
using System.Collections.Generic;
using HarmonyLib;
using PavonisInteractive.TerraInvicta;
using UnityEngine;

namespace BetterKinetics
{
    // Prefix on HumanShipController.SetSkin — skips vanilla skinning for hulls whose
    // mesh comes from a BK asset bundle (Hybrid or FullCustom mode).
    //
    // Vanilla SetSkin assigns sharedMaterial = LoadAsset<Material>(factionPath +
    // "/MAT_<child>" + factionSuffix) on every MeshRenderer under hullModel. For BK-
    // bundle hulls those materials either don't exist OR fail to bind to a mod-bundle
    // mesh, producing white ships. The edit-time bake wrote correct bundle-internal
    // materials onto each MeshRenderer; preserving them by skipping vanilla SetSkin
    // is what makes the hull render correctly.
    //
    // Triggers on Hybrid AND FullCustom; PatchOnly hulls fall through to vanilla.
    [HarmonyPatch(typeof(HumanShipController), "SetSkin")]
    public static class SetSkinSkipPatch
    {
        // One-time log gate per hull dataName.
        static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        static bool Prefix(HumanShipController __instance, TISpaceShipTemplate ship)
        {
            if (!Main.enabled) return true;

            try
            {
                string dataName = ship?.hullTemplate?.dataName;
                if (string.IsNullOrEmpty(dataName)) return true;

                var entry = HullRegistry.GetByDataName(dataName);
                if (entry == null) return true;

                if (entry.Mode != HullRegistry.HullMode.Hybrid &&
                    entry.Mode != HullRegistry.HullMode.FullCustom)
                    return true;

                if (__instance.hullModel == null) return true;

                if (_logged.Add(dataName))
                    Main.Log($"SetSkinSkipPatch: {dataName} ({entry.Mode}) — skipping vanilla SetSkin.");

                return false;   // skip vanilla SetSkin
            }
            catch (Exception ex)
            {
                Main.Error("SetSkinSkipPatch failed", ex);
                return true;
            }
        }
    }
}
