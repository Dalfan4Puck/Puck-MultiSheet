using UnityEngine;

/// <summary>
/// Sheet origin for Flamie layout locals. Layout JSON stays in Rink-1 local space;
/// world pose = local + Current. MultiSheet default grid: 64m X / 128m Z.
/// Phase 1: RinkIndex = 1 → (0,0,0). Flip to 2 for (+64,0,0) once ready.
/// </summary>
public static class RinkOrigin
{
    public const float SpacingX = 64f;
    public const float SpacingZ = 128f;

    private static int activeRinkIndex = 1;

    /// <summary>1-based sheet index (1 = MultiSheet Rink 1 / world origin).</summary>
    public static int ActiveRinkIndex
    {
        get => activeRinkIndex;
        set => activeRinkIndex = Mathf.Max(1, value);
    }

    public static Vector3 Current => OriginFor(ActiveRinkIndex);

    public static Vector3 OriginFor(int rinkIndex)
    {
        int i = Mathf.Max(1, rinkIndex) - 1;
        return new Vector3((i % 3) * SpacingX, 0f, (i / 3) * SpacingZ);
    }

    public static Vector3 Apply(Vector3 localRinkPos) => localRinkPos + Current;

    public static float ApplyX(float localX) => localX + Current.x;

    public static float ApplyZ(float localZ) => localZ + Current.z;

    public static void ConfigureFromLayout(int rinkIndex)
    {
        ActiveRinkIndex = rinkIndex > 0 ? rinkIndex : 1;
        FlamieLog.Info("[FlamiePrac] RinkOrigin sheet=" + ActiveRinkIndex +
                  " worldOrigin=" + Current.ToString("F2"));
    }
}
