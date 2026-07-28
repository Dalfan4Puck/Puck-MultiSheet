using System;
using System.Collections;
using System.Collections.Generic;
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

    public const byte MsgState = 1;

    private const float StateResyncSeconds = 30f;
    private const float MinTrackSecondsBeforeEnd = 1.5f;

    private static readonly List<string> playlistIds = new List<string>();
    private static readonly List<string> shuffleOrder = new List<string>();
    private static readonly HashSet<ulong> skipVotes = new HashSet<ulong>();
    private static readonly HashSet<ulong> restartVotes = new HashSet<ulong>();
    private static readonly Dictionary<string, float> durations = new Dictionary<string, float>(StringComparer.Ordinal);

    private static string currentTrackId = string.Empty;
    private static double trackStartServerTime;
    private static int shuffleIndex;
    private static bool serverReady;
    private static bool fetchStarted;
    private static float nextStateBroadcastAt;
    private static string apiBase = RadioApiUtil.DefaultApiBase;

    public static string CurrentTrackId => currentTrackId;
    public static double TrackStartServerTime => trackStartServerTime;
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
        currentTrackId = string.Empty;
        trackStartServerTime = 0;
        shuffleIndex = 0;
        serverReady = false;
        fetchStarted = false;
        nextStateBroadcastAt = 0f;
    }

    public static void OnServerNetworkReady(MonoBehaviour host)
    {
        if (host == null || fetchStarted)
            return;

        fetchStarted = true;
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
                    BroadcastState();
                break;
            }
            case ReqTrackEnded:
            {
                string id = ReadString(reader);
                if (!string.IsNullOrEmpty(id) && id == currentTrackId)
                    TryAdvanceFromClientEndReport(nm);
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
            return;

        if (host == null)
            host = TrainingSync.Instance;
        if (host == null)
            return;

        host.gameObject.AddComponent<RadioController>();
        if (host.GetComponent<RadioHudDriver>() == null)
            host.gameObject.AddComponent<RadioHudDriver>();
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

        if (string.IsNullOrEmpty(trackId))
            return;

        EnsureClientRadio(TrainingSync.Instance);

        if (RadioController.Instance != null)
            RadioController.Instance.ApplyServerState(
                trackId, startServerTime, duration, voteCount, restartVoteCount, voteNeed);
        else
            PendingClientState.Store(trackId, startServerTime, duration, voteCount, restartVoteCount, voteNeed);

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
        if (string.IsNullOrEmpty(trackId) || duration <= 0.5f)
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
        if (string.IsNullOrEmpty(trackId))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer)
        {
            TryAdvanceFromClientEndReport(nm);
            return;
        }

        if (nm == null || !nm.IsClient || nm.CustomMessagingManager == null)
            return;

        using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
        {
            writer.WriteValueSafe(ReqTrackEnded);
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
    }

    private static IEnumerator FetchPlaylistIds()
    {
        playlistIds.Clear();
        string url = apiBase.TrimEnd('/') + "/playlist";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            RadioApiUtil.ConfigureRequest(req);
            yield return req.SendWebRequest();

            string body = req.downloadHandler?.text;
            if (req.result == UnityWebRequest.Result.Success &&
                RadioApiUtil.TryParsePlaylist(body, playlistIds))
            {
                FlamieLog.Info("[FlamiePrac] RadioSync playlist via UWR: " + playlistIds.Count + " track(s).");
                yield break;
            }

            FlamieLog.Warn("[FlamiePrac] RadioSync UWR playlist failed: result=" + req.result +
                             " code=" + req.responseCode + " err=" + req.error +
                             " bodyLen=" + (body != null ? body.Length : 0));
        }

        // Dedicated Linux TLS often breaks UnityWebRequest — WebClient fallback.
        if (RadioApiUtil.TryDownloadString(url, out string downloaded, out string dlErr) &&
            RadioApiUtil.TryParsePlaylist(downloaded, playlistIds))
        {
            FlamieLog.Info("[FlamiePrac] RadioSync playlist via WebClient: " + playlistIds.Count + " track(s).");
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
        trackStartServerTime = NowServerTime();
        skipVotes.Clear();
        restartVotes.Clear();
    }

    private static void RestartCurrentTrack()
    {
        if (string.IsNullOrEmpty(currentTrackId))
            return;

        trackStartServerTime = NowServerTime();
        skipVotes.Clear();
        restartVotes.Clear();
        BroadcastState();
        FlamieLog.Info("[FlamiePrac] RadioSync restart '" + currentTrackId + "'");
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
        if (string.IsNullOrEmpty(currentTrackId))
            return;

        if (!durations.TryGetValue(currentTrackId, out float duration) || duration < MinTrackSecondsBeforeEnd)
            return;

        double elapsed = nm.ServerTime.Time - trackStartServerTime;
        if (elapsed + 0.05 >= duration)
        {
            AdvanceToNextTrack(first: false);
            BroadcastState();
            FlamieLog.Info("[FlamiePrac] RadioSync auto-advance → '" + currentTrackId + "'");
        }
    }

    private static void TryAdvanceFromClientEndReport(NetworkManager nm)
    {
        if (string.IsNullOrEmpty(currentTrackId))
            return;

        double elapsed = nm.ServerTime.Time - trackStartServerTime;
        float elapsedF = (float)elapsed;
        if (elapsedF < MinTrackSecondsBeforeEnd)
            return;

        if (durations.TryGetValue(currentTrackId, out float duration) && duration >= MinTrackSecondsBeforeEnd)
        {
            if (elapsedF + 0.75f < duration * 0.9f)
            {
                // Ended well before stored duration — inflated MP3 length; trust playback clock.
                durations[currentTrackId] = Mathf.Max(elapsedF, 1f);
                AdvanceToNextTrack(first: false);
                BroadcastState();
                FlamieLog.InfoThrottled("radio-advance",
                    "[FlamiePrac] RadioSync early end (corrected " + duration.ToString("0.0") +
                    "s → " + elapsedF.ToString("0.0") + "s) → '" + currentTrackId + "'");
                return;
            }
        }

        AdvanceToNextTrack(first: false);
        BroadcastState();
        FlamieLog.InfoThrottled("radio-advance", "[FlamiePrac] RadioSync end-report advance → '" + currentTrackId + "'");
    }

    /// <summary>
    /// Grow while streaming loads; accept shorter corrections when MP3 metadata over-estimates length.
    /// </summary>
    private static bool TryStoreDuration(string trackId, float duration)
    {
        if (string.IsNullOrEmpty(trackId) || duration <= 0.5f)
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
                (ushort)Mathf.Clamp(VoteNeed, 0, ushort.MaxValue));
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
        writer.WriteValueSafe((byte)0);
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
        public static ushort VoteNeed;

        public static void Store(
            string trackId, double start, float duration, ushort votes, ushort restartVotes, ushort need)
        {
            Has = true;
            TrackId = trackId;
            StartServerTime = start;
            Duration = duration;
            VoteCount = votes;
            RestartVoteCount = restartVotes;
            VoteNeed = need;
        }

        public static void Clear()
        {
            Has = false;
        }
    }
}
