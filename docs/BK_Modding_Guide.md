# BetterKinetics — Hull Modder's Guide

**Game:** Terra Invicta v1.0.32 · **Unity:** 2020.3.49f1

How to add or modify ship hulls that work with BetterKinetics.

---

## 1 — Four ways to modify a hull

| Use case | What you get | Unity work | Bundle |
|---|---|---|---|
| **A — Vanilla-asset alias** | A new dataName that reuses an existing vanilla hull's appearance and drives, with your own stats and slot map | none | none |
| **B — Slot remap only** | Change which physical mount handles which JSON slot on an existing hull (vanilla or custom), without rebuilding anything | none | none |
| **C — Hybrid hull** | A hull with custom geometry, using vanilla drive variants (DeLaval / Magnetic / Pulse + thruster counts) | full | yes |
| **D — FullCustom hull** | A hull with custom geometry **and** a custom drive prefab | full | yes |

A and B are config-only. C and D require the Unity pipeline (§3–§4).

Your mod folder is a sibling of `ExpandedFleetsAndNavies/` under `Mods/Enabled/`. BK scans every sibling folder at startup; you don't touch BK's own folder. First-wins on `dataName` collision across mods.

---

## 2 — Prerequisites

### 2.1 — Always required

- A `ExpandedFleetsAndNavies` mod folder under `…/Terra Invicta/Mods/Enabled/` containing `BetterKinetics.dll`.
- UnityModManager installed and active.
- Your own mod folder (any name) as a sibling of `ExpandedFleetsAndNavies/`.

### 2.2 — Additional for use cases C and D

- Unity 2020.3.49f1 — exact version. Anything else gives wrong scale and shader issues.
- AssetRipper 1.3.12+
- BK's `ShipTools.cs` editor script.

### 2.3 — Paths

| Item | Path |
|---|---|
| Game data | `…/Terra Invicta/TerraInvicta_Data` |
| BK mod folder | `…/Terra Invicta/Mods/Enabled/ExpandedFleetsAndNavies/` |
| Unity working project | `~/Desktop/Ships/TI_Ships_Extracted/ExportedProject/` |
| Recovery copy (do not modify) | `~/Desktop/Ships/TI_Recovery/ExportedProject/` |
| Bundle output | `~/Desktop/BundleOut/` |

`ShipTools.cs` hardcodes `BundleOutPath` and `DeployPath`. Edit those constants if your paths differ.

---

## 3 — One-time Unity setup (use cases C and D)

Run this once per workstation. After this, every new hull goes straight to §6.

### 3.1 — Extract the game with AssetRipper

Launch AssetRipper. Set:

| Setting | Value |
|---|---|
| Default Version | `2020.3.49f1` |
| Bundled Assets Export Mode | Group By Asset Type |
| Script Content Level | Level 2 |
| Image Export Format | Png |
| Shader Export Mode | Dummy Shader |
| Script Export Format | Hybrid |
| Skip StreamingAssets Folder | Unchecked |
| Save Settings to Disk | Checked |

**File → Open Folder** → `TerraInvicta_Data`. Wait for "Finished processing assets". **Export → Export All Files** → `~/Desktop/Ships/TI_Ships_Extracted`.

Relaunch AssetRipper and export a second identical copy to `~/Desktop/Ships/TI_Recovery`. The recovery copy is your safety net — never modify it.

### 3.2 — Fix decompiler artifacts

```fish
python3 fix_decompiler_errors.py
```

Patches decompiler-produced syntax errors so Unity will accept the project. Without this, Unity opens in Safe Mode.

### 3.3 — Install ShipTools

```fish
mkdir -p ~/Desktop/Ships/TI_Ships_Extracted/ExportedProject/Assets/Editor
cp ~/Desktop/Source/ShipTools.cs ~/Desktop/Ships/TI_Ships_Extracted/ExportedProject/Assets/Editor/
```

### 3.4 — First Unity launch

