using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Replicates slidable beam/speaker transforms from server to remote clients (world poses).
/// </summary>
public static class SlidableObstacleSync
{
    private const string ChannelSlidable = "FlamiePrac_Slidable";
    private const int FallbackTickRate = 100;
    /// <summary>Settled slidables sync ~5 Hz; moving props every network tick.</summary>
    private const int IdleBroadcastIntervalTicks = 20;
    private const int WriterMaxSize = 64 * 1024;

    private static readonly Dictionary<string, SlidableObstacleVisual> Visuals =
        new Dictionary<string, SlidableObstacleVisual>();

    private static bool handlersRegistered;
    private static int lastBroadcastTick;
    private static float nextFallbackBroadcastTime;
    private static float nextMissLogTime;
    private static bool loggedBroadcastOk;

    public static void RegisterVisual(SlidableObstacleVisual visual)
    {
        if (visual == null)
            return;

        string key = MakeKey(
            visual.ParentSyncId,
            SlidableObstacleSetup.CanonicalSlidablePath(visual.RelativePath));
        Visuals[key] = visual;
    }

    public static void RegisterVisualAlias(SlidableObstacleVisual visual, int syncId, string pathOrName)
    {
        if (visual == null || string.IsNullOrEmpty(pathOrName))
            return;

        Visuals[MakeKey(syncId, SlidableObstacleSetup.CanonicalSlidablePath(pathOrName))] = visual;
        Visuals[MakeKey(syncId, pathOrName)] = visual;
    }

    public static void UnregisterVisual(SlidableObstacleVisual visual)
    {
        if (visual == null)
            return;

        var remove = new List<string>();
        foreach (KeyValuePair<string, SlidableObstacleVisual> pair in Visuals)
        {
            if (pair.Value == visual)
                remove.Add(pair.Key);
        }

        foreach (string key in remove)
            Visuals.Remove(key);
    }

    public static void UnregisterAll()
    {
        Visuals.Clear();
        loggedBroadcastOk = false;
    }

    public static List<SlidableObstacleVisual> GetVisualsForSyncId(int syncId)
    {
        var list = new List<SlidableObstacleVisual>();
        foreach (KeyValuePair<string, SlidableObstacleVisual> pair in Visuals)
        {
            SlidableObstacleVisual visual = pair.Value;
            if (visual == null || visual.ParentSyncId != syncId)
                continue;

            if (!list.Contains(visual))
                list.Add(visual);
        }

        return list;
    }

    /// <summary>
    /// Keep unparented client slidables under the DDOL visual root across rink scene swaps.
    /// </summary>
    public static void AttachVisualsToClientRoot(int syncId, Transform clientRoot)
    {
        if (clientRoot == null)
            return;

        foreach (SlidableObstacleVisual visual in GetVisualsForSyncId(syncId))
        {
            if (visual == null)
                continue;

            if (visual.transform.parent != clientRoot)
                visual.transform.SetParent(clientRoot, true);
        }
    }

    /// <summary>
    /// Client slidables are unparented from the hive — destroy them when the hive despawns.
    /// </summary>
    public static void DestroyVisualsForSyncId(int syncId)
    {
        foreach (SlidableObstacleVisual visual in GetVisualsForSyncId(syncId))
        {
            if (visual == null)
                continue;

            UnregisterVisual(visual);
            Object.Destroy(visual.gameObject);
        }
    }

    public static void EnsureHandlers(NetworkManager nm)
    {
        if (nm == null || !nm.IsClient)
            return;

        CustomMessagingManager messaging = nm.CustomMessagingManager;
        if (messaging == null)
            return;

        // Always (re)bind — static flag alone can skip register after NM/messaging recreate.
        if (handlersRegistered)
            messaging.UnregisterNamedMessageHandler(ChannelSlidable);

        messaging.RegisterNamedMessageHandler(ChannelSlidable, OnSlidableReceived);
        handlersRegistered = true;
        Debug.Log("[FlamiePrac] Slidable client handler registered (" + ChannelSlidable + ").");
    }

    public static void UnregisterHandlers(CustomMessagingManager messaging)
    {
        if (!handlersRegistered || messaging == null)
            return;

        messaging.UnregisterNamedMessageHandler(ChannelSlidable);
        handlersRegistered = false;
    }

    public static void ForceBroadcastAll()
    {
        lastBroadcastTick = 0;
        nextFallbackBroadcastTime = 0f;
        SlidableObstacle.ResetAllPoseBroadcast();
        TickServer();
    }

