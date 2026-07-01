# BetterKinetics — DLL Modding Guide

**Game:** Terra Invicta · **Harmony:** 2.3.6.0 (HarmonyX/MonoMod)

How Harmony DLL modding works in Terra Invicta and what BetterKinetics's DLL does.

---

## 1 — How it works

### 1.1 — The problem

Terra Invicta's game code is compiled C# in `Assembly-CSharp.dll`. Mods can override JSON data templates, but adding new behaviors (build limits, mining bonuses, custom hull rendering, weapon-slot remapping) requires modifying code at runtime.

### 1.2 — The solution: Harmony

Harmony patches compiled .NET methods at runtime by inserting code before or after the original.

| Type | When | Use |
|---|---|---|
| Prefix | Before the original method | Replace behavior — return `false` to skip the original |
| Postfix | After the original method | Augment behavior — modify `__result` via `ref` |

### 1.3 — Why custom JSON fields need DLL

Vanilla `TIHabModuleTemplate` has no `[JsonExtensionData]` attribute — custom fields added to JSON are silently dropped during deserialization. The game never sees them. BK's DLL reads raw JSON independently at startup via `ConfigReader`, building its own lookup dictionaries keyed by `dataName`.

### 1.4 — Load chain

```
Game starts
  → Proton loads winhttp.dll (DoorstopProxy)
  → DoorstopProxy loads UnityModManager
  → UMM reads Mods/Enabled/*/ModInfo.json
  → UMM resolves load order
  → UMM calls each mod's EntryMethod
  → BetterKinetics.Main.Load() runs:
    1. ConfigReader.Init()              — parse hab module custom fields
    2. HullRegistry.Init()              — parse HullDefinitions.cfg per-dataName
    3. ControllerRegistry.Init()        — parse ControllerDefinitions.cfg per-controller
    4. harmony.PatchAll()               — applies all [HarmonyPatch] classes:
                                          BuildLimitPatch, MiningPatch (×2),
                                          DriveVisualPatch, DriveVariantPatch,
                                          SetSkinSkipPatch
    5. ControllerRegistry.PatchWeaponMounts() — dynamic per-controller
                                                SlotToWeaponMountIndex prefixes
```

---

## 2 — Source files

Ten files compile into `BetterKinetics.dll`. Each has a single responsibility; the DLL is intentionally small and orthogonal.

| File | Lines | Role |
|---|---|---|
| `Main.cs` | 55 | UMM entry point, load chain, logging |
| `ConfigReader.cs` | 122 | Hab module config parser (cross-mod) |
| `HullRegistry.cs` | 248 | Hull-axis registry (per dataName) |
| `ControllerRegistry.cs` | 330 | Controller-axis registry (per ShipModelController subclass) |
| `WeaponMountPatch.cs` | 55 | Dynamic prefix used by ControllerRegistry |
| `BuildLimitPatch.cs` | 217 | Hab module construction limits |
| `MiningPatch.cs` | 99 | Hab module mining bonuses |
| `DriveVisualPatch.cs` | 117 | Drive prefab path resolution |
| `DriveVariantPatch.cs` | 78 | Per-spawn drive variant selector |
| `SetSkinSkipPatch.cs` | 56 | Vanilla skinning bypass for BK-bundle hulls |

### 2.1 — `Main.cs`

UMM entry point.

- **Does:** Receives the `UnityModManager.ModEntry` callback. Initializes the three registries in order, creates the Harmony instance, applies all `[HarmonyPatch]` classes via `PatchAll`, then calls `ControllerRegistry.PatchWeaponMounts` for dynamic per-controller patches. Catches any load exception, unpatches everything, and tells UMM the load failed cleanly.
- **Touches:** `UnityModManager.ModEntry`, `HarmonyLib.Harmony`. Calls every other BK file's static initializer.
- **Provides:** `Main.enabled` (UMM toggle state), `Main.Log`, `Main.Warning`, `Main.Error` for the rest of the DLL.

### 2.2 — `ConfigReader.cs`

Reads custom hab-module fields out of `TIHabModuleTemplate.json`.