```fish
unity -projectPath ~/Desktop/Ships/TI_Ships_Extracted/ExportedProject
```

First open: 30–60 minutes. If "Enter Safe Mode?" appears, click **Ignore**, then re-run §3.2.

### 3.5 — Project prep (run once, in order)

1. **Ship Tools → Setup → 1. Strip Vanilla Bundle Tags** — removes inherited bundle tags from vanilla assets. Without this, builds take 40+ minutes and produce 500MB+ bundles.
2. **Ship Tools → Setup → 2. Disable Streaming on All Textures** — avoids texture-streaming runtime null refs.
3. **Ship Tools → Setup → 3. Reimport Hull Textures as DXT** — DXT compression required for cross-platform asset bundles.

---

## 4 — Use case A: Vanilla-asset alias

Use this when you want a new dataName (e.g. `HeavyCruiser`) that visually reuses an existing vanilla hull (e.g. Dreadnought) but has different stats, model resource, or slot map.

### 4.1 — Add to `TIShipHullTemplate.json`

In your mod folder, create or extend `TIShipHullTemplate.json`. Point `modelResource` at the vanilla bundle that holds the actual geometry:

```json
{
  "dataName": "HeavyCruiser",
  "modelResource": ["ships/Dreadnought", "ships/Dreadnought"]
}
```

### 4.2 — Add to `HullDefinitions.cfg`

Tell BK which vanilla hull's drive assets to use:

```json
[
  {
    "dataName": "HeavyCruiser",
    "vanillaDriveDataName": "Dreadnought"
  }
]
```

BK substitutes `Earth_HeavyCruiser_<variant>` paths to `Earth_Dreadnought_<variant>` at drive resolution time. No bundle, no Unity.

> Each cfg entry must declare at least one of `bundleName`, `drivePrefab`, or `vanillaDriveDataName`. Entries with none are rejected.

### 4.3 — Optional: slot remapping

If your new dataName needs different mount-slot routing on its shared controller, add an entry to `ControllerDefinitions.cfg` (§10).

---

## 5 — Use case B: Slot remap only

Use this when you want to change which physical mount handles which JSON slot index on an existing hull (vanilla or your own) without touching the hull mesh, bundle, or DLL.

### 5.1 — Add to `ControllerDefinitions.cfg`

```json
[
  {
    "controllerClass": "BattlecruiserController",
    "weaponSlotMap": [
      { "slot": 7, "index": 0 },
      { "slot": 8, "index": 1 },
      { "slot": 9, "index": 2 }
    ]
  }
]
```

Each entry maps `slot` (the slot index in the hull's JSON `shipModuleSlots`) to `index` (the array index inside the controller's `dorsalHullWeaponControllers` / `ventralHullWeaponControllers` / `noseWeaponControllers`).

Slot maps are per controller class. Multiple dataNames sharing one controller (e.g. Dreadnought + HeavyCruiser both using `DreadnoughtController`) all inherit the same map; per-dataName JSON `shipModuleSlots` then picks which subset of slots is active.

### 5.2 — That's it

No bundle, no Unity, no DLL rebuild. Drop the file in your mod folder, restart the game.

For per-mount overrides (route the same slot to different array indices depending on which `Mount` the player equips), see §10.

---

## 6 — Use case C: Hybrid hull (custom mesh, vanilla drives)

The most common custom-hull case. Custom geometry, vanilla drive variants for free.

### 6.1 — Create the hull

1. In Unity Project panel, select a vanilla prefab as your starting skeleton (e.g. `Assets/GameObject/Battlecruiser.prefab`).
2. **Ship Tools → Hull → 1. Create Hull From Vanilla**.
3. Fill the dialog:
   - **Hull Name** — the new prefab's name (e.g. `MyScoutCruiser`). Becomes both the asset filename and (lowercased) the bundle tag.
   - **Drive Mesh Prefix** — which vanilla hull's drive meshes to use. Auto-mirrors **Hull Name** until you edit it. Override when your prefab name differs from the vanilla source you want drives from.
   - **Faction Skin** — which faction's bundle-baked materials to apply (default `resist`).
