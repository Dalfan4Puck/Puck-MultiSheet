using System.Reflection;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Slidables live on a dedicated layer (typically ~22, not Ice). Extend ground raycasts on
/// StickPositioner (stick blade) and Hover (player body) so skaters can stand, skate, and jump
/// on pushed beams/speakers without changing Stick↔Ice layer policy.
/// </summary>
public static class SlidableGroundRaycastPatch
{
    private static FieldInfo stickRaycastLayerMaskField;
    private static FieldInfo hoverRaycastLayerMaskField;

    public static void ExtendRaycastMask(StickPositioner positioner)
    {
        ExtendLayerMask(positioner, GetStickRaycastLayerMaskField());
    }

    public static void ExtendRaycastMask(Hover hover)
    {
        ExtendLayerMask(hover, GetHoverRaycastLayerMaskField());
    }

    public static void RefreshAllGroundRaycasts()
    {
        RefreshAllStickPositioners();
        RefreshAllPlayerHovers();
    }

    public static void RefreshAllStickPositioners()
    {
        StickPositioner[] positioners = Object.FindObjectsByType<StickPositioner>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < positioners.Length; i++)
            ExtendRaycastMask(positioners[i]);
    }

    public static void RefreshAllPlayerHovers()
    {
        PlayerBody[] bodies = Object.FindObjectsByType<PlayerBody>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < bodies.Length; i++)
        {
            PlayerBody body = bodies[i];
            if (body?.Hover != null)
                ExtendRaycastMask(body.Hover);
        }

        Hover[] hovers = Object.FindObjectsByType<Hover>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < hovers.Length; i++)
            ExtendRaycastMask(hovers[i]);
    }

    private static void ExtendLayerMask(object target, FieldInfo field)
    {
        if (target == null || field == null)
            return;

        int slidableLayer = CollisionHelper.GetSlidablePropLayerIndex();
        if (slidableLayer < 0)
            return;

        object raw = field.GetValue(target);
        if (raw is LayerMask mask)
        {
            int bit = 1 << slidableLayer;
            if ((mask.value & bit) != 0)
                return;

            mask.value |= bit;
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
        if (__instance?.Hover != null)
            SlidableGroundRaycastPatch.ExtendRaycastMask(__instance.Hover);
    }
}
