using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Standalone MyMod shims for MultiSheet rink helpers. Real implementations live in
/// FlamiePracRinkInterest.cs / FlamiePracRinkVisibility.cs and are compiled only into
/// MultiSheet (PHLPracticeModPack). This file is compiled only by MyMod.csproj.
/// </summary>
public static class FlamiePracRinkInterest
{
    private static readonly List<ulong> EmptyClients = new List<ulong>(0);

    public static bool UseInterestFilter => false;

    public static int RinkOfWorldPosition(Vector3 worldPosition) => 0;

    public static void RebuildClientGroups(NetworkManager nm)
    {
    }

    public static IReadOnlyList<ulong> ClientsOnRink(int rinkIndex) => EmptyClients;

    public static bool AnyClientsOnRink(int rinkIndex) => true;

    public static bool AnyInterestedClients() => true;

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
        nm?.CustomMessagingManager?.SendNamedMessageToAll(channel, writer, delivery);
    }

    public static void ResetLogFlag()
    {
    }
}

/// <summary>No-op visibility cull when MultiSheet is not hosting Flamie.</summary>
public static class FlamiePracRinkVisibility
{
    public static void Tick(Transform clientVisualRoot)
    {
    }

    public static void Clear()
    {
    }
}
