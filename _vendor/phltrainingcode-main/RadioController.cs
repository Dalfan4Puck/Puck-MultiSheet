using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Single streamed 2D radio output — sync via CustomMessaging clock.
/// Speaker transforms only attenuate volume by distance (no per-speaker AudioSources).
/// Playlist + signed MP3 URLs come from phlstats <c>/radio/api</c> (private S3) only.
/// Dedicated server never loads audio.
/// </summary>
public class RadioController : MonoBehaviour
{
    public const byte CmdNext = 1;
    public const byte CmdPrev = 2;

    private const string PrefVolume = "FlamiePrac_RadioVolume";
    private const string PrefListening = "FlamiePrac_RadioListening";
    private const string PrefLastTrack = "FlamiePrac_RadioLastTrack";
    private const float DoublePrevWindowSeconds = 0.4f;
    private const float MinAutoAdvanceSeconds = 3f;
    private const float UrlRefreshSkewSeconds = 90f;
    /// <summary>Playlist poll interval — well under ~90 req/min/IP; never fetch every frame.</summary>
    private const float PlaylistRefreshSeconds = 300f;
    private const float SyncSeekToleranceSeconds = 1.0f;
    private const float SyncReseekIntervalSeconds = 3.0f;
    private const float SyncSeekVerifySeconds = 0.05f;
    private const float EndOfTrackEpsilonSeconds = 0.2f;
    /// <summary>Match former 3D speaker rolloff for fake near-speaker volume.</summary>
    private const float VolumeMinDistance = 4f;
    private const float DefaultSpeakerMaxDistance = 15f;

    public static RadioController Instance { get; private set; }

    public event Action StateChanged;

    public ushort SyncVoteCount { get; private set; }
    public ushort SyncRestartVoteCount { get; private set; }
    public ushort SyncVoteNeed { get; private set; }
    public bool IsSyncedPlayback { get; private set; }

    /// <summary>Client tune in/out (true radio). Server clock keeps advancing; tune-in re-seeks. Use volume for mute.</summary>
    public bool ListeningEnabled
    {
        get => listeningEnabled;
        set
        {
            if (listeningEnabled == value)
                return;
            listeningEnabled = value;
            PHLPracticeModPack.MultiSheetClientSettings.RadioListening = listeningEnabled;
            PHLPracticeModPack.MultiSheetClientSettings.Save();
            ApplyListeningState(seekIfOn: true);
            NotifyStateChanged();
        }
    }

    /// <summary>Speaker mesh transforms — used only for distance volume, not AudioSources.</summary>
    private readonly List<Transform> speakerAnchors = new List<Transform>();
    private readonly List<int> playHistory = new List<int>();
    private readonly List<int> shuffleOrder = new List<int>();
    private readonly Dictionary<string, float> verifiedDurations =
        new Dictionary<string, float>(StringComparer.Ordinal);

    private AudioSource playback;
    private float nextSpeakerRefreshTime;
    private float nextPlaybackRecoveryTime;
    private float nextDistanceVolumeTime;
    private float lastDistanceAttenuation = 1f;
    private bool listeningEnabled = false;
    private float nextEndReportRetryTime;
    private static Camera cachedListenerCamera;

    private sealed class TrackEntry
    {
        public string Id;
        public string Title;
        public string LocalPath;
        public string SignedUrl;
        public float UrlExpiresAtRealtime = -1f;
    }

    [Serializable]
    private sealed class ClientConfigFile
    {
        public string ApiBase = RadioApiUtil.DefaultApiBase;
    }

    [Serializable]
    private sealed class PlaylistResponse
    {
        public TrackDto[] tracks;
    }

    [Serializable]
    private sealed class TrackDto
    {
        public string id;
        public string title;
        public string url;
        public int expiresIn;
    }

    private TrackEntry[] tracks;
    private AudioClip[] clips;
    private string apiBase = RadioApiUtil.DefaultApiBase;
    private Coroutine playRoutine;
    private Coroutine playlistRefreshRoutine;
    private int playRequestGeneration;
    private bool usingRemoteApi;
    private float lastPlaylistFetchRealtime = -999f;
    private string syncedTrackId = string.Empty;
    private double syncedStartServerTime;
    private bool syncedClockStarted;
    private float syncedDuration;
    private float nextSyncSeekTime;
    private bool reportedDurationForCurrent;
    private bool reportedEndForCurrent;
    private float lastObservedPlaybackTime = -1f;
    private float lastPlaybackTimeChangeAt;
    private float lastPlaybackSampleTime = -1f;
    private const float PlaybackStallSeconds = 0.4f;

    private int currentSong;
    private int shuffleIndex;
    private int historyIndex;
    private bool advancingTrack;
    private bool trackWasPlaying;
    private bool libraryReady;
    private float storedVolume = 0.1f;
    private float lastPrevPressTime;
    private float currentTrackStartedAt;
    private Coroutine delayedRestartCoroutine;
    private Coroutine trackEndCoroutine;
    private Coroutine autoAdvanceCoroutine;
    private int trackPlayGeneration;
    private int pendingStartIndex = -1;
    private string reportedReadyTrackId = string.Empty;

    private AudioSource PrimarySource
    {
        get
        {
            EnsurePlaybackSource();
            return playback;
        }
    }

    public bool IsReady => libraryReady && tracks != null && tracks.Length > 0;

    public bool IsPlaying => listeningEnabled && PrimarySource != null && PrimarySource.isPlaying;

    public float Volume
    {
        get => storedVolume;
        set
        {
            storedVolume = Mathf.Clamp01(value);
            ApplyVolumeToOutputs();
            PHLPracticeModPack.MultiSheetClientSettings.RadioVolume = storedVolume;
            PHLPracticeModPack.MultiSheetClientSettings.Save();
            NotifyStateChanged();
        }
    }

    private void ApplyVolumeToOutputs()
    {
        EnsurePlaybackSource();
        if (playback == null)
            return;

        // Volume slider mutes output; tune On/Off stops streaming via ApplyListeningState.
        playback.volume = listeningEnabled ? storedVolume * lastDistanceAttenuation : 0f;
    }

    private void LateUpdate()
    {
        // 10 Hz is enough for distance volume — avoid Camera.main + distance every render frame.
        if (Time.unscaledTime >= nextDistanceVolumeTime)
        {
            nextDistanceVolumeTime = Time.unscaledTime + 0.1f;
            UpdateDistanceVolume();
        }

        TryRefreshSpeakerAnchors();
        TryRecoverPlayback();
    }

    private void UpdateDistanceVolume()
    {
        if (playback == null)
            return;

        PruneDeadSpeakerAnchors();
        lastDistanceAttenuation = ComputeNearestSpeakerAttenuation();
        ApplyVolumeToOutputs();
    }

    private float ComputeNearestSpeakerAttenuation()
    {
        if (TryGetPlayEverywhere(out bool everywhere) && everywhere)
            return 1f;

        PruneDeadSpeakerAnchors();
        if (speakerAnchors.Count == 0)
            return 0f;

        float maxDistance = TryGetSpeakerMaxDistance(out float maxDist)
            ? maxDist
            : DefaultSpeakerMaxDistance;

        Vector3 listener = GetListenerWorldPosition();
        float best = float.MaxValue;
        for (int i = 0; i < speakerAnchors.Count; i++)
        {
            Transform anchor = speakerAnchors[i];
            if (anchor == null)
                continue;

            float d = Vector3.Distance(listener, anchor.position);
            if (d < best)
                best = d;
        }

        if (best == float.MaxValue)
            return 0f;
        if (best <= VolumeMinDistance)
            return 1f;
        if (best >= maxDistance)
            return 0f;

        // Smooth falloff approximating former log rolloff inside the configured range.
        float t = (best - VolumeMinDistance) / (maxDistance - VolumeMinDistance);
        return Mathf.Clamp01(1f - Mathf.Log10(1f + 9f * t));
    }

