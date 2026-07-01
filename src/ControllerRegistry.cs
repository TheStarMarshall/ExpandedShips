using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;
using UnityModManagerNet;

namespace BetterKinetics
{
    // Per-controller-type registry for weapon slot remapping. Scans every sibling
    // mod folder in Mods/Enabled for ControllerDefinitions.cfg. Each entry remaps
    // the slot→mountIndex resolution for one ShipModelController subclass.
    //
    // Entries are independent of dataNames: they apply to every ship instance
    // that loads a prefab carrying this controller, regardless of which dataName
    // the ship was created from. By design — mount transforms are physical on
    // the prefab, so the mapping naturally belongs to the controller.
    //
    // Multiple dataNames sharing one controller (e.g. Dreadnought ↔ HeavyCruiser
    // both using DreadnoughtController) all inherit this controller's slot map.
    // The slot map is a superset; per-dataName JSON shipModuleSlots picks the
    // subset of slots actually present.
    //
    // First-wins on cross-mod controllerClass collision: only one slot map per
    // controller type is supported, and the first registration wins.
    public static class ControllerRegistry
    {
        const string ConfigFileName = "ControllerDefinitions.cfg";

        public class MountOverride
        {
            public Mount[] mounts;
            public int index;
        }

        public class SlotMapping
        {
            public int slot;
            public int index;
            public MountOverride[] overrides;
        }

        public class ControllerEntry
        {
            public string controllerClassName;
            public Type controllerType;
            public SlotMapping[] weaponSlotMap;
            public string sourceMod;
        }

        static readonly Dictionary<Type, ControllerEntry> _byControllerType =
            new Dictionary<Type, ControllerEntry>();

        public static ControllerEntry GetByControllerType(Type type)
        {
            if (type == null) return null;
            _byControllerType.TryGetValue(type, out var entry);
            return entry;
        }

        public static IEnumerable<ControllerEntry> AllEntries => _byControllerType.Values;

