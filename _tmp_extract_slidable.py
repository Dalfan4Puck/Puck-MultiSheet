using System.Reflection;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Slidables live on a dedicated layer (not Ice). Extend player/stick ground raycasts so
/// blades and bodies treat pushed beams/speakers as standable without touching Stick↔Ice policy.
/// </summary>
public static class SlidableGroundRaycastPatch
{
    private static FieldInfo stickRaycastLayerMaskField;
    private static FieldInfo hoverRaycastLayerMaskField;
    private static FieldInfo softColliderLayerMaskField;

    public static void ExtendRaycastMask(StickPositioner positioner)
    {
        if (positioner == null)
            return;

        ExtendLayerMaskOnObject(positioner, GetStickRaycastLayerMaskField());
    }

    public static void ExtendPlayerBodyGroundMasks(PlayerBody body)
    {
        if (body == null)
            return;

        int slidableLayer = CollisionHelper.GetSlidablePropLayerIndex();
        if (slidableLayer < 0)
            return;

        Hover hover = body.GetComponent<Hover>();
        if (hover != null)
            ExtendLayerMaskOnObject(hover, GetHoverRaycastLayerMaskField());

        SoftCollider soft = body.GetComponent<SoftCollider>();
        if (soft != null)
            ExtendLayerMaskOnObject(soft, GetSoftColliderLayerMaskField());
    }

    public static void RefreshAllStickPositioners()
    {
        StickPositioner[] positioners = Object.FindObjectsByType<StickPositioner>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < positioners.Length; i++)
            ExtendRaycastMask(positioners[i]);
    }

    public static void RefreshAllPlayerBodies()
    {
        PlayerBody[] bodies = Object.FindObjectsByType<PlayerBody>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < bodies.Length; i++)
            ExtendPlayerBodyGroundMasks(bodies[i]);
    }

    public static void RefreshAll()
    {
        RefreshAllStickPositioners();
        RefreshAllPlayerBodies();
    }

    private static void ExtendLayerMaskOnObject(object target, FieldInfo field)
    {
        if (target == null || field == null)
            return;

        int slidableLayer = CollisionHelper.GetSlidablePropLayerIndex();
        if (slidableLayer < 0)
            return;

        object raw = field.GetValue(target);
        if (raw is LayerMask mask)
        {
            mask.value |= 1 << slidableLayer;
            field.SetValue(target, mask);
        }
    }

    private static FieldInfo GetStickRaycastLayerMaskField()
    {
        if (stickRaycastLayerMaskField == null)
            stickRaycastLayerMaskField = AccessTools.Field(typeof(StickPositioner), "raycastLayerMask");

        return stickRaycastLayerMaskField;
    }

    private static FieldInfo GetHoverRaycastLayerMaskField()
    {
        if (hoverRaycastLayerMaskField == null)
            hoverRaycastLayerMaskField = AccessTools.Field(typeof(Hover), "raycastLayerMask");

        return hoverRaycastLayerMaskField;
    }

    private static FieldInfo GetSoftColliderLayerMaskField()
    {
        if (softColliderLayerMaskField == null)
            softColliderLayerMaskField = AccessTools.Field(typeof(SoftCollider), "layerMask");

        return softColliderLayerMaskField;
    }
}

[HarmonyPatch(typeof(StickPositioner), "OnNetworkPostSpawn")]
public static class SlidableGroundRaycastPatch_StickPositionerSpawn
{
    [HarmonyPostfix]
    public static void Postfix(StickPositioner __instance)
    {
        SlidableGroundRaycastPatch.ExtendRaycastMask(__instance);
    }
}

[HarmonyPatch(typeof(PlayerBody), "OnNetworkPostSpawn")]
public static class SlidableGroundRaycastPatch_PlayerBodySpawn
{
    [HarmonyPostfix]
    public static void Postfix(PlayerBody __instance)
    {
        SlidableGroundRaycastPatch.ExtendPlayerBodyGroundMasks(__instance);
    }
}
