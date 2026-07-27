using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using Unity.Netcode;
using Unity.Collections;

public static class CL_ChunkSyncServer
{
    public const string CmmName = "CUSTOMLEVEL/Chunks";
    // How far past the chunk edge before triggering a reassignment (hysteresis band)
    private const float FlipOut = 20f;
    // Ticks to delay a chunk switch so the client receives the announcement before it takes effect
    private const int DeferTicks = 50;
    // Beyond this distance from the chunk center we snap immediately without deferring
    private const float TeleportSnap = 48f;
    // [PHL] Vanilla only sends objects that moved since the last tick, so a resting
    // object (e.g. an untouched puck on a cloned rink) never resends its position.
    // The join-order race is handled deterministically on the client (pending-position
    // buffer in CL_ChunkSyncClient); this keep-alive is only a safety net for a lost
    // join snapshot (it travels over unreliable UDP), so a rare interval suffices.
    // Cost: ~16 bytes per resting object once per interval, per client.
    private const int KeepAliveTicks = 1000; // ~10 s at the 100 Hz sync rate
    private static int _keepAliveCounter;
    private static bool _keepAliveNow;
    // Set for the gather that follows Server_ForceSynchronizeClientId so resting
    // pucks are included in the late-join payload (not only the next 10 s keep-alive).
    private static bool _forceSendAll;

    private static Harmony _harmony;
    private static bool _enabled;
    private static readonly List<SynchronizedObject> _tracked = new List<SynchronizedObject>();
    private static FieldInfo _tickIdField;

    // Actual world positions captured each GatherPrefix so GatherPostfix can encode without
    // touching the already-overflowed shorts that vanilla wrote
    internal static readonly Dictionary<ushort, Vector3> PositionCache = new Dictionary<ushort, Vector3>();

    // Stable delegates so Enable/Disable remove the same listener instances.
    private static readonly Action<Dictionary<string, object>> OnSpawnedHandler = OnSpawned;
    private static readonly Action<Dictionary<string, object>> OnDespawnedHandler = OnDespawned;

    public static void Enable()
    {
        if (_enabled) return;
        if (!NetworkManager.Singleton.IsServer)
        {
            PracticeLog.Info("[CustomLevel] ChunkSyncServer: not server, skipping.");
            return;
        }

        _enabled = true;
        _tickIdField = AccessTools.Field(typeof(SynchronizedObjectManager), "serverLastSentTickId");

        // Seed objects that were already spawned before Enable() was called
        SynchronizedObjectManager mgr = NetworkBehaviourSingleton<SynchronizedObjectManager>.Instance;
        if (mgr != null)
        {
            FieldInfo f = AccessTools.Field(typeof(SynchronizedObjectManager), "synchronizedObjects");
            if (f?.GetValue(mgr) is IEnumerable<SynchronizedObject> existing)
                foreach (var obj in existing)
                    if (obj != null && !_tracked.Contains(obj))
                    { _tracked.Add(obj); InitSlot(obj); }
        }

        EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectSpawned", OnSpawnedHandler);
        EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedHandler);

        _harmony = new Harmony("customlevel.chunksync.server");
        var gather = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_GatherSynchronizedObjectData");
        if (gather != null)
            _harmony.Patch(gather, new HarmonyMethod(typeof(CL_ChunkSyncServer),
                nameof(GatherPrefix)));

        var force = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_ForceSynchronizeClientId");
        if (force != null)
            _harmony.Patch(force, new HarmonyMethod(typeof(CL_ChunkSyncServer),
                nameof(ForceSyncPrefix)));

        var shouldSend = AccessTools.Method(typeof(SynchronizedObject), "ShouldSendPosition");
        if (shouldSend != null)
            _harmony.Patch(shouldSend, postfix: new HarmonyMethod(typeof(CL_ChunkSyncServer),
                nameof(ShouldSendPositionPostfix)));

