using UnityEngine;

/// <summary>
/// Slidable props sit on the Ice layer (skate/stand like rink ice for stick raycasts).
/// Static hive props use an unused user layer so Stick can ignore them while Puck/Player/Ice still collide.
/// </summary>
public static class SlidableBoardCollision
{
    private static bool configured;

    public static void Ensure()
    {
        int ice = CollisionHelper.GetSlidablePropLayerIndex();

        // Puck (or rink load) may re-disable Ice↔Ice after our first setup. Always reassert —
        // otherwise beam/speakers on Ice phase through each other.
        if (ice >= 0)
            Physics.IgnoreLayerCollision(ice, ice, false);

        if (configured)
            return;

        configured = true;

        int staticTraining = CollisionHelper.GetStaticTrainingPropLayerIndex();
        int stick = LayerMask.NameToLayer("Stick");

        // Ice slidables: stick push, skate, body, puck, boards, and goal cage/netting.
        foreach (string layerName in new[]
                 {
                     "Default", "Player", "Player Body", "Body", "Stick", "Puck", "Character",
                     "Barrier", "Boards", "Goal Post",
                     "Goal Net", "Goal Net Cloth", "Goal Frame"
                 })
        {
            EnablePair(ice, LayerMask.NameToLayer(layerName));
        }

        EnablePair(ice, staticTraining);

        Debug.Log("[FlamiePrac] Ice↔Ice collision enabled for slidable prop interaction.");

        // Static hive (cones, decor dummy, rotating sticks, tutor…): solid for puck/player/slidables,
        // but player sticks phase through.
        foreach (string layerName in new[]
                 {
                     "Puck", "Player", "Player Body", "Body", "Character",
                     "Boards", "Goal Post", "Default"
                 })
        {
            EnablePair(staticTraining, LayerMask.NameToLayer(layerName));
        }

        if (stick >= 0 && staticTraining >= 0)
            Physics.IgnoreLayerCollision(stick, staticTraining, true);

        Debug.Log("[FlamiePrac] Slidable Ice=" + LayerMask.LayerToName(ice) +
                  " staticTraining=" + staticTraining +
                  " (Stick↔static ignored; Puck/Player/Ice still collide).");
    }

    public static bool IsBoardLayer(int layer)
    {
        string name = LayerMask.LayerToName(layer);
        return name == "Barrier" || name == "Boards" || name == "Goal Post" ||
               name == "Goal Net" || name == "Goal Net Cloth" || name == "Goal Frame";
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
        if (layerA < 0 || layerB < 0)
            return;

        Physics.IgnoreLayerCollision(layerA, layerB, false);
    }
}
