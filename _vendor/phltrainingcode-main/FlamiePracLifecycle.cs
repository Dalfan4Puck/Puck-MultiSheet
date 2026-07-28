using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Coordinates full mod teardown on <see cref="MyMod.Class1.OnDisable"/> so nothing outlives the plugin.
/// </summary>
public static class FlamiePracLifecycle
{
    public const float DefaultRotatorSpeed = 200f;

    public static void Shutdown()
    {
        RadioHudUI.TearDown();
        RadioHudUI.CleanupLegacyUi();

        if (TrainingSync.Instance != null)
            TrainingSync.Instance.PerformShutdown();

        TrainingObjectManager.Instance?.Shutdown();
        FlamiePracTrainingGoalie.Despawn();

        ConstantRotator.globalSpeed = DefaultRotatorSpeed;

        DestroyOrphans();

        FlamieLog.Info("[FlamiePrac] Lifecycle shutdown complete.");
    }

    private static void DestroyOrphans()
    {
        GameObject bootstrap = GameObject.Find("FlamiePrac_Bootstrap");
        if (bootstrap != null)
            Object.Destroy(bootstrap);

        RadioController[] radios = Object.FindObjectsByType<RadioController>(FindObjectsSortMode.None);
        foreach (RadioController radio in radios)
        {
            if (radio != null)
                radio.PrepareForDestroy();
        }
    }
}
