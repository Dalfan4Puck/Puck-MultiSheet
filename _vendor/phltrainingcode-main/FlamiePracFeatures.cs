public static class FlamiePracFeatures
{
    public const bool EnableSlidableProps = true;

    public const bool EnableHiveMotion = true;

    public const bool EnableRadio = true;

    /// <summary>
    /// When true (MultiSheet), radio only starts from server sync / Rinks UI — no client bootstrap or offline shuffle.
    /// </summary>
    public static bool RadioServerDrivenOnly { get; set; }

    public static bool SlidablePhysicsEnabled { get; private set; }

    public static void SetSlidablePhysicsEnabled(bool enabled)
    {
        if (SlidablePhysicsEnabled != enabled)
        {
            SlidablePhysicsEnabled = enabled;
            SlidableObstacle.ApplyPhysicsEnabled(enabled);
            SlidableBoardCollision.Ensure();
            SlidableBoardCollision.ReassertSlidablePairs();
            SlidableBoardCollision.SyncStickIceLayerPolicy();
            StickIcePassThrough.ScanSceneFloorIce(logResult: true);
            if (enabled)
            {
                SlidableObstacleSync.ForceBroadcastAll();
            }
        }
    }
}
