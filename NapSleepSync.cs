using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Replicates voluntary /nap state to clients for sleep-letter VFX.
    /// </summary>
    internal static class NapSleepSync
    {
        private const string Channel = "multisheet-nap-v1";

        private static readonly HashSet<ulong> nappingClients = new HashSet<ulong>();
        private static CustomMessagingManager registeredMessaging;
        private static NetworkManager registeredManager;

        internal static bool IsNapping(ulong clientId) => nappingClients.Contains(clientId);

        internal static bool AnyNapping => nappingClients.Count > 0;

        internal static void Initialize()
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null)
                    Attach(nm);
            }
            catch { }
        }

        internal static void Teardown()
        {
            Detach();
            nappingClients.Clear();
            NapSleepVfx.Teardown();
        }

        internal static void TickAttach()
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null)
                {
                    Detach();
                    return;
                }

                if (registeredManager != nm)
                    Attach(nm);
            }
            catch { }
        }

        internal static void ServerSetNap(ulong clientId, bool napping)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return;

            ApplyLocal(clientId, napping);
            Broadcast(clientId, napping);
        }

        private static void ApplyLocal(ulong clientId, bool napping)
        {
            if (napping)
                nappingClients.Add(clientId);
            else
                nappingClients.Remove(clientId);
        }

        private static void Attach(NetworkManager manager)
        {
            Detach();
            if (manager?.CustomMessagingManager == null)
                return;

            registeredManager = manager;
            registeredMessaging = manager.CustomMessagingManager;
            registeredMessaging.RegisterNamedMessageHandler(Channel, OnMessage);
            manager.OnClientConnectedCallback += OnClientConnected;
            manager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        private static void Detach()
        {
            try
            {
                if (registeredManager != null)
                {
                    registeredManager.OnClientConnectedCallback -= OnClientConnected;
                    registeredManager.OnClientDisconnectCallback -= OnClientDisconnected;
                }
            }
            catch { }

            try
            {
                registeredMessaging?.UnregisterNamedMessageHandler(Channel);
            }
            catch { }

            registeredManager = null;
            registeredMessaging = null;
        }

        private static void OnClientConnected(ulong clientId)
        {
            if (registeredManager == null || !registeredManager.IsServer)
                return;
            if (clientId == NetworkManager.ServerClientId)
                return;

            foreach (ulong napClientId in nappingClients)
                SendToClient(clientId, napClientId, true);
        }

        private static void OnClientDisconnected(ulong clientId)
        {
            if (!nappingClients.Remove(clientId))
                return;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
                Broadcast(clientId, false);
        }

        internal static void OnLocalDisconnect()
        {
            nappingClients.Clear();
            NapSleepVfx.Teardown();
        }

        private static void Broadcast(ulong subjectClientId, bool napping)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
                    return;

                using (FastBufferWriter writer = new FastBufferWriter(16, Allocator.Temp))
                {
                    writer.WriteValueSafe(subjectClientId);
                    writer.WriteValueSafe(napping);
                    nm.CustomMessagingManager.SendNamedMessageToAll(
                        Channel,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Nap sync broadcast failed: " + ex.Message);
            }
        }

        private static void SendToClient(ulong recipientClientId, ulong subjectClientId, bool napping)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm?.CustomMessagingManager == null)
                    return;

                using (FastBufferWriter writer = new FastBufferWriter(16, Allocator.Temp))
                {
                    writer.WriteValueSafe(subjectClientId);
                    writer.WriteValueSafe(napping);
                    nm.CustomMessagingManager.SendNamedMessage(
                        Channel,
                        recipientClientId,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }
            catch { }
        }

        private static void OnMessage(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || nm.IsServer)
                    return;
                if (senderClientId != NetworkManager.ServerClientId)
                    return;

                reader.ReadValueSafe(out ulong subjectClientId);
                reader.ReadValueSafe(out bool napping);
                ApplyLocal(subjectClientId, napping);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerNapService), nameof(PlayerNapService.ToggleNap))]
    internal static class NapSleepSync_ToggleNapPatch
    {
        private static void Postfix(ulong clientId)
        {
            NapSleepSync.ServerSetNap(clientId, PlayerNapService.IsVoluntary(clientId));
        }
    }

    [HarmonyPatch(typeof(PlayerNapService), nameof(PlayerNapService.StandUp))]
    internal static class NapSleepSync_StandUpPatch
    {
        private static void Postfix(ulong clientId)
        {
            NapSleepSync.ServerSetNap(clientId, false);
        }
    }

    [HarmonyPatch(typeof(PlayerNapService), nameof(PlayerNapService.ClearClient))]
    internal static class NapSleepSync_ClearClientPatch
    {
        private static void Postfix(ulong clientId)
        {
            NapSleepSync.ServerSetNap(clientId, false);
        }
    }
}
