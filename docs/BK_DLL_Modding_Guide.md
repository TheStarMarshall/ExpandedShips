# BetterKinetics - DLL Modding Guide

**Game:** Terra Invicta · **Harmony:** 2.3.6.0 (HarmonyX/MonoMod)

How Harmony DLL modding works in Terra Invicta and what BetterKinetics's DLL does.

---

## 1 - How it works

### 1.1 - The problem

Terra Invicta's game code is compiled C# in `Assembly-CSharp.dll`. Mods can override JSON data templates, but adding new behaviors (build limits, mining bonuses, custom hull rendering, weapon-slot remapping) requires modifying code at runtime.

### 1.2 - The solution: Harmony

Harmony patches compiled .NET methods at runtime by inserting code before or after the original.

| Type | When | Use |
|---|---|---|
| Prefix | Before the original method | Replace behavior - return `false` to skip the original |
| Postfix | After the original method | Augment behavior - modify `__result` via `ref` |

### 1.3 - Why custom JSON fields need DLL

Vanilla `TIHabModuleTemplate` has no `[JsonExtensionData]` attribute - custom fields added to JSON are silently dropped during deserialization. The game never sees them. BK's DLL reads raw JSON independently at startup via `ConfigReader`, building its own lookup dictionaries keyed by `dataName`.

### 1.4 - Load chain

```
Game starts
  → Proton loads winhttp.dll (DoorstopProxy)
  → DoorstopProxy loads UnityModManager
  → UMM reads Mods/Enabled/*/ModInfo.json
  → UMM resolves load order
  → UMM calls each mod's EntryMethod
  → BetterKinetics.Main.Load() runs:
    1. ConfigReader.Init()              - parse hab module custom fields
    2. HullRegistry.Init()              - parse HullDefinitions.cfg per-dataName
    3. ControllerRegistry.Init()        - parse ControllerDefinitions.cfg per-controller
    4. harmony.PatchAll()               - applies all [HarmonyPatch] classes:
                                          BuildLimitPatch, MiningPatch (×2),
                                          HabDescriptionPatch, DriveVisualPatch,
                                          DriveVariantPatch, SetSkinSkipPatch,
                                          CombatScalePatch
    5. ControllerRegistry.PatchWeaponMounts() - dynamic per-controller
                                                SlotToWeaponMountIndex prefixes
```

---

## 2 - Source files

Ten files compile into `BetterKinetics.dll`. Each has a single responsibility; the DLL is intentionally small and orthogonal.

| File | Role |
|---|---|
| `Main.cs` | UMM entry point, load chain, logging |
| `ConfigReader.cs` | Hab module config parser (cross-mod) |
| `HullRegistry.cs` | Hull-axis registry (per dataName) |
| `ControllerRegistry.cs` | Controller-axis registry (per ShipModelController subclass) |
| `WeaponMountPatch.cs` | Dynamic prefix used by ControllerRegistry |
| `BuildLimitPatch.cs` | Hab module construction limits |
| `MiningPatch.cs` | Hab module mining bonuses |
| `HabDescriptionPatch.cs` | Hab module UI text (custom fields + station income) |
| `DriveVisualPatch.cs` | Drive prefab path resolution |
| `DriveVariantPatch.cs` | Per-spawn drive variant selector |
| `SetSkinSkipPatch.cs` | Vanilla skinning bypass for BK-bundle hulls |
| `CombatScalePatch.cs` | Rendered ship length normalization |

### 2.1 - `Main.cs`

UMM entry point.

- **Does:** Receives the `UnityModManager.ModEntry` callback. Initializes the three registries in order, creates the Harmony instance, applies all `[HarmonyPatch]` classes via `PatchAll`, then calls `ControllerRegistry.PatchWeaponMounts` for dynamic per-controller patches. Catches any load exception, unpatches everything, and tells UMM the load failed cleanly.
- **Touches:** `UnityModManager.ModEntry`, `HarmonyLib.Harmony`. Calls every other BK file's static initializer.
- **Provides:** `Main.enabled` (UMM toggle state), `Main.Log`, `Main.Warning`, `Main.Error` for the rest of the DLL.

### 2.2 - `ConfigReader.cs`

Reads custom hab-module fields out of `TIHabModuleTemplate.json`.

