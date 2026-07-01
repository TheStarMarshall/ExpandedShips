# Expanded Fleets and Navies — Technical Reference

This document covers the extension points Expanded Fleets and Navies provides for other Terra Invicta mods. It assumes you are already familiar with TI mod authoring (UMM, the JSON template files, and Harmony patches).

If you only want to play the mod, see the Steam description instead.

## Mod folder layout

```
Mods/Enabled/ExpandedFleetsAndNavies/
├── ModInfo.json                      # UMM mod manifest
├── BetterKinetics.dll                # Compiled Harmony patches and config readers
├── HullDefinitions.cfg               # Hull registry config (read by HullRegistry)
├── ControllerDefinitions.cfg         # Controller weapon-slot remap config (read by ControllerRegistry)
├── TIShipHullTemplate.json           # Hull template patches
├── TIShipHullTemplate.en             # Hull localization
├── TIHabModuleTemplate.json          # Hab module patches + custom field data
├── TIHabModuleTemplate.en            # Hab localization
├── TIDriveTemplate.json              # Drive entries
├── TIDriveTemplate.en                # Drive localization
├── TIProjectTemplate.json            # Project tree changes
├── TIProjectTemplate.en              # Project localization
├── TITechTemplate.json               # Tech tree additions
├── TITechTemplate.en                 # Tech localization
├── TIMagneticGunTemplate.json        # Kinetic weapon stat changes
└── (asset bundles)                   # scoutcruiser, newbattlecruiser, newbattleship
```

The mod loads via UMM's `Main.Load(UnityModManager.ModEntry)` entry point in `BetterKinetics.Main`. At load, `ConfigReader.Init`, `HullRegistry.Init`, and `ControllerRegistry.Init` run before `Harmony.PatchAll`.

The `*.json` and `*.en` files outside the two `.cfg` files are vanilla template patches consumed by Terra Invicta's own loader, not by Expanded Fleets and Navies's extension points.

## Extension points

Expanded Fleets and Navies provides three cross-mod extension points:

- `HullDefinitions.cfg` — register a hull (`dataName`) with a custom asset bundle and/or a vanilla-drive alias.
- `ControllerDefinitions.cfg` — remap weapon slots on a `ShipModelController` subclass.
- Four custom integer fields on `TIHabModuleTemplate.json` entries — body and faction build limits, mining bonus, mining cap bonus.

All three are loaded by scanning every sibling folder under `Mods/Enabled/` at startup. Other mods do not need to modify Expanded Fleets and Navies to participate — drop a file with the right name in your own mod folder.

## HullDefinitions.cfg

A JSON array file. Each entry registers a single hull `dataName` with one of three modes, derived from which fields are set:

| Mode | `bundleName` | `drivePrefab` | What it does |
|---|---|---|---|
| **PatchOnly** | absent | absent | Vanilla hull mesh + vanilla drive. Used to alias another hull's drive assets via `vanillaDriveDataName` (lets a net-new `dataName` reuse an existing hull's visuals). |
| **Hybrid** | set | absent | BK hull mesh from your bundle + vanilla drive. The `dataName` must match a vanilla hull (so its drive paths resolve), unless `vanillaDriveDataName` is also set to point at one. |
| **FullCustom** | set | set | BK hull mesh + BK drive prefab, both from your bundle. |

The file is read by `HullRegistry.Init` at mod startup. Every enabled mod folder is scanned for a file literally named `HullDefinitions.cfg` at the folder root. Cross-mod collisions on `dataName` are resolved with **first-wins** (the first mod to register a `dataName` keeps it; later mods are ignored with a warning).

### Schema

```json
[
  {
    "dataName": "MyHull",
    "bundleName": "myhullbundle",
    "drivePrefab": "MyHull_Drive",
    "vanillaDriveDataName": "Battlecruiser"
  }
]
```

### Field reference