4. Click Create.

What the tool does:
- Instantiates and unpacks the source prefab.
- Renames it to your **Hull Name**.
- Renames the body mesh parent (`Earth_Hull_*`) to `Hull`.
- Saves as `Assets/<HullName>.prefab`, tagged for bundle `<hullname>` (lowercase).
- Creates a sibling `Assets/<HullName>_Drive.prefab` (used in FullCustom mode; harmless to leave for Hybrid).
- Writes `<HullName>.faction` and `<HullName>.driveprefix` sidecars.
- Bakes faction materials onto every `MeshRenderer` whose name has a matching `Assets/Material/MAT_<childName>_<faction>.mat`.

### 6.2 — Naming rules

**Do not rename:**
- Root (the name you entered)
- `Hull`
- Hull mesh children inside `Hull` (`Earth_<hullname>_Crew`, etc. — material paths depend on these names)
- `Drive`, `_ExplosionSequenceRoot`, `SelectionReticle`, `GroupSelectionReticle`, `Padlock Container`
- All 17 radiator names (§12)

**Rename freely:**
- Weapon mounts (matched by case-insensitive `Contains("dorsal" / "ventral" / "nose")`)

### 6.3 — Add weapon mounts (if needed)

1. Select existing mount(s) in Hierarchy (Ctrl+click for multiple).
2. **Ship Tools → Hull → 2. Add Weapon Mount**.
3. New mount is automatically:
   - Named `<base> N+1` (e.g. `Dorsal 3` → `Dorsal 4`).
   - Inserted at sibling-index + 1.
   - Registered in the matching ship-controller array at the next free index.
4. Press W and reposition each new mount in scene view.
5. Drag in Hierarchy to set firing order (controller-array index follows hierarchy order).

**Mount structure** — exactly three levels:
```
<Mount Name>           ← has ShipWeaponVisController component
  └── <Mount Name> Gun ← visual model (mesh + renderer)
       └── FirePoint    ← projectile spawn point
```

**Pairing rule:** dorsal count must equal ventral count. Each pair at the same array index forms one logical hardpoint.

After adding mounts, declare slot mappings in `ControllerDefinitions.cfg` (§10) so JSON slot indices route to your new mount-array indices.

### 6.4 — Change faction skin later (optional)

**Ship Tools → Hull → 3. Set Faction Skin** with the prefab selected. Updates the `.faction` sidecar and re-bakes materials immediately.

### 6.5 — Verify

