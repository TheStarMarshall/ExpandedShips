using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;
using UnityModManagerNet;

namespace BetterKinetics
{
    // Per-dataName registry for ship hulls. Scans every sibling mod folder in
    // Mods/Enabled for HullDefinitions.cfg. Each entry expresses how a particular
    // hull dataName deviates from vanilla TI's defaults — bundle override, drive
    // prefab override, or drive-asset alias to another hull's drive assets.
    //
    // Hull entries are independent of controller-type slot remapping; that lives
    // in ControllerRegistry, keyed off controller class. The two registries are
    // decoupled by design: a hull entry never names a controller, a controller
    // entry never names a dataName, and the prefab→controller binding at runtime
    // is whatever vanilla TI loads via modelResource[0].
    //
    // Three modes — derived from cfg fields:
    //
    //   PatchOnly  — neither bundleName nor drivePrefab. Vanilla hull mesh +
    //                vanilla drive. Used for net-new dataNames that alias another
    //                hull's drive assets via vanillaDriveDataName.
    //
    //   Hybrid     — bundleName set, drivePrefab absent. BK hull mesh + vanilla
    //                drive. dataName must match a vanilla hull (so vanilla drive
    //                paths resolve), unless vanillaDriveDataName is also set.
    //
    //   FullCustom — bundleName + drivePrefab. BK hull mesh + BK drive prefab.
    //                Drive material rendering relies on DriveMaterialShaderRebindPatch.
    //
    // First-wins on cross-mod dataName collision.
    public static class HullRegistry
    {
        const string ConfigFileName = "HullDefinitions.cfg";

        public enum HullMode
        {
            PatchOnly,   // no bundleName, no drivePrefab
            Hybrid,      // bundleName set, drivePrefab absent
            FullCustom   // bundleName + drivePrefab
        }

        public class HullEntry
        {
            public string dataName;
            public string bundleName;
            public string drivePrefab;
            public string vanillaDriveDataName;
            public HullMode Mode;
            public string sourceMod;
        }

        static readonly Dictionary<string, HullEntry> _byDataName =
            new Dictionary<string, HullEntry>(StringComparer.Ordinal);

        public static bool IsRegistered(string dataName) =>
            !string.IsNullOrEmpty(dataName) && _byDataName.ContainsKey(dataName);

        public static HullEntry GetByDataName(string dataName)
        {
            if (string.IsNullOrEmpty(dataName))
                return null;
            _byDataName.TryGetValue(dataName, out var entry);
            return entry;
        }

        public static IEnumerable<HullEntry> AllEntries => _byDataName.Values;

        public static void Init(UnityModManager.ModEntry modEntry)
        {
            _byDataName.Clear();

            // Resolve Mods/Enabled by walking up one level from our own mod folder.
            string enabledDir = Path.GetDirectoryName(
                modEntry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (enabledDir == null)
            {
                Main.Error("HullRegistry: could not resolve Mods/Enabled directory from mod path.");
                return;
            }

            int filesScanned = 0;

            // Scan every sibling mod folder for HullDefinitions.cfg contributions.
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
                    Main.Error($"HullRegistry: failed to parse {cfgPath}", ex);
                    continue;
                }

                ParseEntries(entries, sourceMod);
                filesScanned++;
            }

            Main.Log($"HullRegistry: scanned {filesScanned} file(s), {_byDataName.Count} hull(s) registered.");
        }

        static void ParseEntries(JArray entries, string sourceMod)
        {
            foreach (JToken item in entries)
            {
                // Guard against non-object entries (e.g., accidental bare strings).
                if (!(item is JObject entry))
                    continue;

                HullEntry hullEntry = null;
                try
                {
                    hullEntry = ParseEntry(entry, sourceMod);
                }
                catch (Exception ex)
                {
                    Main.Error($"HullRegistry: unexpected error parsing entry from '{sourceMod}'", ex);
                }

                if (hullEntry == null) continue;

                // First-wins on cross-mod collision. Predictable: the first mod
                // to register a dataName wins; subsequent mods are ignored with
                // a clear log line so the conflict surfaces.
                if (_byDataName.TryGetValue(hullEntry.dataName, out var existing) &&
                    existing.sourceMod != hullEntry.sourceMod)
                {
                    Main.Warning($"HullRegistry: dataName '{hullEntry.dataName}' from " +
                                 $"'{hullEntry.sourceMod}' ignored — already registered by " +
                                 $"'{existing.sourceMod}' (first-wins).");
                    continue;
                }

                _byDataName[hullEntry.dataName] = hullEntry;
                LogParsedEntry(hullEntry);
            }
        }