| Field | Type | Required | Purpose |
|---|---|---|---|
| `dataName` | string | yes | Matches the hull's `dataName` in `TIShipHullTemplate.json`. |
| `bundleName` | string | Hybrid / FullCustom | Unity asset bundle name, lowercase. Required when bringing your own 3D mesh. |
| `drivePrefab` | string | FullCustom only | Prefab name inside the asset bundle for the hull's drive object. Setting this without `bundleName` is rejected. |
| `vanillaDriveDataName` | string | optional | Name of a vanilla hull whose drive assets this hull should alias. Required for PatchOnly entries (otherwise the entry does nothing). Useful for Hybrid entries whose `dataName` doesn't match a vanilla hull. Ignored on FullCustom (the explicit `drivePrefab` takes precedence; a warning is logged). |

`controllerClass`, `weaponSlotMap`, and `factionSkin` are not fields on `HullDefinitions.cfg` entries. If they appear here, the entry is parsed but those fields are ignored with a warning.

### Validation

An entry is rejected with an error if any of:

- `dataName` is missing.
- `drivePrefab` is set without `bundleName`.
- All of `bundleName`, `drivePrefab`, and `vanillaDriveDataName` are absent (the entry would do nothing).

### Examples

**FullCustom — new hull with its own bundle and drive prefab:**

```json
{
  "dataName": "MyDestroyer",
  "bundleName": "mydestroyer",
  "drivePrefab": "MyDestroyer_Drive"
}
```

**Hybrid — new hull mesh, reusing a vanilla drive:**

```json
{
  "dataName": "MyDestroyer",
  "bundleName": "mydestroyer",
  "vanillaDriveDataName": "Battlecruiser"
}
```

**PatchOnly — net-new `dataName` that aliases another hull's drive:**

```json
{
  "dataName": "HeavyCruiser",
  "vanillaDriveDataName": "Dreadnought"
}
```

## ControllerDefinitions.cfg

A JSON array file. Each entry remaps weapon slots on one `ShipModelController` subclass via a Harmony prefix on its `SlotToWeaponMountIndex(int slot, Mount mount)` method.

The file is read by `ControllerRegistry.Init` at mod startup, and the patches are attached after `Harmony.PatchAll` via `ControllerRegistry.PatchWeaponMounts`. Every enabled mod folder is scanned for `ControllerDefinitions.cfg` at the folder root. Cross-mod collisions on `controllerClass` are resolved with **first-wins** — only one slot map per controller type is supported.

### Schema

```json
[
  {
    "controllerClass": "MyHullController",
    "weaponSlotMap": [
      {
        "slot": 7,
        "index": 0,
        "overrides": [
          { "mounts": ["OneNose"], "index": 1 }
        ]
      },
      {
        "slot": 8,
        "index": 3,
        "overrides": [
          { "mounts": ["TwoHullVert", "TwoNoseHoriz", "ThreeNoseAngle"], "index": 0 }
        ]
      }
    ]
  }
]
```

### Field reference

| Field | Type | Required | Purpose |
|---|---|---|---|
| `controllerClass` | string | yes | The C# class name of the controller (e.g. `DreadnoughtController`, `MyHullController`). Looked up by name in `Assembly-CSharp`. Entries naming an unknown class are skipped with an error. |
| `weaponSlotMap` | array | yes | Slot-to-mount-index mappings. Empty arrays are accepted but produce a warning ("entry adds nothing"). |

Each `weaponSlotMap` element:

| Field | Type | Required | Purpose |
|---|---|---|---|
| `slot` | integer | yes | The 0-indexed position of the weapon's entry in the hull JSON's `shipModuleSlots` array. |
| `index` | integer | yes | The mount index to return for this slot when no `mounts`-specific override applies. |
| `overrides` | array | optional | Per-mount-type overrides. Each has `mounts` (array of `Mount` enum names) and `index` (integer). The first override whose `mounts` list contains the called mount type is used. |

Slots not listed in `weaponSlotMap` fall through to the controller's compiled behavior unchanged (the Harmony prefix returns `true`, vanilla runs).

