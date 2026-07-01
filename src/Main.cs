using System;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;

namespace BetterKinetics
{
    public static class Main
    {
        public static bool enabled { get; private set; }
        public static UnityModManager.ModEntry mod { get; private set; }

        static bool Load(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            modEntry.OnToggle = OnToggle;
            enabled = modEntry.Active;   // Respect persisted enabled state across game restarts.

            Harmony harmony = null;
            try
            {
                ConfigReader.Init(modEntry);
                HullRegistry.Init(modEntry);
                ControllerRegistry.Init(modEntry);

                harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                ControllerRegistry.PatchWeaponMounts(harmony);

                return true;
            }
            catch (Exception ex)
            {
                // Fail cleanly: log full context, undo any patches already applied, tell UMM we failed.
                modEntry.Logger.Error($"Load failed: {ex}");
                try { harmony?.UnpatchAll(modEntry.Info.Id); } catch { /* best-effort cleanup */ }
                return false;
            }
        }

        static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            enabled = value;
            return true;
        }

        public static void Log(string msg)     => mod.Logger.Log(msg);
        public static void Warning(string msg) => mod.Logger.Warning(msg);
        public static void Error(string msg)   => mod.Logger.Error(msg);

        // Pairs a context message with the full exception (message + stack).
        public static void Error(string context, Exception ex) => mod.Logger.Error($"{context}: {ex}");
    }
}
