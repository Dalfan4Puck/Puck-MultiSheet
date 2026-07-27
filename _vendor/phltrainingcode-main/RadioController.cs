using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 3D speaker audio only — no screen UI. Visual controls live in <see cref="RadioHudUI"/> (UITK).
/// One global instance on the mod bootstrap; hive speakers register as spatial outputs.
/// </summary>
public class RadioController : MonoBehaviour
{
    public const byte CmdNext = 1;
    public const byte CmdPrev = 2;

    private const string PrefVolume = "FlamiePrac_RadioVolume";
    private const string PrefLastTrack = "FlamiePrac_RadioLastTrack";
    private const float DoublePrevWindowSeconds = 0.4f;
    private const float MinAutoAdvanceSeconds = 3f;

    public static RadioController Instance { get; private set; }

    public event Action StateChanged;

    private readonly List<SpeakerOutput> outputs = new List<SpeakerOutput>();
    private readonly List<int> playHistory = new List<int>();
    private readonly List<int> shuffleOrder = new List<int>();

    private float nextSpeakerRefreshTime;
    private float nextPlaybackRecoveryTime;

    private sealed class SpeakerOutput
    {
        public Transform Target;
        public AudioSource Audio;
    }

    private AudioClip[] clips;
    private string[] songs;

    private int currentSong;
    private int shuffleIndex;
    private int historyIndex;
    private bool userPaused;
    private bool advancingTrack;
    private bool trackWasPlaying;
    private bool libraryReady;
    private float storedVolume = 0.75f;
    private float lastPrevPressTime;
    private float currentTrackStartedAt;
    private Coroutine delayedRestartCoroutine;
    private Coroutine trackEndCoroutine;
    private int trackPlayGeneration;

    private AudioSource PrimarySource
    {
        get
        {
            PruneDeadOutputs();
            return outputs.Count > 0 ? outputs[0].Audio : null;
        }
    }

    public bool IsReady => libraryReady && songs != null && songs.Length > 0 && clips != null;

    public bool IsPlaying => PrimarySource != null && PrimarySource.isPlaying;

    public float Volume
    {
        get => storedVolume;
        set
        {
            storedVolume = Mathf.Clamp01(value);
            ApplyVolumeToOutputs();
            PlayerPrefs.SetFloat(PrefVolume, storedVolume);
            PlayerPrefs.Save();
            NotifyStateChanged();
        }
    }

    private void ApplyVolumeToOutputs()
    {
        PruneDeadOutputs();
        foreach (SpeakerOutput output in outputs)
        {
            if (output?.Audio != null)
                output.Audio.volume = storedVolume;
        }
    }

    private void LateUpdate()
    {
        SyncOutputTransforms();
        TryRefreshSpeakerOutputs();
        TryRecoverPlayback();
    }

    private void SyncOutputTransforms()
    {
        foreach (SpeakerOutput output in outputs)
        {
            if (output?.Audio == null || output.Target == null)
                continue;

            output.Audio.transform.SetPositionAndRotation(output.Target.position, output.Target.rotation);
        }
    }

    private void TryRefreshSpeakerOutputs()
    {
        if (Time.unscaledTime < nextSpeakerRefreshTime)
            return;

        nextSpeakerRefreshTime = Time.unscaledTime + 2f;

        if (outputs.Count > 0)
            return;

        RefreshSpeakerOutputsFromScene();
    }

    private void TryRecoverPlayback()
    {
        if (!libraryReady || userPaused || songs == null || songs.Length == 0)
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

        if (outputs.Count == 0)
            RefreshSpeakerOutputsFromScene();

        if (outputs.Count == 0)
            return;

        if (AllOutputsIdle())
            AutoAdvanceToNextTrack();
    }

    private void RefreshSpeakerOutputsFromScene()
    {
        Transform[] all = FindObjectsByType<Transform>(FindObjectsSortMode.None);
        foreach (Transform transform in all)
        {
            if (transform == null)
                continue;

            if (!TrainingPrefabNames.IsSpeakerName(transform.name))
                continue;

            if (transform.GetComponentsInChildren<Renderer>(true).Length == 0)
                continue;

            RegisterSpeaker(transform.gameObject);
        }
    }

    public float Progress01
    {
        get
        {
            if (PrimarySource == null || PrimarySource.clip == null || PrimarySource.clip.length <= 0f)
                return 0f;

            return PrimarySource.time / PrimarySource.clip.length;
        }
    }