    private static bool TryGetPlayEverywhere(out bool everywhere)
    {
        everywhere = false;
        try
        {
            everywhere = PHLPracticeModPack.MultiSheetClientSettings.RadioPlayEverywhere;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetSpeakerMaxDistance(out float maxDistance)
    {
        maxDistance = DefaultSpeakerMaxDistance;
        try
        {
            maxDistance = PHLPracticeModPack.MultiSheetClientSettings.RadioSpeakerRange;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Vector3 GetListenerWorldPosition()
    {
        if (cachedListenerCamera == null || !cachedListenerCamera.isActiveAndEnabled)
            cachedListenerCamera = Camera.main;

        if (cachedListenerCamera != null)
            return cachedListenerCamera.transform.position;

        return Vector3.zero;
    }

    private void TryRefreshSpeakerAnchors()
    {
        if (Time.unscaledTime < nextSpeakerRefreshTime)
            return;

        nextSpeakerRefreshTime = Time.unscaledTime + 5f;

        if (speakerAnchors.Count > 0)
            return;

        RefreshSpeakerAnchorsFromScene();
    }

    private void TryRecoverPlayback()
    {
        // Synced playback is server-authored — never locally auto-advance (causes loops/desync).
        if (IsSyncedPlayback || !libraryReady || !listeningEnabled || tracks == null || tracks.Length == 0)
            return;

        if (Time.unscaledTime < nextPlaybackRecoveryTime)
            return;

        if (IsAnyOutputPlaying())
            return;

        if (!trackWasPlaying)
            return;

        float idleFor = Time.unscaledTime - currentTrackStartedAt;
        if (idleFor < MinAutoAdvanceSeconds)
            return;

        nextPlaybackRecoveryTime = Time.unscaledTime + 1f;
        EnsurePlaybackSource();

        if (AllOutputsIdle())
            AutoAdvanceToNextTrack();
    }

    /// <summary>
    /// Re-scan speaker anchors under known Flamie roots only (never full-scene FindObjects).
    /// </summary>
    private void RefreshSpeakerAnchorsFromScene()
    {
        PruneDeadSpeakerAnchors();

        if (speakerAnchors.Count > 0)
            return;

        Transform clientRoot = TrainingSync.Instance != null ? TrainingSync.Instance.ClientVisualRoot : null;
        if (clientRoot != null)
            RegisterSpeakersUnder(clientRoot);

        TrainingObjectManager tom = TrainingObjectManager.Instance;
        if (tom != null)
        {
            var roots = new System.Collections.Generic.List<Transform>(16);
            tom.CollectCullRoots(roots);
            for (int i = 0; i < roots.Count; i++)
                RegisterSpeakersUnder(roots[i]);
        }
    }

    private void RegisterSpeakersUnder(Transform root)
    {
        if (root == null)
            return;

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform t = transforms[i];
            if (t == null || !TrainingPrefabNames.IsSpeakerName(t.name))
                continue;

            if (t.GetComponentsInChildren<Renderer>(true).Length == 0)
                continue;

            RegisterSpeaker(t.gameObject);
        }
    }

    public float Progress01
    {
        get
        {
            float len = GetEffectivePlaybackLength();
            if (len <= 0f)
                return 0f;

            float t = IsSyncedPlayback ? ComputeServerSeekSeconds() : (PrimarySource != null ? PrimarySource.time : 0f);
            return Mathf.Clamp01(t / len);
        }
    }

    public string CurrentTrackTitle
    {
        get
        {
            if (tracks == null || tracks.Length == 0 || currentSong < 0 || currentSong >= tracks.Length)
                return string.Empty;

            TrackEntry t = tracks[currentSong];
            return t != null ? (t.Title ?? string.Empty) : string.Empty;
        }
    }

    public string NextTrackTitle
    {
        get
        {
            if (IsSyncedPlayback)
            {
                if (SyncVoteNeed <= 0)
                    return "Skip: vote";
                return "Skip " + SyncVoteCount + "/" + SyncVoteNeed;
            }

            if (tracks == null || tracks.Length == 0)
                return string.Empty;

            if (tracks.Length == 1)
                return tracks[0]?.Title ?? string.Empty;

            return "Shuffled";
        }
    }

    public string RestartVoteTitle
    {
        get
        {
            if (!IsSyncedPlayback)
                return "Restart";
            if (SyncVoteNeed <= 0)
                return "Restart: vote";
            return "Restart " + SyncRestartVoteCount + "/" + SyncVoteNeed;
        }
    }

    public string TimeText
    {
        get
        {
            float len = GetEffectivePlaybackLength();
            float t = IsSyncedPlayback
                ? ComputeServerSeekSeconds()
                : (PrimarySource != null ? PrimarySource.time : 0f);

            if (len <= 0f && (PrimarySource == null || PrimarySource.clip == null))
                return "0:00 / 0:00";

            return FormatTime(t) + " / " + FormatTime(len);
        }
    }

    public string StatusMessage { get; private set; } = string.Empty;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        storedVolume = PHLPracticeModPack.MultiSheetClientSettings.RadioVolume;
        listeningEnabled = PHLPracticeModPack.MultiSheetClientSettings.RadioListening;
    }

    public static void ApplyClientPreferencesFromMultiSheet()
    {
        if (Instance == null)
            return;

        Instance.storedVolume = PHLPracticeModPack.MultiSheetClientSettings.RadioVolume;
        Instance.listeningEnabled = PHLPracticeModPack.MultiSheetClientSettings.RadioListening;
        Instance.ApplyVolumeToOutputs();
        Instance.ApplyListeningState(seekIfOn: false);
    }

    /// <summary>Stop audio and clear speaker anchors on disconnect; keep component for reconnect.</summary>
    public static void StopSessionPlayback()
    {
        if (Instance == null)
            return;

        Instance.PrepareForDestroy();
        Instance.IsSyncedPlayback = false;
        Instance.syncedTrackId = string.Empty;
        Instance.syncedClockStarted = false;
    }

    private void OnDestroy()
    {
        if (Instance != this)
            return;

        PrepareForDestroy();
        Instance = null;
    }

    public void RegisterSpeaker(GameObject speakerGo)
    {
        if (speakerGo == null)
            return;

        Transform anchor = speakerGo.transform;
        PruneDeadSpeakerAnchors();

        for (int i = 0; i < speakerAnchors.Count; i++)
        {
            if (speakerAnchors[i] == anchor)
                return;
        }

        speakerAnchors.Add(anchor);
        FlamieLog.Setup("[FlamiePrac] Radio speaker anchor #" + speakerAnchors.Count + " '" + speakerGo.name +
                       "' (2D streamed playback + distance volume).");

        EnsurePlaybackSource();
        SyncPlaybackToCurrentTrack();
        TryStartPlayback();
    }

    /// <summary>
    /// Clear speaker anchors and stop the single playback source (keep playlist/clips).
    /// Call before hive rebuild.
    /// </summary>
    public void ClearSpeakerOutputs()
    {
        speakerAnchors.Clear();
        lastDistanceAttenuation = 1f;

        if (playback == null)
            return;

        try
        {
            if (playback.isPlaying)
                playback.Stop();
            playback.clip = null;
        }
        catch
        {
            // Unity/FMOD may already have recycled the channel during teardown.
        }
    }

    private void EnsurePlaybackSource()
    {
        if (playback != null)
            return;

        GameObject host = new GameObject("FlamiePrac_RadioOut");
        host.transform.SetParent(transform, false);
        playback = host.AddComponent<AudioSource>();
        ConfigurePlaybackAudio(playback);
        playback.volume = storedVolume * lastDistanceAttenuation;
        FlamieLog.InfoOnce("radio-2d",
            "[FlamiePrac] Radio using single streamed 2D AudioSource (distance volume from speaker anchors).");
    }

    public void PrepareForDestroy()
    {
        CancelDelayedRestart();
        CancelTrackEndWatch();
        CancelPlayRoutine();
        CancelPlaylistRefresh();

        ClearSpeakerOutputs();
        if (playback != null)
        {
            if (playback.gameObject != null)
                Destroy(playback.gameObject);
            playback = null;
        }

        libraryReady = false;
    }

    private bool clientBootstrapStarted;

    private IEnumerator Start()
    {
        if (Application.isBatchMode)
            yield break;

        try
        {
            Unity.Netcode.NetworkManager nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.IsServer && !nm.IsClient)
                yield break;

            // MultiSheet pure clients wait for the first server radio snapshot.
            if (FlamiePracFeatures.RadioServerDrivenOnly && nm != null && nm.IsClient && !nm.IsServer)
            {
                SetStatus("Waiting for server radio…");
                yield break;
            }
        }
        catch { }

        yield return StartLocalOrSyncedRadio();
    }

    private IEnumerator StartLocalOrSyncedRadio()
    {
        ApplyVolumeToOutputs();
        LoadClientConfig();

        SetStatus("Loading playlist…");
        yield return LoadPlaylist();

        if (tracks == null || tracks.Length == 0)
        {
            FlamieLog.Error("[FlamiePrac] Radio has no tracks — phlstats /playlist unavailable.");
            SetStatus("No radio tracks");
            yield break;
        }

        clips = new AudioClip[tracks.Length];
        libraryReady = true;
        if (usingRemoteApi)
            StartPlaylistRefreshLoop();

        // Networked servers own the clock — wait briefly for RadioSync (late join OK).
        if (RadioSync.IsNetworkSynced)
        {
            IsSyncedPlayback = true;
            SetStatus("Syncing radio…");
            RadioSync.ClientRequestState();
            if (RadioSync.PendingClientState.Has)
            {
                ApplyServerState(
                    RadioSync.PendingClientState.TrackId,
                    RadioSync.PendingClientState.StartServerTime,
                    RadioSync.PendingClientState.Duration,
                    RadioSync.PendingClientState.VoteCount,
                    RadioSync.PendingClientState.RestartVoteCount,
                    RadioSync.PendingClientState.VoteNeed,
                    RadioSync.PendingClientState.ClockStarted);
                RadioSync.PendingClientState.Clear();
            }

            float waitUntil = Time.realtimeSinceStartup + 8f;
            while (Time.realtimeSinceStartup < waitUntil && string.IsNullOrEmpty(syncedTrackId))
                yield return null;

            if (!string.IsNullOrEmpty(syncedTrackId))
            {
                FlamieLog.Info("[FlamiePrac] Radio client synced (" + tracks.Length + " track metas).");
                yield break;
            }

            if (FlamiePracFeatures.RadioServerDrivenOnly)
            {
                SetStatus("Waiting for server radio…");
                yield break;
            }

            // Server playlist/sync idle — play locally so the rink isn't silent.
            FlamieLog.Warn("[FlamiePrac] Radio sync unavailable — falling back to local shuffle.");
            IsSyncedPlayback = false;
        }

        if (FlamiePracFeatures.RadioServerDrivenOnly)
            yield break;

        // Offline / no Netcode / sync timeout: local shuffle.
        IsSyncedPlayback = false;
        int lastTrack = PlayerPrefs.GetInt(PrefLastTrack, -1);
        BuildShuffleOrder(lastTrack);

        int first = shuffleOrder[shuffleIndex++];
        playHistory.Clear();
        playHistory.Add(first);
        historyIndex = 0;
        currentSong = first;
        pendingStartIndex = first;
        FlamieLog.Info("[FlamiePrac] Radio local shuffle start → #" + first + " '" + tracks[first].Title + "'");
        SetStatus(string.Empty);
        TryStartPlayback(firstIndex: first);
    }

    /// <summary>Apply authoritative radio snapshot from the server.</summary>
    public void ApplyServerState(
        string trackId,
        double startServerTime,
        float duration,
        ushort voteCount,
        ushort restartVoteCount,
        ushort voteNeed,
        bool clockStarted)
    {
        if (Application.isBatchMode || string.IsNullOrEmpty(trackId))
            return;

        IsSyncedPlayback = true;
        SyncVoteCount = voteCount;
        SyncRestartVoteCount = restartVoteCount;
        SyncVoteNeed = voteNeed;

        bool trackChanged = !string.Equals(syncedTrackId, trackId, StringComparison.Ordinal);
        bool clockJustStarted = clockStarted && !syncedClockStarted;

        syncedStartServerTime = startServerTime;
        syncedClockStarted = clockStarted;
        if (trackChanged)
        {
            syncedDuration = ResolveDurationForTrack(trackId, duration);
        }
        else if (duration > syncedDuration + 0.5f)
            syncedDuration = duration;
        else if (duration > 0.5f && duration + 0.75f < syncedDuration)
            syncedDuration = duration;
        else if (duration > 0.5f && syncedDuration < 0.5f)
            syncedDuration = duration;

        syncedTrackId = trackId;

        if (!libraryReady || tracks == null)
        {
            RadioSync.PendingClientState.Store(
                trackId, startServerTime, duration, voteCount, restartVoteCount, voteNeed, clockStarted);
            EnsureClientBootstrapFromServer();
            NotifyStateChanged();
            return;
        }

        int index = FindTrackIndex(trackId);
        if (index < 0)
        {
            SetStatus("Unknown track: " + trackId);
            NotifyStateChanged();
            StartCoroutine(RefreshPlaylistThenPlay(trackId));
            return;
        }

        if (trackChanged)
        {
            CancelAutoAdvanceDelay();
            reportedDurationForCurrent = false;
            reportedEndForCurrent = false;
            nextEndReportRetryTime = 0f;
            lastObservedPlaybackTime = -1f;
            lastPlaybackSampleTime = -1f;
            reportedReadyTrackId = string.Empty;
            syncedDuration = ResolveDurationForTrack(trackId, duration);
            currentSong = index;
            pendingStartIndex = -1;
            EnsurePlaybackSource();
            if (playback != null)
            {
                playback.Pause();
                playback.clip = null;
            }
            RequestPlay(index, recordHistory: false);
        }
        else if (!clockStarted)
        {
            reportedReadyTrackId = string.Empty;
            SetStatus("Preparing…");
            if (IsPlaybackClipForSyncedTrack())
            {
                EnsurePlaybackSource();
                if (playback != null)
                {
                    playback.time = 0f;
                    playback.Pause();
                }
                ReportTrackReadyIfNeeded();
            }
            else
            {
                RequestPlay(index, recordHistory: false);
            }
        }
        else if (clockJustStarted)
        {
            reportedEndForCurrent = false;
            nextEndReportRetryTime = 0f;
            nextSyncSeekTime = 0f;
            SetStatus(string.Empty);
            TrySyncSeek(force: true);
            ApplyListeningState(seekIfOn: true);
        }
        else
        {
            TrySyncSeek(force: false);
        }

        if (clockStarted)
            SetStatus(string.Empty);
        NotifyStateChanged();
    }

    private void EnsureClientBootstrapFromServer()
    {
        if (clientBootstrapStarted || libraryReady)
            return;

        clientBootstrapStarted = true;
        StartCoroutine(BootstrapFromServer());
    }

    private IEnumerator BootstrapFromServer()
    {
        SetStatus("Loading playlist…");
        yield return LoadPlaylist();

        if (tracks == null || tracks.Length == 0)
        {
            FlamieLog.Error("[FlamiePrac] Radio has no tracks — phlstats /playlist unavailable.");
            SetStatus("No radio tracks");
            yield break;
        }

        clips = new AudioClip[tracks.Length];
        libraryReady = true;
        if (usingRemoteApi)
            StartPlaylistRefreshLoop();

        IsSyncedPlayback = true;

        if (RadioSync.PendingClientState.Has)
        {
            ApplyServerState(
                RadioSync.PendingClientState.TrackId,
                RadioSync.PendingClientState.StartServerTime,
                RadioSync.PendingClientState.Duration,
                RadioSync.PendingClientState.VoteCount,
                RadioSync.PendingClientState.RestartVoteCount,
                RadioSync.PendingClientState.VoteNeed,
                RadioSync.PendingClientState.ClockStarted);
            RadioSync.PendingClientState.Clear();
        }
    }

    private IEnumerator RefreshPlaylistThenPlay(string trackId)
    {
        yield return RefreshPlaylistMerge();
        int index = FindTrackIndex(trackId);
        if (index < 0)
            yield break;

        reportedDurationForCurrent = false;
        reportedEndForCurrent = false;
        currentSong = index;
        RequestPlay(index, recordHistory: false);
    }

    private int FindTrackIndex(string trackId)
    {
        if (tracks == null || string.IsNullOrEmpty(trackId))
            return -1;

        for (int i = 0; i < tracks.Length; i++)
        {
            if (tracks[i] != null && tracks[i].Id == trackId)
                return i;
        }

        return -1;
    }

    private float ComputeServerSeekSeconds()
    {
        NetworkManager nm = NetworkManager.Singleton;
        double now = nm != null ? nm.ServerTime.Time : Time.realtimeSinceStartupAsDouble;
        float seek = (float)(now - syncedStartServerTime);
        if (seek < 0f)
            return seek;

        if (syncedDuration > 0.05f && seek > syncedDuration)
            seek = syncedDuration;

        float effective = GetEffectivePlaybackLength();
        if (effective > 0.05f && seek > effective)
            seek = effective;

        return seek;
    }

    private float GetLoadedClipEndPosition()
    {
        if (playback == null || playback.clip == null || playback.clip.length <= 0.05f)
            return -1f;

        return Mathf.Max(0f, playback.clip.length - EndOfTrackEpsilonSeconds);
    }

    /// <summary>
    /// Unity non-looping AudioSources reset time to 0 at clip end and can blip the intro.
    /// Hold at the end position before that happens.
    /// </summary>
    private bool HoldPlaybackAtEndIfNeeded()
    {
        if (!IsSyncedPlayback || playback == null || playback.clip == null)
            return false;

        if (!IsPlaybackClipForSyncedTrack())
            return false;

        if (IsWaitingForServerStart())
            return false;

        float endPos = GetLoadedClipEndPosition();
        if (endPos < 0f)
            return false;

        float seek = ComputeServerSeekSeconds();
        if (seek < 0f)
            return false;

        bool wrappedToStart = playback.time < 0.35f && seek > endPos * 0.5f;
        bool atClipEnd = playback.time >= endPos - 0.03f;
        bool serverPastClip = seek >= endPos;

        if (!wrappedToStart && !atClipEnd && !serverPastClip)
            return false;

        if (playback.isPlaying)
            playback.Pause();

        if (wrappedToStart || playback.time > endPos + 0.02f)
            playback.time = endPos;

        ReportTrackEndedIfNeeded(forceRetry: wrappedToStart);
        lastPlaybackSampleTime = -1f;
        lastObservedPlaybackTime = -1f;
        return true;
    }

    private bool IsWaitingForServerStart()
    {
        return IsSyncedPlayback && !syncedClockStarted;
    }

    private void ReportTrackReadyIfNeeded()
    {
        if (!IsSyncedPlayback || syncedClockStarted || string.IsNullOrEmpty(syncedTrackId))
            return;

        if (!IsPlaybackClipForSyncedTrack())
            return;

        if (reportedReadyTrackId == syncedTrackId)
            return;

        reportedReadyTrackId = syncedTrackId;
        RadioSync.ClientReportTrackReady(syncedTrackId);
    }

    private void TrySyncSeek(bool force)
    {
        if (!IsSyncedPlayback || !listeningEnabled)
            return;

        if (!force && Time.unscaledTime < nextSyncSeekTime)
            return;

        nextSyncSeekTime = Time.unscaledTime + SyncReseekIntervalSeconds;

        AudioSource primary = PrimarySource;
        if (primary == null || primary.clip == null)
            return;

        // While the next track is streaming in, never touch the previous clip.
        if (playRoutine != null)
            return;

        if (!IsPlaybackClipForSyncedTrack())
            return;

        if (IsWaitingForServerStart())
        {
            if (primary.isPlaying)
                primary.Pause();
            return;
        }

        float seek = ComputeServerSeekSeconds();
        if (seek < 0f)
        {
            if (primary.isPlaying)
                primary.Pause();
            return;
        }

        float len = GetEffectivePlaybackLength();
        if (len <= 0.05f)
            return;

        // Never seek past the loaded clip — wrong server duration causes short clips to loop.
        if (primary.clip != null && primary.clip.length > 0.05f)
            seek = Mathf.Min(seek, Mathf.Max(0f, primary.clip.length - EndOfTrackEpsilonSeconds));

        // Past end of clip: pause locally; server auto-advance owns the playlist clock.
        if (seek >= len - EndOfTrackEpsilonSeconds)
        {
            HoldPlaybackAtEndIfNeeded();
            return;
        }

        float clipEndPos = GetLoadedClipEndPosition();
        if (clipEndPos > 0f)
        {
            // Unity wraps time to 0 at clip end — never re-play from the intro.
            if (primary.time < 0.35f && seek > clipEndPos * 0.5f)
            {
                HoldPlaybackAtEndIfNeeded();
                return;
            }

            if (primary.time >= clipEndPos - 0.03f || seek >= clipEndPos)
            {
                HoldPlaybackAtEndIfNeeded();
                return;
            }
        }

        bool nearEnd = clipEndPos > 0f &&
                       (primary.time >= clipEndPos - 0.15f || seek >= len - EndOfTrackEpsilonSeconds);
        if (!force && !nearEnd && primary.isPlaying &&
            Mathf.Abs(primary.time - seek) <= SyncSeekToleranceSeconds)
            return;

        // Tune-in / forced snap: Pause first so Unity does not resume from the local pause point.
        if (force)
            primary.Pause();

        primary.time = seek;
        if (!primary.isPlaying)
            primary.Play();

        if (Mathf.Abs(primary.time - seek) > SyncSeekVerifySeconds)
            primary.time = seek;
    }

    private void ApplyListeningState(bool seekIfOn)
    {
        EnsurePlaybackSource();
        if (playback == null)
            return;

        ApplyVolumeToOutputs();

        if (!listeningEnabled)
        {
            if (playback.isPlaying)
                playback.Pause();
            return;
        }

        if (IsSyncedPlayback)
        {
            if (playback.clip == null || !IsPlaybackClipForSyncedTrack())
            {
                if (playback.isPlaying)
                    playback.Pause();

                if (!string.IsNullOrEmpty(syncedTrackId))
                {
                    int index = FindTrackIndex(syncedTrackId);
                    if (index >= 0)
                        RequestPlay(index, recordHistory: false);
                }
                return;
            }

            float seek = ComputeServerSeekSeconds();
            float len = GetEffectivePlaybackLength();
            if (len > 0.05f && seek >= len - EndOfTrackEpsilonSeconds)
            {
                playback.Pause();
                return;
            }

            if (seekIfOn)
            {
                nextSyncSeekTime = 0f;
                TrySyncSeek(force: true);
            }
            else if (!playback.isPlaying)
            {
                nextSyncSeekTime = 0f;
                TrySyncSeek(force: true);
            }
            return;
        }

        if (playback.clip == null)
            return;

        if (!playback.isPlaying)
        {
            playback.time = 0f;
            playback.Play();
        }
    }

    private void ReportTrackEndedIfNeeded(bool forceRetry)
    {
        if (!listeningEnabled)
            return;

        if (!IsSyncedPlayback || string.IsNullOrEmpty(syncedTrackId))
            return;

        if (reportedEndForCurrent && !forceRetry)
            return;

        if (forceRetry && reportedEndForCurrent && Time.unscaledTime < nextEndReportRetryTime)
            return;

        reportedEndForCurrent = true;
        nextEndReportRetryTime = Time.unscaledTime + 2f;
        RadioSync.ClientReportTrackEnded(syncedTrackId);
    }

    private void MaybeReportEndFromServerClock()
    {
        if (!IsSyncedPlayback || string.IsNullOrEmpty(syncedTrackId))
            return;

        if (IsWaitingForServerStart())
            return;

        float seek = ComputeServerSeekSeconds();
        float len = GetEffectivePlaybackLength();

        if (len <= 0.5f)
            return;

        if (seek + EndOfTrackEpsilonSeconds < len)
            return;

        if (playback != null && playback.isPlaying)
            playback.Pause();

        // Report even while tuned out so the server playlist keeps advancing.
        if (listeningEnabled)
            ReportTrackEndedIfNeeded(forceRetry: true);
    }

    private void MaybeReportVerifiedClipDuration()
    {
        if (!listeningEnabled)
            return;

        if (!IsSyncedPlayback || string.IsNullOrEmpty(syncedTrackId))
            return;

        if (!TryGetSyncedClipLength(out float clipLen))
            return;

        RememberVerifiedDuration(syncedTrackId, clipLen);

        if (reportedDurationForCurrent && Mathf.Abs(syncedDuration - clipLen) < 0.5f)
            return;

        reportedDurationForCurrent = true;
        syncedDuration = clipLen;
        RadioSync.ClientReportDuration(syncedTrackId, clipLen);
    }

    private void MaybeDetectPlaybackLoop()
    {
        if (!listeningEnabled || playback == null || playback.clip == null || !playback.isPlaying)
        {
            lastPlaybackSampleTime = -1f;
            return;
        }

        if (!IsPlaybackClipForSyncedTrack())
        {
            lastPlaybackSampleTime = -1f;
            return;
        }

        float t = playback.time;
        if (lastPlaybackSampleTime > 0.75f && t + 0.35f < lastPlaybackSampleTime)
        {
            float actualLen = lastPlaybackSampleTime;
            RememberVerifiedDuration(syncedTrackId, actualLen);
            syncedDuration = actualLen;
            reportedDurationForCurrent = true;
            RadioSync.ClientReportDuration(syncedTrackId, actualLen);
            HoldPlaybackAtEndIfNeeded();
            lastPlaybackSampleTime = -1f;
            lastObservedPlaybackTime = -1f;
            return;
        }

        lastPlaybackSampleTime = t;
    }

    /// <summary>HUD open: refresh playlist if the last fetch is stale (rate-limit safe).</summary>
    public void RequestPlaylistRefreshFromHud()
    {
        if (!usingRemoteApi || !libraryReady)
            return;

        if (Time.realtimeSinceStartup - lastPlaylistFetchRealtime < 30f)
            return;

        StartCoroutine(RefreshPlaylistMerge());
    }

    private void LoadClientConfig()
    {
        apiBase = RadioApiUtil.DefaultApiBase;

        try
        {
            string dllFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string path = Path.Combine(dllFolder, "config", "radio_client.json");
            if (!File.Exists(path))
                path = Path.Combine(dllFolder, "config", "radio_client.example.json");

            // Prefer game-cwd config/ (same pattern as multi_rink / training_layout).
            string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "config", "radio_client.json");
            if (File.Exists(cwdPath))
                path = cwdPath;

            if (!File.Exists(path))
                return;

            ClientConfigFile cfg = JsonUtility.FromJson<ClientConfigFile>(File.ReadAllText(path));
            if (cfg == null)
                return;

            if (!string.IsNullOrWhiteSpace(cfg.ApiBase))
                apiBase = cfg.ApiBase.Trim().TrimEnd('/');
            FlamieLog.Info("[FlamiePrac] Radio API base=" + apiBase);
        }
        catch (Exception ex)
        {
            FlamieLog.Warn("[FlamiePrac] Radio client config load failed: " + ex.Message);
        }
    }

