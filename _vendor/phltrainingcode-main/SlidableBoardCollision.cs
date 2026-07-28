using UnityEngine;

public static class SlidableBoardCollision
{
    private static bool configured;

    public static void Ensure()
    {
        int slidablePropLayerIndex = CollisionHelper.GetSlidablePropLayerIndex();
        // Puck/rink load may flip Ice↔Ice off — reassert so beam/speakers block each other.
        if (slidablePropLayerIndex >= 0)
            Physics.IgnoreLayerCollision(slidablePropLayerIndex, slidablePropLayerIndex, false);

        if (configured)
            return;

        configured = true;

        int staticTrainingPropLayerIndex = CollisionHelper.GetStaticTrainingPropLayerIndex();
        int stickLayer = LayerMask.NameToLayer("Stick");

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

        if (stickLayer >= 0 && staticTrainingPropLayerIndex >= 0)
            Physics.IgnoreLayerCollision(stickLayer, staticTrainingPropLayerIndex, true);

        EnablePair(staticTrainingPropLayerIndex, slidablePropLayerIndex);
        FlamieLog.Info("[FlamiePrac] Slidable Ice=" + LayerMask.LayerToName(slidablePropLayerIndex) +
                       " staticTraining=" + staticTrainingPropLayerIndex +
                       " (Stick↔static ignored; props collide on Ice).");
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
