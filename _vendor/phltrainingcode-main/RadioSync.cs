using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Server-authoritative practice radio: one track id + start <see cref="NetworkTime"/> for all clients.
/// Late joiners receive the current snapshot. Skip and restart are majority vote (⌊n/2⌋+1; solo = 1/1).
/// Dedicated owns state without playing audio; clients stream via phlstats signed URLs.
/// </summary>
public static class RadioSync
{
    public const byte ReqVoteSkip = 1;
    public const byte ReqRequestState = 2;
    public const byte ReqRestart = 3;
    public const byte ReqReportDuration = 4;
    public const byte ReqTrackEnded = 5;
    public const byte ReqTrackReady = 6;

    public const byte MsgState = 1;
    public const byte FlagClockStarted = 1;

    private const float StateResyncSeconds = 30f;
    private const float MinTrackSecondsBeforeEnd = 1.5f;
    /// <summary>Offline / local shuffle gap between tracks.</summary>
    public const float InterTrackGapSeconds = 2.5f;
    /// <summary>Max wait for client track prep before the server starts anyway.</summary>
    private const float ReadyTimeoutSeconds = 15f;
    /// <summary>If the clock runs this long with no client duration, skip the stuck track.</summary>
    private const float StuckWithoutDurationSeconds = 8f;
    /// <summary>Re-poll phlstats /playlist so newly uploaded tracks enter the shuffle without a reboot.</summary>
    private const float PlaylistRefreshSeconds = 300f;

    private static readonly List<string> playlistIds = new List<string>();
    private static readonly List<string> shuffleOrder = new List<string>();
    private static readonly HashSet<ulong> skipVotes = new HashSet<ulong>();
    private static readonly HashSet<ulong> restartVotes = new HashSet<ulong>();
    private static readonly HashSet<ulong> readyClients = new HashSet<ulong>();
    private static readonly Dictionary<string, float> durations = new Dictionary<string, float>(StringComparer.Ordinal);

    private static string currentTrackId = string.Empty;
    private static double trackStartServerTime;
    private static bool clockStarted;
    private static float prepareStartedAt;
    private static int shuffleIndex;
    private static bool serverReady;
    private static bool fetchStarted;
    private static bool usingRemotePlaylist;
    private static float nextStateBroadcastAt;
    private static string apiBase = RadioApiUtil.DefaultApiBase;
    private static MonoBehaviour refreshHost;

    public static string CurrentTrackId => currentTrackId;
    public static double TrackStartServerTime => trackStartServerTime;
    public static bool ClockStarted => clockStarted;
    public static int VoteCount => skipVotes.Count;
    public static int RestartVoteCount => restartVotes.Count;
    public static int VoteNeed => MajorityNeed(CountEligibleVoters());
    public static bool ServerReady => serverReady;

    public static event Action StateChanged;

    public static void Reset()
    {
        playlistIds.Clear();
        shuffleOrder.Clear();
        skipVotes.Clear();
        restartVotes.Clear();
        durations.Clear();
        readyClients.Clear();
        currentTrackId = string.Empty;
        trackStartServerTime = 0;
        clockStarted = false;
        prepareStartedAt = 0f;
        shuffleIndex = 0;
        serverReady = false;
        fetchStarted = false;
        usingRemotePlaylist = false;
        nextStateBroadcastAt = 0f;
        refreshHost = null;
    }

    public static void OnServerNetworkReady(MonoBehaviour host)
    {
        if (host == null || fetchStarted)
            return;

        fetchStarted = true;
        refreshHost = host;
        host.StartCoroutine(ServerBootstrap());
    }

    public static void TickServer()
    {
        if (!serverReady)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
            return;

        PruneDisconnectedVoters(nm);
        PruneDisconnectedReady(nm);
        TryStartTrackWhenReady(nm);
        TryAutoAdvance(nm);

        if (Time.unscaledTime >= nextStateBroadcastAt)
        {
            nextStateBroadcastAt = Time.unscaledTime + StateResyncSeconds;
            BroadcastState();
        }
    }

