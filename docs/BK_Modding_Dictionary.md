# BetterKinetics - Modding Dictionary & Reference

**Player-facing name:** Expanded Fleets and Navies · **Internal/file name:** BetterKinetics

A reference for forking BK development. Game internals reference for Terra Invicta v1.0.32 (Unity 2020.3.49f1) plus BK-specific schemas, conventions, and tooling.

---

## 1. Game Types

All require `using PavonisInteractive.TerraInvicta;`

### Game-State Type Architecture (TIGameState pattern)

`TIGameState` is the abstract root of TI's entire runtime data model. Every gameplay object that needs to be referenced, saved/loaded, or queried derives from it (directly or via subclasses). Understanding this pattern unlocks correct patching for many systems.

**Architecture characteristics**:

- **Abstract base** with `[fsObject(Converter = typeof(TIGameStateConverter))]` - every persistent runtime object goes through this serializer.
- **Identity by ID**: equality, hashing, and ordering are all delegated to `GameStateID`. Two TIGameState references with the same ID are equal regardless of object reference identity. This matters when comparing `someObj == otherObj` after save load - the references differ but IDs match.
- **`archived` soft-delete**: objects are not deleted from memory; they're marked `archived = true` via `ArchiveState()`. Many TI systems filter out archived objects in their queries (e.g., `from x in habs where !x.archived select x`). When patching, check this flag.
- **Self-typing via `isXxxState` virtual booleans**: 29 virtual properties default to `false`. Each concrete subclass overrides exactly one to `true`. Code that takes a `TIGameState` parameter often dispatches via `if (location.isHabState) { ... } else if (location.isOrbitState) { ... }` rather than C# `is` checks. **Patches that match the same dispatch pattern integrate cleanly**.
- **Reference accessors via `ref_xxxState` virtuals**: 29 virtual properties default to `null`. Each concrete subclass overrides relevant ones to return references to its parents/children/related objects. For example, `TIHabModuleState` overrides `ref_hab` to return its containing hab. This lets generic code traverse relationships without knowing concrete types: `someState.ref_hab?.faction` works whether `someState` is a hab, hab module, fleet docked at hab, etc.
- **8 lifecycle init virtuals** for staged construction: `PostGameStateCreateInit_OnCreationOnly_1` through `PostEverythingSaveRepair_8`. Subclasses override the relevant stages. The numbered suffix indicates init order; later stages can rely on earlier stages having completed.
- **Reflection-based template lookup**: `GetMyTemplate()` uses `Type.GetProperty("template", ...)` on the derived type. Concrete state classes (TIHabModuleState, TIShipState, etc.) declare a typed `template` property (e.g., `TIHabModuleTemplate`) that GetMyTemplate finds via reflection. The base class doesn't constrain the template type - each subclass picks its own.

**Direct subclasses**:

