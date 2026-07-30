using System.Collections.Generic;

public static class FlamiePracFeatures
{
    public const bool EnableSlidableProps = true;

    public const bool EnableHiveMotion = true;

    public const bool EnableRadio = true;

    /// <summary>
    /// When true (MultiSheet), radio only starts from server sync / Rinks UI — no client bootstrap or offline shuffle.
    /// </summary>
    public static bool RadioServerDrivenOnly { get; set; }

    private static readonly HashSet<int> SlidableRinks = new HashSet<int>();

    /// <summary>True when any rink has slidable physics enabled (layer policy).</summary>
    public static bool SlidablePhysicsEnabled => SlidableRinks.Count > 0;

    public static bool AnySlidablePhysicsEnabled => SlidableRinks.Count > 0;

    public static bool IsSlidablePhysicsEnabled(int rinkIndex)
    {
        return rinkIndex >= 0 && SlidableRinks.Contains(rinkIndex);
    }

    public static void SetSlidablePhysicsEnabled(int rinkIndex, bool enabled)
    {
        if (rinkIndex < 0)
            return;

        bool wasEnabled = SlidableRinks.Contains(rinkIndex);
        if (enabled)
            SlidableRinks.Add(rinkIndex);
        else
            SlidableRinks.Remove(rinkIndex);

        if (wasEnabled == enabled)
            return;

        SlidableObstacle.ApplyPhysicsEnabledForRink(rinkIndex, enabled);
        SlidableBoardCollision.Ensure();
        SlidableBoardCollision.ReassertSlidablePairs();
        SlidableBoardCollision.SyncStickIceLayerPolicy();
        StickIcePassThrough.ScanSceneFloorIce(logResult: true);
        SlidableGroundRaycastPatch.RefreshAllStickPositioners();
        if (enabled)
            SlidableObstacleSync.ForceBroadcastAll();
    }

    /// <summary>Legacy global toggle — applies to rink indices 0..8.</summary>
    public static void SetSlidablePhysicsEnabled(bool enabled)
    {
        for (int i = 0; i < 9; i++)
            SetSlidablePhysicsEnabled(i, enabled);
    }
}
