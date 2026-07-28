public static class FlamiePracFeatures
{
    public const bool EnableSlidableProps = true;

    public const bool EnableHiveMotion = true;

    public const bool EnableRadio = true;

    public static bool SlidablePhysicsEnabled { get; private set; }

    public static void SetSlidablePhysicsEnabled(bool enabled)
    {
        if (SlidablePhysicsEnabled != enabled)
        {
            SlidablePhysicsEnabled = enabled;
            SlidableObstacle.ApplyPhysicsEnabled(enabled);
            SlidableBoardCollision.Ensure();
            if (enabled)
            {
                SlidableObstacleSync.ForceBroadcastAll();
            }
        }
    }
}