        public static void Init(UnityModManager.ModEntry modEntry)
        {
            _byControllerType.Clear();

            string enabledDir = Path.GetDirectoryName(
                modEntry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (enabledDir == null)
            {
                Main.Error("ControllerRegistry: could not resolve Mods/Enabled directory from mod path.");
                return;
            }

            var gameAsm = typeof(ShipModelController).Assembly;
            int filesScanned = 0;

            foreach (string modDir in Directory.GetDirectories(enabledDir))
            {
                string cfgPath = Path.Combine(modDir, ConfigFileName);
                if (!File.Exists(cfgPath))
                    continue;

                string sourceMod = Path.GetFileName(modDir);

                JArray entries;
                try { entries = JArray.Parse(File.ReadAllText(cfgPath)); }
                catch (Exception ex)
                {
                    Main.Error($"ControllerRegistry: failed to parse {cfgPath}", ex);
                    continue;
                }

                ParseEntries(entries, gameAsm, sourceMod);
                filesScanned++;
            }

            Main.Log($"ControllerRegistry: scanned {filesScanned} file(s), " +
                     $"{_byControllerType.Count} controller(s) registered.");
        }

        static void ParseEntries(JArray entries, System.Reflection.Assembly gameAsm, string sourceMod)
        {
            foreach (JToken item in entries)
            {
                if (!(item is JObject entry))
                    continue;

                ControllerEntry ctrlEntry = null;
                try
                {
                    ctrlEntry = ParseEntry(entry, gameAsm, sourceMod);
                }
                catch (Exception ex)
                {
                    Main.Error($"ControllerRegistry: unexpected error parsing entry from '{sourceMod}'", ex);
                }

                if (ctrlEntry == null) continue;

                // First-wins on duplicate controllerType across mods.
                if (_byControllerType.TryGetValue(ctrlEntry.controllerType, out var existing) &&
                    existing.sourceMod != ctrlEntry.sourceMod)
                {
                    Main.Warning($"ControllerRegistry: weaponSlotMap for " +
                                 $"'{ctrlEntry.controllerClassName}' from '{ctrlEntry.sourceMod}' " +
                                 $"ignored — already registered by '{existing.sourceMod}' (first-wins).");
                    continue;
                }

                _byControllerType[ctrlEntry.controllerType] = ctrlEntry;

                Main.Log($"ControllerRegistry: parsed {ctrlEntry.controllerClassName} from " +
                         $"{sourceMod} ({ctrlEntry.weaponSlotMap.Length} mappings).");
            }
        }

        static ControllerEntry ParseEntry(JObject entry, System.Reflection.Assembly gameAsm, string sourceMod)
        {
            string ctrlName = entry["controllerClass"]?.ToString();
            JArray mapArr   = entry["weaponSlotMap"] as JArray;

            if (string.IsNullOrEmpty(ctrlName))
            {
                Main.Error($"ControllerRegistry: entry from '{sourceMod}' missing 'controllerClass' — skipped.");
                return null;
            }

            // Migration warning: hull-axis fields belong in HullDefinitions.cfg now.
            if (entry["dataName"] != null || entry["bundleName"] != null ||
                entry["drivePrefab"] != null || entry["vanillaDriveDataName"] != null)
            {
                Main.Warning($"ControllerRegistry: {ctrlName} from '{sourceMod}' contains " +
                             "hull-axis fields (dataName/bundleName/drivePrefab/vanillaDriveDataName) — " +
                             "those belong in HullDefinitions.cfg now. Ignored here.");
            }

            Type ctrlType = gameAsm.GetType("PavonisInteractive.TerraInvicta." + ctrlName);
            if (ctrlType == null)
            {
                Main.Error($"ControllerRegistry: {ctrlName} from '{sourceMod}' " +
                           "not found in Assembly-CSharp — skipped.");
                return null;
            }

            // Defensive: confirm the resolved type actually inherits ShipModelController.
            // Catches typos that resolve to unrelated classes with similar names.
            if (!typeof(ShipModelController).IsAssignableFrom(ctrlType))
            {
                Main.Error($"ControllerRegistry: {ctrlName} from '{sourceMod}' " +
                           "is not a subclass of ShipModelController — skipped.");
                return null;
            }

            SlotMapping[] slotMap = ParseSlotMap(ctrlName, sourceMod, mapArr);
            if (slotMap == null || slotMap.Length == 0)
            {
                Main.Warning($"ControllerRegistry: {ctrlName} from '{sourceMod}' has empty " +
                             "weaponSlotMap — entry adds nothing. Skipped.");
                return null;
            }

            return new ControllerEntry
            {
                controllerClassName = ctrlName,
                controllerType      = ctrlType,
                weaponSlotMap       = slotMap,
                sourceMod           = sourceMod
            };
        }

        static SlotMapping[] ParseSlotMap(string ctrlName, string sourceMod, JArray mapArr)
        {
            if (mapArr == null || mapArr.Count == 0)
                return null;

            var list = new List<SlotMapping>();
            var seenSlots = new HashSet<int>();
            string ctx = $"{ctrlName} from '{sourceMod}'";

            foreach (JToken item in mapArr)
            {
                if (!(item is JObject slotEntry))
                    continue;

                if (!TryReadInt(slotEntry, "slot",  ctx, out int slot))  continue;
                if (!TryReadInt(slotEntry, "index", ctx, out int index)) continue;

                if (!seenSlots.Add(slot))
                {
                    Main.Warning($"ControllerRegistry: {ctx} has duplicate weaponSlotMap entry for slot {slot} — last wins.");
                    list.RemoveAll(m => m.slot == slot);
                }

                MountOverride[] overrides = ParseOverrides(ctrlName, sourceMod, slot,
                    slotEntry["overrides"] as JArray);

                list.Add(new SlotMapping
                {
                    slot      = slot,
                    index     = index,
                    overrides = overrides
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        static MountOverride[] ParseOverrides(string ctrlName, string sourceMod, int slot, JArray arr)
        {
            if (arr == null || arr.Count == 0) return null;

            var result = new List<MountOverride>();
            string ctx = $"{ctrlName} from '{sourceMod}' slot {slot} override";

            foreach (JToken item in arr)
            {
                if (!(item is JObject ov))
                    continue;

                if (!TryReadInt(ov, "index", ctx, out int ovIdx))
                    continue;

                JArray mountsArr = ov["mounts"] as JArray;
                if (mountsArr == null || mountsArr.Count == 0)
                {
                    Main.Error($"ControllerRegistry: {ctx} missing 'mounts' — override skipped.");
                    continue;
                }

                var mounts = new List<Mount>();
                bool valid = true;
                foreach (JToken mTok in mountsArr)
                {
                    string mStr = (mTok?.Type == JTokenType.String) ? mTok.Value<string>() : null;
                    if (mStr == null)
                    {
                        Main.Error($"ControllerRegistry: {ctx} mount value " +
                                   $"is {mTok?.Type.ToString() ?? "null"}, expected String — override skipped.");
                        valid = false;
                        break;
                    }
                    if (!Enum.TryParse<Mount>(mStr, out Mount m))
                    {
                        Main.Error($"ControllerRegistry: {ctx} invalid mount '{mStr}' — override skipped.");
                        valid = false;
                        break;
                    }
                    mounts.Add(m);
                }
                if (valid)
                    result.Add(new MountOverride { mounts = mounts.ToArray(), index = ovIdx });
            }
            return result.Count > 0 ? result.ToArray() : null;
        }

        /// <summary>
        /// Reads an integer field with explicit type check. Errors on missing or wrong type.
        /// </summary>
        static bool TryReadInt(JObject obj, string field, string context, out int value)
        {
            value = 0;
            JToken token = obj[field];
            if (token == null)
            {
                Main.Error($"ControllerRegistry: {context} missing '{field}' — skipped.");
                return false;
            }
            if (token.Type != JTokenType.Integer)
            {
                Main.Warning($"ControllerRegistry: {context} '{field}' is {token.Type}, expected Integer — skipped.");
                return false;
            }
            value = token.Value<int>();
            return true;
        }

        public static void PatchWeaponMounts(Harmony harmony)
        {
            // Partial-patch policy: one failure logs and continues. A broken entry
            // should not prevent other controllers from working.
            int patched = 0;
            foreach (var entry in _byControllerType.Values)
            {
                try
                {
                    var method = AccessTools.Method(entry.controllerType, "SlotToWeaponMountIndex");
                    if (method == null)
                    {
                        Main.Error($"ControllerRegistry: SlotToWeaponMountIndex not found " +
                                   $"on {entry.controllerClassName} (from '{entry.sourceMod}').");
                        continue;
                    }

                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(WeaponMountPatch),
                                                  nameof(WeaponMountPatch.DynamicPrefix)));
                    patched++;
                }
                catch (Exception ex)
                {
                    Main.Error($"ControllerRegistry: failed to patch {entry.controllerClassName} " +
                               $"(from '{entry.sourceMod}')", ex);
                }
            }
            Main.Log($"ControllerRegistry: patched {patched} controller(s) for weapon slot remapping.");
        }
    }
}
