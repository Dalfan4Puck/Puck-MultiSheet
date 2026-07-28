using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Keeps hive movers aligned across server hitboxes and client visuals.
/// Rotators / decoy movers use synchronized <see cref="NetworkManager.ServerTime"/> locally
/// (no pose lag). Circular targets still receive authoritative pose snapshots.
/// </summary>
public static class TrainingMotionSync
{
    private const string ChannelMotion = "FlamiePrac_Mover";
    private const int FallbackTickRate = 100;
    /// <summary>Max pose stream rate for circular targets (rotators/movers stay clock-local).</summary>
    private const int PoseSendHz = 30;
    private const int MaxUnreliablePayload = 1000;
    private const int WriterMaxSize = 64 * 1024;
    private const int PoseBytes = sizeof(float) * 13;
    private const byte MsgParams = 1;
    private const byte MsgPoses = 2;
    private const int ParamsEveryTicks = 15; // ~0.5s at 30 Hz network tick

    private sealed class ServerEntry
    {
        public int SyncId;
        public string RelativePath;
        public Transform Transform;
    }

    private static readonly List<ServerEntry> ServerEntries = new List<ServerEntry>();
    private static readonly Dictionary<int, List<ServerEntry>> EntriesByRink =
        new Dictionary<int, List<ServerEntry>>(8);
    private static readonly Dictionary<string, TrainingMotionVisual> Visuals =
        new Dictionary<string, TrainingMotionVisual>();

    private static bool handlersRegistered;
    private static int lastSeenTick;
    private static int lastPoseSentTick;
    private static float nextFallbackBroadcastTime;
    private static int lastParamsTick;
    private static bool loggedClockDrive;
    private static float nextOverflowLogTime;

    public static void RegisterFromRoot(GameObject root, int syncId, bool serverAuthority)
    {
        if (root == null)
            return;

        Transform rootTransform = root.transform;

        // Clock-locked local sim on server AND clients — eliminates spin visual/hitbox lag.
        foreach (ConstantRotator rotator in root.GetComponentsInChildren<ConstantRotator>(true))
        {
            if (rotator == null)
                continue;
            rotator.simulateLocally = true;
        }

        foreach (ConstantMover mover in root.GetComponentsInChildren<ConstantMover>(true))
        {
            if (mover == null)
                continue;
            mover.simulateLocally = true;
        }

        if (!loggedClockDrive)
        {
            loggedClockDrive = true;
            FlamieLog.InfoOnce("clock-drive", "[FlamiePrac] Rotators/movers use ServerTime clock sync (local sim both sides).");
        }

        // Bounce targets are non-deterministic (random on hit) — keep pose replication.
        CircularMovingTarget circular = root.GetComponent<CircularMovingTarget>();
        if (circular != null)
        {
            circular.simulateLocally = serverAuthority;
            RegisterNode(rootTransform, circular.transform, syncId, serverAuthority);
        }

        // Force an early params push so late joiners get /speed overrides.
        if (serverAuthority)
            lastParamsTick = 0;
    }

    private static void RegisterNode(Transform root, Transform target, int syncId, bool serverAuthority)
    {
        if (target == null)
            return;

        string path = SlidableObstacleSetup.GetRelativePath(root, target);
        if (serverAuthority)
        {
            for (int i = ServerEntries.Count - 1; i >= 0; i--)
            {
                ServerEntry existing = ServerEntries[i];
                if (existing.SyncId == syncId && existing.RelativePath == path)
                    ServerEntries.RemoveAt(i);
            }

            ServerEntries.Add(new ServerEntry
            {
                SyncId = syncId,
                RelativePath = path,
                Transform = target
            });
            return;
        }

        TrainingMotionVisual visual = target.GetComponent<TrainingMotionVisual>();
        if (visual == null)
            visual = target.gameObject.AddComponent<TrainingMotionVisual>();

        visual.Initialize(syncId, path);
        Visuals[MakeKey(syncId, path)] = visual;
    }