    private static void PruneDisconnectedVoters(NetworkManager nm)
    {
        if (skipVotes.Count > 0)
            skipVotes.RemoveWhere(id => !IsClientConnected(nm, id));
        if (restartVotes.Count > 0)
            restartVotes.RemoveWhere(id => !IsClientConnected(nm, id));
    }

    private static void PruneDisconnectedReady(NetworkManager nm)
    {
        if (readyClients.Count == 0)
            return;

        readyClients.RemoveWhere(id => !IsClientConnected(nm, id));
    }

    private static int CountReadyClients(NetworkManager nm)
    {
        PruneDisconnectedReady(nm);
        return readyClients.Count;
    }

    private static int CountEligibleRadioClients(NetworkManager nm)
    {
        if (nm == null)
            return 0;

        int n = 0;
        foreach (ulong id in nm.ConnectedClientsIds)
        {
            if (id == NetworkManager.ServerClientId && !nm.IsClient)
                continue;
            if (FakePlayerDetector.IsAnyFakeClientId(id))
                continue;
            n++;
        }

        return n;
    }

    private static void TryStartTrackWhenReady(NetworkManager nm)
    {
        if (clockStarted || string.IsNullOrEmpty(currentTrackId))
            return;

        int eligible = CountEligibleRadioClients(nm);
        int have = CountReadyClients(nm);
        int need = MajorityNeed(Math.Max(1, eligible));
        bool timeout = prepareStartedAt > 0f
            && Time.unscaledTime - prepareStartedAt >= ReadyTimeoutSeconds;

        if (eligible == 0)
        {
            if (timeout)
                StartTrackClock(nm);
            return;
        }

        // Wait for a client duration report before starting the clock (unless prepare timeout).
        if (!timeout && !HasKnownDuration(currentTrackId))
            return;

        if (have >= need || timeout)
            StartTrackClock(nm);
    }

    private static bool HasKnownDuration(string trackId)
    {
        return !string.IsNullOrEmpty(trackId) &&
               durations.TryGetValue(trackId, out float dur) &&
               dur > 0.05f;
    }

    private static void StartTrackClock(NetworkManager nm)
    {
        if (clockStarted || string.IsNullOrEmpty(currentTrackId))
            return;

        trackStartServerTime = nm != null ? nm.ServerTime.Time : Time.realtimeSinceStartupAsDouble;
        int ready = CountReadyClients(nm);
        int eligible = CountEligibleRadioClients(nm);
        clockStarted = true;
        readyClients.Clear();
        BroadcastState();
        FlamieLog.Info("[FlamiePrac] RadioSync started '" + currentTrackId + "' ready=" + ready + "/" + eligible);
    }

    private static void BeginPreparePhase()
    {
        clockStarted = false;
        trackStartServerTime = 0;
        readyClients.Clear();
        prepareStartedAt = Time.unscaledTime;
    }

    private static bool IsClientConnected(NetworkManager nm, ulong clientId)
    {
        foreach (ulong id in nm.ConnectedClientsIds)
        {
            if (id == clientId)
                return true;
        }

        return false;
    }

    public static void SendStateToClient(ulong clientId)
    {
        if (!serverReady || string.IsNullOrEmpty(currentTrackId))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm?.CustomMessagingManager == null || !nm.IsServer)
            return;

