using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Vanilla stick blades dip through the rink floor via StickPositioner raycasts to Soft Collider
/// hits, with Stick↔Ice physics ignored. Slidables opt in per collider (SlidableStickCollision).
/// MultiSheet _IceSurface boxes sit on Ice and must not block those raycasts or stick physics.
/// </summary>
public static class StickIcePassThrough
{
    private const int RaycastHitBufferSize = 32;
    private const string SoftColliderTag = "Soft Collider";
    private const string IceSurfaceSuffix = "_IceSurface";
    private const float BladeDipBelowSurface = 0.09f;

    private static readonly HashSet<Collider> floorIceColliders = new HashSet<Collider>();
    private static readonly HashSet<Collider> stickColliders = new HashSet<Collider>();
    private static readonly RaycastHit[] RaycastHitBuffer = new RaycastHit[RaycastHitBufferSize];

    private static int stickPositionerRaycastDepth;
    private static bool harmonyInstalled;
    private static FieldInfo bladeTargetField;

    internal static bool StickPositionerRaycastActive => stickPositionerRaycastDepth > 0;

    internal static void EnterStickPositionerRaycast() => stickPositionerRaycastDepth++;

    internal static void ExitStickPositionerRaycast()
    {
        if (stickPositionerRaycastDepth > 0)
            stickPositionerRaycastDepth--;
    }

