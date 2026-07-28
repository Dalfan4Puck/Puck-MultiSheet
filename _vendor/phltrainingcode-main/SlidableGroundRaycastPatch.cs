using System.Reflection;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Slidables live on a dedicated layer (not Ice). Extend StickPositioner ground raycasts so
/// blades treat pushed beams/speakers as standable without touching Stick↔Ice layer policy.
/// </summary>
public static class SlidableGroundRaycastPatch
{
    private static FieldInfo raycastLayerMaskField;

    public static void ExtendRaycastMask(StickPositioner positioner)
    {
        if (positioner == null)
            return;

        int slidableLayer = CollisionHelper.GetSlidablePropLayerIndex();
        if (slidableLayer < 0)
            return;

        FieldInfo field = GetRaycastLayerMaskField();
        if (field == null)
            return;

        object raw = field.GetValue(positioner);
        if (raw is LayerMask mask)
        {
            mask.value |= 1 << slidableLayer;
            field.SetValue(positioner, mask);
        }
    }

    public static void RefreshAllStickPositioners()
    {
        StickPositioner[] positioners = Object.FindObjectsByType<StickPositioner>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < positioners.Length; i++)
            ExtendRaycastMask(positioners[i]);
    }

    private static FieldInfo GetRaycastLayerMaskField()
    {
        if (raycastLayerMaskField == null)
            raycastLayerMaskField = AccessTools.Field(typeof(StickPositioner), "raycastLayerMask");

        return raycastLayerMaskField;
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
