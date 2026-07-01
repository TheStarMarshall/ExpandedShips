using System;
using HarmonyLib;
using PavonisInteractive.TerraInvicta;
using UnityEngine;

namespace BetterKinetics
{
    // Normalizes every ship's rendered longest-axis to its TIShipHullTemplate.length_m.
    //
    // Vanilla design: the per-hull prefab (modelLink) is instantiated as a child of the
    // ShipVisController GameObject. Combat overwrites the ShipVisController's localScale to
    // Vector3.one * modelScalingFactor (CombatShipController.cs:382); strategic view leaves
    // it at Vector3.one. The per-hull prefab's localScale is never touched by either path.
    //
    // This postfix writes the corrective scale onto modelLink. Combined effect:
    //   strategic: rendered length = length_m
    //   combat:    rendered length = length_m * modelScalingFactor
    // Both views are consistent by construction (single source of truth, applied once
    // at ship-visualizer creation).
    //
    // Walks every MeshFilter under modelLink — no name-based detection, no special cases.
    // Same rule applied to every prefab, so proportionality between ships is preserved
    // regardless of how the prefab is organized internally.
    [HarmonyPatch(typeof(ShipVisController), nameof(ShipVisController.InitializeShipVisualizer))]
    static class CombatScalePatch
    {
        static void Postfix(ShipVisController __instance, TISpaceShipTemplate shipTemplate)
        {
            try
            {
                float length_m = shipTemplate?.hullTemplate?.length_m ?? 0f;
                if (length_m <= 0f) return;

                Transform modelLink = Traverse.Create(__instance)
                                              .Field<GameObject>("modelLink")
                                              .Value?.transform;
                if (modelLink == null) return;

                MeshFilter[] meshFilters = modelLink.GetComponentsInChildren<MeshFilter>(true);
                if (meshFilters.Length == 0) return;

                // Combine each mesh's local AABB through its transform chain into a
                // single AABB expressed in modelLink's local space. We use sharedMesh.bounds
                // (always defined, no activation dependency) rather than Renderer.bounds
                // (world-space, may be stale for inactive GameObjects).
                Bounds combined = default;
                bool any = false;
                foreach (MeshFilter mf in meshFilters)
                {
                    Mesh mesh = mf.sharedMesh;
                    if (mesh == null) continue;

                    Bounds meshLocal = mesh.bounds;
                    Matrix4x4 toModelLink = modelLink.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    Bounds inModelLinkSpace = TransformBounds(meshLocal, toModelLink);

                    if (!any) { combined = inModelLinkSpace; any = true; }
                    else      { combined.Encapsulate(inModelLinkSpace); }
                }
                if (!any) return;

                float longest = Mathf.Max(combined.size.x, combined.size.y, combined.size.z);
                if (longest <= 0f) return;

                float multiplier = length_m / longest;
                modelLink.localScale *= multiplier;
            }
            catch (Exception)
            {
                // Never let a scaling failure break ship construction.
                // Fall through to vanilla scale.
            }
        }

        // Transform an axis-aligned bounding box through an arbitrary matrix into a new
        // axis-aligned bounding box that encloses the transformed corners.
        static Bounds TransformBounds(Bounds local, Matrix4x4 m)
        {
            Vector3 c = local.center;
            Vector3 e = local.extents;
            Vector3 p0 = m.MultiplyPoint3x4(c + new Vector3(-e.x, -e.y, -e.z));
            Bounds result = new Bounds(p0, Vector3.zero);
            result.Encapsulate(m.MultiplyPoint3x4(c + new Vector3( e.x, -e.y, -e.z)));
            result.Encapsulate(m.MultiplyPoint3x4(c + new Vector3(-e.x,  e.y, -e.z)));
            result.Encapsulate(m.MultiplyPoint3x4(c + new Vector3( e.x,  e.y, -e.z)));
            result.Encapsulate(m.MultiplyPoint3x4(c + new Vector3(-e.x, -e.y,  e.z)));
            result.Encapsulate(m.MultiplyPoint3x4(c + new Vector3( e.x, -e.y,  e.z)));
            result.Encapsulate(m.MultiplyPoint3x4(c + new Vector3(-e.x,  e.y,  e.z)));
            result.Encapsulate(m.MultiplyPoint3x4(c + new Vector3( e.x,  e.y,  e.z)));
            return result;
        }
    }
}