- **Does:** Walks every sibling mod folder under `Mods/Enabled/`. For each `TIHabModuleTemplate.json` found, parses entries with Newtonsoft.Json and populates a `Dictionary<string, HabModuleConfig>` keyed by `dataName`. Reads four optional integer fields: `bodyBuildLimit`, `factionBuildLimit`, `miningBonus`, `miningCapBonus`. Negative values and wrong-type values are warned and ignored.
- **Touches:** Newtonsoft.Json. Filesystem (read-only).
- **Output:** `ConfigReader.habConfigs` - consumed by `BuildLimitPatch`, `MiningPatch`, and `HabDescriptionPatch`.
- **Cross-mod:** Per-field merge. Later mods can extend prior mods' configs without overwriting unrelated fields (a mod that sets only `miningBonus` for `MyRefinery` won't clear another mod's `bodyBuildLimit` on the same dataName).

### 2.3 - `HullRegistry.cs`

Per-`dataName` registry for ship hulls.

- **Does:** Walks every sibling mod folder for `HullDefinitions.cfg`. Each entry resolves to one of three modes by field combination: **PatchOnly** (only `vanillaDriveDataName` set - alias another vanilla hull's drive assets), **Hybrid** (`bundleName` set, no `drivePrefab` - BK hull mesh + vanilla drive variants), or **FullCustom** (both `bundleName` and `drivePrefab` - BK hull mesh + BK drive prefab). Rejects entries with neither a bundle, drive prefab, nor alias. Rejects `drivePrefab` without `bundleName`. Warns on FullCustom (cross-bundle shader rendering is fragile).
- **Touches:** Newtonsoft.Json. Filesystem (read-only).
- **Output:** `HullRegistry.GetByDataName(string)` - consumed by `DriveVisualPatch`, `DriveVariantPatch` (indirectly via runtime queries), and `SetSkinSkipPatch`.
- **Cross-mod:** First-wins on `dataName` collision.

### 2.4 - `ControllerRegistry.cs`

Per-controller-class registry for weapon-slot remapping.

- **Does:** Walks every sibling mod folder for `ControllerDefinitions.cfg`. Each entry names one `ShipModelController` subclass (resolved via reflection against `Assembly-CSharp.dll`) and a list of slot mappings: `slot → index`, with optional per-`Mount` overrides. Validates the resolved type actually inherits `ShipModelController`. After Harmony's `PatchAll`, calls `harmony.Patch` directly on each controller's `SlotToWeaponMountIndex` method, attaching `WeaponMountPatch.DynamicPrefix` as the prefix.
- **Touches:** Newtonsoft.Json. Reflection on `Assembly-CSharp.dll`. Direct Harmony patch invocation.
- **Output:** `ControllerRegistry.GetByControllerType(Type)` - consumed by `WeaponMountPatch`.
- **Cross-mod:** First-wins on `controllerClass` collision.

### 2.5 - `WeaponMountPatch.cs`

Dynamic prefix attached by `ControllerRegistry` to every registered controller's `SlotToWeaponMountIndex`.

- **Does:** Looks up the controller's slot map. Walks per-mount overrides first; on a `Mount` match returns the override's `index` and skips vanilla. Otherwise returns the slot's default `index`. No mapping match → falls through to vanilla `SlotToWeaponMountIndex`.
- **Touches:** `ControllerRegistry.GetByControllerType`. Patches game's `SlotToWeaponMountIndex` (one per registered controller).

### 2.6 - `BuildLimitPatch.cs`

Hab-module construction enforcement.

- **Does:** Postfix on `TIHabState.IsModuleAllowedForHab`. When a module has a configured `bodyBuildLimit` or `factionBuildLimit`, walks every hab on the target body, counts existing modules in the same upgrade family at or above the target tier, and sets `__result = false` if the cascade limit is reached. Tier cascade semantics: a limit on tier T means "at most N modules at `EffectiveTier ≥ T`."
- **Touches:** Patches `TIHabState.IsModuleAllowedForHab`. Reads `ConfigReader.habConfigs`. Iterates `body.habs`, `hab.sectors`, `sector.habModules` to count.
- **Safety:** Only narrows the base game's verdict (sets `__result = false`). Never permits a module the base game denied. Disabled mod = no effect.

### 2.7 - `MiningPatch.cs`

Hab-module mining-bonus aggregation. Two patches in one file.

- **`MiningBonusPatch`** - Postfix on `TIFactionState.GetCurrentMiningMultiplierFromOrgsAndEffects(FactionResource)`. Sums every active hab module's `miningBonus`, divides by 100, adds to `__result`. The bonus is uniform across resources - the `resource` parameter is required for Harmony's name-based binding but isn't used to filter.
- **`MiningCapBonusPatch`** - Postfix on `TIFactionState.SafeMineNextworkSize` getter (game preserves the typo "Nextwork"). Sums every active hab module's `miningCapBonus`, adds to `__result`.
- **Touches:** Patches two `TIFactionState` methods. Reads `ConfigReader.habConfigs`. Iterates `faction.habs`, `hab.sectors`, `sector.habModules`. Helper `MiningPatchHelper.SumActiveModuleBonus` is shared by both.

### 2.8 - `HabDescriptionPatch.cs`

Hab module UI text.

- **Does:** Postfix on `TIHabModuleTemplate.benefitsAndCostsDescription`, the single builder behind the hab build tooltip, the installed-module tooltip, and the research screen module preview. Appends a "Module Rules" block for any module with an entry in `ConfigReader.habConfigs` (mining bonus, mining cap bonus, per-body limit, per-faction-per-body limit; unset fields are skipped). For Station modules rendered without a hab context (research screen), also splices the template's flat material incomes (Water, Volatiles, Metals, NobleMetals, Fissiles) into the vanilla income line, which the base game leaves at zero in that context. Antimatter and Exotics are excluded because vanilla already shows them there.
- **Why:** The custom fields exist only in `ConfigReader.habConfigs`, never on the template, so no vanilla code path can display them. Display-only; gameplay math is untouched.
- **Touches:** Patches `TIHabModuleTemplate.benefitsAndCostsDescription`. Reads `ConfigReader.habConfigs` and template income fields. Uses `Loc.T` and `TIUtilities` formatters to match vanilla output.

### 2.9 - `DriveVisualPatch.cs`

Drive-prefab path resolution.

- **Does:** Postfix on `TIDriveTemplate.modelResource(TIShipHullTemplate, int)`. For hulls registered in `HullRegistry`, rewrites the returned path according to mode:
  - `FullCustom` → replaces the entire path with `<bundleName>/<drivePrefab>`.
  - `Hybrid` / `PatchOnly` with `vanillaDriveDataName` set → substitutes `hull.dataName` with the alias inside vanilla's two path patterns: chem (`_<dataName>_`) and alien (`_<dataName>x`). Boundary characters prevent substring collisions.
  - Everything else → unchanged.
- **Touches:** Patches `TIDriveTemplate.modelResource`. Reads `HullRegistry.GetByDataName`.
- **One-time log gate:** caches `(originalPath, remappedPath)` pairs and logs each remap exactly once to keep the log readable.

### 2.10 - `DriveVariantPatch.cs`

Per-spawn drive variant selector for BK hulls.

- **Does:** Postfix on `ShipModelController.BuildDrives`. Iterates child GameObjects of the ship's `thrusterModel` (the `Drive` container). Looks for a child name matching the equipped drive's variant - `<nozzleStr>x<thrusters>`, with `_ALT1` suffix if `hullAppearanceIndex == 1`. Activates the matching child via `SetActive(true)` and deactivates other variant children. If no children match the variant naming convention (DeLavalx<n> / Magneticx<n> / Pulsex<n>), bails silently - vanilla hulls are unaffected.
- **Touches:** Patches `ShipModelController.BuildDrives`. Reads `ship.driveTemplate`. Operates on baked variant children placed inside BK hull prefabs at edit time by `ShipTools.BakeDriveVariants`.

### 2.11 - `SetSkinSkipPatch.cs`

Vanilla-skinning bypass for BK-bundle hulls.

- **Does:** Prefix on `HumanShipController.SetSkin`. For Hybrid or FullCustom hulls (per `HullRegistry`), returns `false` to skip vanilla `SetSkin` entirely. PatchOnly and unregistered hulls fall through to vanilla.
- **Why:** Vanilla `SetSkin` overwrites every `MeshRenderer.sharedMaterial` under `hullModel` with paths from a vanilla faction bundle. Those paths don't resolve for BK-bundle meshes - result is white ships. The edit-time bake already wrote correct bundle-internal materials onto each renderer; skipping vanilla `SetSkin` preserves them.
- **Touches:** Patches `HumanShipController.SetSkin`. Reads `HullRegistry.GetByDataName`.

### 2.12 - `CombatScalePatch.cs`

Rendered ship length normalization.

- **Does:** Postfix on `ShipVisController.InitializeShipVisualizer`. Scales each ship's visualizer so its rendered length matches the hull template's `length_m` at creation time.
- **Why:** Keeps on-screen sizing consistent and proportional across combat and strategic views, including hulls whose meshes ship at arbitrary scales.
- **Touches:** Patches `ShipVisController.InitializeShipVisualizer`. Reads `TIShipHullTemplate.length_m`.

---

## 3 - Patch design pattern

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

Postfixes that narrow a boolean result also fast-exit when `__result` is already `false` - never permit something the base game denied. Postfixes that add to a numeric result use `+=`, never `=`.

Parameter names in patch methods MUST match the original method's parameter names exactly. Harmony binds by name, not position. A typo'd parameter name silently disables the patch with no error.

---

## 4 - DLL behaviors

### 4.1 - Hab module build limits (`BuildLimitPatch`)

| Field | Effect |
|---|---|
| `bodyBuildLimit` | Max modules in family on a single body, all factions combined |
| `factionBuildLimit` | Max modules in family per faction per body |

Tier cascade: a limit on tier T applies to modules at `EffectiveTier ≥ T`. With T1/T2/T3 limits of 6/4/1, you can have at most 6 in the family total, at most 4 at T2+, at most 1 at T3.

New builds check every tier in the cascade. Upgrades only check the target tier and above (an in-place T1→T2 upgrade doesn't change the family total, so the T1 cap isn't re-checked). The module being upgraded is excluded from the faction count to allow upgrades when the faction count is at the cap.

### 4.2 - Hab module mining bonuses (`MiningPatch`)

| Field | Effect | Patch |
|---|---|---|
| `miningBonus` | Per-active-module flat bonus added to the mining multiplier (uniform across all resources, cumulative across faction-owned habs) | `MiningBonusPatch` |
| `miningCapBonus` | Per-active-module bonus to faction `SafeMineNextworkSize` (cumulative; game preserves the "Nextwork" typo) | `MiningCapBonusPatch` |

Both bonuses iterate every active module the faction owns - output scales with hab investment. `miningBonus` is divided by 100 before adding to the multiplier (so a configured value of 5 = +5% multiplier).

### 4.3 - Hab module UI display (`HabDescriptionPatch`)

Configured fields render in the hab build tooltip, the installed-module tooltip, and the research screen module preview:

| Field | Displayed as |
|---|---|
| `miningBonus` | `Mining Output: +N%` |
| `miningCapBonus` | `Mining Cap: +N` |
| `bodyBuildLimit` | `Limit: N per body (all factions)` |
| `factionBuildLimit` | `Limit: N per faction per body` |

Unset fields produce no line. A zero limit still displays (it means unbuildable); zero bonuses do not. Station modules additionally show their flat material incomes on the research screen, which vanilla omits when no hab context exists.

### 4.4 - Hull drive resolution (`DriveVisualPatch`)

Three resolution outcomes per hull, decided by `HullMode` derived from `HullDefinitions.cfg` fields:

| Mode | Path resolves to |
|---|---|
| Unregistered | Vanilla path used as-is |
| `PatchOnly` with `vanillaDriveDataName` | `ships/Earth_<alias>_<variant>` |
| `Hybrid` with `vanillaDriveDataName` | `ships/Earth_<alias>_<variant>` |
| `Hybrid` without `vanillaDriveDataName` | Vanilla path used as-is (dataName already matches a vanilla hull) |
| `FullCustom` | `<bundleName>/<drivePrefab>` |

### 4.5 - Drive variant rendering (`DriveVariantPatch`)

BK hull prefabs hold per-variant child GameObjects under their `Drive` container, each holding a `MeshFilter` + `MeshRenderer` for one (nozzle, thruster-count, ALT) combination. All start inactive. At runtime, `DriveVariantPatch` activates the child matching the equipped drive's nozzle and thruster count.

This sidesteps the cross-bundle shader-instance problem that would otherwise leave drives white: each variant's material is baked into the BK bundle alongside its mesh, so they share a shader instance.

---

## 5 - Adding new behaviors

### 5.1 - New hab module field

1. Add field to `HabModuleConfig` in `ConfigReader.cs`.
2. Add `ApplyInt` line in `ConfigReader.ParseFile()` to read the new field.
3. Create a new `[HarmonyPatch]` class targeting the relevant game method.
4. Follow the patch design pattern (§3).
5. Read from `ConfigReader.habConfigs` to look up per-module values.
6. To surface the field in tooltips and the research screen, add a display line for it in `HabDescriptionPatch.AppendCustomBlock`.

### 5.2 - New hull mode or behavior

1. Decide whether the new behavior is hull-axis (per dataName) or controller-axis (per ShipModelController). Add the field to the corresponding registry's parser.
2. If a new mode is needed, extend `HullRegistry.HullMode` enum and update mode-derivation logic in `ParseEntry`.
3. Add or modify a patch class that reads the new field via `HullRegistry.GetByDataName` or `ControllerRegistry.GetByControllerType`.

### 5.3 - Net-new patch on a new game method

1. Verify the method's exact name and parameter list against `Assembly-CSharp.dll` (use dnSpyEx).
2. Create a new `.cs` file with `[HarmonyPatch(typeof(GameType), "MethodName")]` (and parameter type array if the method is overloaded).
3. Match parameter names exactly - Harmony binds by name.
4. Follow the patch design pattern.
5. `harmony.PatchAll` in `Main.cs` will pick it up automatically - no manual registration needed.

---

## 6 - Build and deploy

### 6.1 - Commands

```fish
dotnet build <repo>/src/BetterKinetics.csproj -c Release
cp <repo>/src/bin/Release/net48/BetterKinetics.dll "<game>/Mods/Enabled/<YourModFolder>/"
```

Output: `bin/Release/net48/BetterKinetics.dll`. Target framework `net48`. The csproj resolves the TI install path via `TERRA_INVICTA_PATH` env var, then Linux Steam+Proton default, then Windows Steam default.

### 6.2 - Assembly references

| DLL | Where | Why |
|---|---|---|
| `Assembly-CSharp.dll` | `<game>/TerraInvicta_Data/Managed/` | Game types |
| `UnityEngine.dll` | Same | Unity core |
| `UnityEngine.CoreModule.dll` | Same | Unity core module |
| `UnityEngine.AnimationModule.dll` | Same | Animation API |
| `UnityEngine.PhysicsModule.dll` | Same | Physics API (CapsuleCollider, etc.) |
| `0Harmony.dll` | `<game>/TerraInvicta_Data/Managed/UnityModManager/` | Harmony patching |
| `UnityModManager.dll` | Same | UMM API |
| `Newtonsoft.Json.dll` | `<game>/TerraInvicta_Data/Managed/` | JSON parsing |

All references use `<Private>false</Private>` so the mod doesn't ship copies of game DLLs.

### 6.3 - Source layout

```
<repo>/src/
├── BetterKinetics.csproj
├── Main.cs
├── ConfigReader.cs
├── HullRegistry.cs
├── ControllerRegistry.cs
├── WeaponMountPatch.cs
├── BuildLimitPatch.cs
├── MiningPatch.cs
├── HabDescriptionPatch.cs
├── DriveVisualPatch.cs
├── DriveVariantPatch.cs
├── SetSkinSkipPatch.cs
└── CombatScalePatch.cs
```

### 6.4 - Deploy folder layout

```
.../Mods/Enabled/ExpandedFleetsAndNavies/
├── BetterKinetics.dll
├── ModInfo.json
├── HullDefinitions.cfg            ← optional, BK's own hull declarations
├── ControllerDefinitions.cfg      ← optional, BK's own slot maps
├── TIShipHullTemplate.json        ← optional, hull definitions
├── TIHabModuleTemplate.json       ← optional, hab fields
├── <TemplateName>.en              ← optional, localization for added templates
├── <bundlename>                   ← AssetBundle, no extension
└── <bundlename>.manifest          ← required if bundle present
```

### 6.5 - Workshop publishing (SteamCMD)

Upload with SteamCMD's `workshop_build_item`, pointing at the folder that holds exactly the files to publish. The in-game uploader stages a fresh copy of the currently published item each session, so local changes are not what gets uploaded; SteamCMD uploads the named folder as the item's complete new content, replacing prior content.

Write a VDF:

```
"workshopitem"
{
 "appid" "1176470"
 "publishedfileid" "<your item id>"
 "contentfolder" "<absolute path to mod content>"
 "changenote" "<what changed>"
}
```

Omit `title`, `description`, and `previewfile` to leave the live Workshop page metadata untouched. Upload and verify:

```fish
steamcmd +login <steam_login> +workshop_build_item <path-to-vdf> +quit
steamcmd +login <steam_login> +workshop_download_item 1176470 <your item id> +quit
diff -r <mod content folder> ~/.steam/steamcmd/steamapps/workshop/content/1176470/<your item id>
```

An empty diff means the published item is byte-identical to the content folder. For a first upload, omit `publishedfileid`; SteamCMD prints the new id on success.

---

## 7 - Failure modes

| Symptom | Likely cause | Check |
|---|---|---|
| `Load failed: HarmonyException` at boot | A patch's target method changed in a TI update | Recompile against current `Assembly-CSharp.dll`; verify method name and parameter list with dnSpyEx |
| Patch compiles but never fires | Parameter name in patch doesn't match game method | Compare to dnSpyEx exact signature; Harmony binds by name |
| Mod loads but no behavior | `ConfigReader` can't find expected JSON | Check Player.log for "scanned N file(s), loaded N module config(s)" |
| Module Rules block missing from tooltip | Field not in `habConfigs` (file not found, wrong type, or dataName mismatch) | Check Player.log dictionary counts; verify field spelling and dataName |
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
