using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server → clients: which practice puck goalies should track-look at on each rink.
    /// Carries a primary look target plus the next queued holder so clients can bypass
    /// a puck that would force the goalie to look behind them.
    /// </summary>
    internal static class GoaliePracticeLookTarget
    {
        private const string Channel = "multisheet-goalie-look-v2";
        private const string ResyncChannel = "multisheet-goalie-look-req";

        private static readonly Dictionary<int, ulong> clientLookObjectIdByRink = new Dictionary<int, ulong>();
        private static readonly Dictionary<int, ulong> clientQueuedObjectIdByRink = new Dictionary<int, ulong>();
        private static readonly Dictionary<int, ulong> lastBroadcastLookByRink = new Dictionary<int, ulong>();
        private static readonly Dictionary<int, ulong> lastBroadcastQueuedByRink = new Dictionary<int, ulong>();
        private static CustomMessagingManager registeredMessaging;
        private static NetworkManager registeredManager;

        /// <summary>Remote clients call after MOTD / scene ready so they catch look targets missed at connect.</summary>
        internal static void RequestClientResync()
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsConnectedClient || nm.IsServer)
                    return;
                if (nm.CustomMessagingManager == null)
                    return;

                using (FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp))
                {
                    writer.WriteValueSafe((byte)1);
                    nm.CustomMessagingManager.SendNamedMessage(
                        ResyncChannel,
                        NetworkManager.ServerClientId,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }
            catch { }
        }

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
            clientLookObjectIdByRink.Clear();
            clientQueuedObjectIdByRink.Clear();
            lastBroadcastLookByRink.Clear();
            lastBroadcastQueuedByRink.Clear();
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

        /// <summary>Server: publish preferred look + queued fallback (null clears).</summary>
        internal static void Publish(int rinkIndex, Puck lookPuck, Puck queuedPuck)
        {
            ulong lookId = ResolveObjectId(lookPuck);
            ulong queuedId = ResolveObjectId(queuedPuck);

            // Primary not replicated yet — still publish queued holder so clients can look ahead.
            if (lookPuck != null && lookId == 0)
            {
                if (queuedPuck == null || queuedId == 0)
                    return;
                lookId = 0;
            }
            else if (queuedPuck != null && queuedId == 0)
            {
                queuedId = 0;
            }

            bool lookSame = lastBroadcastLookByRink.TryGetValue(rinkIndex, out ulong prevLook)
                && prevLook == lookId;
            bool queuedSame = lastBroadcastQueuedByRink.TryGetValue(rinkIndex, out ulong prevQueued)
                && prevQueued == queuedId;
            if (lookSame && queuedSame)
                return;

            lastBroadcastLookByRink[rinkIndex] = lookId;
            lastBroadcastQueuedByRink[rinkIndex] = queuedId;
            clientLookObjectIdByRink[rinkIndex] = lookId;
            clientQueuedObjectIdByRink[rinkIndex] = queuedId;
            Broadcast(rinkIndex, lookId, queuedId);
        }

        internal static void ClearRink(int rinkIndex)
        {
            Publish(rinkIndex, null, null);
            lastBroadcastLookByRink.Remove(rinkIndex);
            lastBroadcastQueuedByRink.Remove(rinkIndex);
            clientLookObjectIdByRink.Remove(rinkIndex);
            clientQueuedObjectIdByRink.Remove(rinkIndex);
        }

        internal static bool TryGetLookPuck(int rinkIndex, out Puck puck)
        {
            puck = null;
            if (rinkIndex < 0)
                return false;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                puck = RinkPracticeDrills.ResolveLookPuck(rinkIndex);
                if (puck != null)
                    return true;
            }

            return TryResolveCachedPuck(clientLookObjectIdByRink, rinkIndex, out puck);
        }

        internal static bool TryGetQueuedLookPuck(int rinkIndex, out Puck puck)
        {
            puck = null;
            if (rinkIndex < 0)
                return false;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                puck = RinkPracticeDrills.ResolveQueuedLookPuck(rinkIndex);
                if (puck != null)
                    return true;
            }

            return TryResolveCachedPuck(clientQueuedObjectIdByRink, rinkIndex, out puck);
        }

        private static bool TryResolveCachedPuck(
            Dictionary<int, ulong> cache,
            int rinkIndex,
            out Puck puck)
        {
            puck = null;
            if (!cache.TryGetValue(rinkIndex, out ulong objectId) || objectId == 0)
                return false;

            PuckManager pm = PuckManager.Instance;
            if (pm == null)
                return false;

            try
            {
                puck = pm.GetPuckByNetworkObjectId(objectId);
                if (puck != null && puck.gameObject != null)
                    return true;
            }
            catch { }

            // Fallback when the lookup table lags behind replication (seen on some joining clients).
            try
            {
                var pucks = pm.GetPucks(false);
                if (pucks != null)
                {
                    for (int i = 0; i < pucks.Count; i++)
                    {
                        Puck candidate = pucks[i];
                        if (candidate?.NetworkObject == null || !candidate.NetworkObject.IsSpawned)
                            continue;
                        if (candidate.NetworkObject.NetworkObjectId != objectId)
                            continue;
                        puck = candidate;
                        return puck.gameObject != null;
                    }
                }
            }
            catch { }

            puck = null;
            return false;
        }

        private static ulong ResolveObjectId(Puck puck)
        {
            try
            {
                if (puck != null && puck.NetworkObject != null && puck.NetworkObject.IsSpawned)
                    return puck.NetworkObjectId;
            }
            catch { }

            return 0;
        }

        private static void Attach(NetworkManager manager)
        {
            Detach();
            if (manager?.CustomMessagingManager == null)
                return;

            registeredManager = manager;
            registeredMessaging = manager.CustomMessagingManager;
            registeredMessaging.RegisterNamedMessageHandler(Channel, OnMessage);
            registeredMessaging.RegisterNamedMessageHandler(ResyncChannel, OnResyncRequest);
            manager.OnClientConnectedCallback += OnClientConnected;

            if (manager.IsConnectedClient && !manager.IsServer)
                RequestClientResync();
        }

        private static void Detach()
        {
            try
            {
                if (registeredManager != null)
                    registeredManager.OnClientConnectedCallback -= OnClientConnected;
            }
            catch { }

            try
            {
                registeredMessaging?.UnregisterNamedMessageHandler(Channel);
                registeredMessaging?.UnregisterNamedMessageHandler(ResyncChannel);
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

            SendSnapshotToClient(clientId);
        }

        private static void OnResyncRequest(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer)
                    return;
                if (senderClientId == NetworkManager.ServerClientId)
                    return;

                SendSnapshotToClient(senderClientId);
            }
            catch { }
        }

        private static void SendSnapshotToClient(ulong clientId)
        {
            foreach (KeyValuePair<int, ulong> pair in lastBroadcastLookByRink)
            {
                ulong queuedId = 0;
                lastBroadcastQueuedByRink.TryGetValue(pair.Key, out queuedId);
                if (pair.Value == 0 && queuedId == 0)
                    continue;
                SendToClient(clientId, pair.Key, pair.Value, queuedId);
            }
        }

        private static void Broadcast(int rinkIndex, ulong lookId, ulong queuedId)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
                    return;

                using (FastBufferWriter writer = new FastBufferWriter(24, Allocator.Temp))
                {
                    writer.WriteValueSafe(rinkIndex);
                    writer.WriteValueSafe(lookId);
                    writer.WriteValueSafe(queuedId);
                    nm.CustomMessagingManager.SendNamedMessageToAll(
                        Channel,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Goalie look broadcast failed: " + ex.Message);
            }
        }

        private static void SendToClient(ulong clientId, int rinkIndex, ulong lookId, ulong queuedId)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm?.CustomMessagingManager == null)
                    return;

                using (FastBufferWriter writer = new FastBufferWriter(24, Allocator.Temp))
                {
                    writer.WriteValueSafe(rinkIndex);
                    writer.WriteValueSafe(lookId);
                    writer.WriteValueSafe(queuedId);
                    nm.CustomMessagingManager.SendNamedMessage(
                        Channel,
                        clientId,
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

                reader.ReadValueSafe(out int rinkIndex);
                reader.ReadValueSafe(out ulong lookId);
                reader.ReadValueSafe(out ulong queuedId);
                if (rinkIndex < 0)
                    return;

                if (lookId == 0)
                    clientLookObjectIdByRink.Remove(rinkIndex);
                else
                    clientLookObjectIdByRink[rinkIndex] = lookId;

                if (queuedId == 0)
                    clientQueuedObjectIdByRink.Remove(rinkIndex);
                else
                    clientQueuedObjectIdByRink[rinkIndex] = queuedId;
            }
            catch { }
        }
    }
}