    public string CurrentTrackTitle =>
        songs != null && songs.Length > 0 && currentSong >= 0 && currentSong < songs.Length
            ? Path.GetFileNameWithoutExtension(songs[currentSong])
            : string.Empty;

    public string NextTrackTitle
    {
        get
        {
            if (songs == null || songs.Length == 0)
                return string.Empty;

            if (songs.Length == 1)
                return Path.GetFileNameWithoutExtension(songs[0]);

            return "Shuffled";
        }
    }

    public string TimeText
    {
        get
        {
            if (PrimarySource == null || PrimarySource.clip == null)
                return "0:00 / 0:00";

            return FormatTime(PrimarySource.time) + " / " + FormatTime(PrimarySource.clip.length);
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
        storedVolume = PlayerPrefs.GetFloat(PrefVolume, 0.75f);
    }

    private void OnDestroy()
    {
        if (Instance != this)
            return;

        PrepareForDestroy();
        Instance = null;
    }

    /// <summary>Attach a 3D audio output to a Speaker prop.</summary>
    public void RegisterSpeaker(GameObject speakerGo)
    {
        if (speakerGo == null)
            return;

        PruneDeadOutputs();

        foreach (SpeakerOutput existing in outputs)
        {
            if (existing?.Target == speakerGo.transform)
                return;
        }

        GameObject host = new GameObject("FlamiePrac_RadioOut_" + speakerGo.name);
        host.transform.SetParent(transform, false);
        host.transform.SetPositionAndRotation(speakerGo.transform.position, speakerGo.transform.rotation);

        AudioSource audio = host.AddComponent<AudioSource>();
        ConfigureSpatialAudio(audio, speakerGo.name);

        outputs.Add(new SpeakerOutput
        {
            Target = speakerGo.transform,
            Audio = audio
        });

        audio.volume = storedVolume;
        Debug.Log("[FlamiePrac] Radio registered output #" + outputs.Count + " following '" + speakerGo.name + "'.");
        SyncOutputToCurrentTrack(audio);
        TryStartPlayback();
    }

    public void PrepareForDestroy()
    {
        CancelDelayedRestart();
        CancelTrackEndWatch();

        foreach (SpeakerOutput output in outputs)
        {
            if (output?.Audio == null)
                continue;

            output.Audio.Stop();
            if (output.Audio.gameObject != null)
                Destroy(output.Audio.gameObject);
        }

        outputs.Clear();
        libraryReady = false;
    }

    private IEnumerator Start()
    {
        if (Application.isBatchMode)
            yield break;

        try
        {
            Unity.Netcode.NetworkManager nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.IsServer && !nm.IsClient)
                yield break;
        }
        catch { }

        ApplyVolumeToOutputs();

        string dllFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string radioFolder = Path.Combine(dllFolder ?? string.Empty, "RadioSongs");

        Debug.Log("[FlamiePrac] Radio looking for songs in: " + radioFolder);

        if (!Directory.Exists(radioFolder))
        {
            Debug.LogError("[FlamiePrac] RadioSongs folder not found at: " + radioFolder);
            SetStatus("RadioSongs folder missing");
            yield break;
        }

        songs = Directory.GetFiles(radioFolder, "*.mp3");
        Array.Sort(songs);

        if (songs.Length == 0)
        {
            Debug.LogError("[FlamiePrac] RadioSongs folder is empty (no .mp3 files).");
            SetStatus("No MP3 files found");
            yield break;
        }

        Debug.Log("[FlamiePrac] Radio found " + songs.Length + " track(s) across " + outputs.Count + " speaker(s).");
        clips = new AudioClip[songs.Length];

        for (int i = 0; i < songs.Length; i++)
            yield return StartCoroutine(LoadClip(i));

        int lastTrack = PlayerPrefs.GetInt(PrefLastTrack, -1);
        BuildShuffleOrder(lastTrack);

        int first = shuffleOrder[shuffleIndex++];
        playHistory.Clear();
        playHistory.Add(first);
        historyIndex = 0;
        libraryReady = true;
        TryStartPlayback(firstIndex: first);
    }

