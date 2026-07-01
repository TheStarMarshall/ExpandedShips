using UnityEditor;
using UnityEngine;
using System.IO;
using System.Collections.Generic;

public class ShipTools
{
    // ── Paths ────────────────────────────────────────────────────
    static readonly string BundleOutPath = "/home/kyle/Desktop/BundleOut";
    static readonly string DeployPath =
        "/mnt/steamgames/SteamLibrary/steamapps/common/Terra Invicta/Mods/Enabled/ExpandedFleetsAndNavies";

    // ── Layers ───────────────────────────────────────────────────
    const int LayerIgnoreRaycast = 2;
    const int LayerHurtBox = 17;

    // ── Hull structure expectations ──────────────────────────────
    static readonly string[] RequiredChildren = {
        "Hull", "Drive", "_ExplosionSequenceRoot",
        "SelectionReticle", "GroupSelectionReticle", "Padlock Container"
    };
    static readonly string[] FinRadiators = {
        "Radiator12", "Radiator3", "Radiator130", "Radiator6", "Radiator4",
        "Radiator430", "Radiator730", "Radiator1030", "Radiator8", "Radiator9"
    };
    static readonly string[] SpikeRadiators = { "spikes 12", "spikes 3", "spikes 6", "spikes 9" };
    static readonly string[] DropletRadiators = { "Droplet12", "Droplet8", "Droplet4" };

    public static readonly string[] FactionSuffixes = {
        "appease", "cooperate", "destroy", "escape", "exploit", "resist", "submit"
    };
    public const int DefaultFactionIndex = 5; // "resist"

    // ── TI reference names ───────────────────────────────────────
    const string FieldNoseWeapons = "noseWeaponControllers";
    const string FieldDorsalWeapons = "dorsalHullWeaponControllers";
    const string FieldVentralWeapons = "ventralHullWeaponControllers";

    const string TypeShipWeaponVisController = "ShipWeaponVisController";
    const string TypeDamageLayer = "DamageLayer";

    // ── Sidecar extensions for per-hull persistent state ─────────
    const string FactionSidecarExt     = ".faction";
    const string DrivePrefixSidecarExt = ".driveprefix";

    // ═══════════════════════════════════════════════════════════════
    // SETUP — one-time workspace preparation
    // ═══════════════════════════════════════════════════════════════

    [MenuItem("Ship Tools/Setup/1. Strip Vanilla Bundle Tags")]
    static void StripVanillaBundleTags()
    {
        string[] bundles = AssetDatabase.GetAllAssetBundleNames();
        int stripped = 0, kept = 0;
        List<string> keptNames = new List<string>();
        foreach (string b in bundles)
        {
            bool isBK = false;
            foreach (string path in AssetDatabase.GetAssetPathsFromAssetBundle(b))
            {
                if (path.StartsWith("Assets/") && !path.Contains("/GameObject/")
                    && !path.Contains("/Prefab/") && path.EndsWith(".prefab"))
                { isBK = true; break; }
            }
            if (isBK) { kept++; keptNames.Add(b); }
            else
            {
                foreach (string path in AssetDatabase.GetAssetPathsFromAssetBundle(b))
                {
                    AssetImporter imp = AssetImporter.GetAtPath(path);
                    if (imp != null) imp.assetBundleName = "";
                }
                stripped++;
            }
        }
        AssetDatabase.RemoveUnusedAssetBundleNames();
        Debug.Log($"Ship Tools: stripped {stripped}, kept {kept}: {string.Join(", ", keptNames)}");
    }

    [MenuItem("Ship Tools/Setup/2. Disable Streaming on All Textures")]
    public static void DisableStreamingAllTextures()
    {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D");
        int changed = 0, off = 0, err = 0;
        try
        {
            AssetDatabase.StartAssetEditing();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (i % 250 == 0)
                    EditorUtility.DisplayProgressBar("Ship Tools — Disable Streaming",
                        $"{i}/{guids.Length}", (float)i / guids.Length);
                TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                if (!imp.streamingMipmaps) { off++; continue; }
                try { imp.streamingMipmaps = false; imp.SaveAndReimport(); changed++; }
                catch (System.Exception ex) { Debug.LogError($"Ship Tools: '{path}': {ex.Message}"); err++; }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
        }
        Debug.Log($"Ship Tools: streaming disabled on {changed}, already off {off}, errors {err}");
    }

    [MenuItem("Ship Tools/Setup/3. Reimport Hull Textures as DXT")]
    public static void ReimportHullTexturesAsDxt()
    {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D");
        int scope = 0, conv = 0, ok = 0, err = 0;
        try
        {
            AssetDatabase.StartAssetEditing();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (i % 250 == 0)
                    EditorUtility.DisplayProgressBar("Ship Tools — Reimport as DXT",
                        $"{i}/{guids.Length}", (float)i / guids.Length);
                if (!Importer.IsHullTexture(path)) continue;
                scope++;
                TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                try
                {
                    if (Importer.ApplyDxtFormat(imp)) { imp.SaveAndReimport(); conv++; }
                    else ok++;
                }
                catch (System.Exception ex) { Debug.LogError($"Ship Tools: '{path}': {ex.Message}"); err++; }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
        }
        Debug.Log($"Ship Tools: scanned {guids.Length}, in scope {scope}, converted {conv}, already correct {ok}, errors {err}");
    }

    // ── One-time fix: write drive prefix sidecars for legacy hulls ───
    // Hulls created before the Drive Mesh Prefix field existed have no .driveprefix
    // sidecar. Without it, BakeDriveVariants hard-stops the build. This menu writes
    // the correct sidecar value for each known legacy prefab. Hardcoded mapping —
    // safe to re-run, idempotent.
    [MenuItem("Ship Tools/Setup/4. Write Drive Prefix Sidecars (one-time fix)")]
    static void WriteDrivePrefixSidecarsOneTimeFix()
    {
        // Each entry: BK prefab basename → vanilla hull whose drive meshes to use.
        // Source of truth: HullDefinitions.cfg dataName field. These mappings reflect
        // the three legacy hulls that existed before .driveprefix was introduced.
        var legacyMappings = new Dictionary<string, string>
        {
            { "ScoutCruiser",     "Battlecruiser" },
            { "NewBattleCruiser", "Lancer"        },
            { "NewBattleShip",    "Titan"         },
        };

        int wrote = 0, alreadyCorrect = 0, notFound = 0, mismatch = 0;
        var details = new List<string>();
        foreach (var kvp in legacyMappings)
        {
            string prefabPath = $"Assets/{kvp.Key}.prefab";
            if (!File.Exists(prefabPath))
            {
                Debug.Log($"Ship Tools: '{kvp.Key}.prefab' not in project — skipped");
                details.Add($"  • {kvp.Key} → not found in project");
                notFound++;
                continue;
            }

            string existing = ReadDrivePrefixSidecar(prefabPath);
            if (existing == kvp.Value)
            {
                Debug.Log($"Ship Tools: '{kvp.Key}.driveprefix' already correct ('{kvp.Value}')");
                details.Add($"  • {kvp.Key} → already '{kvp.Value}' (no change)");
                alreadyCorrect++;
                continue;
            }

            if (existing != null && existing != kvp.Value)
            {
                Debug.LogWarning($"Ship Tools: '{kvp.Key}.driveprefix' was '{existing}', overwriting with '{kvp.Value}'");
                details.Add($"  • {kvp.Key} → '{existing}' OVERWRITTEN with '{kvp.Value}'");
                mismatch++;
            }
            else
            {
                details.Add($"  • {kvp.Key} → wrote '{kvp.Value}'");
            }

            WriteDrivePrefixSidecar(prefabPath, kvp.Value);
            wrote++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Ship Tools — Drive Prefix Sidecars",
            $"Wrote: {wrote}\nAlready correct: {alreadyCorrect}\nNot in project: {notFound}\n" +
            (mismatch > 0 ? $"Overwrote different value: {mismatch}\n" : "") +
            "\nDetails:\n" + string.Join("\n", details) +
            "\n\nNext: run Build AssetBundle to re-bake variants with correct meshes.",
            "OK");
    }