    private IEnumerator LoadPlaylist()
    {
        usingRemoteApi = false;
        bool apiOk = false;
        yield return FetchRemotePlaylist(ok => apiOk = ok);

        if (apiOk)
            usingRemoteApi = true;
    }

    private IEnumerator FetchRemotePlaylist(Action<bool> done)
    {
        string url = apiBase + "/playlist";
        var ids = new List<string>();
        var titles = new List<string>();

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            RadioApiUtil.ConfigureRequest(req);
            yield return req.SendWebRequest();
            lastPlaylistFetchRealtime = Time.realtimeSinceStartup;

            string json = req.downloadHandler?.text;
            if (req.result == UnityWebRequest.Result.Success &&
                RadioApiUtil.TryParsePlaylist(json, ids, titles))
            {
                ApplyParsedPlaylist(ids, titles);
                done(true);
                yield break;
            }

            FlamieLog.Warn("[FlamiePrac] Radio playlist UWR failed: result=" + req.result +
                             " code=" + req.responseCode + " err=" + req.error +
                             " (" + url + ")");
        }

        if (RadioApiUtil.TryDownloadString(url, out string body, out string err) &&
            RadioApiUtil.TryParsePlaylist(body, ids, titles))
        {
            lastPlaylistFetchRealtime = Time.realtimeSinceStartup;
            ApplyParsedPlaylist(ids, titles);
            done(true);
            yield break;
        }