    private void BuildShuffleOrder(int avoidFirstIndex)
    {
        shuffleOrder.Clear();
        for (int i = 0; i < songs.Length; i++)
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
        if (songs == null || songs.Length == 0)
            return 0;

        if (songs.Length == 1)
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
        if (!libraryReady || songs == null || songs.Length == 0)
            return;

        PruneDeadOutputs();

        if (outputs.Count == 0)
            RefreshSpeakerOutputsFromScene();

        if (outputs.Count == 0)
            return;

        if (!userPaused &&
            !advancingTrack &&
            AllOutputsIdle() &&
            trackWasPlaying &&
            Time.unscaledTime - currentTrackStartedAt >= MinAutoAdvanceSeconds)
        {
            AutoAdvanceToNextTrack();
        }

        trackWasPlaying = IsAnyOutputPlaying() && !userPaused;

        if (PrimarySource != null && PrimarySource.clip != null)
            NotifyStateChangedThrottled();
    }

    private float nextUiPulse;

    private void NotifyStateChangedThrottled()
    {
        if (Time.unscaledTime < nextUiPulse)
            return;

        nextUiPulse = Time.unscaledTime + 0.25f;
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
            Debug.LogWarning("[FlamiePrac] Radio StateChanged handler failed: " + ex.Message);
        }
    }

    private static void ConfigureSpatialAudio(AudioSource audio, string speakerName)
    {
        audio.loop = false;
        audio.playOnAwake = false;
        audio.dopplerLevel = 0f;
        audio.spatialBlend = 1f;
        audio.rolloffMode = AudioRolloffMode.Logarithmic;
        audio.minDistance = 4f;
        audio.maxDistance = 48f;
        Debug.Log("[FlamiePrac] Radio using 3D audio at " + speakerName + ": " + audio.transform.position);
    }

    public void TogglePlayPause()
    {
        if (PrimarySource == null || PrimarySource.clip == null)
            return;

        if (PrimarySource.isPlaying)
        {
            foreach (SpeakerOutput output in outputs)
            {
                if (output?.Audio != null)
                    output.Audio.Pause();
            }

            CancelTrackEndWatch();
            userPaused = true;
        }
        else
        {
            foreach (SpeakerOutput output in outputs)
            {
                if (output?.Audio == null)
                    continue;

                output.Audio.UnPause();
                if (!output.Audio.isPlaying)
                    output.Audio.Play();
            }

            userPaused = false;
        }

        NotifyStateChanged();
    }

    public void RequestTrackChange(byte command)
    {
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
            Debug.LogWarning("[FlamiePrac] Radio advance failed: " + ex.Message);
        }
        finally
        {
            advancingTrack = false;
        }
    }

    private void AutoAdvanceToNextTrack()
    {
        if (advancingTrack || userPaused || songs == null || songs.Length == 0)
            return;

        advancingTrack = true;
        Debug.Log("[FlamiePrac] Radio auto-advancing to next track.");
        AdvanceTrackLocally(CmdNext);
    }

    private bool AllOutputsIdle()
    {
        PruneDeadOutputs();
        if (outputs.Count == 0)
            return true;

        foreach (SpeakerOutput output in outputs)
        {
            if (output?.Audio != null && output.Audio.isPlaying)
                return false;
        }

        return true;
    }

    private bool IsAnyOutputPlaying()
    {
        PruneDeadOutputs();
        foreach (SpeakerOutput output in outputs)
        {
            if (output?.Audio != null && output.Audio.isPlaying)
                return true;
        }

        return false;
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

    private static string ToFileUri(string path)
    {
        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    private static string FormatTime(float seconds)
    {
        int total = Mathf.Max(0, (int)seconds);
        return (total / 60) + ":" + (total % 60).ToString("00");
    }

    private IEnumerator LoadClip(int index)
    {
        string uri = ToFileUri(songs[index]);

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG))
        {
            if (www.downloadHandler is DownloadHandlerAudioClip streamHandler)
                streamHandler.streamAudio = false;

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[FlamiePrac] Failed to load " + songs[index] + ": " + www.error);
                yield break;
            }

            clips[index] = DownloadHandlerAudioClip.GetContent(www);
            Debug.Log("[FlamiePrac] Radio loaded: " + Path.GetFileNameWithoutExtension(songs[index]));
        }
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        NotifyStateChanged();
    }

    private void PlayLoadedSong(int index, bool recordHistory = true)
    {
        if (songs == null || songs.Length == 0)
            return;

        currentSong = index;
        userPaused = false;
        advancingTrack = false;
        currentTrackStartedAt = Time.unscaledTime;

        if (recordHistory)
            RecordForwardPlay(index);

        PlayerPrefs.SetInt(PrefLastTrack, index);
        PlayerPrefs.Save();

        if (clips == null || clips[index] == null)
        {
            Debug.LogWarning("[FlamiePrac] Song failed to load, skipping: " +
                             Path.GetFileNameWithoutExtension(songs[index]));
            TryPlayAlternate(PickNextIndex(index), songs.Length - 1);
            return;
        }

        AudioClip clip = clips[index];
        PruneDeadOutputs();

        if (outputs.Count == 0)
            RefreshSpeakerOutputsFromScene();

        if (outputs.Count == 0)
        {
            Debug.LogWarning("[FlamiePrac] Radio track ready but no speaker outputs — audio will not play.");
            NotifyStateChanged();
            return;
        }

        foreach (SpeakerOutput output in outputs)
        {
            if (output?.Audio == null)
                continue;

            output.Audio.clip = clip;
            output.Audio.time = 0f;
            output.Audio.Play();
        }

        ScheduleTrackEndWatch(GetActiveClipLength());

        StatusMessage = string.Empty;
        Debug.Log("[FlamiePrac] Radio playing: " + Path.GetFileNameWithoutExtension(songs[index]));
        NotifyStateChanged();
    }

    private void SyncOutputToCurrentTrack(AudioSource audio)
    {
        if (audio == null || !libraryReady || clips == null || currentSong < 0 || currentSong >= clips.Length)
            return;

        AudioClip clip = clips[currentSong];
        if (clip == null)
            return;

        audio.clip = clip;

        if (userPaused)
            return;

        AudioSource primary = PrimarySource;
        if (primary != null && primary != audio && primary.isPlaying)
            audio.time = primary.time;

        audio.Play();
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
            if (generation != trackPlayGeneration || userPaused)
                yield break;

            if (AllOutputsIdle())
                break;

            AudioSource primary = PrimarySource;
            if (primary != null && primary.clip != null && primary.time >= primary.clip.length - 0.1f)
                break;

            yield return null;
        }

        if (generation != trackPlayGeneration || userPaused || advancingTrack)
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
        if (!libraryReady || userPaused || songs == null || songs.Length == 0)
            return;

        PruneDeadOutputs();
        if (outputs.Count == 0)
            return;

        if (IsAnyOutputPlaying())
            return;

        int index = firstIndex ?? currentSong;
        if (index < 0 || index >= songs.Length)
            index = 0;

        PlayLoadedSong(index, recordHistory: false);
    }

    private void PruneDeadOutputs()
    {
        for (int i = outputs.Count - 1; i >= 0; i--)
        {
            SpeakerOutput output = outputs[i];
            if (output == null || output.Audio == null || output.Target == null)
            {
                if (output?.Audio != null && output.Audio.gameObject != null)
                    Destroy(output.Audio.gameObject);

                outputs.RemoveAt(i);
            }
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
            return;

        CancelTrackEndWatch();
        currentTrackStartedAt = Time.unscaledTime;

        foreach (SpeakerOutput output in outputs)
        {
            if (output?.Audio == null)
                continue;

            output.Audio.time = 0f;
            if (!output.Audio.isPlaying)
                output.Audio.Play();
        }

        ScheduleTrackEndWatch(GetActiveClipLength());

        userPaused = false;
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
        PlayLoadedSong(playHistory[historyIndex], recordHistory: false);
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

    private void TryPlayAlternate(int index, int attemptsLeft)
    {
        if (attemptsLeft <= 0 || songs == null || songs.Length == 0)
        {
            SetStatus("No playable tracks");
            advancingTrack = false;
            return;
        }

        if (clips == null || clips[index] == null)
        {
            TryPlayAlternate(PickNextIndex(index), attemptsLeft - 1);
            return;
        }

        PlayLoadedSong(index);
    }

    public void NextSong()
    {
        if (songs == null || songs.Length == 0)
            return;

        CancelDelayedRestart();
        CancelTrackEndWatch();
        TryPlayAlternate(PickNextIndex(currentSong), Mathf.Max(songs.Length * 2, 4));
    }

    public void PreviousSong()
    {
        if (songs == null || songs.Length == 0)
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