        static void LogParsedEntry(HullEntry e)
        {
            string driveAlias = string.IsNullOrEmpty(e.vanillaDriveDataName)
                ? ""
                : $", drive alias '{e.vanillaDriveDataName}'";

            switch (e.Mode)
            {
                case HullMode.PatchOnly:
                    Main.Log($"HullRegistry: parsed {e.dataName} from {e.sourceMod}" +
                             $" (patch-only{driveAlias}).");
                    break;
                case HullMode.Hybrid:
                    Main.Log($"HullRegistry: parsed {e.dataName} from {e.sourceMod}, " +
                             $"hybrid bundle '{e.bundleName}'{driveAlias}.");
                    break;
                case HullMode.FullCustom:
                    Main.Log($"HullRegistry: parsed {e.dataName} from {e.sourceMod}, " +
                             $"full-custom bundle '{e.bundleName}', drive '{e.drivePrefab}'.");
                    break;
            }
        }

        static HullEntry ParseEntry(JObject entry, string sourceMod)
        {
            string dataName             = entry["dataName"]?.ToString();
            string bundleName           = entry["bundleName"]?.ToString();
            string drivePfb             = entry["drivePrefab"]?.ToString();
            string vanillaDriveDataName = entry["vanillaDriveDataName"]?.ToString();

            if (string.IsNullOrEmpty(dataName))
            {
                Main.Error($"HullRegistry: entry from '{sourceMod}' missing 'dataName' — skipped.");
                return null;
            }

            // Migration warning: weaponSlotMap belongs in ControllerDefinitions.cfg now.
            if (entry["weaponSlotMap"] != null || entry["controllerClass"] != null)
            {
                Main.Warning($"HullRegistry: {dataName} from '{sourceMod}' contains " +
                             "'weaponSlotMap' or 'controllerClass' — those fields belong in " +
                             "ControllerDefinitions.cfg now. Ignored here.");
            }

            bool hasBundle = !string.IsNullOrEmpty(bundleName);
            bool hasDrive  = !string.IsNullOrEmpty(drivePfb);
            bool hasAlias  = !string.IsNullOrEmpty(vanillaDriveDataName);

            // drivePrefab without bundleName is invalid: drive prefabs must live in a bundle.
            if (!hasBundle && hasDrive)
            {
                Main.Error($"HullRegistry: {dataName} from '{sourceMod}' has drivePrefab " +
                           "but missing bundleName — drive prefabs must live in a bundle. Skipped.");
                return null;
            }

            // Useless entry: nothing to declare beyond the dataName itself.
            if (!hasBundle && !hasDrive && !hasAlias)
            {
                Main.Error($"HullRegistry: {dataName} from '{sourceMod}' has no bundleName, " +
                           "drivePrefab, or vanillaDriveDataName — entry would do nothing. Skipped.");
                return null;
            }

            HullMode mode;
            if (hasBundle && hasDrive)       mode = HullMode.FullCustom;
            else if (hasBundle && !hasDrive) mode = HullMode.Hybrid;
            else                             mode = HullMode.PatchOnly;

            // FullCustom drive material rendering historically required
            // DriveMaterialShaderRebindPatch to clone+rebind shaders across bundles.
            // Verify drives render correctly before relying on this mode.
            if (mode == HullMode.FullCustom)
            {
                Main.Warning($"HullRegistry: {dataName} from '{sourceMod}' is FullCustom mode " +
                             "(bundleName + drivePrefab). Drive rendering relies on " +
                             "DriveMaterialShaderRebindPatch — verify drives render correctly " +
                             "in-game before assuming this mode is fully supported.");
            }

            // vanillaDriveDataName has no effect in FullCustom mode (drivePrefab takes precedence).
            if (mode == HullMode.FullCustom && hasAlias)
            {
                Main.Warning($"HullRegistry: {dataName} from '{sourceMod}' is full-custom — " +
                             $"vanillaDriveDataName '{vanillaDriveDataName}' ignored (drivePrefab takes precedence).");
                vanillaDriveDataName = null;
            }

            return new HullEntry
            {
                dataName             = dataName,
                bundleName           = bundleName,
                drivePrefab          = drivePfb,
                vanillaDriveDataName = vanillaDriveDataName,
                Mode                 = mode,
                sourceMod            = sourceMod
            };
        }
    }
}
