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

        // Stick↔Ice is NOT enabled here — SyncStickIceLayerPolicy owns that (dip vs push).
        if (slidablePropLayerIndex >= 0)
        {
            foreach (string layerName in new[]
                     {
                         "Default", "Player", "Player Body", "Body", "Puck", "Character",
                         "Barrier", "Boards", "Goal Post", "Goal Net", "Goal Net Cloth", "Goal Frame"
                     })
            {
                EnablePair(slidablePropLayerIndex, LayerMask.NameToLayer(layerName));
            }

            EnablePair(slidablePropLayerIndex, staticTrainingPropLayerIndex);
            FlamieLog.Info("[FlamiePrac] Slidable Ice pairs enabled (Ice↔Ice on for prop-to-prop).");
        }

        foreach (string layerName in new[]
                 {
                     "Puck", "Player", "Player Body", "Body", "Character",
                     "Boards", "Goal Post", "Default"
                 })
        {
            EnablePair(staticTrainingPropLayerIndex, LayerMask.NameToLayer(layerName));
        }

        int stickLayer = LayerMask.NameToLayer("Stick");
        if (stickLayer >= 0 && staticTrainingPropLayerIndex >= 0)
            Physics.IgnoreLayerCollision(stickLayer, staticTrainingPropLayerIndex, true);

        EnablePair(staticTrainingPropLayerIndex, slidablePropLayerIndex);
        FlamieLog.Info("[FlamiePrac] Slidable Ice=" + LayerMask.LayerToName(slidablePropLayerIndex) +
                       " staticTraining=" + staticTrainingPropLayerIndex +
                       " (Stick↔Ice toggled with slidable physics; floor ignored per collider).");

        StickIcePassThrough.ScanSceneFloorIce();
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
    }

    /// <summary>
    /// Slidable off: vanilla Stick↔Ice ignored (blade dip). Slidable on: Stick↔Ice enabled so
    /// beams/speakers push; rink floor still ignored per collider via StickIcePassThrough.
    /// </summary>
    public static void SyncStickIceLayerPolicy()
    {
        int stickLayer = LayerMask.NameToLayer("Stick");
        int iceLayer = CollisionHelper.GetSlidablePropLayerIndex();
        if (stickLayer < 0 || iceLayer < 0)
            return;

        bool ignoreStickIce = !FlamiePracFeatures.SlidablePhysicsEnabled;
        Physics.IgnoreLayerCollision(stickLayer, iceLayer, ignoreStickIce);

        bool stateChanged = !lastStickIceIgnored.HasValue || lastStickIceIgnored.Value != ignoreStickIce;
        lastStickIceIgnored = ignoreStickIce;

        if (stateChanged)
        {
            FlamieLog.Info("[FlamiePrac] Stick↔Ice layer ignore=" + ignoreStickIce +
                           " (slidablePhysics=" + FlamiePracFeatures.SlidablePhysicsEnabled + ").");
        }

        if (!ignoreStickIce)
            SlidableStickCollision.ReapplyAllStickPairs();
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
