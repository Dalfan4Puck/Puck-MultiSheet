using System.Collections.Generic;
using PHLPracticeModPack;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// MultiSheet join helper: map clients / props to rink sheets and send CustomMessaging
/// only to clients whose body is on that sheet. Falls back to broadcast when MultiSheet
/// rink config is unavailable (standalone MyMod).
/// </summary>
public static class FlamiePracRinkInterest
{
    private static readonly Dictionary<int, List<ulong>> ClientsByRink =
        new Dictionary<int, List<ulong>>(8);
    private static readonly List<ulong> EmptyClients = new List<ulong>(0);
    private static readonly List<int> OccupiedRinks = new List<int>(8);
    private static int rebuildFrame = -1;
    private static bool multiRinkActive;
    private static bool loggedMode;

    /// <summary>
    /// True when MultiSheet has 2+ rinks — Flamie high-rate streams should interest-filter.
    /// </summary>
    public static bool UseInterestFilter
    {
        get
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            return cfg?.Rinks != null && cfg.Rinks.Count >= 2;
        }
    }

    public static int RinkOfWorldPosition(Vector3 worldPosition)
    {
        MultiRinkConfig cfg = MultiRinkConfig.Current;
        if (cfg?.Rinks == null || cfg.Rinks.Count == 0)
            return 0;
        return RinkLocator.NearestRink(cfg, worldPosition);
    }

    /// <summary>
    /// Rebuild body→rink client groups once per frame. Clients without a spawned body
    /// are omitted (MOTD / spectator) — they do not need Flamie pose streams.
    /// </summary>
    public static void RebuildClientGroups(NetworkManager nm)
    {
        if (nm == null)
            return;

        int frame = Time.frameCount;
        if (frame == rebuildFrame)
            return;
        rebuildFrame = frame;

        foreach (List<ulong> list in ClientsByRink.Values)
            list.Clear();
        OccupiedRinks.Clear();

        multiRinkActive = UseInterestFilter;
        if (!multiRinkActive)
        {
            if (!loggedMode)
            {
                loggedMode = true;
                FlamieLog.InfoOnce("rink-interest", "[FlamiePrac] Rink interest: MultiSheet multi-rink inactive — broadcast sync.");
            }
            return;
        }

        if (!loggedMode)
        {
            loggedMode = true;
            FlamieLog.InfoOnce("rink-interest", "[FlamiePrac] Rink interest: filtering slidable/motion sync by client sheet.");
        }

        try
        {
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null || nm.ConnectedClientsList == null)
                return;

            foreach (NetworkClient client in nm.ConnectedClientsList)
            {
                if (client == null || client.ClientId == NetworkManager.ServerClientId)
                    continue;

                Player player = pm.GetPlayerByClientId(client.ClientId);
                PlayerBody body = player != null ? player.PlayerBody : null;
                if (body == null)
                    continue;

                int rink = RinkOfWorldPosition(body.transform.position);
                if (!ClientsByRink.TryGetValue(rink, out List<ulong> list))
                {
                    list = new List<ulong>(4);
                    ClientsByRink.Add(rink, list);
                }

                list.Add(client.ClientId);
            }

            foreach (KeyValuePair<int, List<ulong>> kvp in ClientsByRink)
            {
                if (kvp.Value.Count > 0)
                    OccupiedRinks.Add(kvp.Key);
            }
        }
        catch
        {
            // Leave groups empty — callers treat as "no interested clients".
        }
    }

    public static IReadOnlyList<ulong> ClientsOnRink(int rinkIndex)
    {
        if (!multiRinkActive)
            return EmptyClients;
        if (!ClientsByRink.TryGetValue(rinkIndex, out List<ulong> list) || list == null)
            return EmptyClients;
        return list;
    }

    public static bool AnyClientsOnRink(int rinkIndex)
    {
        if (!multiRinkActive)
            return true; // caller should broadcast
        return ClientsByRink.TryGetValue(rinkIndex, out List<ulong> list) && list != null && list.Count > 0;
    }

    public static bool AnyInterestedClients()
    {
        if (!multiRinkActive)
            return true;
        return OccupiedRinks.Count > 0;
    }

    public static void SendToClients(
        NetworkManager nm,
        string channel,
        FastBufferWriter writer,
        IReadOnlyList<ulong> clients,
        NetworkDelivery delivery)
    {
        if (nm?.CustomMessagingManager == null || clients == null || clients.Count == 0)
            return;

        CustomMessagingManager messaging = nm.CustomMessagingManager;
        for (int i = 0; i < clients.Count; i++)
            messaging.SendNamedMessage(channel, clients[i], writer, delivery);
    }

    public static void SendBroadcastOrToAllBodies(
        NetworkManager nm,
        string channel,
        FastBufferWriter writer,
        NetworkDelivery delivery)
    {
        if (nm?.CustomMessagingManager == null)
            return;

        if (!multiRinkActive)
        {
            nm.CustomMessagingManager.SendNamedMessageToAll(channel, writer, delivery);
            return;
        }

        // Params / rare messages: every client with a body, all occupied rinks.
        for (int i = 0; i < OccupiedRinks.Count; i++)
            SendToClients(nm, channel, writer, ClientsOnRink(OccupiedRinks[i]), delivery);
    }

    public static void ResetLogFlag()
    {
        loggedMode = false;
        rebuildFrame = -1;
    }
}