        using (FastBufferWriter writer = BuildStateWriter())
        {
            nm.CustomMessagingManager.SendNamedMessage(
                "FlamiePrac_Radio",
                clientId,
                writer,
                NetworkDelivery.Reliable);
        }
    }

    public static void HandleRadioRequest(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
            return;

        reader.ReadValueSafe(out byte cmd);

        switch (cmd)
        {
            case ReqVoteSkip:
                HandleVoteSkip(senderClientId);
                break;
            case ReqRequestState:
                SendStateToClient(senderClientId);
                break;
            case ReqRestart:
                HandleVoteRestart(senderClientId);
                break;
            case ReqReportDuration:
            {
                string id = ReadString(reader);
                reader.ReadValueSafe(out float duration);
                if (TryStoreDuration(id, duration) && id == currentTrackId)
                {
                    BroadcastState();
                    TryStartTrackWhenReady(nm);
                }
                break;
            }
            case ReqTrackEnded:
                // Server clock owns playlist advance — end reports are ignored.
                ReadString(reader);
                break;
            case ReqTrackReady:
            {
                string id = ReadString(reader);
                if (!serverReady || clockStarted || string.IsNullOrEmpty(currentTrackId))
                    break;
                if (!string.Equals(id, currentTrackId, StringComparison.Ordinal))
                    break;
                readyClients.Add(senderClientId);
                FlamieLog.Info("[FlamiePrac] RadioSync client ready " + senderClientId +
                               " for '" + id + "' (" + CountReadyClients(nm) + "/" +
                               CountEligibleRadioClients(nm) + ")");
                TryStartTrackWhenReady(nm);
                break;
            }
        }
    }

    public static void EnsureClientRadio(MonoBehaviour host)
    {
        if (Application.isBatchMode || !FlamiePracFeatures.EnableRadio)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsClient || nm.IsServer)
            return;

        if (RadioController.Instance != null)
        {
            RadioController.ApplyClientPreferencesFromMultiSheet();
            return;
        }

        if (host == null)
            host = TrainingSync.Instance;
        if (host == null)
            return;

        host.gameObject.AddComponent<RadioController>();
        if (host.GetComponent<RadioHudDriver>() == null)
            host.gameObject.AddComponent<RadioHudDriver>();
        RadioController.ApplyClientPreferencesFromMultiSheet();
    }

    public static void HandleRadioStateMessage(FastBufferReader reader)
    {
        reader.ReadValueSafe(out byte msgType);
        if (msgType != MsgState)
            return;

        reader.ReadValueSafe(out ushort voteCount);
        reader.ReadValueSafe(out ushort restartVoteCount);
        reader.ReadValueSafe(out ushort voteNeed);
        reader.ReadValueSafe(out double startServerTime);
        reader.ReadValueSafe(out float duration);
        reader.ReadValueSafe(out byte flags);
        string trackId = ReadString(reader);
        bool stateClockStarted = (flags & FlagClockStarted) != 0;

        if (string.IsNullOrEmpty(trackId))
            return;

        EnsureClientRadio(TrainingSync.Instance);

        if (RadioController.Instance != null)
            RadioController.Instance.ApplyServerState(
                trackId, startServerTime, duration, voteCount, restartVoteCount, voteNeed, stateClockStarted);
        else
            PendingClientState.Store(
                trackId, startServerTime, duration, voteCount, restartVoteCount, voteNeed, stateClockStarted);

        try
        {
            StateChanged?.Invoke();
        }
        catch { }
    }

    public static void ClientRequestState()
    {
        SendRequest(ReqRequestState);
    }

    public static void ClientVoteSkip()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer)
        {
            HandleVoteSkip(nm.LocalClientId);
            return;
        }

        SendRequest(ReqVoteSkip);
    }

    public static void ClientRestart()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer)
        {
            HandleVoteRestart(nm.LocalClientId);
            return;
        }

        SendRequest(ReqRestart);
    }

    public static void ClientReportDuration(string trackId, float duration)
    {
        if (string.IsNullOrEmpty(trackId) || duration <= 0.05f)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer)
        {
            if (TryStoreDuration(trackId, duration) && trackId == currentTrackId)
                BroadcastState();
            return;
        }

        if (nm == null || !nm.IsClient || nm.CustomMessagingManager == null)
            return;

        byte[] idBytes = Encoding.UTF8.GetBytes(trackId);
        int size = 1 + 2 + idBytes.Length + 4;
        using (FastBufferWriter writer = new FastBufferWriter(size + 8, Allocator.Temp))
        {
            writer.WriteValueSafe(ReqReportDuration);
            WriteString(writer, trackId);
            writer.WriteValueSafe(duration);
            nm.CustomMessagingManager.SendNamedMessage(
                "FlamiePrac_RadioRequest",
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.Reliable);
        }
    }

    public static void ClientReportTrackEnded(string trackId)
    {
        // Server clock owns playlist advance — clients no longer report track end.
    }

    public static void ClientReportTrackReady(string trackId)
    {
        if (string.IsNullOrEmpty(trackId))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer)
        {
            if (!serverReady || clockStarted || trackId != currentTrackId)
                return;
            readyClients.Add(nm.LocalClientId);
            TryStartTrackWhenReady(nm);
            return;
        }

        if (nm == null || !nm.IsClient || nm.CustomMessagingManager == null)
            return;

        using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
        {
            writer.WriteValueSafe(ReqTrackReady);
            WriteString(writer, trackId);
            nm.CustomMessagingManager.SendNamedMessage(
                "FlamiePrac_RadioRequest",
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.Reliable);
        }
    }

    /// <summary>Offline / no Netcode: local client owns a simple shuffle.</summary>
    public static bool IsNetworkSynced
    {
        get
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm != null && (nm.IsServer || nm.IsClient) && nm.IsListening;
        }
    }

    private static IEnumerator ServerBootstrap()
    {
        LoadServerApiBase();
        yield return FetchPlaylistIds();

        if (playlistIds.Count == 0)
        {
            var fileIds = new List<string>();
            if (RadioApiUtil.TryLoadPlaylistFile(fileIds))
                playlistIds.AddRange(fileIds);
        }

        if (playlistIds.Count == 0)
        {
            FlamieLog.Warn("[FlamiePrac] RadioSync: server playlist empty — radio sync idle.");
            yield break;
        }

        playlistIds.Sort(StringComparer.Ordinal);
        BuildShuffleOrder(null);
        AdvanceToNextTrack(first: true);
        serverReady = true;
        nextStateBroadcastAt = Time.unscaledTime + StateResyncSeconds;
        BroadcastState();
        FlamieLog.Info("[FlamiePrac] RadioSync ready — track='" + currentTrackId + "' playlist=" + playlistIds.Count);

        if (usingRemotePlaylist && refreshHost != null)
            refreshHost.StartCoroutine(ServerPlaylistRefreshLoop());
    }

    private static void LoadServerApiBase()
    {
        try
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), "config", "radio_client.json");
            if (!File.Exists(path))
                return;

            RadioClientConfigFile cfg = JsonUtility.FromJson<RadioClientConfigFile>(File.ReadAllText(path));
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.ApiBase))
                return;

            apiBase = cfg.ApiBase.Trim().TrimEnd('/');
            FlamieLog.Info("[FlamiePrac] RadioSync API base=" + apiBase);
        }
        catch (Exception ex)
        {
            FlamieLog.Warn("[FlamiePrac] RadioSync API config load failed: " + ex.Message);
        }
    }

    [Serializable]
    private sealed class RadioClientConfigFile
    {
        public string ApiBase;
    }

    private static IEnumerator ServerPlaylistRefreshLoop()
    {
        while (serverReady && usingRemotePlaylist)
        {
            yield return new WaitForSecondsRealtime(PlaylistRefreshSeconds);
            if (!serverReady || !usingRemotePlaylist)
                yield break;

            yield return RefreshServerPlaylist();
        }
    }

    private static IEnumerator RefreshServerPlaylist()
    {
        var fresh = new List<string>();
        yield return FetchRemotePlaylistIds(fresh);
        if (fresh.Count == 0)
            yield break;

        MergeServerPlaylist(fresh);
    }

    private static void MergeServerPlaylist(List<string> fresh)
    {
        fresh.Sort(StringComparer.Ordinal);
        var freshSet = new HashSet<string>(fresh, StringComparer.Ordinal);
        int beforeCount = playlistIds.Count;

        var existing = new HashSet<string>(playlistIds, StringComparer.Ordinal);
        int added = 0;
        for (int i = 0; i < fresh.Count; i++)
        {
            string id = fresh[i];
            if (existing.Add(id))
            {
                playlistIds.Add(id);
                added++;
            }
        }

        for (int i = playlistIds.Count - 1; i >= 0; i--)
        {
            if (!freshSet.Contains(playlistIds[i]))
                playlistIds.RemoveAt(i);
        }

        playlistIds.Sort(StringComparer.Ordinal);

        var queuedAhead = new HashSet<string>(StringComparer.Ordinal);
        for (int i = shuffleIndex; i < shuffleOrder.Count; i++)
            queuedAhead.Add(shuffleOrder[i]);

        for (int i = 0; i < fresh.Count; i++)
        {
            string id = fresh[i];
            if (queuedAhead.Contains(id))
                continue;
            shuffleOrder.Add(id);
            queuedAhead.Add(id);
        }

        for (int i = shuffleOrder.Count - 1; i >= shuffleIndex; i--)
        {
            if (!freshSet.Contains(shuffleOrder[i]))
                shuffleOrder.RemoveAt(i);
        }

        if (added == 0 && beforeCount == playlistIds.Count)
            return;

        FlamieLog.Info("[FlamiePrac] RadioSync playlist refreshed: " + playlistIds.Count +
                       " track(s) (+" + added + ", -" + Math.Max(0, beforeCount + added - playlistIds.Count) + ").");
    }

    private static IEnumerator FetchPlaylistIds()
    {
        playlistIds.Clear();
        usingRemotePlaylist = false;
        yield return FetchRemotePlaylistIds(playlistIds);
    }

    private static IEnumerator FetchRemotePlaylistIds(List<string> target)
    {
        target.Clear();
        string url = apiBase.TrimEnd('/') + "/playlist";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            RadioApiUtil.ConfigureRequest(req);
            yield return req.SendWebRequest();

            string body = req.downloadHandler?.text;
            if (req.result == UnityWebRequest.Result.Success &&
                RadioApiUtil.TryParsePlaylist(body, target))
            {
                usingRemotePlaylist = true;
                FlamieLog.Info("[FlamiePrac] RadioSync playlist via UWR: " + target.Count + " track(s).");
                yield break;
            }

            FlamieLog.Warn("[FlamiePrac] RadioSync UWR playlist failed: result=" + req.result +
                             " code=" + req.responseCode + " err=" + req.error +
                             " bodyLen=" + (body != null ? body.Length : 0));
        }

        // Dedicated Linux TLS often breaks UnityWebRequest — WebClient fallback.
        if (RadioApiUtil.TryDownloadString(url, out string downloaded, out string dlErr) &&
            RadioApiUtil.TryParsePlaylist(downloaded, target))
        {
            usingRemotePlaylist = true;
            FlamieLog.Info("[FlamiePrac] RadioSync playlist via WebClient: " + target.Count + " track(s).");
            yield break;
        }

        if (!string.IsNullOrEmpty(dlErr))
            FlamieLog.Warn("[FlamiePrac] RadioSync WebClient playlist failed: " + dlErr);
    }

    private static void BuildShuffleOrder(string avoidId)
    {
        shuffleOrder.Clear();
        shuffleOrder.AddRange(playlistIds);

        for (int i = shuffleOrder.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            string tmp = shuffleOrder[i];
            shuffleOrder[i] = shuffleOrder[j];
            shuffleOrder[j] = tmp;
        }

        if (!string.IsNullOrEmpty(avoidId) && shuffleOrder.Count > 1 && shuffleOrder[0] == avoidId)
        {
            int swap = UnityEngine.Random.Range(1, shuffleOrder.Count);
            string tmp = shuffleOrder[0];
            shuffleOrder[0] = shuffleOrder[swap];
            shuffleOrder[swap] = tmp;
        }

        shuffleIndex = 0;
    }

    private static void AdvanceToNextTrack(bool first)
    {
        if (playlistIds.Count == 0)
            return;

        if (shuffleIndex >= shuffleOrder.Count)
            BuildShuffleOrder(currentTrackId);

        string next = shuffleOrder[shuffleIndex++];
        if (!first && next == currentTrackId && shuffleOrder.Count > 1)
        {
            if (shuffleIndex >= shuffleOrder.Count)
                BuildShuffleOrder(currentTrackId);
            next = shuffleOrder[shuffleIndex++];
        }

        currentTrackId = next;
        BeginPreparePhase();
        skipVotes.Clear();
        restartVotes.Clear();
    }

    private static void RestartCurrentTrack()
    {
        if (string.IsNullOrEmpty(currentTrackId))
            return;

        BeginPreparePhase();
        skipVotes.Clear();
        restartVotes.Clear();
        BroadcastState();
        FlamieLog.Info("[FlamiePrac] RadioSync restart prepare '" + currentTrackId + "'");
    }

    /// <summary>Server chat / admin: count as a skip vote from this client.</summary>
    public static void ServerCastSkipVote(ulong voterId)
    {
        HandleVoteSkip(voterId);
    }

    /// <summary>Server chat: restart current track for everyone.</summary>
    public static void ServerRestart()
    {
        RestartCurrentTrack();
    }

    private static void HandleVoteSkip(ulong voterId)
    {
        if (!serverReady || string.IsNullOrEmpty(currentTrackId))
            return;

        skipVotes.Add(voterId);
        int need = VoteNeed;
        int have = skipVotes.Count;
        FlamieLog.Info("[FlamiePrac] RadioSync skip vote " + have + "/" + need + " from client " + voterId);

        if (have >= need)
        {
            AdvanceToNextTrack(first: false);
            BroadcastState();
            FlamieLog.Info("[FlamiePrac] RadioSync skip passed → '" + currentTrackId + "'");
            return;
        }

        BroadcastState();
    }

    private static void HandleVoteRestart(ulong voterId)
    {
        if (!serverReady || string.IsNullOrEmpty(currentTrackId))
            return;

        restartVotes.Add(voterId);
        int need = VoteNeed;
        int have = restartVotes.Count;
        FlamieLog.Info("[FlamiePrac] RadioSync restart vote " + have + "/" + need + " from client " + voterId);

        if (have >= need)
        {
            RestartCurrentTrack();
            FlamieLog.Info("[FlamiePrac] RadioSync restart passed → '" + currentTrackId + "'");
            return;
        }

        BroadcastState();
    }

    private static void TryAutoAdvance(NetworkManager nm)
    {
        if (string.IsNullOrEmpty(currentTrackId) || !clockStarted)
            return;

        double elapsed = nm.ServerTime.Time - trackStartServerTime;
        if (elapsed < 0.0)
            return;

        if (!durations.TryGetValue(currentTrackId, out float duration))
        {
            if (elapsed < StuckWithoutDurationSeconds)
                return;

            AdvanceToNextTrack(first: false);
            BroadcastState();
            FlamieLog.Warn("[FlamiePrac] RadioSync auto-advance (no duration after " +
                           elapsed.ToString("0.0") + "s) → '" + currentTrackId + "'");
            return;
        }

        if (elapsed + 0.05 < duration)
            return;

        AdvanceToNextTrack(first: false);
        BroadcastState();
        FlamieLog.Info("[FlamiePrac] RadioSync auto-advance → '" + currentTrackId + "'");
    }

    /// <summary>
    /// Prefer longer estimates while a track plays — partial streams and loop glitches report short lengths.
    /// Shorter corrections apply only once server elapsed time confirms the new end.
    /// </summary>
    private static bool TryStoreDuration(string trackId, float duration)
    {
        if (string.IsNullOrEmpty(trackId) || duration <= 0.05f)
            return false;

        if (!durations.TryGetValue(trackId, out float existing))
        {
            durations[trackId] = duration;
            return true;
        }

        if (duration > existing + 0.5f)
        {
            durations[trackId] = duration;
            return true;
        }

        if (duration + 0.75f < existing)
        {
            if (clockStarted && trackId == currentTrackId)
            {
                NetworkManager nm = NetworkManager.Singleton;
                double elapsed = nm != null ? nm.ServerTime.Time - trackStartServerTime : 0.0;
                if (elapsed + 1.0 < duration)
                    return false;
                if (elapsed + 2.0 < existing - MinTrackSecondsBeforeEnd)
                    return false;
            }

            durations[trackId] = duration;
            return true;
        }

        return false;
    }

    private static void BroadcastState()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm?.CustomMessagingManager == null || !nm.IsServer)
            return;

        if (string.IsNullOrEmpty(currentTrackId))
            return;

        using (FastBufferWriter writer = BuildStateWriter())
        {
            nm.CustomMessagingManager.SendNamedMessageToAll(
                "FlamiePrac_Radio",
                writer,
                NetworkDelivery.Reliable);
        }

        // Host plays audio locally too.
        if (nm.IsClient && RadioController.Instance != null)
        {
            durations.TryGetValue(currentTrackId, out float duration);
            RadioController.Instance.ApplyServerState(
                currentTrackId,
                trackStartServerTime,
                duration,
                (ushort)Mathf.Clamp(VoteCount, 0, ushort.MaxValue),
                (ushort)Mathf.Clamp(RestartVoteCount, 0, ushort.MaxValue),
                (ushort)Mathf.Clamp(VoteNeed, 0, ushort.MaxValue),
                clockStarted);
        }

        try
        {
            StateChanged?.Invoke();
        }
        catch { }
    }

    private static FastBufferWriter BuildStateWriter()
    {
        durations.TryGetValue(currentTrackId, out float duration);
        byte[] idBytes = Encoding.UTF8.GetBytes(currentTrackId ?? string.Empty);
        int size = 1 + 2 + 2 + 2 + 8 + 4 + 1 + 2 + idBytes.Length + 16;
        FastBufferWriter writer = new FastBufferWriter(size, Allocator.Temp);
        writer.WriteValueSafe(MsgState);
        writer.WriteValueSafe((ushort)Mathf.Clamp(VoteCount, 0, ushort.MaxValue));
        writer.WriteValueSafe((ushort)Mathf.Clamp(RestartVoteCount, 0, ushort.MaxValue));
        writer.WriteValueSafe((ushort)Mathf.Clamp(VoteNeed, 0, ushort.MaxValue));
        writer.WriteValueSafe(trackStartServerTime);
        writer.WriteValueSafe(duration);
        writer.WriteValueSafe((byte)(clockStarted ? FlagClockStarted : 0));
        WriteString(writer, currentTrackId);
        return writer;
    }

    private static void SendRequest(byte cmd)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsClient || nm.CustomMessagingManager == null)
            return;

        using (FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp))
        {
            writer.WriteValueSafe(cmd);
            nm.CustomMessagingManager.SendNamedMessage(
                "FlamiePrac_RadioRequest",
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.Reliable);
        }
    }

    private static int CountEligibleVoters()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return 1;

        int n = 0;
        foreach (ulong id in nm.ConnectedClientsIds)
        {
            // Dedicated server process is not a voter; listen-server host (IsClient) is.
            if (id == NetworkManager.ServerClientId && !nm.IsClient)
                continue;
            // AI goalies / traffic / passer bots must not inflate skip/restart majorities.
            if (FakePlayerDetector.IsAnyFakeClientId(id))
                continue;
            n++;
        }

        return Math.Max(1, n);
    }

    private static int MajorityNeed(int voters) => Utils.GetVoteMajority(Math.Max(1, voters));

    private static double NowServerTime()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null)
            return nm.ServerTime.Time;
        return Time.realtimeSinceStartupAsDouble;
    }

    private static void WriteString(FastBufferWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > 512)
        {
            byte[] cut = new byte[512];
            Buffer.BlockCopy(bytes, 0, cut, 0, 512);
            bytes = cut;
        }

        writer.WriteValueSafe((ushort)bytes.Length);
        writer.WriteBytesSafe(bytes, bytes.Length);
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

    /// <summary>Holds state if RadioController is not awake yet.</summary>
    public static class PendingClientState
    {
        public static bool Has;
        public static string TrackId;
        public static double StartServerTime;
        public static float Duration;
        public static ushort VoteCount;
        public static ushort RestartVoteCount;
        public static bool ClockStarted;
        public static ushort VoteNeed;

        public static void Store(
            string trackId, double start, float duration, ushort votes, ushort restartVotes, ushort need,
            bool clockStartedFlag)
        {
            Has = true;
            TrackId = trackId;
            StartServerTime = start;
            Duration = duration;
            VoteCount = votes;
            RestartVoteCount = restartVotes;
            VoteNeed = need;
            ClockStarted = clockStartedFlag;
        }

        public static void Clear()
        {
            Has = false;
        }
    }
}
