using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// When slidable physics is off, vanilla Stick↔Ice is ignored at the layer matrix. When on,
/// Stick↔Ice is enabled for push and slidables also opt in per collider as a fallback.
/// Rink floor ice stays ignored per collider via StickIcePassThrough.
/// </summary>
public static class SlidableStickCollision
{
    private const string StickSpawnEvent = "Event_Everyone_OnStickSpawned";

    private static readonly List<Collider> SlidableColliders = new List<Collider>();

    private static Action<Dictionary<string, object>> onStickSpawned;

    private static bool eventRegistered;

    public static bool IsRegisteredSlidableCollider(Collider col)
    {
        if (col == null)
            return false;

        PruneDestroyed(SlidableColliders);
        return SlidableColliders.Contains(col);
    }

    public static void RegisterSlidable(GameObject root)
    {
        if (root == null)
            return;

        Collider[] cols = CollectPhysicalColliders(root);
        for (int i = 0; i < cols.Length; i++)
            RegisterSlidableCollider(cols[i]);
    }

    public static void Unregister(GameObject root)
    {
        if (root == null)
            return;

        Collider[] cols = CollectPhysicalColliders(root);
        for (int i = 0; i < cols.Length; i++)
            SlidableColliders.Remove(cols[i]);
    }

    public static void ApplyStick(Stick stick)
    {
        if (stick == null)
            return;

        PruneDestroyed(SlidableColliders);
        for (int i = 0; i < SlidableColliders.Count; i++)
        {
            Collider slidableCol = SlidableColliders[i];
            if (slidableCol != null)
                EnableStickHit(slidableCol, stick);
        }
    }

    public static void ReapplyAllStickPairs()
    {
        PruneDestroyed(SlidableColliders);
        if (SlidableColliders.Count == 0)
            return;

        Stick[] sticks = UnityEngine.Object.FindObjectsByType<Stick>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < sticks.Length; i++)
            ApplyStick(sticks[i]);
    }

    private static void RegisterSlidableCollider(Collider col)
    {
        if (col == null)
            return;

        PruneDestroyed(SlidableColliders);
        if (SlidableColliders.Contains(col))
        {
            EnableStickHits(col);
            return;
        }

        SlidableColliders.Add(col);
        EnableStickHits(col);
        EnsureStickSpawnListener();
    }

    private static void EnableStickHits(Collider slidableCol)
    {
        Stick[] sticks = UnityEngine.Object.FindObjectsByType<Stick>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < sticks.Length; i++)
            EnableStickHit(slidableCol, sticks[i]);
    }

    private static void EnableStickHit(Collider slidableCol, Stick stick)
    {
        if (slidableCol == null || stick == null)
            return;

        foreach (Collider stickCol in CollectStickColliders(stick))
            Physics.IgnoreCollision(slidableCol, stickCol, false);
    }

    private static IEnumerable<Collider> CollectStickColliders(Stick stick)
    {
        if (stick == null)
            yield break;

        StickMesh stickMesh = stick.StickMesh;
        if (stickMesh != null)
        {
            if (stickMesh.BladeCollider != null && !stickMesh.BladeCollider.isTrigger)
                yield return stickMesh.BladeCollider;

            if (stickMesh.ShaftCollider != null && !stickMesh.ShaftCollider.isTrigger)
                yield return stickMesh.ShaftCollider;
        }

        Collider[] cols = stick.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            Collider col = cols[i];
            if (col != null && !col.isTrigger)
                yield return col;
        }
    }

    private static Collider[] CollectPhysicalColliders(GameObject root)
    {
        List<Collider> list = new List<Collider>();
        Collider[] cols = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            Collider col = cols[i];
            if (col != null && !col.isTrigger)
                list.Add(col);
        }

        return list.ToArray();
    }

    private static void EnsureStickSpawnListener()
    {
        if (eventRegistered)
            return;

        onStickSpawned = OnStickSpawned;
        EventManager.AddEventListener(StickSpawnEvent, onStickSpawned);
        eventRegistered = true;
    }

    private static void OnStickSpawned(Dictionary<string, object> message)
    {
        if (message == null || !message.TryGetValue("stick", out object value))
            return;

        Stick stick = value as Stick;
        if (stick == null)
            return;

        ApplyStick(stick);
    }

    private static void PruneDestroyed(List<Collider> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] == null)
                list.RemoveAt(i);
        }
    }

    public static void Shutdown()
    {
        if (!eventRegistered || onStickSpawned == null)
            return;

        try
        {
            EventManager.RemoveEventListener(StickSpawnEvent, onStickSpawned);
        }
        catch
        {
        }

        eventRegistered = false;
        onStickSpawned = null;
        SlidableColliders.Clear();
    }
}