    public static void UnregisterSyncId(int syncId)
    {
        for (int i = ServerEntries.Count - 1; i >= 0; i--)
        {
            if (ServerEntries[i].SyncId == syncId)
                ServerEntries.RemoveAt(i);
        }

        var removeKeys = new List<string>();
        foreach (KeyValuePair<string, TrainingMotionVisual> pair in Visuals)
        {
            if (pair.Value == null || pair.Value.ParentSyncId == syncId)
                removeKeys.Add(pair.Key);
        }

        foreach (string key in removeKeys)
            Visuals.Remove(key);
    }

    public static void UnregisterAll()
    {
        ServerEntries.Clear();
        EntriesByRink.Clear();
        Visuals.Clear();
        loggedClockDrive = false;
        lastSeenTick = 0;
        lastPoseSentTick = 0;
        FlamiePracRinkInterest.ResetLogFlag();
    }

    public static void EnsureHandlers(NetworkManager nm)
    {
        if (handlersRegistered || nm == null || !nm.IsClient)
            return;

        nm.CustomMessagingManager?.RegisterNamedMessageHandler(ChannelMotion, OnMotionReceived);
        handlersRegistered = true;
    }

    public static void UnregisterHandlers(CustomMessagingManager messaging)
    {
        if (!handlersRegistered || messaging == null)
            return;

        messaging.UnregisterNamedMessageHandler(ChannelMotion);
        handlersRegistered = false;
    }

