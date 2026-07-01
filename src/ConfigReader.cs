using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityModManagerNet;

namespace BetterKinetics
{
    /// <summary>
    /// Per-module custom config. Null fields = not configured by any mod.
    /// Extend by adding fields here + a line in ParseFile — no consumer changes required.
    /// </summary>
    public class HabModuleConfig
    {
        public int? BodyBuildLimit;
        public int? FactionBuildLimit;
        public int? MiningBonus;
        public int? MiningCapBonus;
    }

    public static class ConfigReader
    {
        // dataName -> config. Last mod wins on collision (standard load-order behavior).
        // Ordinal comparer: dataName values are ASCII identifiers; locale-independent and faster.
        public static readonly Dictionary<string, HabModuleConfig> habConfigs =
            new Dictionary<string, HabModuleConfig>(StringComparer.Ordinal);

        public static void Init(UnityModManager.ModEntry modEntry)
        {
            habConfigs.Clear();

            // Resolve Mods/Enabled by walking up one level from our own mod folder.
            string enabledDir = Path.GetDirectoryName(
                modEntry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (enabledDir == null)
            {
                Main.Error("ConfigReader: could not resolve Mods/Enabled directory from mod path.");
                return;
            }

            int fileCount = 0;

            // Scan every sibling mod folder for TIHabModuleTemplate.json contributions.
            foreach (string modDir in Directory.GetDirectories(enabledDir))
            {
                string jsonPath = Path.Combine(modDir, "TIHabModuleTemplate.json");
                if (!File.Exists(jsonPath))
                    continue;

                try
                {
                    ParseFile(jsonPath);
                    fileCount++;
                }
                catch (Exception ex)
                {
                    Main.Error($"ConfigReader: failed to parse {jsonPath}", ex);
                }
            }

            Main.Log($"ConfigReader: scanned {fileCount} file(s), loaded {habConfigs.Count} module config(s).");

            if (habConfigs.Count == 0)
                Main.Warning("ConfigReader: no custom hab fields found. Patches will have no effect.");
        }

        private static void ParseFile(string path)
        {
            string json = File.ReadAllText(path);
            JArray entries = JArray.Parse(json);

            foreach (JToken item in entries)
            {
                if (!(item is JObject entry))
                    continue;

                string dataName = entry["dataName"]?.ToString();
                if (string.IsNullOrEmpty(dataName))
                    continue;

                // Fetch-or-create: later mods override individual fields without nuking prior fields.
                if (!habConfigs.TryGetValue(dataName, out HabModuleConfig cfg))
                {
                    cfg = new HabModuleConfig();
                    habConfigs[dataName] = cfg;
                }

                ApplyInt(entry, "bodyBuildLimit",    dataName, v => cfg.BodyBuildLimit    = v);
                ApplyInt(entry, "factionBuildLimit", dataName, v => cfg.FactionBuildLimit = v);
                ApplyInt(entry, "miningBonus",       dataName, v => cfg.MiningBonus       = v);
                ApplyInt(entry, "miningCapBonus",    dataName, v => cfg.MiningCapBonus    = v);
            }
        }

        /// <summary>
        /// Reads an integer field into a config setter. Warns on wrong type or negative value.
        /// Absent fields are silent (expected: most mods won't set most fields).
        /// </summary>
        private static void ApplyInt(JObject entry, string field, string dataName, Action<int> setter)
        {
            JToken token = entry[field];
            if (token == null)
                return;

            if (token.Type != JTokenType.Integer)
            {
                Main.Warning($"ConfigReader: '{field}' on '{dataName}' is {token.Type}, expected Integer — ignored.");
                return;
            }

            int value = token.Value<int>();
            if (value < 0)
            {
                Main.Warning($"ConfigReader: '{field}' on '{dataName}' is negative ({value}) — ignored.");
                return;
            }

            setter(value);
        }
    }
}