    // ═══════════════════════════════════════════════════════════════
    // HULL — per-hull authoring
    // ═══════════════════════════════════════════════════════════════

    [MenuItem("Ship Tools/Hull/1. Create Hull From Vanilla")]
    static void CreateHullFromVanilla()
    {
        GameObject src = Selection.activeGameObject;
        string path = AssetDatabase.GetAssetPath(src);
        if (src == null || string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
        {
            EditorUtility.DisplayDialog("Ship Tools",
                "Select a vanilla ship .prefab in the Project panel first.", "OK");
            return;
        }
        CreateHullWindow.Open(src);
    }

    // Full create-hull pipeline: instantiate, unpack, rename body mesh parent,
    // save as asset, tag bundle, create drive prefab, bake faction skin,
    // write sidecar files for later re-bakes.
    public static void ExecuteCreateHull(GameObject src, string newName, string faction, string drivePrefix)
    {
        EnsureBundleSettings();
        string bundleName = newName.ToLower();

        GameObject inst = PrefabUtility.InstantiatePrefab(src) as GameObject;
        if (inst == null)
        { EditorUtility.DisplayDialog("Ship Tools", "Failed to instantiate prefab.", "OK"); return; }
        Undo.RegisterCreatedObjectUndo(inst, "Ship Create Hull");
        PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        inst.name = newName;

        // Framework expects a child named exactly "Hull"
        foreach (Transform c in inst.transform)
        {
            if (c.name.StartsWith("Earth_Hull_") || c.name.StartsWith("Hull_"))
            { c.name = "Hull"; break; }
        }

        string prefabPath = $"Assets/{newName}.prefab";
        PrefabUtility.SaveAsPrefabAsset(inst, prefabPath);
        AssetImporter imp = AssetImporter.GetAtPath(prefabPath);
        if (imp != null) imp.assetBundleName = bundleName;

        CreateDrivePrefab(inst, newName, bundleName);

        // Write sidecars BEFORE baking so rebuilds know faction + drive prefix.
        WriteFactionSidecar(prefabPath, faction);
        WriteDrivePrefixSidecar(prefabPath, drivePrefix);

        // Bake faction skin immediately on the saved asset
        int assigned = BakeFactionSkin(prefabPath, faction);

        Selection.activeGameObject = inst;
        Debug.Log($"Ship Tools: hull '{newName}' created from '{src.name}' → bundle '{bundleName}', " +
                  $"{assigned} materials baked ({faction}), drive prefix '{drivePrefix}', sidecars written");
    }

    static void CreateDrivePrefab(GameObject hullInst, string hullName, string bundleName)
    {
        Transform driveSrc = hullInst.transform.Find("Drive");
        if (driveSrc == null) { Debug.LogWarning("Ship Tools: no 'Drive' child."); return; }
        GameObject go = Object.Instantiate(driveSrc.gameObject);
        go.name = $"{hullName}_Drive";
        go.transform.SetParent(null);
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        string path = $"Assets/{hullName}_Drive.prefab";
        PrefabUtility.SaveAsPrefabAsset(go, path);
        AssetImporter imp = AssetImporter.GetAtPath(path);
        if (imp != null) imp.assetBundleName = bundleName;
        Object.DestroyImmediate(go);
    }

    // Assigns Assets/Material/MAT_<childName>_<faction>.mat to every MeshRenderer
    // in the prefab. DESTRUCTIVE — overwrites any existing material assignment
    // on renderers whose name has a matching material file. Returns renderers
    // successfully assigned.
    static int BakeFactionSkin(string prefabPath, string faction)
    {
        int assigned = 0;
        using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
        {
            foreach (MeshRenderer mr in scope.prefabContentsRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(
                    $"Assets/Material/MAT_{mr.gameObject.name}_{faction}.mat");
                if (mat == null) continue;
                mr.sharedMaterial = mat;
                assigned++;
            }
        }
        return assigned;
    }

    // ── Drive variant baking ─────────────────────────────────────
    // Bakes per-variant child GameObjects under the prefab's "Drive" child, one
    // per (nozzle, thruster-count, ALT) combination where both the vanilla mesh
    // AND the faction material exist in the project. Each variant child holds:
    //   - MeshFilter with Earth_<drivePrefix>_<variant> mesh
    //   - MeshRenderer with MAT_<materialPrefix>x<n><alt>_<faction>.mat material
    //   - SetActive(false) — runtime DriveVariantPatch activates the matching one
    //
    // Parent Drive's MeshRenderer is disabled at bake time. Vanilla SetDrive will
    // still write to its sharedMesh/sharedMaterial at runtime, but those don't
    // render because the variant children render in its place.
    //
    // Drive prefix (which vanilla hull's drive meshes to use, e.g. "Titan") comes
    // from the .driveprefix sidecar. If missing, prompts the user via
    // DrivePrefixPromptWindow with the prefab's basename pre-filled as default.
    //
    // Return convention:
    //    >= 0  → variants baked successfully
    //    -1    → user chose "Skip this hull" at the prompt
    //    -2    → user chose "Cancel pipeline" at the prompt
    static int BakeDriveVariants(string prefabPath)
    {
        string drivePrefix = ReadDrivePrefixSidecar(prefabPath);
        if (string.IsNullOrEmpty(drivePrefix))
        {
            // No sidecar — prompt user. Default value is the prefab's basename
            // (works as-is for hulls whose name matches the vanilla mesh prefix).
            string prefabName = Path.GetFileNameWithoutExtension(prefabPath);
            string entered = DrivePrefixPromptWindow.ShowBlocking(prefabName, prefabName);

            if (entered == null) return -2;   // user cancelled pipeline
            if (entered == "")  return -1;    // user chose skip-this-hull

            WriteDrivePrefixSidecar(prefabPath, entered);
            drivePrefix = entered;
        }

        string faction = ReadFactionSidecar(prefabPath) ?? FactionSuffixes[DefaultFactionIndex];

        int baked = 0;
        using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
        {
            Transform driveT = scope.prefabContentsRoot.transform.Find("Drive");
            if (driveT == null)
            {
                Debug.LogWarning($"Ship Tools: BakeDriveVariants — '{Path.GetFileName(prefabPath)}' has no 'Drive' child, skipping");
                return 0;
            }

            // Disable parent Drive's MeshRenderer. Vanilla SetDrive still writes to
            // its mesh/material at runtime, but those writes are invisible since the
            // renderer is disabled — variant children render in its place.
            MeshRenderer parentMr = driveT.GetComponent<MeshRenderer>();
            if (parentMr != null) parentMr.enabled = false;

            // Remove any pre-existing variant children (idempotent re-bake)
            for (int i = driveT.childCount - 1; i >= 0; i--)
            {
                Transform c = driveT.GetChild(i);
                if (IsDriveVariantName(c.gameObject.name))
                    Object.DestroyImmediate(c.gameObject);
            }

            // Bake every (nozzle, n, alt) where BOTH mesh and material exist.
            // Iterating widely (n=1..12) and skip-if-missing handles vanilla's
            // varied per-nozzle counts without hardcoding limits.
            string[] nozzles = { "DeLaval", "Magnetic", "Pulse" };
            foreach (string nozzle in nozzles)
            {
                string matPrefix = nozzle == "DeLaval" ? "EngineDeLaval" : nozzle;
                for (int n = 1; n <= 12; n++)
                {
                    foreach (string altSuffix in new[] { "", "_ALT1" })
                    {
                        string variantName = $"{nozzle}x{n}{altSuffix}";
                        string meshName    = $"Earth_{drivePrefix}_{variantName}";
                        string matPath     = $"Assets/Material/MAT_{matPrefix}x{n}{altSuffix}_{faction}.mat";

                        Mesh mesh = FindMeshByName(meshName);
                        if (mesh == null) continue;

                        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                        if (mat == null) continue;

                        GameObject child = new GameObject(variantName);
                        child.transform.SetParent(driveT, worldPositionStays: false);
                        child.transform.localPosition = Vector3.zero;
                        child.transform.localRotation = Quaternion.identity;
                        child.transform.localScale    = Vector3.one;
                        child.layer = driveT.gameObject.layer;
                        child.SetActive(false);

                        MeshFilter mf = child.AddComponent<MeshFilter>();
                        mf.sharedMesh = mesh;

                        MeshRenderer mr = child.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = mat;
                        mr.shadowCastingMode = parentMr != null
                            ? parentMr.shadowCastingMode
                            : UnityEngine.Rendering.ShadowCastingMode.On;
                        mr.receiveShadows = parentMr == null || parentMr.receiveShadows;

                        baked++;
                    }
                }
            }
        }
        return baked;
    }

    // Locates a Mesh asset by its name. Tries direct path first (the typical layout
    // after AssetRipper extraction is Assets/Mesh/<MeshName>.asset), then falls back
    // to FindAssets walking sub-assets (handles .fbx with embedded meshes).
    static Mesh FindMeshByName(string name)
    {
        string directPath = "Assets/Mesh/" + name + ".asset";
        Mesh m = AssetDatabase.LoadAssetAtPath<Mesh>(directPath);
        if (m != null) return m;

        foreach (string guid in AssetDatabase.FindAssets("t:Mesh " + name))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is Mesh mm && mm.name == name) return mm;
            }
        }
        return null;
    }

    // True if `name` matches our baked-variant naming convention
    // (DeLavalx<n>, Magneticx<n>, Pulsex<n>, with optional _ALT1 suffix).
    // Used by re-bake cleanup to remove stale variants without touching unrelated children.
    static bool IsDriveVariantName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith("DeLavalx",  System.StringComparison.Ordinal)
            || name.StartsWith("Magneticx", System.StringComparison.Ordinal)
            || name.StartsWith("Pulsex",    System.StringComparison.Ordinal);
    }

    // ── Set Faction Skin (manual tool) ───────────────────────────
    // Change an existing hull's faction skin after creation. Writes/updates
    // the sidecar and re-bakes immediately.
    [MenuItem("Ship Tools/Hull/3. Set Faction Skin")]
    static void SetFactionSkin()
    {
        GameObject sel = Selection.activeGameObject;
        string path = null;
        if (sel != null)
        {
            path = AssetDatabase.GetAssetPath(sel);
            if (string.IsNullOrEmpty(path))
            {
                Object src = PrefabUtility.GetCorrespondingObjectFromSource(sel);
                if (src != null) path = AssetDatabase.GetAssetPath(src);
            }
        }
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
        {
            EditorUtility.DisplayDialog("Ship Tools",
                "Select a hull .prefab in the Project panel or its instance in Hierarchy.", "OK");
            return;
        }

        string existing = ReadFactionSidecar(path);
        int startIdx = DefaultFactionIndex;
        if (existing != null)
        {
            for (int i = 0; i < FactionSuffixes.Length; i++)
                if (FactionSuffixes[i] == existing) { startIdx = i; break; }
        }
        FactionSkinWindow.Open(path, startIdx);
    }

    public static void ApplyFactionSkinChange(string prefabPath, string faction)
    {
        WriteFactionSidecar(prefabPath, faction);
        int assigned = BakeFactionSkin(prefabPath, faction);
        AssetDatabase.SaveAssets();
        Debug.Log($"Ship Tools: faction '{faction}' applied to {Path.GetFileName(prefabPath)} ({assigned} materials re-baked, sidecar updated)");
    }

    // ── Faction sidecar helpers ──────────────────────────────────
    static string SidecarPathFor(string prefabPath)
    {
        string dir = Path.GetDirectoryName(prefabPath);
        string name = Path.GetFileNameWithoutExtension(prefabPath);
        return Path.Combine(dir, name + FactionSidecarExt).Replace('\\', '/');
    }

    static void WriteFactionSidecar(string prefabPath, string faction)
    {
        string sidecar = SidecarPathFor(prefabPath);
        try
        {
            File.WriteAllText(sidecar, faction.Trim());
            AssetDatabase.ImportAsset(sidecar);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Ship Tools: could not write sidecar {sidecar}: {ex.Message}");
        }
    }

    static string ReadFactionSidecar(string prefabPath)
    {
        string sidecar = SidecarPathFor(prefabPath);
        if (!File.Exists(sidecar)) return null;
        try
        {
            string s = File.ReadAllText(sidecar).Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch { return null; }
    }

    // ── Drive prefix sidecar helpers ─────────────────────────────
    // Stores the vanilla hull name whose drive meshes this hull should use, e.g.
    // "Titan" for a BK prefab named "NewBattleShip". Bake reads this to look up
    // Earth_<prefix>_<variant>. Mirrors the .faction sidecar pattern exactly.
    static string DrivePrefixSidecarPathFor(string prefabPath)
    {
        string dir = Path.GetDirectoryName(prefabPath);
        string name = Path.GetFileNameWithoutExtension(prefabPath);
        return Path.Combine(dir, name + DrivePrefixSidecarExt).Replace('\\', '/');
    }

    static void WriteDrivePrefixSidecar(string prefabPath, string drivePrefix)
    {
        string sidecar = DrivePrefixSidecarPathFor(prefabPath);
        try
        {
            File.WriteAllText(sidecar, drivePrefix.Trim());
            AssetDatabase.ImportAsset(sidecar);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Ship Tools: could not write sidecar {sidecar}: {ex.Message}");
        }
    }

    static string ReadDrivePrefixSidecar(string prefabPath)
    {
        string sidecar = DrivePrefixSidecarPathFor(prefabPath);
        if (!File.Exists(sidecar)) return null;
        try
        {
            string s = File.ReadAllText(sidecar).Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch { return null; }
    }

    // Used by Finalize and Ship's faction re-bake stage when a hull prefab has
    // no sidecar. Prompts the user with a 3-way dialog and writes the sidecar
    // with their choice. Returns the chosen faction, or null if user declined.
    static string PromptAndCreateSidecar(string prefabPath)
    {
        string name = Path.GetFileNameWithoutExtension(prefabPath);
        int choice = EditorUtility.DisplayDialogComplex(
            "Ship Tools — Missing Faction Sidecar",
            $"'{name}' has no .faction sidecar. Build needs a faction to re-bake materials.\n\n" +
            $"Use '{FactionSuffixes[DefaultFactionIndex]}' (default), skip this hull, or pick another?",
            $"Use '{FactionSuffixes[DefaultFactionIndex]}'",
            "Skip this hull",
            "Pick another");

        if (choice == 0)
        {
            WriteFactionSidecar(prefabPath, FactionSuffixes[DefaultFactionIndex]);
            return FactionSuffixes[DefaultFactionIndex];
        }
        if (choice == 1) return null;

        // choice == 2 → pick another via window
        string picked = FactionPromptWindow.ShowBlocking(name);
        if (string.IsNullOrEmpty(picked)) return null;
        WriteFactionSidecar(prefabPath, picked);
        return picked;
    }

    // ── Add Weapon Mount ─────────────────────────────────────────
    [MenuItem("Ship Tools/Hull/2. Add Weapon Mount")]
    static void AddWeaponMount()
    {
        GameObject[] selected = Selection.gameObjects;
        if (selected == null || selected.Length == 0)
        { EditorUtility.DisplayDialog("Ship Tools", "Select one or more mounts in the Hierarchy.", "OK"); return; }

        List<GameObject> created = new List<GameObject>();
        foreach (GameObject src in selected)
        {
            if (src.transform.parent == null)
            { Debug.LogWarning($"Ship Tools: '{src.name}' no parent — skipped."); continue; }
            if (src.transform.childCount == 0)
            { Debug.LogWarning($"Ship Tools: '{src.name}' no children — skipped."); continue; }
            if (src.transform.GetChild(0).childCount == 0)
            { Debug.LogWarning($"Ship Tools: '{src.name}' no FirePoint — skipped."); continue; }

            string fieldName = FieldNameForMount(src.name);
            if (fieldName == null)
            { Debug.LogWarning($"Ship Tools: '{src.name}' not dorsal/ventral/nose — skipped."); continue; }

            string newName = GenerateNextName(src.name, src.transform.parent);
            int idx = src.transform.GetSiblingIndex();
            GameObject newMount = Object.Instantiate(src, src.transform.parent);
            Undo.RegisterCreatedObjectUndo(newMount, "Add Weapon Mount");
            newMount.name = newName;
            newMount.transform.GetChild(0).name = newName + " Gun";
            newMount.transform.GetChild(0).GetChild(0).name = "FirePoint";
            newMount.transform.SetSiblingIndex(idx + 1);

            GameObject root = src.transform.parent.gameObject;
            Component swvc = newMount.GetComponent(TypeShipWeaponVisController);
            if (swvc == null)
                Debug.LogWarning($"Ship Tools: '{newName}' missing {TypeShipWeaponVisController} — NOT registered");
            else if (!RegisterMountInController(root, fieldName, swvc))
                Debug.LogWarning($"Ship Tools: no ship controller with '{fieldName}' on {root.name} — register manually");
            else
                Debug.Log($"Ship Tools: duplicated '{src.name}' → '{newName}', registered in {fieldName}");

            created.Add(newMount);
        }
        if (created.Count > 0) Selection.objects = created.ToArray();
    }

    static string FieldNameForMount(string mountName)
    {
        string n = mountName.ToLower();
        if (n.Contains("dorsal")) return FieldDorsalWeapons;
        if (n.Contains("ventral")) return FieldVentralWeapons;
        if (n.Contains("nose")) return FieldNoseWeapons;
        return null;
    }

    static string GenerateNextName(string baseName, Transform parent)
    {
        string core = baseName;
        int lastSpace = baseName.LastIndexOf(' ');
        if (lastSpace > 0 && int.TryParse(baseName.Substring(lastSpace + 1), out _))
            core = baseName.Substring(0, lastSpace);
        int highest = 1;
        foreach (Transform sib in parent)
        {
            if (sib.name == core) continue;
            if (sib.name.StartsWith(core + " "))
            {
                if (int.TryParse(sib.name.Substring(core.Length + 1), out int n) && n > highest)
                    highest = n;
            }
        }
        return core + " " + (highest + 1);
    }

    static bool RegisterMountInController(GameObject root, string fieldName, Component swvc)
    {
        Component ctrl = FindShipController(root);
        if (ctrl == null) return false;

        SerializedObject so = new SerializedObject(ctrl);
        SerializedProperty arr = so.FindProperty(fieldName);
        if (arr == null || !arr.isArray) return false;

        for (int i = 0; i < arr.arraySize; i++)
            if (arr.GetArrayElementAtIndex(i).objectReferenceValue == swvc)
                return true;

        int newIdx = arr.arraySize;
        arr.InsertArrayElementAtIndex(newIdx);
        arr.GetArrayElementAtIndex(newIdx).objectReferenceValue = swvc;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(ctrl);
        return true;
    }

    static Component FindShipController(GameObject root)
    {
        foreach (Component c in root.GetComponents<Component>())
        {
            if (c == null) continue;
            SerializedObject so = new SerializedObject(c);
            if (so.FindProperty(FieldDorsalWeapons) != null
                && so.FindProperty(FieldVentralWeapons) != null
                && so.FindProperty(FieldNoseWeapons) != null)
                return c;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // VERIFY / BUILD / DEPLOY
    // ═══════════════════════════════════════════════════════════════

    [MenuItem("Ship Tools/Verify Hull")]
    static void VerifyHull()
    {
        GameObject sel = Selection.activeGameObject;
        if (sel == null) { EditorUtility.DisplayDialog("Ship Tools", "Select hull root.", "OK"); return; }
        if (sel.transform.parent != null)
        { EditorUtility.DisplayDialog("Ship Tools", $"'{sel.name}' not a root.", "OK"); return; }

        List<string> errors = new List<string>(), warnings = new List<string>(), info = new List<string>();
        RunHullChecks(sel, errors, warnings, info);

        info.Add("");
        info.Add("── Hierarchy order ──");
        int d = 0, v = 0, n = 0;
        foreach (Transform c in sel.transform)
        {
            string lower = c.name.ToLower();
            Vector3 p = c.localPosition;
            string loc = $"({p.x:F1}, {p.y:F1}, {p.z:F1})";
            if (lower.Contains("dorsal")) info.Add($"  [{d++}] {c.name}  {loc}");
            else if (lower.Contains("ventral")) info.Add($"  [{v++}] {c.name}  {loc}");
            else if (lower.Contains("nose")) info.Add($"  [{n++}] {c.name}  {loc}");
        }

        info.Add("");
        string prefabPath = AssetDatabase.GetAssetPath(
            PrefabUtility.GetCorrespondingObjectFromSource(sel) ?? sel);
        if (!string.IsNullOrEmpty(prefabPath))
        {
            AssetImporter imp = AssetImporter.GetAtPath(prefabPath);
            if (imp != null && string.IsNullOrEmpty(imp.assetBundleName))
                warnings.Add("No AssetBundle tag on prefab");
            else if (imp != null) info.Add($"Bundle tag: '{imp.assetBundleName}'");

            string faction = ReadFactionSidecar(prefabPath);
            if (faction == null)
                info.Add("Faction sidecar: none (build will prompt)");
            else
                info.Add($"Faction sidecar: '{faction}'");
        }

        PrintReport("BK HULL VERIFICATION", sel.name, errors, warnings, info);
    }

    // ═══════════════════════════════════════════════════════════════
    // FINALIZE AND SHIP — single-click setup → bake → build → deploy
    // ═══════════════════════════════════════════════════════════════
    // One menu does everything needed to ship a build:
    //   1. Ensure bundle settings (shader strip, instancing strip)
    //   2. Save pending in-memory asset changes
    //   3. Re-bake faction skin on every hull (auto-prompts for missing sidecars
    //      via PromptAndCreateSidecar — same UX as before)
    //   4. Bake drive variants on every hull (auto-prompts for missing
    //      .driveprefix sidecars via DrivePrefixPromptWindow; user can Apply,
    //      Skip this hull, or Cancel pipeline)
    //   5. Save again, then build asset bundles to BundleOutPath
    //   6. Deploy bundles + manifests to DeployPath
    //   7. Single Done dialog with per-stage counts
    //
    // No verify step — bake step does the work that verify would have caught,
    // and any real problems surface as build failures or runtime errors.
    [MenuItem("Ship Tools/Finalize and Ship")]
    static void FinalizeAndShip()
    {
        var log = new List<string>();
        void Note(string s) { Debug.Log("Ship Tools: " + s); log.Add(s); }
        void Warn(string s) { Debug.LogWarning("Ship Tools: " + s); log.Add("WARN: " + s); }
        void Err(string s)  { Debug.LogError("Ship Tools: " + s);   log.Add("ERROR: " + s); }

        Note("Finalize and Ship — pipeline started");

        // Stage 1 — bundle settings (always-safe to re-apply)
        Note("Stage 1/6: ensure bundle settings");
        EnsureBundleSettings();

        // Stage 2 — flush pending asset changes
        Note("Stage 2/6: save pending asset changes");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string[] bundles = AssetDatabase.GetAllAssetBundleNames();
        if (bundles.Length == 0)
        {
            Err("no AssetBundle tags found in project — nothing to do");
            EditorUtility.DisplayDialog("Ship Tools — Finalize and Ship",
                "Aborted: no AssetBundle tags exist in the project.\n\n" +
                "Tag at least one prefab with an AssetBundle name in its Inspector first.",
                "OK");
            return;
        }

        // Stage 3 — faction skin re-bake on every hull
        Note("Stage 3/6: re-bake faction skin on every hull");
        int factionRebakedHulls = 0, factionRebakedMaterials = 0,
            factionSkipped = 0, factionAutoCreated = 0;
        foreach (string b in bundles)
        {
            foreach (string path in AssetDatabase.GetAssetPathsFromAssetBundle(b))
            {
                if (!path.EndsWith(".prefab")) continue;
                if (Path.GetFileNameWithoutExtension(path).EndsWith("_Drive")) continue;

                bool hadSidecar = File.Exists(SidecarPathFor(path));
                string faction = ReadFactionSidecar(path);
                if (faction == null)
                {
                    faction = PromptAndCreateSidecar(path);
                    if (faction == null) { factionSkipped++; continue; }
                    if (!hadSidecar) factionAutoCreated++;
                }

                int n = BakeFactionSkin(path, faction);
                factionRebakedHulls++;
                factionRebakedMaterials += n;
                Note($"  re-baked {Path.GetFileName(path)} ({faction}) → {n} material(s)");
            }
        }

        // Stage 4 — drive variant baking on every hull
        // Prompts in-flow for missing .driveprefix sidecars; user can choose
        // Apply (writes sidecar + bakes), Skip this hull, or Cancel pipeline.
        Note("Stage 4/6: bake drive variants on every hull");
        int variantsBakedTotal = 0, variantPrefabsBaked = 0, variantsSkipped = 0;
        bool pipelineCancelled = false;
        foreach (string b in bundles)
        {
            if (pipelineCancelled) break;
            foreach (string path in AssetDatabase.GetAssetPathsFromAssetBundle(b))
            {
                if (!path.EndsWith(".prefab")) continue;
                if (Path.GetFileNameWithoutExtension(path).EndsWith("_Drive")) continue;

                int n = BakeDriveVariants(path);
                if (n == -2)
                {
                    Warn($"  pipeline cancelled by user during {Path.GetFileName(path)}");
                    pipelineCancelled = true;
                    break;
                }
                if (n == -1)
                {
                    Warn($"  skipped drive variants for {Path.GetFileName(path)} (user declined)");
                    variantsSkipped++;
                    continue;
                }
                variantsBakedTotal += n;
                variantPrefabsBaked++;
                Note($"  baked {Path.GetFileName(path)} → {n} drive variant(s)");
            }
        }

        if (pipelineCancelled)
        {
            string cancelSummary =
                "Pipeline cancelled by user during drive variant baking.\n\n" +
                $"Faction re-bake: {factionRebakedHulls} hull(s), {factionRebakedMaterials} material(s)\n" +
                $"Drive variants baked: {variantsBakedTotal} across {variantPrefabsBaked} hull(s)\n" +
                $"Drive variants skipped: {variantsSkipped}\n\n" +
                "Bundles NOT built or deployed. Per-stage log in Console.";
            EditorUtility.DisplayDialog("Ship Tools — Finalize and Ship", cancelSummary, "OK");
            return;
        }

        // Stage 5 — persist + build
        Note("Stage 5/6: save and build asset bundles");
        AssetDatabase.SaveAssets();

        int bundlesBuilt = 0;
        try
        {
            if (!Directory.Exists(BundleOutPath)) Directory.CreateDirectory(BundleOutPath);
            BuildPipeline.BuildAssetBundles(BundleOutPath,
                BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle,
                BuildTarget.StandaloneWindows64);
            foreach (string b in bundles)
            {
                string p = Path.Combine(BundleOutPath, b);
                if (File.Exists(p))
                {
                    bundlesBuilt++;
                    long bytes = new FileInfo(p).Length;
                    string sz = bytes > 1048576 ? $"{bytes / 1048576f:F1}MB" : $"{bytes / 1024}KB";
                    Note($"  built {b} ({sz})");
                }
                else Warn($"  {b} NOT FOUND after build");
            }
        }
        catch (System.Exception ex)
        {
            Err($"build failed — {ex.Message}");
            EditorUtility.DisplayDialog("Ship Tools — Finalize and Ship",
                $"Build failed: {ex.Message}\n\nBundles NOT deployed. Check Console for details.",
                "OK");
            return;
        }

        // Stage 6 — deploy
        Note($"Stage 6/6: deploy to {DeployPath}");
        int bundlesDeployed = 0;
        if (!Directory.Exists(DeployPath))
        {
            Err($"mod folder not found: {DeployPath} — bundles built but not deployed");
        }
        else
        {
            try
            {
                foreach (string b in bundles)
                {
                    string src = Path.Combine(BundleOutPath, b);
                    string srcM = src + ".manifest";
                    if (File.Exists(src))
                    {
                        File.Copy(src, Path.Combine(DeployPath, b), true);
                        bundlesDeployed++;
                        long bytes = new FileInfo(src).Length;
                        string sz = bytes > 1048576 ? $"{bytes / 1048576f:F1}MB" : $"{bytes / 1024}KB";
                        Note($"  deployed {b} ({sz})");
                    }
                    if (File.Exists(srcM)) File.Copy(srcM, Path.Combine(DeployPath, b + ".manifest"), true);
                }
            }
            catch (System.Exception ex) { Err($"deploy failed — {ex.Message}"); }
        }

        // Single Done dialog
        string summary =
            "Finalize and Ship complete.\n\n" +
            $"Faction sidecars auto-created:  {factionAutoCreated}\n" +
            $"Faction skin re-baked:          {factionRebakedHulls} hull(s), {factionRebakedMaterials} material(s)" +
                (factionSkipped > 0 ? $" ({factionSkipped} skipped)" : "") + "\n" +
            $"Drive variants baked:           {variantsBakedTotal} across {variantPrefabsBaked} hull(s)" +
                (variantsSkipped > 0 ? $" ({variantsSkipped} skipped)" : "") + "\n" +
            $"Bundles built:                  {bundlesBuilt} of {bundles.Length}\n" +
            $"Bundles deployed:               {bundlesDeployed} of {bundles.Length}\n\n" +
            "Per-stage log in Console.";
        Note("pipeline complete");
        EditorUtility.DisplayDialog("Ship Tools — Finalize and Ship", summary, "OK");
    }

    // ═══════════════════════════════════════════════════════════════
    // INTERNALS
    // ═══════════════════════════════════════════════════════════════

    static int RunHullChecks(GameObject go, List<string> errors,
        List<string> warnings, List<string> info)
    {
        int start = errors.Count;
        info.Add($"Hull: {go.name}  ({go.transform.childCount} children)");

        if (BundleSettingsOk()) info.Add("  GraphicsSettings: OK");
        else errors.Add("  GraphicsSettings: Standard in Always Included, or InstancingStripping != KeepAll. Run Build to auto-fix.");

        foreach (string n in RequiredChildren)
            if (go.transform.Find(n) == null) errors.Add($"  MISSING: '{n}'");

        Transform hull = go.transform.Find("Hull");
        if (hull != null && hull.childCount == 0)
            errors.Add("  'Hull' is empty — needs mesh children");
        else if (hull != null) info.Add($"  Hull: {hull.childCount} mesh children");

        if (go.GetComponent<CapsuleCollider>() == null) errors.Add("  Root MISSING CapsuleCollider");
        if (go.GetComponent(TypeDamageLayer) == null) errors.Add($"  Root MISSING {TypeDamageLayer}");
        if (go.layer != LayerIgnoreRaycast)
            warnings.Add($"  Root layer={go.layer}, expected {LayerIgnoreRaycast}");

        CheckChildGroup(go, FinRadiators, "fin", errors, info);
        CheckChildGroup(go, SpikeRadiators, "spike", errors, info);
        CheckChildGroup(go, DropletRadiators, "droplet", errors, info);

        int drawable = 0, skipped = 0, matOK = 0, matNull = 0, matNoTex = 0;
        foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
        {
            MeshFilter mf = mr.GetComponent<MeshFilter>();
            if (!mr.enabled || mf == null || mf.sharedMesh == null) { skipped++; continue; }
            drawable++;
            Material[] mats = mr.sharedMaterials;
            if (mats == null || mats.Length == 0)
            { matNull++; errors.Add($"  '{mr.gameObject.name}' NO materials"); continue; }
            bool hasTex = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null)
                { matNull++; errors.Add($"  '{mr.gameObject.name}' slot[{i}] NULL material"); }
                else
                {
                    Texture t = mats[i].HasProperty("_MainTex") ? mats[i].GetTexture("_MainTex") : null;
                    if (t != null) hasTex = true; else matNoTex++;
                }
            }
            if (hasTex) matOK++;
        }
        info.Add($"  Renderers: {drawable} drawable, {skipped} skipped ({matOK} textured, {matNull} null, {matNoTex} no albedo)");
        if (matNull > 0) errors.Add($"  {matNull} null material(s) — re-run Create Hull or bake manually");
        else if (matNoTex > 0) warnings.Add($"  {matNoTex} material(s) missing _MainTex");

        Transform drive = go.transform.Find("Drive");
        if (drive != null)
        {
            int tp = 0;
            foreach (Transform c in drive) if (c.name.Contains("ThrusterPoint")) tp++;
            if (tp == 0) errors.Add("  Drive: no ThrusterPoints");
            else info.Add($"  Drive: {tp} ThrusterPoint(s)");
            if (drive.gameObject.layer != LayerHurtBox)
                warnings.Add($"  Drive layer={drive.gameObject.layer}, expected {LayerHurtBox}");
        }

        int vec = 0;
        foreach (Transform c in go.transform) if (c.name.Contains("Vector")) vec++;
        info.Add($"  Vector Thrusters: {vec}");

        int dI = 0, vI = 0, nI = 0;
        foreach (Transform c in go.transform)
        {
            string lower = c.name.ToLower();
            bool isW = false;
            if (lower.Contains("dorsal")) { dI++; isW = true; }
            else if (lower.Contains("ventral")) { vI++; isW = true; }
            else if (lower.Contains("nose")) { nI++; isW = true; }
            if (!isW) continue;
            if (c.GetComponent(TypeShipWeaponVisController) == null)
                errors.Add($"  '{c.name}' MISSING {TypeShipWeaponVisController}");
            if (c.childCount == 0) { errors.Add($"  '{c.name}' no Gun child"); continue; }
            if (c.GetChild(0).childCount == 0)
                errors.Add($"  '{c.name}' → '{c.GetChild(0).name}' no FirePoint");
        }
        info.Add($"  Weapon mounts: {nI} nose, {dI} dorsal, {vI} ventral");
        if (dI != vI) warnings.Add($"  Dorsal ({dI}) != Ventral ({vI})");

        CheckControllerArrays(go, errors, info);

        return errors.Count - start;
    }

    static void CheckControllerArrays(GameObject root, List<string> errors, List<string> info)
    {
        Component ctrl = FindShipController(root);
        if (ctrl == null)
        {
            errors.Add("  Root MISSING ship controller (ShipModelController or subclass)");
            return;
        }

        SerializedObject so = new SerializedObject(ctrl);
        string compName = ctrl.GetType().Name;
        foreach (string field in new[] { FieldNoseWeapons, FieldDorsalWeapons, FieldVentralWeapons })
            CheckArray(so.FindProperty(field), compName, errors, info);
    }

    static void CheckArray(SerializedProperty arr, string compName,
        List<string> errors, List<string> info)
    {
        if (arr == null || !arr.isArray) return;

        int nullCount = 0, dupCount = 0;
        HashSet<int> seen = new HashSet<int>();
        List<int> dupIndices = new List<int>();
        for (int i = 0; i < arr.arraySize; i++)
        {
            Object o = arr.GetArrayElementAtIndex(i).objectReferenceValue;
            if (o == null) { nullCount++; continue; }
            if (!seen.Add(o.GetInstanceID())) { dupCount++; dupIndices.Add(i); }
        }
        if (nullCount > 0)
            errors.Add($"  {compName}.{arr.name}: {nullCount} NULL entry(s)");
        if (dupCount > 0)
            errors.Add($"  {compName}.{arr.name}: {dupCount} DUPLICATE entry(s) at [{string.Join(",", dupIndices)}] — renders white");
        if (nullCount == 0 && dupCount == 0)
            info.Add($"  {arr.name}: {arr.arraySize} entries OK");
    }

    static void CheckChildGroup(GameObject root, string[] names, string label,
        List<string> errors, List<string> info)
    {
        int found = 0;
        foreach (string n in names)
        { if (root.transform.Find(n) != null) found++; else errors.Add($"  MISSING {label}: '{n}'"); }
        if (found == names.Length) info.Add($"  {names.Length} {label}(s) OK");
    }

    // ── GraphicsSettings for AssetBundle builds ──────────────────
    static SerializedObject LoadGraphicsSettings()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        return (assets == null || assets.Length == 0) ? null : new SerializedObject(assets[0]);
    }

    static int FindShaderInArray(SerializedProperty arr, Shader target)
    {
        for (int i = 0; i < arr.arraySize; i++)
            if (arr.GetArrayElementAtIndex(i).objectReferenceValue == target) return i;
        return -1;
    }

    static bool EnsureBundleSettings()
    {
        SerializedObject so = LoadGraphicsSettings();
        if (so == null) return false;
        bool changed = false;

        Shader standard = Shader.Find("Standard");
        SerializedProperty arr = so.FindProperty("m_AlwaysIncludedShaders");
        if (standard != null && arr != null)
        {
            int idx = FindShaderInArray(arr, standard);
            if (idx >= 0) { arr.DeleteArrayElementAtIndex(idx); changed = true; }
        }

        SerializedProperty strip = so.FindProperty("m_InstancingStripping");
        if (strip != null && strip.intValue != 2) { strip.intValue = 2; changed = true; }

        if (changed)
        {
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(so.targetObject);
            AssetDatabase.SaveAssets();
            Debug.Log("Ship Tools: GraphicsSettings updated for bundle safety");
        }
        return true;
    }

    static bool BundleSettingsOk()
    {
        SerializedObject so = LoadGraphicsSettings();
        if (so == null) return false;
        Shader standard = Shader.Find("Standard");
        SerializedProperty arr = so.FindProperty("m_AlwaysIncludedShaders");
        if (standard != null && arr != null && FindShaderInArray(arr, standard) >= 0) return false;
        SerializedProperty strip = so.FindProperty("m_InstancingStripping");
        return strip == null || strip.intValue == 2;
    }

    static void PrintReport(string title, string subject,
        List<string> errors, List<string> warnings, List<string> info,
        string passMsg = "ALL CHECKS PASSED", string failMsg = "NOT READY")
    {
        Debug.Log($"═══ {title}: {subject} ═══");
        foreach (string s in info) Debug.Log(s);
        if (warnings.Count > 0)
        {
            Debug.LogWarning($"── {warnings.Count} WARNING(S) ──");
            foreach (string s in warnings) Debug.LogWarning(s);
        }
        if (errors.Count > 0)
        {
            Debug.LogError($"── {errors.Count} ERROR(S) — {failMsg} ──");
            foreach (string s in errors) Debug.LogError(s);
        }
        else Debug.Log($"══ {passMsg} ══");
    }

    // ═══════════════════════════════════════════════════════════════
    // AssetPostprocessor — auto-applies texture fixes on every import
    // ═══════════════════════════════════════════════════════════════
    public class Importer : AssetPostprocessor
    {
        public static bool IsHullTexture(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Replace('\\', '/');
            return p.StartsWith("Assets/Texture2D/", System.StringComparison.OrdinalIgnoreCase)
                || p.IndexOf("/Resources/objects/", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool ApplyDxtFormat(TextureImporter imp)
        {
            TextureImporterFormat target = imp.DoesSourceTextureHaveAlpha()
                ? TextureImporterFormat.DXT5 : TextureImporterFormat.DXT1;

            TextureImporterPlatformSettings s = imp.GetPlatformTextureSettings("Standalone");
            if (s.overridden && s.format == target
                && s.textureCompression == TextureImporterCompression.Compressed)
                return false;

            s.overridden = true;
            s.format = target;
            s.textureCompression = TextureImporterCompression.Compressed;
            imp.SetPlatformTextureSettings(s);
            return true;
        }

        void OnPreprocessTexture()
        {
            TextureImporter imp = (TextureImporter)assetImporter;
            if (imp.streamingMipmaps) imp.streamingMipmaps = false;
            if (IsHullTexture(assetPath)) ApplyDxtFormat(imp);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// Create Hull window — new hull + faction skin bake
// ═══════════════════════════════════════════════════════════════════
public class CreateHullWindow : EditorWindow
{
    GameObject sourcePrefab;
    string hullName = "";
    string drivePrefix = "";
    bool drivePrefixManuallyEdited = false;
    int factionIndex = ShipTools.DefaultFactionIndex;
    bool focusSet;

    public static void Open(GameObject source)
    {
        CreateHullWindow w = GetWindow<CreateHullWindow>(true, "Create Hull From Vanilla");
        w.sourcePrefab = source;
        w.hullName = "";
        w.drivePrefix = "";
        w.drivePrefixManuallyEdited = false;
        w.focusSet = false;
        w.minSize = new Vector2(440, 240); w.maxSize = new Vector2(440, 240);
        w.ShowUtility();
    }

    void OnGUI()
    {
        if (sourcePrefab == null)
        { EditorGUILayout.HelpBox("Source prefab lost.", MessageType.Error); return; }

        GUILayout.Space(8);
        EditorGUILayout.LabelField("Source:", sourcePrefab.name, EditorStyles.boldLabel);

        GUILayout.Space(4);
        GUI.SetNextControlName("HullField");
        string previousHullName = hullName;
        hullName = EditorGUILayout.TextField("Hull Name", hullName);

        // Drive prefix mirrors hull name until the user edits it explicitly.
        // After explicit edit, it stays decoupled.
        if (!drivePrefixManuallyEdited && hullName != previousHullName)
            drivePrefix = hullName;

        string db = string.IsNullOrEmpty(hullName) ? "—" : hullName.ToLower();
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.TextField("Bundle (auto)", db);
        EditorGUI.EndDisabledGroup();

        string previousDrivePrefix = drivePrefix;
        drivePrefix = EditorGUILayout.TextField("Drive Mesh Prefix", drivePrefix);
        if (drivePrefix != previousDrivePrefix && drivePrefix != hullName)
            drivePrefixManuallyEdited = true;

        EditorGUILayout.HelpBox(
            "Vanilla hull whose drive meshes to use (e.g. 'Titan' for a renamed Battleship hull). " +
            "Defaults to Hull Name. Override when your prefab name differs from the vanilla hull " +
            "whose drives you want.",
            MessageType.None);

        factionIndex = EditorGUILayout.Popup("Faction Skin", factionIndex, ShipTools.FactionSuffixes);

        if (!focusSet && Event.current.type == EventType.Repaint)
        { EditorGUI.FocusTextInControl("HullField"); focusSet = true; }

        GUILayout.Space(8);
        GUILayout.BeginHorizontal(); GUILayout.FlexibleSpace();
        bool valid = !string.IsNullOrEmpty(hullName) && !string.IsNullOrEmpty(drivePrefix);
        if (valid && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
        { Execute(); Event.current.Use(); }
        EditorGUI.BeginDisabledGroup(!valid);
        if (GUILayout.Button("Create", GUILayout.Width(100))) Execute();
        EditorGUI.EndDisabledGroup();
        if (GUILayout.Button("Cancel", GUILayout.Width(80))) Close();
        GUILayout.EndHorizontal();
    }

    void Execute()
    {
        ShipTools.ExecuteCreateHull(
            sourcePrefab,
            hullName,
            ShipTools.FactionSuffixes[factionIndex],
            drivePrefix);
        Close();
    }
}

// ═══════════════════════════════════════════════════════════════════
// Set Faction Skin window — change an existing hull's faction skin
// ═══════════════════════════════════════════════════════════════════
public class FactionSkinWindow : EditorWindow
{
    string prefabPath;
    int factionIndex;

    public static void Open(string prefabPath, int startIdx)
    {
        FactionSkinWindow w = GetWindow<FactionSkinWindow>(true, "Set Faction Skin");
        w.prefabPath = prefabPath;
        w.factionIndex = startIdx;
        w.minSize = new Vector2(360, 120); w.maxSize = new Vector2(360, 120);
        w.ShowUtility();
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField("Hull:", Path.GetFileNameWithoutExtension(prefabPath), EditorStyles.boldLabel);

        GUILayout.Space(4);
        factionIndex = EditorGUILayout.Popup("Faction", factionIndex, ShipTools.FactionSuffixes);

        GUILayout.Space(8);
        GUILayout.BeginHorizontal(); GUILayout.FlexibleSpace();
        if (GUILayout.Button("Apply", GUILayout.Width(100)))
        {
            ShipTools.ApplyFactionSkinChange(prefabPath, ShipTools.FactionSuffixes[factionIndex]);
            Close();
        }
        if (GUILayout.Button("Cancel", GUILayout.Width(80))) Close();
        GUILayout.EndHorizontal();
    }
}

// ═══════════════════════════════════════════════════════════════════
// Drive prefix prompt window — blocking text-input prompt used by
// BakeDriveVariants when a hull is missing its .driveprefix sidecar.
// Three outcomes:
//   - Apply (with text)  → returns the entered prefix, sidecar gets written
//   - Skip this hull     → returns "" (caller treats as "skip and continue")
//   - Cancel pipeline    → returns null (caller aborts the whole pipeline)
//
// Uses a polling wait because ShowModal is broken on Linux Unity, mirroring
// FactionPromptWindow's pattern.
// ═══════════════════════════════════════════════════════════════════
public class DrivePrefixPromptWindow : EditorWindow
{
    string hullName;
    string drivePrefix;
    string result;
    bool   skipped;
    bool   cancelled;
    bool   done;
    bool   focusSet;

    public static string ShowBlocking(string hullName, string defaultValue)
    {
        DrivePrefixPromptWindow w = CreateInstance<DrivePrefixPromptWindow>();
        w.titleContent = new GUIContent("Drive Prefix Required");
        w.hullName    = hullName;
        w.drivePrefix = defaultValue ?? "";
        w.minSize = new Vector2(440, 200); w.maxSize = new Vector2(440, 200);
        w.ShowUtility();

        while (!w.done)
        {
            w.Repaint();
            System.Threading.Thread.Sleep(50);
        }

        if (w.cancelled) return null;
        if (w.skipped)   return "";
        return w.result;
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField("Hull:", hullName, EditorStyles.boldLabel);

        GUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "No .driveprefix sidecar found for this hull. Enter the vanilla hull name " +
            "whose drive meshes this hull should use (e.g. 'Titan' for a Battleship-class hull, " +
            "or the hull's own name for fully-custom drives).",
            MessageType.Info);

        GUILayout.Space(4);
        GUI.SetNextControlName("PrefixField");
        drivePrefix = EditorGUILayout.TextField("Drive Mesh Prefix", drivePrefix);

        if (!focusSet && Event.current.type == EventType.Repaint)
        { EditorGUI.FocusTextInControl("PrefixField"); focusSet = true; }

        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        bool valid = !string.IsNullOrEmpty(drivePrefix) && !string.IsNullOrWhiteSpace(drivePrefix);
        EditorGUI.BeginDisabledGroup(!valid);
        if (GUILayout.Button("Apply", GUILayout.Width(80)))
        {
            result = drivePrefix.Trim();
            done = true;
            Close();
        }
        EditorGUI.EndDisabledGroup();

        if (GUILayout.Button("Skip this hull", GUILayout.Width(110)))
        {
            skipped = true;
            done = true;
            Close();
        }
        if (GUILayout.Button("Cancel pipeline", GUILayout.Width(120)))
        {
            cancelled = true;
            done = true;
            Close();
        }
        GUILayout.EndHorizontal();

        // Enter key submits if Apply is enabled
        if (valid && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
        {
            result = drivePrefix.Trim();
            done = true;
            Close();
            Event.current.Use();
        }
    }

    // If the window is closed via the OS (X button), treat as cancellation.
    void OnDestroy()
    {
        if (!done)
        {
            cancelled = true;
            done = true;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// Faction prompt window — blocking picker for build-time "pick another"
// path. Uses a polling wait because ShowModal is broken on Linux Unity.
// ═══════════════════════════════════════════════════════════════════
public class FactionPromptWindow : EditorWindow
{
    string hullName;
    int factionIndex;
    string result;
    bool done;

    public static string ShowBlocking(string hullName)
    {
        FactionPromptWindow w = CreateInstance<FactionPromptWindow>();
        w.titleContent = new GUIContent("Pick Faction");
        w.hullName = hullName;
        w.factionIndex = ShipTools.DefaultFactionIndex;
        w.minSize = new Vector2(360, 130); w.maxSize = new Vector2(360, 130);
        w.ShowUtility();

        while (!w.done)
        {
            w.Repaint();
            System.Threading.Thread.Sleep(50);
        }
        return w.result;
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField("Hull:", hullName, EditorStyles.boldLabel);

        GUILayout.Space(4);
        factionIndex = EditorGUILayout.Popup("Faction", factionIndex, ShipTools.FactionSuffixes);

        GUILayout.Space(8);
        GUILayout.BeginHorizontal(); GUILayout.FlexibleSpace();
        if (GUILayout.Button("Apply", GUILayout.Width(100)))
        {
            result = ShipTools.FactionSuffixes[factionIndex];
            done = true;
            Close();
        }
        if (GUILayout.Button("Cancel", GUILayout.Width(80)))
        {
            result = null;
            done = true;
            Close();
        }
        GUILayout.EndHorizontal();
    }

    void OnDestroy() { done = true; }
}