**Ship Tools → Verify Hull** with the hull root selected. Reports errors, warnings, and current weapon-mount array indices (you'll need those for `ControllerDefinitions.cfg`). Fix every error before proceeding. Warnings are advisory.

### 6.6 — Finalize and ship

**Ship Tools → Finalize and Ship**. Single click, six stages:

1. Applies bundle-safe graphics settings.
2. Saves pending in-memory asset changes.
3. Re-bakes faction skin on every tagged hull. Auto-prompts via 3-way dialog (use default / skip / pick another) if any prefab is missing a `.faction` sidecar.
4. Bakes drive variants on every tagged hull. Auto-prompts via Apply / Skip-this-hull / Cancel-pipeline if any prefab is missing a `.driveprefix` sidecar.
5. Builds AssetBundles to `BundleOutPath`.
6. Copies bundles + manifests to `DeployPath`.

A single Done dialog summarizes per-stage counts. If you cancel during stage 4, no build/deploy happens and you get a summary of what completed.

### 6.7 — Configure JSON + cfg

In your mod folder:

**`TIShipHullTemplate.json`** — point `modelResource` at the bundle:
```json
{
  "dataName": "MyScoutCruiser",
  "modelResource": ["myscoutcruiser/MyScoutCruiser", "myscoutcruiser/MyScoutCruiser"]
}
```

**`HullDefinitions.cfg`** — register as Hybrid:
```json
[
  {
    "dataName": "MyScoutCruiser",
    "bundleName": "myscoutcruiser"
  }
]
```

If your dataName doesn't match a vanilla hull, add `vanillaDriveDataName` so drive paths resolve:
```json
{
  "dataName": "MyScoutCruiser",
  "bundleName": "myscoutcruiser",
  "vanillaDriveDataName": "Battlecruiser"
}
```

**`ControllerDefinitions.cfg`** — slot mappings if you added mounts (§10).

---

## 7 — Use case D: FullCustom hull (custom mesh + custom drive)

Same Unity pipeline as §6, with one cfg difference — declare a `drivePrefab`:

```json
[
  {
    "dataName": "MyDreadnought",
    "bundleName": "mydreadnought",
    "drivePrefab": "MyDreadnought_Drive"
  }
]
```

What changes vs Hybrid:
- `DriveVisualPatch` replaces the entire vanilla drive path with `<bundleName>/<drivePrefab>`. Your bundle owns the drive geometry.
- Drive material rendering across bundles is fragile: BK bundles built with Standard stripped from Always Included Shaders carry their own shader copies, while vanilla materials reference the global shader pool. Drives may render flat white if shader instances don't match.

> Verify drives render correctly in-game before committing. If they appear flat white, drop the `drivePrefab` field — that switches you to Hybrid mode using vanilla drive variants.

The `<HullName>_Drive.prefab` that **Create Hull From Vanilla** generates is the starting point for your custom drive. Edit it like any other prefab; it's already tagged for the same bundle.

---

## 8 — `HullDefinitions.cfg` schema

JSON array of objects. Each object describes one hull dataName.

| Field | Type | Required | Effect |
|---|---|---|---|
| `dataName` | string | yes | The hull's `dataName` (must match `TIShipHullTemplate.json`) |
| `bundleName` | string | optional | BK bundle for the hull mesh. Sets Hybrid (alone) or FullCustom (with `drivePrefab`). |
| `drivePrefab` | string | optional | BK drive prefab name inside the bundle. Requires `bundleName`. Sets FullCustom. |
| `vanillaDriveDataName` | string | optional | Alias the drive path to another vanilla hull's drive assets. Useful when `dataName` doesn't match a vanilla hull. Ignored in FullCustom. |

### Mode matrix

| `bundleName` | `drivePrefab` | `vanillaDriveDataName` | Mode | Status |
|---|---|---|---|---|
| absent | absent | absent | — | **Rejected** (entry would do nothing) |
| absent | absent | set | PatchOnly | ✓ |
| set | absent | optional | Hybrid | ✓ |
| set | set | (ignored) | FullCustom | ⚠ verify drive material rendering |
| absent | set | any | — | **Rejected** (drive must live in a bundle) |

### Example — three hulls, three modes

```json
[
  {
    "dataName": "HeavyCruiser",
    "vanillaDriveDataName": "Dreadnought"
  },
  {
    "dataName": "Battlecruiser",
    "bundleName": "scoutcruiser"
  },
  {
    "dataName": "MyDreadnought",
    "bundleName": "mydreadnought",
    "drivePrefab": "MyDreadnought_Drive"
  }
]
```

---

## 9 — `TIShipHullTemplate.json` — `modelResource` conventions

`modelResource` is the vanilla TI field BK reads to locate the hull prefab. The path format is `<bundle>/<prefab>` and BK respects whatever you put in it.

| Mode | `modelResource` |
|---|---|
| Vanilla-asset alias (use case A) | `["ships/<vanillaHull>", "ships/<vanillaHull>"]` |
| Hybrid / FullCustom (use cases C and D) | `["<yourbundle>/<YourHull>", "<yourbundle>/<YourHull>"]` |

Two entries in the array are appearance index 0 and 1 (default and ALT skins). Provide both even if identical — vanilla TI sometimes queries index 1.

---

## 10 — `ControllerDefinitions.cfg` schema

JSON array. Each object describes slot remapping for one `ShipModelController` subclass. Used standalone (use case B) or as a configuration step within use cases C / D.

Slot maps are per controller class. Multiple dataNames sharing one controller all inherit the same slot map. Per-dataName JSON `shipModuleSlots` then picks which subset of slots is actually active.

### Schema

| Field | Type | Required | Effect |
|---|---|---|---|
| `controllerClass` | string | yes | C# class name (without namespace) of a `ShipModelController` subclass |
| `weaponSlotMap` | array | yes (non-empty) | List of slot mappings |

Each `weaponSlotMap` entry:

| Field | Type | Required | Effect |
|---|---|---|---|
| `slot` | int | yes | JSON slot index from `shipModuleSlots` |
| `index` | int | yes | Default array index in the matching weapon-controllers array |
| `overrides` | array | optional | Per-mount overrides — see below |

Each override:

| Field | Type | Required | Effect |
|---|---|---|---|
| `mounts` | string array | yes | One or more `Mount` enum names (`Nose`, `Dorsal`, `Ventral`, …) |
| `index` | int | yes | Array index to use when one of these mounts is equipped |

### Resolution at runtime

1. Find the matching `slot` in `weaponSlotMap`.
2. If `overrides` is present and any override's `mounts` list contains the runtime `mount`, return that override's `index`.
3. Otherwise return the slot's default `index`.
4. If no slot matches, fall through to vanilla `SlotToWeaponMountIndex`.

### Example with per-mount override

```json
[
  {
    "controllerClass": "DreadnoughtController",
    "weaponSlotMap": [
      { "slot": 0, "index": 0 },
      { "slot": 1, "index": 1 },
      {
        "slot": 5,
        "index": 0,
        "overrides": [
          { "mounts": ["Nose"],   "index": 4 },
          { "mounts": ["Dorsal"], "index": 2 }
        ]
      }
    ]
  }
]
```

---

## 11 — Sidecar files

Sidecar files persist per-prefab editor state next to the `.prefab` asset. They are **editor-only** — never shipped in a bundle.

### `<HullName>.faction`

Plain-text file, contents = one of `appease`, `cooperate`, `destroy`, `escape`, `exploit`, `resist`, `submit`. Read by `Finalize and Ship`'s faction re-bake stage and `Hull / 3. Set Faction Skin`. Written by `Create Hull From Vanilla`, `Set Faction Skin`, and the build's auto-prompt path.

### `<HullName>.driveprefix`

Plain-text file, contents = vanilla hull name whose drive meshes to use (e.g. `Titan`). Read at variant-bake time to look up `Earth_<prefix>_<variant>` meshes. Written by `Create Hull From Vanilla` (auto-mirrors Hull Name unless overridden) or the in-flow prompt during `Finalize and Ship`.

### Recovery — missing sidecar during Finalize and Ship

Both stages handle missing sidecars in-flow rather than hard-stopping:

- **Missing `.faction`** → 3-way dialog: use default (`resist`) / Skip this hull / Pick another. Picking writes the sidecar and continues.
- **Missing `.driveprefix`** → text-input window defaulted to the prefab's basename. Apply / Skip this hull / Cancel pipeline. Apply writes the sidecar and continues.

Skipping a hull leaves it un-baked but doesn't break the pipeline. Cancelling halts the pipeline cleanly with a per-stage summary.

### Manual recovery

Create the sidecar by hand if you'd rather not use the prompt. Plain text — literally the file `MyHull.driveprefix` next to `MyHull.prefab`, contents `Battlecruiser`.

---

## 12 — Required hull hierarchy

### Direct children of root (all required)

| Name | Layer | Purpose |
|---|---|---|
| `Hull` | 0 | Container for hull-mesh children (each on Layer 17) |
| `Drive` | 17 | Drive container; must contain `ThrusterPoint` children |
| `_ExplosionSequenceRoot` | 2 | Destruction VFX |
| `SelectionReticle` | 0 | Single-select indicator |
| `GroupSelectionReticle` | 0 | Multi-select indicator |
| `Padlock Container` | 0 | Camera lock target |

### Radiators (17 total — names are exact)

**Fin (10):** `Radiator12`, `Radiator3`, `Radiator130`, `Radiator6`, `Radiator4`, `Radiator430`, `Radiator730`, `Radiator1030`, `Radiator8`, `Radiator9`

**Spike (4):** `spikes 12`, `spikes 3`, `spikes 6`, `spikes 9`

**Droplet (3):** `Droplet12`, `Droplet4`, `Droplet8`

`Verify Hull` flags any missing radiator as an error. If your hull doesn't visually use a radiator, keep the GameObject (empty) so the controller's array integrity stays valid.

### Weapon mounts

- Name must contain `dorsal`, `ventral`, or `nose` (case-insensitive).
- Three-level structure: Mount → Mount Gun → FirePoint.
- Mount component required: `ShipWeaponVisController`.
- Dorsal count must equal Ventral count.

### Root component requirements

- `CapsuleCollider`
- `DamageLayer`
- A ship-controller component (e.g. `BattlecruiserController`, `DreadnoughtController`)
- Root layer = 2 (Ignore Raycast)
- All hull-mesh children inside `Hull` must be on Layer 17 (HurtBox) for damage detection

---

## 13 — In-game verification

After deploy, launch the game and watch `Player.log`:

```fish
cp "…/compatdata/1176470/pfx/drive_c/users/steamuser/AppData/LocalLow/Pavonis Interactive/TerraInvicta/Player.log" \
   ~/Desktop/Player.log
```

### 13.1 — Expected boot lines

```
[ExpandedFleetsAndNavies] HullRegistry: parsed <dataName> from <mod> ...
[ExpandedFleetsAndNavies] HullRegistry: scanned <N> file(s), <N> hull(s) registered.
[ExpandedFleetsAndNavies] ControllerRegistry: parsed <ControllerClass> from <mod> (<N> mappings).
[ExpandedFleetsAndNavies] ControllerRegistry: scanned <N> file(s), <N> controller(s) registered.
[ExpandedFleetsAndNavies] ControllerRegistry: patched <N> controller(s) for weapon slot remapping.
```

No "Load failed". No `HarmonyException`. No "Not loaded" line.

### 13.2 — Per-hull runtime lines

When a Hybrid or FullCustom hull spawns:
```
[ExpandedFleetsAndNavies] SetSkinSkipPatch: <dataName> (Hybrid) — skipping vanilla SetSkin.
[ExpandedFleetsAndNavies] DriveVariantPatch: activated 'DeLavalx5' on <HullName>
```

When a vanilla-asset-alias hull spawns:
```
[ExpandedFleetsAndNavies] DriveVisualPatch (patchonly): ships/Earth_HeavyCruiser_DeLavalx5 -> ships/Earth_Dreadnought_DeLavalx5
```

When a FullCustom hull spawns:
```
[ExpandedFleetsAndNavies] DriveVisualPatch (full-custom): ships/Earth_<dataName>_DeLavalx5 -> <bundle>/<drivePrefab>
```

If `DriveVariantPatch` warns `no baked variant 'DeLavalx<N>' on <HullName>`, that combo wasn't baked because either the mesh or the material was missing in the project. Add the missing asset and re-run **Finalize and Ship**.

### 13.3 — In-game checklist

- Ship is textured (not white).
- Scale matches surrounding ships.
- Selection reticle shows on click.
- Each weapon slot fires from the correct turret.
- Radiators extend on overheat, retract on cool-down.
- Drive VFX plays at the nozzles.
- Projectiles register hits (damage flashes).
- Destruction sequence plays on kill.

---

## 14 — Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `Load failed: HarmonyException` at boot | A patch's target method changed in a TI update | Rebuild BK against current `Assembly-CSharp.dll` |
| Pink hull in Unity | Incomplete extraction | Re-extract full `TerraInvicta_Data` |
| "Enter Safe Mode?" on Unity open | Decompiler artifacts remain | Run `fix_decompiler_errors.py`; reopen |
| Wrong scale in-game | Wrong Unity version | Use 2020.3.49f1 exactly |
| Pure white ship in combat | Bundle baked from prefab with null-material renderers | `Verify Hull` in Unity; clean null entries from controller weapon arrays; **Finalize and Ship** |
| NullRef on ship spawn | Missing required root child | `Verify Hull`; add the missing GameObject (empty if unused) |
| Weapons fire from wrong position | `weaponSlotMap` indices don't match controller-array indices | Run `Verify Hull` to print current array indices; reconcile with `ControllerDefinitions.cfg` |
| Projectiles pass through ship | Hull-mesh children not on Layer 17 | Set Layer 17 on all `MeshRenderer` children inside `Hull` |
| White drive in combat (Hybrid) | Variant missing for that drive type/count | Confirm `Earth_<prefix>_<variant>` mesh and `MAT_<prefix>x<n>_<faction>.mat` exist; re-run **Finalize and Ship** |
| White drive in combat (FullCustom) | Cross-bundle shader-instance mismatch | Drop `drivePrefab` from cfg entry to fall back to Hybrid |
| Bundle 500MB+ or build takes 40+ min | Vanilla bundle tags not stripped | **Setup → 1. Strip Vanilla Bundle Tags**; rebuild |
| Bundle loads but ship invisible | Bundle deployed without manifest | Confirm both `<bundle>` and `<bundle>.manifest` are in the mod folder |
| Materials missing | `Assets/Material/MAT_<childName>_<faction>.mat` not in project | Re-extract; run `Hull / 3. Set Faction Skin` to confirm bake count |
| `HullRegistry` rejects entry: "no bundleName, drivePrefab, or vanillaDriveDataName" | Entry declares a `dataName` but no behavior | Add at least one of those three fields, or delete the entry |

---

## 15 — Mod folder layout

A complete BK-extending mod folder contains:

```
Mods/Enabled/MyShipMod/
├── ModInfo.json                    ← UMM manifest
├── HullDefinitions.cfg             ← optional, hull dataName declarations
├── ControllerDefinitions.cfg       ← optional, slot mappings
├── TIShipHullTemplate.json         ← optional, hull definitions (vanilla schema)
├── <bundlename>                    ← optional, AssetBundle (no extension)
├── <bundlename>.manifest           ← required if bundle present
└── (any other vanilla TI JSON file you're overriding/extending)
```

For your mod to extend BK without modifying it:
- Use a different folder name from `ExpandedFleetsAndNavies`.
- BK scans every sibling folder under `Mods/Enabled/` for `HullDefinitions.cfg` and `ControllerDefinitions.cfg`.
- Don't ship a copy of `BetterKinetics.dll` in your folder; UMM loads BK from its own folder.

---

## 16 — Quick reference card

```
Use case A — Vanilla-asset alias       │ HullDefinitions.cfg (vanillaDriveDataName)
                                       │ + TIShipHullTemplate.json
Use case B — Slot remap only           │ ControllerDefinitions.cfg
Use case C — Hybrid hull               │ Unity pipeline + bundle
                                       │ + HullDefinitions.cfg (bundleName)
                                       │ + TIShipHullTemplate.json
                                       │ + optional ControllerDefinitions.cfg
Use case D — FullCustom hull           │ Unity pipeline + bundle
                                       │ + HullDefinitions.cfg (bundleName + drivePrefab)
                                       │ + TIShipHullTemplate.json

New hull, in order                     │ Hull/1 → Hull/2 (×N) → Verify Hull → Finalize and Ship
Workstation prep, in order             │ Setup/1 → Setup/2 → Setup/3
```