        if (!string.IsNullOrEmpty(err))
            FlamieLog.Warn("[FlamiePrac] Radio playlist WebClient failed: " + err);

        // Last resort: same file the dedicated server may use.
        if (RadioApiUtil.TryLoadPlaylistFile(ids, titles))
        {
            ApplyParsedPlaylist(ids, titles);
            done(true);
            yield break;
        }

        done(false);
    }

    private void ApplyParsedPlaylist(List<string> ids, List<string> titles)
    {
        tracks = new TrackEntry[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            string title = (titles != null && i < titles.Count && !string.IsNullOrWhiteSpace(titles[i]))
                ? titles[i]
                : id;
            tracks[i] = new TrackEntry { Id = id, Title = title };
        }

        FlamieLog.InfoOnce("radio-playlist", "[FlamiePrac] Radio playlist from API: " + tracks.Length + " track(s).");
    }

    private void StartPlaylistRefreshLoop()
    {
        CancelPlaylistRefresh();
        playlistRefreshRoutine = StartCoroutine(PlaylistRefreshLoop());
    }

    private void CancelPlaylistRefresh()
    {
        if (playlistRefreshRoutine == null)
            return;

        StopCoroutine(playlistRefreshRoutine);
        playlistRefreshRoutine = null;
    }

    private IEnumerator PlaylistRefreshLoop()
    {
        while (usingRemoteApi && libraryReady)
        {
            yield return new WaitForSecondsRealtime(PlaylistRefreshSeconds);
            yield return RefreshPlaylistMerge();
        }

        playlistRefreshRoutine = null;
    }

    private IEnumerator RefreshPlaylistMerge()
    {
        if (!usingRemoteApi)
            yield break;

        TrackEntry[] previous = tracks;
        AudioClip[] previousClips = clips;
        string currentId = (previous != null && currentSong >= 0 && currentSong < previous.Length)
            ? previous[currentSong]?.Id
            : null;

        bool ok = false;
        yield return FetchRemotePlaylist(success => ok = success);
        if (!ok || tracks == null || tracks.Length == 0)
        {
            // Keep prior playlist if refresh fails.
            if (previous != null)
                tracks = previous;
            yield break;
        }

        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        if (previous != null)
        {
            for (int i = 0; i < previous.Length; i++)
            {
                if (previous[i] != null && !string.IsNullOrEmpty(previous[i].Id))
                    byId[previous[i].Id] = i;
            }
        }

        var newClips = new AudioClip[tracks.Length];
        for (int i = 0; i < tracks.Length; i++)
        {
            TrackEntry entry = tracks[i];
            if (entry == null || !byId.TryGetValue(entry.Id, out int oldIndex))
                continue;

            TrackEntry old = previous[oldIndex];
            if (old != null)
            {
                entry.SignedUrl = old.SignedUrl;
                entry.UrlExpiresAtRealtime = old.UrlExpiresAtRealtime;
                entry.LocalPath = old.LocalPath;
                if (!string.IsNullOrWhiteSpace(old.Title) && string.IsNullOrWhiteSpace(entry.Title))
                    entry.Title = old.Title;
            }

            if (previousClips != null && oldIndex >= 0 && oldIndex < previousClips.Length)
                newClips[i] = previousClips[oldIndex];
        }

        clips = newClips;

        if (!string.IsNullOrEmpty(currentId))
        {
            for (int i = 0; i < tracks.Length; i++)
            {
                if (tracks[i] != null && tracks[i].Id == currentId)
                {
                    currentSong = i;
                    break;
                }
            }
        }

        NotifyStateChanged();
        FlamieLog.Info("[FlamiePrac] Radio playlist refreshed: " + tracks.Length + " track(s).");
    }

    private void BuildShuffleOrder(int avoidFirstIndex)
    {
        UnityEngine.Random.InitState(unchecked(
            Environment.TickCount * 397 ^
            (int)(DateTime.UtcNow.Ticks & 0x7fffffff) ^
            (tracks != null ? tracks.Length * 7919 : 0)));

        shuffleOrder.Clear();
        for (int i = 0; i < tracks.Length; i++)
            shuffleOrder.Add(i);

        for (int i = shuffleOrder.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            int tmp = shuffleOrder[i];
            shuffleOrder[i] = shuffleOrder[j];
            shuffleOrder[j] = tmp;
        }

        if (avoidFirstIndex >= 0 &&
            avoidFirstIndex < shuffleOrder.Count &&
            shuffleOrder.Count > 1 &&
            shuffleOrder[0] == avoidFirstIndex)
        {
            int swap = UnityEngine.Random.Range(1, shuffleOrder.Count);
            int tmp = shuffleOrder[0];
            shuffleOrder[0] = shuffleOrder[swap];
            shuffleOrder[swap] = tmp;
        }

        shuffleIndex = 0;
    }

    private int PickNextIndex(int excludeIndex = -1)
    {
        if (tracks == null || tracks.Length == 0)
            return 0;

        if (tracks.Length == 1)
            return 0;

        if (shuffleIndex >= shuffleOrder.Count)
            BuildShuffleOrder(excludeIndex >= 0 ? excludeIndex : currentSong);

        int picked = shuffleOrder[shuffleIndex++];

        if (excludeIndex >= 0 && picked == excludeIndex)
        {
            if (shuffleIndex >= shuffleOrder.Count)
                BuildShuffleOrder(excludeIndex);

            picked = shuffleOrder[shuffleIndex++];
        }

        return picked;
    }

    private void Update()
    {
        if (!libraryReady || tracks == null || tracks.Length == 0)
            return;

        if (playback == null)
            EnsurePlaybackSource();

        if (playback == null)
            return;

        if (IsSyncedPlayback)
        {
            // End-of-track uses the server clock so mute / far speakers still advance the playlist.
            MaybeReportVerifiedClipDuration();
            if (!HoldPlaybackAtEndIfNeeded())
            {
                MaybeDetectPlaybackLoop();
                MaybeDetectPlaybackStall();
                MaybeReportEndFromServerClock();

                if (listeningEnabled)
                    TrySyncSeek(force: false);
                else if (playback.isPlaying)
                    playback.Pause();
            }

            trackWasPlaying = listeningEnabled && IsAnyOutputPlaying();
        }
        else if (listeningEnabled &&
                 !advancingTrack &&
                 AllOutputsIdle() &&
                 trackWasPlaying &&
                 Time.unscaledTime - currentTrackStartedAt >= MinAutoAdvanceSeconds)
        {
            AutoAdvanceToNextTrack();
            trackWasPlaying = IsAnyOutputPlaying();
        }
        else
        {
            trackWasPlaying = listeningEnabled && IsAnyOutputPlaying();
        }

        // HUD progress only needs pulses while the panel is expanded (handler no-ops when collapsed).
        if (playback.clip != null)
            NotifyStateChangedThrottled();
    }

    private float nextUiPulse;

    private void NotifyStateChangedThrottled()
    {
        if (Time.unscaledTime < nextUiPulse)
            return;

        nextUiPulse = Time.unscaledTime + 0.5f;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FlamieLog.Warn("[FlamiePrac] Radio StateChanged handler failed: " + ex.Message);
        }
    }

    private static void ConfigurePlaybackAudio(AudioSource audio)
    {
        audio.loop = false;
        audio.playOnAwake = false;
        audio.dopplerLevel = 0f;
        audio.spatialBlend = 0f; // 2D — volume faked from nearest speaker distance
        audio.spatialize = false;
        audio.priority = 64;
    }

    /// <summary>
    /// Tune in/out (true radio). Does not affect the server clock — tune-in re-seeks to the live position.
    /// Use the volume slider to mute while staying tuned in.
    /// </summary>
    public void TogglePlayPause()
    {
        ListeningEnabled = !ListeningEnabled;
    }

    public void RequestTrackChange(byte command)
    {
        if (IsSyncedPlayback || RadioSync.IsNetworkSynced)
        {
            if (command == CmdNext)
                RadioSync.ClientVoteSkip();
            else if (command == CmdPrev)
                RadioSync.ClientRestart();
            return;
        }

        if (TrainingSync.Instance != null)
            TrainingSync.Instance.RequestRadioCommand(command);
        else
            AdvanceTrackLocally(command);
    }

    private void AdvanceTrackLocally(byte command)
    {
        try
        {
            if (command == CmdNext)
                NextSong();
            else if (command == CmdPrev)
                PreviousSong();
        }
        catch (Exception ex)
        {
            FlamieLog.Warn("[FlamiePrac] Radio advance failed: " + ex.Message);
        }
        finally
        {
            advancingTrack = false;
        }
    }

    private void CancelAutoAdvanceDelay()
    {
        if (autoAdvanceCoroutine == null)
            return;

        StopCoroutine(autoAdvanceCoroutine);
        autoAdvanceCoroutine = null;
    }

    private void AutoAdvanceToNextTrack()
    {
        if (advancingTrack || !listeningEnabled || tracks == null || tracks.Length == 0)
            return;

        if (IsSyncedPlayback)
        {
            // Server clock owns playlist advance; clients load during the inter-track gap.
            return;
        }

        CancelAutoAdvanceDelay();
        autoAdvanceCoroutine = StartCoroutine(AutoAdvanceAfterGap());
    }

    private IEnumerator AutoAdvanceAfterGap()
    {
        advancingTrack = true;
        yield return new WaitForSecondsRealtime(RadioSync.InterTrackGapSeconds);

        if (!listeningEnabled || tracks == null || tracks.Length == 0)
        {
            advancingTrack = false;
            autoAdvanceCoroutine = null;
            yield break;
        }

        FlamieLog.InfoThrottled("radio-advance", "[FlamiePrac] Radio auto-advancing to next track.");
        AdvanceTrackLocally(CmdNext);
        autoAdvanceCoroutine = null;
    }

    private bool AllOutputsIdle()
    {
        return !IsAnyOutputPlaying();
    }

    private bool IsAnyOutputPlaying()
    {
        return playback != null && playback.isPlaying;
    }

    private AudioClip GetActiveClip()
    {
        AudioSource primary = PrimarySource;
        if (primary != null && primary.clip != null)
            return primary.clip;

        if (clips == null || currentSong < 0 || currentSong >= clips.Length)
            return null;

        return clips[currentSong];
    }

    private float GetActiveClipLength()
    {
        AudioClip clip = GetActiveClip();
        if (clip == null || clip.length <= 0.05f)
            return MinAutoAdvanceSeconds + 1f;

        return clip.length;
    }

    private static string FormatTime(float seconds)
    {
        int total = Mathf.Max(0, (int)seconds);
        return (total / 60) + ":" + (total % 60).ToString("00");
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        NotifyStateChanged();
    }

    private void CancelPlayRoutine()
    {
        CancelAutoAdvanceDelay();
        if (playRoutine == null)
            return;

        StopCoroutine(playRoutine);
        playRoutine = null;
    }

    private void RequestPlay(int index, bool recordHistory)
    {
        CancelPlayRoutine();
        int gen = ++playRequestGeneration;
        playRoutine = StartCoroutine(PlayTrackRoutine(index, recordHistory, gen));
    }

    private IEnumerator PlayTrackRoutine(int index, bool recordHistory, int generation)
    {
        if (tracks == null || index < 0 || index >= tracks.Length)
        {
            advancingTrack = false;
            yield break;
        }

        SetStatus("Loading…");
        // Always resolve via /track?id= before play (playlist has titles only).
        yield return EnsureSignedUrl(index, force: false);
        if (generation != playRequestGeneration)
            yield break;

        yield return EnsureClipLoaded(index, allowResignRetry: true);
        if (generation != playRequestGeneration)
            yield break;

        if (clips == null || clips[index] == null)
        {
            FlamieLog.Warn("[FlamiePrac] Song failed to load, skipping: " + tracks[index].Title);
            yield return PlayAlternateRoutine(PickNextIndex(index), Mathf.Max(tracks.Length * 2, 4), generation);
            yield break;
        }

        ApplyClipToOutputs(index, recordHistory);
        advancingTrack = false;
        playRoutine = null;
    }

    private IEnumerator EnsureSignedUrl(int index, bool force)
    {
        TrackEntry entry = tracks[index];
        if (entry == null)
            yield break;

        if (!string.IsNullOrEmpty(entry.LocalPath))
            yield break;

        if (!force)
        {
            bool fresh = !string.IsNullOrEmpty(entry.SignedUrl) &&
                         entry.UrlExpiresAtRealtime > Time.realtimeSinceStartup + UrlRefreshSkewSeconds;
            if (fresh)
                yield break;
        }

        entry.SignedUrl = null;
        string url = apiBase + "/track?id=" + UnityWebRequest.EscapeURL(entry.Id);
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            RadioApiUtil.ConfigureRequest(req);
            yield return req.SendWebRequest();

            long code = req.responseCode;
            if (req.result != UnityWebRequest.Result.Success)
            {
                if (code == 404)
                    FlamieLog.Warn("[FlamiePrac] Radio track not found (404): '" + entry.Id + "'");
                else
                    FlamieLog.Warn("[FlamiePrac] Radio sign failed for '" + entry.Id + "': " + req.error);
                entry.SignedUrl = null;
                yield break;
            }

            TrackDto dto;
            try
            {
                dto = JsonUtility.FromJson<TrackDto>(req.downloadHandler.text);
            }
            catch (Exception ex)
            {
                FlamieLog.Warn("[FlamiePrac] Radio sign JSON failed: " + ex.Message);
                yield break;
            }

            if (dto == null || string.IsNullOrWhiteSpace(dto.url))
            {
                FlamieLog.Warn("[FlamiePrac] Radio sign returned no url for '" + entry.Id + "'.");
                yield break;
            }

            entry.SignedUrl = dto.url.Trim();
            int ttl = dto.expiresIn > 0 ? dto.expiresIn : 3600;
            entry.UrlExpiresAtRealtime = Time.realtimeSinceStartup + ttl;
            if (!string.IsNullOrWhiteSpace(dto.title))
                entry.Title = dto.title.Trim();
        }
    }

    private IEnumerator EnsureClipLoaded(int index, bool allowResignRetry)
    {
        if (clips != null && clips[index] != null)
            yield break;

        TrackEntry entry = tracks[index];
        if (entry == null)
            yield break;

        yield return DownloadClipOnce(index);

        if (clips != null && clips[index] != null)
            yield break;

        // Expired / 403 from S3 → ask phlstats for a fresh signed URL once.
        if (!allowResignRetry || !string.IsNullOrEmpty(entry.LocalPath))
            yield break;

        entry.SignedUrl = null;
        entry.UrlExpiresAtRealtime = -1f;
        yield return EnsureSignedUrl(index, force: true);
        yield return DownloadClipOnce(index);
    }

    private IEnumerator DownloadClipOnce(int index)
    {
        if (clips != null && clips[index] != null)
            yield break;

        TrackEntry entry = tracks[index];
        if (entry == null)
            yield break;

        string uri;
        if (!string.IsNullOrEmpty(entry.LocalPath))
            uri = new Uri(Path.GetFullPath(entry.LocalPath)).AbsoluteUri;
        else if (!string.IsNullOrEmpty(entry.SignedUrl))
            uri = entry.SignedUrl;
        else
            yield break;

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG))
        {
            RadioApiUtil.ConfigureRequest(www);
            // Full download — streaming MP3 length is often wrong (VBR/header padding reads as 20s+).
            if (www.downloadHandler is DownloadHandlerAudioClip streamHandler)
            {
                streamHandler.streamAudio = false;
                streamHandler.compressed = true;
            }

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                long code = www.responseCode;
                FlamieLog.Error("[FlamiePrac] Failed to load '" + entry.Title + "': " + www.error +
                               (code > 0 ? " (HTTP " + code + ")" : string.Empty));
                if (string.IsNullOrEmpty(entry.LocalPath))
                {
                    entry.SignedUrl = null;
                    entry.UrlExpiresAtRealtime = -1f;
                }

                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
            if (clips == null || index >= clips.Length)
                yield break;

            clips[index] = clip;
            FlamieLog.Info("[FlamiePrac] Radio loaded: " + entry.Title + " (" + clip.length.ToString("0.0") + "s)");
        }
    }

    private float ResolveDurationForTrack(string trackId, float serverDuration)
    {
        if (verifiedDurations.TryGetValue(trackId, out float verified) && verified > 0.05f)
            return verified;

        return serverDuration > 0.05f ? serverDuration : 0f;
    }

    private void RememberVerifiedDuration(string trackId, float duration)
    {
        if (string.IsNullOrEmpty(trackId) || duration <= 0.05f)
            return;

        verifiedDurations[trackId] = duration;
    }

    private bool IsPlaybackClipForSyncedTrack()
    {
        if (playback == null || playback.clip == null || string.IsNullOrEmpty(syncedTrackId))
            return false;

        if (tracks == null || currentSong < 0 || currentSong >= tracks.Length)
            return false;

        TrackEntry entry = tracks[currentSong];
        if (entry == null || !string.Equals(entry.Id, syncedTrackId, StringComparison.Ordinal))
            return false;

        return clips != null && currentSong < clips.Length && clips[currentSong] == playback.clip;
    }

    private bool TryGetSyncedClipLength(out float length)
    {
        length = 0f;
        if (!IsPlaybackClipForSyncedTrack())
            return false;

        length = playback.clip.length;
        return length > 0.05f;
    }

    private float GetEffectivePlaybackLength()
    {
        if (IsSyncedPlayback && !string.IsNullOrEmpty(syncedTrackId))
        {
            if (verifiedDurations.TryGetValue(syncedTrackId, out float verified) && verified > 0.05f)
                return verified;

            if (TryGetSyncedClipLength(out float clipLen))
                return clipLen;

            if (syncedDuration > 0.05f)
                return syncedDuration;
        }

        AudioSource src = PrimarySource;
        float loaded = src != null && src.clip != null ? src.clip.length : 0f;
        return loaded > 0.05f ? loaded : 0f;
    }

    private void ReportCorrectedDuration(float duration)
    {
        if (!IsSyncedPlayback || string.IsNullOrEmpty(syncedTrackId) || duration <= 0.5f)
            return;

        RememberVerifiedDuration(syncedTrackId, duration);
        syncedDuration = duration;
        reportedDurationForCurrent = true;
        RadioSync.ClientReportDuration(syncedTrackId, duration);
    }

    private void MaybeDetectPlaybackStall()
    {
        if (!listeningEnabled || playback == null || playback.clip == null)
        {
            lastObservedPlaybackTime = -1f;
            return;
        }

        if (!playback.isPlaying)
        {
            lastObservedPlaybackTime = -1f;
            return;
        }

        float t = playback.time;
        if (lastObservedPlaybackTime < 0f || t > lastObservedPlaybackTime + 0.02f)
        {
            lastObservedPlaybackTime = t;
            lastPlaybackTimeChangeAt = Time.unscaledTime;
            return;
        }

        if (t < 0.5f || Time.unscaledTime - lastPlaybackTimeChangeAt < PlaybackStallSeconds)
            return;

        float actualLen = Mathf.Max(t, lastObservedPlaybackTime);
        ReportCorrectedDuration(actualLen);
        playback.Pause();
        ReportTrackEndedIfNeeded(forceRetry: true);
        lastObservedPlaybackTime = -1f;
    }

    private void ReleaseUnusedClips(int keepIndex)
    {
        if (clips == null)
            return;

        for (int i = 0; i < clips.Length; i++)
        {
            if (i == keepIndex || clips[i] == null)
                continue;

            Destroy(clips[i]);
            clips[i] = null;
        }
    }

    private void ApplyClipToOutputs(int index, bool recordHistory)
    {
        currentSong = index;
        advancingTrack = false;
        currentTrackStartedAt = Time.unscaledTime;

        if (recordHistory && !IsSyncedPlayback)
            RecordForwardPlay(index);

        PlayerPrefs.SetInt(PrefLastTrack, index);
        // Do not PlayerPrefs.Save() here — disk flush hitch on every track change.

        AudioClip clip = clips[index];
        ReleaseUnusedClips(index);
        EnsurePlaybackSource();
        PruneDeadSpeakerAnchors();

        if (speakerAnchors.Count == 0)
            RefreshSpeakerAnchorsFromScene();

        if (playback == null)
        {
            FlamieLog.Warn("[FlamiePrac] Radio track ready but playback source missing.");
            NotifyStateChanged();
            return;
        }

        float seek = IsSyncedPlayback ? ComputeServerSeekSeconds() : 0f;
        if (clip != null && clip.length > 0.05f)
        {
            if (IsSyncedPlayback && (IsWaitingForServerStart() || seek < 0f))
            {
                playback.loop = false;
                playback.clip = clip;
                playback.time = 0f;
                playback.Pause();
                ReportTrackReadyIfNeeded();
                NotifyStateChanged();
                return;
            }

            // If sync clock is already past the clip, wait for server advance — don't restart.
            if (IsSyncedPlayback && seek >= clip.length - EndOfTrackEpsilonSeconds)
            {
                float endPos = Mathf.Max(0f, clip.length - EndOfTrackEpsilonSeconds);
                playback.loop = false;
                playback.clip = clip;
                playback.time = endPos;
                playback.Pause();
                NotifyStateChanged();
                return;
            }

            seek = Mathf.Clamp(seek, 0f, Mathf.Max(0f, clip.length - EndOfTrackEpsilonSeconds));
        }

        playback.loop = false;
        playback.clip = clip;
        UpdateDistanceVolume();

        if (listeningEnabled)
        {
            playback.time = seek;
            playback.Play();
        }
        else
        {
            playback.time = seek;
            playback.Pause();
        }

        if (IsSyncedPlayback)
        {
            CancelTrackEndWatch();
            if (clip != null && tracks[index] != null &&
                string.Equals(tracks[index].Id, syncedTrackId, StringComparison.Ordinal))
            {
                RememberVerifiedDuration(tracks[index].Id, clip.length);
                reportedDurationForCurrent = true;
                syncedDuration = clip.length;
                RadioSync.ClientReportDuration(tracks[index].Id, clip.length);
            }
        }
        else if (listeningEnabled)
        {
            ScheduleTrackEndWatch(GetActiveClipLength());
        }

        StatusMessage = string.Empty;
        FlamieLog.Info("[FlamiePrac] Radio " + (listeningEnabled ? "playing" : "loaded (muted)") + ": " +
                       tracks[index].Title +
                       (IsSyncedPlayback ? (" @ " + seek.ToString("0.00") + "s sync") : string.Empty));
        NotifyStateChanged();
    }

    private void SyncPlaybackToCurrentTrack()
    {
        EnsurePlaybackSource();
        if (playback == null || !libraryReady || clips == null || currentSong < 0 || currentSong >= clips.Length)
            return;

        AudioClip clip = clips[currentSong];
        if (clip == null)
            return;

        if (playback.clip == clip && (playback.isPlaying || !listeningEnabled))
            return;

        playback.clip = clip;
        UpdateDistanceVolume();
        ApplyListeningState(seekIfOn: true);
    }

    private void ScheduleTrackEndWatch(float clipLength)
    {
        CancelTrackEndWatch();
        int generation = ++trackPlayGeneration;
        trackEndCoroutine = StartCoroutine(WatchTrackEnd(generation, Mathf.Max(clipLength, MinAutoAdvanceSeconds + 0.5f)));
    }

    private IEnumerator WatchTrackEnd(int generation, float clipLength)
    {
        float wait = Mathf.Max(clipLength - 0.05f, MinAutoAdvanceSeconds);
        yield return new WaitForSecondsRealtime(wait);

        float deadline = Time.unscaledTime + 3f;
        while (Time.unscaledTime < deadline)
        {
            if (generation != trackPlayGeneration || !listeningEnabled)
                yield break;

            if (AllOutputsIdle())
                break;

            AudioSource primary = PrimarySource;
            if (primary != null && primary.clip != null && primary.time >= primary.clip.length - 0.1f)
                break;

            yield return null;
        }

        if (generation != trackPlayGeneration || !listeningEnabled || advancingTrack)
            yield break;

        AutoAdvanceToNextTrack();
    }

    private void CancelTrackEndWatch()
    {
        if (trackEndCoroutine == null)
            return;

        StopCoroutine(trackEndCoroutine);
        trackEndCoroutine = null;
    }

    private void TryStartPlayback(int? firstIndex = null)
    {
        if (!libraryReady || !listeningEnabled || tracks == null || tracks.Length == 0)
            return;

        EnsurePlaybackSource();
        if (playback == null)
            return;

        if (IsAnyOutputPlaying())
            return;

        int index = firstIndex ?? pendingStartIndex;
        if (index < 0)
            index = currentSong;
        if (index < 0 || index >= tracks.Length)
            index = 0;

        pendingStartIndex = -1;
        RequestPlay(index, recordHistory: false);
    }

    private void PruneDeadSpeakerAnchors()
    {
        for (int i = speakerAnchors.Count - 1; i >= 0; i--)
        {
            if (speakerAnchors[i] == null)
                speakerAnchors.RemoveAt(i);
        }
    }

    private void RecordForwardPlay(int index)
    {
        if (historyIndex < playHistory.Count - 1)
            playHistory.RemoveRange(historyIndex + 1, playHistory.Count - historyIndex - 1);

        if (playHistory.Count == 0 || playHistory[playHistory.Count - 1] != index)
            playHistory.Add(index);

        historyIndex = playHistory.Count - 1;
    }

    private void RestartCurrentSong()
    {
        if (PrimarySource == null || PrimarySource.clip == null)
        {
            RequestPlay(currentSong, recordHistory: false);
            return;
        }

        CancelTrackEndWatch();
        currentTrackStartedAt = Time.unscaledTime;

        EnsurePlaybackSource();
        if (playback != null)
        {
            playback.time = 0f;
            if (!playback.isPlaying)
                playback.Play();
        }

        if (listeningEnabled)
            ScheduleTrackEndWatch(GetActiveClipLength());

        NotifyStateChanged();
    }

    private void GoBackInPlayHistory()
    {
        if (playHistory.Count == 0 || historyIndex <= 0)
        {
            RestartCurrentSong();
            return;
        }

        historyIndex--;
        RequestPlay(playHistory[historyIndex], recordHistory: false);
    }

    private IEnumerator DelayedRestartCurrentSong()
    {
        yield return new WaitForSecondsRealtime(DoublePrevWindowSeconds);
        delayedRestartCoroutine = null;
        RestartCurrentSong();
    }

    private void CancelDelayedRestart()
    {
        if (delayedRestartCoroutine == null)
            return;

        StopCoroutine(delayedRestartCoroutine);
        delayedRestartCoroutine = null;
    }

    private IEnumerator PlayAlternateRoutine(int index, int attemptsLeft, int generation)
    {
        while (attemptsLeft > 0)
        {
            if (generation != playRequestGeneration)
                yield break;

            yield return EnsureSignedUrl(index, force: false);
            if (generation != playRequestGeneration)
                yield break;

            yield return EnsureClipLoaded(index, allowResignRetry: true);
            if (generation != playRequestGeneration)
                yield break;

            if (clips != null && clips[index] != null)
            {
                ApplyClipToOutputs(index, recordHistory: true);
                advancingTrack = false;
                playRoutine = null;
                yield break;
            }

            index = PickNextIndex(index);
            attemptsLeft--;
        }

        SetStatus("No playable tracks");
        advancingTrack = false;
        playRoutine = null;
    }

    public void NextSong()
    {
        if (tracks == null || tracks.Length == 0)
            return;

        CancelAutoAdvanceDelay();
        CancelDelayedRestart();
        CancelTrackEndWatch();
        advancingTrack = true;
        RequestPlay(PickNextIndex(currentSong), recordHistory: true);
    }

    public void PreviousSong()
    {
        if (tracks == null || tracks.Length == 0)
            return;

        CancelTrackEndWatch();

        float now = Time.unscaledTime;
        if (now - lastPrevPressTime <= DoublePrevWindowSeconds)
        {
            CancelDelayedRestart();
            GoBackInPlayHistory();
            lastPrevPressTime = 0f;
            return;
        }

        lastPrevPressTime = now;
        CancelDelayedRestart();
        delayedRestartCoroutine = StartCoroutine(DelayedRestartCurrentSong());
    }
}