If your hull adds a weapon hardpoint at a slot index that the vanilla controller does not handle, you must include it in `weaponSlotMap` or accept the controller's default branch (often `return 0`, which collides with the first nose mount).

Hull-axis fields (`dataName`, `bundleName`, `drivePrefab`, `vanillaDriveDataName`) appearing in this file are ignored with a warning.

### Mount enum values

The `mounts` field accepts vanilla TI `Mount` enum names. The full set:

```
OneNose, OneHull,
TwoNoseHoriz, TwoNoseVert, TwoHullHoriz, TwoHullVert,
ThreeNoseAngle, ThreeHullHoriz,
FourNose, FourHull,
HalfNose, HalfHull
```

Unknown values are rejected and the entry is skipped with an error.

## Custom hab module fields

Expanded Fleets and Navies reads four optional integer fields on `TIHabModuleTemplate.json` entries beyond the vanilla schema. Vanilla TI ignores these fields; Expanded Fleets and Navies's Harmony patches consume them at runtime.

The file is read by `ConfigReader.Init` at mod startup. Every enabled mod folder is scanned for `TIHabModuleTemplate.json` at the folder root. Multiple mods can patch the same module's custom fields; later mods override individual fields without clearing other fields.

### Schema

```json
[
  {
    "dataName": "MyModule",
    "bodyBuildLimit": 6,
    "factionBuildLimit": 1,
    "miningBonus": 8,
    "miningCapBonus": 6
  }
]
```

### Field reference

| Field | Type | Default | Purpose |
|---|---|---|---|
| `bodyBuildLimit` | integer ≥ 0 | unlimited | Maximum number of this module that can be built on a single planetary body, summed across all factions. |
| `factionBuildLimit` | integer ≥ 0 | unlimited | Maximum number of this module a single faction can own globally. |
| `miningBonus` | integer ≥ 0 | 0 | Mining-output boost in percentage points per instance on a body. The integer is divided by 100 at runtime, so `miningBonus: 8` adds `0.08` to the body's mining multiplier. |
| `miningCapBonus` | integer ≥ 0 | 0 | Additional mining cap units per instance on a body. |

All fields are optional. Absent fields are silently ignored. Wrong types (non-integer) and negative values produce a warning and are ignored.

### Behavior notes

- Cross-mod merge is **per field**: if mod A sets `bodyBuildLimit` and mod B sets `miningBonus` on the same `dataName`, both apply. If both mods set the same field, the later-loaded mod wins for that field; other fields are not cleared.
- Build-limit semantics within `BuildLimitPatch`: limits cascade across the upgrade family — a tier-1 limit counts the whole upgrade chain, tier-2 counts T2+T3, tier-3 counts T3 only. The most restrictive applicable limit wins for each tier.
- Mining bonuses sum across instances of the same module on the same body, and across different bonus-providing modules.
- Mining cap bonuses sum across instances of the same module on the same body.

### Example — adding a custom mining module

```json
[
  {
    "dataName": "MyMiningModule",
    "friendlyName": "My Mining Module",
    "tier": 2,
    "habType": "Body",
    "requiredProjectName": "Project_MyMiningModule",
    "miningBonus": 8,
    "miningCapBonus": 6,
    "bodyBuildLimit": 4,
    "factionBuildLimit": 2
  }
]
```

This combines vanilla TI fields with Expanded Fleets and Navies's four custom fields. Vanilla TI's hab loader processes the standard fields; `ConfigReader` separately picks up the custom fields and applies them via Harmony patches.

## Load order

All three extension systems scan every sibling folder under `Mods/Enabled/` at startup. UMM's load order does not affect which mods are scanned (all enabled mods are scanned), but it does affect file processing order within each scan, which determines the winner on collision:

| File | Collision policy |
|---|---|
| `HullDefinitions.cfg` | First-wins on `dataName`. The first mod to register a `dataName` keeps it; later mods are ignored. |
| `ControllerDefinitions.cfg` | First-wins on `controllerClass`. The first mod to register a controller's slot map keeps it; later mods are ignored. |
| `TIHabModuleTemplate.json` custom fields | Per-field overwrite. Later mods replace individual field values; other fields previously set are not cleared. |