- **Does:** Walks every sibling mod folder under `Mods/Enabled/`. For each `TIHabModuleTemplate.json` found, parses entries with Newtonsoft.Json and populates a `Dictionary<string, HabModuleConfig>` keyed by `dataName`. Reads four optional integer fields: `bodyBuildLimit`, `factionBuildLimit`, `miningBonus`, `miningCapBonus`. Negative values and wrong-type values are warned and ignored.
- **Touches:** Newtonsoft.Json. Filesystem (read-only).
- **Output:** `ConfigReader.habConfigs` — consumed by `BuildLimitPatch` and `MiningPatch`.
- **Cross-mod:** Per-field merge. Later mods can extend prior mods' configs without overwriting unrelated fields (a mod that sets only `miningBonus` for `MyRefinery` won't clear another mod's `bodyBuildLimit` on the same dataName).

### 2.3 — `HullRegistry.cs`

Per-`dataName` registry for ship hulls.

- **Does:** Walks every sibling mod folder for `HullDefinitions.cfg`. Each entry resolves to one of three modes by field combination: **PatchOnly** (only `vanillaDriveDataName` set — alias another vanilla hull's drive assets), **Hybrid** (`bundleName` set, no `drivePrefab` — BK hull mesh + vanilla drive variants), or **FullCustom** (both `bundleName` and `drivePrefab` — BK hull mesh + BK drive prefab). Rejects entries with neither a bundle, drive prefab, nor alias. Rejects `drivePrefab` without `bundleName`. Warns on FullCustom (cross-bundle shader rendering is fragile).
- **Touches:** Newtonsoft.Json. Filesystem (read-only).
- **Output:** `HullRegistry.GetByDataName(string)` — consumed by `DriveVisualPatch`, `DriveVariantPatch` (indirectly via runtime queries), and `SetSkinSkipPatch`.
- **Cross-mod:** First-wins on `dataName` collision.

### 2.4 — `ControllerRegistry.cs`

Per-controller-class registry for weapon-slot remapping.

- **Does:** Walks every sibling mod folder for `ControllerDefinitions.cfg`. Each entry names one `ShipModelController` subclass (resolved via reflection against `Assembly-CSharp.dll`) and a list of slot mappings: `slot → index`, with optional per-`Mount` overrides. Validates the resolved type actually inherits `ShipModelController`. After Harmony's `PatchAll`, calls `harmony.Patch` directly on each controller's `SlotToWeaponMountIndex` method, attaching `WeaponMountPatch.DynamicPrefix` as the prefix.
- **Touches:** Newtonsoft.Json. Reflection on `Assembly-CSharp.dll`. Direct Harmony patch invocation.
- **Output:** `ControllerRegistry.GetByControllerType(Type)` — consumed by `WeaponMountPatch`.
- **Cross-mod:** First-wins on `controllerClass` collision.

### 2.5 — `WeaponMountPatch.cs`

Dynamic prefix attached by `ControllerRegistry` to every registered controller's `SlotToWeaponMountIndex`.

- **Does:** Looks up the controller's slot map. Walks per-mount overrides first; on a `Mount` match returns the override's `index` and skips vanilla. Otherwise returns the slot's default `index`. No mapping match → falls through to vanilla `SlotToWeaponMountIndex`.
- **Touches:** `ControllerRegistry.GetByControllerType`. Patches game's `SlotToWeaponMountIndex` (one per registered controller).

### 2.6 — `BuildLimitPatch.cs`

Hab-module construction enforcement.

- **Does:** Postfix on `TIHabState.IsModuleAllowedForHab`. When a module has a configured `bodyBuildLimit` or `factionBuildLimit`, walks every hab on the target body, counts existing modules in the same upgrade family at or above the target tier, and sets `__result = false` if the cascade limit is reached. Tier cascade semantics: a limit on tier T means "at most N modules at `EffectiveTier ≥ T`."
- **Touches:** Patches `TIHabState.IsModuleAllowedForHab`. Reads `ConfigReader.habConfigs`. Iterates `body.habs`, `hab.sectors`, `sector.habModules` to count.
- **Safety:** Only narrows the base game's verdict (sets `__result = false`). Never permits a module the base game denied. Disabled mod = no effect.

### 2.7 — `MiningPatch.cs`

Hab-module mining-bonus aggregation. Two patches in one file.

- **`MiningBonusPatch`** — Postfix on `TIFactionState.GetCurrentMiningMultiplierFromOrgsAndEffects(FactionResource)`. Sums every active hab module's `miningBonus`, divides by 100, adds to `__result`. The bonus is uniform across resources — the `resource` parameter is required for Harmony's name-based binding but isn't used to filter.
- **`MiningCapBonusPatch`** — Postfix on `TIFactionState.SafeMineNextworkSize` getter (game preserves the typo "Nextwork"). Sums every active hab module's `miningCapBonus`, adds to `__result`.
- **Touches:** Patches two `TIFactionState` methods. Reads `ConfigReader.habConfigs`. Iterates `faction.habs`, `hab.sectors`, `sector.habModules`. Helper `MiningPatchHelper.SumActiveModuleBonus` is shared by both.

### 2.8 — `DriveVisualPatch.cs`

Drive-prefab path resolution.

- **Does:** Postfix on `TIDriveTemplate.modelResource(TIShipHullTemplate, int)`. For hulls registered in `HullRegistry`, rewrites the returned path according to mode:
  - `FullCustom` → replaces the entire path with `<bundleName>/<drivePrefab>`.
  - `Hybrid` / `PatchOnly` with `vanillaDriveDataName` set → substitutes `hull.dataName` with the alias inside vanilla's two path patterns: chem (`_<dataName>_`) and alien (`_<dataName>x`). Boundary characters prevent substring collisions.
  - Everything else → unchanged.
- **Touches:** Patches `TIDriveTemplate.modelResource`. Reads `HullRegistry.GetByDataName`.
- **One-time log gate:** caches `(originalPath, remappedPath)` pairs and logs each remap exactly once to keep the log readable.

### 2.9 — `DriveVariantPatch.cs`

Per-spawn drive variant selector for BK hulls.

- **Does:** Postfix on `ShipModelController.BuildDrives`. Iterates child GameObjects of the ship's `thrusterModel` (the `Drive` container). Looks for a child name matching the equipped drive's variant — `<nozzleStr>x<thrusters>`, with `_ALT1` suffix if `hullAppearanceIndex == 1`. Activates the matching child via `SetActive(true)` and deactivates other variant children. If no children match the variant naming convention (DeLavalx<n> / Magneticx<n> / Pulsex<n>), bails silently — vanilla hulls are unaffected.
- **Touches:** Patches `ShipModelController.BuildDrives`. Reads `ship.driveTemplate`. Operates on baked variant children placed inside BK hull prefabs at edit time by `ShipTools.BakeDriveVariants`.

### 2.10 — `SetSkinSkipPatch.cs`

Vanilla-skinning bypass for BK-bundle hulls.

- **Does:** Prefix on `HumanShipController.SetSkin`. For Hybrid or FullCustom hulls (per `HullRegistry`), returns `false` to skip vanilla `SetSkin` entirely. PatchOnly and unregistered hulls fall through to vanilla.
- **Why:** Vanilla `SetSkin` overwrites every `MeshRenderer.sharedMaterial` under `hullModel` with paths from a vanilla faction bundle. Those paths don't resolve for BK-bundle meshes — result is white ships. The edit-time bake already wrote correct bundle-internal materials onto each renderer; skipping vanilla `SetSkin` preserves them.
- **Touches:** Patches `HumanShipController.SetSkin`. Reads `HullRegistry.GetByDataName`.

---

## 3 — Patch design pattern

Every patch in BK follows the same defensive template:

```csharp
1. if (!Main.enabled) return;            // UMM toggle
2. try {
3.   if (no relevant data) return;        // fast exit
4.   [patch logic]
5. } catch (Exception ex) {
6.   Main.Error($"PatchName: {ex}");      // log, don't crash
7. }
                                          // original __result unchanged on error
```

Postfixes that narrow a boolean result also fast-exit when `__result` is already `false` — never permit something the base game denied. Postfixes that add to a numeric result use `+=`, never `=`.

Parameter names in patch methods MUST match the original method's parameter names exactly. Harmony binds by name, not position. A typo'd parameter name silently disables the patch with no error.

---

## 4 — DLL behaviors

### 4.1 — Hab module build limits (`BuildLimitPatch`)

| Field | Effect |
|---|---|
| `bodyBuildLimit` | Max modules in family on a single body, all factions combined |
| `factionBuildLimit` | Max modules in family per faction per body |

Tier cascade: a limit on tier T applies to modules at `EffectiveTier ≥ T`. With T1/T2/T3 limits of 6/4/1, you can have at most 6 in the family total, at most 4 at T2+, at most 1 at T3.

New builds check every tier in the cascade. Upgrades only check the target tier and above (an in-place T1→T2 upgrade doesn't change the family total, so the T1 cap isn't re-checked). The module being upgraded is excluded from the faction count to allow upgrades when the faction count is at the cap.

### 4.2 — Hab module mining bonuses (`MiningPatch`)

| Field | Effect | Patch |
|---|---|---|
| `miningBonus` | Per-active-module flat bonus added to the mining multiplier (uniform across all resources, cumulative across faction-owned habs) | `MiningBonusPatch` |
| `miningCapBonus` | Per-active-module bonus to faction `SafeMineNextworkSize` (cumulative; game preserves the "Nextwork" typo) | `MiningCapBonusPatch` |

Both bonuses iterate every active module the faction owns — output scales with hab investment. `miningBonus` is divided by 100 before adding to the multiplier (so a configured value of 5 = +5% multiplier).

### 4.3 — Hull drive resolution (`DriveVisualPatch`)

Three resolution outcomes per hull, decided by `HullMode` derived from `HullDefinitions.cfg` fields:

| Mode | Path resolves to |
|---|---|
| Unregistered | Vanilla path used as-is |
| `PatchOnly` with `vanillaDriveDataName` | `ships/Earth_<alias>_<variant>` |
| `Hybrid` with `vanillaDriveDataName` | `ships/Earth_<alias>_<variant>` |
| `Hybrid` without `vanillaDriveDataName` | Vanilla path used as-is (dataName already matches a vanilla hull) |
| `FullCustom` | `<bundleName>/<drivePrefab>` |

### 4.4 — Drive variant rendering (`DriveVariantPatch`)

BK hull prefabs hold per-variant child GameObjects under their `Drive` container, each holding a `MeshFilter` + `MeshRenderer` for one (nozzle, thruster-count, ALT) combination. All start inactive. At runtime, `DriveVariantPatch` activates the child matching the equipped drive's nozzle and thruster count.

This sidesteps the cross-bundle shader-instance problem that would otherwise leave drives white: each variant's material is baked into the BK bundle alongside its mesh, so they share a shader instance.

---

## 5 — Adding new behaviors

### 5.1 — New hab module field

1. Add field to `HabModuleConfig` in `ConfigReader.cs`.
2. Add `ApplyInt` line in `ConfigReader.ParseFile()` to read the new field.
3. Create a new `[HarmonyPatch]` class targeting the relevant game method.
4. Follow the patch design pattern (§3).
5. Read from `ConfigReader.habConfigs` to look up per-module values.

### 5.2 — New hull mode or behavior

1. Decide whether the new behavior is hull-axis (per dataName) or controller-axis (per ShipModelController). Add the field to the corresponding registry's parser.
2. If a new mode is needed, extend `HullRegistry.HullMode` enum and update mode-derivation logic in `ParseEntry`.
3. Add or modify a patch class that reads the new field via `HullRegistry.GetByDataName` or `ControllerRegistry.GetByControllerType`.

### 5.3 — Net-new patch on a new game method

1. Verify the method's exact name and parameter list against `Assembly-CSharp.dll` (use dnSpyEx).
2. Create a new `.cs` file with `[HarmonyPatch(typeof(GameType), "MethodName")]` (and parameter type array if the method is overloaded).
3. Match parameter names exactly — Harmony binds by name.
4. Follow the patch design pattern.
5. `harmony.PatchAll` in `Main.cs` will pick it up automatically — no manual registration needed.

---

## 6 — Build and deploy

### 6.1 — Commands

```fish
cd ~/Desktop/Source && dotnet build -c Release
cp bin/Release/net48/BetterKinetics.dll \
   "/mnt/steamgames/SteamLibrary/steamapps/common/Terra Invicta/Mods/Enabled/ExpandedFleetsAndNavies/"
```

Output: `bin/Release/net48/BetterKinetics.dll`. Target framework `net48`. The csproj resolves the TI install path via `TERRA_INVICTA_PATH` env var, then Linux Steam+Proton default, then Windows Steam default.

### 6.2 — Assembly references

| DLL | Where | Why |
|---|---|---|
| `Assembly-CSharp.dll` | `…/TerraInvicta_Data/Managed/` | Game types |
| `UnityEngine.dll` | Same | Unity core |
| `UnityEngine.CoreModule.dll` | Same | Unity core module |
| `UnityEngine.AnimationModule.dll` | Same | Animation API |
| `UnityEngine.PhysicsModule.dll` | Same | Physics API (CapsuleCollider, etc.) |
| `0Harmony.dll` | `…/Managed/UnityModManager/` | Harmony patching |
| `UnityModManager.dll` | Same | UMM API |
| `Newtonsoft.Json.dll` | `…/Managed/` | JSON parsing |

All references use `<Private>false</Private>` so the mod doesn't ship copies of game DLLs.

### 6.3 — Source layout

```
~/Desktop/Source/
├── BetterKinetics.csproj
├── Main.cs
├── ConfigReader.cs
├── HullRegistry.cs
├── ControllerRegistry.cs
├── WeaponMountPatch.cs
├── BuildLimitPatch.cs
├── MiningPatch.cs
├── DriveVisualPatch.cs
├── DriveVariantPatch.cs
└── SetSkinSkipPatch.cs
```

### 6.4 — Deploy folder layout

```
…/Mods/Enabled/ExpandedFleetsAndNavies/
├── BetterKinetics.dll
├── ModInfo.json
├── HullDefinitions.cfg            ← optional, BK's own hull declarations
├── ControllerDefinitions.cfg      ← optional, BK's own slot maps
├── TIShipHullTemplate.json        ← optional, hull definitions
├── TIHabModuleTemplate.json       ← optional, hab fields
├── <bundlename>                   ← AssetBundle, no extension
└── <bundlename>.manifest          ← required if bundle present
```

---

## 7 — Failure modes

| Symptom | Likely cause | Check |
|---|---|---|
| `Load failed: HarmonyException` at boot | A patch's target method changed in a TI update | Recompile against current `Assembly-CSharp.dll`; verify method name and parameter list with dnSpyEx |
| Patch compiles but never fires | Parameter name in patch doesn't match game method | Compare to dnSpyEx exact signature; Harmony binds by name |
| Mod loads but no behavior | `ConfigReader` can't find expected JSON | Check Player.log for "scanned N file(s), loaded N module config(s)" |
| Build limits don't trigger | `bodyBuildLimit` / `factionBuildLimit` not in habConfigs | Check Player.log dictionary counts; verify dataName matches |
| Mining bonus magnitude wrong | Forgot `÷100`, or used `=` instead of `+=` | Verify in log |
| `HullRegistry rejects entry: "no bundleName, drivePrefab, or vanillaDriveDataName"` | Entry has only `dataName` | Add at least one of those three fields, or delete the entry |
| `ControllerRegistry: SlotToWeaponMountIndex not found` | Typo in `controllerClass` or game update renamed class | Verify class name with dnSpyEx |
| `ControllerRegistry: <X> is not a subclass of ShipModelController` | Wrong class name | Verify with dnSpyEx; class must inherit `ShipModelController` |
| White ship in combat | Hybrid/FullCustom hull missing baked materials | Verify `SetSkinSkipPatch` fires in Player.log; verify materials baked correctly |
| White drive in combat (Hybrid) | Variant child missing for that drive | `DriveVariantPatch` warning in Player.log: `no baked variant '<name>' on <hull>` |
| White drive in combat (FullCustom) | Cross-bundle shader-instance mismatch | Drop `drivePrefab` from cfg entry; falls back to Hybrid |
| Drive remap not happening | Hull not registered, or alias not set | Check `DriveVisualPatch` log line for the hull |
| Weapons fire from wrong position | Slot map indices don't match controller-array indices | Run **Verify Hull** in Unity to print indices; reconcile with `ControllerDefinitions.cfg` |
| UMM not loading at all | Doorstop/winhttp issue | Check Player.log (not game log); verify `WINEDLLOVERRIDES="winhttp=n,b"` is set |
