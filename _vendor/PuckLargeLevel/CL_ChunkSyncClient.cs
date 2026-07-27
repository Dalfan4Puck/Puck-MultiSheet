using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Unity.Netcode;

public static class CL_ChunkSyncClient
{
    // Positions that jump more than this in one tick are held at the last good value
    private const float RejectThreshold = 16f;
    // After this many consecutive drops we let the position through to avoid permanent stalls
    private const int MaxDrops = 20;
    // [PHL] Drop-count alone scales with update cadence: resting objects only update on
    // the ~10 s keep-alive, so 20 drops would suppress a legit position for minutes.
    // Cap the suppression window in wall time as well.
    private const float MaxDropSeconds = 2f;

    private static Harmony _harmony;
    private static bool _enabled;
    private static readonly Dictionary<ushort, FilterState> _filter = new Dictionary<ushort, FilterState>();

    // [PHL] The join snapshot (unreliable RPC) can be processed before the chunk table
    // (reliable CMM). Positions that arrive for an unknown chunk are buffered raw
    // (chunk-local) here and applied the moment the chunk announcement lands, so
    // resting objects place correctly without any server-side resend.
    private static readonly Dictionary<ushort, Vector3> _pendingRaw = new Dictionary<ushort, Vector3>();
    private static readonly Dictionary<ushort, SynchronizedObject> _objects = new Dictionary<ushort, SynchronizedObject>();

    // Stable delegates so Enable/Disable remove the same listener instances.
    private static readonly Action<Dictionary<string, object>> OnSpawnedHandler = OnSpawned;
    private static readonly Action<Dictionary<string, object>> OnDespawnedHandler = OnDespawned;

    private struct FilterState
    {
        public Vector3 LastDecoded;
        public int ConsecutiveDrops;
        public bool Initialized;
        public float FirstDropRealtime;
    }

    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        _filter.Clear();
        _pendingRaw.Clear();
        _objects.Clear();

        _harmony = new Harmony("customlevel.chunksync.client");

        var rpcMethod = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_SynchronizeObjectsRpc");
        if (rpcMethod != null)
            _harmony.Patch(rpcMethod, new HarmonyMethod(typeof(CL_ChunkSyncClient), nameof(RpcPrefix)));

        var tick = AccessTools.Method(typeof(SynchronizedObject), "OnClientTick");
        if (tick != null)
            _harmony.Patch(tick, new HarmonyMethod(typeof(CL_ChunkSyncClient), nameof(OnClientTickPrefix)));

        var smoothTick = AccessTools.Method(typeof(SynchronizedObject), "OnClientSmoothTick");
        if (smoothTick != null)
            _harmony.Patch(smoothTick, new HarmonyMethod(typeof(CL_ChunkSyncClient), nameof(OnClientSmoothTickPrefix)));

        EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectSpawned", OnSpawnedHandler);
        EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedHandler);

        // Late-enable / already-spawned objects so ApplyPendingRaw can place them when
        // the chunk bulk snapshot lands (resting-puck join race).
        foreach (var so in UnityEngine.Object.FindObjectsByType<SynchronizedObject>(FindObjectsSortMode.None))
        {
            if (so == null || !so.IsSpawned) continue;
            _objects[(ushort)so.NetworkObjectId] = so;
        }

        PracticeLog.Info("[CustomLevel] ChunkSyncClient enabled.");
    }

    public static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        _filter.Clear();
        _pendingRaw.Clear();
        _objects.Clear();
        EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectSpawned", OnSpawnedHandler);
        EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedHandler);
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
    }

    private static void OnSpawned(Dictionary<string, object> msg)
    {
        if (msg["synchronizedObject"] is SynchronizedObject obj && obj != null)
            _objects[(ushort)obj.NetworkObjectId] = obj;
    }

    private static void OnDespawned(Dictionary<string, object> msg)
    {
        if (msg["synchronizedObject"] is SynchronizedObject obj)
        {
            ushort id = (ushort)obj.NetworkObjectId;
            _filter.Remove(id);
            _pendingRaw.Remove(id);
            _objects.Remove(id);
            CL_ChunkRegistry.Remove(id);
        }
    }

    public static void OnChunkMessage(ulong senderId, FastBufferReader reader)
    {
        if (NetworkManager.Singleton.IsServer) return;
        try
        {
            reader.ReadValueSafe(out byte type);
            if (type == 0)
            {
                ReadSingle(reader);
            }
            else if (type == 1)
            {
                // Bulk snapshot sent on late join; apply all entries in sequence
                reader.ReadValueSafe(out ushort count);
                for (int i = 0; i < count; i++) ReadSingle(reader);
                PracticeLog.Info($"[CustomLevel] ChunkSyncClient: applied bulk snapshot ({count} entries).");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CustomLevel] ChunkSyncClient.OnChunkMessage failed: " + e.Message);
        }
    }

    private static void ReadSingle(FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort id);
        reader.ReadValueSafe(out sbyte cx);
        reader.ReadValueSafe(out sbyte cz);
        reader.ReadValueSafe(out ushort switchTick);
        ChunkCoord coord = new ChunkCoord(cx, cz);
        CL_ChunkRegistry.ApplyAnnounce(id, coord, switchTick);

        // [PHL] Instant announces (join snapshot, teleports) invalidate whatever the
        // filter believed before: drop its state so the next decoded position is
        // accepted as-is. Never pre-seed LastDecoded with the chunk origin — that
        // made the glitch filter treat every real resting position >16 m from
        // center ice as a jump and hold objects at (cx*32, 0, cz*32) for up to
        // MaxDrops keep-alives (pucks stacked spinning at center ice for minutes
        // after a join). Deferred transition announces keep their filter state:
        // suppressing mis-decoded packets around a chunk switch is the filter's job.
        if (!ApplyPendingRaw(id, coord) && switchTick == ushort.MaxValue)
            _filter.Remove(id);
    }

    // A position received before this chunk was known is applied now; without this, a
    // resting object would keep its (wrong) local placement until it next moves.
    // Returns true when a buffered position was applied (filter is then seeded at it).
    private static bool ApplyPendingRaw(ushort id, ChunkCoord c)
    {
        if (!_pendingRaw.TryGetValue(id, out Vector3 raw)) return false;
        _pendingRaw.Remove(id);
        if (!_objects.TryGetValue(id, out SynchronizedObject obj) || obj == null)
            return false;

        Vector3 world = new Vector3(
            raw.x + c.X * CL_ChunkRegistry.ChunkSize,
            raw.y,
            raw.z + c.Z * CL_ChunkRegistry.ChunkSize);
        obj.transform.position = world;
        _filter[id] = new FilterState { LastDecoded = world, Initialized = true, ConsecutiveDrops = 0 };
        return true;
    }

    // Capture the tick id before any position decoding happens in the RPC so all objects in
    // the same batch resolve against the same snapshot of pending chunk transitions
    public static void RpcPrefix(ushort tickId)
    {
        CL_ChunkRegistry.CurrentDecodeTickId = tickId;
    }

    public static void OnClientTickPrefix(SynchronizedObject __instance, ref Vector3 position)
    {
        if (!_enabled || __instance == null) return;
        if (NetworkManager.Singleton?.IsServer == true) return;
        FilterAndReplace(__instance, ref position);
    }

    public static void OnClientSmoothTickPrefix(SynchronizedObject __instance, ref Vector3 position)
    {
        if (!_enabled || __instance == null) return;
        if (NetworkManager.Singleton?.IsServer == true) return;
        FilterAndReplace(__instance, ref position);
    }

    private static void FilterAndReplace(SynchronizedObject obj, ref Vector3 position)
    {
        ushort id = (ushort)obj.NetworkObjectId;
        _objects[id] = obj;

        // If chunks are active but we have no slot yet, hold the object in place until
        // the server's chunk announcement arrives; avoids applying an offset of zero.
        // Remember the raw (chunk-local) position so ApplyPendingRaw can place the
        // object correctly the moment the announcement lands.
        if (CL_ChunkRegistry.ChunksActive && !CL_ChunkRegistry.TryGet(id, out _))
        {
            _pendingRaw[id] = position;
            position = obj.transform.position;
            return;
        }

        // Vanilla decoded chunk-local shorts to a chunk-local float. Add the chunk origin here
        // to get the true world position. Doing this in the short layer (SyncPrefix) would
        // overflow the short again, so we expand at the float stage instead.
        if (CL_ChunkRegistry.TryGet(id, out ChunkSlot slot))
        {
            ChunkCoord c = slot.ResolveAt(CL_ChunkRegistry.CurrentDecodeTickId);
            position.x += c.X * CL_ChunkRegistry.ChunkSize;
            position.z += c.Z * CL_ChunkRegistry.ChunkSize;
        }

        _filter.TryGetValue(id, out FilterState fs);
        if (!fs.Initialized)
        {
            fs.LastDecoded = position; fs.Initialized = true; fs.ConsecutiveDrops = 0;
            _filter[id] = fs; return;
        }

        float dx = Mathf.Abs(position.x - fs.LastDecoded.x);
        float dz = Mathf.Abs(position.z - fs.LastDecoded.z);
        bool withinDropWindow = fs.ConsecutiveDrops == 0 ||
            (fs.ConsecutiveDrops < MaxDrops &&
             Time.realtimeSinceStartup - fs.FirstDropRealtime < MaxDropSeconds);
        if ((dx > RejectThreshold || dz > RejectThreshold) && withinDropWindow)
        {
            // Likely a chunk-offset glitch or a packet decoded against the wrong tick; suppress it
            position = fs.LastDecoded;
            if (fs.ConsecutiveDrops == 0) fs.FirstDropRealtime = Time.realtimeSinceStartup;
            fs.ConsecutiveDrops++;
        }
        else
        {
            fs.LastDecoded = position;
            fs.ConsecutiveDrops = 0;
        }
        _filter[id] = fs;
    }
}