    public static void InstallHarmonyPatches(Harmony harmony)
    {
        if (harmonyInstalled || harmony == null)
            return;

        MethodInfo raycast = AccessTools.Method(
            typeof(Physics),
            nameof(Physics.Raycast),
            new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int) });

        if (raycast != null)
        {
            harmony.Patch(
                raycast,
                prefix: new HarmonyMethod(typeof(StickIcePassThrough_PhysicsRaycastPatch), nameof(StickIcePassThrough_PhysicsRaycastPatch.Prefix)));
            FlamieLog.Info("[FlamiePrac] Stick ice dip: patched Physics.Raycast.");
        }
        else
        {
            FlamieLog.Warn("[FlamiePrac] Stick ice dip: Physics.Raycast patch target not found.");
        }

        harmonyInstalled = true;
    }

    public static bool IsFloorIce(Collider col)
    {
        if (col == null)
            return false;

        if (floorIceColliders.Contains(col))
            return true;

        if (IsSlidableCollider(col))
            return false;

        if (col.gameObject.name.EndsWith(IceSurfaceSuffix, StringComparison.Ordinal))
            return true;

        int iceLayer = LayerMask.NameToLayer("Ice");
        return iceLayer >= 0 &&
               col.gameObject.layer == iceLayer &&
               LooksLikeRinkFloor(col);
    }

    public static void RegisterFloorIce(Collider col)
    {
        if (col == null || col.isTrigger || IsSlidableCollider(col))
            return;

        if (SlidableStickCollision.IsRegisteredSlidableCollider(col))
            return;

        PruneDestroyed(floorIceColliders);
        if (!floorIceColliders.Add(col))
            return;

        foreach (Collider stickCol in stickColliders)
        {
            if (stickCol != null)
                Physics.IgnoreCollision(col, stickCol, true);
        }
    }

    public static void RegisterStick(Stick stick)
    {
        if (stick == null)
            return;

        PruneDestroyed(stickColliders);
        foreach (Collider col in CollectStickColliders(stick))
        {
            if (col == null || !stickColliders.Add(col))
                continue;

            foreach (Collider floor in floorIceColliders)
            {
                if (floor != null)
                    Physics.IgnoreCollision(floor, col, true);
            }
        }

        SlidableStickCollision.ApplyStick(stick);
    }

    /// <summary>Register native level + MultiSheet ice sheets after spawn/clone.</summary>
    public static void ScanSceneFloorIce(bool logResult = false)
    {
        int iceLayer = LayerMask.NameToLayer("Ice");
        if (iceLayer < 0)
            return;

        Collider[] all = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
            RegisterFloorIceFromScene(all[i], iceLayer);

        Stick[] sticks = UnityEngine.Object.FindObjectsByType<Stick>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < sticks.Length; i++)
            RegisterStick(sticks[i]);

        string scanLine = "[FlamiePrac] Stick floor-ice pass-through: " + floorIceColliders.Count +
                          " floor collider(s), " + stickColliders.Count + " stick collider(s).";
        if (logResult || floorIceColliders.Count > 0 || stickColliders.Count > 0)
            FlamieLog.Info(scanLine);
        else
            FlamieLog.InfoOnce("stick-ice-pass", scanLine);
    }

    public static bool RaycastSkippingFloorIce(
        Vector3 origin,
        Vector3 direction,
        out RaycastHit hit,
        float maxDistance,
        int layerMask)
    {
        hit = default;

        if (TryRaycastAll(origin, direction, maxDistance, layerMask, QueryTriggerInteraction.Collide, out RaycastHit[] hits) ||
            TryRaycastAll(origin, direction, maxDistance, layerMask, QueryTriggerInteraction.UseGlobal, out hits))
        {
            return TrySelectHit(hits, out hit);
        }

        return false;
    }

    private static bool TryRaycastAll(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        int layerMask,
        QueryTriggerInteraction queryTriggerInteraction,
        out RaycastHit[] hits)
    {
        hits = null;
        int count = Physics.RaycastNonAlloc(
            origin, direction, RaycastHitBuffer, maxDistance, layerMask, queryTriggerInteraction);
        if (count <= 0)
            return false;

        if (count < RaycastHitBufferSize)
        {
            hits = new RaycastHit[count];
            for (int i = 0; i < count; i++)
                hits[i] = RaycastHitBuffer[i];
            return true;
        }

        hits = Physics.RaycastAll(origin, direction, maxDistance, layerMask, queryTriggerInteraction);
        return hits != null && hits.Length > 0;
    }

    private static bool TrySelectHit(RaycastHit[] hits, out RaycastHit hit)
    {
        hit = default;

        RaycastHit? bestSoft = null;
        float bestSoftDistance = float.PositiveInfinity;
        RaycastHit? bestOther = null;
        float bestOtherDistance = float.PositiveInfinity;
        RaycastHit? bestFloor = null;
        float bestFloorDistance = float.PositiveInfinity;

        for (int i = 0; i < hits.Length; i++)
        {
            Collider col = hits[i].collider;
            if (col == null)
                continue;

            float distance = hits[i].distance;
            if (IsFloorIce(col))
            {
                if (distance < bestFloorDistance)
                {
                    bestFloorDistance = distance;
                    bestFloor = hits[i];
                }

                continue;
            }

            if (col.CompareTag(SoftColliderTag))
            {
                if (distance < bestSoftDistance)
                {
                    bestSoftDistance = distance;
                    bestSoft = hits[i];
                }

                continue;
            }

            if (distance < bestOtherDistance)
            {
                bestOtherDistance = distance;
                bestOther = hits[i];
            }
        }

        if (bestSoft.HasValue)
        {
            hit = bestSoft.Value;
            return true;
        }

        if (bestOther.HasValue)
        {
            hit = bestOther.Value;
            return true;
        }

        if (bestFloor.HasValue)
        {
            hit = ApplyFloorDip(bestFloor.Value);
            return true;
        }

        return false;
    }

    private static RaycastHit ApplyFloorDip(RaycastHit floorHit)
    {
        if (Mathf.Abs(floorHit.normal.y) >= 0.5f)
        {
            Vector3 point = floorHit.point;
            point.y -= BladeDipBelowSurface;
            floorHit.point = point;
        }

        return floorHit;
    }

    private static void RegisterFloorIceFromScene(Collider col, int iceLayer)
    {
        if (col == null || col.isTrigger)
            return;

        if (IsSlidableCollider(col))
            return;

        if (col.gameObject.name.EndsWith(IceSurfaceSuffix, StringComparison.Ordinal) ||
            (col.gameObject.layer == iceLayer && LooksLikeRinkFloor(col)))
        {
            RegisterFloorIce(col);
        }
    }

    private static bool LooksLikeRinkFloor(Collider col)
    {
        if (col == null)
            return false;

        Transform t = col.transform;
        while (t != null)
        {
            string name = t.name;
            if (name.Equals("Ice Top", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Ice Bottom", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(IceSurfaceSuffix, StringComparison.Ordinal))
            {
                return true;
            }

            t = t.parent;
        }

        if (col is BoxCollider box)
        {
            Vector3 size = Vector3.Scale(box.size, col.transform.lossyScale);
            return size.x >= 20f && size.z >= 40f && size.y <= 1f;
        }

        if (col is MeshCollider meshCol && meshCol.sharedMesh != null)
        {
            Bounds bounds = meshCol.sharedMesh.bounds;
            Vector3 size = Vector3.Scale(bounds.size, col.transform.lossyScale);
            return size.x >= 20f && size.z >= 40f && size.y <= 2f;
        }

        return false;
    }

    private static bool IsSlidableCollider(Collider col)
    {
        if (col == null)
            return false;

        if (col.GetComponentInParent<SlidableObstacle>() != null)
            return true;

        return SlidableObstacleSetup.IsSlidableSubtree(col.transform);
    }

    private static IEnumerable<Collider> CollectStickColliders(Stick stick)
    {
        if (stick == null)
            yield break;

        Collider[] cols = stick.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            Collider col = cols[i];
            if (col != null && !col.isTrigger)
                yield return col;
        }
    }

    private static void PruneDestroyed(HashSet<Collider> set)
    {
        set.RemoveWhere(c => c == null);
    }

    private static GameObject GetBladeTarget(StickPositioner positioner)
    {
        if (positioner == null)
            return null;

        if (bladeTargetField == null)
            bladeTargetField = AccessTools.Field(typeof(StickPositioner), "bladeTarget");

        return bladeTargetField?.GetValue(positioner) as GameObject;
    }

    internal static void ApplyBladeDipFallback(StickPositioner positioner)
    {
        if (positioner == null || !positioner.IsGrounded)
            return;

        GameObject bladeTarget = GetBladeTarget(positioner);
        if (bladeTarget == null)
            return;

        Vector3 pos = bladeTarget.transform.position;
        int iceLayer = LayerMask.NameToLayer("Ice");
        if (iceLayer < 0)
            return;

        int mask = 1 << iceLayer;
        Vector3 origin = pos + Vector3.up * 0.35f;
        if (!RaycastSkippingFloorIce(origin, Vector3.down, out RaycastHit hit, 0.75f, mask))
            return;

        float targetY = hit.point.y;
        if (pos.y > targetY + 0.02f)
            return;

        pos.y = targetY;
        bladeTarget.transform.position = pos;
    }
}

[HarmonyPatch(typeof(StickPositioner), "FixedUpdate")]
public static class StickIcePassThrough_StickPositionerFixedUpdatePatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        StickIcePassThrough.EnterStickPositionerRaycast();
    }

    [HarmonyPostfix]
    public static void Postfix(StickPositioner __instance)
    {
        StickIcePassThrough.ExitStickPositionerRaycast();
        StickIcePassThrough.ApplyBladeDipFallback(__instance);
    }
}

public static class StickIcePassThrough_PhysicsRaycastPatch
{
    public static bool Prefix(
        Vector3 origin,
        Vector3 direction,
        ref RaycastHit hitInfo,
        float maxDistance,
        int layerMask,
        ref bool __result)
    {
        if (!StickIcePassThrough.StickPositionerRaycastActive)
            return true;

        __result = StickIcePassThrough.RaycastSkippingFloorIce(
            origin, direction, out hitInfo, maxDistance, layerMask);
        return false;
    }
}

[HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
public static class StickIcePassThrough_StickSpawnPatch
{
    [HarmonyPostfix]
    public static void Postfix(Stick __instance)
    {
        StickIcePassThrough.RegisterStick(__instance);
    }
}