        PracticeLog.Info($"[CustomLevel] ChunkSyncServer enabled ({_tracked.Count} objects).");
    }

    public static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        _tracked.Clear();
        PositionCache.Clear();
        EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectSpawned", OnSpawnedHandler);
        EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedHandler);
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
    }

    private static void OnSpawned(Dictionary<string, object> msg)
    {
        if (!_enabled) return;
        if (msg["synchronizedObject"] is SynchronizedObject obj && !_tracked.Contains(obj))
        { _tracked.Add(obj); InitSlot(obj); }
    }

    private static void OnDespawned(Dictionary<string, object> msg)
    {
        if (!_enabled) return;
        if (msg["synchronizedObject"] is SynchronizedObject obj)
        {
            ushort id = (ushort)obj.NetworkObjectId;
            _tracked.Remove(obj);
            CL_ChunkRegistry.Remove(id);
            // GatherPrefix writes every tick; without this, puck/player churn grows forever.
            PositionCache.Remove(id);
        }
    }

    internal static void InitSlot(SynchronizedObject obj)
    {
        if (!_enabled || obj == null) return;
        ushort id = (ushort)obj.NetworkObjectId;
        ChunkCoord c = WorldToChunk(obj.transform.position);
        // Immediate announce (ushort.MaxValue) so the client knows the chunk before any position data arrives
        CL_ChunkRegistry.ApplyAnnounce(id, c, ushort.MaxValue);
        BroadcastInstant(id, c);
    }

    // Only meaningful during the gather that follows GatherPrefix; forces resting
    // objects into the payload on keep-alive ticks and late-join force-sync.
    public static void ShouldSendPositionPostfix(ref bool __result)
    {
        if (_keepAliveNow || _forceSendAll) __result = true;
    }

    /// <summary>Clear the one-shot late-join force-send after the gather finishes.</summary>
    public static void EndForceSendAll()
    {
        _forceSendAll = false;
        _keepAliveNow = false;
    }

    // Prefix runs before the vanilla gather so CurrentEncodeTickId is set for all encode calls in that frame
    public static void GatherPrefix()
    {
        if (!_enabled) return;
        bool force = _forceSendAll;
        _keepAliveNow = force || (++_keepAliveCounter >= KeepAliveTicks);
        if (_keepAliveNow && !force) _keepAliveCounter = 0;
        ushort tick = GetTickId();
        CL_ChunkRegistry.CurrentEncodeTickId = tick;

        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            SynchronizedObject obj = _tracked[i];
            if (obj == null) { _tracked.RemoveAt(i); continue; }

            ushort id = (ushort)obj.NetworkObjectId;
            Vector3 pos = obj.transform.position;
            PositionCache[id] = pos;

            if (!CL_ChunkRegistry.TryGet(id, out ChunkSlot slot))
            { InitSlot(obj); continue; }

            ChunkCoord cur = slot.ResolveAt(tick);
            float dx = pos.x - cur.X * CL_ChunkRegistry.ChunkSize;
            float dz = pos.z - cur.Z * CL_ChunkRegistry.ChunkSize;

            // Teleport: skip deferred transition and snap to the new chunk immediately
            if (Mathf.Abs(dx) > TeleportSnap || Mathf.Abs(dz) > TeleportSnap)
            {
                ChunkCoord nc = WorldToChunk(pos);
                if (nc != cur) { CL_ChunkRegistry.ApplyAnnounce(id, nc, ushort.MaxValue); BroadcastInstant(id, nc); }
                continue;
            }

            ChunkCoord hyst = HysteresisCheck(pos, cur);
            if (hyst == cur) continue;

            // Don't re-broadcast a pending transition that's already in flight for the same target
            if (slot.HasPending && slot.Pending == hyst && !TickGE(tick, slot.PendingTickId)) continue;

            ushort switchTick = (ushort)((tick + DeferTicks) % ushort.MaxValue);
            CL_ChunkRegistry.ApplyAnnounce(id, hyst, switchTick);
            BroadcastSwitch(id, hyst, switchTick);
        }
    }

    // Called when a late-joining client needs a full state snapshot
    public static void ForceSyncPrefix(ulong clientId)
    {
        if (!_enabled || CL_ChunkRegistry.Count == 0) return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        // Host's local client receives state through the in-process path, not CMM
        if (nm.IsHost && clientId == nm.LocalClientId) return;

        // Include every resting puck/player in the force-sync gather that follows.
        _forceSendAll = true;

        try
        {
            ushort tick = GetTickId();
            ushort entryCount = 0;
            // Objects with an active pending transition need two entries: current + pending
            foreach (var kvp in CL_ChunkRegistry.Snapshot())
                entryCount += (ushort)(kvp.Value.HasPending && !TickGE(tick, kvp.Value.PendingTickId) ? 2 : 1);

            int cap = 3 + entryCount * 12 + 64;
            using (var writer = new FastBufferWriter(cap, Allocator.Temp, cap * 4))
            {
                byte type = 1;
                writer.WriteValueSafe(type);
                writer.WriteValueSafe(entryCount);

                foreach (var kvp in CL_ChunkRegistry.Snapshot())
                {
                    ChunkSlot s = kvp.Value;
                    bool hasPending = s.HasPending && !TickGE(tick, s.PendingTickId);
                    ushort key = kvp.Key;

                    if (hasPending)
                    {
                        // Send current first so the client can apply it, then the pending switch on top
                        writer.WriteValueSafe(key);
                        writer.WriteValueSafe(s.Current.X);
                        writer.WriteValueSafe(s.Current.Z);
                        ushort noSwitch = ushort.MaxValue;
                        writer.WriteValueSafe(noSwitch);

                        writer.WriteValueSafe(key);
                        writer.WriteValueSafe(s.Pending.X);
                        writer.WriteValueSafe(s.Pending.Z);
                        writer.WriteValueSafe(s.PendingTickId);
                    }
                    else
                    {
                        ChunkCoord c = s.ResolveAt(tick);
                        writer.WriteValueSafe(key);
                        writer.WriteValueSafe(c.X);
                        writer.WriteValueSafe(c.Z);
                        ushort noSwitch = ushort.MaxValue;
                        writer.WriteValueSafe(noSwitch);
                    }
                }
                nm.CustomMessagingManager.SendNamedMessage(CmmName, clientId, writer, NetworkDelivery.Reliable);
            }
            PracticeLog.Info($"[CustomLevel] ChunkSyncServer: bulk snapshot sent to client {clientId}.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CustomLevel] ChunkSyncServer.ForceSyncPrefix failed: " + e.Message);
        }
    }

    private static void BroadcastInstant(ushort id, ChunkCoord c) => BroadcastSingle(id, c, ushort.MaxValue);
    private static void BroadcastSwitch(ushort id, ChunkCoord c, ushort tick) => BroadcastSingle(id, c, tick);

    private static void BroadcastSingle(ushort id, ChunkCoord c, ushort switchTick)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        // Host client receives state through the in-process path; don't count it
        int clients = nm.IsHost ? nm.ConnectedClientsIds.Count - 1 : nm.ConnectedClientsIds.Count;
        if (clients <= 0) return;

        try
        {
            using (var writer = new FastBufferWriter(8, Allocator.Temp, 32))
            {
                byte type = 0;
                writer.WriteValueSafe(type);
                writer.WriteValueSafe(id);
                writer.WriteValueSafe(c.X);
                writer.WriteValueSafe(c.Z);
                writer.WriteValueSafe(switchTick);
                nm.CustomMessagingManager.SendNamedMessageToAll(CmmName, writer, NetworkDelivery.Reliable);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CustomLevel] ChunkSyncServer.BroadcastSingle failed: " + e.Message);
        }
    }

    // [PHL] Rink-zone override: each rink is pinned to a single dedicated chunk so no
    // chunk transitions ever happen during play on a rink. A rink is ~91 m long
    // (boards at local z=±45.75) and the position shorts encode ±50.02 m
    // (32767 / 655), so a whole rink fits one chunk's window — provided the rink's
    // origin sits exactly on the 32 m chunk grid (hence the 128 m rink spacing).
    // Rink 1's zone is chunk (0,0), which keeps its wire data byte-identical to
    // vanilla so UNMODDED clients see rink 1 correctly. Other rinks' zones are only
    // decodable by modded clients. Outside all zones the plain 32 m grid applies.
    private struct RinkZone
    {
        public Vector3 Origin;
        public ChunkCoord Chunk;
    }

    private static readonly List<RinkZone> _zones = new List<RinkZone>
    {
        // Default: protect the vanilla rink even if ConfigureRinkZones is never called.
        new RinkZone { Origin = Vector3.zero, Chunk = new ChunkCoord(0, 0) }
    };

    // Zone envelopes are rectangular to match the rink footprint (~45 m wide on X,
    // ~91 m long on Z). The tighter X extent lets rinks sit 64 m apart laterally
    // (2 chunk cells) without neighbouring envelopes overlapping: on-ice positions
    // reach |dx| ≈ 23 m, envelopes span ±30 m, and the next origin starts at 64 m.
    private const float ZoneEnterX = 30f;  // inside this of a rink origin → that rink's chunk
    private const float ZoneExitX = 31.5f; // rink chunk sticks until beyond this
    private const float ZoneEnterZ = 46f;
    private const float ZoneExitZ = 47.5f;

    /// <summary>
    /// Pin each rink origin to its own chunk. Origins must lie on the chunk grid
    /// (multiples of ChunkSize); misaligned origins are skipped with a warning and
    /// fall back to plain grid chunking.
    /// </summary>
    public static void ConfigureRinkZones(IEnumerable<Vector3> origins)
    {
        _zones.Clear();
        foreach (Vector3 o in origins)
        {
            float fx = o.x / CL_ChunkRegistry.ChunkSize;
            float fz = o.z / CL_ChunkRegistry.ChunkSize;
            int ix = Mathf.RoundToInt(fx), iz = Mathf.RoundToInt(fz);
            if (Mathf.Abs(fx - ix) > 0.001f || Mathf.Abs(fz - iz) > 0.001f)
            {
                Debug.LogWarning($"[CustomLevel] Rink origin {o} is not on the {CL_ChunkRegistry.ChunkSize} m chunk grid; it will use plain grid chunks.");
                continue;
            }
            _zones.Add(new RinkZone
            {
                Origin = o,
                Chunk = new ChunkCoord((sbyte)Mathf.Clamp(ix, -128, 127), (sbyte)Mathf.Clamp(iz, -128, 127))
            });
        }
        PracticeLog.Info($"[CustomLevel] ChunkSyncServer: {_zones.Count} rink zone(s) pinned to dedicated chunks.");
    }

    private static bool TryGetZoneChunk(Vector3 pos, float halfX, float halfZ, out ChunkCoord chunk)
    {
        for (int i = 0; i < _zones.Count; i++)
        {
            Vector3 o = _zones[i].Origin;
            if (Mathf.Abs(pos.x - o.x) < halfX && Mathf.Abs(pos.z - o.z) < halfZ)
            {
                chunk = _zones[i].Chunk;
                return true;
            }
        }
        chunk = default;
        return false;
    }

    private static ChunkCoord WorldToChunk(Vector3 pos)
    {
        if (TryGetZoneChunk(pos, ZoneEnterX, ZoneEnterZ, out ChunkCoord zc)) return zc;
        int x = Mathf.Clamp(Mathf.RoundToInt(pos.x / CL_ChunkRegistry.ChunkSize), -128, 127);
        int z = Mathf.Clamp(Mathf.RoundToInt(pos.z / CL_ChunkRegistry.ChunkSize), -128, 127);
        return new ChunkCoord((sbyte)x, (sbyte)z);
    }

    // Returns the current chunk unless the object has crossed FlipOut past the edge, preventing
    // rapid back-and-forth reassignments when an object sits near a chunk boundary
    private static ChunkCoord HysteresisCheck(Vector3 pos, ChunkCoord cur)
    {
        // [PHL] Rink-zone rules take precedence over the 32 m grid stepping.
        // If the object is currently on a rink's chunk, it stays there until it
        // leaves that rink's envelope. Fast leavers (>48 m from the chunk center)
        // are handled by the TeleportSnap path, which switches instantly instead
        // of deferring, so the shorts never overflow.
        for (int i = 0; i < _zones.Count; i++)
        {
            if (cur != _zones[i].Chunk) continue;
            Vector3 o = _zones[i].Origin;
            if (Mathf.Abs(pos.x - o.x) < ZoneExitX && Mathf.Abs(pos.z - o.z) < ZoneExitZ) return cur;
            return WorldToChunk(pos);
        }
        if (TryGetZoneChunk(pos, ZoneEnterX, ZoneEnterZ, out ChunkCoord zc)) return zc;

        float ox = cur.X * CL_ChunkRegistry.ChunkSize;
        float oz = cur.Z * CL_ChunkRegistry.ChunkSize;
        int nx = cur.X, nz = cur.Z;
        float dx = pos.x - ox, dz = pos.z - oz;
        if (dx > FlipOut) nx = Mathf.Clamp(nx + 1, -128, 127);
        else if (dx < -FlipOut) nx = Mathf.Clamp(nx - 1, -128, 127);
        if (dz > FlipOut) nz = Mathf.Clamp(nz + 1, -128, 127);
        else if (dz < -FlipOut) nz = Mathf.Clamp(nz - 1, -128, 127);
        return new ChunkCoord((sbyte)nx, (sbyte)nz);
    }

    private static ushort GetTickId()
    {
        SynchronizedObjectManager mgr = NetworkBehaviourSingleton<SynchronizedObjectManager>.Instance;
        if (mgr == null || _tickIdField == null) return 0;
        try { return (ushort)_tickIdField.GetValue(mgr); } catch { return 0; }
    }

    // Unsigned comparison that handles ushort wrap-around (correct even when a > b crosses 65535→0)
    private static bool TickGE(ushort a, ushort b) => (ushort)(a - b) < 32768;
}
