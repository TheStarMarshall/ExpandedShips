using System;
using PavonisInteractive.TerraInvicta;

namespace BetterKinetics
{
    // Dynamic Prefix applied per-controller-type by ControllerRegistry.PatchWeaponMounts.
    // Returns delta-only mappings from weaponSlotMap; unmapped slots fall through
    // to the vanilla controller's SlotToWeaponMountIndex.
    public static class WeaponMountPatch
    {
        public static bool DynamicPrefix(
            ref int __result, int slot, Mount mount, ShipModelController __instance)
        {
            if (!Main.enabled)
                return true;

            try
            {
                var entry = ControllerRegistry.GetByControllerType(__instance.GetType());
                if (entry == null)
                    return true;

                foreach (var mapping in entry.weaponSlotMap)
                {
                    if (mapping.slot != slot)
                        continue;

                    if (mapping.overrides != null)
                    {
                        foreach (var ov in mapping.overrides)
                        {
                            foreach (var m in ov.mounts)
                            {
                                if (m == mount)
                                {
                                    __result = ov.index;
                                    return false;
                                }
                            }
                        }
                    }

                    __result = mapping.index;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"WeaponMountPatch: slot={slot} mount={mount}", ex);
            }

            return true;
        }
    }
}
