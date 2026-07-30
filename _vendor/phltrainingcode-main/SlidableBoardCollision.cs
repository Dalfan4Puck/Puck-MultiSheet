using UnityEngine;

public static class SlidableBoardCollision
{
    private static bool configured;
    private static bool? lastStickIceIgnored;

    /// <summary>One-time layer setup. Safe to call from spawn paths; no-ops after first run.</summary>
    public static void Ensure()
    {
        if (configured)
            return;

        configured = true;

        int slidablePropLayerIndex = CollisionHelper.GetSlidablePropLayerIndex();
        int staticTrainingPropLayerIndex = CollisionHelper.GetStaticTrainingPropLayerIndex();
        int stickLayer = LayerMask.NameToLayer("Stick");
        int vanillaIceLayer = LayerMask.NameToLayer("Ice");

        // Benchmark: blade always dips through rink Ice; slidables live on a separate layer.
        if (stickLayer >= 0 && vanillaIceLayer >= 0)
            Physics.IgnoreLayerCollision(stickLayer, vanillaIceLayer, true);

        if (slidablePropLayerIndex >= 0)
        {
            foreach (string layerName in new[]
                     {
                         "Default", "Player", "Player Body", "Body", "Stick", "Puck", "Character",
                         "Barrier", "Boards", "Goal Post", "Goal Net", "Goal Net Cloth", "Goal Frame"
                     })
            {
                EnablePair(slidablePropLayerIndex, LayerMask.NameToLayer(layerName));
            }

            EnablePair(slidablePropLayerIndex, staticTrainingPropLayerIndex);

            if (vanillaIceLayer >= 0)
                Physics.IgnoreLayerCollision(slidablePropLayerIndex, vanillaIceLayer, true);

            FlamieLog.Info("[FlamiePrac] Slidable prop layer=" + slidablePropLayerIndex +
                           " (Stick↔vanilla Ice ignored; Stick↔slidable toggled with physics).");
        }

        foreach (string layerName in new[]
                 {
                     "Puck", "Player", "Player Body", "Body", "Character",
                     "Boards", "Goal Post", "Default"
                 })
        {
            EnablePair(staticTrainingPropLayerIndex, LayerMask.NameToLayer(layerName));
        }

        if (stickLayer >= 0 && staticTrainingPropLayerIndex >= 0)
            Physics.IgnoreLayerCollision(stickLayer, staticTrainingPropLayerIndex, true);

        EnablePair(staticTrainingPropLayerIndex, slidablePropLayerIndex);
        FlamieLog.Info("[FlamiePrac] Static training layer=" + staticTrainingPropLayerIndex +
                       " slidable=" + slidablePropLayerIndex + ".");

        StickIcePassThrough.ScanSceneFloorIce();
        SyncStickIceLayerPolicy();
    }

    /// <summary>
    /// Cheap reassert after level load or toggle — Puck may flip Ice↔Ice off.
    /// Not for SlidableObstacle FixedUpdate (benchmark never did per-frame collision work).
    /// </summary>
    public static void ReassertSlidablePairs()
    {
        int slidablePropLayerIndex = CollisionHelper.GetSlidablePropLayerIndex();
        if (slidablePropLayerIndex < 0)
            return;

        Physics.IgnoreLayerCollision(slidablePropLayerIndex, slidablePropLayerIndex, false);

        int puckLayer = LayerMask.NameToLayer("Puck");
        if (puckLayer >= 0)
            Physics.IgnoreLayerCollision(slidablePropLayerIndex, puckLayer, false);

        int vanillaIceLayer = LayerMask.NameToLayer("Ice");
        if (vanillaIceLayer >= 0)
            Physics.IgnoreLayerCollision(slidablePropLayerIndex, vanillaIceLayer, true);
    }

    /// <summary>
    /// Slidable off: Stick↔slidable ignored (vanilla dip). Slidable on: Stick↔slidable enabled;
    /// Stick↔vanilla Ice stays ignored; rink floor pass-through uses StickIcePassThrough.
    /// </summary>
    public static void SyncStickIceLayerPolicy()
    {
        int stickLayer = LayerMask.NameToLayer("Stick");
        int slidableLayer = CollisionHelper.GetSlidablePropLayerIndex();
        int vanillaIceLayer = LayerMask.NameToLayer("Ice");
        if (stickLayer < 0 || slidableLayer < 0)
            return;

        if (vanillaIceLayer >= 0)
            Physics.IgnoreLayerCollision(stickLayer, vanillaIceLayer, true);

        bool ignoreStickSlidable = !FlamiePracFeatures.AnySlidablePhysicsEnabled;
        Physics.IgnoreLayerCollision(stickLayer, slidableLayer, ignoreStickSlidable);

        bool stateChanged = !lastStickIceIgnored.HasValue || lastStickIceIgnored.Value != ignoreStickSlidable;
        lastStickIceIgnored = ignoreStickSlidable;

        if (stateChanged)
        {
            FlamieLog.Info("[FlamiePrac] Stick↔slidable layer ignore=" + ignoreStickSlidable +
                           " (slidablePhysics=" + FlamiePracFeatures.SlidablePhysicsEnabled + ").");
        }

        if (!ignoreStickSlidable)
            SlidableStickCollision.ReapplyAllStickPairs();
    }

    public static bool IsSlidablePropLayer(int layer)
    {
        int slidablePropLayerIndex = CollisionHelper.GetSlidablePropLayerIndex();
        return slidablePropLayerIndex >= 0 && layer == slidablePropLayerIndex;
    }

    public static bool IsBoardLayer(int layer)
    {
        string name = LayerMask.LayerToName(layer);
        switch (name)
        {
        default:
            return name == "Goal Frame";
        case "Barrier":
        case "Boards":
        case "Goal Post":
        case "Goal Net":
        case "Goal Net Cloth":
            return true;
        }
    }

    public static void CancelVelocityIntoBoard(Rigidbody body, Collision collision)
    {
        if (body == null || collision == null)
            return;

        Vector3 vel = body.linearVelocity;
        int contactCount = collision.contactCount;
        for (int i = 0; i < contactCount; i++)
        {
            Vector3 normal = collision.GetContact(i).normal;
            if (normal.sqrMagnitude < 0.0001f)
                continue;

            float into = Vector3.Dot(vel, normal);
            if (into < 0f)
                vel -= normal * into;
        }

        body.linearVelocity = vel;
    }

    private static void EnablePair(int layerA, int layerB)
    {
        if (layerA >= 0 && layerB >= 0)
            Physics.IgnoreLayerCollision(layerA, layerB, false);
    }
}
