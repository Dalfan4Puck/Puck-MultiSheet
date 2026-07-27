using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pass-back bumpers ignore puck colliders so shots trigger the pass trigger without knocking boards out of place.
/// Movable hive props (speakers, beams) keep normal puck collision.
/// </summary>
public static class SlidablePuckFilter
{
    private const string PuckShieldName = "PuckShield";
    private const string PuckSpawnEvent = "Event_Everyone_OnPuckSpawned";

    private static readonly List<Collider> IgnoredPuckColliders = new List<Collider>();
    private static Action<Dictionary<string, object>> onPuckSpawned;
    private static bool eventRegistered;

    public static void ConfigureForPlayerPushOnly(GameObject root)
    {
        if (root == null)
            return;

        Collider[] cols = CollectPhysicalColliders(root);
        if (cols.Length == 0)
            return;

        foreach (Collider col in cols)
            RegisterIgnoredPuckCollider(col);
    }

    public static void Unregister(GameObject root)
    {
        if (root == null)
            return;

        foreach (Collider col in CollectPhysicalColliders(root))
            IgnoredPuckColliders.Remove(col);

        Transform shield = root.transform.Find(PuckShieldName);
        if (shield != null)
            UnityEngine.Object.Destroy(shield.gameObject);
    }

    private static Collider[] CollectPhysicalColliders(GameObject root)
    {
        var cols = new List<Collider>();
        foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
        {
            if (col != null && !col.isTrigger)
                cols.Add(col);
        }

        return cols.ToArray();
    }

    private static void RegisterIgnoredPuckCollider(Collider col)
    {
        if (col == null || IgnoredPuckColliders.Contains(col))
            return;

        IgnoredPuckColliders.Add(col);
        IgnoreAllPucks(col);
        EnsureEventRegistered();
    }

    private static void EnsureEventRegistered()
    {
        if (eventRegistered)
            return;

        onPuckSpawned = OnPuckSpawned;
        EventManager.AddEventListener(PuckSpawnEvent, onPuckSpawned);
        eventRegistered = true;
    }

    public static void Shutdown()
    {
        if (!eventRegistered || onPuckSpawned == null)
            return;

        try
        {
            EventManager.RemoveEventListener(PuckSpawnEvent, onPuckSpawned);
        }
        catch { }

        eventRegistered = false;
        onPuckSpawned = null;
        IgnoredPuckColliders.Clear();
    }

    private static void OnPuckSpawned(Dictionary<string, object> message)
    {
        if (message == null ||
            !message.TryGetValue("puck", out object puckObj) ||
            puckObj is not Puck puck)
            return;

        for (int i = 0; i < IgnoredPuckColliders.Count; i++)
        {
            Collider col = IgnoredPuckColliders[i];
            if (col != null)
                IgnorePuck(col, puck);
        }
    }

    private static void IgnoreAllPucks(Collider slidableCol)
    {
        Puck[] pucks = UnityEngine.Object.FindObjectsByType<Puck>(FindObjectsSortMode.None);
        foreach (Puck puck in pucks)
            IgnorePuck(slidableCol, puck);
    }

    private static void IgnorePuck(Collider slidableCol, Puck puck)
    {
        if (slidableCol == null || puck == null)
            return;

        // Ice + Stick alone left NetSphereCollider colliding — center hits "died" on the solid board
        // while edge shots still reached the HitFace trigger.
        Collider ice = puck.IceCollider;
        if (ice != null)
            Physics.IgnoreCollision(slidableCol, ice, true);

        Collider stick = puck.StickCollider;
        if (stick != null)
            Physics.IgnoreCollision(slidableCol, stick, true);

        SphereCollider net = puck.NetSphereCollider;
        if (net != null)
            Physics.IgnoreCollision(slidableCol, net, true);

        foreach (Collider col in puck.GetComponentsInChildren<Collider>(true))
        {
            if (col == null || col.isTrigger)
                continue;

            Physics.IgnoreCollision(slidableCol, col, true);
        }
    }
}