There is no mechanism to declare a hard dependency or required order between mods using these systems. If two mods set conflicting values, the result depends on UMM's directory enumeration order, which is filesystem-dependent.

## Logging

All extension load activity is logged via UMM's mod log. Look for messages prefixed with `HullRegistry:`, `ControllerRegistry:`, and `ConfigReader:`:

```
HullRegistry: scanned N file(s), N hull(s) registered.
HullRegistry: parsed <dataName> from <sourceMod> (patch-only, drive alias '<X>').
HullRegistry: parsed <dataName> from <sourceMod>, hybrid bundle '<X>'[, drive alias '<Y>'].
HullRegistry: parsed <dataName> from <sourceMod>, full-custom bundle '<X>', drive '<Y>'.
HullRegistry: dataName '<X>' from '<modA>' ignored — already registered by '<modB>' (first-wins).

ControllerRegistry: scanned N file(s), parsed N entry(ies).
ControllerRegistry: parsed <controllerClass> from <sourceMod> (N mappings).
ControllerRegistry: weaponSlotMap for '<controllerClass>' from '<modA>' ignored — already registered by '<modB>' (first-wins).
ControllerRegistry: patched N controller(s) for weapon slot remapping.

ConfigReader: scanned N file(s), loaded N module config(s).
ConfigReader: '<field>' on '<dataName>' is <type>, expected Integer — ignored.
ConfigReader: '<field>' on '<dataName>' is negative (<n>) — ignored.
```

## Compatibility

Expanded Fleets and Navies is built against:

- Terra Invicta v1.0.32
- Unity Mod Manager 0.24+
- Newtonsoft.Json (shipped with the game)

## Patches applied

Expanded Fleets and Navies applies Harmony patches to the following vanilla methods. Other mods patching the same methods may interact:

| Patch | Target | Type | Purpose |
|---|---|---|---|
| `BuildLimitPatch` | `TIHabState.IsModuleAllowedForHab` | Postfix | Enforces `bodyBuildLimit` and `factionBuildLimit` from `TIHabModuleTemplate.json`, with tier-cascade semantics across upgrade families. |
| `MiningBonusPatch` | `TIFactionState.GetCurrentMiningMultiplierFromOrgsAndEffects` | Postfix | Adds `miningBonus / 100` per instance of any bonus-providing module on the body. |
| `MiningCapBonusPatch` | `TIFactionState.SafeMineNextworkSize` | Postfix | Adds `miningCapBonus` per instance. (The vanilla method's misspelling — `Nextwork` — is preserved by `nameof()`.) |
| `DriveVisualPatch` | `TIDriveTemplate.modelResource(hull, appearanceIndex)` | Postfix | Rewrites the drive asset path for hulls registered in `HullDefinitions.cfg`. PatchOnly and Hybrid hulls with `vanillaDriveDataName` get the alias substituted; FullCustom hulls get `<bundleName>/<drivePrefab>`. |
| `DriveVariantPatch` | `ShipModelController.BuildDrives` | Postfix | Activates the matching baked drive-variant child on the prefab for the equipped drive. |
| `SetSkinSkipPatch` | `HumanShipController.SetSkin` | Prefix | Returns `false` (skips vanilla) for Hybrid and FullCustom hulls — vanilla SetSkin would overwrite the edit-time-baked materials with cross-bundle paths that don't resolve. PatchOnly hulls fall through to vanilla. |
| `WeaponMountPatch.DynamicPrefix` | `<ControllerClass>.SlotToWeaponMountIndex` per registered controller | Prefix | Applied dynamically by `ControllerRegistry.PatchWeaponMounts` to each controller class registered in `ControllerDefinitions.cfg`. Returns the mapped `index` (or override `index`) for matching slots and skips vanilla; falls through for unmapped slots. |