    public static void TickServer()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.ConnectedClientsList == null)
            return;

        int tick = GetServerTick(nm);
        if (tick != 0)
        {
            if (tick == lastBroadcastTick)
                return;
        }
        else if (Time.time < nextFallbackBroadcastTime)
        {
            return;
        }

        int clientCount = 0;
        foreach (NetworkClient client in nm.ConnectedClientsList)
        {
            if (client.ClientId != NetworkManager.ServerClientId)
                clientCount++;
        }

        if (clientCount == 0)
            return;

        IReadOnlyList<SlidableObstacle> obstacles = SlidableObstacle.ActiveObstacles;
        if (obstacles == null || obstacles.Count == 0)
            return;

        var live = new List<SlidableObstacle>(obstacles.Count);
        float idleSeconds = IdleBroadcastIntervalTicks / (float)GetTickRate(nm);
        for (int i = 0; i < obstacles.Count; i++)
        {
            SlidableObstacle obstacle = obstacles[i];
            if (obstacle == null)
                continue;

            if (!obstacle.ShouldBroadcastPose(tick, IdleBroadcastIntervalTicks, idleSeconds))
                continue;

            live.Add(obstacle);
        }

        if (live.Count == 0)
        {
            if (tick != 0)
                lastBroadcastTick = tick;
            else
                nextFallbackBroadcastTime = Time.time + (1f / GetTickRate(nm));
            return;
        }

        if (tick != 0)
            lastBroadcastTick = tick;
        else
            nextFallbackBroadcastTime = Time.time + (1f / GetTickRate(nm));

        try
        {
            int estimatedSize = 4 + live.Count * 256;
            using (FastBufferWriter writer = new FastBufferWriter(estimatedSize, Allocator.Temp, WriterMaxSize))
            {
                writer.WriteValueSafe(live.Count);
                for (int i = 0; i < live.Count; i++)
                {
                    live[i].WriteState(writer);
                    live[i].MarkPoseBroadcast(tick);
                }

                nm.CustomMessagingManager.SendNamedMessageToAll(
                    ChannelSlidable,
                    writer,
                    NetworkDelivery.Unreliable);

                if (!loggedBroadcastOk)
                {
                    loggedBroadcastOk = true;
                    Debug.Log("[FlamiePrac] Slidable sync broadcasting up to " + obstacles.Count +
                              " prop(s) (moving every tick, idle every " + IdleBroadcastIntervalTicks + " ticks).");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] Slidable sync broadcast failed: " + ex.Message);
        }
    }

    private static void OnSlidableReceived(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.IsServer || senderClientId != NetworkManager.ServerClientId)
            return;

        try
        {
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

                SlidableObstacleVisual visual = ResolveVisual(syncId, path);
                if (visual == null)
                {
                    LogMiss(syncId, path);
                    continue;
                }

                visual.ApplyState(
                    new Vector3(px, py, pz),
                    new Quaternion(qx, qy, qz, qw),
                    new Vector3(vx, vy, vz),
                    new Vector3(ax, ay, az));
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] Slidable sync receive failed: " + ex.Message);
        }
    }

    private static SlidableObstacleVisual ResolveVisual(int syncId, string path)
    {
        string canonical = SlidableObstacleSetup.CanonicalSlidablePath(path);

        if (Visuals.TryGetValue(MakeKey(syncId, canonical), out SlidableObstacleVisual visual) &&
            visual != null)
            return visual;

        if (Visuals.TryGetValue(MakeKey(syncId, path), out visual) && visual != null)
            return visual;

        foreach (KeyValuePair<string, SlidableObstacleVisual> pair in Visuals)
        {
            SlidableObstacleVisual candidate = pair.Value;
            if (candidate == null || candidate.ParentSyncId != syncId)
                continue;

            if (SlidableObstacleSetup.CanonicalSlidablePath(candidate.RelativePath) == canonical ||
                SlidableObstacleSetup.CanonicalSlidablePath(candidate.gameObject.name) == canonical)
                return candidate;
        }

        return null;
    }

    private static void LogMiss(int syncId, string path)
    {
        if (Time.time < nextMissLogTime)
            return;

        nextMissLogTime = Time.time + 3f;
        var keys = new StringBuilder();
        int n = 0;
        foreach (string key in Visuals.Keys)
        {
            if (n++ > 12)
            {
                keys.Append("…");
                break;
            }

            if (keys.Length > 0)
                keys.Append(", ");
            keys.Append(key);
        }

        Debug.LogWarning("[FlamiePrac] Slidable pose miss syncId=" + syncId +
                         " path='" + path + "' canon='" +
                         SlidableObstacleSetup.CanonicalSlidablePath(path) +
                         "' registered=[" + keys + "]");
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

    private static string ReadString(FastBufferReader reader)
    {
        reader.ReadValueSafe(out int length);
        if (length <= 0)
            return string.Empty;

        if (length > 2048)
            throw new System.InvalidOperationException("Slidable path length " + length);

        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            reader.ReadValueSafe(out char c);
            chars[i] = c;
        }

        return new string(chars);
    }

    private static string MakeKey(int syncId, string path) => syncId + ":" + (path ?? string.Empty);
}