    /// <summary>Call after /speed (or similar) so clients match server rotator rate.</summary>
    public static void BroadcastParamsNow()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        try
        {
            SendParams(nm);
            lastParamsTick = GetServerTick(nm);
        }
        catch (Exception ex)
        {
            FlamieLog.Warn("[FlamiePrac] Motion params broadcast failed: " + ex.Message);
        }
    }

    public static void TickServer()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.ConnectedClientsList == null)
            return;

        int tick = GetServerTick(nm);
        int tickRate = GetTickRate(nm);
        int poseStride = Mathf.Max(1, Mathf.RoundToInt(tickRate / (float)PoseSendHz));

        if (tick != 0)
        {
            if (tick == lastSeenTick)
                return;
            lastSeenTick = tick;
        }
        else if (Time.time < nextFallbackBroadcastTime)
        {
            return;
        }

        FlamiePracRinkInterest.RebuildClientGroups(nm);
        if (!FlamiePracRinkInterest.AnyInterestedClients() && FlamiePracRinkInterest.UseInterestFilter)
            return;

        try
        {
            bool needParams = tick == 0 ||
                              lastParamsTick == 0 ||
                              (tick - lastParamsTick) >= ParamsEveryTicks;
            if (needParams)
            {
                SendParams(nm);
                lastParamsTick = tick != 0 ? tick : lastParamsTick;
            }

            for (int i = ServerEntries.Count - 1; i >= 0; i--)
            {
                if (ServerEntries[i].Transform == null)
                    ServerEntries.RemoveAt(i);
            }

            bool sendPoses = ServerEntries.Count > 0;
            if (sendPoses && tick != 0)
                sendPoses = lastPoseSentTick == 0 || (tick - lastPoseSentTick) >= poseStride;

            if (sendPoses && ServerEntries.Count > 0)
            {
                SendPoseBatches(nm);
                if (tick != 0)
                    lastPoseSentTick = tick;
                else
                    nextFallbackBroadcastTime = Time.time + (1f / PoseSendHz);
            }
            else if (tick == 0)
            {
                nextFallbackBroadcastTime = Time.time + (1f / PoseSendHz);
            }
        }
        catch (Exception ex)
        {
            if (Time.time >= nextOverflowLogTime)
            {
                nextOverflowLogTime = Time.time + 5f;
                FlamieLog.Warn("[FlamiePrac] Motion sync broadcast failed: " + ex.Message);
            }
        }
    }

    private static void SendParams(NetworkManager nm)
    {
        using (FastBufferWriter writer = new FastBufferWriter(32, Allocator.Temp, 256))
        {
            writer.WriteValueSafe(MsgParams);
            writer.WriteValueSafe(ConstantRotator.globalSpeed);
            writer.WriteValueSafe(ConstantMover.globalSpeed);
            writer.WriteValueSafe(ConstantMover.globalDistance);
            FlamiePracRinkInterest.RebuildClientGroups(nm);
            FlamiePracRinkInterest.SendBroadcastOrToAllBodies(
                nm,
                ChannelMotion,
                writer,
                NetworkDelivery.Unreliable);
        }
    }

    private static void SendPoseBatches(NetworkManager nm)
    {
        if (!FlamiePracRinkInterest.UseInterestFilter)
        {
            SendPoseBatchesForEntries(nm, ServerEntries, broadcastAll: true);
            return;
        }

        foreach (List<ServerEntry> list in EntriesByRink.Values)
            list.Clear();

        for (int i = 0; i < ServerEntries.Count; i++)
        {
            ServerEntry entry = ServerEntries[i];
            if (entry.Transform == null)
                continue;
            int rink = FlamiePracRinkInterest.RinkOfWorldPosition(entry.Transform.position);
            if (!EntriesByRink.TryGetValue(rink, out List<ServerEntry> bucket))
            {
                bucket = new List<ServerEntry>(8);
                EntriesByRink.Add(rink, bucket);
            }

            bucket.Add(entry);
        }

        foreach (KeyValuePair<int, List<ServerEntry>> kvp in EntriesByRink)
        {
            if (kvp.Value.Count == 0)
                continue;
            if (!FlamiePracRinkInterest.AnyClientsOnRink(kvp.Key))
                continue;
            SendPoseBatchesForEntries(nm, kvp.Value, broadcastAll: false, rinkIndex: kvp.Key);
        }
    }

    private static void SendPoseBatchesForEntries(
        NetworkManager nm,
        List<ServerEntry> entries,
        bool broadcastAll,
        int rinkIndex = -1)
    {
        int index = 0;
        while (index < entries.Count)
        {
            int batchStart = index;
            int estimated = 5; // msg type + count
            while (index < entries.Count)
            {
                int entryBytes = EstimateEntryBytes(entries[index]);
                if (index > batchStart && estimated + entryBytes > MaxUnreliablePayload)
                    break;

                estimated += entryBytes;
                index++;
            }

            int batchCount = index - batchStart;
            int capacity = Mathf.Max(estimated + 32, 256);
            using (FastBufferWriter writer = new FastBufferWriter(capacity, Allocator.Temp, WriterMaxSize))
            {
                writer.WriteValueSafe(MsgPoses);
                writer.WriteValueSafe(batchCount);
                for (int i = batchStart; i < index; i++)
                    WriteEntry(writer, entries[i]);

                if (broadcastAll)
                {
                    nm.CustomMessagingManager.SendNamedMessageToAll(
                        ChannelMotion,
                        writer,
                        NetworkDelivery.Unreliable);
                }
                else
                {
                    FlamiePracRinkInterest.SendToClients(
                        nm,
                        ChannelMotion,
                        writer,
                        FlamiePracRinkInterest.ClientsOnRink(rinkIndex),
                        NetworkDelivery.Unreliable);
                }
            }
        }
    }

    private static int EstimateEntryBytes(ServerEntry entry)
    {
        string path = entry.RelativePath ?? string.Empty;
        int pathBytes = Encoding.UTF8.GetByteCount(path);
        return 4 + 2 + pathBytes + PoseBytes;
    }

    private static void WriteEntry(FastBufferWriter writer, ServerEntry entry)
    {
        Transform t = entry.Transform;
        writer.WriteValueSafe(entry.SyncId);
        WriteString(writer, entry.RelativePath);
        writer.WriteValueSafe(t.position.x);
        writer.WriteValueSafe(t.position.y);
        writer.WriteValueSafe(t.position.z);
        writer.WriteValueSafe(t.rotation.x);
        writer.WriteValueSafe(t.rotation.y);
        writer.WriteValueSafe(t.rotation.z);
        writer.WriteValueSafe(t.rotation.w);
        // Velocities unused for circular targets; keep zeros for layout stability.
        writer.WriteValueSafe(0f);
        writer.WriteValueSafe(0f);
        writer.WriteValueSafe(0f);
        writer.WriteValueSafe(0f);
        writer.WriteValueSafe(0f);
        writer.WriteValueSafe(0f);
    }

    private static void OnMotionReceived(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.IsServer || senderClientId != NetworkManager.ServerClientId)
            return;

        try
        {
            reader.ReadValueSafe(out byte msgType);
            if (msgType == MsgParams)
            {
                reader.ReadValueSafe(out float rotSpeed);
                reader.ReadValueSafe(out float moveSpeed);
                reader.ReadValueSafe(out float moveDist);
                ConstantRotator.globalSpeed = rotSpeed;
                ConstantMover.globalSpeed = moveSpeed;
                ConstantMover.globalDistance = moveDist;
                return;
            }

            if (msgType != MsgPoses)
                return;

            reader.ReadValueSafe(out int count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out int syncId);
                string path = ReadString(reader);
                reader.ReadValueSafe(out float px);
                reader.ReadValueSafe(out float py);
                reader.ReadValueSafe(out float pz);
                reader.ReadValueSafe(out float qx);
                reader.ReadValueSafe(out float qy);
                reader.ReadValueSafe(out float qz);
                reader.ReadValueSafe(out float qw);
                reader.ReadValueSafe(out float vx);
                reader.ReadValueSafe(out float vy);
                reader.ReadValueSafe(out float vz);
                reader.ReadValueSafe(out float ax);
                reader.ReadValueSafe(out float ay);
                reader.ReadValueSafe(out float az);

                if (!Visuals.TryGetValue(MakeKey(syncId, path), out TrainingMotionVisual visual) ||
                    visual == null)
                    continue;

                if (!visual.gameObject.activeInHierarchy)
                    continue;

                visual.ApplyState(
                    new Vector3(px, py, pz),
                    new Quaternion(qx, qy, qz, qw),
                    new Vector3(vx, vy, vz),
                    new Vector3(ax, ay, az));
            }
        }
        catch (Exception ex)
        {
            FlamieLog.Warn("[FlamiePrac] Motion sync receive failed: " + ex.Message);
        }
    }

    private static int GetServerTick(NetworkManager nm)
    {
        try
        {
            if (nm.NetworkTickSystem != null)
                return (int)nm.NetworkTickSystem.ServerTime.Tick;
        }
        catch
        {
            // Fall through
        }

        try
        {
            return (int)nm.ServerTime.Tick;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetTickRate(NetworkManager nm)
    {
        try
        {
            if (nm.NetworkConfig != null && nm.NetworkConfig.TickRate > 0)
                return (int)nm.NetworkConfig.TickRate;
        }
        catch
        {
            // ignored
        }

        return FallbackTickRate;
    }

    private static void WriteString(FastBufferWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > 1024)
        {
            byte[] cut = new byte[1024];
            Buffer.BlockCopy(bytes, 0, cut, 0, 1024);
            bytes = cut;
        }

        writer.WriteValueSafe((ushort)bytes.Length);
        if (bytes.Length > 0)
            writer.WriteBytesSafe(bytes);
    }

    private static string ReadString(FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort length);
        if (length == 0)
            return string.Empty;

        byte[] bytes = new byte[length];
        reader.ReadBytesSafe(ref bytes, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string MakeKey(int syncId, string path) => syncId + ":" + (path ?? string.Empty);
}

/// <summary>Client follower for non-deterministic movers (circular targets).</summary>
public sealed class TrainingMotionVisual : MonoBehaviour
{
    public int ParentSyncId { get; private set; }
    public string RelativePath { get; private set; }

    private Vector3 networkPos;
    private Quaternion networkRot = Quaternion.identity;
    private bool hasState;

    public void Initialize(int syncId, string relativePath)
    {
        ParentSyncId = syncId;
        RelativePath = relativePath ?? string.Empty;
        networkPos = transform.position;
        networkRot = transform.rotation;
        hasState = false;
    }

    public void ApplyState(Vector3 worldPos, Quaternion worldRot, Vector3 linVel, Vector3 angVel)
    {
        networkPos = worldPos;
        networkRot = worldRot;
        hasState = true;
    }

    private void LateUpdate()
    {
        if (!hasState || !isActiveAndEnabled)
            return;

        transform.SetPositionAndRotation(networkPos, networkRot);
    }
}
