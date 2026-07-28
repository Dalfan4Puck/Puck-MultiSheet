using System;
using Object = UnityEngine.Object;
using System.Collections.Generic;
using UnityEngine;

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
        {
            return;
        }
        Collider[] array = CollectPhysicalColliders(root);
        if (array.Length != 0)
        {
            Collider[] array2 = array;
            for (int i = 0; i < array2.Length; i++)
            {
                RegisterIgnoredPuckCollider(array2[i]);
            }
        }
    }

    public static void Unregister(GameObject root)
    {
        if (!(root == null))
        {
            Collider[] array = CollectPhysicalColliders(root);
            foreach (Collider item in array)
            {
                IgnoredPuckColliders.Remove(item);
            }
            Transform val = root.transform.Find("PuckShield");
            if (val != null)
            {
                Object.Destroy(((Component)val).gameObject);
            }
        }
    }

    private static Collider[] CollectPhysicalColliders(GameObject root)
    {
        List<Collider> list = new List<Collider>();
        Collider[] componentsInChildren = root.GetComponentsInChildren<Collider>(true);
        foreach (Collider val in componentsInChildren)
        {
            if (val != null && !val.isTrigger)
            {
                list.Add(val);
            }
        }
        return list.ToArray();
    }

    private static void RegisterIgnoredPuckCollider(Collider col)
    {
        if (!(col == null) && !IgnoredPuckColliders.Contains(col))
        {
            IgnoredPuckColliders.Add(col);
            IgnoreAllPucks(col);
            EnsureEventRegistered();
        }
    }

    private static void EnsureEventRegistered()
    {
        if (!eventRegistered)
        {
            onPuckSpawned = OnPuckSpawned;
            EventManager.AddEventListener("Event_Everyone_OnPuckSpawned", onPuckSpawned);
            eventRegistered = true;
        }
    }

    public static void Shutdown()
    {
        if (eventRegistered && onPuckSpawned != null)
        {
            try
            {
                EventManager.RemoveEventListener("Event_Everyone_OnPuckSpawned", onPuckSpawned);
            }
            catch
            {
            }
            eventRegistered = false;
            onPuckSpawned = null;
            IgnoredPuckColliders.Clear();
        }
    }

    private static void OnPuckSpawned(Dictionary<string, object> message)
    {
        if (message == null || !message.TryGetValue("puck", out var value))
        {
            return;
        }
        Puck val = (Puck)((value is Puck) ? value : null);
        if (val == null)
        {
            return;
        }
        for (int i = 0; i < IgnoredPuckColliders.Count; i++)
        {
            Collider val2 = IgnoredPuckColliders[i];
            if (val2 != null)
            {
                IgnorePuck(val2, val);
            }
        }
    }

    private static void IgnoreAllPucks(Collider slidableCol)
    {
        PuckManager instance = MonoBehaviourSingleton<PuckManager>.Instance;
        if (instance == null)
        {
            return;
        }
        List<Puck> pucks = instance.GetPucks(false);
        if (pucks != null)
        {
            for (int i = 0; i < pucks.Count; i++)
            {
                IgnorePuck(slidableCol, pucks[i]);
            }
        }
    }

    private static void IgnorePuck(Collider slidableCol, Puck puck)
    {
        if (slidableCol == null || puck == null)
        {
            return;
        }
        Collider iceCollider = puck.IceCollider;
        if (iceCollider != null)
        {
            Physics.IgnoreCollision(slidableCol, iceCollider, true);
        }
        Collider stickCollider = puck.StickCollider;
        if (stickCollider != null)
        {
            Physics.IgnoreCollision(slidableCol, stickCollider, true);
        }
        SphereCollider netSphereCollider = puck.NetSphereCollider;
        if (netSphereCollider != null)
        {
            Physics.IgnoreCollision(slidableCol, (Collider)(object)netSphereCollider, true);
        }
        Collider[] componentsInChildren = ((Component)puck).GetComponentsInChildren<Collider>(true);
        foreach (Collider val in componentsInChildren)
        {
            if (!(val == null) && !val.isTrigger)
            {
                Physics.IgnoreCollision(slidableCol, val, true);
            }
        }
    }
}