- `TIHabModuleState` - hab module (this is what BK MiningPatch iterates), inherits TIGameState directly
- `TISectorState` - a sector within a hab, inherits TIGameState directly (despite being part of the space-asset chain conceptually, it doesn't go through TISpaceGameState)
- `TISpaceGameState` (abstract subclass, just 2 members: barycenter + isSpaceGameState)
  - `TIHabSiteState` (concrete - surface base location; inherits TISpaceGameState directly, NOT through TISpaceObjectState; has parentBody, hab, latitude/longitude, mining productivity fields, nested Statistics class with SpaceResourceGrade enum)
  - `TISpaceObjectState` (abstract - adds visualizer interface, orbital mechanics fields, static distance/transfer math)
    - `TINaturalSpaceObjectState` (abstract - adds `habs`, `Colonized()`, `Populous()`, `maxHabTier`)
      - `TISpaceBodyState` (concrete - overrides `habs` to include surfaceBases; has habSites, naturalSatellites, lagrangePoints)
      - `TILagrangePointState` (concrete - secondary object reference, synthetic mass, Earth-LP-always-Colonized override)
    - `TISpaceAssetState` (abstract - adds `faction`, `orbitState`, abstract `IsAlien()`, abstract `CombatRange_km()`, implements ITransferTarget)
      - `TIHabState` - stations and outposts
      - `TISpaceFleetState` - fleets
      - `TISpaceShipState` - individual ships
  - `TIOrbitState` (concrete - implements ITransferTarget, has `assetsInOrbit`/`stationsInOrbit`/`fleetsInOrbit`/`isEarthLEO`/`interfaceOrbit`/`amat_ugpy`)
- `TIRegionState` - Earth surface region
- `TINationState` - Earth nation
- `TIFactionState` - player or AI faction
- `TICouncilorState` - councilor (game agent)
- `TIArmyState` - Earth army
- `TIControlPoint` (or `TIControlPointState`)
- `TIWarState` - wars between factions
- `TIOrgState` - organizations
- `TIOfficerState` - ship/army officers
- `TIRegionAlienEntityState`, `TIRegionSpaceFacilityState`, `TIRegionAlienAssetState`, `TIRegionUFOCrashdownState`, `TIRegionXenoformingState`, `TIRegionUFOLandingState` (variant), `TIRegionAlienFacilityState`, `TIRegionAlienActivityState` - alien-related Earth-region state types

**Practical patterns for modders**:

1. **Type discrimination in Postfix patches**: When a method takes `TIGameState` parameters, `location.isHabState` is the cleanest dispatch - no reflection, no boxing, no LINQ. Concrete patterns are visible in BK's BuildLimitPatch (uses `is TISpaceGameState` cast) and could equally use `location.isSpaceGameState` flag.
2. **Reference traversal**: `state.ref_faction` works on every state type that has a meaningful faction owner. Don't instantiate type checks for every relationship - use the ref_* getters.
3. **ID equality**: when comparing if two state references are "the same object" across save/load boundaries, use `Equals()` (delegates to ID) not `==` reference equality. Within a single session, `==` works (returns true via `Equals`).
4. **Archived filtering**: many TI internal queries filter `where !x.archived` - but NOT all. `TINaturalSpaceObjectState.habs` (= `stationsInOrbit`) does NOT filter archived habs (only `fleetsInOrbit` does). BK BuildLimitPatch iterates `body.habs` without filtering archived; per-body archived hab counts are presumably zero-or-rare in normal gameplay (decommissioning fully removes the hab from the orbit), but worth verifying. **When writing new patches, do not assume any TI collection filters archived - check the getter source.**

### TIUtilities - Static utility class for state operations

`TIUtilities` is a `public static class` in `PavonisInteractive.TerraInvicta` with **117 public static methods** (TIUtilities.cs, 2,485 lines). It's the canonical place for cross-cutting helpers operating on TIGameState references. **5 methods are extension methods** (using `this TIGameState` parameter) - meaning they're callable as `someState.MethodName()` syntax even though defined elsewhere:

**Extension methods on TIGameState**:
- `IsIrradiated()` (TIUtilities.cs:340) - see "BK's Hab Patches" subsection in §1 for the NotInIrradiated note
- `GetDebugString(bool embedLinks = false)` (TIUtilities.cs:129) - debug string for any state
- `GetLocationDebugString(bool embedLinks = false)` (TIUtilities.cs:151) - location-focused debug string
- Also extension on Trajectory: `GetTrajectoryDebugString` (TIUtilities.cs:167)
- Also extension on `IEnumerable<T>`: `Median<T>(Func<T,float>)` (TIUtilities.cs:714)

**Location-resolver methods (NOT extensions; take TIGameState as parameter)** - useful for any patch that needs to walk from one location concept to another:
- `ObjectToSupraLocation(TIGameState)` - returns "above" location: ref_orbit → ref_fleet → ref_spaceBody (for navigation/travel destinations)
- `ObjectToExactLocation(TIGameState)` - returns most-specific location: ref_region → ref_ship → ref_fleet[0] → ref_hab → ref_councilor.location → ref_habSite → org's home/councilor location (for UI focus/selection)
- `ObjectToScannableLocation(TIGameState)` - returns location appropriate for surveillance scanning
- `GameStateHasLatLong(TIGameState)` - true if state has surface coordinates (region or habSite)
- `GameStateLatLong(TIGameState)` - Vector2(latitude, longitude); checks ref_region → ref_hab.habSite (for IsBase) → ref_habSite

**Irradiation helpers**:
- `IsIrradiated(this TIGameState)` - see above
- `IrradiatedMultiplier(TIGameState)` - float; checks isXxxState flags first (faster), then ref_xxx fallbacks; defaults to `1f`. Used in build cost calculations.

**Camera/UI helpers** (multiple `GotoGameState` overloads): for TIFactionState, TIGlobalResearchState, TIGlobalValuesState, TIMissionPhaseState, TICouncilorState, CouncilorView, generic TIGameState. Plus `LookAtGameState`, `GotoSelectedStateUI`, `TriggerSelectionEvent`.

**Number/string formatting** (extensive): `FormatBigOrSmallNumber`, `FormatBigNumber`, `FormatSmallNumber`, `FormatSmallNumber_prefix`, `LocalizeGW`, `DecimalPlaces`, `DecimalPlaces_P` (percentage form), `ForceValueSign` (for +N/-N display), `RemoveWorkshopTags`, `StripDiacriticsFromString`, `StripInvalidPathCharsFromString`, `CombineStrings`, `ConstructTextList` (3 overloads for game states/templates/strings).

**Color/tag string helpers**: `RedLine`, `GreenLine`, `CyanLine`, `HeaderCyanLine`, `BlueLine`, `BlackLine`, `PurpleLine`, `YellowLine`, `GoldLine`, `GrayLine`, `HighlightLine`, `FactionLine(str, faction)`, `GetColorString(Color)`. UI color constants: `UITextColor`, `UITextColorTransluscent`, `UIRedTextColor`, `UIHighlightColor`, `UIDisabled`, `UIColorIndicatorNeutral/Positive/Negative`, `UIColorIndicatorPipUnfilled`, `UIColorIndicatorTimePipEmpty`.

**State display helpers**: `GetStateDisplayName`, `GetLocationString`, `GetStateIcon` (returns Sprite), `GetStateIconPath` (returns string), `PathResourceIcon`, `InlineResourceStr`, `PathAttributeIcon`, `InlineAttributeStr`, `InlineKeyboardModifierStr`, `InlineMouseClickStr`.

**Save file helpers**: `GetSaveFileExtension`, `GetSaveFilePath(filename)`, `GetMostRecentSave`.

**Platform/system helpers**: `IsMainThread`, `SetMainThread`, `IsLinux`, `IsSteamDeck`, `HasRadeonGPU`, `GetScreenRatio`, `GetAspectRatio`, `CanIncreaseUIScale`, `GetMouseHeightRelativeToRectTransformBounds`.

**Unity helpers**: `CopyComponent(Component, GameObject)`, `UpdateButtonSpritesPlusMinus(Button, plus, gold)`, `UpdateButtonSpritesPlusMinusAlt`, `TryPrepareVideo`, `TryPlayVideo`.

**Combat math helpers**: `GetAccelerationConstraintsForGroup(List<CombatShipController>, conserveDV)`, `WillHitSphere(myPos, myVel, projPos, projVel_u, diameter_m)`, `MovingTowardsTarget`, `SimpleConeCastAll`.

**Type conversion helpers**: `GetBoolValue(string)`, `GetFloatValue(string)`, `GetDoubleValue(string)`, `GetIntValue(string)`, `GetTemplateValue<T>(string)` where T : TIDataTemplate.

**Static fields on TIUtilities**: `IsInCombatMode` (bool, get-only), `assetLoader` (AssetLoader), `camera` (CameraManager from `World.Active.GetExistingManager<CameraManager>()`).

**Practical implications for BK**:
- **`IrradiatedMultiplier(location)`** could replace `TIHabState.GetIrradiatedMultiplier(location)` (the static helper BK might use for irradiation-aware computations) - the TIUtilities version is essentially a more defensive version that handles all 3 location types plus null fallbacks.
- **`CopyComponent(Component, GameObject)`** is useful for prefab editing patches (BK's hull rebuild could use this).
- **`PromptPlayerForBugReport(message, recommendReload = true)`** is what TI uses for graceful crash handling. BK's catch-blocks could call this for serious failures instead of just logging.
- **`IsLinux()` / `IsSteamDeck()`** could conditionally enable/disable BK features that have platform issues.

### Hab Module System

| Type | Role | Notes |
|---|---|---|
| `TIHabModuleTemplate` | Static module definition (read-only at runtime) | NO `[JsonExtensionData]` attribute - custom JSON fields silently dropped at deserialization. Inherits from `TIDataTemplate`. Key fields: `coreModule` (bool), `habType` (HabType enum: Any/Base/Station), `tier` (int 1-3), `requiredProjectName` (string, parallel to ship side), `specialRules` (List<HabModuleSpecialRule>), `specialRulesValue` (float), `weightedBuildMaterials` (ResourceCostBuilder). Computed properties: `slotsProvided` (only on coreModule: 4/12/20 for tier 1/2/3), `weaponMounts` (only on spaceCombatModule: 3/3/4 for tier 1/2/3, alien tier 3 = 3 not 4). Has `SharesUpgradePath()` method (used by BK BuildLimitPatch). `benefitsAndCostsDescription(TIFactionState faction, TIHabState hab, bool prospectiveForHab = false)` is the single builder behind all three module-info display surfaces (hab build tooltip, installed-module tooltip, research screen module preview); both `faction` and `hab` may be null (research screen passes null hab). New behaviors require DLL raw JSON parsing. |
| `TIHabModuleState` | Live module instance in a hab | Inherits from `TIGameState` (implements `CombatWeaponCarrierState`, `CombatTargetableState`). 4 distinct module-state booleans (TIHabModuleState.cs:163-198): `okay = !empty && !destroyed && !decommissioning`, `functional = completed && !destroyed && !decommissioning`, `active = functional && powered` (= `completed && !destroyed && !decommissioning && powered`), `powered` is a directly-serialized property set externally. Also: `completed = !empty && constructionCompleted`, `underConstruction = !empty && !constructionCompleted`, `empty = templateName == null || templateName == ""`. Has `moduleTemplate` (TIHabModuleTemplate, get/private set), `priorModuleTemplate` (TIHabModuleTemplate, computed via `TemplateManager.Find` with private cached `_priorModuleTemplate` field), `priorModuleTemplateName`/`priorModuleCompleted`/`priorModuleCompletionDate` (set when an upgrade starts - see TIHabModuleState.cs:537-541). Hab access via `hab` property (`= this.sector?.hab`). |
| `TIHabState` | Station or outpost | Inherits from `TISpaceAssetState` (implements `OfficerCarrierState`). `IsModuleAllowedForHab` is `public static`; signature: `(TIFactionState faction, TIGameState location, TIHabModuleTemplate moduleTemplate, IEnumerable<TIHabModuleTemplate> existingModules = null, bool skipOnePerHabUpgradeCheckForDowngrade = false)`. Shared by player UI AND AI - patching affects all factions. Has 5 distinct module-state filters (see below): `OkayModules()`, `FunctionalModules()`, `ActiveModules()`, `UnpoweredModules()`, `PresentModules()`. Faction reference accessed via `base.faction` (inherited from TISpaceAssetState) or `ref_faction` getter override. Key fields: `tier` (int, get/private set), `habType` (HabType: Any/Base/Station, get/private set), `coreModule` (TIHabModuleState), `sectors` (List<TISectorState>), `anyCoreCompleted`, `decommissioning`, `underBombardment`, `underAssault`, `IsBase`/`IsStation` (computed bools), **`irradiated` (computed: `IsStation ? orbitState.irradiated : habSite.irradiated`)**, **`irradiatedMultiplier` (computed: `TIHabState.GetIrradiatedMultiplier(this.location)` - static helper)**. `AllowedModules(faction)` requires `!decommissioning && !underBombardment && !underAssault`. Static `IsMineSlot(sector, slot, habType)` returns true only for `habType == Base && sector == 0 && slot == 1` - the canonical mine slot location. |
| `TIFactionState` | Faction global state | `.habs` = all faction habs galaxy-wide. Mining patches fire for every faction every tick. |
| `TIGameState` | **Abstract base class for ALL game-state objects** | Inherits from `TIDataClass`, implements `IEquatable<TIGameState>` and `IComparable<TIGameState>`. Decorated with `[fsObject(Converter = typeof(TIGameStateConverter))]` (FullSerializer custom converter for save/load). Has 2 stored fields: `archived` (bool, get/private set) and `ID` (GameStateID, get/set). Has 1 stored string `displayName`. Implements 29 virtual `isXxxState` properties (all default `false`, overridden by subclasses to return `true` for self-typing) and 29 virtual `ref_xxxState` properties (all default `null`, overridden by subclasses to return relevant references). Equality is by `ID`: `Equals(other) => this == other || ID.Equals(other.ID)`. Hash is `ID.GetHashCode()`. `CompareTo` delegates to `ID.CompareTo`. Has `ArchiveState(bool trigger = true)` and `DeArchiveState()` for soft-delete pattern (sets `archived` flag and fires `GameStateArchived` event). Has 8 lifecycle init virtuals: `PostGameStateCreateInit_OnCreationOnly_1`, `PostGlobalGameStateCreateInit_2`, `PostCanvasManagerCreateInit_3`, `PostInitializationInit_4`, `PostAllStartUpInit_5`, `PostVisualizerCreationInit_6`/`_7`, `PostEverythingSaveRepair_8`. Reflection-based template lookup via `GetMyTemplate()` (looks for `template` property on derived type). Cast to `TISpaceGameState` for space-located objects. **Code that takes any game-state object often type-discriminates via `isXxxState` flags rather than C# type checks** - useful pattern when patching. |
| `TISpaceGameState` | Abstract intermediate base for space-located game states (between TIGameState and concrete subclasses like TISpaceAssetState/TIOrbitState) | **Tiny class - only 2 members**: (1) `barycenter` (TINaturalSpaceObjectState, virtual get/set) - the parent body the space object orbits around; (2) `isSpaceGameState` override returning `true`. Does NOT define `ref_naturalSpaceObject` or `habs` directly - those are on subclasses. **`ref_naturalSpaceObject` is overridden separately by TIHabState, TIHabModuleState, TISpaceFleetState, TISpaceShipState** (each with its own traversal path to the body). The `habs` collection lives on `TINaturalSpaceObjectState`, not on TISpaceGameState. Code that needs habs at a body must traverse `someState.ref_naturalSpaceObject.habs`. BK's BuildLimitPatch uses `if (location is TISpaceGameState spaceLocation) { var body = spaceLocation.ref_naturalSpaceObject; ... }` - the `is` cast is to confirm the location is a space-located state at all (not e.g. a region/nation), then ref_naturalSpaceObject (inherited from TIGameState as virtual) returns the body if the concrete type overrides it. |
| `TISpaceObjectState` | Abstract: anything visualized in space - bodies, orbits, lagrange points, fleets, ships, habs | Inherits from `TISpaceGameState`, implements `IGameStateVisualizer`. Overrides `isSpaceObjectState` to `true` and `ref_spaceObject` to return `this`. **14 virtual orbital mechanics properties** (most subclasses delegate to template values): `semiMajorAxis_m`, `semiMajorAxis_km`, `semiMajorAxis_AU`, `ecc` (eccentricity), `inclination_Rad`, `longAscendingNode_Rad`, `argPeriapsis_Rad`, `meanAnomalyAtEpoch_Rad`, `meanLongitude_Rad`, `orbitalPeriod_s` (+_Hours/_Days/_Years), `mass_kg`, `meanRadius_m` (+_km), `epoch_JYears`, `SpatialRotation` (Quaterniond). Other key fields: `epoch_DateTime` (TIDateTime, get/protected set, [SerializeField]), `controller` (SpaceObjectController, [fsIgnore] - runtime visualizer reference), `objectType` (SpaceObjectType enum), `iconResource` (string), `modelResource` (string), `inEarthSystem` (bool, virtual). **Static math API**: `MaxDistanceBetweenTwoSpaceObjects_m`, `MinDistanceBetweenTwoSpaceObjects_m`, `AverageDistanceBetweenTwoSpaceObjects_m`, `ExactDistanceBetweenTwoSpaceObjects_m`, `FindCommonBarycenter`, `IsAroundBarycenter`, `genericSynodicPeriod_s`, `GetHohmannTimePenaltyFraction`, `GenericTransferTime_s`, `GenericTransferDeltaV_mps`, `ModifiedGenericTransferEV_kps`, `GenericTransferBoostFromEarthSurface`, `GenericTransferTime_d`, `GenericTransferTimeFromEarthsSurface_d`. **Constants**: `symbolResource = "ui/SpaceObjectSymbol"`, `GenericTransferEV_kps = 2.11f`. **Nested enum** `HabClassification { Any, Resupply, Shipyard }`. **`IsIrradiated()` is NOT defined here** - it must be on TIHabSiteState or as an extension method (called by TIHabModuleTemplate.AllowedLocation on ref_orbit/ref_habSite/ref_spaceBody/ref_lagrangePoint, but no archive file contains its definition). |
| `TINaturalSpaceObjectState` | Abstract: planets, moons, lagrange points, asteroids - bodies habs orbit/sit on | Inherits from `TISpaceObjectState`. Overrides `isNaturalSpaceObjectState` to `true` and `ref_naturalSpaceObject` to return `this` (the body IS the natural space object). Key fields: `maxHabTier` (int, get/protected set - used by `IsModuleAllowedForHab` tier check at TIHabState.cs:923), `population` (ulong, virtual), `orbits` (`List<TIOrbitState>`, public field), `naturalObjectTemplate` (TINaturalSpaceObjectTemplate, computed). `[fsIgnore]` properties (NOT serialized to save, recomputed): `sphereOfInfluence_m`, `localBarycenterGravity_kps2`, `hillRadius_m`. **`Colonized() => population >= TemplateManager.global.colonizedSpaceObjectValue`** and **`Populous() => population >= TemplateManager.global.populousSpaceObjectValue`** - thresholds are tunable via `TIGlobalValuesTemplate` JSON, NOT hardcoded (the often-cited "10K/50K" values are current TI vanilla data, not constants). Provides the default `habs` collection: `habs => stationsInOrbit` (virtual, default impl) where `stationsInOrbit => orbits.SelectMany(o => o.stationsInOrbit).ToList()` - **per-call allocation** (BK iterations should cache or be wary). **Subclasses can override `habs`** - see TISpaceBodyState. Constants: `maxMaxHabTier = 3`. |
| `TISpaceBodyState` | Concrete: planets, moons, the Sun (anything with surface) | Inherits from `TINaturalSpaceObjectState`. Overrides `isSpaceBodyState` to `true`. **Overrides `habs` to include surface bases**: `habs => base.stationsInOrbit.Union(this.surfaceBases).ToList()`. So when BK iterates `body.habs` and the body is a planet/moon, surface bases ARE included automatically - no bug. Also overrides `habsInSystem` to include satellites' habs recursively. Key fields: `habSites` (TIHabSiteState[], potential locations for surface bases), `naturalSatellites` (List<TISpaceBodyState>, e.g. moons), `lagrangePoints` (List<TILagrangePointState>), `currentTilt` (Quaterniond, `[fsIgnore]`), `solarMirrorBonus` (Dictionary<TIFactionState, int>). Computed: `atmosphere` (Atmosphere enum: Trace/Thin/Standard/Thick/Massive - read from template), `surfaceBases` (= `habSites.Where(hasPlannedOrOperatingBase).Select(hab).ToList()`), **`irradiated` (= `template.irradiated`)**, **`irradiatedMultiplier` (float, = `template.irradiatedMultiplier`)** - both used by `IsIrradiated()` and BuildMaterials cost calculations. The `atmosphere == Atmosphere.Massive` check is what `Requires_GasGiant_Orbit` AllowedLocation gate validates. |
| `TIOrbitState` | Concrete: an orbital position around a body | Inherits from `TISpaceGameState`, implements `ITransferTarget`. Overrides `isOrbitState` to `true` and `ref_orbit` to return `this`. **Public fields BK and AllowedLocation use**: `assetsInOrbit` (List<TISpaceAssetState>, all assets here), `interfaceOrbit` (bool, computed at init from template OR if any higher orbit is interface - see InitWithTemplate), `isEarthLEO` (bool, `[fsIgnore]`, recomputed from template), `alienTerritory` (bool, `[fsIgnore]`), `solarMultiplier` (float, `[fsIgnore]`), `amat_ugpy` (float, virtual property - antimatter ug per year), **`irradiated` (virtual bool, returns `template.irradiated`) - this is what `IsIrradiated()` extension method dispatches to for orbits**. Computed: `stationsInOrbit` (`List<TIHabState>`, **per-call allocation**: `assetsInOrbit.Where(x => x.isHabState).Select(x => x.ref_hab).ToList()`), `fleetsInOrbit`, `altitude_m`/`altitude_km` (`semiMajorAxis_m - barycenter.meanRadius_m`). |
| `TISpaceAssetState` | Abstract: faction-owned things in space (habs, fleets, ships) | Inherits from `TISpaceObjectState`, implements `ITransferTarget`. Overrides `isSpaceAssetState` to `true`. Defines core ownership and combat fields: `faction` (TIFactionState, get/protected set - **THIS is where TIHabState.faction is inherited from**), `orbitState` (TIOrbitState, get/protected set), and 2 ABSTRACT methods all subclasses must implement: `bool IsAlien()` and `float CombatRange_km()`. Override of `ref_faction` returns `this.faction`. Direct subclasses: TIHabState, TISpaceFleetState, TISpaceShipState. |
| `TISectorState` | A sector within a hab (containing a list of modules) | Inherits from `TIGameState`. Has `faction` (TIFactionState, get/private set - duplicates hab's faction for fast lookup). Overrides 8 ref_* properties: `ref_faction`, `ref_hab`, `ref_habSite`, `ref_orbit`, `ref_spaceBody`, `ref_spaceObject`, `ref_naturalSpaceObject`, `ref_spaceAsset` (all delegate via `this.hab` chain). **`ref_naturalSpaceObject` checks IsBase**: returns `hab.barycenter` for stations, `hab.ref_spaceBody` for bases - interesting asymmetry. Public fields: `hab` (TIHabState back-reference), `habModules` (List<TIHabModuleState> - what BK MiningPatch iterates), `slots` (int). Has its own `OkayModules`/`FunctionalModules`/`ActiveModules`/`UnpoweredModules`/`AllModules`/`CompletedModules`/`ActiveCombatModules` filter methods - **NOT cached** (each call allocates). **`UnpoweredModules() = OkayModules() where !powered`** (different from TIHabState's `UnpoweredModules() = FunctionalModules() where !powered`). Has `active` (bool), `coreSector` (bool), `numFunctionalModules` (int) computed properties. |
| `TIHabSiteState` | A surface location on a body where a base CAN be built (planned or operating) | Inherits from `TISpaceGameState` directly (NOT through TISpaceObjectState - surface sites aren't space objects). Overrides `isHabSiteState` to `true`, plus 7 ref_* properties: `ref_faction`, `ref_factions` (multi-faction support - habs can be shared, e.g. LEO), `ref_hab`, `ref_spaceBody`, `ref_habSite`, `ref_spaceObject`, `ref_naturalSpaceObject` (= `this.ref_spaceBody`). Key fields: `parentBody` (TISpaceBodyState - the body this site is on), `hab` (TIHabState - null until base founded), `landedFleets` (List<TISpaceFleetState>), `latitude`/`longitude` (float, default -1f), `pendingHab` (bool), `solarMultiplier` (float, [fsIgnore]), `miningProfile` (TIMiningProfileTemplate, [fsIgnore]). Resource production fields per type: `water_day`, `volatiles_day`, `metals_day`, `nobles_day`, `fissiles_day` (float). **Irradiation properties (delegate to parentBody)**: `irradiated` (bool, = `parentBody.irradiated`), `irradiatedValue` (float, = `parentBody.irradiatedMultiplier`). Computed: `hasPlannedOrOperatingBase` (= `hab != null && hab.PresentModules().Count > 0`). Methods: `MinDeltaVToLaunch_kps`, `DeltaVToLandFromInterface_kps`, `MarkPendingHab`, `FoundHab`, `GetDailyProduction`, `GetMonthlyProduction`, `GetHabSiteMin/Max/ExpectedProductivity_day/_month`, `LandFleet`, `LaunchFleet`, etc. **Nested static class `Statistics`** with `SpaceResourceGrade` enum (7 values: None, Awful, Poor, BelowAverage, AboveAverage, Good, Great). |
| `TILagrangePointState` | A Lagrange point (L1-L5) of a body - concrete type | Inherits from `TINaturalSpaceObjectState`. Overrides `isLagrangePointState` to `true`, plus `ref_lagrangePoint` and `ref_naturalSpaceObject` returning `this`. Also overrides `ref_spaceBody` to return `secondaryObject` (the body this lagrange point is associated with - e.g., Earth's L1 has secondaryObject = Earth). Overrides `objectType` to return `SpaceObjectType.LagrangePoint`. Overrides `mass_kg` to return `secondaryObject.mass_kg / 10000000.0` (synthetic - for orbital math purposes). Hides parent template via `new` keyword: `template => GetMyTemplate<TINavigableTemplate>()`. Key fields: `secondaryObject` (TISpaceBodyState, get/private set), `lagrangeValue` (LagrangeValue enum from template, computed). **Overrides `population`**: returns sum of `crew` across all non-alien stations in orbit (synthetic population from station crews). **Overrides `Colonized()` and `Populous()`** to return `true` if related sun-orbiting object is Earth, OR base check passes - meaning Earth's lagrange points always pass the colonized/populous gates regardless of actual population. Lifecycle: `PostGameStateCreateInit_OnCreationOnly_1` resolves secondaryObject from template; `PostGlobalGameStateCreateInit_2` computes orbital period and Hill radius (uses `Mathd.Max(sphereOfInfluence_m, hillRadius_m / 3.0)` for Hill radius). |

**Cross-impact: 5 module-state filters on TIHabState**

TI distinguishes 5 separate module states, each with its own filter method and caching behavior. All five boolean predicates are computed properties on `TIHabModuleState` (TIHabModuleState.cs:163-209):

| Method | Predicate Boolean | Predicate Formula | Caching | Use Case |
|---|---|---|---|---|
| `OkayModules()` | `okay` | `!empty && !destroyed && !decommissioning` | **Per-frame cached** | Build limits (F1/F2): includes structurally-present modules even if under construction |
| `FunctionalModules()` | `functional` | `completed && !destroyed && !decommissioning` | **Per-frame cached** | Modules that exist as completed structures regardless of power state |
| `ActiveModules()` | `active` | `functional && powered` (= `completed && !destroyed && !decommissioning && powered`) | **NOT cached** - allocates new List each call | Bonuses (F3/F4): unpowered = no bonus. **BK's MiningPatch deliberately avoids this** to prevent per-call List allocation, iterating `sectors → habModules` directly. |
| `UnpoweredModules()` | derived | `FunctionalModules()` where `!powered` | NOT cached (derives from FunctionalModules's cache) | Identifies functional-but-unpowered modules |
| `PresentModules()` | `present` | `!empty && !destroyed` (broader than okay - INCLUDES decommissioning) | NOT cached | Used by `TIHabSiteState.hasPlannedOrOperatingBase` to detect whether a base has ANY non-empty non-destroyed modules. Includes decommissioning modules (which `okay` excludes). |

Where the underlying booleans are:
- `empty = templateName == null || templateName == ""` (TIHabModuleState.cs:133-138)
- `present = !empty && !destroyed` (TIHabModuleState.cs:203-208 - broader than okay; INCLUDES decommissioning)
- `completed = !empty && constructionCompleted`
- `underConstruction = !empty && !constructionCompleted` (mutually exclusive with `completed`)
- `powered`, `destroyed`, `decommissioning`, `constructionCompleted` are all directly-serialized `bool` properties

**TIHabState filter scope (TIHabState.cs:317-323)**: All 4 TIHabState filter methods iterate `activeSectors`, NOT all `sectors`. `activeSectors` is a per-call computed property: `this.sectors.Where(x => x.active).ToList()`. Inactive sectors (those with `active == false`) and their modules are excluded from every TIHabState filter result. **BK's MiningPatch iterates `hab.sectors` directly, NOT `hab.activeSectors`** - this is a deliberate optimization (skips the per-call activeSectors allocation) but means BK includes modules in inactive sectors when computing mining bonus. In current TI gameplay this rarely matters (sectors typically stay active throughout a hab's lifetime), but the divergence is worth knowing if patching anything sector-aware.

**TISectorState ALSO has filter methods, with two important differences from TIHabState's**:
1. **None are cached** - TISectorState's `OkayModules`/`FunctionalModules`/`ActiveModules`/`AllModules`/`CompletedModules` all allocate fresh lists per call (TISectorState.cs:174-217). Only TIHabState's OkayModules and FunctionalModules have per-frame caching.
2. **Different `UnpoweredModules` semantic**:
   - `TIHabState.UnpoweredModules() = FunctionalModules() where !powered` - completed-but-unpowered modules (excludes under-construction)
   - `TISectorState.UnpoweredModules() = OkayModules() where !powered` - okay-but-unpowered (INCLUDES under-construction)
   
   This subtle asymmetry could matter for sector-level UI displays vs hab-level resource calculations. TISectorState.cs:204-209 vs TIHabState.cs:189-198.

TISectorState also defines `ActiveCombatModules()` (= `ActiveModules where x.moduleTemplate.spaceCombatModule`) for combat-eligible sector modules.

**Tier-aware counting**: BK's `BuildLimitPatch.EffectiveTier(mod)` returns `max(mod.tier, mod.priorModuleTemplate?.tier)` so in-progress upgrades count at their target (higher) tier. Note `priorModuleTemplate` is computed via `TemplateManager.Find<TIHabModuleTemplate>(priorModuleTemplateName)` - it's not stored as a direct reference and may incur a lookup. The fields `priorModuleTemplateName`, `priorModuleCompleted`, `priorModuleCompletionDate` are set when an upgrade begins (TIHabModuleState.cs:537-541). Combined with `SharesUpgradePath()` family detection: T1 limit counts all tiers in the upgrade chain, T2 counts T2+T3 only, T3 counts T3 only.

### BK's Hab Patches - Implementation Reference

BK extends hab module behavior via two Postfix patches that read custom JSON fields side-loaded by `ConfigReader`. **None of BK's hab logic touches vanilla `specialRules`** - BK uses its own custom field schema entirely.

#### Vanilla `IsModuleAllowedForHab` base logic (TIHabState.cs:866-924) - what BK narrows

The Postfix BK patches runs AFTER this static method has already returned a result. Vanilla checks (in order, AND'd together for a `true` result):

1. **Tier check (location)**: `moduleTemplate.tier <= location.ref_naturalSpaceObject.maxHabTier`
2. **Tier check (faction)**: For non-core modules, `tier <= num4` where `num4` is determined by:
   - If hab already exists: hab's `tier`
   - Else for Base type: `faction.MaxBaseTier`
   - Else for Station type: `faction.MaxStationTier`
3. **Core upgrade check**: For core modules, `tier > num` (must upgrade UP, not down at same tier)
4. **One-per-hab check**: If `moduleTemplate.onePerHab`, search existing modules for `SharesUpgradePath(moduleTemplate)`. If found and `tihabModuleTemplate2.tier >= moduleTemplate.tier`, returns false (unless `skipOnePerHabUpgradeCheckForDowngrade=true`)
5. **Hab type match**: `moduleTemplate.IsForHabType(habType)` - habType derived from `(location.ref_habSite != null) ? Base : Station`
6. **Automated match**: `moduleTemplate.automated == coreModule.automated` (the new module's automation flag must match the core's)
7. **Project gate**: `moduleTemplate.FactionCanBuild(faction)` - hab-side has different semantics from ship side: includes `noBuild` short-circuit, `EverAllowedForFaction` check (alien/human module-faction match enforced), per-frame caching, and treats null requiredProject as unlocked even for alien factions. See §2 requiredProjectName Semantics for the comparison table.
8. **Location gate**: `moduleTemplate.AllowedLocation(location, hab)` - checks habType vs location (Base requires habSite, Station requires orbit) plus these specialRules:
   - `EarthLEOOnly` → must be `habLocation.ref_orbit.isEarthLEO`
   - `Requires_Colonized_Body` → calls `ref_naturalSpaceObject.Colonized()` (`population >= TemplateManager.global.colonizedSpaceObjectValue`; vanilla value ≈ 10K)
   - `Requires_Inhabited_Body` → calls `ref_naturalSpaceObject.Populous()` (`population >= TemplateManager.global.populousSpaceObjectValue`; vanilla value ≈ 50K)
   - `NotInIrradiated` → checks `IsIrradiated()` on whichever of orbit/habSite/spaceBody/lagrangePoint is non-null. **`IsIrradiated()` is an extension method on TIGameState** (TIUtilities.cs:340-351), `public static bool IsIrradiated(this TIGameState gameState)`:
     ```csharp
     public static bool IsIrradiated(this TIGameState gameState)
     {
         if (gameState.ref_habSite != null) return gameState.ref_habSite.irradiated;
         if (gameState.ref_orbit != null)   return gameState.ref_orbit.irradiated;
         return gameState.isSpaceBodyState && gameState.ref_spaceBody.irradiated;
     }
     ```
     Note the dispatch order: **habSite → orbit → spaceBody** (NOT the orbit-first order shown in TIHabModuleTemplate.AllowedLocation's manual chain). For lagrange points, the method falls through to `ref_spaceBody` (TILagrangePointState.ref_spaceBody returns `secondaryObject` which IS a TISpaceBodyState). The underlying `irradiated` bool properties are at: TIOrbitState.cs:129, TIHabSiteState.cs:197, TISpaceBodyState.cs:168, TIHabState.cs:634. All ultimately delegate to template's `irradiated` flag.
   - Related: `TIUtilities.IrradiatedMultiplier(TIGameState)` (NOT an extension; takes parameter explicitly) returns float; tries `isXxxState` flags first, then ref_xxx fallbacks, defaults to `1f` if no location component matches. Used in `BuildMaterials` cost calculations.
   - `Requires_Interface_Orbit` → must have orbit AND `orbit.interfaceOrbit == true`
   - `Requires_GasGiant_Orbit` → must have orbit AND `ref_spaceBody.atmosphere == Atmosphere.Massive`
   - `HarvestAntimatter` → orbit must have `amat_ugpy > 0` AND no other station in orbit already has HarvestAntimatter (one per orbit)
   - `SolarMirror` → not at L2/L3 lagrange points (no sunlight); not at sun-barycenter L4/L5; if orbiting, body must have habSites

`IsModuleAllowedForThisHab(faction, moduleTemplate, downgrading)` is an instance method that delegates to the static version with `existingModules=null`.

`AllowedModules(faction)` returns full list - but only if `!decommissioning && !underBombardment && !underAssault`. Hab under attack or being torn down has empty allowed list.

#### BK's BuildLimitPatch (Postfix narrowing only)

**ConfigReader** (`ConfigReader.cs`, consumed by BuildLimitPatch, MiningPatch, HabDescriptionPatch): At mod load, walks up one level from BK's mod folder (`modEntry.Path`) to resolve `Mods/Enabled/`, then iterates **every sibling mod folder** looking for a `TIHabModuleTemplate.json` file. Parses each found file as a `JArray` via Newtonsoft, builds a `dataName → HabModuleConfig` dictionary keyed with `StringComparer.Ordinal` (locale-independent, faster). Custom fields read per entry:

| Field | Type | Purpose |
|---|---|---|
| `bodyBuildLimit` | int? | Max instances of this module family per body (any faction) |
| `factionBuildLimit` | int? | Max instances of this module family per body per faction |
| `miningBonus` | int? | % multiplier added to mining yield per active instance (cumulative) |
| `miningCapBonus` | int? | Adds to safe mine network size cap per active instance |

Fields are nullable (`int?`) - absent fields are not configured (silent, expected because most mods won't set most fields). Wrong type → warning logged and ignored. Negative value → warning logged and ignored. **Last mod wins on collision** for individual fields (standard load-order behavior); BK uses fetch-or-create so later mods can override individual fields without nuking prior fields on the same `dataName`. Multiple mods can therefore extend the same module's config additively as long as they don't conflict on individual fields.

**Why the side-loader pattern**: `TIHabModuleTemplate` lacks `[JsonExtensionData]`, so TI's JSON loader silently drops unknown custom fields at deserialization. BK side-loads its own fields outside TI's loader, keeping vanilla TI's deserialization unmodified.

**`TIHabModuleTemplate.MonthlyResourceIncome(FactionResource, TIGameState location = null, TIFactionState faction = null)`**: For Water, Volatiles, Metals, NobleMetals, and Fissiles, returns 0 when `location == null` (the mining component is site-dependent). Antimatter and Exotics have no such guard and return their flat income regardless. The research screen module preview calls the description builder with a null hab, so station flat material incomes are invisible there in vanilla; BK's `HabDescriptionPatch` splices them back in for Station modules.

**`HabitatsScreenController.UpdateModulePreviewText`**: Builds the Build Modules side panel as an icon grid from a local `List<IncomeEntry>` (private nested type) populated inline and pushed into `ResourceGridItemController` cells before the method returns. Not extensible by postfix; adding entries would require re-sizing and re-populating the grid or a transpiler. The list-item hover tooltip (`HabModuleListItem.SetTooltipText`) carries the full text detail for the same modules via `benefitsAndCostsDescription`, so the grid is left vanilla.

**BuildLimitPatch** (`BuildLimitPatch.cs`): Postfix on `TIHabState.IsModuleAllowedForHab`. Enforces `bodyBuildLimit`/`factionBuildLimit` with full tier cascade semantics:
- A T1 limit of N means "at most N modules in this family at EffectiveTier ≥ T1 on this body"
- A T2 limit caps total at T2+T3
- New builds check the full cascade (every defined tier limit)
- Upgrades only check tiers ≥ targetTier (lower-tier total caps don't re-fire on upgrades since count doesn't increase)
- The module being upgraded is excluded from faction counts (so 1-per-body faction caps don't block upgrades)
- Body counts include ALL family modules regardless of ownership; faction counts include only this faction's modules
- `EffectiveTier(mod)` returns max of current tier and prior tier - in-progress transitions count at higher tier until complete
- Only ever NARROWS the base game's result (sets `__result = false`); never permits a denied module
- Skips alien-faction habs

**MiningBonusPatch** (`MiningPatch.cs`): Postfix on `TIFactionState.GetCurrentMiningMultiplierFromOrgsAndEffects`. Iterates `faction.habs` → `hab.sectors` → `sector.habModules`, sums `miningBonus` (integer percentage points) from each ACTIVE module's config, divides by 100 to convert to multiplier delta, adds to `__result`. Uses direct iteration to avoid `hab.ActiveModules()`'s per-call List allocation. The `resource` parameter is accepted for Harmony binding but the bonus is currently applied uniformly to all resource types.

**MiningCapBonusPatch** (`MiningPatch.cs`): Postfix on `TIFactionState.SafeMineNextworkSize` getter (using `MethodType.Getter` to disambiguate property getter from setter). Same iteration pattern as MiningBonusPatch; sums `miningCapBonus` raw int (no /100 conversion - it's added directly to the network size cap). Uses `nameof(TIFactionState.SafeMineNextworkSize)` so the patch fails to compile (intentional early warning) if TI ever fixes the typo upstream.

### Ship System

| Type | Role | Notes |
|---|---|---|
| `TIShipHullTemplate` | Hull definition | `modelResource` is `string[]` not `List<string>` - use `.Length`. Both entries identical for single variant. Inherits from `TIShipModuleTemplate` → `TIShipPartTemplate` → `TIDataTemplate`. |
| `TIShipModuleTemplate` | Hull base class - adds `mass_tons` field, `buildMass_tons()`, `buildCost()` overrides | Tiny intermediate class. |
| `TIShipPartTemplate` | Grandparent class - defines `requiredProjectName`, `weightedBuildMaterials`, `crew`, `iconResource`, base `modelResource`/`combatUIpath` (string singletons; hull's `new` keyword shadows with arrays), `noCombatRepair`, `hp` | Contains `FactionCanBuild()` gate logic - see §2 requiredProjectName semantics. |
| `TISpaceShipTemplate` | Ship design preset (= design; there is no separate `TISpaceShipDesign` class - see §22.8) | Extends `TIDataTemplate`. **References templates by name string fields** (`hullName`, `driveName`, `powerPlantName`, `radiatorName`, `factionName`) resolved lazily via `TemplateManager.Find<T>`. Slot indices baked into saves via `moduleTemplateEntries`/`hullWeaponTemplateEntries`/`noseWeaponTemplateEntries`. **All template-resolution properties are NOT null-safe** - `hullTemplate` getter returns null if `hullName` doesn't match, then `.alien` access throws. Player-designed ships saved as TISpaceShipTemplate entries directly into save file (NOT into JSON). 4,904 lines / 168KB - largest template file in TI. |
| `TIDriveTemplate` | Drive definition | `modelResource()` is a METHOD not field - computes path from `hull.dataName` at runtime. |
| `ShipModelController` | Ship visual base | Call chain: `BuildShip()` → `SetSkin()` → `SetRadiators()` → `BuildDrives()` → `BuildWeapons()` → `SetRadiatorEmissiveKelvinRange()` → `AddExplosions()` |
| `HumanShipController` | Human ship base | Abstract. Serialized field `public GameObject hullModel` - stored as fileID in prefab, survives renames. Each hull has own controller subclass (`BattlecruiserController`, `LancerController`, `TitanController`, `DreadnoughtController`, etc.). |
| `AlienShipController` | Alien ship base | Parallel hierarchy: `AlienBattlecruiserController`, `AlienBattleshipController`, `AlienCruiserController`, `AlienAssaultCarrierController`, etc. Vanilla-only - BK does not subclass these. |
| `ShipWeaponVisController` | Weapon mount component | Required on every mount. Baked in prefab. Has serialized `baseObject`/`weaponObject`/`firePoint` fields. Vanilla code calls `GetComponent<>()`. For habs, runtime initialization uses `GetChild(0)` chain; for ships, fields are baked. |
| `ColorAnimationEffect` | Radiator emissive glow | Required on every radiator GO. `m_colorAnimation` (private Gradient with `[GradientUsage(true)]`) is serialized by Unity when baked. Drives material `_EmissionColor` uniform. |
| `RadiatorVisController` | Radiator state management | Required on every radiator GO. Baked in prefab. Has `intactRadiatorModel`, `destroyedRadiatorModel`, `explosionPrefab` serialized fields. |
| `DamageLayer` | Combat damage tracking (decals) | Required on root. Baked in prefab. Serialized `_shipDamageMaterial` and `_shipRenderers` fields. Manages `_DamagePointArray` shader uniform (max 8 active damage points). **NOT related to `_ExplosionSequenceRoot`** - that is a separate destruction effect system handled by `ShipModelController.AddExplosions` via `destructionEffectController` and asset-loaded prefabs (`spaceCombat/BigExplosion`, `spaceCombat/FinalExplosion`). |
| `HullRegistry` | BK hull registry (per dataName) | Reads `HullDefinitions.cfg`, derives `HullMode` per entry, exposes `GetByDataName()` for `DriveVisualPatch`, `DriveVariantPatch`, and `SetSkinSkipPatch`. See §7. |
| `ControllerRegistry` | BK controller registry (per ShipModelController subclass) | Reads `ControllerDefinitions.cfg`, exposes `GetByControllerType()`, dynamically attaches `WeaponMountPatch.DynamicPrefix` to each registered controller's `SlotToWeaponMountIndex`. See §7. |

### HumanShipController Decompiled Fields

```csharp
public GameObject hullModel;           // Hull mesh parent - serialized reference
public GameObject spikesRadiator12;    // Spike radiator references (4)
public GameObject spikesRadiator3;
public GameObject spikesRadiator6;
public GameObject spikesRadiator9;
public GameObject dropletRadiator12;   // Droplet radiator references (3)
public GameObject dropletRadiator4;
public GameObject dropletRadiator8;
```

Inherited from `ShipModelController`:
```csharp
public List<ShipWeaponVisController> noseWeaponControllers;
public List<ShipWeaponVisController> dorsalHullWeaponControllers;
public List<ShipWeaponVisController> ventralHullWeaponControllers;
public GameObject thrusterModel;
public GameObject[] thrusterLocations;
public List<GameObject> vectorThrusterGOs;
public GameObject radiator12, radiator130, radiator3, radiator4, radiator430;
public GameObject radiator6, radiator730, radiator8, radiator9, radiator1030;
protected List<Animator> radiatorAnimators;
protected List<ColorAnimationEffect> radiatorEmissivesFx;
```

### SetSkin Decompiled Logic (HumanShipController)

```csharp
public override void SetSkin(TISpaceShipTemplate ship)
{
    string shipMaterialSuffix = TIFactionTemplate.GetShipMaterialSuffix(ship.designingFaction);
    foreach (object obj in this.hullModel.transform)
    {
        Transform transform = (Transform)obj;
        MeshRenderer component = transform.GetComponent<MeshRenderer>();
        if (component != null && !transform.gameObject.name.ToLowerInvariant().Contains("common"))
        {
            component.sharedMaterial = GameControl.assetLoader.LoadAsset<Material>(
                new StringBuilder(ship.designingFaction.template.GetShipMaterialBundlePath(ship.hullAppearanceIndex))
                    .Append("/MAT_")
                    .Append(transform.gameObject.name)
                    .Append(shipMaterialSuffix)
                    .ToString());
        }
    }
    STOFighterController stofighterController = this as STOFighterController;
    if (stofighterController != null)
    {
        stofighterController.SetFlagMaterial(ship.nation);
    }
}
```

---

## 2. JSON Template System

### Merge Rules

| Level | Key | Rule |
|---|---|---|
| Top-level entries | `dataName` | Matched by dataName, not position. New entries add cleanly. |
| Sub-arrays | Index position | `shipModuleSlots`, `specialRules`, `techBonuses` - partial = Frankenstein merge. Always provide FULL arrays. |
| Scalar fields | Field name | Your value replaces vanilla. Unspecified fields keep vanilla value. |

**Field-level merge.** A BK override entry like `{"dataName": "Project_Warships", "friendlyName": "Orbital Ships"}` (only 2 fields) works correctly without zeroing the other ~14 vanilla fields.

### Three-Way Coupling: Slots ↔ Headers ↔ 3D Model

- `hullHardpoints` must equal count of `HullHardPoint` slots in array
- `noseHardpoints` must equal count of `NoseHardPoint` slots
- `internalModules` must equal count of `Utility` slots
- 3D model must have ≥ `hullHardpoints` dorsal+ventral mount pairs
- Exceeding 3D count → combat crash (`NullReferenceException`)
- Reducing below 3D count is safe - unused mounts hidden

### Slot Index Preservation

Saves reference slots by index. Observed save-compat rules from in-game testing:

- **Existing weapon slot indices must NOT move.** Moving an equipped weapon slot to a different array position desyncs saves referencing that index. Weapon positions effectively have hardcoded identity per save.
- **New weapon slots can be inserted anywhere.** Adding new HullHardPoint/NoseHardPoint entries is safe even when this shifts downstream utility/armor/propellant slots.
- **Utility, Armor, Propellant slots are position-flexible.** Their indices can shift (up or down) without crashing the game or desyncing equipped modules. The game reconciles these by type at load time.
- **In-place type conversion** (change an existing Utility slot to HullHardPoint at the same array index) is also save-safe - total array length unchanged, old index still valid. Count header (`internalModules`) must be updated to match.

Practical implications:
- Inserting new weapon slots mid-array is safe as long as no existing weapon slot moves from its original index.
- Appending at the end is always safe.
- Converting an existing utility to a weapon (in-place) is safe.
- Moving or deleting an existing weapon slot breaks saves and requires a new game.

### modelResource Format

`"bundleName/assetName"` - `AssetBundleManager.LoadAsset<T>(assetPath)`:
1. If no `/` in path → logs error `"Invalid asset path \"<path>\" expected BUNDLE/ASSET format"` and returns null
2. Splits on first `/`
3. Bundle name → `ToLowerInvariant()` (lowercased)
4. Asset name → as-is (case-sensitive)
5. Looks up `loadedBundles[bundleNameLower]`. If absent → logs `"Bundle not found! bundle name: <name>"` and returns null
6. Calls `bundle.LoadAsset<T>(assetName)`. If null → logs warning `"No asset found for <assetPath>"`

### specialRules (vanilla TIHabModuleTemplate field)

Field on `TIHabModuleTemplate`:
```csharp
public List<HabModuleSpecialRule> specialRules = new List<HabModuleSpecialRule>();
public float specialRulesValue;
```

Hardcoded enum - new behaviors CANNOT be added via JSON. Single companion `specialRulesValue` (float) consumed by certain rules.

**54 distinct values + `none`** found across the source archive (55 total). Categorized by usage pattern:

#### Value-consuming rules (use `specialRulesValue`)

| Rule | What `specialRulesValue` represents |
|---|---|
| `Farm` | Population fed (food capacity) |
| `FleetECM` | Fleet ECM bonus magnitude (TIHabModuleState.FleetECMBonus → `GetSpecialRuleValue(HabModuleSpecialRule.FleetECM)`) |
| `Stability` | Stability bonus |
| `MoneyIfNotBuilding` | Money/month when shipyard idle |
| `Efficiency` | Generic efficiency multiplier |
| `Solar_Power_Variable_Output` | Power output (modulated by solar conditions) |
| `Cost_Scales_With_Gravity` | Mass scaling factor for high-gravity bodies |
| `LEOControlPointCapacity` | Control point capacity (when in Earth LEO) |
| `LEOBonus*` (11 variants - see below) | Magnitude of the specific LEO bonus |

#### LEO Bonus rules (all value-consuming, only fire when hab in Earth LEO)

| Rule | Resource/Effect Boosted |
|---|---|
| `LEOBonusEconomy` | Economy priority output |
| `LEOBonusWelfare` | Welfare priority output |
| `LEOBonusEnvironment` | Environment priority output |
| `LEOBonusKnowledge` | Knowledge priority output |
| `LEOBonusGovernment` | Government priority output |
| `LEOBonusUnity` | Unity priority output |
| `LEOBonusOppression` | Oppression priority output |
| `LEOBonusLaunchFacilities` | Launch facilities capacity |
| `LEOBonusMissionControl` | Mission Control capacity |
| `LEOBonusMiltech` | Military tech research |
| `LEOBonusArmyCombatValue` | Army combat strength |
| `LEOBonusPropagandaStrength` | Propaganda effectiveness |
| `LEOBonusAlienDetection` | Alien faction asset detection |
| `LEOBonusHumanDetection` | Human faction asset detection |

`HasLEOBonus()` method tests `EarthLEOOnly` OR any rule in the contiguous `LEOBonusArmyCombatValue` through index+14 enum range - meaning the LEO* rules are stored as consecutive enum values.

#### Flag rules (ignore `specialRulesValue`)

| Rule | Effect |
|---|---|
| `EarthLEOOnly` | Hab can only be built in Earth LEO |
| `NotInIrradiated` | Cannot be built on irradiated bodies |
| `Requires_Colonized_Body` | Body must have `population >= colonizedSpaceObjectValue` (vanilla ≈ 10K; tunable via TIGlobalValuesTemplate JSON) |
| `Requires_Inhabited_Body` | Body must have `population >= populousSpaceObjectValue` (vanilla ≈ 50K; tunable via TIGlobalValuesTemplate JSON) |
| `Requires_Interface_Orbit` | Must orbit a body with surface interface (Earth, etc.) |
| `Requires_GasGiant_Orbit` | Must orbit a gas giant (Jupiter, Saturn, etc.) |
| `CanFoundTier1Habs` | Engineer module - enables founding T1 habs |
| `CanFoundTier2Habs` | Enables founding T2 habs |
| `CanFoundTier3Habs` | Enables founding T3 habs (alien-tech-tier) |
| `ConsumesMCWhenUnpowered` | Mission Control still consumed if hab loses power |
| `PowerFirst` | Power is allocated to this module before others (priority) |
| `HarvestAntimatter` | Module produces antimatter |
| `HarvestHelium3` | Module produces He3 |
| `UsesHelium3` | Build cost uses fissiles slot for Helium3 (when faction has He3 access) |
| `SolarMirror` | Solar mirror infrastructure (specific gameplay module) |
| `InterstellarLaunchModule` | Required for interstellar launches |
| `AlienSurveillance` | Alien-tech surveillance capability |
| `AlienWormhole` | Alien wormhole infrastructure (gates destruction protection on alien primary hab) |
| `AtrocityToKill` | Killing this module by faction action counts as 1 atrocity (always; flat per-module) |
| `AtrocityToKill_Populous` | Killing this module counts as 1 atrocity ONLY if `ref_naturalSpaceObject.Populous()` (≥50K body population) - separate from base AtrocityToKill check |
| `AtrocityToLose` | Losing this module to combat costs 1 atrocity to its owner |
| `FleetECM` | Module provides fleet ECM bonus; magnitude in `specialRulesValue` (consumed via `GetSpecialRuleValue(HabModuleSpecialRule.FleetECM)`) |
| `FleetTargeting` | Fleet targeting computer support |
| `RepairsHabKitShipModules` | Hab can repair ship utility modules with `RepairOnlyWhenConstructionModulePresent` rule |
| `RepairsMarineShipModules` | Hab can repair ship utility modules with `RepairOnlyWhenMarineModulePresent` rule |
| `StaticHab` | Hab cannot be moved/relocated (used for founding-time gating) |

#### Combat troops rules (defined in `combatTroopsRules` static list)

```csharp
public static readonly List<HabModuleSpecialRule> combatTroopsRules = new List<HabModuleSpecialRule>
{
    HabModuleSpecialRule.DropTroops,
    HabModuleSpecialRule.Griffins,
    HabModuleSpecialRule.MarineCompany,
    HabModuleSpecialRule.MarinePlatoon,
    HabModuleSpecialRule.MarineBattalion,
    HabModuleSpecialRule.Salamanders,
    HabModuleSpecialRule.WarDogs
};
```

7 rules that grant ground troop capability of varying types (drop infantry, mech battalions, etc.)

#### Default

`HabModuleSpecialRule.none` (lowercase) - enum default, no effect

#### Population thresholds for `Requires_*` rules

Both `Requires_Colonized_Body` and `Requires_Inhabited_Body` check **`ref_naturalSpaceObject.Colonized()` / `.Populous()`** which are virtual methods on `TINaturalSpaceObjectState`:

```csharp
public virtual bool Colonized()
    => this.population >= TemplateManager.global.colonizedSpaceObjectValue;

public virtual bool Populous()
    => this.population >= TemplateManager.global.populousSpaceObjectValue;
```

The thresholds are **tunable via `TIGlobalValuesTemplate` JSON** (`colonizedSpaceObjectValue` and `populousSpaceObjectValue` fields). Vanilla TI values are approximately 10K and 50K respectively, but mods can override these. The check is on the body's own `population` field (typed `ulong`, virtual).

#### LEOBonus per-module limit

`HasLEOBonus()` iterates `specialRules` and detects whether ANY LEO bonus is present. Practice from BK observations: most modules limit themselves to ≤2 LEO-bonus rules per template entry, but no hard enforcement at this level - limit appears to come from balance considerations or downstream consumer checks.

`TechBonusDiminishingReturns` is NOT found anywhere in the current archive's `HabModuleSpecialRule` references. The most likely explanation is confusion with the SEPARATE `techBonuses: TechBonus[]` field on TIHabModuleTemplate (which IS real, but is a different field, not a specialRule). Until/unless TIHabModuleTemplate.cs's enum declaration itself is reviewed, treat this name as unconfirmed.

### SHIP utility modules use a different enum: `SpecialModuleRule`

37 ship-side values (NOT to be confused with `HabModuleSpecialRule`):

`Assault`, `ComponentArmor`, `Crashdown`, `ECM`, `FoundFissionOutpost`, `FoundFissionPlatform`, `FoundFusionOutpost`, `FoundFusionPlatform`, `FoundSolarOutpost`, `FoundSolarPlatform`, `FoundSurveillanceOrbital`, `FoundSurveillancePlatform`, `FoundSurveillanceRing`, `FoundAutomatedFissionOutpost`, `FoundAutomatedFissionPlatform`, `FoundAutomatedSolarOutpost`, `FoundAutomatedSolarPlatform`, `ImmuneToDamage`, `ImmunetoAerobrakingDamage`, `LandArmy`, `LandHydra`, `Magazine`, `MarineOpsDefenseOnly`, `None`, `Prospector`, `RadHardened`, `ReduceFleetMCConsumption`, `RefuelFromAtmospheres`, `RefuelFromUnimprovedSites`, `Repair`, `RepairOnlyWhenConstructionModulePresent`, `RepairOnlyWhenMarineModulePresent`, `RotationalThrust`, `Salamanders`, `SalvageBonus`, `Surveillance`, `TargetingComputer`.

**Cross-system gating** (TIHabState.cs): two of the SHIP `SpecialModuleRule` values are gated by HAB `HabModuleSpecialRule` capabilities at the docked hab:
- Ship utility with `SpecialModuleRule.RepairOnlyWhenMarineModulePresent` → only repaired if hab has `HabModuleSpecialRule.RepairsMarineShipModules`
- Ship utility with `SpecialModuleRule.RepairOnlyWhenConstructionModulePresent` → only repaired if hab has `HabModuleSpecialRule.RepairsHabKitShipModules`

This is the formal interface between ship-side and hab-side repair systems - useful to know when designing modules that span both sides.

### BK's hab approach (does NOT use specialRules)

BetterKinetics' hab patches do NOT read or modify vanilla `specialRules`. Instead, BK's `ConfigReader` side-loads its OWN custom JSON fields from `TIHabModuleTemplate.json` files in every enabled mod folder, parsed separately from TI's template loader. Custom fields read: `bodyBuildLimit`, `factionBuildLimit`, `miningBonus`, `miningCapBonus`. These feed `BuildLimitPatch` (Postfix on `TIHabState.IsModuleAllowedForHab`) and the two mining-patch postfixes. This works around vanilla `TIHabModuleTemplate`'s lack of `[JsonExtensionData]` (TI's loader silently drops unknown fields, so BK never relies on TI's loader for its custom fields). See "BK's Hab Patches" subsection in §1.

### prereqs Array

Accepts project AND tech dataNames interchangeably. Per source: BK's `TIProjectTemplate.json` has projects with prereqs containing both `Project_*` names AND tech names like `MagnetoInertialFusion`, `PrinciplesofSpaceWarfare`, `MilitarizationofSpace`. Circular dependencies silently break.

### File Rules

- TI parses ALL `.json` in mod folder as template arrays. Exception: `ModInfo.json` only.
- Non-template JSON → parse crash. Store reference files in Source dir, never deploy folder.
- `HullDefinitions.cfg` uses `.cfg` extension specifically to avoid TI's JSON scanner.

### Income Fields (no DLL needed)

`income[Resource]_month` - engine handles: `money`, `research`, `influence`, `ops`, `volatiles`, `metals`, `nobles`, `fissiles`, `antimatter`, `exotics`.

### Build Materials

`weightedBuildMaterials` keys (from `ResourceCostBuilder`): `water`, `volatiles`, `metals`, `nobleMetals`, `fissiles`, `antimatter`, `exotics`. All `float` typed.

The hab module's actual material costs are computed via `BuildMaterials(...)` which takes weighted values and multiplies by mass - see TIHabModuleTemplate. For ship parts, see ResourceCostBuilder usage in TIShipPartTemplate.

### requiredProjectName Semantics - CRITICAL GOTCHA

Source: `TIShipPartTemplate.cs:164-171`:

```csharp
public bool FactionCanBuild(TIFactionState faction)
{
    if (faction.IsAlienFaction || requiredProject != null)
    {
        return faction.completedProjects.Contains(requiredProject);
    }
    return true;
}
```

And `TIFactionState.cs:8679`:
```csharp
public bool UnlockedShipPart(TIShipPartTemplate part)
{
    return part.requiredProject == null || this.completedProjects.Contains(part.requiredProject);
}
```

**The behavior of `requiredProjectName` is non-obvious:**

| `requiredProjectName` value | `requiredProject` resolves to | Human faction `FactionCanBuild` | Effect |
|---|---|---|---|
| `null` or empty string | `null` | Returns `true` | **UNLOCKED for all human factions, no project gate** |
| Pointing at non-existent project name | `null` (TemplateManager.Find returns null and logs error) | Returns `true` | **STILL UNLOCKED** (counterintuitive) |
| Pointing at real project | The TIProjectTemplate | `completedProjects.Contains(project)` | Locked until project completed |

**Common modder trap:** Setting `requiredProjectName` to `"Project_DoesNotExist"` does NOT lock the hull - it UNLOCKS it. The null project resolves to "no gate". To actually lock a hull from being built, point `requiredProjectName` at a REAL project that is hard or impossible to research (e.g., a sentinel project with unreachable prereqs and very low chance fields).

**Hab-side `FactionCanBuild` (TIHabModuleTemplate.cs) has DIFFERENT semantics - verify carefully:**

```csharp
public bool FactionCanBuild(TIFactionState faction)
{
    if (this.noBuild || !this.EverAllowedForFaction(faction))
        return false;
    if (this.RequiredProject == null)
        return true;
    // (cached per-frame lookup of completedProjects.Contains(RequiredProject))
    return faction.completedProjects.Contains(this.RequiredProject);
}

public bool EverAllowedForFaction(TIFactionState faction)
{
    return this.alienModule == faction.IsAlienFaction;
}
```

**Ship-side vs hab-side asymmetry (important for modders):**

| Condition | Ship-side `FactionCanBuild` | Hab-side `FactionCanBuild` |
|---|---|---|
| Human faction, `requiredProject == null` | `true` (unlocked) | `true` (unlocked) |
| Human faction, project completed | `true` | `true` |
| Human faction, project not completed | `false` | `false` |
| Alien faction, `requiredProject == null` | **`false`** (Contains(null) = false) | `true` (separate null check passes before alien check matters) |
| Alien faction, project completed | `true` | `true` |
| `noBuild = true` (hab module field) | (no such field on ship parts) | `false` (hard-stop, ignores everything else) |
| Alien module, human faction | (no equivalent gate) | `false` (EverAllowedForFaction check) |
| Result caching | None | Per-frame cache via `cachedHasBeenResearched` dict |

**For alien factions on SHIP side**: `FactionCanBuild` returns `completedProjects.Contains(null)` = false when project is null, so ship parts with null `requiredProjectName` are not buildable by alien factions. Asymmetric behavior between alien and human factions.

### noShipyardBuild Filter Behavior

Per TIFactionState.cs at three filter sites:
- Line 11617: Alien faction filter - `isAlien && !noShipyardBuild`
- Line 11653: Human faction startup filter - `!isAlien && !noShipyardBuild`
- Line 11694: Human faction with project gating - `FactionCanBuild(this) && !noShipyardBuild`

Setting `noShipyardBuild: true` on a hull removes it from every `allowedShipHulls` query - the player's design UI, the AI autodesigner, and all build pipelines. Template still exists in memory for code lookups (e.g., STOFighter spawn at line 14558, Titan achievement at line 8625) but cannot be selected for construction.

### Project Field Reference (TIProjectTemplate)

Per BK. `TIProjectTemplate.json` and use in TIFactionState's project advancement logic:

| Field | Type | Purpose |
|---|---|---|
| `dataName` | string | Unique identifier |
| `friendlyName` | string | UI display name |
| `techCategory` | string | "MilitaryScience", "SocialScience", "SpaceScience", "Energy", etc. |
| `AI_techRole` | string | "SpaceWar", "SpaceDevelopment", etc. AI uses for prioritization |
| `AI_criticalTech` | bool | If true, AI strongly prioritizes |
| `AI_projectRole` | string | "Fleet", "MissionControl", "SpaceResources" - AI fit-for-purpose tag |
| `researchCost` | int | Research points to complete |
| `prereqs` | string[] | Other project or tech dataNames required first |
| `altPrereq0` | string | Optional alternative prerequisite (OR semantics) |
| `oneTimeGlobally` | bool | Only one faction can ever complete |
| `repeatable` | bool | Can be researched multiple times |
| `factionAvailableChance` | int (0-100) | % chance ANY given faction sees this project as available |
| `initialUnlockChance` | int (0-100) | Initial % chance per check that project becomes researchable |
| `deltaUnlockChance` | int | Increment to unlock chance per check cycle |
| `maxUnlockChance` | int | Ceiling on cumulative unlock chance |
| `resourcesGranted` | array | Resources granted on completion |

**For AI deprioritization** (e.g., the BK rebuild's "vanilla loophole" project):
- Set `factionAvailableChance` very low (e.g., 5) → only 5% of factions even see the project
- Set `initialUnlockChance: 0` and `deltaUnlockChance: 1` → very slow accumulation
- Set `maxUnlockChance` low (e.g., 25) → caps probability
- Set `researchCost` very high (e.g., 250000) → prohibitive cost
- Set `AI_criticalTech: false` and `AI_techRole` to a non-military category like `SpaceDevelopment` → AI deprioritizes
- Set `AI_projectRole` to a non-fleet category like `MissionControl` → AI sees no fleet value

### Save-Load Null-Safety per Template Type

Per `TIFactionState.cs:998-1028`:

**Projects and techs are NULL-SAFE at save load:**
```csharp
foreach (string text in this.availableProjectNames) {
    TIProjectTemplate tiprojectTemplate = TemplateManager.Find<TIProjectTemplate>(text, false);
    if (tiprojectTemplate != null) {
        this.availableProjects.Add(tiprojectTemplate);
    }
}
// Same pattern for finishedProjectNames, availableTechNames, completedTechNames
```

If a save references a project or tech name that no longer exists in templates (e.g., mod removed/renamed), the template is silently skipped at load - no crash.

**Hulls are NOT null-safe.** TISpaceShipTemplate references hull templates by name. If a save references a hull dataName that no longer exists, `TemplateManager.Find<TIShipHullTemplate>(name)` returns null, and subsequent property access (`.alien`, etc.) throws NullReferenceException at load.

**Practical implications:**
- Renaming projects/techs is save-compatible (orphan refs silently dropped)
- **Renaming hulls is save-BREAKING** - must be released as a save-breaking version

---

## 3. Harmony Patching Rules

### Core Constraints

| Rule | Why | Violation |
|---|---|---|
| Check `Main.enabled` first | UMM toggle | Can't disable without restart |
| Wrap in try/catch | Crash isolation | Unhandled exception corrupts state |
| Postfix for hab patches | No side effects | Prefix skips vanilla entirely |
| Parameter names must match ALL original params | Harmony binds by name at runtime | Compiles fine, silently fails to bind |
| `nameof()` compiling ≠ runtime binding | C# resolves at compile, Harmony at runtime | Silent no-op |

### Priority & Ordering

| Priority | Patch | Reason |
|---|---|---|
| Default (400) | All BK static `[HarmonyPatch]` classes | `BuildLimitPatch`, `MiningBonusPatch` + `MiningCapBonusPatch`, `DriveVisualPatch`, `DriveVariantPatch`, `SetSkinSkipPatch` - independent, no ordering constraints |
| N/A (dynamic) | `WeaponMountPatch.DynamicPrefix` | Applied per-controller by `ControllerRegistry.PatchWeaponMounts()` after `harmony.PatchAll()`, not via `[HarmonyPatch]` attribute |

### Static Initialization Hazard

`AssetCacheManager` is a `public static class` where every field is `public static readonly Sprite X = GameControl.assetLoader.LoadAsset<Sprite>(...)`. C# static field initializers run in the static constructor (cctor) the first time the class is referenced. If `GameControl.assetLoader` isn't ready when AssetCacheManager is touched (e.g., during mod load before the game has initialized), every field initializer fails.

### Vanilla Typo

`SafeMineNextworkSize` - "Nextwork" not "Network". Per `BetterKinetics/MiningPatch.cs` - uses the `nameof(TIFactionState.SafeMineNextworkSize)` reference. BK's MiningCapBonusPatch comments document this intentionally: "If TI ever corrects the typo, nameof() below will fail to compile - intentional early warning."

When patching this property, must use the exact misspelling. The `nameof()` operator catches typo-correction breakages at compile time.

---

## 4. Asset Pipeline

### AssetRipper Extraction

Full `TerraInvicta_Data` extraction provides:
- Cross-bundle material resolution (ship meshes → faction material bundles → textures). Without this, material references are unresolved `deadbeef` placeholders.
- Real Unity version from `globalgamemanagers` (2020.3.49f1 for v1.0.32)
- Full script decompilation from `Managed/Assembly-CSharp.dll` (145 assemblies, Mono backend). Scripts contain real field definitions enabling correct component serialization in bundles.
- All AnimatorControllers as standalone assets (no manual extraction needed)

**AssetRipper settings:**

| Setting | Value |
|---|---|
| Default Version | `2020.3.49f1` |
| Bundled Assets Export Mode | Group By Asset Type |
| Script Content Level | Level 2 (full Mono decompilation) |
| Image Export Format | Png |
| Shader Export Mode | Dummy Shader |
| Script Export Format | Hybrid (Assembly-CSharp → .cs, others → .dll) |

**Decompiler artifacts** in v1.0.32 + AssetRipper 1.3.12 - 4 files need automated fixes before Unity compiles:

| File | Error | Fix |
|---|---|---|
| `Trajectory_Patched.cs` | CS0177: unassigned out param | Add default assignment at method start |
| `MasterTransferPlanner.cs` | CS8196: out var scoping | Hoist declarations before call |
| `MasterTransferPlanner.cs` | CS1061: `.trajectory` on tuple | → `.Item3` |
| `SimulatedCombat.cs` | CS1061: `.x` / `.DistanceToTarget_km` on tuple | → `.Item1` / `.Item1.DistanceToTarget_km` |
| `HumanHabPlanner.cs` | CS1061: `.Hab`/`.Order`/`.Cost` on tuple | → `.Item1`/`.Item2`/`.Item3` (regex with word boundaries - naive replace breaks `hab.HabSchematic`) |

Fix script: `fix_decompiler_errors.py` (~30 automated fixes).

### Bundle Discovery → Load

```
Game startup
  → ModManager.GetEnabledModFiles()
    → Auto-creates Mods/Enabled and Mods/Disabled if missing
    → Recursive scan Mods/Enabled/*/  via Directory.EnumerateDirectories
    → Recursively gets all files in each enabled mod via Directory.GetFiles(text, "*.*", SearchOption.AllDirectories)
    → .manifest file → ModAssetBundles list
  → AssetBundleManager.Initialize()
    → Loads vanilla manifest from Application.streamingAssetsPath/AssetBundles/AssetBundleManifest
    → Vanilla → DLC (overrides via Remove+Add) → Mod bundles
    → Mod bundles ADDED without dupe check
  → loadedBundles dict keyed by bundle.name (lowercase)
```

### LoadAsset<T>(path)

```csharp
public static T LoadAsset<T>(string assetPath) where T : Object {
    if (!assetPath.Contains("/")) {
        Log.Error("Invalid asset path \"" + assetPath + "\" expected BUNDLE/ASSET format");
        return null;
    }
    string[] array = assetPath.Split('/');
    string text = array[0].ToLowerInvariant();
    string name = array[1];
    if (loadedBundles.ContainsKey(text)) {
        T val = loadedBundles[text].LoadAsset<T>(name);
        if (val == null) {
            Debug.LogWarning("No asset found for " + assetPath);
        }
        return val;
    }
    Debug.LogError("Bundle not found! bundle name: " + text);
    return null;
}
```

Splits on first `/`. Bundle name lowercased. Asset name as-is (case-sensitive).

### Rules

| Rule | Consequence |
|---|---|
| Bundle name unique across all mods | Duplicate → `ArgumentException` at startup |
| Never name bundle `ships` | Conflicts with vanilla 1.8GB bundle |
| Deploy both bundle AND .manifest | Missing manifest → never discovered |
| Build target: `StandaloneWindows64` | TI is a Windows game (even under Proton). Wrong target → load failure |
| Unity version: exact 2020.3.49f1 | Serialization format changes between versions → incompatible → crash |

### Bundle Build Requirements (Editor Graphics Settings)

**Critical for any bundle containing materials that reference shaders.** Must be set in the extracted project's Edit > Project Settings > Graphics **before** building, or via code in the build tool. ShipTools' `EnsureBundleSettings()` applies these automatically.

| Setting | Required Value | Why |
|---|---|---|
| Always Included Shaders - Standard | **REMOVED** from list | If Standard is in Always Included Shaders, the bundle stores only a *reference* to Standard instead of the shader code. At runtime in TI, that reference can't resolve (TI has a different Always Included list), the material's shader binding is null, and the mesh renders pure white. **This is the root cause of the "white hull" bug.** |
| Shader Stripping - Instancing Variants | **Keep All** | Default is "Strip Unused" which removes `INSTANCING_ON` variant from bundled shaders. Unity issue tracker confirms this happens even with ShaderVariantCollections. Canonical fix: set to Keep All. |

**Note on dependency inclusion:** Unity's asset bundle system walks dependencies automatically. Tagging a prefab pulls in its referenced materials → their textures → their shaders. No manual tagging of materials/textures needed - and in fact, tagging them separately creates cross-bundle shader reference problems.

**Build options for BK bundles:**
```csharp
BuildPipeline.BuildAssetBundles(outputPath,
    BuildAssetBundleOptions.ForceRebuildAssetBundle |
    BuildAssetBundleOptions.DeterministicAssetBundle,
    BuildTarget.StandaloneWindows64);
```

- `ForceRebuildAssetBundle` - ensures no stale cache after editor changes
- `DeterministicAssetBundle` - stable internal IDs across rebuilds (avoids subtle cache-invalidation issues between iterations)
- Default compression is LZMA (full-file). Unity recommends `ChunkBasedCompression` (LZ4) for locally-distributed bundles loaded via `AssetBundle.LoadFromFile` - future optimization, not required for correctness.

### Bundle Inspection Limits

- `AssetBundle.LoadAllAssets<T>()` returns only explicitly tagged assets and their direct sub-assets - **not dependency assets** pulled in by reference. A bundle may contain 10 dependency materials that `LoadAllAssets<Material>()` reports as zero.
- Reliable bundle verification: load the prefab, walk `MeshRenderer.sharedMaterials`, check for null slots and texture references. This is what ShipTools `VerifyHull` does.

### Asset bundle tag location

Asset bundle assignments live in the `.meta` file, not the `.prefab` file. `SaveAsPrefabAsset` and `EditPrefabContentsScope` do not touch the bundle tag - it persists across any number of content edits. Tagging is done via `AssetImporter.GetAtPath(path).SetAssetBundleNameAndVariant(...)`.

### Texture Streaming - CRITICAL SETUP STEP

Unity's `Streaming Mipmaps` texture import setting is **incompatible with mod-loaded AssetBundles in TI** and is the root cause of the "white ship" rendering bug. This is the single most important non-obvious gotcha in the entire BK pipeline.

**What goes wrong:**
- Unity's texture streaming system loads only the lowest mip level (16×16 or smaller) at first, then progressively upgrades based on visibility and `Mip Map Priority`
- Streaming priority is established at scene-load time when the streaming system catalogs textures in loaded bundles
- Mod bundles are loaded by `AssetBundleManager.Initialize` AFTER the streaming system is already running for vanilla bundles
- TI's streaming system never properly prioritizes mod-bundle textures - they stay stuck at the lowest mip level forever
- The lowest mip of a 2048×2048 texture is essentially a single average color
- The Standard shader samples that single-pixel "texture" and renders it as a flat color across the whole mesh
- Result: every mod-loaded ship hull renders as a solid white/gray silhouette in both combat and strategy view, regardless of how correct the materials, shader keywords, prefab structure, or DLL patches are

**The fix:**
- Every Texture2D in the project must have `streamingMipmaps = false` in its TextureImporter
- Direct comparison with the working Expanse Ships Mod bundle: their textures have `streaming=False`, ours had `streaming=True`
- This must be set at **import time** - the flag is baked into the texture's `.meta` file and serialized into the bundle
- Editing the flag at build time via ShipTools `EnsureBundleSettings()` does NOT work - the bundle serializer reads the imported state, not runtime overrides

**Project setup pattern (REQUIRED for any new BK-style extracted Unity project):**
1. Drop `ShipTools.cs` into `Assets/Editor/` **BEFORE** running AssetRipper or copying any textures. Its nested `Importer : AssetPostprocessor` (see §16) calls `streamingMipmaps = false` on every texture imported afterwards.
2. Run AssetRipper extraction → all extracted textures get the flag disabled at import time.
3. After project is set up, run `Ship Tools/Setup/2. Disable Streaming on All Textures` once as a one-shot for any textures imported before the postprocessor was added.

**Verification via UnityPy after build (sanity check):**
```python
import UnityPy
env = UnityPy.load('path/to/bundle')
for o in env.objects:
    if o.type.name == 'Texture2D':
        d = o.read()
        print(f'{d.m_Name}: streaming={d.m_StreamingMipmaps}')
```
Expected: `streaming=False` for every texture. If any show `streaming=True`, the postprocessor was missed for those imports - re-run the menu item and rebuild the bundle.

**Why this is invisible without comparison testing:**
- Unity Editor renders textures correctly in Scene view, Game view, and Play Mode regardless of streaming flag (editor doesn't run the streaming system the same way)
- ShipTools `Verify Hull` reports materials as "textured" because the texture references are valid - the renderer just can't sample them at runtime
- Player.log shows no errors, warnings, or null references - the bundle loads fine
- Bundle inspection via UnityPy confirms shader, materials, keywords, and texture PathIDs are all valid
- The bug only manifests when TI's runtime tries to sample the actual mip levels at draw time
- The only way to find this without prior knowledge is to compare a known-working mod bundle (like Expanse Ships Mod) to the broken bundle and notice the streaming flag delta

### Comparison Diagnostic Methodology

When debugging custom hull rendering issues, use the Expanse Ships Mod bundle as the reference for "what a working mod bundle looks like." Installed via Workshop subscription at `<SteamLibrary>/steamapps/workshop/content/1176470/3490333915/expanseshipsmod`. The DLL `ExpanseShipsModActual.dll` is decompilable with dnSpy.

Methodology:

1. Inspect the broken bundle with UnityPy - dump material keywords, shader PathIDs, texture references, streaming flags
2. Inspect Expanse's bundle the same way
3. Diff the outputs - fields that match are eliminated as causes; fields that differ are candidates
4. Verify candidates against Unity docs and community discussions
5. Apply the minimal fix that aligns the broken bundle to the working one

A working reference is more valuable than any amount of single-bundle inspection.

### Inspector Display Quirks

Unity's material inspector for the Standard shader displays texture slots with thumbnails when textures are assigned. **However, the Albedo slot can sometimes display as visually empty (white square with eyedropper icon) even when the underlying material file has a valid texture reference.** This is an inspector display artifact, not a real null.

Ground truth is always the YAML in the .mat file, not the inspector. To verify any texture slot:

```fish
grep -A 2 "_MainTex\|_BumpMap\|_MetallicGlossMap" path/to/material.mat
```

Expected for a valid assignment:
```
_MainTex:
  m_Texture: {fileID: 2800000, guid: c97daa434332d4a4387d6488032f857f, type: 3}
```

Expected for a true null:
```
_MainTex:
  m_Texture: {fileID: 0}
```

If `m_Texture` has a non-zero `fileID` and a `guid`, the texture is assigned regardless of what the inspector shows. Trust the file, not the UI.

---

## 5. Enums & Constants

### Mount Enum

Source-derived from usage across TIShipHullTemplate.cs, TIFactionState.cs, TISpaceShipTemplate.cs, and per-hull controllers:

| Name | Notes |
|---|---|
| OneNose | Single nose weapon |
| OneHull | Single hull weapon |
| TwoNoseHoriz | Two nose horizontal pair |
| TwoNoseVert | Two nose vertical pair |
| TwoHullHoriz | Hull horizontal pair |
| TwoHullVert | Hull vertical pair |
| ThreeNoseAngle | Three nose, angled |
| ThreeHullHoriz | Three hull, horizontal |
| FourNose | Four nose mount |
| FourHull | Four hull mount |
| HalfNose | Alien-only half nose |
| HalfHull | Alien-only half hull |

12 distinct values. Source comparisons like `mount - Mount.TwoHullHoriz > 3` and `mount - Mount.TwoNoseHoriz > 3` indicate consecutive enum value spans of 4 each. Some controllers use `(uint)(mount - N) <= K` decompiler patterns for range checks against contiguous enum values.

### Nozzle Enum (computed, not JSON)

| Result | String | Condition |
|---|---|---|
| Pulsed | `"Pulse"` | `pulsedDrive` true |
| DeLaval | `"DeLaval"` | thrustPower < 1 GW, OR EV < 87.5 kps, OR not antimatter/nuclear |
| Magnetic | `"Magnetic"` | ≥1 GW AND ≥87.5 kps AND antimatter or nuclear |

Source:
```csharp
public Nozzle nozzle {
    get {
        if (this.pulsedDrive) return Nozzle.Pulsed;
        if (this.singleThrusterTemplate.thrustPower_GW < 1f
            || (double)this.EV_kps < 87.5
            || !this.singleThrusterTemplate.antimatterOrNuclearDrive)
            return Nozzle.DeLaval;
        return Nozzle.Magnetic;
    }
}
```

### Slot Types

`Drive`, `PowerPlant`, `Utility`, `Radiator`, `TailArmor`, `LateralArmor`, `NoseArmor`, `NoseHardPoint`, `HullHardPoint`, `None` - confirmed in source via `ShipModuleSlotType.X` references at multiple sites in TISpaceShipState.cs, TISpaceShipTemplate.cs, TIShipHullTemplate.cs, TIDriveTemplate.cs.

`weaponSlot` (computed property) returns true for `HullHardPoint` or `NoseHardPoint`.
`armorSlot` (computed property) returns true for `LateralArmor`, `NoseArmor`, or `TailArmor`.

(Note: there is a separate `Propellant` enum in `TIDriveTemplate.cs` used for drive type classification - not a slot type. The two are unrelated.)

### Module States

| State | Method | Used By |
|---|---|---|
| active | `ActiveModules()` | F3/F4 (bonuses) |
| okay | `OkayModules()` | F1/F2 (limits) |

### Faction Material Suffixes - DATA-DRIVEN (not enum)

`TIFactionTemplate.GetShipMaterialSuffix(designingFaction)`:

```csharp
public static string GetShipMaterialSuffix(TIFactionState designingFaction)
{
    string result = designingFaction.template.hullSkinBase;
    if (designingFaction.IsAlienFaction)
    {
        result = GameStateManager.AlienProxy().template.hullSkinBase;
    }
    else
    {
        string text = designingFaction.template.shipMaterialBundlePath;
        foreach (TIFactionTemplate item in TemplateManager.IterateByClass<TIFactionTemplate>())
        {
            if (text.Contains(item.hullSkinBase))
            {
                result = item.hullSkinBase;
                break;
            }
        }
    }
    return result;
}
```

The suffixes (`_resist`, `_destroy`, etc.) are values stored in each faction's `hullSkinBase` JSON field in `TIFactionTemplate.json` - NOT enum constants. Listed here for reference:

| Suffix | Faction |
|---|---|
| `_resist` | Resistance |
| `_destroy` | Humanity First |
| `_submit` | Servants |
| `_appease` | Protectorate |
| `_cooperate` | Academy |
| `_exploit` | Initiative |
| `_escape` | Exodus |

Implementation note: for non-alien factions, the function loops checking if the faction's `shipMaterialBundlePath` contains another faction's `hullSkinBase` and switches if found. This implements a "bundle inheritance" mechanism for sub-factions.

---

## 6. Prefab Requirements

### Flat Hierarchy

All children must be direct children of the prefab root. `transform.Find("name")` without a path separator only searches immediate children. Nested objects are invisible to vanilla `transform.Find` → null → crash.

Edit in **Prefab Edit Mode** (double-click prefab in Project panel). Scene edits do NOT persist to the `.prefab` asset file.

### Exact-Name Children (vanilla `transform.Find` - case-sensitive)

Required GameObject names:

| Name | Purpose |
|---|---|
| `Hull` | **Renamed body mesh parent.** Vanilla hulls ship as `Earth_Hull_<n>` (Battlecruiser, LightCruiser, Battleship) or `Hull_<n>` (Lancer, Frigate, Corvette, Monitor, Destroyer, Titan). Vanilla `SetSkin` calls `transform.Find("Hull")` to locate `hullModel`. Without a child literally named `Hull` at root, vanilla fails to locate the hull mesh and the ship cannot render. (For BK Hybrid/FullCustom hulls `SetSkinSkipPatch` skips vanilla SetSkin entirely, but other vanilla call sites still expect `Hull`.) **The rename step is REQUIRED for every custom hull.** Body mesh children under the renamed parent keep their vanilla names. |
| `Drive` | Engine visual. Must contain children with `ThrusterPoint` in name (VFX placement) and `Vector` in name (vector thrusters). |
| `SelectionReticle` | Combat selection highlight |
| `GroupSelectionReticle` | Multi-select highlight |
| `Padlock Container` | Tracking/maneuver icon |

**Radiators (10 fin):** `Radiator12`, `Radiator3`, `Radiator130`, `Radiator6`, `Radiator4`, `Radiator430`, `Radiator730`, `Radiator1030`, `Radiator8`, `Radiator9`

**Spikes (4):** `spikes 12`, `spikes 3`, `spikes 6`, `spikes 9` (literal space in name, case-sensitive)

**Droplets (3):** `Droplet12`, `Droplet8`, `Droplet4`

**`_ExplosionSequenceRoot`** is **NOT** searched for via `transform.Find`. It is a baked component subtree on the prefab root used by the destruction effect system (managed via `ShipModelController.AddExplosions` and the `destructionEffectController`). The "required" status is for combat destruction effects to function. See the DamageLayer row in §1 Ship System.

### Body Mesh Parent Naming (pre-rename)

Vanilla hulls have one body mesh subtree at root. Two naming patterns:

| Pattern | Examples | Body Part Count |
|---|---|---|
| `Earth_Hull_<n>` | `Earth_Hull_Battlecruiser`, `Earth_Hull_LightCruiser`, `Earth_Hull_Battleship` | ~10 parts |
| `Hull_<n>` | `Hull_Lancer`, `Hull_Frigate`, `Hull_Corvette`, `Hull_Monitor`, `Hull_Destroyer`, `Hull_Titan` | ~7 parts |

ShipTools `Create Hull From Vanilla` detects either pattern automatically and renames the body mesh parent to `Hull` during hull creation. If neither pattern matches (e.g., future alien hull), the tool logs a warning and the user must rename manually before building. The rename is what lets vanilla code find the hull mesh via `transform.Find("Hull")`.

**Battlecruiser body part children (10, under Earth_Hull_Battlecruiser):** `Earth_Battlecruiser_Crew`, `Earth_Battlecruiser_Crew_Detail`, `Earth_Battlecruiser_Head`, `Earth_Battlecruiser_Laser`, `Earth_Battlecruiser_Mid_HardPoint`, `Earth_Battlecruiser_Mid_Module`, `Earth_Battlecruiser_Radiators`, `Earth_Battlecruiser_Shoulders_A`, `Earth_Battlecruiser_Shoulders_B`, `Earth_Battlecruiser_Tanks`

**Lancer body part children (7, under Hull_Lancer):** `Crew`, `Crew_Details` (note plural), `Head`, `Mid_A`, `Mid_B`, `Radiators`, `Tanks`

**Vanilla prefab body children have EMPTY material GUIDs** - confirmed by YAML parsing. TI relies on runtime `SetSkin` to load faction-specific materials from separate faction bundles at render time. This is why registered mod hulls (which bypass vanilla SetSkin) must have materials baked into the bundle at edit time. Body part MeshRenderers in vanilla prefabs have no `sharedMaterial` at all until the engine assigns one.

### Hull Mesh Children - Keep Vanilla Names

Body mesh children (e.g., `Earth_Battlecruiser_Crew`, `Earth_Battlecruiser_Head`, `Hull_Lancer/Head`) must keep their vanilla names. The edit-time bake tool identifies which faction material belongs to each renderer by matching `MAT_{childName}_{faction}.mat` - the filename carries the identity. Renamed children have no matching material file and the bake tool will skip them, leaving them unrendered. Only the body mesh PARENT gets renamed to `Hull`; the children under it keep their vanilla names.

### Harmless Null Renderers

Top-level radiator GameObjects (`Radiator12`, `Droplet4`, etc., 13 total on Battlecruiser) have a MeshRenderer component with no actual mesh to draw - the renderer is vestigial, with its MeshFilter either missing or holding a null sharedMesh. The actual radiator rendering happens on child SkinnedMeshRenderer components driven by bone chains and the Animator. These top-level MeshRenderers have null `sharedMaterial` in vanilla prefabs and always have - vanilla ships render correctly because the draw-call for a null-mesh renderer is a no-op.

**`RunHullChecks` and `VerifyBundleHull` skip renderers where any of the following are true:**
- `MeshRenderer.enabled == false`
- `MeshFilter` component is missing
- `MeshFilter.sharedMesh == null`

Only drawable renderers are counted. This prevents false-positive null-material errors on vestigial containers.

### Weapon Mounts (case-INsensitive Contains)

```csharp
if (child.gameObject.name.ToLower().Contains("nose"))    // → noseWeaponControllers
if (child.gameObject.name.ToLower().Contains("dorsal"))  // → dorsalHullWeaponControllers
if (child.gameObject.name.ToLower().Contains("ventral")) // → ventralHullWeaponControllers
```

| Contains | Array | Pairing |
|---|---|---|
| `dorsal` | `dorsalHullWeaponControllers` | `[0]` pairs with ventral `[0]` |
| `ventral` | `ventralHullWeaponControllers` | Same index = same hardpoint |
| `nose` | `noseWeaponControllers` | Independent array |

Hierarchy order = array index. Aft-to-forward convention.

Three-level structure required per mount (per `ShipWeaponVisController.Initialize`):
```
Mount GO           ← edit-time AddWeaponMount finds via name Contains, this is baseObject
  └── Gun child    ← weaponObject = baseObjectTransform.GetChild(0)
       └── FirePoint  ← firePoint = weaponObjectTransform.GetChild(0)
```

### Layers

| Layer | Applied to | Purpose |
|-------|-----------|---------|
| 2 (Ignore Raycast) | Root GO | Default - not hit by projectiles |
| 17 (HurtBox) | Hull mesh children, Drive | Projectile collision detection. Wrong layer = projectiles pass through. |
| 0 (Default) | Weapon mounts, radiators, thrusters, UI elements | Non-hittable |

### Baked Components (~96 total on Battlecruiser)

All components are serialized in the prefab from the AssetRipper-extracted Unity project.

| Script | Count | Applied to | Notes |
|--------|-------|-----------|-------|
| `BattlecruiserController` | 1 | Root | Hull-specific controller subclass |
| `DamageLayer` | 1 | Root | Real-time damage decals via shader uniforms (`_DamagePointArray`, `_DamagePointArrayLength`); max 8 active damage points |
| `ShipWeaponVisController` | 8 | Weapon mounts | Has `baseObject`/`weaponObject`/`firePoint` serialized fields |
| `RadiatorVisController` | 17 | All radiator GOs | `intactRadiatorModel`/`destroyedRadiatorModel`/`explosionPrefab` |
| `ColorAnimationEffect` | 19 | Radiators + hull mesh | Drives `_EmissionColor` shader uniform |
| `ParticleGroupEffect` | 33 | Explosion children | Sub-systems for destruction effects |
| `EffectSequencer` | 8 | Explosion sub-systems | |
| `BurnUpEffect` | 5 | Explosion children | |
| `DestructionEffect` | 2 | Explosion children | |
| `SimpleExplosionSequence` | 1 | `_ExplosionSequenceRoot` | Triggers destruction effect cascade |
| `RandomEffectSequence` | 1 | Explosion sub-system | |

---

## 7. Config file schemas

BK reads two config files at startup. Both are JSON arrays. Both scan all sibling mod folders under `Mods/Enabled/`. Both are first-wins on collision (later mods registering the same key are ignored with a log warning).

### 7.1 - `HullDefinitions.cfg`

Per-`dataName` hull declarations. Each entry's field combination derives a `HullMode`:

```json
[
    {
        "dataName": "Battlecruiser",
        "bundleName": "scoutcruiser"
    },
    {
        "dataName": "HeavyCruiser",
        "vanillaDriveDataName": "Dreadnought"
    }
]
```

| Field | Type | Required | Description |
|---|---|---|---|
| `dataName` | string | Yes | Hull's `dataName` in `TIShipHullTemplate.json`. Registry key. |
| `bundleName` | string | No | AssetBundle name (lowercase). Required for Hybrid + FullCustom modes. |
| `drivePrefab` | string | No | Standalone drive prefab name in the bundle. Promotes Hybrid → FullCustom. |
| `vanillaDriveDataName` | string | No | A vanilla hull's dataName whose drive paths are reused. Required for PatchOnly mode. |

**Mode derivation** (from `HullRegistry.cs`):

| Field combination | Mode | Behavior |
|---|---|---|
| only `vanillaDriveDataName` | `PatchOnly` | New dataName aliases another hull's drive assets; no bundle |
| `bundleName` only | `Hybrid` | BK hull mesh from bundle; vanilla drive variants baked in at edit time |
| `bundleName` + `drivePrefab` | `FullCustom` | BK hull mesh + BK drive prefab (cross-bundle shader rendering is fragile - `HullRegistry` warns at parse) |
| `bundleName` + `vanillaDriveDataName` | `Hybrid` | BK mesh; drive paths use the alias when resolved by `DriveVisualPatch` |
| `drivePrefab` without `bundleName` | rejected | Invalid; entry is skipped |
| no bundle, no drive prefab, no alias | rejected | Useless entry; entry is skipped |

### 7.2 - `ControllerDefinitions.cfg`

Per-`ShipModelController` slot-to-mount-index maps. Resolved via reflection against `Assembly-CSharp.dll`. Applied dynamically as Harmony prefixes on each registered controller's `SlotToWeaponMountIndex`.

```json
[
    {
        "controllerClass": "BattlecruiserController",
        "weaponSlotMap": [
            {"slot": 7, "index": 0, "overrides": [{"mounts": ["OneNose"], "index": 1}]},
            {"slot": 8, "index": 3, "overrides": [{"mounts": ["TwoHullVert", "TwoNoseHoriz", "ThreeNoseAngle"], "index": 0}]},
            {"slot": 10, "index": 2}
        ]
    }
]
```

| Field | Type | Required | Description |
|---|---|---|---|
| `controllerClass` | string | Yes | A subclass of `ShipModelController`. Validated at parse - wrong type or missing class is rejected. |
| `weaponSlotMap` | array | Yes | Slot-to-mount-index mappings. Empty array is invalid - at least one slot must be mapped. |

**`weaponSlotMap` entry**:

| Field | Type | Required | Description |
|---|---|---|---|
| `slot` | int | Yes | JSON `shipModuleSlots` array index |
| `index` | int | Yes | Default 3D weapon mount array index for this slot |
| `overrides` | array | No | Per-`Mount` index overrides for this slot |

**`overrides` entry**:

| Field | Type | Required | Description |
|---|---|---|---|
| `mounts` | string[] | Yes | `Mount` enum values that trigger this override |
| `index` | int | Yes | Array index to use when one of the listed mounts is equipped |

### 7.3 - Delta-only semantics

`WeaponMountPatch.DynamicPrefix` is the Harmony prefix attached to each registered controller's `SlotToWeaponMountIndex`. Behavior on each call:

1. Look up the controller's slot map via `ControllerRegistry.GetByControllerType(__instance.GetType())`.
2. Walk per-mount overrides first; on a `Mount` match, set `__result` to the override's `index` and return `false` (skip vanilla).
3. Otherwise, on a slot match, set `__result` to the slot's default `index` and return `false`.
4. No mapping match → return `true` (run vanilla `SlotToWeaponMountIndex` unmodified).

**Consequence:** `weaponSlotMap` only needs entries for slots whose 3D mount index differs from vanilla's return value. Unmapped slots fall through to the vanilla controller's switch statement.

**Verification:** check the decompiled `<ControllerClass>.SlotToWeaponMountIndex` in `Assets/Scripts/Assembly-CSharp/PavonisInteractive/TerraInvicta/`. For each vanilla weapon slot, compare the returned index against the position in the new 3D model's mount hierarchy. If they align → omit from cfg. If not → add an entry. See §17 for full controller switch listings.

---

## 8. Hull DataName Map

Current BK hulls registered in `HullDefinitions.cfg`. Three Hybrid hulls ship custom meshes; one PatchOnly hull aliases a vanilla drive.

| dataName | Friendly Name | Mode | Bundle / Source | Notes |
|---|---|---|---|---|
| Battlecruiser | Scout Cruiser | Hybrid | `scoutcruiser` (ScoutCruiser.prefab) | Reuses BattlecruiserController; vanilla drive variants |
| Lancer | Battlecruiser | Hybrid | `newbattlecruiser` (NewBattleCruiser.prefab) | Reuses LancerController; vanilla drive variants |
| Titan | Battleship | Hybrid | `newbattleship` (NewBattleShip.prefab) | Reuses TitanController; vanilla drive variants |
| HeavyCruiser | Heavy Cruiser | PatchOnly | (alias only) | `vanillaDriveDataName: "Dreadnought"` - reuses Dreadnought drive paths via `DriveVisualPatch` |

Other vanilla hulls (Gunship, Escort, Corvette, Frigate, Destroyer, Monitor, Cruiser, Battleship, Dreadnought) are unmodified - BK does not register them.

### Bundle integrity

Each shipped bundle has a companion `<bundlename>.manifest` file (Unity-generated YAML) recording CRC, asset/typetree hashes, class types, script GUIDs, and contained assets.

| Bundle | Contains | Hull dataName | Controller |
|---|---|---|---|
| `scoutcruiser` | `ScoutCruiser.prefab` (+ baked drive variants) | Battlecruiser | BattlecruiserController |
| `newbattlecruiser` | `NewBattleCruiser.prefab` (+ baked drive variants) | Lancer | LancerController |
| `newbattleship` | `NewBattleShip.prefab` (+ baked drive variants) | Titan | TitanController |

Each bundle is self-contained (`Dependencies: []`) - no inter-bundle references. CRC and `AssetFileHash` provide integrity verification; any change to bundle contents changes both. `TypeTreeHash` is Unity's serialization-format fingerprint - a mismatch between build and runtime prevents deserialization (typically due to Unity version mismatch or script structure changes).

ShipTools `Finalize and Ship` writes both `<bundlename>` and `<bundlename>.manifest` to the deploy folder; missing `.manifest` causes the bundle never to be discovered.

---

## 9. Best Practices

### JSON
- Full sub-arrays always. Partial = corruption.
- Python verification after every edit: headers, dupes, weapon index, parse.
- Never move or delete existing weapon slots. Inserting new weapon slots anywhere is safe. See §2 Slot Index Preservation.
- No `None` slots - render as usable when mod-loaded.
- Utility slots flank nearest HullHardPoint column (avoids phantom rendering).
- Localization function-only - no stat values (prevents desync).
- **`thrusterMultiplier` is INT type** - fractional values (1.12, 1.15, 1.25) silently truncate to 1 at parse. To get a multiplier effect, either use whole numbers or apply via Harmony patch.
- **`requiredProjectName` null = unlocked, not locked.** Pointing at non-existent project = also null = also unlocked. To lock a hull, point at a real-but-unreachable project. See §2.

### DLL
- All patches: enabled check, try/catch, log error, return unmodified on failure.
- Postfix parameter list must exactly match original method.
- New hulls = config entry in `HullDefinitions.cfg`, not code changes.
- Use controller type matching (`GetByControllerType`), not name matching.
- Harmony Postfix must include ALL original method parameters or silently fails to bind.
- Compare against a known-working reference DLL (e.g., Expanse Ships Mod) when stuck.

### Assets
- **Drop `ShipTools.cs` into `Assets/Editor/` BEFORE extracting anything** - its `Importer` postprocessor disables `streamingMipmaps` on every imported texture. Without this, mod-bundle ships render flat white in TI runtime regardless of correct materials/shaders/keywords. See §4 Texture Streaming.
- **Flat hierarchy** - all children direct under prefab root.
- **Prefab Edit Mode** - double-click prefab in Project panel. Scene edits don't persist.
- Verify Unity version from game's `globalgamemanagers`, never docs.
- Unique bundle names. Deploy bundle + manifest together.
- Keep vanilla hull mesh child names intact for faction material matching.
- All hull mesh children on Layer 17 for projectile collision.
- Use Ship Tools → Verify Hull before every build.

### C5 - Bad Hull/Utility Module Placement

If on game start the log shows hundreds of `"Bad hull module placement"` or `"Bad utility module placement"` warnings, this means:
- One or more vanilla AI ship designs (typically `Ship1` through `Ship33`) reference slot indices that don't match the current hull's slot layout.
- Most often caused by BK changing slot layouts on hulls whose vanilla designs are still loaded from save or scenario data.
- Symptom is purely cosmetic in most cases (warnings only) but indicates AI ships may render with weapons in wrong positions or be unbuildable.
- Verify on fresh-game start.

### Environment (Linux)
- Fish shell: `echo '...' >` not heredocs.
- Unity build: `openssl-1.1` (AUR) + `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
- Unity licensing (kernel 6.19+): `patchelf --clear-execstack` on `libbindings.so`
- Build DLL: `cd <repo>/src && dotnet build -c Release`
- Deploy DLL: `cp bin/Release/net48/BetterKinetics.dll ".../Mods/Enabled/ExpandedFleetsAndNavies/"`
- Unity editor: `unity -projectPath <unity-work>/TI_Ships_Extracted/ExportedProject`
- UMM: `WINEDLLOVERRIDES="winhttp=n,b" %command%`

---

## 10. Key Paths

| Item | Path |
|---|---|
| Game root | `<game>/` = `<SteamLibrary>/steamapps/common/Terra Invicta/` |
| Assembly-CSharp.dll | `<game>/TerraInvicta_Data/Managed/Assembly-CSharp.dll` |
| Ships bundle | `<game>/TerraInvicta_Data/StreamingAssets/AssetBundles/ships` |
| BK deploy folder | `<game>/Mods/Enabled/ExpandedFleetsAndNavies/` |
| DLL source | `<repo>/src/` |
| Extracted working project | `<unity-work>/TI_Ships_Extracted/ExportedProject/` |
| Recovery project (never modify) | `<unity-work>/TI_Recovery/ExportedProject/` |
| Bundle output | `<bundle-out>/` |
| Vanilla prefabs | `<unity-work>/TI_Ships_Extracted/ExportedProject/Assets/GameObject/Battlecruiser.prefab` (475 total) |
| Decompiled scripts | `<unity-work>/TI_Ships_Extracted/ExportedProject/Assets/Scripts/Assembly-CSharp/PavonisInteractive/TerraInvicta/` |
| AnimatorControllers | `<unity-work>/TI_Ships_Extracted/ExportedProject/Assets/AnimatorController/Earth_Fin_Radiator.controller` |
| GameAssemblies (recovery) | `<unity-work>/TI_Ships_Extracted/AuxiliaryFiles/GameAssemblies/Assembly-CSharp.dll` |
| Player.log | `<SteamLibrary>/steamapps/compatdata/1176470/pfx/drive_c/users/steamuser/AppData/LocalLow/Pavonis Interactive/TerraInvicta/Player.log` |
| Workshop content root | `<SteamLibrary>/steamapps/workshop/content/1176470/` |
| Expanse Ships Mod workshop | `<SteamLibrary>/steamapps/workshop/content/1176470/3490333915/` (mod ID `3490333915`) - reference for working custom hull integration |
| Expanse Ships Mod DLL | `<SteamLibrary>/steamapps/workshop/content/1176470/3490333915/ExpanseShipsModActual.dll` - decompile with dnSpy for canonical custom-hull DLL pattern |
| Expanse Ships Mod bundle | `<SteamLibrary>/steamapps/workshop/content/1176470/3490333915/expanseshipsmod` - UnityPy comparison reference for material/texture/shader serialization |

---

## 11. dnSpy Token Reference

### Ship Visual Chain

| Token | Type.Method | BK Patch |
|---|---|---|
| - | `AssetLoader.InstantiatePrefab` | - (components baked in bundle) |
| - | `ShipModelController.BuildShip` | - (orchestrator) |
| - | `HumanShipController.SetSkin` | `SetSkinSkipPatch` (Prefix returns false for Hybrid/FullCustom - preserves edit-time baked materials per §18) |
| - | `HumanShipController.SetRadiators` | - |
| - | `ShipModelController.SetRadiatorEmissiveKelvinRange` | - |
| `0x0600254F` | `ShipModelController.BuildDrives` | `DriveVariantPatch` (Postfix - activates the matching baked variant child) |
| `0x06000F15` | `TIDriveTemplate.modelResource()` | `DriveVisualPatch` (Postfix - for FullCustom replaces path with `<bundle>/<drivePrefab>`; for Hybrid/PatchOnly with `vanillaDriveDataName` substitutes the alias) |
| `0x0600254A` | `ShipModelController.SetDrive` | - |
| `0x06002551` | `ShipModelController.BuildWeapons` | - |
| `0x06002543` | `ShipModelController.AddExplosions` | - |

### Per-Hull Controllers

| Token | Controller | Hull(s) using |
|---|---|---|
| `0x060024D1` | `BattlecruiserController.SlotToWeaponMountIndex` | Battlecruiser (ScoutCruiser bundle) |
| `0x060024ED` | `LancerController.SlotToWeaponMountIndex` | Lancer (NewBattleCruiser bundle) |
| - | `TitanController.SlotToWeaponMountIndex` | Titan (NewBattleShip bundle) |
| - | `DreadnoughtController.SlotToWeaponMountIndex` | HeavyCruiser (PatchOnly alias to Dreadnought) |

### Asset System

| Token | Type.Method | Purpose |
|---|---|---|
| `0x06001DE6` | `AssetBundleManager.Initialize` | Loads all bundles into dict |
| `0x06001DE7` | `AssetBundleManager.LoadAsset<T>` | `"bundle/asset"` → split → load |
| `0x06005A39` | `ModManager.GetEnabledModFiles` | `.manifest` = bundle discovery |

### Hab Module Patches

| Token | Type.Method | BK Patch |
|---|---|---|
| `0x06003F73` | `TIHabState.IsModuleAllowedForHab` | BuildLimitPatch (F1/F2) |
| - | `TIFactionState.GetCurrentMiningMultiplierFromOrgsAndEffects` | MiningPatch (F3) |
| - | `TIFactionState.get_SafeMineNextworkSize` | MiningPatch (F4) |

### Nozzle/Weapon/VFX

Nozzle: `TIDriveTemplate.nozzle` (`0x06000F20`), `.nozzleStr` (`0x06000F1A`).
Mount enum: `WeaponSlotSet` (`0x06001218`).
VFX: `ships/HumanThrusterBasic`, `ships/HumanThrusterAdvanced`, `ships/AlienThruster`, `ships/NuclearThruster`.

---

## 12. Log Patterns

All BK logs are prefixed with `[ExpandedFleetsAndNavies]` (added by `Main.Log`/`Warning`/`Error`).

### BK Startup Logs (one-line summaries)

| Pattern | Meaning |
|---|---|
| `"ConfigReader: scanned N file(s), loaded N module config(s)."` | Hab module custom-field parsing complete |
| `"ConfigReader: no custom hab fields found. Patches will have no effect."` | No hab module configs found in any mod folder |
| `"HullRegistry: scanned N file(s), N hull(s) registered."` | HullDefinitions.cfg parsing complete |
| `"HullRegistry: parsed <dataName> from <mod>, ..."` | Per-hull successful parse |
| `"ControllerRegistry: scanned N file(s), ..."` | ControllerDefinitions.cfg parsing complete |
| `"ControllerRegistry: parsed <controllerClass> from <mod>"` | Per-controller successful parse |
| `"ControllerRegistry: patched N controller(s) for weapon slot remapping."` | Dynamic prefix attached to each registered controller's `SlotToWeaponMountIndex` |

### BK Warnings/Errors (per-entry parse issues)

| Pattern | Meaning |
|---|---|
| `"<Registry>: entry from '<mod>' missing '<field>' - skipped."` | Required field absent in cfg entry |
| `"HullRegistry: <dataName> from '<mod>' has no bundleName, drivePrefab, or vanillaDriveDataName - skipped."` | Useless entry (none of the three mode-determining fields set) |
| `"HullRegistry: <dataName> from '<mod>' has drivePrefab without bundleName - skipped."` | Invalid combination |
| `"HullRegistry: <dataName> from '<mod>' is FullCustom mode - cross-bundle shader rendering is fragile."` | Parse-time warning on FullCustom hulls |
| `"HullRegistry: dataName '<X>' from <mod> already registered - first-wins (skipped)."` | Duplicate dataName collision |
| `"ControllerRegistry: SlotToWeaponMountIndex not found ..."` | controllerClass typo or game update renamed class |
| `"ConfigReader: '<field>' on '<dataName>' is <type>, expected Integer - ignored."` | Wrong-type value in custom hab field |
| `"ConfigReader: '<field>' on '<dataName>' is negative (<n>) - ignored."` | Negative value rejected |

### BK Runtime Patch Logs

| Pattern | Meaning |
|---|---|
| `"DriveVisualPatch: <originalPath> → <remappedPath>"` | Drive path redirect (logged once per unique remap pair) |
| `"DriveVariantPatch: no baked variant '<name>' on <hull>"` | DriveVariantPatch couldn't find a matching child for the equipped drive's variant |

### TI (vanilla) Log Patterns Worth Knowing

| Pattern | Meaning |
|---|---|
| `"Bundle not found! bundle name: <n>"` | Bundle not loaded - check manifest deployment |
| `"No asset found for <bundle/asset>"` | Bundle loaded but asset absent - wrong asset name or bundle didn't include it |
| `"Invalid asset path \"<path>\" expected BUNDLE/ASSET format"` | Path missing `/` separator |
| `"Bad hull module placement"` / `"Bad utility module placement"` | Slot array vs save mismatch (see §9) |
| `"Bad requiredProjectName: X for ship part Y"` | requiredProjectName references nonexistent project |

### Diagnostic Flow

1. `grep "[ExpandedFleetsAndNavies]" Player.log` - did mod load?
2. Check `ConfigReader: scanned`, `HullRegistry: scanned`, `ControllerRegistry: scanned` counts.
3. Vanilla build chain: `"ADDING HULL MODEL"` → `"FINISHED ADDING OBJECTS"` → `"ADDING WEAPONS"` → `"ASSIGNING THRUSTER POINTS"` → `"BUILDING DRIVES"` → `"Building Weapon"`
4. If crash after `"ADDING OBJECTS"` → child naming or nesting wrong.
5. If crash at `"ADDING WEAPONS"` → weapon mount structure incomplete (missing Gun or FirePoint child).
6. If crash at combat entry → `_ExplosionSequenceRoot` missing sub-components OR `DamageLayer._shipRenderers` array empty/null.

---

## 13. Console Commands

| Command | Use |
|---|---|
| `giveproject [Name],ResistCouncil` | Unlock project (faction = `ResistCouncil` not `Resistance`) |
| `givetech [dataName]` | Unlock technology |

No instant hab construction command. Use `buildTime_Days: 1` for testing.

**Refinery unlock chain:** `Project_PlatformCore` → `Project_OutpostCore` → `Project_Refinery`
**Fleet Center chain:** `Project_MissionControlNode` → `Project_OperationsCenter` → `Project_CommandCenter` → `Project_FleetCenter`
**BK ship-progression chain:** `Project_Warships` → `Project_PatrolCraft` → `Project_PatrolVessels` → `Project_LightEscorts` → `Project_FleetCombatants` → `Project_Cruisers` → `Project_LightCruisers` → `Project_HeavyCruisers` → `Project_ShipsoftheLine` → `Project_BKBattleships` → `Project_VanillaShipsLoophole`

---

## 14. Radiator Animation System

Three shared human AnimatorControllers used across ALL hulls:
- `Earth_Fin_Radiator` - fin radiators (Radiator3/4/6/8/9/12/130/430/730/1030)
- `Earth_Droplet_Radiator` - droplet radiators (Droplet4/8/12)
- `Earth_Spikes_Radiator` - spike radiators (spikes 3/6/9/12)

Each contains one state ("Extend") with one animation clip. The clip contains compressed joint transform data (13-274 generic bindings depending on radiator type, 35-joint bone chains for fins).

Controllers are present as standalone assets in the AssetRipper-extracted project at `Assets/AnimatorController/`. Baked into prefab Animator components - no manual extraction needed.

### SetRadiators flow

1. `WhichRadiators(ship)` → list of active radiator GOs (based on `radiatorTemplate.radiatorType` + waste heat threshold)
2. `SetActive(true/false)` on each radiator GO
3. `GetComponent<Animator>()` on active GOs → `radiatorAnimators` list
4. `.Play("Extend", 0, 1f)` on each (fully-extended state, normalized time = 1)
5. `GetComponent<ColorAnimationEffect>()` on active GOs → `radiatorEmissivesFx` list
6. `SetRadiatorEmissiveKelvinRange()` → `SetColors()` on each - drives the `_EmissionColor` shader uniform

---

## 15. ColorAnimationEffect Fields

| Field | Type | Default | Notes |
|---|---|---|---|
| `m_useScaledGameTimeCheck` | `bool` (private, [SerializeField], [Tooltip]) | false (default) | Checked OnEnable only |
| `m_squareIntensity` | `bool` (private, [SerializeField]) | `true` | - |
| `m_blendMode` | `ColorBlendMode` (private, [SerializeField]) | `MULTIPLICATIVE` | enum {OVERRIDE=0, ADDITIVE=1, MULTIPLICATIVE=2} |
| `m_colorAnimation` | `Gradient` (private, [SerializeField], [GradientUsage(true)]) | Serialized by Unity when baked | Crash chain if null: `SetColors()` → `m_colorAnimation.SetKeys()` → NullRef |
| `m_intensityAnimation` | `AnimationCurve` (private, [SerializeField]) | `new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0f))` | - |
| `m_duration` | `float` (private, [SerializeField]) | `1f` | - |
| `m_targetRenderers` | `Renderer[]` (private, [SerializeField]) | `new Renderer[0]` | - |
| `m_targetUniformName` | `string` (private, [SerializeField]) | `"_EmissionColor"` | - |

Internal:
- `m_targetUniform` (int) - set in `Awake()` via `Shader.PropertyToID(m_targetUniformName)`
- `m_targetMaterials` (`List<(Material, Color)>`) - populated in `OnPlay`
- `m_progress` (float), `m_reversed` (bool), `m_useScaledTime` (bool), `m_gameTime` (GameTimeManager) - runtime state

Awake() sets `m_targetUniform = Shader.PropertyToID(m_targetUniformName)` - runs automatically.

---

## 16. ShipTools Editor Menu

`Assets/Editor/ShipTools.cs` in the extracted Unity project. Editor-only - never included in bundles.

**Class structure**: top-level `public class ShipTools` (no namespace), plus 5 nested/co-defined types: `Importer` (nested AssetPostprocessor), `CreateHullWindow`, `FactionSkinWindow`, `FactionPromptWindow`, `DrivePrefixPromptWindow` (all EditorWindows).

### Menu hierarchy (9 items in 3 groups)

**Setup group** (one-time / occasional maintenance):

| # | Menu Path | Source Method | Action |
|---|---|---|---|
| 1 | `Ship Tools/Setup/1. Strip Vanilla Bundle Tags` | `StripVanillaBundleTags()` | Run once per extracted project. Clears bundle tags from all vanilla extracted assets, keeps BK tags intact. **BK detection**: prefab at `Assets/*.prefab` root (not in `/GameObject/` or `/Prefab/` subfolders). Prevents 40-minute full-game rebuild on first Build. Safe to re-run. |
| 2 | `Ship Tools/Setup/2. Disable Streaming on All Textures` | `DisableStreamingAllTextures()` | Iterates ALL `t:Texture2D` assets, sets `streamingMipmaps = false` on each TextureImporter. Uses `StartAssetEditing()/StopAssetEditing()` for batched performance. Shows progress bar every 250 assets. Tracks `changed`/`already-off`/`error` counts. |
| 3 | `Ship Tools/Setup/3. Reimport Hull Textures as DXT` | `ReimportHullTexturesAsDxt()` | Iterates ALL `t:Texture2D`, filters via `Importer.IsHullTexture(path)` (path starts with `Assets/Texture2D/` OR contains `/Resources/objects/`). Applies `Importer.ApplyDxtFormat`: DXT5 if alpha, DXT1 otherwise; Standalone platform setting overridden + Compressed. Reports `scope/conv/ok/err` counts. |
| 4 | `Ship Tools/Setup/4. Write Drive Prefix Sidecars (one-time fix)` | `WriteDrivePrefixSidecars()` | One-time backfill for legacy hulls (Battlecruiser, Lancer, Titan) that predate the `.driveprefix` sidecar pattern. Writes correct prefix values so `BakeDriveVariants` can resolve `Earth_<prefix>_<variant>` meshes. Idempotent. |

**Hull group** (per-hull authoring - note source order: 1, 3, 2 - but menu order is 1, 2, 3):

| # | Menu Path | Source Method | Action |
|---|---|---|---|
| 5 | `Ship Tools/Hull/1. Create Hull From Vanilla` | `CreateHullFromVanilla()` → `CreateHullWindow.Open()` → `ExecuteCreateHull()` | Select vanilla `.prefab` in Project panel → modal-style EditorWindow (420×170 fixed) with `ShowUtility()` (because `ShowModal()` is broken on Linux Unity 2020.3.49f1 - see §20). Window has 3 fields: Hull Name (focused on open, Enter submits), Bundle (auto = `hullName.ToLower()`, disabled), Faction Skin (popup, default = `resist` index 5). On Create: `EnsureBundleSettings()` → instantiate prefab → unpack completely → rename body mesh parent (`Earth_Hull_*` or `Hull_*` → `Hull`) → save as `Assets/<hullName>.prefab` → tag bundle → `WriteFactionSidecar()` and `WriteDrivePrefixSidecar()` BEFORE bake → `BakeFactionSkin()`. |
| 6 | `Ship Tools/Hull/2. Add Weapon Mount` | `AddWeaponMount()` | Multi-select duplicate of existing weapon mount(s) in Hierarchy. Per-mount validation: must have parent, must have ≥1 child (Gun), Gun must have ≥1 child (FirePoint). `FieldNameForMount(name)` matches lowercased name against substrings: `dorsal` → `dorsalHullWeaponControllers`, `ventral` → `ventralHullWeaponControllers`, `nose` → `noseWeaponControllers`. `GenerateNextName()` parses trailing space-separated number, increments past highest sibling. Duplicates with `Object.Instantiate(src, parent)` → renames root `<core> <n>` → renames Gun child `<newName> Gun` → renames FirePoint grandchild → `SetSiblingIndex(srcIdx + 1)` (sibling order = weapon array index in hierarchy). `RegisterMountInController()` finds the ship controller via `FindShipController()` (component on root with all 3 weapon arrays in SerializedObject), inserts the new `ShipWeaponVisController` into the matching SerializedProperty array. |
| 7 | `Ship Tools/Hull/3. Set Faction Skin` | `SetFactionSkin()` → `FactionSkinWindow.Open()` → `ApplyFactionSkinChange()` | Re-bake an existing hull's faction skin. Selection accepts both Project-panel asset OR Hierarchy instance (uses `PrefabUtility.GetCorrespondingObjectFromSource()` for instance case). Reads existing sidecar via `ReadFactionSidecar(path)` to pre-select dropdown to current faction (defaults to `resist`). FactionSkinWindow is 360×120 fixed-size with Apply/Cancel. Apply calls `WriteFactionSidecar()` + `BakeFactionSkin()` + `AssetDatabase.SaveAssets()`. |

**Root group** (build pipeline):

| # | Menu Path | Source Method | Action |
|---|---|---|---|
| 8 | `Ship Tools/Verify Hull` | `VerifyHull()` → `RunHullChecks()` | Select hull root in Hierarchy. Comprehensive check battery: GraphicsSettings (`BundleSettingsOk`), all `RequiredChildren` present, Hull child non-empty, root has `CapsuleCollider` + `DamageLayer` + correct Layer (`LayerIgnoreRaycast`=2), all 10 FinRadiators + 4 SpikeRadiators + 3 DropletRadiators, every drawable MeshRenderer has texture (counts: drawable/skipped/textured/null/no-albedo), Drive child has ThrusterPoints + correct Layer (`LayerHurtBox`=17), Vector Thrusters count, weapon mount counts (nose/dorsal/ventral) with dorsal == ventral symmetry warning, controller arrays via `CheckArray()` (NULL = error, DUPLICATE = error "renders white"). Reports bundle tag and sidecar status. Output via `PrintReport()`. |
| 9 | `Ship Tools/Finalize and Ship` | `FinalizeAndShip()` | Single-click pipeline. Six stages: (1) `EnsureBundleSettings()`; (2) `AssetDatabase.SaveAssets() + Refresh()`; (3) Faction-skin re-bake on every hull prefab in any tagged bundle (reads `.faction` sidecar; if missing, prompts via 3-way `DisplayDialogComplex`: Use `resist` default / Skip / Pick another via `FactionPromptWindow` polling-blocking dialog); (4) Drive-variant bake via `BakeDriveVariants` (reads `.driveprefix` sidecar; if missing, prompts inline via `DrivePrefixPromptWindow` Apply/Skip/Cancel); (5) Build with `ForceRebuildAssetBundle | DeterministicAssetBundle` for `BuildTarget.StandaloneWindows64` to `BundleOutPath`; (6) Deploy bundles + `.manifest` files to `DeployPath` with overwrite. Per-stage logs and a final summary dialog. |

### Constants and config

```csharp
static readonly string BundleOutPath = "<bundle-out>";
static readonly string DeployPath    = "<game>/Mods/Enabled/ExpandedFleetsAndNavies";

const int LayerIgnoreRaycast = 2;       // Unity built-in; root expected
const int LayerHurtBox       = 17;      // Custom; Drive child expected

const string FactionSidecarExt     = ".faction";
const string DrivePrefixSidecarExt = ".driveprefix";

static readonly string[] RequiredChildren = {
    "Hull", "Drive", "_ExplosionSequenceRoot",
    "SelectionReticle", "GroupSelectionReticle", "Padlock Container"
};

static readonly string[] FinRadiators = {           // 10 names
    "Radiator12", "Radiator3", "Radiator130", "Radiator6", "Radiator4",
    "Radiator430", "Radiator730", "Radiator1030", "Radiator8", "Radiator9"
};
static readonly string[] SpikeRadiators   = { "spikes 12", "spikes 3", "spikes 6", "spikes 9" };
static readonly string[] DropletRadiators = { "Droplet12", "Droplet8", "Droplet4" };

public static readonly string[] FactionSuffixes = {
    "appease", "cooperate", "destroy", "escape", "exploit", "resist", "submit"
};
public const int DefaultFactionIndex = 5;           // "resist"

const string FieldNoseWeapons    = "noseWeaponControllers";
const string FieldDorsalWeapons  = "dorsalHullWeaponControllers";
const string FieldVentralWeapons = "ventralHullWeaponControllers";
```

`FactionSuffixes` are lowercase. Material naming pattern: `Assets/Material/MAT_<childRendererName>_<lowercaseFaction>.mat`.

### Sidecar files

ShipTools writes two sidecar files next to each hull prefab to persist edit-time state across rebuilds.

| Sidecar | Records | Read by |
|---|---|---|
| `<hullname>.faction` | Last-baked faction suffix (e.g. `resist`) | `BakeFactionSkin` |
| `<hullname>.driveprefix` | Drive variant prefix (e.g. `Battlecruiser`) | `BakeDriveVariants` |

Both follow the same API pattern:
- `SidecarPathFor(prefabPath)` / `DrivePrefixSidecarPathFor(prefabPath)` - builds `<dir>/<name>.<ext>` path
- `Write*Sidecar(path, value)` - writes value + calls `AssetDatabase.ImportAsset`
- `Read*Sidecar(path)` - returns trimmed value or null (with try/catch)
- `PromptAndCreateSidecar(path)` / `DrivePrefixPromptWindow.Open()` - prompts when build hits a sidecar-less hull

Sidecars are written BEFORE the bake in `ExecuteCreateHull` so first build has them in place. The `Finalize and Ship` pipeline auto-detects missing sidecars and prompts the user once per hull, then proceeds.

### Importer (nested AssetPostprocessor)

`ShipTools.Importer : AssetPostprocessor` - Unity automatically calls this on every texture import. `OnPreprocessTexture` always disables `streamingMipmaps`, then calls `ApplyDxtFormat()` if `IsHullTexture()` matches. Hull textures imported AFTER ShipTools.cs is added to the project automatically get the right settings - no manual reimport for new textures.

### EditorWindow workarounds for Linux Unity

`FactionPromptWindow.ShowBlocking()` and `DrivePrefixPromptWindow.ShowBlocking()` use polling instead of `ShowModal()`:

```csharp
w.ShowUtility();
while (!w.done)
{
    w.Repaint();
    System.Threading.Thread.Sleep(50);
}
```

`ShowModal()` is broken on Linux Unity 2020.3.49f1 (deadlocks the editor - see §20). The polling approach with 50ms sleeps + manual `Repaint` avoids it. `OnDestroy` sets `done = true` so window-close-via-X also unblocks.

### Workflow summary

1. **One-time setup**: Setup/1 (strip tags) → Setup/2 (disable streaming) - only on first opening of extracted project. Setup/3 (DXT reimport) is run if hull textures need format fixing. Setup/4 is a one-time backfill for legacy hulls and can be removed once no longer needed.
2. **Per-hull authoring**: Hull/1 (Create from Vanilla - opens window, picks faction, writes sidecars, auto-bakes) → manually arrange weapon mounts in Hierarchy → Hull/2 (Add Weapon Mount) for each desired mount → save scene to prefab.
3. **Per-build**: Verify Hull (debug check) → Finalize and Ship (one click - re-bakes faction + drive variants, builds, deploys).
4. **Per-faction-change**: Hull/3 (Set Faction Skin) → updates sidecar + re-bakes immediately.

---

## 17. SlotToWeaponMountIndex Reference (Decompiled)

### BattlecruiserController

```csharp
public override int SlotToWeaponMountIndex(int slot, Mount mount)
{
    switch (slot)
    {
    case 7:  return mount switch { Mount.OneNose => 1, _ => 0 };
    case 8:  switch (mount) {
                 default: return 3;
                 case Mount.TwoHullVert:
                 case Mount.TwoNoseHoriz:
                 case Mount.ThreeNoseAngle: return 0;
             }
    case 9:  switch (mount) {
                 default: return 2;
                 case Mount.TwoHullVert:
                 case Mount.TwoNoseHoriz:
                 case Mount.ThreeNoseAngle: return 0;
             }
    case 11: return 0;
    case 14: return 1;
    default: return 0;
    }
}
```

Large mount types (TwoHullVert, ThreeNoseAngle) at slots 8/9 redirect to the center nose mount (index 0) because they physically can't fit at the small nose positions (indices 2/3).

### LancerController

```csharp
public override int SlotToWeaponMountIndex(int slot, Mount mount)
{
    switch (slot)
    {
    case 7:
        if ((uint)(mount - 11) <= 1u) return 0;  // values 11, 12 in declaration order
        return 4;
    case 8:
    case 9:
    case 10:
        switch (mount)
        {
            case Mount.TwoNoseHoriz:
            case Mount.ThreeNoseAngle:
            case Mount.FourNose: return 0;
            default:
                // case 8 → 2, case 9 → 3, case 10 → 1
                return slot == 8 ? 2 : slot == 9 ? 3 : 1;
        }
    case 11: return 0;
    case 14: return 1;
    case 18: return 2;
    default: return 0;
    }
}
```

(Note: actual decompiled source has the cases as separate switch blocks; the consolidated form above is for readability. See actual decompiled file for exact branch structure.)

### TitanController

```csharp
public override int SlotToWeaponMountIndex(int slot, Mount mount)
{
    switch (slot)
    {
    case 7:
    case 8:
    case 9:
    case 10:
        if ((uint)(mount - 9) <= 3u) return 0;  // values 9, 10, 11, 12
        // case 7 → 1, case 8 → 3, case 9 → 2, case 10 → 4
        return slot == 7 ? 1 : slot == 8 ? 3 : slot == 9 ? 2 : 4;
    case 12: return 0;
    case 13: return 1;
    case 16: return 2;
    case 17: return 3;
    case 20: return 4;
    case 21: return 5;
    default: return 0;
    }
}
```

### DreadnoughtController

```csharp
public override int SlotToWeaponMountIndex(int slot, Mount mount)
{
    switch (slot)
    {
    case 7: return 0;
    case 8: if (mount == Mount.ThreeNoseAngle) return 0; return 2;
    case 9: if (mount == Mount.ThreeNoseAngle) return 0; return 1;
    case 16: return 6;
    case 17: return 7;
    case 18: return 0;
    case 19: return 1;
    case 20: return 2;
    case 21: return 3;
    case 22: return 4;
    case 23: return 5;
    default: return 0;
    }
}
```

### Alien Controllers (vanilla - for reference)

Alien hulls inherit from `AlienShipController` (parallel hierarchy to `HumanShipController`). They are not touched by BK.

**AlienBattlecruiserController** - slots 7,8,9,10,13,16 with mount-conditional logic and a `Log.Warn` on fallthrough for diagnostic visibility.

**AlienBattleshipController** - slots 7,8,9,10,12,13,16,17 with `Mount.OneNose` special handling on slots 7-8.

**AlienCruiserController** - overrides `WhichRadiators` with waste-heat-aware logic (uses radiator1030/130 for low heat, all four for high heat).

---

## 18. Edit-Time Material Bake Workflow

### Why bake at edit time

For Hybrid and FullCustom hulls, `SetSkinSkipPatch` returns `false` from vanilla `SetSkin`, so vanilla never assigns materials at runtime. Materials must already be on the prefab's MeshRenderers when the bundle is built.

The shader binding problem (see §4 Bundle Build Requirements) prevents materials without baked shader code from rendering correctly. Edit-time baking is the only mechanism that produces correct rendering for mod-bundle hulls.

**Bake = assign materials to MeshRenderer.sharedMaterial in the prefab asset, save, rebuild bundle.** The material, its texture, and its shader are all pulled in as bundle dependencies and serialized together.

### Sidecar memory

ShipTools writes a `<hullName>.faction` text file alongside `<hullName>.prefab` containing the faction string used for the most recent bake. The `Finalize and Ship` pipeline auto-rebakes from sidecar; if missing, prompts once with default fallback to `resist`. Avoids re-selecting faction on every rebuild. See §16 for full mechanism.

### Identity via filename

The bake tool walks every MeshRenderer in the prefab and tries to load `Assets/Material/MAT_{child.gameObject.name}_{faction}.mat`. If the file exists, assign it. If not, skip.

This matches vanilla's own SetSkin convention (`MAT_{name}{suffix}`). The material filename carries the identity - no subtree containers, no canonical structure, no rename step, no pattern detection. Works on any hull regardless of body mesh naming convention (`Earth_Hull_*` or `Hull_*`).

**Filter effect:** Only children whose names match a real material file get baked. Weapon-placeholder Hull children (`base_medium`, `medium_railgun_battery`), radiators, drives, and other non-body geometry have no matching faction material and are left untouched - they keep whatever material they already have from vanilla.

### EditPrefabContentsScope pattern

Unity's canonical pattern for batch-editing a prefab asset without instantiating it in a scene (Unity 2020.1+). Loads the prefab into an isolated preview scene, returns the root GameObject for editing, and on `Dispose()` auto-saves to the .prefab file and unloads the preview scene. Exception-safe - scope cleans up even if the edit code throws.

```csharp
using (var scope = new PrefabUtility.EditPrefabContentsScope(assetPath))
{
    GameObject root = scope.prefabContentsRoot;
    foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>(true))
    {
        string matName = $"MAT_{mr.gameObject.name}_{faction}";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Material/{matName}.mat");
        if (mat == null) continue;
        mr.sharedMaterial = mat;
    }
} // auto-saves and unloads here
```

**Use `sharedMaterial` not `material`.** `.material` is forbidden on prefab assets - it clones the material at edit time which breaks the reference chain. `.sharedMaterial` assigns the reference directly.

**No manual `AssetDatabase.SaveAssets()` needed** - scope disposal handles the write. No Undo either (preview scene is destroyed on scope exit; Undo doesn't track preview scene changes).

### Baked bundle size

Bundle size with baked materials is larger than expected. For Battlecruiser, the baked ScoutCruiser bundle is **115MB** (vs. ~50MB estimate in early planning). This is because AssetRipper's "Group By Asset Type" extraction mode resolves materials from multiple faction bundles into one project, and Unity's dependency walker may include dependency chains beyond the single faction's materials. The bundle still loads correctly in TI - size is an optimization concern, not a correctness concern.

### Multi-faction deployment

With `factionSkin: None` and an edit-time bake of one faction's materials (e.g., `resist`), all players see the same faction appearance regardless of their faction. This is a simplification vs. vanilla's per-faction rendering - acceptable for BK's scope.

For per-faction appearance in a future expansion: bake each faction separately into its own prefab variant, build as separate bundles or bundle variants, and load the correct variant based on `ship.designingFaction` at registration time. Not implemented.

---

## 19. Unity Editor API Reference (BK-Relevant)

### Prefab Asset Editing

| API | Purpose | Notes |
|---|---|---|
| `PrefabUtility.InstantiatePrefab(prefab)` | Creates scene instance of a prefab asset | Preserves GUID-based material refs across unpack and rename |
| `PrefabUtility.UnpackPrefabInstance(go, Completely, Action)` | Fully disconnects instance from prefab source | Doesn't break material GUID refs |
| `PrefabUtility.SaveAsPrefabAsset(go, path)` | Saves scene GO as prefab asset (disconnected) | Scene instance is no longer a prefab instance |
| `PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, action)` | Saves and keeps scene instance connected | Changes on instance become prefab overrides, applied back via standard Unity machinery |
| `PrefabUtility.EditPrefabContentsScope` | Canonical batch-edit of prefab asset | Disposable struct - auto-loads, auto-saves, auto-unloads. Unity 2020.1+. Exception-safe. |
| `PrefabUtility.LoadPrefabContents(path)` | Lower-level manual load into preview scene | **Crashes editor on non-existent path** - validate with `AssetDatabase.LoadAssetAtPath<GameObject>(path) != null` first. Manual pair: `LoadPrefabContents` → edit → `SaveAsPrefabAsset` → `UnloadPrefabContents`. Prefer the Scope version. |
| `PrefabUtility.IsPartOfPrefabAsset(go)` | True if GO is a prefab asset (not instance) | Canonical menu validator for asset-mode operations. Greys out menu when a scene instance or nothing is selected. |

### Menu Validator Pattern

```csharp
[MenuItem("Ship Tools/Hull/3. Set Faction Skin")]
static void BakeFactionSkin() { /* ... */ }

[MenuItem("Ship Tools/Hull/3. Set Faction Skin", true)]
static bool BakeFactionSkinValidate()
{
    GameObject go = Selection.activeGameObject;
    return go != null && PrefabUtility.IsPartOfPrefabAsset(go);
}
```

The second `MenuItem` with `validate: true` is called before the menu renders. Returning false greys out the menu item. `Selection.activeGameObject` returns the prefab asset root for Project-panel selections.

### AssetBundle Building

| Flag | Effect |
|---|---|
| `ForceRebuildAssetBundle` | Rebuild even if cache says content is unchanged |
| `DeterministicAssetBundle` | Stable internal IDs across rebuilds - recommended for iteration |
| `ChunkBasedCompression` | LZ4 (chunk-based) instead of LZMA (full-file). Recommended for local bundles loaded via `LoadFromFile`. Not currently used by BK - LZMA default works. |
| `UncompressedAssetBundle` | No compression. Largest file, fastest load. Not recommended. |

### SerializedObject pattern for ProjectSettings

```csharp
var settingsAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
var so = new SerializedObject(settingsAsset);
// ... modify serialized properties ...
so.ApplyModifiedProperties();
EditorUtility.SetDirty(so.targetObject);
AssetDatabase.SaveAssets();
```

Used by ShipTools `EnsureBundleSettings()` to modify the Always Included Shaders list and Instancing Variants setting programmatically.

### UnityPy Bundle Inspection (External Tool)

UnityPy is a Python library that reads Unity AssetBundle files directly without going through the Unity editor. Install: `pip install --user UnityPy`.

This is the canonical "what's actually in the bundle?" diagnostic and is the only way to verify bundle contents independent of Unity's serialization layer. Use whenever you suspect the bundle contains something different from what the project shows.

**List all object types in a bundle:**
```python
import UnityPy
env = UnityPy.load('/path/to/bundle')
from collections import Counter
print(Counter(o.type.name for o in env.objects))
```

**Inspect a material's keywords, shader, and texture references:**
```python
import UnityPy
env = UnityPy.load('/path/to/bundle')
for o in env.objects:
    if o.type.name == 'Material':
        d = o.read()
        if 'YourMaterialName' in d.m_Name:
            print(f'name: {d.m_Name}')
            print(f'keywords: {repr(d.m_ShaderKeywords)}')
            print(f'shader: {d.m_Shader}')
            for k, v in d.m_SavedProperties.m_TexEnvs:
                print(f'  {k}: {v.m_Texture}')
            break
```

**Inspect a texture's streaming flag:**
```python
import UnityPy
env = UnityPy.load('/path/to/bundle')
for o in env.objects:
    if o.type.name == 'Texture2D':
        d = o.read()
        flags = getattr(d, 'm_StreamingMipmaps', 'unknown')
        prio = getattr(d, 'm_StreamingMipmapsPriority', 'unknown')
        print(f'{d.m_Name}: streaming={flags} priority={prio}')
        break
```

**Comparison pattern:** run the same inspection script against both the BK bundle and the Expanse Ships Mod bundle (`<SteamLibrary>/steamapps/workshop/content/1176470/3490333915/expanseshipsmod`). Diff the outputs. Any field that differs is a candidate for investigation.

**Reading shader PathIDs:** PathIDs are stable hash-derived identifiers. If two bundles reference the same shader, the PathID will match exactly. Standard shader's PathID at the time of writing is `-4850512016903265157`. Matching PathIDs across bundles confirms they're referencing the same shader instance.

**Limitations:**
- UnityPy reads serialized state, not runtime state. It won't catch issues that only manifest at GPU sampling time.
- It can't verify whether a texture's mip levels actually load at runtime - only that the texture is present and has metadata indicating streaming-on or streaming-off.
- For runtime-only issues, the Unity editor Play Mode test is the next escalation level after UnityPy inspection.

---

## 20. Environment Quirks

### Linux Unity (Unity Editor 2020.3.49f1)

| Quirk | Symptom | Fix |
|---|---|---|
| `EditorWindow.ShowModal()` broken | First modal dialog works, second returns silently with null values | Use non-modal `EditorWindow.ShowUtility()` with single-field windows. Never chain modals. |
| AssetBundle build crypto errors | Roslyn culture crash during build | `openssl-1.1` (AUR) + `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` env var |
| Unity licensing (kernel 6.19+) | Cannot activate license | `patchelf --clear-execstack` on `libbindings.so` |
| Unity editor launch | - | `unity -projectPath <unity-work>/TI_Ships_Extracted/ExportedProject` (TI_Recovery project is empty - use TI_Ships_Extracted) |

### dnSpy (dnSpyEx under Wine)

| Issue | Fix |
|---|---|
| Wine 11.6 breaks .NET 8 build (`XmlLanguage.GetSpecificCulture` crash) | Use net-framework build at `~/Apps/dnspy-fw/dnSpy.exe` with `WINEPREFIX=~/.wine-dnspy` (winetricks `dotnet48`) |
| Black context menus | Set `DisableHWAcceleration=1` registry key in the dnspy wineprefix |
| Launcher | `/usr/bin/dnspy` |

### Fish shell

No heredoc support - use `echo '...' >` for multi-line writes, or use `view`/`create_file` tools to write files directly.

### UMM launch

Terra Invicta Steam launch options for UMM support:
```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

---

## 21. Save and Load Considerations

### Template Merge is Field-Level

BK's existing project file overrides like `{"dataName": "Project_Warships", "friendlyName": "Orbital Ships"}` (only 2 fields) work correctly without zeroing the other ~14 vanilla fields. Minimal overrides are safe throughout TI template files.

### Null-Safety per Template Type

| Template Type | Save Load Behavior | Implication |
|---|---|---|
| `TIProjectTemplate` | NULL-SAFE - `TemplateManager.Find<TIProjectTemplate>(name, false)` returns null silently if missing, and the load loop `if (tiprojectTemplate != null) availableProjects.Add(...)` skips them | Renaming/removing projects is save-compatible |
| `TITechTemplate` | NULL-SAFE - same pattern | Renaming/removing techs is save-compatible |
| `TIShipHullTemplate` | NOT NULL-SAFE - `TISpaceShipTemplate.hullTemplate` getter and other code paths access `.alien` and other properties without null checks | **Renaming hulls is SAVE-BREAKING** |
| `TIShipModuleTemplate` (drives, weapons, utilities, etc.) | NOT NULL-SAFE in some paths | Renaming may be save-breaking depending on which fields are touched |

### Save-Breaking Change Indicators

A change is save-breaking if it does any of:
- Renames a hull dataName (existing ships in saves reference old name)
- Removes a hull dataName (existing ships in saves NRE)
- Changes the type identity of a slot at a specific index in a hull's slot array
- Changes a hull's controller class type (saved designs reference old controller)

A change is save-compatible if:
- Adds new hulls (no existing reference)
- Adds new projects/techs
- Renames or removes projects/techs (null-safe at load)
- Adds or removes weapon slot entries at the END of a hull's slot array
- Modifies stat values, costs, prereqs (no slot-index implications)

### `requiredProjectName` Semantics for Save Compatibility

Recall from §2: `requiredProjectName == null` (or pointing at a missing project) → `requiredProject` resolves to null → `FactionCanBuild` returns true for human factions.

**Save implications:**
- Changing `requiredProjectName` from a real project to a missing project = unlocks the hull (UNINTENDED if you're trying to lock it)
- Changing from missing to a real project = locks the hull
- Either change is save-compatible in terms of crashing - but gameplay is affected

**To safely lock a hull:** point `requiredProjectName` at a real project that is hard to research. BK uses `Project_VanillaShipsLoophole` with restrictive parameters.

### `FactionCanBuild` Logic

Per `TIShipPartTemplate.cs:164-171`:
```csharp
public bool FactionCanBuild(TIFactionState faction)
{
    if (faction.IsAlienFaction || requiredProject != null)
    {
        return faction.completedProjects.Contains(requiredProject);
    }
    return true;
}
```

| Faction Type | requiredProject | FactionCanBuild Returns |
|---|---|---|
| Human | null | `true` (unlocked) |
| Human | non-null project, completed | `true` (unlocked) |
| Human | non-null project, not completed | `false` (locked) |
| Alien | null | `false` (`Contains(null)` = false) |
| Alien | non-null project, completed | `true` |
| Alien | non-null project, not completed | `false` |

The asymmetry between human and alien factions for null `requiredProject` is present in source. Alien hulls always have a real `requiredProjectName` in vanilla data so this rarely matters in practice.

---

## 22. Ship Asset Bundle and Engine Load Pipeline

This section documents the end-to-end runtime pipeline from disk to rendered ship - bundle discovery, prefab instantiation, controller wiring, animation systems, weapon firing. The goal is reproducible understanding of what TI/Unity needs to spawn and animate a ship hull.

### 22.1 Pipeline overview (the 30-second view)

```
DISK                         TI RUNTIME                            UNITY
────────────────────────────────────────────────────────────────────────
Mods/Enabled/                
  ExpandedFleetsAndNavies/            
    *.bundle                 
    *.bundle.manifest        
    *.json                   
                             
                  [Game start]
                             
                             ModManager.GetEnabledModFiles()
                              → scans Mods/Enabled/ recursively
                              → populates ModAssetBundles list
                              → populates ModAssetBundleManifestFiles
                              → populates jsonMods list
                             
                             AssetBundleManager.Initialize()
                              → loads vanilla AssetBundleManifest
                              → loads each vanilla bundle into loadedBundles
                              → loads each mod bundle into loadedBundles
                                                                    AssetBundle.LoadFromFile(path)
                                                                     → returns AssetBundle handle
                             
                  [Player builds ship via UI / AI builds ship]
                             
                             TISpaceShipState created
                              → references TISpaceShipTemplate
                              → which references TIShipHullTemplate
                              → which has modelResource[] strings
                             
                             ShipVisController.InitializeShipVisualizer(template, state, ...)
                              → GameControl.assetLoader.InstantiatePrefab(
                                  template.hullTemplate.modelResource[hullAppearanceIndex])
                                                                    AssetBundleManager.LoadAsset<GameObject>("bundle/Prefab")
                                                                     → loadedBundles["bundle"].LoadAsset<GameObject>("Prefab")
                                                                     → returns prefab GameObject
                                                                    Object.Instantiate(prefab, parent)
                                                                     → spawns scene instance
                              → modelLink = the new instance
                              → ModelController = modelLink.GetComponent<ShipModelController>()
                              → ModelController.BuildShip(this, template, state, false):
                                  ├─ SetSkin(ship)              
                                  ├─ BuildDrives(ship)            
                                  ├─ BuildVectorThrusters       (optional)
                                  ├─ SetRadiators(ship)         ← active/inactive per faction
                                  ├─ BuildWeapons(parent, ship, state)  
                                  ├─ AddExplosions()
                                  ├─ SetShadows(true)
                                  └─ gameTime = ...GetExistingManager<GameTimeManager>()
                             
                  [Per-frame Update loop while ship visible]
                             
                             ShipModelController.Update / FixedUpdate
                             ShipWeaponVisController.Update (per weapon)
                             RadiatorVisController (animation triggered by Animator)
                             ColorAnimationEffect.Update (color cycling)
                             DamageLayer.Update (shader uniform refresh)
```

**Key observation**: Bundle loading happens ONCE at game start (all bundles loaded eagerly). Prefab instantiation happens on-demand per ship via `GameControl.assetLoader.InstantiatePrefab`. There is no streaming, no lazy bundle load - everything is in memory.

### 22.2 Disk → Loaded bundle

#### File discovery (ModManager.cs:75-128)

`ModManager.GetEnabledModFiles()` runs once at game start when `TIPlayerProfileManager.useMods` is true:

```csharp
// Source: ModManager.cs:71-128 (paraphrased)
public List<string> GetEnabledModFiles()
{
    // Clear all static lists
    ModDirectories.Clear();
    ModAssetBundleManifestFiles.Clear();
    ModAssetBundles.Clear();
    ModNames.Clear();
    
    // Ensure folders exist
    if (!Directory.Exists("Mods/Enabled"))  Directory.CreateDirectory("Mods/Enabled");
    if (!Directory.Exists("Mods/Disabled")) Directory.CreateDirectory("Mods/Disabled");
    
    // Walk Mods/Enabled/
    foreach (string text in Directory.EnumerateDirectories("Mods/Enabled/").ToArray())
    {
        list.AddRange(Directory.GetFiles(text, "*.*", SearchOption.AllDirectories));
        ModDirectories.Add(text);
        ModNames.Add(text.Split("Mods/Enabled/")[1]);
    }
    
    // Identify bundles via .manifest files
    foreach (string file in list)
    {
        if (file.Contains(".manifest") && !file.Contains(".meta"))
        {
            ModAssetBundleManifestFiles.Add(file);
            ModAssetBundles.Add(file.Replace(".manifest", ""));  // bundle = manifest minus extension
        }
    }
    return list;
}
```

**Critical pattern**: TI identifies bundles by their **`.manifest` companion file**, NOT by the bundle file itself. The .manifest discovery happens first; the bundle path is derived by stripping `.manifest`. **A bundle without its .manifest is invisible to TI.** This is why `Deploy to Mod Folder` in ShipTools copies both files.

The `.meta` exclusion is to avoid Unity's editor-side `.meta` files (which also contain "manifest" sometimes) being picked up. Production bundles deployed by ShipTools don't have `.meta` files, so this exclusion is defensive.

JSON mods are loaded separately by `ModManager.LoadJsonMods()`, walking the same `GetEnabledModFiles()` results filtered by `.json` extension and excluding `ModInfo.json`.

#### Bundle loading (AssetBundleManager.cs:22-66) 

`AssetBundleManager.Initialize()` runs once at game start. Loads ALL bundles eagerly into a static dictionary:

```csharp
// Source: AssetBundleManager.cs:22-66 (paraphrased)
public static void Initialize()
{
    if (SimulateAssetBundleInEditor || loadedBundles != null)
        return;  // already initialized
    
    bundlePath = Path.Combine(Application.streamingAssetsPath, "AssetBundles");
    
    // Load vanilla manifest (the meta-manifest listing all vanilla bundles)
    manifest = AssetBundle.LoadFromFile(Path.Combine(bundlePath, "AssetBundles"))
        .LoadAsset<AssetBundleManifest>("AssetBundleManifest");
    string[] allAssetBundles = manifest.GetAllAssetBundles();
    
    loadedBundles = new Dictionary<string, AssetBundle>(allAssetBundles.Length);
    
    // Load each vanilla bundle
    for (int i = 0; i < allAssetBundles.Length; i++)
    {
        AssetBundle bundle = AssetBundle.LoadFromFile(Path.Combine(bundlePath, allAssetBundles[i]));
        loadedBundles.Add(allAssetBundles[i], bundle);
    }
    
    // Load DLC bundles
    if (ModManager.dlcDirectories.Count > 0 && ModManager.dlcAssetbundleManifestFiles.Count > 0) { ... }
    
    // Load mod bundles
    if (TIPlayerProfileManager.useMods && ModManager.ModDirectories.Count > 0
        && ModManager.ModAssetBundleManifestFiles.Count > 0)
    {
        List<AssetBundle> modBundles = new List<AssetBundle>();
        for (int l = 0; l < ModManager.ModAssetBundleManifestFiles.Count; l++)
        {
            modBundles.Add(AssetBundle.LoadFromFile(ModManager.ModAssetBundles[l]));
        }
        for (int m = 0; m < modBundles.Count; m++)
        {
            loadedBundles.Add(modBundles[m].name, modBundles[m]);
        }
    }
}
```

**Key facts**:
- **All bundles loaded eagerly at game start** - no lazy loading. Memory cost is upfront.
- **Mod bundles load AFTER vanilla bundles**. If a mod bundle has the same name as a vanilla bundle, `Add()` will throw a duplicate-key exception. (DLC bundles handle this via `ContainsKey` check + Remove + Add; mods do NOT.)
- **Bundle name comes from the bundle file itself** (`bundle.name`), not from the file path. Set by `AssetImporter.assetBundleName` at build time. ShipTools sets bundle name to `hullName.ToLower()` - so `Lancer.prefab` → bundle name `"newbattlecruiser"` (the bundle name on the file is what `LoadFromFile` reads).
- **No CRC verification at load time** - TI just calls `LoadFromFile` and trusts the bundle. The CRC in the .manifest is informational (used by Unity's editor for incremental builds, not by runtime).
- **`UnloadAssetBundle` exists** but `unloadAllLoadedObjects: false` means already-instantiated prefabs remain valid after unload. Useful for memory management but BK never calls it.

#### Asset retrieval (AssetBundleManager.cs:68-87) 

```csharp
public static T LoadAsset<T>(string assetPath) where T : Object
{
    if (!assetPath.Contains("/"))
    {
        Log.Error("Invalid asset path \"" + assetPath + "\" expected BUNDLE/ASSET format");
        return null;
    }
    string[] array = assetPath.Split('/');
    string text = array[0].ToLowerInvariant();  // bundle name lowercased
    string name = array[1];                      // asset name preserved
    if (loadedBundles.ContainsKey(text))
    {
        T val = loadedBundles[text].LoadAsset<T>(name);
        if (val == null) Debug.LogWarning("No asset found for " + assetPath);
        return val;
    }
    Debug.LogError("Bundle not found! bundle name: " + text);
    return null;
}
```

**Asset path format is `BUNDLE/ASSET`** with `/` separator:
- Bundle name is lowercased before lookup (matches Unity's bundle name convention)
- Asset name is preserved as-is (matches the prefab's actual name in the bundle)
- Example: `"newbattlecruiser/NewBattleCruiser"` - bundle "newbattlecruiser" → prefab "NewBattleCruiser"

See §12 for log messages from this method.

#### `GameControl.assetLoader.LoadAsset` and `InstantiatePrefab` 

`AssetLoader` (`AssetLoader.cs`, 83 lines, NOT in Pavonis namespace - global type). Plain class (no MonoBehaviour). Thin wrapper around `AssetBundleManager` with one minor cache for UI sprites.

**Key methods**:

```csharp
public T LoadAsset<T>(string asset) where T : Object
    => AssetBundleManager.LoadAsset<T>(asset);  // pure delegation

public T[] LoadAll<T>(string[] assetArray) where T : Object  // batch loop calling LoadAsset

public GameObject InstantiatePrefab(string asset)
{
    GameObject prefab = LoadAsset<GameObject>(asset);
    if (prefab == null) return null;
    return Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
}

public GameObject InstantiatePrefab(string asset, Transform parent)
{
    GameObject prefab = LoadAsset<GameObject>(asset);
    if (prefab == null) return null;
    return Object.Instantiate(prefab, Vector3.zero, Quaternion.identity, parent);
}

public void LoadAssetForImageAssignment(string asset, Image imageToAssign)
{
    if (_cachedAssets.TryGetValue(asset, out Sprite sprite))
    { imageToAssign.sprite = sprite; return; }
    sprite = LoadAsset<Sprite>(asset);
    _cachedAssets[asset] = sprite;
    imageToAssign.sprite = sprite;
}

public void Initialize() { }                               // EMPTY - no setup logic

private Dictionary<string, Sprite> _cachedAssets = ...;    // caches UI Image sprites only
```

**Notes**:

1. `LoadAsset<T>` is pure delegation - no path translation, no error handling beyond null check.
2. `InstantiatePrefab` returns null on missing asset (does NOT throw). `AssetBundleManager.LoadAsset` already logs `"Bundle not found"` or `"No asset found"`. Caller is responsible for null-check.
3. **No prefab cache** - every `InstantiatePrefab` does a fresh `LoadAsset<GameObject>` + `Object.Instantiate`. The bundle is cached, but asset extraction repeats per call.
4. Sprite cache is UI-only (`_cachedAssets`). Other types (Texture2D, GameObject, Material) are not cached at this layer.
5. `Object.Instantiate` is called with `Vector3.zero, Quaternion.identity` - caller is responsible for repositioning. `ShipVisController.InitializeShipVisualizer` does `base.transform.localPosition = ...` after instantiation.

**Diagnostic implications**:

- "White hull": `LoadAsset<Material>` returning null means `renderer.sharedMaterial` becomes null → renders MAGENTA (not white). The "white" symptom is shader binding, not null material.
- "No engines": hull `InstantiatePrefab` works (else ship wouldn't spawn). The drive prefab is loaded inside `SetDrive` via a separate `LoadAsset<GameObject>` - that's where engine load can fail.
- "No thrusters": `AssetCacheManager.thrusterFXPrefabs` field initializers call `LoadAsset<GameObject>("ships/HumanThrusterBasic")` etc. at static init. If those 4 paths fail, the dictionary contains nulls, and `SetDrive`'s `Object.Instantiate(thrusterFXPrefab, ...)` throws on null prefab.

### 22.3 JSON → Built ship

#### Template lookup chain

When a ship is built:
1. Player or AI selects a `TISpaceShipTemplate` (the design - TISpaceShipTemplate IS the design class; see §22.8)
2. Build queue creates a `TISpaceShipState` with the design
3. State has `template` typed `TISpaceShipTemplate` (computed via reflection per §1 Game-State Type Architecture)
4. Template has `hullTemplate` (TIShipHullTemplate), `driveTemplate` (TIDriveTemplate), `radiatorTemplate`, `designingFaction`, `hullAppearanceIndex`, etc.
5. `template.hullTemplate.modelResource[hullAppearanceIndex]` is the asset path string ("bundle/asset")

**`hullAppearanceIndex`** is a per-faction index into the `modelResource[]` array. Allows the same hull template to point at different prefabs per faction (e.g., "Lancer" → 7 different prefabs for 7 factions). BK currently uses ONE prefab per hull (factionSkin: None + edit-time bake), so the index always picks the same prefab.

#### Vanilla BuildShip orchestration (ShipModelController.cs:1132-1146) 

```csharp
public void BuildShip(ShipVisController parentController, TISpaceShipTemplate ship,
    TISpaceShipState shipState = null, bool buildVectorThrusters = false)
{
    this.ship = shipState;
    this.SetSkin(ship);                         // 
    this.BuildDrives(ship);                     // 
    if (buildVectorThrusters)
    {
        this.BuildVectorThrusters(ship);
    }
    this.SetRadiators(ship);                    
    this.BuildWeapons(parentController, ship, shipState);  // 
    this.AddExplosions();
    this.SetShadows(true);
    this.gameTime = World.Active.GetExistingManager<GameTimeManager>();
}
```

Order matters. **SetSkin runs FIRST** because vanilla SetSkin populates `hullModel`. Subsequent steps depend on it being set. For BK Hybrid/FullCustom hulls, `SetSkinSkipPatch` skips vanilla SetSkin entirely (preserving edit-time baked materials); the `hullModel` field is populated separately by vanilla constructor logic.

### 22.4 Instantiation and controller setup

#### ShipVisController.InitializeShipVisualizer (ShipVisController.cs:46-112) 

This is the actual entry point - the method that takes a TISpaceShipTemplate and produces a fully-built scene instance:

```csharp
public void InitializeShipVisualizer(TISpaceShipTemplate shipTemplate, TISpaceShipState ship,
    FleetVisController fleetVisController, StrategyShipController strategyShipController,
    bool fullVersion)
{
    this.shipState = ship;
    this.fleetVisController = fleetVisController;
    this.strategyShipController = strategyShipController;
    base.name = this.shipState.ID.ToString();  // GameObject name = ship's ID
    
    // (1) Instantiate the prefab into the scene
    this.modelLink = GameControl.assetLoader.InstantiatePrefab(
        shipTemplate.hullTemplate.modelResource[shipTemplate.hullAppearanceIndex],
        base.transform);
    this.modelLink.SetActive(false);  // hide while we wire it up
    
    // (2) Get the ShipModelController component (must exist on prefab root)
    this.ModelController = this.modelLink.GetComponent<ShipModelController>();
    
    // (3) Run the build pipeline
    this.ModelController.BuildShip(this, shipTemplate, this.shipState, false);
    
    // (4) Set radiator emissive temperature (visible color of operating radiators)
    if (this.shipState.radiators != null)
    {
        this.ModelController.SetRadiatorEmissiveKelvinRange(295.0,
            (double)this.shipState.radiators.operatingTemp_K);
    }
    
    // (5) Add UI overlay (only for full version, not preview/UI-only)
    if (fullVersion)
    {
        GameObject uiObject = Object.Instantiate(
            GameControl.assetLoader.LoadAsset<GameObject>("ui_spaceCombat/Ship UI Object"),
            base.transform);
        uiObject.transform.localPosition = new Vector3(0f, 0f, 0f);
        this.UIController = uiObject.GetComponent<ShipUIController>();
        this.UIController.Initialize(this);
        
        // (6) Subscribe to combat enter/leave events
        GameControl.eventManager.AddListener<ShipEntersCombat>(...);
        GameControl.eventManager.AddListener<ShipLeavesCombat>(...);
    }
    else
    {
        this.SetAsUIVisualization(this.shipState, false);
    }
    
    // (7) Position in fleet formation
    if (fleetVisController != null)
    {
        base.transform.localPosition = (formation.pattern == null)
            ? defaultPositionOnCreation
            : fleetFormationOffset;
    }
    
    // (8) Hide selection visuals
    if (this.ModelController?.selectionAnimObject != null)
        this.ModelController.selectionAnimObject.SetActive(false);
    // ... (similar for groupSelectionAnimObject, padlockIconObject)
}
```

**Critical chain**:
1. Asset path resolved from template (`hullTemplate.modelResource[idx]`)
2. Prefab instantiated via `assetLoader.InstantiatePrefab` (loads from bundle if not yet, instantiates into scene)
3. `ShipModelController` component is REQUIRED on prefab root - without it, `GetComponent` returns null and subsequent calls NullReferenceException
4. `BuildShip` orchestrates the full pipeline (skin/drives/thrusters/radiators/weapons/explosions/shadows)
5. Radiator color temperature is set from ship state (per-ship setting, not template)
6. UI overlay (`Ship UI Object` from `ui_spaceCombat` bundle) is added only for combat-visible ships
7. Selection reticles start hidden (shown later when ship is selected)

There is also a `InitializeModelOnly` (ShipVisController.cs:14-43) for UI-only previews (e.g., shipyard preview screen) that runs the same prefab instantiation but skips state/fleet/UI wiring and disables all colliders.

#### Why the ShipModelController component must exist on the prefab root

`ShipModelController` is `abstract`. Concrete subclasses include:
- `HumanShipController` (abstract; for human ships)
- Each hull class's subclass: `BattlecruiserController`, `LancerController`, `TitanController`, `DreadnoughtController`, etc.
- Alien controllers: `AlienBattlecruiserController`, `AlienBattleshipController`, etc.

The actual concrete component on each prefab is the per-hull controller. ShipTools' `Verify Hull` uses `FindShipController()` to detect this - finds the FIRST root component with all 3 weapon array properties (noseWeaponControllers, dorsalHullWeaponControllers, ventralHullWeaponControllers).

For BK hulls, the prefab uses one of the existing vanilla controllers (matched per §8 Hull DataName Map):
- Lancer → LancerController
- Battlecruiser → BattlecruiserController
- Titan → TitanController
- (etc. - see §8 for full map)

This works because the controller class is concerned with mount index resolution (`SlotToWeaponMountIndex`) and shared visualizer logic - there's no per-hull behavior unique to a controller class beyond mount-index switch statements (§17).

### 22.5 Material/skin system

See §18 Edit-Time Material Bake Workflow for the full picture. Key facts here:

- **Vanilla SetSkin** (HumanShipController.cs:11-29) loads `MAT_<childName><factionSuffix>` from the faction's material bundle path. Works only when the faction's material bundle is already loaded and contains the right materials.
- **BK bypass**: `SetSkinSkipPatch` returns `false` for Hybrid/FullCustom hulls, skipping vanilla SetSkin. Materials come from edit-time bake (sharedMaterial assignment in the prefab) - bundle ships pre-baked.
- **ShipTools sidecar** (`<hullname>.faction`) records last-baked faction so rebuilds use correct materials without prompting.
- **Filename pattern** baked into ShipTools: `Assets/Material/MAT_<childRendererName>_<lowercaseFaction>.mat`
- **Filter effect**: only renderers whose name matches a real material file get baked. Weapon-placeholder children, radiators, drives, etc. keep their vanilla materials.

### 22.6 Animation systems

#### Radiators 

**Three radiator types**, each with its own GameObject naming convention:

| Type | Names | Style |
|---|---|---|
| Fin (rectangular blade) | Radiator12, Radiator3, Radiator130, Radiator6, Radiator4, Radiator430, Radiator730, Radiator1030, Radiator8, Radiator9 | 10 positions, named by clock-face position |
| Spike (long pointed) | spikes 12, spikes 3, spikes 6, spikes 9 | 4 positions (corners) |
| Droplet (circular array) | Droplet12, Droplet8, Droplet4 | 3 positions |

**Radiator selection per ship - `WhichRadiators(ship)` virtual method** (HumanShipController.cs:31-84): Each hull controller picks which radiators to activate based on `radiatorTemplate.radiatorArea_m2(ship.wasteHeat_GW)` and `ship.radiatorTemplate.radiatorType` (RadiatorType enum: Fin/Droplet/Spike). The HumanShipController canonical implementation uses these thresholds:

```csharp
// Source: HumanShipController.cs:31-84 (paraphrased)
case RadiatorType.Fin:
    if (num < 800f)  list = [radiator3, radiator9];                          // 2 fins
    else if (num < 1200f)  list = [radiator12, radiator4, radiator8];        // 3 fins
    else if (size >= ShipSize.Medium)  list = [radiator12, radiator3, radiator6, radiator9];  // 4 fins, cardinal
    else  list = [radiator130, radiator430, radiator730, radiator1030];      // 4 fins, diagonal
    break;
case RadiatorType.Droplet:
    list = [dropletRadiator12, dropletRadiator4, dropletRadiator8];          // 3 droplets always
    break;
case RadiatorType.Spike:
    list = [spikesRadiator3, spikesRadiator9];                                // 2 spikes minimum
    if (num > 400f)  list += [spikesRadiator6, spikesRadiator12];            // +2 if hot
    break;
```

**Alien controllers' WhichRadiators** (per `Alien<X>Controller.cs`):
- **All alien hulls only use 4 fin radiators**: `radiator1030, radiator130, radiator430, radiator730` (the diagonal/clock-pos-30 series)
- **No spike or droplet radiators on alien hulls** - alien hulls don't have these GameObjects on their prefabs at all
- AlienBattlecruiser/AlienBattleship: hardcoded 4 radiators always
- AlienCruiser/AlienAssaultCarrier: 2 vs 4 radiators based on `radiatorArea_m2(wasteHeat_GW) < 800f`

**Implication for BK hulls**: BK hulls reuse vanilla controllers (e.g., LancerController for Lancer). Each vanilla controller's `WhichRadiators` selection determines which radiators get activated. If you want a BK hull to use specific radiators regardless of ship size/heat, you'd need a custom controller subclass that overrides `WhichRadiators`.

**Per-radiator components** (RadiatorVisController on each):

```csharp
public class RadiatorVisController : MonoBehaviour
{
    public GameObject intactRadiatorModel;
    public GameObject destroyedRadiatorModel;
    public GameObject explosionPrefab;
    public bool showDestroyedRetractedRadiator;
    
    public void OnRadiatorRepaired() { ... } // swap intact ON, destroyed OFF
    public void OnRadiatorDestroyed(bool radiatorsRetracted) { ... }
    public void OnPlay() { explosionParticles?.Play(); }
    public void OnPause() { explosionParticles?.Pause(); }
}
```

**Activation / deactivation** is per-faction (SetRadiators in HumanShipController.cs:86+):
```csharp
List<GameObject> list = this.WhichRadiators(ship);  // virtual; returns subset
this.radiator12.SetActive(list.Contains(this.radiator12));
this.radiator130.SetActive(list.Contains(this.radiator130));
// ... 17 SetActive calls
```

`WhichRadiators(ship)` is `virtual` - overridden by hull-specific controllers to choose which radiators are visible. For BK hulls reusing vanilla controllers, the existing override determines radiator selection.

**Animation** is via Unity Animator components on each active radiator GameObject. After `SetActive(true)`, `radiatorAnimators` list is populated:
```csharp
this.radiatorAnimators = new List<Animator>();
if (this.radiator12.activeSelf) this.radiatorAnimators.Add(this.radiator12.GetComponent<Animator>());
// ... 17 conditional adds
```

The Animator runs the deploy/retract animation (Unity Animation Controller asset baked into the prefab). The animation curves are NOT in the dictionary's source archive - they're Unity .anim assets serialized into the bundle.

**Radiator emissive color** (operating temperature glow): `ShipModelController.SetRadiatorEmissiveKelvinRange(295.0, operatingTemp_K)` - converts Kelvin to RGB blackbody color, applied to material. Called in InitializeShipVisualizer step 4. Higher operating temperature = whiter/bluer emissive; lower = redder.

#### Color animations (ColorAnimationEffect)

See §15 ColorAnimationEffect Fields. 7 fields, 4 animation channels (Red, Green, Cyan animators).

#### Drive thrusters 

`ShipModelController.SetDrive(resource, targetObject, thrusters, drive, faction, variableMaterial, hullAppearanceIndex, simpleHull)` (ShipModelController.cs:995-1057) sets up the drive prefab and thruster particle effects:

```csharp
// Source: ShipModelController.cs:995-1057 (paraphrased)
public void SetDrive(string resource, GameObject targetObject, int thrusters,
    TIDriveTemplate drive, TIFactionState faction, bool variableMaterial,
    int hullAppearanceIndex, bool simpleHull)
{
    GameObject driveSource;
    if (!simpleHull)
    {
        // (1) Load drive prefab from bundle
        GameObject loaded = GameControl.assetLoader.LoadAsset<GameObject>(resource);
        targetObject.GetComponent<MeshFilter>().sharedMesh = loaded.GetComponent<MeshFilter>().sharedMesh;
        MeshRenderer mr = targetObject.GetComponent<MeshRenderer>();
        
        // (2) Material assignment (3 paths)
        if (variableMaterial)
            mr.sharedMaterial = assetLoader.LoadAsset<Material>(drive.GetMaterialPath(faction, hullAppearanceIndex));
        else if (skirmishMode && faction.IsAlienFaction && !ship.isAlien)
            mr.sharedMaterial = assetLoader.LoadAsset<Material>(drive.GetMaterialPath(GameStateManager.AlienProxy(), hullAppearanceIndex));
        else
            mr.sharedMaterial = loaded.GetComponent<MeshRenderer>().sharedMaterial;  // use prefab's own
        
        targetObject.transform.localScale = loaded.transform.localScale;
        driveSource = loaded;
    }
    else  driveSource = targetObject;  // simpleHull = use existing
    
    targetObject.SetActive(true);
    
    // (3) Find ThrusterPoints in drive's children (substring match: "ThrusterPoint" || "Thruster" || "thruster", but NOT "Thruster_Alien")
    List<Transform> thrusterTransforms = new List<Transform>();
    foreach (Transform t in driveSource.GetComponentsInChildren<Transform>())
    {
        if ((t.name.Contains("ThrusterPoint") || t.name.Contains("Thruster") || t.name.Contains("thruster"))
            && !t.name.Contains("Thruster_Alien"))
        {
            thrusterTransforms.Add(t);
        }
    }
    
    // (4) Get thruster FX prefab from cache
    GameObject thrusterFXPrefab = AssetCacheManager.thrusterFXPrefabs[
        drive.MainThrusterFXResource(faction.IsAlienFaction)];
    
    // (5) For each thrusterLocation slot, instantiate FX prefab + register MultiEffectContainer
    this.thrusterEffectContainers = new List<MultiEffectContainer>();
    for (int j = 0; j < this.thrusterLocations.Length; j++)
    {
        if (j < thrusters)  // active slots
        {
            if (thrusterTransforms.Count > j && thrusterTransforms[j] != null)
            {
                this.thrusterLocations[j].transform.localPosition = thrusterTransforms[j].localPosition;
                GameObject fxInstance = Object.Instantiate(thrusterFXPrefab, this.thrusterLocations[j].transform);
                fxInstance.transform.localScale = base.transform.localScale;
                fxInstance.transform.localPosition = Vector3.zero;
                this.thrusterEffectContainers.Add(new MultiEffectContainer(
                    fxInstance.GetComponentsInChildren<ParticleSystem>(true).ToList()));
                this.thrusterLocations[j].SetActive(true);
            }
        }
        else  this.thrusterLocations[j].SetActive(false);  // inactive slots hidden
    }
    
    if (this.eventInstance.isValid())
        this.eventInstance.SetVolume(0.5f);
    this.DeactivateThrusters();  // start with thrusters off
}
```

**Notes**:
1. **`AssetCacheManager.thrusterFXPrefabs`** is a Dictionary cache of pre-loaded thruster FX prefabs, keyed by `drive.MainThrusterFXResource(IsAlien)` path. This is loaded at game start (alongside Sprite caches in AssetCacheManager).
2. **`thrusterEffectContainers`** (List<MultiEffectContainer>) is the runtime structure that ActivateThrusters/DeactivateThrusters operates on. NOT `thrusterLocations` directly.
3. **Substring match for thruster naming** is permissive: `ThrusterPoint || Thruster || thruster`, but **excludes `Thruster_Alien`** - alien drives have alien-specific thrusters identified by the suffix.
4. **`thrusters` parameter caps the FX count** - slots beyond it are deactivated.

**Vanilla `ActivateThrusters` and `DeactivateThrusters`** (ShipModelController.cs:270-293):
```csharp
public void ActivateThrusters(bool playAudio)
{
    for (int i = 0; i < this.thrusters; i++)
        this.thrusterEffectContainers[i].Play();
    if (playAudio) { /* FMOD audio */ }
    else this.StopThrusterAudio();
}

public void DeactivateThrusters()
{
    for (int i = 0; i < this.thrusters; i++)
        this.thrusterEffectContainers[i].Stop();
    this.StopThrusterAudio();
}
```

**`SetVectorThrusters(drive, faction)`** (ShipModelController.cs:1062-1093) sets up vector thrusters (turn-maneuver particle effects):

```csharp
// Source: ShipModelController.cs:1062-1093 (paraphrased)
public void SetVectorThrusters(TIDriveTemplate drive, TIFactionState faction)
{
    if (this.initVectorThrusters) return;
    this.vectorThrusterFXPath = drive.VectorThrusterFXResource(faction.IsAlienFaction);
    
    // 16 iterations: first 12 use existing vectorThrusterGOs, last 4 mirror them
    for (int i = 0; i < 16; i++)
    {
        if (i > 11)
        {
            // Create "Counter" GO mirroring vectorThrusterGOs[i-4]
            Transform src = this.vectorThrusterGOs[i - 4].transform;
            GameObject mirrored = new GameObject(TIUtilities.CombineStrings(new[] { src.name, "Counter" }));
            mirrored.transform.SetParent(src.transform.parent, true);
            mirrored.transform.localPosition = new Vector3(src.localPosition.x, -src.localPosition.y, src.localPosition.z);
            mirrored.transform.localScale = new Vector3(src.localScale.x, -src.localScale.y, src.localScale.z);
            mirrored.transform.localEulerAngles = new Vector3(src.localEulerAngles.x, -src.localEulerAngles.y, src.localEulerAngles.z);
            this.vectorThrusterGOs.Add(mirrored);
        }
        // Spawn FX via TIVFXManager
        GameObject vfx = TIVFXManager.GetVFX(this.vectorThrusterFXPath, this.vectorThrusterGOs[i].transform);
        vfx.SetActive(true);
        this.vectorThrusterEffect[i] = vfx.GetComponentsInChildren<ParticleSystem>()[0];
        vfx.transform.localEulerAngles = new Vector3(90f, 0f, 0f);
        vfx.transform.localPosition = Vector3.zero;
        vfx.transform.localScale = Vector3.one;
    }
    this.DeactivateAllVectorThrusters();
    this.initVectorThrusters = true;
}
```

**Notes**:
1. **Vector thrusters are 16 total**: 12 from prefab, 4 mirrored automatically
2. **TIVFXManager.GetVFX(path, parent)** is the canonical FX spawning method - likely a pooled instantiator
3. **Mirroring** mirrors Y axis (position/scale/rotation)
4. **`initVectorThrusters` flag** prevents double-initialization (idempotent)

**Vector thruster trigger**: ShipModelController.cs:325-355 random selection (`UnityEngine.Random.Range(0, 12)`) picks one of 12 named effects (backRight, frontLeft, backLeft, frontRight, frontDorsal, etc.) during turn maneuvers.

#### Weapon firing pipeline 

**WeaponClass enum** (per `ShipWeaponVisController.cs:Fire`, ≥5 values):
- `WeaponClass.Laser` - beam weapon
- `WeaponClass.Particle` - beam weapon
- `WeaponClass.NavalGun` - projectile weapon
- `WeaponClass.Magnetic` - projectile weapon (railguns/coilguns)
- `WeaponClass.Plasma` - projectile weapon

(There may be more values, e.g. Missile - `TIMissileTemplate` is referenced separately as `weaponTemplate.ref_missileWeapon` in SetPrefabs.)

**Weapon prefab init - `SetPrefabs()` (ShipWeaponVisController.cs:135-196)** :

```csharp
// Source: ShipWeaponVisController.cs:135-196 (paraphrased)
private void SetPrefabs()
{
    if (createdPrefabs || weaponTemplate == null) return;
    
    // Cast to subtype to determine path source
    TIBeamWeaponTemplate beam = weaponTemplate as TIBeamWeaponTemplate;
    TIGunTypeWeaponTemplate gun = weaponTemplate as TIGunTypeWeaponTemplate;
    TIMissileTemplate missile = weaponTemplate.ref_missileWeapon;
    
    // (1) Determine shotEffect path (beam line or muzzle flash)
    if (!string.IsNullOrEmpty(weaponTemplate.effectResource))
        shotEffectPath = weaponTemplate.effectResource;
    else if (beam != null)
        shotEffectPath = TemplateManager.global.pathFallbackLaserVFX;
    else if (gun != null)
        shotEffectPath = TemplateManager.global.pathFallbackMuzzleFlashVFX;
    
    // (2) Spawn shotEffect at firePoint via TIVFXManager
    if (!string.IsNullOrEmpty(shotEffectPath))
    {
        shotEffectInstance = TIVFXManager.GetVFX(shotEffectPath, firePoint.transform);
        shotEffectInstance.transform.localEulerAngles = Vector3.zero;
        shotEffectInstance.transform.localPosition = Vector3.zero;
        shotEffectInstance.transform.localScale = Vector3.one;
        shotEffectInstance.SetActive(false);
        beamController = shotEffectInstance.GetComponent<BeamWeaponController>();  // null for guns
    }
    
    // (3) Load projectile prefab (for guns/missiles)
    if (gun != null)
    {
        projectileResource = !string.IsNullOrEmpty(gun.shotModelResource)
            ? gun.shotModelResource
            : TemplateManager.global.pathFallbackProjectileVFX;
        projectilePrefab = GameControl.assetLoader.LoadAsset<GameObject>(projectileResource);
    }
    if (missile != null) { /* same pattern with missile.shotModelResource */ }
    
    // (4) Find muzzle flashes (per-weapon array of ShipWeaponMuzzleFlashController)
    muzzleFlashes = base.gameObject.GetComponentsInChildren<ShipWeaponMuzzleFlashController>(includeInactive: true);
    for (int i = 0; i < muzzleFlashes.Length; i++)
    {
        muzzleFlashes[i].transform.parent.gameObject.SetActive(true);
        muzzleFlashes[i].transform.parent.gameObject.transform.localScale = new Vector3(50f, 50f, 50f);
    }
    createdPrefabs = true;
}
```

**Weapon fire - `Fire(truncated, time)` (ShipWeaponVisController.cs:434-518)** :

```csharp
// Source: ShipWeaponVisController.cs:434-518 (paraphrased)
public void Fire(bool truncated, TIDateTime time = null)
{
    if (!createdPrefabs) SetPrefabs();
    
    // Beam re-parenting if ship is hidden during strategic-layer bombardment
    if (!GameControl.spaceCombat.enabled && shipVisController != null) {
        if (shipVisController.fleetVisController == null) return;
        if (!shipVisController.gameObject.activeInHierarchy && beamController != null)
            beamController.transform.SetParent(shipVisController.fleetVisController.container.transform, false);
    }
    
    // Combat-mode line-of-sight check
    if (GameControl.spaceCombat.enabled && !OnTarget()) return;
    
    // Branch by weapon class
    switch (weaponTemplate.weaponClass)
    {
    case WeaponClass.Laser:
    case WeaponClass.Particle:
        // BEAM PATH
        if (beamController != null)
        {
            shotEffectInstance.SetActive(true);
            beamController.enabled = true;
            shotEffectInstance.transform.localScale = (combatHabModuleController != null)
                ? habBeamScaling : Vector3.one;
            LineRenderer line = shotEffectInstance.GetComponent<LineRenderer>();
            if (!Bombarding())
            {
                beamController.Initialize(target);
                ceaseBeamFireTime = new TIDateTime(gameTime.currentTime, 2.0);  // beam burns 2s
            }
            else
            {
                beamController.Initialize(weaponCarrierState.GetTargetableState(), stratLayerTarget,
                    time, LayerMask.NameToLayer("Solar System"));
                line.endWidth = 0f;
                Invoke("CeaseBeamFire", (gameTime.currentSpeedIndex <= 1) ? 2f : 1f);
            }
        }
        break;
    case WeaponClass.NavalGun:
    case WeaponClass.Magnetic:
    case WeaponClass.Plasma:
        // GUN PATH
        shotEffectInstance.transform.localPosition = Vector3.zero;
        shotEffectInstance.SetActive(true);
        shotEffectInstance.transform.localRotation = firePoint.transform.localRotation;
        foreach (ShipWeaponMuzzleFlashController muzzle in muzzleFlashes)
        {
            muzzle.gameObject.SetActive(true);
            muzzle.Flash();
        }
        break;
    }
    
    // FMOD audio
    eventPath = weaponTemplate.fireSoundFXResource;
    if (eventPath != null) {
        if (!eventInstance.isValid())
            eventInstance = AudioManager.CreateFMODInstance(eventPath);
        if (GameControl.spaceCombat.enabled)
            eventInstance.SetDistance(AudioManager.GetCombatAudioMaxDistance(eventInstance));
        if (combat || ship visible) {
            eventInstance.set3DAttributes(base.gameObject.transform.To3DAttributes());
            eventInstance.Play(base.gameObject);
        }
    }
}
```

**Notes**:
1. **Two distinct firing paths** based on weapon class (beam vs gun)
2. **Beams use `BeamWeaponController` + `LineRenderer`** for the visible beam line
3. **Guns use `shotEffectInstance` (muzzle flash effect) + `ShipWeaponMuzzleFlashController[]` array** for muzzle flashes - each flash is its own controller
4. **Beam burns for 2 seconds** by default in combat (1s during fast-forward strategic bombardment)
5. **`Bombarding()`** distinguishes strategic-layer bombardment (different beam initialization with longitude/latitude/parentBody)
6. **Audio is per-fire** via FMOD `AudioManager.CreateFMODInstance(weaponTemplate.fireSoundFXResource)`

#### Damage layer 

`DamageLayer`. Operates via shader uniform `_DamagePointArray` - array of damage points sent to a custom shader that displays scorch/damage effects on hull. Updated when ship takes hits during combat.

Does NOT use `_ExplosionSequenceRoot`. Explosion sequences are separate (handled by AddExplosions() in BuildShip and by individual `OnRadiatorDestroyed`/`OnWeaponDestroyed` etc. callbacks).

#### Destruction sequence

`ShipModelController.AddExplosions()` is called in BuildShip and sets up the destruction visualizer.

Per-component destruction:
- Radiator: `RadiatorVisController.OnRadiatorDestroyed(radiatorsRetracted)` - instantiates explosion prefab, swaps to destroyed model
- Weapon: referenced as `OnWeaponDestroyedExplosion` / `ShipDestroyedWeaponExplosion`
- Whole ship destruction: not yet fully traced in source

### 22.7 Weapon mount/firing pipeline

#### Weapon placement at ship build - `SetWeapon` (ShipModelController.cs:1077-1109) 

```csharp
// Source: ShipModelController.cs:1077-1109 (paraphrased)
public static void SetWeapon(string resource, ShipVisController parentController,
    ShipWeaponVisController targetController, ModuleDataEntry moduleDataEntry,
    bool forVisualizationOnly)
{
    targetController.Initialize(parentController, moduleDataEntry, forVisualizationOnly);
    
    // (1) Load weapon prefab from bundle
    GameObject weaponPrefab = GameControl.assetLoader.LoadAsset<GameObject>(resource);
    Transform child0 = weaponPrefab.transform.GetChild(0);
    
    // (2) Apply meshes/materials from prefab to mount components
    targetController.baseObject.GetComponent<MeshFilter>().sharedMesh =
        weaponPrefab.GetComponent<MeshFilter>().sharedMesh;
    targetController.baseObject.GetComponent<MeshRenderer>().sharedMaterial =
        weaponPrefab.GetComponent<MeshRenderer>().sharedMaterial;
    
    targetController.weaponObject.GetComponent<MeshFilter>().sharedMesh =
        child0.GetComponent<MeshFilter>().sharedMesh;
    targetController.weaponObject.GetComponent<MeshRenderer>().sharedMaterial =
        child0.GetComponent<MeshRenderer>().sharedMaterial;
    targetController.weaponObject.transform.localPosition = child0.transform.localPosition;
    
    if (targetController.weaponModuleData.moduleTemplate.ref_weapon.staticLauncher)
        targetController.weaponObject.transform.localRotation = child0.transform.localRotation;
    
    // (3) Set FirePoint position from prefab template's grandchild
    if (child0.transform.childCount > 0)
        targetController.firePoint.transform.localPosition = child0.transform.GetChild(0).localPosition;
    else
        Log.Error("Missing FirePoint for resource " + resource, Array.Empty<object>());
    
    // (4) Apply scale (only for non-simple hulls)
    TISpaceShipState shipState = targetController.shipVisController.shipState;
    if (shipState != null && !shipState.hull.simpleHull)
    {
        targetController.baseObject.transform.localScale = weaponPrefab.transform.localScale;
        targetController.weaponObject.transform.localScale = child0.transform.localScale;
    }
}
```

**Notes**:
1. **Weapon prefab structure is hierarchical**:
   - `prefab` → has `MeshFilter` + `MeshRenderer` (base mount: turret base, hardpoint)
   - `prefab.child[0]` → weapon body (gun barrel, beam emitter, missile launcher)
   - `prefab.child[0].child[0]` → FirePoint (transform marker; spawn location for projectiles/beams)
2. **Mesh/material swap, not Instantiate** - `SetWeapon` reuses the existing GameObject hierarchy from the hull prefab, swapping in the weapon prefab's data. This is why the hull prefab must have empty mount slots with the right component layout.
3. **At edit time**, ShipTools.AddWeaponMount creates this layout by walking weapon mount children with `nose`/`dorsal`/`ventral` substring matching:
   - Sets `controller.baseObject = controller.gameObject` (mount root)
   - Sets `controller.weaponObject = controller.transform.GetChild(0).gameObject` (gun child)
   - Sets `controller.firePoint = controller.weaponObject.transform.GetChild(0).gameObject` (FirePoint grandchild)

**`SetShipPart(resource, targetObject)`** (ShipModelController.cs:1066-1074) - utility for non-weapon parts (utility modules, etc.):
```csharp
public static void SetShipPart(string resource, GameObject targetObject)
{
    GameObject partPrefab = GameControl.assetLoader.LoadAsset<GameObject>(resource);
    targetObject.GetComponent<MeshFilter>().sharedMesh = partPrefab.GetComponent<MeshFilter>().sharedMesh;
    targetObject.GetComponent<MeshRenderer>().sharedMaterial = partPrefab.GetComponent<MeshRenderer>().sharedMaterial;
    targetObject.transform.localScale = partPrefab.transform.localScale;
    targetObject.SetActive(true);
}
```

#### Slot index resolution (per §17)

- Vanilla controllers' `SlotToWeaponMountIndex(int slot, Mount mount)` switches map abstract slot indices to concrete array indices, varying by Mount enum (OneNose, TwoNoseHoriz, ThreeNoseAngle, OneHull, TwoHullHoriz, TwoHullVert, ThreeHullHoriz, FourHull, etc.)
- Each hull controller (BattlecruiserController, LancerController, TitanController, DreadnoughtController, AlienBattlecruiserController, AlienBattleshipController, AlienCruiserController, AlienAssaultCarrierController) has its own switch
- BK's `WeaponMountPatch` extends this for additional BK-defined slots via `HullDefinitions.cfg`

#### Runtime fire pipeline 

1. Combat AI or player issues fire order - sets `target` via `SetTarget(IDamageable, Vector3)` or `SetStratLayerTarget(...)`
2. `ShipWeaponVisController.Fire(truncated, time)` called per weapon (see §22.6 Weapon firing pipeline for full Fire body)
3. Branch by `weaponTemplate.weaponClass`:
   - **Beam (Laser/Particle)**: enable `BeamWeaponController`, configure `LineRenderer`, set 2-second auto-cease timer
   - **Gun (NavalGun/Magnetic/Plasma)**: enable shotEffectInstance (muzzle flash), call `Flash()` on each `ShipWeaponMuzzleFlashController`
4. Audio plays via FMOD `AudioManager.CreateFMODInstance(weaponTemplate.fireSoundFXResource)`
5. Hit detection happens in separate combat code (CombatantController, not ship visualizer) - projectile spawning likely happens via separate timer/Update tick using `projectilePrefab` loaded in SetPrefabs
6. DamageLayer update on target ship (shader uniform `_DamagePointArray` refresh)

### 22.8 Ship designs (TISpaceShipTemplate)

**Note**: There is NO separate `TISpaceShipDesign.cs` - `TISpaceShipTemplate` IS the ship design class. It extends `TIDataTemplate` (TISpaceShipTemplate.cs:10). 4,904 lines / 168KB - by far the largest single template file in TI.

#### Class structure

```csharp
public class TISpaceShipTemplate : TIDataTemplate
{
    // ... 4,904 lines of properties, methods, and computed fields
}
```

#### JSON-deserializable fields  (lines 4640-4715)

These are the actual `public` fields that get populated from `TISpaceShipTemplate.json`:

```csharp
// Identity (string references resolved lazily via TemplateManager)
public string factionName;          // → designingFaction (TIFactionState)
public string hullName;             // → hullTemplate (TIShipHullTemplate)
public string driveName;            // → driveTemplate (TIDriveTemplate)
public string powerPlantName;       // → powerPlantTemplate (TIPowerPlantTemplate)
public string radiatorName;         // → radiatorTemplate (TIRadiatorTemplate)

// Configuration counts
public int propellantTanks;         // number of propellant tanks
public int refitIteration;          // refit suffix counter
public int hullAppearanceIndex;     // index into hullTemplate.modelResource[]

// Armor - separate facing structures (NOT just template names)
public ArmorFacingTemplate noseArmor;
public ArmorFacingTemplate lateralArmor;
public ArmorFacingTemplate tailArmor;

// Slot-by-slot module lists - THE CORE OF SHIP DESIGN
public List<ModuleDataTemplateEntry> moduleTemplateEntries;     // utility modules per slot
public List<ModuleDataTemplateEntry> hullWeaponTemplateEntries; // hull weapons per slot
public List<ModuleDataTemplateEntry> noseWeaponTemplateEntries; // nose weapons per slot
public List<FireModeDataTemplateEntry> fireModeTemplateEntries; // fire mode per slot

// Ship role (AI behavior categorization)
public ShipRole role;

// Display
public bool hasDisplayName;
public TINationState nation;        // for human ships, nation owner
public bool hideInSkirmish;

// Constants (these are NOT JSON-deserializable but inform JSON values):
public const float longRange_km = 800f;
public const float mediumRange_km = 500f;
public const float shortRange_km = 200f;
public const float propellantTankMass_tons = 100f;
public const float mass_per_crew_tons = 4f;
public const float maneuverThrust_N_human = 2500000f;
public const float maneuverThrust_N_alien = 4000000f;
public const int maxThrusters = 6;
public const int numRotationalThrusters = 2;
public const float StandardAlienShipStrength = 100f;
```

**Key insight**: A ship design is **NOT** specified by `hullTemplateName`/`driveTemplateName`/etc. The actual JSON fields are `hullName`, `driveName`, `powerPlantName`, `radiatorName`, `factionName` (no "Template" suffix). These are then resolved lazily through `TemplateManager.Find<T>(name, false)` getters.

#### Lazy template resolution 

All template references use the **lazy-load + null-find pattern**:

```csharp
// Source: TISpaceShipTemplate.cs:177-188 (paraphrased - pattern repeated for all templates)
private TIShipHullTemplate _hullTemplate;
public TIShipHullTemplate hullTemplate
{
    get
    {
        if (this._hullTemplate == null)
            this._hullTemplate = TemplateManager.Find<TIShipHullTemplate>(this.hullName, false);
        return this._hullTemplate;
    }
}
```

**Critical implication**: `TemplateManager.Find<T>(name, false)` returns null when name doesn't match. The hullTemplate property does NOT null-check - the very first usage of `hullTemplate.X` after a load with a missing hull will throw NullReferenceException. This is what breaks saves when BK renames hulls (§21).

#### `modelResource` - the asset path used for prefab instantiation 

```csharp
// Source: TISpaceShipTemplate.cs:146-152
public string modelResource
{
    get
    {
        return this.hullTemplate.modelResource[this.hullAppearanceIndex];
    }
}
```

This is the EXACT property used by `ShipVisController.InitializeShipVisualizer` to load the prefab. The chain is:
- `template.modelResource` → reads `template.hullTemplate.modelResource[hullAppearanceIndex]`
- `template.hullTemplate` triggers TemplateManager lookup if not cached
- `TIShipHullTemplate.modelResource` is `string[]` (one entry per faction skin) - see §1
- `hullAppearanceIndex` selects which entry

**For BK hulls** (factionSkin: None, single modelResource entry): `hullAppearanceIndex` is always 0 effectively, since the array has only one entry.

#### Ship designs are NOT a separate template type - they're TISpaceShipTemplate JSON entries

A "ship design" in TI terminology is just **a TISpaceShipTemplate entry in TISpaceShipTemplate.json**. The template name (e.g., `Ship1`, `Ship2`, ..., `Ship33` for vanilla AI designs, or custom names for player-designed ships) is its dataName. `factionName` field identifies the owning faction. Player-designed ships are saved as TISpaceShipTemplate entries dynamically into the save game (NOT into JSON files).

This means:
1. Adding a new vanilla design = adding a new entry to TISpaceShipTemplate.json with `dataName: "Ship34"`, hull/drive/power/radiator names, slot module entries
2. Player ship design = same structure, saved to player save file
3. AI doesn't "design" ships at runtime - it picks from existing TISpaceShipTemplate entries that match its needs (via `AllowedRole(role)` and `FitsRole(role)`)

#### `ModuleDataTemplateEntry` and `FireModeDataTemplateEntry`  usage, structure

Each entry slot in the design holds a `ModuleDataTemplateEntry` (slot index + module template name + secondary references). Usage patterns:

- `moduleTemplateEntries` → utility modules in utility slots
- `hullWeaponTemplateEntries` → weapons in hull (dorsal/ventral) slots
- `noseWeaponTemplateEntries` → weapons in nose slots
- `fireModeTemplateEntries` → fire mode override per weapon slot (FireMode enum)

The split between hull and nose weapons in separate lists is significant - they're NOT in a single weapons array. Hull controllers' `SlotToWeaponMountIndex(int slot, Mount mount)` (per §17) maps slot indices to mount transform indices on the prefab - but the slot index space is unified across utility + nose + hull (slots 0-N), so each entry must carry its own slot-index field to know where it goes.

Field layout (from source):

```csharp
public struct ModuleDataTemplateEntry            // ModuleDataTemplateEntry.cs
{
    public string moduleName;                    // dataName of the part template
    public int slot;                             // slot index in hull's shipModuleSlots
    public TIShipPartTemplate moduleTemplate     // computed via TemplateManager.Find
}

public struct FireModeDataTemplateEntry          // FireModeDataTemplateEntry.cs
{
    public int slot;                             // slot index
    public FireMode fireMode;                    // FireMode enum value
}

public class ModuleDataEntry                    // ModuleDataEntry.cs (runtime)
{
    public string moduleTemplateName { get; }    // resolves to current part
    public int slotIndex { get; }
    public TIShipPartTemplate moduleTemplate     // computed
    public TIShipWeaponTemplate weaponTemplate   // computed (when applicable)
    public void CorrectBrokenSlot(int correctIndex)  // recovery if slot index drifted
}
```

`ModuleDataEntry` is the runtime equivalent used in `utilityModules`, `noseWeapons`, and `hullWeapons` getters. The `CorrectBrokenSlot` method handles slot-index drift recovery on save load.

#### Design-time validation methods 

- **`ValidPartForDesign(TIShipPartTemplate part) : bool`** (line 1745) - checks if a part can be added to this design. Validates: drive compatibility (utility modules need compatible drive), laser/particle bonus requires matching weapon class on ship, grouping uniqueness (only one utility per grouping), hull has utility slots, hull's consTier ≥ part's minConsTier.
- **`ValidAssignedSlotForLocation(TIShipPartTemplate, int slot)`** (line 1106) - validates slot index against part's allowed locations
- **`SlotIndexOccupied(int slotIndex, bool testSecondarySlotsForWeapons) : bool`** - overlap check
- **`GetPartInHullSlotIndex(int slotIndex, ...)`** - retrieve which part is in a specific slot
- **`validDrivesForPowerPlant : List<TIDriveTemplate>`** - auto-computed list of compatible drives given power plant choice
- **`IsAValidRefitFor(TISpaceShipTemplate oldShipTemplate, out string reason, bool getReason = false)`** - refit validation
- **`AreUtilityModulesValidForRefit(...)`**, **`AreWeaponModulesValidForRefit(...)`**

#### Computed/cached properties (auto-populated, not JSON) 

These are properties that compute from the JSON inputs and get cached in the underscore fields:

```csharp
// Mass calculations
public float dryMass_tons(bool forceUpdate = false)        // total dry mass
public float dryMass_kg                                    // = dryMass_tons * 1000
public float wetMass_tons                                  // dry + propellant
public float wetMass_kg                                    // wet * 1000
public float propellantMass_tons                           // = propellantTanks * 100
public float propellantMass_kg                             // * 1000
public float powerPlantMass_tons                           // computed from powerPlant
public float radiatorMass_tons
public float noseArmorMass_tons / lateralArmorMass_tons / tailArmorMass_tons
public float totalArmorMass_tons
public float allBatteriesMass_tons
public float crewMass_tons                                 // = crewBillets * 4 + damConCrewBillets * 4
public float weaponsMass_tons

// Power
public float drivePowerRequirement_GW
public float shipPowerProductionRequirement_GW
public float requiredSystemsPower_GW
public float requiredWeaponsPowerGeneration_GW
public float requiredWeaponsPowerStorage_GJ
public float wasteHeat_GW                                  // total heat to be dissipated by radiators

// Acceleration / DV (cached fields with -1f sentinel for "not computed")
public float baseCruiseAcceleration_mps2(bool forceUpdate)
public float baseCruiseAcceleration_gs(bool forceUpdate)
public float basePursuitAcceleration_mps2(bool forceUpdate)
public float baseCruiseDeltaV_kps(bool forceUpdate)
public float baseCruiseDeltaV_mps(bool forceUpdate)
public float baseCombatAcceleration_mps2
public float baseCombatAcceleration_gs
public float baseCombatThrust_N
public float baseCombatExhaustVelocity_kps
public float modifiedThrust_N
public float modifiedEV_kps                                // exhaust velocity
public float baseManueverThrust
public float baseAngularAcceleration_rads2
public float baseAngularAcceleration_degs2
public float maxAngularVelocity_mps                        // max linear angular velocity
public float maxAngularVelocity_degs
public float maxDamageControlAngularVelocity_mps

// Capacities
public float HeatCapacity_GJ(bool forceUpdate = false)
public float BatteryCapacity_GJ(bool forceUpdate = false)
public float modifiedCapSurfaceArea_m2

// Costs
public TIResourcesCost powerPlantBuildCost
public TIResourcesCost radiatorsBuildCost
public TIResourcesCost noseArmorBuildCost
public TIResourcesCost lateralArmorBuildCost
public TIResourcesCost tailArmorBuildCost
public TIResourcesCost spaceResourceConstructionCost(bool forceUpdate, TIHabModuleState shipyard, ...)
public TIResourcesCost earthResourceConstructionCost(TIFactionState faction, TIHabModuleState shipyard)
public TIResourcesCost singlePropellantTankCost(TIFactionState faction, float fillFraction = 1f)
public TIResourcesCost propellantTanksBuildCost(TIFactionState faction)
public float propellantTanksBuildCost(TIFactionState faction, FactionResource resource)

// Combat values (used by AI for ship valuation)
public float UnnormalizedTemplateSpaceCombatValue(bool forceUpdate = false, float fidelity = 1f)
public float TemplateSpaceCombatValue(bool forceUpdate = false, float updateFraction = -1f, float fidelity = 1f, bool fast = false)
public float BombardmentValue(TISpaceBodyState spaceBody)
public float InvasionCombatValue()
public float AssaultCombatValue(bool defense)

// Crew
public int crewBillets
public int damConCrewBillets

// Capability flags
public bool isAlien                                         // = hullTemplate.alien (NULL-DEREFS if hull missing)
public bool requiresExotics
public bool requiresAntimatter
public bool shipHasALaser
public bool shipHasAParticleBeam
public bool ValidTemplate

// Magazine (for missile/projectile reload)
public int magazineModuleCount
public float magazineModuleMultiplier

// Power weapon bonuses (cumulative across utility modules)
public float GetLaserBonusPower_MJ(Func<ModuleDataEntry, float> GetPartFunction = null)
public float GetParticleBonusPower_MJ(Func<ModuleDataEntry, float> GetPartFunction = null)
public float GetBonusPowerForWeapon_MJ(TIShipWeaponTemplate weapon, ...)
public float GetBonusPowerForWeapon_GJ(TIShipWeaponTemplate weapon, ...)
public float GetBonusPowerForWeapon_Multiplier(TIShipWeaponTemplate weapon, float range_km, ...)

// Targeting
public float TargetingBonus(TIFactionState faction = null)
public float ECMValue(bool attackerIsAlien, TIFactionState faction = null)

// Cross-section (for hit chance)
public float GetCrossSectionalArea_m2(float angle_degrees = -3.40282347E+38f)

// Income (for owned ships)
public float GetMonthlyNetIncome(FactionResource resource)
public float GetMonthlyGrossRevenue(FactionResource resource)
public float GetMonthlyExpenses(FactionResource resource)
```

#### `ShipRole` enum  (19 values)

Enumerated from usage in TISpaceShipTemplate.cs (`AllowedRole` switch + `orderToCheckRoles` list):

```
NoRole, TroopCarrier, ArmyCarrier, Explorer,
InnerSystemColonyShip, OuterSystemColonyShip, EarthSurveillance,
CouncilorTransport,
LL_Bomber, LL_Intruder,                       // Long-range Light
LM_Interdictor, LM_Protector,                 // Long-range Medium
LS_Penetrator,                                // Long-range Short(?)
ML_Standoff,                                  // Medium-range Light
MM_SpaceSuperiority,                          // Medium-range Medium
MS_Strike,                                    // Medium-range Short(?)
SL_Defender, SM_Patrol, SS_Interceptor        // Short-range Light/Medium/Short
```

Naming convention: `<Range><ClassSize>_<RoleType>`. **Static categorizers**:
- `shortRangeStrategic(role) / mediumRangeStrategic / longRangeStrategic` - strategic-range categorization
- `shortRangeCombatant / mediumRangeCombatant / longRangeCombatant` - combatant-range
- `SoloOperator(role) : bool` - whether role operates solo vs in fleet

**Range constants**: `shortRange_km = 200f`, `mediumRange_km = 500f`, `longRange_km = 800f`

#### `AllowedRole(ShipRole)` and `AssignRole()` 

`AllowedRole(role)` checks if a design can fulfill a role based on:
- **TroopCarrier**: requires utility module with `SpecialModuleRule.Assault`
- **ArmyCarrier**: requires utility module matching `armyCarrierRequirement = [SpecialModuleRule.LandArmy]`
- **Explorer**: requires utility module matching `explorerRequirement = [SpecialModuleRule.Prospector]`
- **InnerSystemColonyShip** (alien): requires `FoundSurveillanceStationRules` matches
- **InnerSystemColonyShip** (human): requires `innerColonyShipRequirement` (10 colony-related rules) match
- **OuterSystemColonyShip**: requires `outerColonyShipRequirement` (6 outer-colony rules) match
- **EarthSurveillance**: requires `surveillanceShipRequirement = [SpecialModuleRule.Surveillance]` match
- **NoRole**: returns false
- **All other roles**: returns true (no special requirements)

`AssignRole()` walks `orderToCheckRoles` (priority order list of 18 roles, checked first-match-wins) and returns the first matching role.

**Implication for BK**: When BK adds a custom utility module with `SpecialModuleRule.Assault`, that module enables the TroopCarrier role on any ship that mounts it. Similarly for other special-module-driven roles.

#### Static helpers / utilities 

- `Clone(string dataName, string factionName)` - duplicate template with new identity
- `CreateDummyShip()` - instantiate dummy TISpaceShipState (for previews)
- `InitAtRunTime(bool skipNaming = false)` - runtime init after JSON load
- `CacheTemplateValues(bool skipCost = false)` - pre-compute all derived values
- `ReCacheUtilityModules()` - invalidate utility module cache (after changes)
- `IsDuplicateOf(TISpaceShipTemplate other) : bool` - duplicate detection
- `ShouldObsolete(TIFactionState faction)`, `Obsolete(TIFactionState faction)` - design retirement
- `quickSummary(...)`, `DebugSummary()` - UI/log output
- `GenerateRandomClassName(TIFactionTemplate faction)` - random ship class name generator
- `GetRefitSuffix(int iteration)` - refit naming (R1, R2, R3, ...)
- `static ClearStaticData()`, `static ClearUnusedTemplates()` - cleanup
- `static GenerateTestCombats()` - combat balance testing helper

#### Why this matters for BK

BK's `TIShipHullTemplate.json` defines hulls. `TISpaceShipTemplate.json` defines designs. **They are DIFFERENT files referencing each other** - a design references a hull by `hullName`. BK currently doesn't add custom designs (it uses vanilla designs that get hulls via `factionAvailableChance` + `requiredProjectName` gating in TIShipHullTemplate). 

If BK ever wants to add custom AI ship designs (e.g., a specific weapon loadout AI should prefer for Titan hulls):
1. Add new entries to `TISpaceShipTemplate.json` with `hullName: "Titan"`, picked drive/power/radiator, populated `noseWeaponTemplateEntries`/`hullWeaponTemplateEntries`/`moduleTemplateEntries`
2. Set `factionName` to the intended owner faction
3. Set `role` to enable AI use of the design
4. Ensure `AllowedRole(role)` would return true (special modules satisfy requirements)

**Save-load behavior**: Player-designed ships are stored AS TISpaceShipTemplate entries in the save file. Renaming or removing a hull breaks ANY ship (vanilla or player-designed) that references it.

---

## 23. Hull Editing Pipeline

This section is the practical guide for editing ship hulls - slot layout, weapon mount placement, and the cross-cutting concerns (prefab hierarchy, JSON template, controller switch). It synthesizes §16 (ShipTools), §17 (SlotToWeaponMountIndex), §22 (full pipeline), and the BK `HullRegistry`/`ControllerRegistry`/`WeaponMountPatch` infrastructure.

**Goal**: enable a modder to take a 3D ship model from extracted TI assets, modify its mount/slot layout, save it as a new BK hull, and have it work end-to-end.

### 23.1 The 3 places ship layout is defined - must stay in sync

Editing a hull means coordinating changes across THREE separate definitions. Mismatch between them is the #1 source of bugs.

| Location | What it defines | File / location |
|---|---|---|
| **A. Prefab hierarchy** (Unity) | Physical 3D mount positions, weapon controller components, FirePoint transforms | `<bundle>/<HullName>.prefab` (in extracted Unity project) |
| **B. JSON template** (TI runtime data) | Logical slot grid: which slot at coordinates (x,y), what type (Drive/Utility/HullHardPoint/etc.) | `TIShipHullTemplate.json` → `shipModuleSlots` array |
| **C. Controller logic** (compiled C#) | Mapping from JSON slot index → prefab mount index, varying by Mount enum | Vanilla controller class's `SlotToWeaponMountIndex(slot, mount)` switch, OR BK's `HullDefinitions.cfg` `weaponSlotMap` |

When a ship is rendered in combat, vanilla code does:
1. Load prefab via `assetLoader.InstantiatePrefab(hullTemplate.modelResource[hullAppearanceIndex])`
2. At edit time, ShipTools.AddWeaponMount populates `noseWeaponControllers` / `dorsalHullWeaponControllers` / `ventralHullWeaponControllers` arrays based on `nose`/`dorsal`/`ventral` substring matching, in **hierarchy sibling order**
3. For each weapon slot in the design (`TISpaceShipTemplate.noseWeaponTemplateEntries` + `hullWeaponTemplateEntries`), the controller's `SlotToWeaponMountIndex(slot, mount)` returns which array index to use
4. Vanilla `SetWeapon` mesh-swaps the weapon's geometry into the mount at that index

**Therefore**: changing a mount's sibling index in the prefab → changes its array index → potentially breaks the controller's mapping. Adding a new mount → extends the array → the controller must know to map a new slot to it.

### 23.2 Slot vs Index - the terminology trap

These two concepts are easy to confuse and the cfg/JSON uses both:

- **Slot** = JSON-defined logical position. In `shipModuleSlots[N]`, the slot's "slot index" is `N` (its position in the array). Slots have type (HullHardPoint, NoseHardPoint, Utility, Drive, etc.) and (x,y) grid coordinates.
- **Index** (mount index) = position in the prefab's per-side weapon controller array (noseWeaponControllers[index], dorsalHullWeaponControllers[index], ventralHullWeaponControllers[index]). Determined by sibling order under the hull root.

`SlotToWeaponMountIndex(int slot, Mount mount) → int` is the bridge: takes a slot-index + Mount enum (telling it the Mount layout), returns mount-index. The Mount enum encodes whether the SAME slot is rendered as 1 nose / 2 nose horizontal / 4 hull / etc. - different Mount values for the same slot map to different mount-indices.

**Mount enum** (12 named values):
```
OneNose, OneHull,
TwoNoseHoriz, TwoNoseVert, TwoHullHoriz, TwoHullVert,
ThreeNoseAngle, ThreeHullHoriz,
FourNose, FourHull,
HalfNose, HalfHull
```

**Numeric ordering** inferred from controller switch patterns (`(uint)(mount - 9) <= 3u` in TitanController, `(uint)(mount - 11) <= 1u` in LancerController):
- TitanController checks "is it FourNose, FourHull, HalfNose, or HalfHull" → values 9, 10, 11, 12
- LancerController checks "is it HalfNose or HalfHull" → values 11, 12
- This gives the inferred enum order: `OneNose=0, OneHull=1, TwoNoseHoriz=2, TwoNoseVert=3, TwoHullHoriz=4, TwoHullVert=5, ThreeNoseAngle=6, ThreeHullHoriz=7, ?=8, FourNose=9, FourHull=10, HalfNose=11, HalfHull=12`
- Position 8 is unknown - there are exactly 12 NAMED values in source but the numeric pattern requires one at position 8. It may be `Mount.None` (a default/invalid value not used in any switch case).

### 23.3 Current ShipTools `Add Weapon Mount` capability and limits

(See §16 for the full menu inventory.) ShipTools' current `Hull/2. Add Weapon Mount` does:

1. **Selection**: select one or more existing weapon mounts in Hierarchy
2. **Validation per mount**: parent exists, ≥1 child (Gun), ≥1 grandchild (FirePoint)
3. **Naming gate**: name must contain `nose`/`dorsal`/`ventral` substring
4. **Duplication**: `Object.Instantiate(src, parent)` clones the mount including all components
5. **Auto-naming**: appends incrementing number (`<core> 2`, `<core> 3`, ...)
6. **Sibling placement**: `SetSiblingIndex(srcIdx + 1)` - places immediately after source
7. **Component registration**: finds ship controller via SerializedObject duck-type (looks for any component with all 3 weapon array properties), inserts new mount's `ShipWeaponVisController` into the matching SerializedProperty array

**What it does well**:
- Idempotent: re-running on same source produces clean numbered duplicates
- Auto-registers in controller's serialized array (no manual Inspector work)
- Correctly handles sibling-index ordering
- Multi-select friendly

**What it CANNOT do today** (this is the gap):
- ❌ **Remove a mount** (no `Remove Weapon Mount` menu item)
- ❌ **Reorder mounts** (sibling index changes are manual; controller array index changes are manual)
- ❌ **Add mounts of a NEW type** to a hull that has none of that type (requires knowing the controller field name to register into; current code derives field name from the mount's existing name)
- ❌ **Bulk operations** (e.g., "rebuild all mount registrations from current hierarchy")
- ❌ **JSON sync** - `shipModuleSlots` array in TIShipHullTemplate.json must be edited manually after prefab changes
- ❌ **Mount index → slot mapping update** - HullDefinitions.cfg must be edited manually

### 23.4 Slot index = JSON array position

**Finding from TISpaceShipTemplate.cs:1108** (`ValidAssignedSlotForLocation`):
```csharp
public bool ValidAssignedSlotForLocation(TIShipPartTemplate partTemplate, int slot)
{
    if (slot < this.hullTemplate.shipModuleSlots.Count
        && partTemplate.allowedSlots.Contains(this.hullTemplate.shipModuleSlots[slot].moduleSlotType))
    {
        // ... mount-specific validation
    }
    return false;
}
```

**The `slot` integer is the direct array index into `hullTemplate.shipModuleSlots`.** It is NOT a separate slot ID, NOT a slot name, NOT a Vector2 coordinate - just an int that says "the Nth slot in the JSON array."

**Implications**:
1. Slot 0 in JSON = slot index 0 at runtime
2. Reordering the array reorders the slot indices
3. Inserting a slot anywhere except the END shifts all subsequent indices
4. The `weaponSlotMap` in `HullDefinitions.cfg` references these same indices - they MUST stay in sync with the JSON array order

### 23.5 The utility/weapon interleave problem ⚠️ SAVE-BREAKING TRAP

**The JSON `shipModuleSlots` array contains ALL slot types interleaved** - Drive, PowerPlant, Utility, Radiator, NoseHardPoint, HullHardPoint, NoseArmor, LateralArmor, TailArmor - in arbitrary order. From BK's existing `TIShipHullTemplate.json`:

```json
"shipModuleSlots": [
  { "moduleSlotType": "Drive",        "x": 2, "y": 3 },   // index 0
  { "moduleSlotType": "PowerPlant",   "x": 3, "y": 3 },   // index 1
  { "moduleSlotType": "Utility",      "x": 5, "y": 3 },   // index 2
  { "moduleSlotType": "Radiator",     "x": 4, "y": 3 },   // index 3
  { "moduleSlotType": "TailArmor",    "x": 2, "y": 6 },   // index 4
  { "moduleSlotType": "LateralArmor", "x": 4, "y": 6 },   // index 5
  // ... HullHardPoint and NoseHardPoint slots come later
  // ... weapons at index 7+ for typical hulls
]
```

**The trap**: Adding a utility slot in the MIDDLE of the array shifts every subsequent slot index. **Every weapon currently positioned with `slot: 8` in any TISpaceShipTemplate JSON entry would now refer to a different physical slot.**

**Worked example - Lancer style hull**:

Before edit:
```
[0] Drive
[1] PowerPlant  
[2] Utility
[3] Radiator
[4] TailArmor
[5] LateralArmor
[6] NoseArmor
[7] NoseHardPoint   ← weapon slot 7 maps to nose mount index 0
[8] HullHardPoint   ← weapon slot 8 maps to dorsal/ventral mount index 0
[9] HullHardPoint   ← weapon slot 9 maps to dorsal/ventral mount index 1
```

User adds a new Utility slot in position 3 (between PowerPlant and Radiator):
```
[0] Drive
[1] PowerPlant
[2] Utility
[3] Utility       ← NEW - inserted here
[4] Radiator      ← was [3]
[5] TailArmor     ← was [4]
[6] LateralArmor  ← was [5]
[7] NoseArmor     ← was [6]
[8] NoseHardPoint ← was [7]  - weapon designs with slot:7 now resolve to NoseArmor!
[9] HullHardPoint ← was [8]
[10] HullHardPoint ← was [9]
```

**What breaks**:
- Every TISpaceShipTemplate (player saves AND vanilla designs) that uses this hull and references slot 7+ now points to wrong slot type
- `ValidAssignedSlotForLocation` will return false (NoseHardPoint weapon trying to go in NoseArmor slot)
- Log spam: `"Bad nose weapon placement for X in Y"` (TISpaceShipTemplate.cs:1474, 1559)
- HullDefinitions.cfg `weaponSlotMap` entries for slot 7+ now map wrong slots → wrong mount indices → wrong physical mounts on prefab

**This is the #1 hidden landmine in slot editing.**

### 23.6 Safe vs unsafe slot edits

| Edit | Safety | Why |
|---|---|---|
| Add slot at END of array | ✅ SAFE | No existing index shifts |
| Add slot in MIDDLE | ❌ SAVE-BREAKING | All subsequent indices shift |
| Remove slot at END | ⚠️ Save-breaking only if any design uses that index | Can be safe if last slot was unused |
| Remove slot in MIDDLE | ❌ SAVE-BREAKING | All subsequent indices shift |
| Change `moduleSlotType` of an existing slot | ⚠️ Save-breaking if designs use it | The slot type validation will fail |
| Change `x` / `y` coordinates | ✅ SAFE | Only affects UI display position, not slot index |
| Reorder array (any swap) | ❌ SAVE-BREAKING | All indices shift |

**The safe pattern for adding slots**: append to the end of the array. New slot gets a new high index, no existing index changes, all existing designs still work.

**The unsafe pattern**: inserting where it "logically belongs" (e.g., grouping all utility slots together).

### 23.7 Utility modules have NO PREFAB PRESENCE

**Utility modules have no visual representation on the ship prefab.**

Source: `ShipModelController.BuildWeapons` (ShipModelController.cs:1170-1200):
```csharp
private void BuildWeapons(ShipVisController parentController, TISpaceShipTemplate ship, TISpaceShipState shipState = null)
{
    foreach (ShipWeaponVisController shipWeaponVisController in this.allWeaponControllers)
        shipWeaponVisController.baseObject.SetActive(false);
    
    foreach (ModuleDataEntry moduleDataEntry in ship.noseWeapons) { ... }    // ONLY weapons
    foreach (ModuleDataEntry moduleDataEntry2 in ship.hullWeapons) { ... }   // ONLY weapons
    // NO iteration over ship.utilityModules - utility modules NEVER visualized
}
```

**`SetShipPart` is defined** (ShipModelController.cs:1091) but **NEVER CALLED in any archived file**. It may be vestigial, used by external code we don't have, or reserved for future use.

**Implication for tooling**:
- Adding a utility slot = JSON-only edit (add entry to `shipModuleSlots`)
- No prefab work needed
- No mount transform needed
- No `ShipWeaponVisController` component needed
- No HullDefinitions.cfg entry needed (cfg only maps weapon slots → mount indices)

### 23.8 Data recompute flow

When does TI recompute mass/cost/power/heat?

Source: `TISpaceShipTemplate.CacheTemplateValues(bool skipCost = false)` (TISpaceShipTemplate.cs:131-141):
```csharp
public void CacheTemplateValues(bool skipCost = false)
{
    if (!skipCost)
        this.spaceResourceConstructionCost(true, null, true, false, false);
    this.dryMass_tons(true);
    this.baseCruiseAcceleration_mps2(true);
    this.baseCruiseDeltaV_mps(true);
    this.BatteryCapacity_GJ(true);
    this.HeatCapacity_GJ(true);
}
```

**6 properties get recomputed in this exact order**, each with `forceUpdate=true`. The `_xxx = -1f` sentinel pattern (per §22.8) is invalidated by `forceUpdate`.

**When is `CacheTemplateValues` called?** Search results show it's called in `TIFactionState` and ship-design UI flows after design changes. Not called automatically when JSON changes - JSON values are read fresh each time.

**For a modder**: After editing JSON `shipModuleSlots`, restart the game. JSON is loaded fresh; ship templates are re-instantiated. Cached values are invalidated by template recreation. **No manual recompute needed for JSON edits.**

**For player save modifications**: If a hull's slot count changes between save and load, the saved TISpaceShipTemplate's `cachedXxx` arrays may be stale. Force a recompute via `template.CacheTemplateValues(false)` after load. (BK doesn't do this currently.)

### 23.9 Combat impact when slots change

When weapon mount counts change between hull versions:

**Per-shot fire impact**:
- `ShipWeaponVisController.Fire(truncated, time)` uses `firePoint.transform.position` for shot origin
- If a mount is removed but a saved design references it, `noseWeaponControllers[index]` may throw IndexOutOfRange
- Beam controllers have `BeamWeaponController.Initialize(target)` which assumes the controller is wired

**Damage allocation impact**:
- DamageLayer uses `_DamagePointArray` shader uniform - this operates on the prefab's overall hull mesh, NOT per-mount
- Damage location is geometry-based, not mount-based
- Removing a mount doesn't break damage layer - but if a weapon is destroyed and the controller tries to swap to "destroyed" model, missing mount = exception

**Thruster firing impact**:
- `ActivateThrusters/DeactivateThrusters` operate on `thrusterEffectContainers[i]` for `i in 0..thrusters` (per §22.6)
- Thruster count is from drive (`drive.thrusters * hullTemplate.thrusterMultiplier`) - NOT from slot count
- **Slot changes do NOT affect thruster firing** ✓

**Save-load impact** (per §21):
- TIShipHullTemplate is NOT null-safe - slot index out of range will throw
- AI designs using removed slots: log spam, may cause AI to skip the design
- Player designs using removed slots: ship may fail to render or crash on combat enter

### 23.10 Cross-references

For complete understanding of slot editing:
- **§1** TIShipHullTemplate (NOT null-safe) and TISpaceShipTemplate (lazy resolution)
- **§5** ShipModuleSlotType enum (10 values: Drive, PowerPlant, Utility, Radiator, NoseHardPoint, HullHardPoint, NoseArmor, LateralArmor, TailArmor, None)
- **§16** ShipTools editor menu (current capability)
- **§17** SlotToWeaponMountIndex per-controller switches (consumes slot index)
- **§21** Save and load (HULL RENAMES are save-breaking - same severity as slot reordering)
- **§22.6** WhichRadiators algorithms (radiator slot count doesn't directly affect radiator visualization)
- **§22.7** Weapon mount/firing pipeline (consumes slot index via SlotToWeaponMountIndex)
- **§22.8** TISpaceShipTemplate JSON fields (`moduleTemplateEntries`, `noseWeaponTemplateEntries`, `hullWeaponTemplateEntries` - each entry has a `.slot` field that IS the JSON array index)

---

## 24. Engine Expectations Reference - what TI demands of mod prefabs

This section documents **exactly what TI's runtime expects from mod hull prefabs** at the Unity component level. It's the diagnostic reference for the three known bugs (white hulls / no thrusters / no engines) and the safety net for any future hull modification.

### 24.1 The diagnostic flowchart

For each known issue, follow this chain:

#### Issue: HULL RENDERS ALL WHITE

```
SYMPTOM: Ship appears as solid white silhouette in combat or strategy view
│
├─ CHECK 1: Streaming Mipmaps disabled on textures?
│            Verify: ShipTools/Setup/2 was run; BKTextureImporter.cs in Editor/
│            Source: §4 Texture Streaming
│
├─ CHECK 2: Standard shader removed from m_AlwaysIncludedShaders?
│            Verify: ShipTools.BundleSettingsOk() returns true
│            Source: §4 + ShipTools.cs:794-818
│
├─ CHECK 3: Renderers actually have materials baked?
│            Verify: BakeFactionSkin assigned to renderer.sharedMaterial
│                    For each MeshRenderer, check Inspector: material slot not None
│            Common cause: renderer's GameObject was renamed; MAT_<oldName>_<faction>.mat
│                          doesn't exist; BakeFactionSkin silently skipped (continue)
│            Source: ShipTools.cs:222-237 BakeFactionSkin
│
├─ CHECK 4: No DUPLICATE entries in controller weapon arrays?
│            Verify: Run Verify Hull; CheckArray flags duplicates with "renders white"
│            Source: ShipTools.cs:726-746 CheckArray
│
├─ CHECK 5: Materials reference shaders that exist in BK's bundle?
│            Verify: Each material's shader is one of:
│                    - URP/Standard variant (shipped with TI)
│                    - Custom shader compiled into BK's bundle
│            Cross-bundle shader references CAN render white if shader unavailable
│
└─ CHECK 6: Are vanilla materials (cross-bundle refs) being used after edits?
             If the user copies a vanilla prefab and DOESN'T re-bake, the renderer's
             sharedMaterial points at a vanilla material in the vanilla bundle.
             At TI runtime, the vanilla bundle IS loaded, so this CAN work - but
             only if the vanilla material asset still exists with the original GUID.
             Renaming the renderer's GameObject does NOT break this; only changing
             the material reference does.
```

#### Issue: THRUSTER ANIMATIONS NOT SHOWING

```
SYMPTOM: Ship has drive but no flame/particle FX during burns
│
├─ CHECK 1: Is shipState.thrustersActive being set true?
│            Verify: Fleet acceleration phase or strategy burn triggers ActivateThrusters()
│            Source: TISpaceFleetState.SetAccelerationPhaseStatus (line 4922)
│                    StrategyShipController.Update (line 335)
│                    TISpaceShipState.ActivateThrusters (line 5370)
│            Note: thrustersActive is FALSE by default; will be set true on first burn
│
├─ CHECK 2: Were thrusterEffectContainers populated?
│            Verify: SetDrive successfully populated thrusterEffectContainers list
│            Two failure modes:
│              (a) thrusterLocations.Length == 0 → loop body never executes
│              (b) drive prefab has no Thruster/ThrusterPoint children → list is empty
│            Source: ShipModelController.SetDrive (line 995-1057)
│
├─ CHECK 3: Hull prefab's "Drive" child has "ThrusterPoint" children?
│            Verify: Open hull prefab in Unity, expand Drive child, count GameObjects
│                    with "ThrusterPoint" in name
│
├─ CHECK 4: Drive prefab has matching Thruster children?
│            Verify: Drive prefab has GameObjects with name containing
│                    "Thruster" / "ThrusterPoint" / "thruster" (excluding "Thruster_Alien")
│            Common cause: Drive prefab created via Create Hull From Vanilla copies
│                          the hull's Drive child verbatim - so ThrusterPoint children
│                          should be present. If user DELETED them, no FX.
│
├─ CHECK 5: thrusterFXPrefabs cache contains the drive's FX path?
│            Verify: drive.MainThrusterFXResource(IsAlien) returns one of:
│                    "ships/HumanThrusterBasic"
│                    "ships/HumanThrusterAdvanced"
│                    "ships/AlienThruster"
│                    "ships/NuclearThruster"
│            Source: AssetCacheManager.cs:290-308 (hardcoded dict, 4 entries)
│            Source: TIDriveTemplate.cs:147-178 MainThrusterFXResource
│            Note: Determined by drive's nozzle (DeLaval/Magnetic/Pulsed) + alien flag
│            Hard limit: ONLY 4 thruster FX types possible - modders cannot add new ones
│                        without patching the dictionary (Harmony or reflection)
│
├─ CHECK 5b: thrusterFXPrefabs entries are not null?
│             Verify: At game start, AssetCacheManager static field initializers call
│                     GameControl.assetLoader.LoadAsset<GameObject>("ships/HumanThrusterBasic")
│                     etc. If those vanilla bundle paths fail to load (vanilla bundle
│                     missing, asset renamed in TI update), the dict entries are NULL.
│             Source: AssetCacheManager.cs:290-308 + AssetLoader.cs:34-42
│             Symptom: SetDrive's Object.Instantiate(thrusterFXPrefab, ...) throws on
│                      null prefab → silent failure with NRE in log
│             Mitigation: Verify vanilla "ships" bundle is loaded; check Player.log for
│                         "Bundle not found! bundle name: ships" or
│                         "No asset found for ships/HumanThrusterBasic"
│
├─ CHECK 6: ParticleSystems on the FX prefab actually exist?
│            Verify: Loaded thruster FX prefab has ParticleSystem components in children
│            Source: SetDrive uses GetComponentsInChildren<ParticleSystem>(true)
│            Note: includeInactive=true; will pick up disabled ParticleSystems too
│
```

#### Issue: NO ENGINES SHOWING IN COMBAT

```
SYMPTOM: Imported (extracted+modified vanilla) hull renders, but the drive is invisible
         (the area where the engine bell/nozzle should be is empty/black)
│
├─ CHECK 1: Does hull prefab have a "Drive" child?
│            Verify: Hull root has a child GameObject named exactly "Drive"
│
├─ CHECK 2: Does the "Drive" child have MeshFilter + MeshRenderer components?
│            Verify: Open hull prefab; Drive child Inspector shows both components
│            Note: SetDrive does targetObject.GetComponent<MeshFilter>().sharedMesh = ...
│                  with NO null check. Missing component = NullReferenceException.
│            Source: ShipModelController.SetDrive (line 1001)
│            CRITICAL: This is a silent bug if you remove the MeshFilter accidentally
│
├─ CHECK 3: Does the corresponding drive prefab exist in BK's bundle?
│            Verify: BK's bundle contains <HullName>_Drive.prefab
│                  comes from BK's HullRegistry registration, typically "<HullName>_Drive"
│            Source: ShipTools.cs CreateDrivePrefab (line 201-216)
│
├─ CHECK 4: Drive prefab itself has MeshFilter + MeshRenderer on root?
│            Verify: Open <HullName>_Drive.prefab; root has both components
│            Note: SetDrive does loaded.GetComponent<MeshFilter>().sharedMesh - NO null check
│            Common cause: User edited hull's Drive child to remove its visible mesh
│                          (because they're using a different drive aesthetic), but
│                          ShipTools' CreateDrivePrefab snapshots the Drive child verbatim
│
├─ CHECK 5: Drive prefab's material is loadable?
│            Verify: drive prefab's MeshRenderer.sharedMaterial is non-null
│            Note: SetDrive uses gameObject.GetComponent<MeshRenderer>().sharedMaterial
│                  (when variableMaterial=false and not skirmish-alien)
│            Common cause: drive prefab references a vanilla material whose GUID
│                          changed between TI versions
│
├─ CHECK 6: thrusterModel field is set?
│                    (only if currently null)
│            Note: If thrusterModel is null at SetDrive time, NRE in
│                  targetObject.GetComponent<MeshFilter>()
│
└─ CHECK 7: ship.hullTemplate.simpleHull == false?
             Verify: BK hull JSON has simpleHull=false (default)
             Note: simpleHull=true bypasses prefab loading entirely; uses targetObject
                   as-is. If accidentally set true, drive's mesh swap is skipped.
             Source: ShipModelController.SetDrive line 1023 (if simpleHull branch)
```

### 24.2 Hull prefab invariants (must-haves)

These are the components and structure TI demands. Violation = NRE or silent failure.

#### Root GameObject (the hull prefab itself)

| Component | Required | Set by | Read by |
|---|---|---|---|
| Transform | YES (Unity baseline) | - | All |
| `<X>Controller` (e.g., LancerController) extends ShipModelController | YES | Inherited from extracted vanilla | ShipVisController.GetComponent<ShipModelController>() at line 49 |
| CapsuleCollider | YES (checked by ShipTools.VerifyHull) | Manual or extracted vanilla | TI hit detection |
| DamageLayer | YES | Inherited from extracted vanilla | Damage shader uniform updates |
| Layer = `Ignore Raycast` (2) | YES (checked by VerifyHull) | Manual or extracted vanilla | TI ship-vs-environment raycast filtering |

#### Required children of the hull root

(Names ARE EXACT; case-sensitive; no whitespace deviations.)

| Child name | Required | Purpose | What populates it (BK pipeline) |
|---|---|---|---|
| `Hull` | YES | Hosts hull mesh + child meshes for skin material assignment | Renamed body parent during ShipTools.CreateHullFromVanilla |
| `Drive` | YES | Hosts drive mesh; ShipTools.AddWeaponMount and BakeDriveVariants populate ThrusterPoint children | Inherited from extracted vanilla; mesh swapped at runtime via SetDrive |
| `_ExplosionSequenceRoot` | YES (checked by VerifyHull) | Hosts destruction VFX | Inherited from extracted vanilla |
| `SelectionReticle` | YES | Hosts selection visual when ship is selected | Inherited from extracted vanilla |
| `GroupSelectionReticle` | YES | Hosts group-selection visual | Inherited from extracted vanilla |
| `Padlock Container` | YES | Hosts padlock icon (locked-state UI) | Inherited from extracted vanilla |
| `Radiator12`, `Radiator3`, `Radiator130`, `Radiator6`, `Radiator4`, `Radiator430`, `Radiator730`, `Radiator1030`, `Radiator8`, `Radiator9` | YES (10 fin radiators) | Active radiator subset selected by WhichRadiators | Inherited from extracted vanilla |
| `spikes 12`, `spikes 3`, `spikes 6`, `spikes 9` | YES (4 spike radiators) | Active when RadiatorType=Spike | Inherited from extracted vanilla |
| `Droplet12`, `Droplet8`, `Droplet4` | YES (3 droplet radiators) | Active when RadiatorType=Droplet | Inherited from extracted vanilla |
| (variable) Weapon mounts containing `nose`/`dorsal`/`ventral` substring | YES (≥1 of each typically) | ShipTools.AddWeaponMount populates per-side weapon controller arrays at edit time | ShipTools.AddWeaponMount or extracted vanilla |

**Total**: 6 required RequiredChildren + 17 radiators + N weapon mounts = **23+ named children minimum**.

For ALIEN hulls: only the 4 `radiator1030/130/430/730` are present; spike/droplet radiators are NOT on alien prefabs. Alien hulls extend `AlienShipController` (separate inheritance branch from HumanShipController).

#### Drive child component requirements

The `Drive` child of the hull prefab is the target for SetDrive's mesh swap:

| Component on Drive child | Required | Why |
|---|---|---|
| MeshFilter | YES | `targetObject.GetComponent<MeshFilter>().sharedMesh = loaded.sharedMesh` |
| MeshRenderer | YES | `targetObject.GetComponent<MeshRenderer>().sharedMaterial = ...` |
| Layer = `HurtBox` (17) | RECOMMENDED (checked by VerifyHull) | Combat damage targeting |
| `ThrusterPoint*` children | YES (≥1) | Vanilla SetDrive populates `thrusterLocations` from these |

**Critical mismatch potential**:
- ShipTools `Verify Hull` checks for ThrusterPoint children before bake
- SetDrive's filter for `list`: child name CONTAINS `"Thruster"` OR `"ThrusterPoint"` OR `"thruster"` (excluding `"Thruster_Alien"`)

Both lists must have the SAME COUNT for thruster FX to match positions correctly. If the hull's Drive child has 4 "ThrusterPoint" children but the drive prefab has 6 "Thruster" children, SetDrive's loop only iterates min(4, 6) = 4 - the extra 2 in the drive prefab are unused.

### 24.3 Drive prefab invariants (the separate `<HullName>_Drive.prefab`)

The drive prefab is loaded by vanilla `SetDrive` via `assetLoader.LoadAsset<GameObject>("<modBuildName>/<driveName>")`. The path is resolved by `DriveVisualPatch` for BK-registered hulls.

| Component on drive prefab root | Required | Why |
|---|---|---|
| MeshFilter | YES | `loaded.GetComponent<MeshFilter>().sharedMesh` (NO null check at SetDrive line 1001) |
| MeshRenderer | YES | `loaded.GetComponent<MeshRenderer>().sharedMaterial` (NO null check at SetDrive line 1015) |
| Children with `Thruster*` substring | YES (≥1) | Position template for thruster FX placement |

**ShipTools' CreateDrivePrefab process** (ShipTools.cs:201-216):
1. `transform.Find("Drive")` on the hull instance - finds the Drive child
2. `Object.Instantiate(driveSrc.gameObject)` - clones it
3. Names it `<HullName>_Drive`, removes parent, resets transform
4. `PrefabUtility.SaveAsPrefabAsset` to `Assets/<HullName>_Drive.prefab`
5. Tags the bundle name

This means **the drive prefab is a snapshot of the hull's Drive child**. If the hull's Drive child has the right components (MeshFilter+MeshRenderer+ThrusterPoint children), the drive prefab does too. If the user MODIFIED the hull's Drive child after CreateDrivePrefab, the drive prefab is now stale.

**Stale drive prefab is a frequent silent failure**:
- User runs `Create Hull From Vanilla` → drive prefab created with N ThrusterPoint children
- User edits hull prefab to add more thrusters → hull now has N+M ThrusterPoint children
- Drive prefab still has N ThrusterPoint children
- Result: thrusterEffectContainers loop iterates min(N+M, N) = N positions; the M new positions get no FX

**Fix**: re-run `Create Hull From Vanilla` (overwrites drive prefab) or run `Finalize and Ship`, which calls `BakeDriveVariants` on every tagged hull prefab and rebuilds the variant children.

### 24.4 Material/shader requirements

#### What the renderer's sharedMaterial must be

| Material source | Result at runtime |
|---|---|
| Vanilla material from extracted bundle (cross-bundle reference) | Works IF vanilla bundle still has it AND GUID hasn't changed |
| Material in BK's bundle (same-bundle reference) | Works always |
| Material referencing Standard shader | Works IF Standard NOT removed from m_AlwaysIncludedShaders (TI removes Standard, so this fails - see §4) |
| Material referencing custom shader compiled into BK bundle | Works always |
| Null material | Renders the **MAGENTA** error material (Unity default for missing material) |

**Common confusion**: "white hull" is NOT the magenta error material. White hull = material loaded but shader can't bind properly. Magenta hull = material is null.

#### What ShipTools' BakeFactionSkin actually does

(ShipTools.cs:222-237):
```csharp
foreach (MeshRenderer mr in scope.prefabContentsRoot.GetComponentsInChildren<MeshRenderer>(true))
{
    Material mat = AssetDatabase.LoadAssetAtPath<Material>(
        $"Assets/Material/MAT_{mr.gameObject.name}_{faction}.mat");
    if (mat == null) continue;  // ← SILENT SKIP
    mr.sharedMaterial = mat;
    assigned++;
}
```

**SILENT SKIP IS THE WHITE-HULL TRAP**:
- Renderer's GameObject was renamed (e.g., from `Battlecruiser_Hull` to `MyShip_Body`)
- BakeFactionSkin looks for `MAT_MyShip_Body_resist.mat`
- File doesn't exist → `mat == null` → `continue`
- Renderer keeps OLD material from extracted vanilla (a cross-bundle reference)
- At runtime: shader binding may fail → renders white

**Diagnosis approach**: After BakeFactionSkin runs, inspect every MeshRenderer's sharedMaterial:
- If material's name starts with `MAT_<currentChildName>_<faction>` → bake worked
- If material's name starts with `MAT_<oldChildName>_<faction>` → bake skipped, stale material
- If material is null → renders magenta (different bug)

### 24.5 The thruster activation lifecycle (full chain)

Documented for diagnostic purposes. This is the FULL chain from "user issues fleet command" to "particle FX appears":

```
1. PLAYER ACTION: User clicks "burn" / fleet maneuver
   ↓
2. TISpaceFleetState.SetAccelerationPhaseStatus(true)
   - Iterates ships
   - Calls ships[i].ActivateThrusters() and InitiateManuever
   ↓
3. TISpaceShipState.ActivateThrusters()
   - if (!thrustersActive) { thrustersActive = true; SetVisualizationDataDirty(); }
   ↓
4. TISpaceShipState.SetVisualizationDataDirty()
   - Triggers ShipVisualizationDataDirty event
   ↓
5. StrategyShipController.OnShipVisualizationDataDirty
   - Sets dataDirty = true
   ↓
6. StrategyShipController.Update (next frame)
   - When in burn trajectory, checks inPreviousBurn flag
   - If trajectory.IsInBurn(currentTime) && !inPreviousBurn:
     ModelController.ActivateThrusters(true);
     inPreviousBurn = true;
   ↓
7. ShipModelController.ActivateThrusters(playAudio)
   - for (i=0; i<thrusters; i++) thrusterEffectContainers[i].Play();
   - if (playAudio) AudioManager.CreateFMODInstance(...)
   ↓
8. MultiEffectContainer.Play()
   - Calls Play() on each ParticleSystem in its list
   ↓
9. ParticleSystem.Play()
   - Unity ParticleSystem starts emitting

[Concurrent path on ship instantiation:]
ShipVisController.InitializeShipVisualizer (line 91)
  if (shipState.thrustersActive)
    ModelController.ActivateThrusters(false);  ← starts thrusters if ship was loaded mid-burn
```

**Critical fact**: ActivateThrusters is **NOT** called automatically when a ship spawns in combat. Combat-mode thruster activation requires either:
- The ship arrived during a burn (thrustersActive set true in strategic layer), OR
- Combat-mode burn triggers the activation

If a ship is parked in orbit with thrusters off and you enter combat, **the thrusters stay off** until something orders a maneuver. This is correct behavior but can confuse modders who expect thrusters to be visible by default.

### 24.6 BK-specific gotchas

#### Gotcha 1: `BakeFactionSkin` silent skip on renamed children (white hull cause)

When you rename a renderer's GameObject after `Create Hull From Vanilla`, the bake pass silently skips it. The renderer keeps its old material - a cross-bundle reference that may or may not work at runtime.

**Workaround**: Don't rename renderer GameObjects. If you must rename, ALSO create a new MAT_<newName>_<faction>.mat file in `Assets/Material/`.

#### Gotcha 2: Drive prefab is a SNAPSHOT of hull's Drive child (no engines after edit)

`CreateDrivePrefab` copies hull's Drive child verbatim. Subsequent edits to the hull's Drive child do NOT propagate to the drive prefab.

**Workaround**: Re-run `Create Hull From Vanilla` to regenerate the drive prefab (this WILL overwrite your hull edits - copy them out first).

#### Gotcha 3: Thrusters off until first burn

`ActivateThrusters` is conditional on `shipState.thrustersActive`. A ship parked in orbit has thrusters off. Modders testing in combat may see "no thrusters" and assume something's broken - but if the ship hasn't been ordered to maneuver yet, thrusters being off is correct.

**Workaround**: Order a fleet maneuver in-game; thrusters should activate. Alternatively, force thrustersActive=true via console (if available) for testing.

#### Gotcha 4: Only 4 thruster FX types possible

`AssetCacheManager.thrusterFXPrefabs` is a 4-entry hardcoded dictionary. Modders cannot add new thruster FX without patching the dictionary at runtime via Harmony or reflection.

**Implication**: BK can't ship custom thruster particle effects without Harmony-patching the dictionary's initialization (or adding via reflection at mod load).

### 24.7 Cross-references

- **§4** Texture Streaming + Always Included Shaders (white hull root causes)
- **§16** ShipTools current capabilities (BakeFactionSkin, Verify Hull)
- **§18** Edit-Time Material Bake Workflow
- **§22.6** WhichRadiators algorithm + SetDrive body (lines 995-1057)
- **§22.7** Weapon mount/firing pipeline (FirePoint hierarchy requirements)

---

## 25. Component & Controller Reference

This section consolidates Unity component class details referenced elsewhere in the dictionary.

### 25.1 ColorAnimationEffect

**Class declaration**: `public class ColorAnimationEffect : AbstractEffectController` (extends an unsourced base - `AbstractEffectController` not yet in archive but we don't need its body for the inheriting class behavior).

**Public enum** ColorBlendMode:
- `OVERRIDE = 0` - replace material color with animation color
- `ADDITIVE = 1` - color + base color
- `MULTIPLICATIVE = 2` - color * base color (DEFAULT)

**Serialized fields** (8 total - these are the JSON-deserializable / Inspector-visible fields):
- `m_useScaledGameTimeCheck` (bool) - only checked OnEnable; later changes ignored
- `m_squareIntensity` (bool, default true) - `Pow(2, intensity)` instead of linear
- `m_blendMode` (ColorBlendMode, default MULTIPLICATIVE)
- `m_colorAnimation` (Gradient, GradientUsage(true) - i.e. allows HDR colors)
- `m_intensityAnimation` (AnimationCurve, default 0→1f to 1→0f)
- `m_duration` (float, default 1f) - total animation length in seconds
- `m_targetRenderers` (Renderer[]) - which renderers get the color animation
- `m_targetUniformName` (string, default "_EmissionColor") - shader uniform to drive

**Lifecycle**:
- `Awake()`: caches `Shader.PropertyToID(m_targetUniformName)` into `m_targetUniform`
- `OnEnable()`: if `m_useScaledGameTimeCheck`, gets GameTimeManager from `World.Active.GetExistingManager<GameTimeManager>()`; calls base.OnEnable()
- `OnPlay()`: walks each target renderer, finds materials with the target uniform, captures original color into `(Material, Color)` tuples
- `OnUpdate(deltaTime)`: applies blend mode to material colors based on Gradient.Evaluate(progress) and intensity curve

**Diagnostic implications**:
- ColorAnimationEffect drives `_EmissionColor` shader uniform via `material.SetColor(m_targetUniform, color * intensity)`
- If a material doesn't HAVE the `_EmissionColor` property, it's skipped (not added to `m_targetMaterials`)
- For "edited extracted vanilla" hulls: if the user replaces a renderer's material with one that doesn't support `_EmissionColor`, the radiator's color animation has NO EFFECT - silent
- The check `material.HasProperty(m_targetUniform)` is the silent-skip filter

**For BK hulls**: each radiator GO has a ColorAnimationEffect component. After material baking via ShipTools, if the baked material's shader supports `_EmissionColor`, animation works. If not, radiator looks dead but doesn't error.

### 25.2 DamageLayer

**Class declaration**: `public class DamageLayer : MonoBehaviour`

**Static fields** (cached shader property IDs):
- `s_uDamagePointArray = Shader.PropertyToID("_DamagePointArray")`
- `s_uDamagePointArrayLength = Shader.PropertyToID("_DamagePointArrayLength")`

**Serialized fields**:
- `_shipDamageMaterial` (Material) - the damage decal material template (one per ship)
- `_shipRenderers` (Renderer[]) - array of renderers that should display damage
- `_clearDamageOnUpdate` (bool) - if true, damage points clear every LateUpdate (for transient effects)

**Runtime fields**:
- `_originalMaterials` (Dictionary<Renderer, Material[]>) - saved materials at OnEnable
- `_damageMaterials` (List<Material>) - instance-cloned damage materials per renderer
- `_damagePoints` (List<Vector4>) - current damage points (max 8)
- `refShipState` (TISpaceShipState) - captured at Start()

**Lifecycle**:

1. **Start()**: `refShipState = GetComponent<ShipModelController>()?.GetRefShipState();` - captures ship state
2. **OnEnable()**:
   - Calls SyncDamageVisualizations (loads damagePoints from shipState)
   - For each renderer in `_shipRenderers`:
     - Saves original materials to `_originalMaterials`
     - Creates a new Material instance from `_shipDamageMaterial` template
     - Adds the new material to renderer's materials array (renderer now has N+1 materials)
     - Stores the new material in `_damageMaterials`
3. **OnDisable()**: restores original materials, destroys cloned damage materials
4. **LateUpdate()** (every frame):
   - Builds Vector4[8] array (zero-padded if fewer than 8 damage points)
   - For each `_damageMaterials` entry: SetInt + SetVectorArray on shader uniforms
   - If `_clearDamageOnUpdate`: ClearDamagePoints

**`AddDamagePoint(hitPosition, radius, damageType)`**:
- Calls `refShipState.SyncDamageVisuals()`
- Clamps radius: `Mathf.Clamp(radius * (count+1) * 1.02f, 1f, 50f)` - radius grows with damage point count!
- Packs (radius/50.1, damageType/2) into a single float via `GetPackedFloat`
- Stores Vector4(x, y, z, packedFloat) in damagePoints
- If count > 8: sorts by .w descending, removes lowest

**`GetPackedFloat(a, b)`** - packs two normalized floats into one uint:
```csharp
uint num = (uint)(a * 65535f);
uint num2 = (uint)(b * 65535f);
return (num << 16) | (num2 & 0xFFFF);
```

**Shader uniforms passed**:
- `_DamagePointArray` (Vector4[8]) - XYZ = world position, W = packed (radius, damageType)
- `_DamagePointArrayLength` (int) - number of active damage points (0-8)

**MAJOR NEW finding**: DamageLayer requires renderer's material to have `_DamagePointArray` and `_DamagePointArrayLength` shader uniforms. If material doesn't (e.g., user replaced shader): **damage decals don't appear** but combat damage still applies (it's purely cosmetic).

**For modified vanilla hulls**: if user changes shader on the damage material, decals silently disappear. This is unrelated to the white-hull bug but could confuse.

**Critical for BK**: `_shipRenderers` is a SerializedField - must be assigned in the Inspector at edit time. ShipTools Verify Hull doesn't currently check this. Empty array = no damage decals.

### 25.3 CombatantController hierarchy

**Inheritance chain**:
```
MonoBehaviour, IDamageable
    ↑
CombatantController (abstract)
    ↑
CombatantShipController (abstract)
    ↑                       ↑
[ship combat hierarchy]    [hab combat hierarchy via separate inheritance - see CombatHabModuleController]
```

**`CombatantController` (abstract base, 134 lines)**:

Abstract members (must be implemented by subclasses):
- `SpaceCombatAssetUIController UIController()` - combat UI for this asset
- `CombatTargetableState GetCombatantState()` - return targetable state
- `IDamageableType GetCombatantType()` - type discriminator
- `Vector3 positionAtTime(DateTime currentTime)` - predicted position
- `float ApplyDamage(DamageSource source)` - receive damage
- `List<Collider> hitColliders` - hit detection colliders
- `Vector3 velocityVector` - current velocity
- `Vector3 velocityVector_kps` - velocity in km/s
- `Vector3 accelerationVector` - current acceleration
- `Vector3 accelerationVector_kps` - accel in km/s
- `IDamageableType damageableType` - type discriminator
- `CombatTargetableState combatTargetableState` - target binding
- `float GetCrossSectionalArea_m2(float angle)` - for hit chance calculations

Concrete members (provide default behavior):
- `bool isDestroyed` (= destructionTriggered)
- `Vector3 position`, `Vector3 localPosition` (delegates to GetDamageableTransform)
- `Transform IDamageable.damageableTransform` (= GetDamageableTransform)
- `TISpaceCombatProjectileState ref_projectile` (= null by default - overridden by projectile combatants)
- `CombatWeaponCarrierState WeaponCarrierState` (settable by subclass)
- `TIFactionState faction` (= WeaponCarrierState.GetFaction())
- `bool destructionTriggered` (settable by subclass)
- `bool IsFriendlyTo(combatant)` (= alliedCombatants.Contains)
- `Transform combatantTransform`, `SpaceCombatManager combatMgr` (settable)
- `CombatShipController ref_shipController` (= null; overridden in CombatShipController)
- `CombatHabModuleController ref_habModuleController` (= null; overridden in CombatHabModuleController)
- `Transform GetDamageableTransform` (= combatantTransform; overridable)
- `bool isMissileSaturated` (settable)
- `List<CombatantController> alliedCombatants`, `enemyCombatants` (HideInInspector)
- `List<IDamageable> ECMDefeats` (initialized empty)

**`CombatantShipController` (abstract, 24 lines)**:
- Abstract: `TISpaceShipState ShipState`, `ShipModelController ModelController`
- Override: `combatTargetableState` (= ShipState)
- Override: `GetCrossSectionalArea_m2(angle)` (= ShipState.GetCrossSectionalArea_m2(angle))

**`CombatHabModuleController`** extends CombatantController directly (NOT CombatantShipController) - habs are not ships.

**For BK modding**: BK does not subclass CombatantController. The combat handling happens via vanilla CombatShipController (not in archive but inferred from `ref_shipController` virtual). CombatShipController must be a concrete subclass that ties ShipState + ModelController together.

### 25.4 SolarSysModelController and FleetVisController

**`SolarSysModelController`** (45 lines, MonoBehaviour) - base for all strategy-layer scene models:
- `container` (SpaceObjectController, get/protected set)
- `SetShadowBehavior()` - protected method; star/lagrange = no receive, others = receive shadows
- `InitializeModel(container)` - virtual; sets layer to "Solar System" on all children, initializes RotateCloudsSolarSystemScene component if present, calls SetShadowBehavior

**`FleetVisController : SolarSysModelController`** (97 lines):

This is the strategic-layer fleet container. NOT for combat - ships in combat use ShipVisController via different code path.

Public properties:
- `fleetState` (TISpaceFleetState, get/private set)
- `shipStratControllerObjects` (List<GameObject>, get/private set)

Serialized field:
- `shipPrefab` (GameObject) - base ship prefab for strategic-layer rendering

`InitializeModel(container)` flow:
1. Initialize empty `shipStratControllerObjects`
2. Call base.InitializeModel(container) - sets layer + shadow behavior
3. Set name to `<container.name> Container`
4. Cast `container.spaceObjectState` to `TISpaceFleetState`
5. **For each ship in fleet**:
   - Create new GameObject named `<ID> Strategy Controller`
   - Parent to FleetVisController transform
   - Add `StrategyShipController` component
   - Call `Initialize(shipPrefab, shipState, this, false)`
   - Add to `shipStratControllerObjects`
6. Subscribe to `FleetDisbanded` event
7. Set `init = true`

`InitializeForUIAppearanceOnly(fleet)`:
- Walks all child ShipVisControllers
- Calls `SetAsUIVisualization(matchingShip, copiedFleet=true)` - for fleet preview UI

`OnFleetDisbanded(e)`:
- Removes listener
- Disables symbol controller, model controller, this component
- Schedules destruction after 15 seconds (`Invoke("DestroyThis", 15f)`)

`OnDisable()`: stops thruster audio for all child ships (prevents audio leak)
`OnEnable()`: SetDirty for all child strategy controllers

**Implication for BK**: BK doesn't touch FleetVisController. Strategic-layer rendering of BK ships goes through this path automatically - TISpaceFleetState contains TISpaceShipState entries, FleetVisController spawns StrategyShipControllers per ship, each StrategyShipController internally creates its own ShipVisController which loads the prefab via `assetLoader.InstantiatePrefab(...)`.

### 25.5 Hull-controller switch bodies

The actual SlotToWeaponMountIndex switch bodies for the 4 BK-relevant vanilla controllers:

#### LancerController (Lancer uses this, hull dataName mapped via §8)

```csharp
public override int SlotToWeaponMountIndex(int slot, Mount mount)
{
    switch (slot)
    {
    case 7:
        if ((uint)(mount - 11) <= 1u) return 0;  // HalfNose or HalfHull → 0
        return 4;
    case 8:
        switch (mount)
        {
        case Mount.TwoNoseHoriz:
        case Mount.ThreeNoseAngle:
        case Mount.FourNose:    return 0;
        default:                return 2;
        }
    case 9:
        switch (mount)
        {
        case Mount.TwoNoseHoriz:
        case Mount.ThreeNoseAngle:
        case Mount.FourNose:    return 0;
        default:                return 3;
        }
    case 10:
        switch (mount)
        {
        case Mount.TwoNoseHoriz:
        case Mount.ThreeNoseAngle:
        case Mount.FourNose:    return 0;
        default:                return 1;
        }
    case 11: return 0;
    case 14: return 1;
    case 18: return 2;
    default: return 0;
    }
}
```

**Slot semantics for LancerController**:
- Slots 7-11: nose weapons (mostly), slot 7 is special-cased for half-mount Aliens
- Slot 14, 18: hull weapons
- All other slots: return 0 (default - first nose mount index)

#### TitanController (Titan uses this)

```csharp
public override int SlotToWeaponMountIndex(int slot, Mount mount)
{
    switch (slot)
    {
    case 7:
        if ((uint)(mount - 9) <= 3u) return 0;  // FourNose/FourHull/HalfNose/HalfHull
        return 1;
    case 8:
        if ((uint)(mount - 9) <= 3u) return 0;
        return 3;
    case 9:
        if ((uint)(mount - 9) <= 3u) return 0;
        return 2;
    case 10:
        if ((uint)(mount - 9) <= 3u) return 0;
        return 4;
    case 12: return 0;
    case 13: return 1;
    case 16: return 2;
    case 17: return 3;
    case 20: return 4;
    case 21: return 5;
    default: return 0;
    }
}
```

**Slot semantics for TitanController**:
- Slots 7-10: nose weapons; oversize mounts (Four/Half) all collapse to position 0
- Slots 12, 13, 16, 17, 20, 21: hull weapons in 6 distinct positions
- 6 hull positions matches the typical Battleship-class layout

#### DreadnoughtController (used by HeavyCruiser via patch-only cfg entry)

```csharp
public override int SlotToWeaponMountIndex(int slot, Mount mount)
{
    switch (slot)
    {
    case 7: return 0;
    case 8:
        if (mount == Mount.ThreeNoseAngle) return 0;
        return 2;
    case 9:
        if (mount == Mount.ThreeNoseAngle) return 0;
        return 1;
    case 16: return 6;
    case 17: return 7;
    case 18: return 0;
    case 19: return 1;
    case 20: return 2;
    case 21: return 3;
    case 22: return 4;
    case 23: return 5;
    default: return 0;
    }
}
```

**Slot semantics for DreadnoughtController**:
- Slots 7-9: nose weapons (3 positions, ThreeNoseAngle collapses to 0)
- Slots 16-23: hull weapons (8 distinct positions - most of any controller)

#### BattlecruiserController (Battlecruiser uses this)

```csharp
public override int SlotToWeaponMountIndex(int slot, Mount mount)
{
    switch (slot)
    {
    case 7:
        return mount switch
        {
            Mount.OneNose => 1,
            _             => 0,
        };
    case 8:
        switch (mount)
        {
        case Mount.TwoHullVert:
        case Mount.TwoNoseHoriz:
        case Mount.ThreeNoseAngle: return 0;
        default:                   return 3;
        }
    case 9:
        switch (mount)
        {
        case Mount.TwoHullVert:
        case Mount.TwoNoseHoriz:
        case Mount.ThreeNoseAngle: return 0;
        default:                   return 2;
        }
    case 11: return 0;
    case 14: return 1;
    default: return 0;
    }
}
```

**Slot semantics for BattlecruiserController**:
- Slot 7: OneNose → 1, others → 0
- Slots 8-9: nose paired/grouped weapons
- Slot 11, 14: hull weapons

### 25.6 Cross-controller analysis - slot index ranges

| Controller | Slot indices used | Hull weapon slot range | Comment |
|---|---|---|---|
| BattlecruiserController | 7, 8, 9, 11, 14 | 11, 14 | Smallest mount inventory |
| LancerController | 7, 8, 9, 10, 11, 14, 18 | 11, 14, 18 | 3 hull weapons |
| TitanController | 7, 8, 9, 10, 12, 13, 16, 17, 20, 21 | 12, 13, 16, 17, 20, 21 | 6 hull weapons (most common Battleship layout) |
| DreadnoughtController | 7, 8, 9, 16-23 | 16, 17, 18-23 | 8 hull weapons (largest) |

**Note for §23 hull editing**:
- Slots 0-6 are **never** mapped in any controller's switch - these are always defaults (utility/drive/power/etc., not weapons)
- Slot 7 is always nose weapons across all controllers
- Hull weapon slot ranges differ - modifying slot indices in TIShipHullTemplate.json must respect each controller's expected ranges

This confirms §23.6's safety rule: append-to-end is the only safe edit, and "end" specifically means past the highest-indexed slot in any controller's switch (slot 23 for DreadnoughtController).

### 25.7 TIShipModuleTemplate (the abstract base)

**Class declaration**: `public abstract class TIShipModuleTemplate : TIShipPartTemplate`

```csharp
public abstract class TIShipModuleTemplate : TIShipPartTemplate
{
    public float mass_tons;
    
    public override float buildMass_tons(float v1=0, float v2=0, float v3=0, float v4=0, bool b=false)
    {
        return mass_tons;
    }
    
    public override TIResourcesCost buildCost(float v1=0, float v2=0)
    {
        TIResourcesCost cost = new TIResourcesCost();
        cost.SumCosts_NoDuration(weightedBuildMaterials.ToResourcesCost(
            buildMass_tons(v1, v2) * TemplateManager.global.spaceResourceToTons));
        return cost;
    }
}
```

**Findings**:
- TIShipModuleTemplate is a TINY base class - only 18 lines, one field, two override methods
- `mass_tons` is the ONLY direct JSON field on this base
- `buildMass_tons` is constant (returns `mass_tons` regardless of inputs) - **utility modules don't scale with anything**
- `buildCost` derives from mass × `spaceResourceToTons` via weightedBuildMaterials

**Subclass hierarchy** (inferred from references):
- `TIShipModuleTemplate` (this base)
  - `TIDriveTemplate` (drives - has its own thrustPower_GW, nozzle, etc.)
  - `TIPowerPlantTemplate` (not yet in archive - referenced via `powerPlantTemplate`)
  - `TIRadiatorTemplate` (not yet in archive - referenced via `radiatorTemplate`)
  - Utility module subclasses (not yet in archive)

**For modding**: utility modules don't have visualization (per §23.7) AND don't have variable mass (this section). They're purely numeric - JSON-only with constant mass and cost.

### 25.8 Cross-references

- **§1** Game-state architecture (TIShipModuleTemplate is base for drives/utility/etc.)
- **§5** Mount enum (12 names + numeric ordering)
- **§15** ColorAnimationEffect summary (full body in §25.1)
- **§17** SlotToWeaponMountIndex (full switch bodies in §25.5)
- **§22** Ship pipeline (CombatantController hierarchy referenced throughout)
- **§23** Hull editing pipeline (slot index ranges per controller in §25.6)
- **§24** Engine expectations (diagnostic checks reference these components)