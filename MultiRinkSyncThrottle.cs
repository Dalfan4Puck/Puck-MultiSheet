using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server-side interest throttle. Rinks are hard-isolated (players and pucks never
    /// cross), so a client only needs full-rate position sync for the sheet they are
    /// standing on. This patch replaces SynchronizedObjectManager.Server_ServerTick:
    ///
    ///   - Objects on the client's own rink stream at the full sync tick rate,
    ///     exactly like vanilla.
    ///   - Objects on every other rink are accumulated (latest position per object)
    ///     and flushed to that client at OffRinkSyncHz.
    ///
    /// Clients are grouped by which rink their body is on (nearest rink origin), so
    /// the server sends one RPC per occupied rink per tick instead of one broadcast —
    /// same packet cadence per client as vanilla, just smaller payloads. Clients with
    /// no spawned body (sitting in the MOTD) get everything at the throttled rate,
    /// which is plenty for the hover previews.
    ///
    /// Correctness notes:
    ///   - The accumulator keeps the LAST gathered entry per object, so an off-rink
    ///     object that moves and stops still delivers its resting position on the
    ///     next flush. The existing 10 s chunk-sync keep-alive remains the backstop
    ///     for lost packets.
    ///   - Objects whose rink can't be resolved (no PositionCache entry yet) are sent
    ///     to everyone at full rate — never silently dropped.
    ///   - Any error disables the throttle and falls back to the vanilla tick.
    /// </summary>
    internal static class MultiRinkSyncThrottle
    {
        private static Harmony harmony;
        private static bool enabled;
        private static bool warnedFallback;

        private static MethodInfo gatherMethod;
        private static MethodInfo rpcMethod;
        private static FieldInfo tickIdField;
        private static FieldInfo lastSentTimeField;
        private static FieldInfo clientIdsField;

        private static Vector3[] rinkOrigins = new Vector3[0];
        private static int offRinkHz = 10;
        private static int flushCounter;

        // Latest entry per object since the last flush, tagged with its rink.
        private static readonly Dictionary<ushort, PendingEntry> pending = new Dictionary<ushort, PendingEntry>();

        // Scratch buffers reused every tick.
        private static readonly List<SynchronizedObjectData> globalCurrent = new List<SynchronizedObjectData>();
        private static readonly Dictionary<int, List<SynchronizedObjectData>> currentByRink = new Dictionary<int, List<SynchronizedObjectData>>();
        private static readonly Dictionary<int, List<ulong>> membersByRink = new Dictionary<int, List<ulong>>();
        private static readonly List<SynchronizedObjectData> payloadScratch = new List<SynchronizedObjectData>();
        private static readonly object[] gatherArgs = { false };
        private static readonly object[] rpcArgs = new object[4];
        private static readonly SynchronizedObjectData[] emptyPayload = new SynchronizedObjectData[0];

        // Persistent per-rink RPC targets, rebuilt only when membership changes.
        private static readonly Dictionary<int, CachedGroup> groups = new Dictionary<int, CachedGroup>();

        private struct PendingEntry
        {
            public int Rink;
            public SynchronizedObjectData Data;
        }

        private sealed class CachedGroup
        {
            public readonly List<ulong> Members = new List<ulong>();
            public BaseRpcTarget Target;
        }

        internal static void Enable(MultiRinkConfig config)
        {
            if (enabled) return;
            if (config == null || config.Rinks == null || config.Rinks.Count < 2) return;
            if (config.OffRinkSyncHz <= 0)
            {
                PracticeLog.Info("[PHLPractice] SyncThrottle disabled by config (OffRinkSyncHz=0).");
                return;
            }
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            gatherMethod = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_GatherSynchronizedObjectData");
            rpcMethod = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_SynchronizeObjectsRpc");
            tickIdField = AccessTools.Field(typeof(SynchronizedObjectManager), "serverLastSentTickId");
            lastSentTimeField = AccessTools.Field(typeof(SynchronizedObjectManager), "serverLastSentServerTime");
            clientIdsField = AccessTools.Field(typeof(SynchronizedObjectManager), "synchronizedClientIds");
            if (gatherMethod == null || rpcMethod == null || tickIdField == null ||
                lastSentTimeField == null || clientIdsField == null)
            {
                Debug.LogWarning("[PHLPractice] SyncThrottle: SynchronizedObjectManager internals not found — staying on vanilla sync.");
                return;
            }

            var origins = new List<Vector3>();
            foreach (RinkSlot slot in config.Rinks)
                if (slot != null) origins.Add(slot.Origin);
            rinkOrigins = origins.ToArray();
            offRinkHz = config.OffRinkSyncHz;
            flushCounter = 0;
            warnedFallback = false;

            harmony = new Harmony("PHL.PHLPracticeModPack.SyncThrottle");
            harmony.Patch(
                AccessTools.Method(typeof(SynchronizedObjectManager), "Server_ServerTick"),
                new HarmonyMethod(typeof(MultiRinkSyncThrottle), nameof(ServerTickPrefix)));

            enabled = true;
            PracticeLog.Info("[PHLPractice] SyncThrottle enabled: own rink at full tick, other rinks at " +
                      offRinkHz + " Hz (" + rinkOrigins.Length + " rinks).");
        }

        internal static void Disable()
        {
            if (!enabled && harmony == null) return;
            enabled = false;
            try { harmony?.UnpatchSelf(); } catch { }
            harmony = null;
            pending.Clear();
            foreach (CachedGroup group in groups.Values)
            {
                try { group.Target?.Dispose(); } catch { }
            }
            groups.Clear();
            currentByRink.Clear();
            membersByRink.Clear();
            globalCurrent.Clear();
        }

        private static bool ServerTickPrefix(SynchronizedObjectManager __instance)
        {
            if (!enabled) return true;
            try
            {
                RunTick(__instance);
                return false;
            }
            catch (Exception ex)
            {
                if (!warnedFallback)
                {
                    warnedFallback = true;
                    Debug.LogWarning("[PHLPractice] SyncThrottle tick failed, reverting to vanilla sync: " + ex);
                }
                Disable();
                return true;
            }
        }

        private static void RunTick(SynchronizedObjectManager mgr)
        {
            // Mirror the vanilla tick: bump the id, gather movers (the existing chunk
            // patches run inside this call), then stamp the send time.
            ushort tickId = (ushort)tickIdField.GetValue(mgr);
            tickId++;
            if (tickId >= ushort.MaxValue) tickId = 0;
            tickIdField.SetValue(mgr, tickId);

            var gathered = (SynchronizedObjectData[])gatherMethod.Invoke(mgr, gatherArgs);
            double serverTime = NetworkManager.Singleton.ServerTime.Time;
            lastSentTimeField.SetValue(mgr, serverTime);

            int tickRate = Mathf.Max(1, mgr.TickRate);
            int flushInterval = Mathf.Max(1, Mathf.RoundToInt(tickRate / (float)offRinkHz));
            bool flush = ++flushCounter >= flushInterval;
            if (flush) flushCounter = 0;

            // Partition this tick's movers by rink.
            globalCurrent.Clear();
            foreach (List<SynchronizedObjectData> list in currentByRink.Values) list.Clear();
            if (gathered != null)
            {
                for (int i = 0; i < gathered.Length; i++)
                {
                    ushort id = gathered[i].NetworkObjectId;
                    int rink = RinkOfObject(id);
                    if (rink < 0)
                    {
                        globalCurrent.Add(gathered[i]);
                        continue;
                    }
                    if (!currentByRink.TryGetValue(rink, out List<SynchronizedObjectData> list))
                    {
                        list = new List<SynchronizedObjectData>();
                        currentByRink.Add(rink, list);
                    }
                    list.Add(gathered[i]);
                    pending[id] = new PendingEntry { Rink = rink, Data = gathered[i] };
                }
            }

            RebuildGroups(mgr);

            foreach (KeyValuePair<int, CachedGroup> kvp in groups)
            {
                CachedGroup group = kvp.Value;
                if (group.Target == null || group.Members.Count == 0) continue;
                int rink = kvp.Key;

                payloadScratch.Clear();
                payloadScratch.AddRange(globalCurrent);
                if (rink >= 0 && currentByRink.TryGetValue(rink, out List<SynchronizedObjectData> own))
                    payloadScratch.AddRange(own);
                if (flush)
                {
                    foreach (PendingEntry entry in pending.Values)
                        if (entry.Rink != rink) payloadScratch.Add(entry.Data);
                }

                SynchronizedObjectData[] payload = payloadScratch.Count > 0
                    ? payloadScratch.ToArray()
                    : emptyPayload;

                rpcArgs[0] = tickId;
                rpcArgs[1] = serverTime;
                rpcArgs[2] = payload;
                rpcArgs[3] = new RpcParams { Send = new RpcSendParams { Target = group.Target } };
                rpcMethod.Invoke(mgr, rpcArgs);
            }

            if (flush) pending.Clear();
        }

        /// <summary>
        /// Group synchronized clients by the rink their body stands on (-1 = no body,
        /// e.g. still in the MOTD). Persistent RPC targets are only rebuilt when a
        /// group's membership actually changes.
        /// </summary>
        private static void RebuildGroups(SynchronizedObjectManager mgr)
        {
            foreach (List<ulong> list in membersByRink.Values) list.Clear();

            var clientIds = clientIdsField.GetValue(mgr) as List<ulong>;
            if (clientIds != null)
            {
                for (int i = 0; i < clientIds.Count; i++)
                {
                    int rink = RinkOfClient(clientIds[i]);
                    if (!membersByRink.TryGetValue(rink, out List<ulong> list))
                    {
                        list = new List<ulong>();
                        membersByRink.Add(rink, list);
                    }
                    list.Add(clientIds[i]);
                }
            }

            foreach (KeyValuePair<int, List<ulong>> kvp in membersByRink)
            {
                if (!groups.TryGetValue(kvp.Key, out CachedGroup group))
                {
                    group = new CachedGroup();
                    groups.Add(kvp.Key, group);
                }
                if (SameMembers(group.Members, kvp.Value)) continue;

                try { group.Target?.Dispose(); } catch { }
                group.Target = null;
                group.Members.Clear();
                group.Members.AddRange(kvp.Value);
                if (group.Members.Count > 0)
                    group.Target = NetworkManager.Singleton.RpcTarget.Group(group.Members, RpcTargetUse.Persistent);
            }

            // Drop groups whose rink emptied out entirely.
            foreach (KeyValuePair<int, CachedGroup> kvp in groups)
            {
                if (membersByRink.TryGetValue(kvp.Key, out List<ulong> members) && members.Count > 0) continue;
                if (kvp.Value.Members.Count == 0) continue;
                try { kvp.Value.Target?.Dispose(); } catch { }
                kvp.Value.Target = null;
                kvp.Value.Members.Clear();
            }
        }

        private static bool SameMembers(List<ulong> a, List<ulong> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static int RinkOfObject(ushort networkObjectId)
        {
            // GatherPrefix (chunk sync) caches true world positions every tick.
            if (!CL_ChunkSyncServer.PositionCache.TryGetValue(networkObjectId, out Vector3 pos))
                return -1;
            return NearestRink(pos);
        }

        private static int RinkOfClient(ulong clientId)
        {
            try
            {
                var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                Player player = pm != null ? pm.GetPlayerByClientId(clientId) : null;
                PlayerBody body = player != null ? player.PlayerBody : null;
                if (body == null) return -1;
                return NearestRink(body.transform.position);
            }
            catch
            {
                return -1;
            }
        }

        private static int NearestRink(Vector3 position)
        {
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < rinkOrigins.Length; i++)
            {
                float dx = rinkOrigins[i].x - position.x;
                float dz = rinkOrigins[i].z - position.z;
                float distance = dx * dx + dz * dz;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }
    }
}
