using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using MyMod;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Replicates training spawns from server to clients via Custom Messaging.
/// Clients instantiate local visual/physics copies; server remains authoritative for puck interaction.
/// </summary>
public sealed class TrainingSync : MonoBehaviour
{
    private const string ChannelSpawn = "FlamiePrac_Spawn";
    private const string ChannelDespawn = "FlamiePrac_Despawn";
    private const string ChannelSnapshot = "FlamiePrac_Snapshot";
    private const string ChannelRequestSync = "FlamiePrac_RequestSync";
    private const string ChannelRadio = "FlamiePrac_Radio";
    private const string ChannelRadioRequest = "FlamiePrac_RadioRequest";
    private const string ChannelTestPuckSpawnRequest = "FlamiePrac_TestPuckSpawn";

    private const byte SpawnPrefab = 1;
    private const byte SpawnPasser = 2;
    private const byte SpawnCircularTarget = 3;

    public static TrainingSync Instance { get; private set; }

    public Transform ClientVisualRoot => clientVisualRoot;

    private readonly Dictionary<int, GameObject> clientObjects = new Dictionary<int, GameObject>();
    private readonly HashSet<ulong> pendingSnapshotClients = new HashSet<ulong>();
    private Transform clientVisualRoot;
    private bool networkReady;
    private bool isServer;
    private bool isClient;
    private bool shutDown;
    private bool tickSubscribed;
    private bool gameEventsRegistered;
    private bool serverHandlersRegistered;
    private bool clientHandlersRegistered;
    private bool clientAwaitingSnapshot;
    private float nextClientResyncTime;
    private float nextSnapshotFlushTime;
    private float clientSnapshotRetryAt;
    private int clientSnapshotRetries;
    private Coroutine waitForNetworkCoroutine;
    private Coroutine delayedSnapshotCoroutine;

    private Action<Dictionary<string, object>> onLevelSpawned;
    private Action<Dictionary<string, object>> onLevelDespawned;
    private Action<Dictionary<string, object>> onClientSceneSyncComplete;
    private Action<Dictionary<string, object>> onPuckClientStarted;
    private Action<Dictionary<string, object>> onPuckClientStopped;

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (!Application.isBatchMode && !FlamiePracFeatures.RadioServerDrivenOnly &&
            GetComponent<RadioHudDriver>() == null)
            gameObject.AddComponent<RadioHudDriver>();
    }

    private void Start()
    {
        RegisterGameEvents();
        waitForNetworkCoroutine = StartCoroutine(WaitForNetwork());
    }

    private void OnDestroy()
    {
        PerformShutdown();

        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        // Fallback if NetworkTickSystem.Tick is unavailable; slidables prefer OnNetworkTick (100 Hz).
        if (isServer && !tickSubscribed)
        {
            SlidableObstacleSync.TickServer();
            TrainingMotionSync.TickServer();
            RadioSync.TickServer();
        }

        if (isServer && !shutDown && pendingSnapshotClients.Count > 0 && Time.time >= nextSnapshotFlushTime)
        {
            nextSnapshotFlushTime = Time.time + 0.35f;
            FlushPendingSnapshots();
        }

        // Remote clients: keep asking until a non-empty snapshot sticks (join races are common).
        if (isClient && !isServer && networkReady && !shutDown)
            TickClientSnapshotRetry();
    }

    private void OnNetworkTick()
    {
        if (!isServer || shutDown)
            return;

        SlidableObstacleSync.TickServer();
        TrainingMotionSync.TickServer();
        RadioSync.TickServer();
    }

    /// <summary>Idempotent teardown — messaging handlers, client mirrors, server spawns, radio HUD.</summary>
    public void PerformShutdown()
    {
        if (shutDown)
            return;

        shutDown = true;
        networkReady = false;

        if (waitForNetworkCoroutine != null)
        {
            StopCoroutine(waitForNetworkCoroutine);
            waitForNetworkCoroutine = null;
        }

        StopAllCoroutines();
        delayedSnapshotCoroutine = null;

        UnregisterGameEvents();

        TrainingObjectManager mgr = GetComponent<TrainingObjectManager>();
        if (mgr != null)
            mgr.Shutdown();
        else if (TrainingObjectManager.Instance != null)
            TrainingObjectManager.Instance.Shutdown();

        ClearClientObjects();
        DestroyClientVisualRoot();
        SlidableObstacleSync.UnregisterAll();
        TrainingMotionSync.UnregisterAll();
        SlidablePuckFilter.Shutdown();
        FlamiePracTestPuckSpawn.ClearTrackedPucks();
        UnregisterHandlers();
        serverHandlersRegistered = false;
        clientHandlersRegistered = false;
        RadioHudUI.TearDown();
        RadioHudUI.CleanupLegacyUi();
    }

    private void RegisterGameEvents()
    {
        if (gameEventsRegistered)
            return;

        onLevelSpawned = OnLevelSpawned;
        onLevelDespawned = OnLevelDespawned;
        onClientSceneSyncComplete = OnClientSceneSynchronizeComplete;
        onPuckClientStarted = OnPuckClientStarted;
        onPuckClientStopped = OnPuckClientStopped;

        try
        {
            EventManager.AddEventListener("Event_Everyone_OnLevelSpawned", onLevelSpawned);
            EventManager.AddEventListener("Event_Everyone_OnLevelDespawned", onLevelDespawned);
            EventManager.AddEventListener("Event_Server_OnClientSceneSynchronizeComplete", onClientSceneSyncComplete);
            // Workshop mods often enable on server join (after ClientStarted). App-start
            // mods need ClientStarted/Stopped to catch connect + reconnect.
            EventManager.AddEventListener("Event_OnClientStarted", onPuckClientStarted);
            EventManager.AddEventListener("Event_OnClientStopped", onPuckClientStopped);
            gameEventsRegistered = true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] RegisterGameEvents failed: " + ex.Message);
        }
    }

    private void UnregisterGameEvents()
    {
        if (!gameEventsRegistered)
            return;

        try
        {
            if (onLevelSpawned != null)
                EventManager.RemoveEventListener("Event_Everyone_OnLevelSpawned", onLevelSpawned);
            if (onLevelDespawned != null)
                EventManager.RemoveEventListener("Event_Everyone_OnLevelDespawned", onLevelDespawned);
            if (onClientSceneSyncComplete != null)
                EventManager.RemoveEventListener("Event_Server_OnClientSceneSynchronizeComplete", onClientSceneSyncComplete);
            if (onPuckClientStarted != null)
                EventManager.RemoveEventListener("Event_OnClientStarted", onPuckClientStarted);
            if (onPuckClientStopped != null)
                EventManager.RemoveEventListener("Event_OnClientStopped", onPuckClientStopped);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] UnregisterGameEvents failed: " + ex.Message);
        }

        gameEventsRegistered = false;
    }

    /// <summary>
    /// Workshop: mod may enable after ClientStarted (LevelSpawned already fired).
    /// App-start: fires when leaving the menu into a session.
    /// </summary>
    private void OnPuckClientStarted(Dictionary<string, object> data)
    {
        if (shutDown)
            return;

        Debug.Log("[FlamiePrac] Event_OnClientStarted — catch-up network + snapshot.");
        RestartNetworkWait("ClientStarted");
    }

    private void OnPuckClientStopped(Dictionary<string, object> data)
    {
        if (shutDown)
            return;

        Debug.Log("[FlamiePrac] Event_OnClientStopped — clearing client mirrors.");
        ClearClientObjects();
        clientAwaitingSnapshot = false;
        clientSnapshotRetries = 0;

        // Pure client leaves session; drop client handlers so the next join re-binds cleanly.
        if (!isServer)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                CustomMessagingManager messaging = nm?.CustomMessagingManager;
                if (messaging != null && clientHandlersRegistered)
                {
                    messaging.UnregisterNamedMessageHandler(ChannelSpawn);
                    messaging.UnregisterNamedMessageHandler(ChannelDespawn);
                    messaging.UnregisterNamedMessageHandler(ChannelSnapshot);
                    messaging.UnregisterNamedMessageHandler(ChannelRadio);
                    SlidableObstacleSync.UnregisterHandlers(messaging);
                    TrainingMotionSync.UnregisterHandlers(messaging);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FlamiePrac] ClientStopped handler cleanup: " + ex.Message);
            }

            clientHandlersRegistered = false;
            networkReady = false;
            isClient = false;
        }
    }

    private void RestartNetworkWait(string reason)
    {
        if (waitForNetworkCoroutine != null)
            StopCoroutine(waitForNetworkCoroutine);

        waitForNetworkCoroutine = StartCoroutine(WaitForNetwork());
        Debug.Log("[FlamiePrac] Network wait restarted (" + reason + ").");
    }

    private void OnLevelSpawned(Dictionary<string, object> data)
    {
        if (shutDown)
            return;

        StickIcePassThrough.ScanSceneFloorIce(logResult: true);
        SlidableBoardCollision.ReassertSlidablePairs();
        SlidableBoardCollision.SyncStickIceLayerPolicy();

        RefreshNetworkRoles();

        // Use live Netcode flags — stale isClient misses LevelSpawned before WaitForNetwork finishes.
        if (IsPureClient())
        {
            Debug.Log("[FlamiePrac] Level spawned — requesting training snapshot.");
            BeginClientSnapshotWait();
            ScheduleSnapshotRequest(0.2f);
            return;
        }

        // Dedicated/listen: if hive was wiped with the old level, AutoStart again.
        if (isServer)
            TrainingObjectManager.Instance?.EnsureTrainingRunningAfterLevelSpawn();
    }

    private void OnLevelDespawned(Dictionary<string, object> data)
    {
        if (shutDown)
            return;

        RefreshNetworkRoles();
        if (IsPureClient())
        {
            ClearClientObjects();
            clientAwaitingSnapshot = true;
            Debug.Log("[FlamiePrac] Level despawned — cleared client training visuals.");
        }
    }

    private void OnClientSceneSynchronizeComplete(Dictionary<string, object> data)
    {
        if (!isServer || shutDown || data == null || !data.ContainsKey("clientId"))
            return;

        try
        {
            ulong clientId = (ulong)data["clientId"];
            if (clientId == NetworkManager.ServerClientId)
                return;

            Debug.Log("[FlamiePrac] Client scene sync complete — queueing snapshot for " + clientId);
            QueueSnapshotToClient(clientId);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] OnClientSceneSynchronizeComplete: " + ex.Message);
        }
    }

    private void ScheduleSnapshotRequest(float delaySeconds)
    {
        if (delayedSnapshotCoroutine != null)
            StopCoroutine(delayedSnapshotCoroutine);

        delayedSnapshotCoroutine = StartCoroutine(RequestSnapshotDelayed(delaySeconds));
    }

    private IEnumerator RequestSnapshotDelayed(float delaySeconds)
    {
        if (delaySeconds > 0f)
            yield return new WaitForSeconds(delaySeconds);

        delayedSnapshotCoroutine = null;
        RequestSnapshot();
    }

    private void BeginClientSnapshotWait()
    {
        clientAwaitingSnapshot = true;
        clientSnapshotRetries = 0;
        clientSnapshotRetryAt = Time.time + 0.5f;
    }

    private void TickClientSnapshotRetry()
    {
        if (!clientAwaitingSnapshot && HasLiveClientVisuals())
            return;

        if (HasLiveClientVisuals())
        {
            clientAwaitingSnapshot = false;
            clientSnapshotRetries = 0;
            return;
        }

        clientAwaitingSnapshot = true;
        if (Time.time < clientSnapshotRetryAt)
            return;

        clientSnapshotRetries++;
        float delay = Mathf.Min(0.4f * clientSnapshotRetries, 2.5f);
        clientSnapshotRetryAt = Time.time + delay;
        nextClientResyncTime = clientSnapshotRetryAt;

        if (clientSnapshotRetries <= 24)
        {
            Debug.Log("[FlamiePrac] Client visuals missing — snapshot retry #" + clientSnapshotRetries);
            RequestSnapshot();
        }
    }

    private void RefreshNetworkRoles()
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null)
                return;

            isServer = nm.IsServer;
            isClient = nm.IsClient;
            networkReady = nm.IsServer || nm.IsClient;
        }
        catch
        {
            // ignored
        }
    }

    private bool IsPureClient()
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm != null && nm.IsClient && !nm.IsServer;
        }
        catch
        {
            return isClient && !isServer;
        }
    }

    /// <summary>Coalesce bursty join events (connect + scene sync + client request) into one send.</summary>
    public void QueueSnapshotToClient(ulong clientId)
    {
        if (!isServer || shutDown || clientId == NetworkManager.ServerClientId)
            return;

        pendingSnapshotClients.Add(clientId);
        if (Time.time >= nextSnapshotFlushTime)
            nextSnapshotFlushTime = Time.time; // flush on next Update
    }

    public void QueueSnapshotToAllClients()
    {
        if (!isServer || shutDown)
            return;

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm?.ConnectedClientsList == null)
                return;

            foreach (NetworkClient client in nm.ConnectedClientsList)
            {
                if (client.ClientId != NetworkManager.ServerClientId)
                    pendingSnapshotClients.Add(client.ClientId);
            }

            nextSnapshotFlushTime = Time.time;
            Debug.Log("[FlamiePrac] Queued training snapshot for all connected clients (" +
                      pendingSnapshotClients.Count + ").");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] QueueSnapshotToAllClients: " + ex.Message);
        }
    }

    private void FlushPendingSnapshots()
    {
        if (pendingSnapshotClients.Count == 0)
            return;

        ulong[] ids = new ulong[pendingSnapshotClients.Count];
        pendingSnapshotClients.CopyTo(ids);
        pendingSnapshotClients.Clear();

        foreach (ulong clientId in ids)
            SendSnapshotToClientNow(clientId);
    }

    private bool HasLiveClientVisuals()
    {
        foreach (GameObject obj in clientObjects.Values)
        {
            if (obj != null)
                return true;
        }

        return false;
    }

    private Transform EnsureClientVisualRoot()
    {
        if (clientVisualRoot != null)
            return clientVisualRoot;

        GameObject root = new GameObject("FlamiePrac_ClientVisuals");
        DontDestroyOnLoad(root);
        clientVisualRoot = root.transform;
        return clientVisualRoot;
    }

    private void DestroyClientVisualRoot()
    {
        if (clientVisualRoot != null)
        {
            Destroy(clientVisualRoot.gameObject);
            clientVisualRoot = null;
        }
    }

    private IEnumerator WaitForNetwork()
    {
        float waited = 0f;
        while (waited < 120f && !shutDown)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && (nm.IsServer || nm.IsClient))
            {
                // Workshop join-enable: session may already be up; messaging can lag one frame.
                if (nm.CustomMessagingManager == null)
                {
                    waited += 0.1f;
                    yield return new WaitForSeconds(0.1f);
                    continue;
                }

                CatchUpAfterNetworkReady(nm);
                yield break;
            }

            waited += 0.25f;
            yield return new WaitForSeconds(0.25f);
        }

        if (!shutDown)
            Debug.LogWarning("[FlamiePrac] TrainingSync gave up waiting for NetworkManager.");
    }

    /// <summary>
    /// Idempotent session catch-up for both enable modes:
    /// 1) App-start: called when the client finally becomes IsClient after menu.
    /// 2) Workshop server-join enable: called immediately — LevelSpawned already happened.
    /// </summary>
    private void CatchUpAfterNetworkReady(NetworkManager nm)
    {
        if (nm == null || shutDown)
            return;

        RegisterHandlers(nm);
        networkReady = true;
        isServer = nm.IsServer;
        isClient = nm.IsClient;

        SlidableBoardCollision.Ensure();
        SlidableBoardCollision.ReassertSlidablePairs();
        SlidableBoardCollision.SyncStickIceLayerPolicy();

        Debug.Log("[FlamiePrac] Network ready — " + FlamiePracVersion.Banner +
                  " IsServer=" + isServer + " IsClient=" + isClient +
                  " Dedicated=" + Application.isBatchMode +
                  " (catch-up: app-start or workshop join-enable)");

        if (isServer)
        {
            EnsureServerRadio();
            if (FlamiePracFeatures.EnableRadio)
                RadioSync.OnServerNetworkReady(this);
            EnsureServerManager();
            // Only nudge spawn if rink ice already exists (workshop mid-session enable).
            // Otherwise TrainingObjectManager's ice-wait coroutine owns first AutoStart —
            // calling Ensure here too early spawned into a void and got wiped on LevelSpawned.
            TrainingObjectManager.Instance?.EnsureTrainingRunningIfIceReady();
            QueueSnapshotToAllClients();
        }

        if (isClient && !isServer)
        {
            // Do not rely on LevelSpawned — workshop enable often misses it.
            BeginClientSnapshotWait();
            RequestSnapshot();
            ScheduleSnapshotRequest(0.5f);
        }
    }

    private void RegisterHandlers(NetworkManager nm)
    {
        CustomMessagingManager messaging = nm.CustomMessagingManager;
        if (messaging == null)
            return;

        if (nm.IsServer && !serverHandlersRegistered)
        {
            messaging.RegisterNamedMessageHandler(ChannelRequestSync, OnRequestSync);
            messaging.RegisterNamedMessageHandler(ChannelRadioRequest, OnRadioRequest);
            messaging.RegisterNamedMessageHandler(ChannelTestPuckSpawnRequest, OnTestPuckSpawnRequest);
            nm.OnClientConnectedCallback += OnClientConnected;
            SubscribeNetworkTick(nm);
            serverHandlersRegistered = true;
        }

        if (nm.IsClient && !clientHandlersRegistered)
        {
            messaging.RegisterNamedMessageHandler(ChannelSpawn, OnSpawnReceived);
            messaging.RegisterNamedMessageHandler(ChannelDespawn, OnDespawnReceived);
            messaging.RegisterNamedMessageHandler(ChannelSnapshot, OnSnapshotReceived);
            messaging.RegisterNamedMessageHandler(ChannelRadio, OnRadioReceived);
            SlidableObstacleSync.EnsureHandlers(nm);
            TrainingMotionSync.EnsureHandlers(nm);
            clientHandlersRegistered = true;
        }
    }

    private void UnregisterHandlers()
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            CustomMessagingManager messaging = nm?.CustomMessagingManager;
            if (messaging == null)
            {
                serverHandlersRegistered = false;
                clientHandlersRegistered = false;
                return;
            }

            if (serverHandlersRegistered)
            {
                messaging.UnregisterNamedMessageHandler(ChannelRequestSync);
                messaging.UnregisterNamedMessageHandler(ChannelRadioRequest);
                messaging.UnregisterNamedMessageHandler(ChannelTestPuckSpawnRequest);
                if (nm != null)
                {
                    nm.OnClientConnectedCallback -= OnClientConnected;
                    UnsubscribeNetworkTick(nm);
                }
            }

            if (clientHandlersRegistered)
            {
                messaging.UnregisterNamedMessageHandler(ChannelSpawn);
                messaging.UnregisterNamedMessageHandler(ChannelDespawn);
                messaging.UnregisterNamedMessageHandler(ChannelSnapshot);
                messaging.UnregisterNamedMessageHandler(ChannelRadio);
                SlidableObstacleSync.UnregisterHandlers(messaging);
                TrainingMotionSync.UnregisterHandlers(messaging);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] UnregisterHandlers: " + ex.Message);
        }
        finally
        {
            serverHandlersRegistered = false;
            clientHandlersRegistered = false;
        }
    }

    private void SubscribeNetworkTick(NetworkManager nm)
    {
        if (tickSubscribed || nm?.NetworkTickSystem == null)
            return;

        nm.NetworkTickSystem.Tick += OnNetworkTick;
        tickSubscribed = true;
        Debug.Log("[FlamiePrac] Slidable sync locked to NetworkTickSystem (" +
                  (nm.NetworkConfig != null ? nm.NetworkConfig.TickRate.ToString() : "?") + " Hz).");
    }

    private void UnsubscribeNetworkTick(NetworkManager nm)
    {
        if (!tickSubscribed || nm?.NetworkTickSystem == null)
        {
            tickSubscribed = false;
            return;
        }

        nm.NetworkTickSystem.Tick -= OnNetworkTick;
        tickSubscribed = false;
    }

    private void EnsureServerManager()
    {
        if (GetComponent<TrainingObjectManager>() != null)
            return;

        gameObject.AddComponent<TrainingObjectManager>();
        Debug.Log("[FlamiePrac] TrainingObjectManager attached on server.");
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!isServer || clientId == NetworkManager.ServerClientId)
            return;

        QueueSnapshotToClient(clientId);
        if (FlamiePracFeatures.EnableRadio)
            RadioSync.SendStateToClient(clientId);
    }

    private void EnsureServerRadio()
    {
        if (!FlamiePracFeatures.EnableRadio || GetComponent<RadioController>() != null)
            return;

        gameObject.AddComponent<RadioController>();
        if (!Application.isBatchMode && GetComponent<RadioHudDriver>() == null)
            gameObject.AddComponent<RadioHudDriver>();
    }

    private void RequestSnapshot()
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.CustomMessagingManager == null)
                return;

            clientAwaitingSnapshot = true;

            using (FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)1);
                nm.CustomMessagingManager.SendNamedMessage(
                    ChannelRequestSync,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.Reliable);
            }

            Debug.Log("[FlamiePrac] Requested training snapshot from server.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] RequestSnapshot failed: " + ex.Message);
            clientAwaitingSnapshot = true;
            clientSnapshotRetryAt = Time.time + 0.5f;
        }
    }

    private void OnRequestSync(ulong senderClientId, FastBufferReader reader)
    {
        if (!isServer)
            return;

        QueueSnapshotToClient(senderClientId);
    }

    public void BroadcastSpawn(TrainingSpawnRecord record)
    {
        if (!isServer)
            return;

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null)
                return;

            using (FastBufferWriter writer = BuildSpawnWriter(record))
            {
                nm.CustomMessagingManager.SendNamedMessageToAll(
                    ChannelSpawn,
                    writer,
                    NetworkDelivery.Reliable);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] BroadcastSpawn failed: " + ex.Message);
        }
    }

    public void RequestTestPuckSpawn(Vector3 bodyPosition, Vector3 bodyForward)
    {
        if (Application.isBatchMode)
            return;

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !networkReady)
                return;

            if (nm.IsServer)
            {
                FlamiePracTestPuckSpawn.TrySpawnForClient(nm.LocalClientId, bodyPosition, bodyForward);
                return;
            }

            if (!nm.IsClient || nm.CustomMessagingManager == null)
                return;

            using (FastBufferWriter writer = new FastBufferWriter(sizeof(float) * 6, Allocator.Temp))
            {
                writer.WriteValueSafe(bodyPosition.x);
                writer.WriteValueSafe(bodyPosition.y);
                writer.WriteValueSafe(bodyPosition.z);
                writer.WriteValueSafe(bodyForward.x);
                writer.WriteValueSafe(bodyForward.y);
                writer.WriteValueSafe(bodyForward.z);
                nm.CustomMessagingManager.SendNamedMessage(
                    ChannelTestPuckSpawnRequest,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.Reliable);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] RequestTestPuckSpawn failed: " + ex.Message);
        }
    }

    private void OnTestPuckSpawnRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (!isServer)
            return;

        reader.ReadValueSafe(out float px);
        reader.ReadValueSafe(out float py);
        reader.ReadValueSafe(out float pz);
        reader.ReadValueSafe(out float fx);
        reader.ReadValueSafe(out float fy);
        reader.ReadValueSafe(out float fz);

        FlamiePracTestPuckSpawn.TrySpawnForClient(
            senderClientId,
            new Vector3(px, py, pz),
            new Vector3(fx, fy, fz));
    }

    public void RequestRadioCommand(byte command)
    {
        if (Application.isBatchMode)
            return;

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !networkReady)
            {
                ApplyRadioCommandLocal(command);
                return;
            }

            if (nm.IsServer)
            {
                BroadcastRadioCommand(command);
                return;
            }

            if (!nm.IsClient || nm.CustomMessagingManager == null)
                return;

            using (FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp))
            {
                writer.WriteValueSafe(command);
                nm.CustomMessagingManager.SendNamedMessage(
                    ChannelRadioRequest,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.Reliable);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] RequestRadioCommand failed: " + ex.Message);
            ApplyRadioCommandLocal(command);
        }
    }

    private void OnRadioRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (!isServer)
            return;

        if (FlamiePracFeatures.RadioServerDrivenOnly || RadioSync.ServerReady)
        {
            RadioSync.HandleRadioRequest(senderClientId, reader);
            return;
        }

        reader.ReadValueSafe(out byte command);
        BroadcastRadioCommand(command);
    }

    public void BroadcastRadioCommand(byte command)
    {
        if (!isServer)
            return;

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager != null)
            {
                using (FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp))
                {
                    writer.WriteValueSafe(command);
                    nm.CustomMessagingManager.SendNamedMessageToAll(
                        ChannelRadio,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }

            // Always apply locally on the listening peer (host or offline), even if messaging is unavailable.
            ApplyRadioCommandLocal(command);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] BroadcastRadioCommand failed: " + ex.Message);
            ApplyRadioCommandLocal(command);
        }
    }

    private void OnRadioReceived(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        // Pure clients only — host/server already applied in BroadcastRadioCommand.
        if (!isClient || nm == null || nm.IsServer || senderClientId != NetworkManager.ServerClientId)
            return;

        if (FlamiePracFeatures.RadioServerDrivenOnly || RadioSync.ServerReady)
        {
            RadioSync.EnsureClientRadio(this);
            RadioSync.HandleRadioStateMessage(reader);
            return;
        }

        reader.ReadValueSafe(out byte command);
        ApplyRadioCommandLocal(command);
    }

    private static void ApplyRadioCommandLocal(byte command)
    {
        if (Application.isBatchMode)
            return;

        if (RadioController.Instance == null)
        {
            Debug.LogWarning("[FlamiePrac] Radio command ignored — no RadioController in scene.");
            return;
        }

        if (command == RadioController.CmdNext)
            RadioController.Instance.NextSong();
        else if (command == RadioController.CmdPrev)
            RadioController.Instance.PreviousSong();
        else
            Debug.LogWarning("[FlamiePrac] Unknown radio command byte: " + command);
    }

    public void BroadcastDespawn(int syncId)
    {
        if (!isServer)
            return;

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null)
                return;

            using (FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(syncId);
                nm.CustomMessagingManager.SendNamedMessageToAll(
                    ChannelDespawn,
                    writer,
                    NetworkDelivery.Reliable);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] BroadcastDespawn failed: " + ex.Message);
        }
    }

    public void SendSnapshotToClient(ulong clientId) => QueueSnapshotToClient(clientId);

    private void SendSnapshotToClientNow(ulong clientId)
    {
        TrainingObjectManager mgr = TrainingObjectManager.Instance;
        if (mgr == null)
        {
            pendingSnapshotClients.Add(clientId);
            return;
        }

        List<TrainingSpawnRecord> records = mgr.GetSpawnRecords();
        if (records.Count == 0)
        {
            // Join beat AutoStart — keep retrying until hive exists.
            pendingSnapshotClients.Add(clientId);
            Debug.Log("[FlamiePrac] Snapshot deferred for client " + clientId +
                      " — no spawn records yet.");
            return;
        }

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null)
            {
                pendingSnapshotClients.Add(clientId);
                return;
            }

            int estimatedSize = 4 + records.Count * 128;
            using (FastBufferWriter writer = new FastBufferWriter(estimatedSize, Allocator.Temp))
            {
                writer.WriteValueSafe(records.Count);
                foreach (TrainingSpawnRecord record in records)
                    WriteSpawnRecord(writer, record);

                nm.CustomMessagingManager.SendNamedMessage(
                    ChannelSnapshot,
                    clientId,
                    writer,
                    NetworkDelivery.Reliable);
            }

            Debug.Log("[FlamiePrac] Sent snapshot (" + records.Count + " object(s)) to client " + clientId);
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] SendSnapshotToClient failed: " + ex.Message);
            pendingSnapshotClients.Add(clientId);
        }
    }

    private void OnSpawnReceived(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (!isClient || nm == null || nm.IsServer || senderClientId != NetworkManager.ServerClientId)
            return;

        try
        {
            TrainingSpawnRecord record = ReadSpawnRecord(reader);
            ApplyClientSpawn(record);
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] OnSpawnReceived failed: " + ex.Message);
        }
    }

    private void OnDespawnReceived(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (!isClient || nm == null || nm.IsServer || senderClientId != NetworkManager.ServerClientId)
            return;

        reader.ReadValueSafe(out int syncId);
        ApplyClientDespawn(syncId);
    }

    private void OnSnapshotReceived(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.IsServer || senderClientId != NetworkManager.ServerClientId)
            return;

        // Prefer live role flags — isClient can lag one frame behind connect.
        if (!nm.IsClient)
            return;

        try
        {
            reader.ReadValueSafe(out int count);

            // Parse fully BEFORE clearing — a mid-read failure must not wipe a good previous hive.
            var records = new List<TrainingSpawnRecord>(count);
            for (int i = 0; i < count; i++)
                records.Add(ReadSpawnRecord(reader));

            ClearClientObjects();
            int built = 0;
            for (int i = 0; i < records.Count; i++)
            {
                int before = clientObjects.Count;
                ApplyClientSpawn(records[i]);
                if (clientObjects.Count > before)
                    built++;
            }

            bool live = HasLiveClientVisuals();
            clientAwaitingSnapshot = !live;
            if (live)
                clientSnapshotRetries = 0;

            Debug.Log("[FlamiePrac] Applied snapshot with " + count + " record(s), built=" +
                      built + ", live=" + live + ".");

            if (!live && count > 0)
            {
                Debug.LogWarning("[FlamiePrac] Snapshot applied but no live visuals — will retry.");
                clientSnapshotRetryAt = Time.time + 0.4f;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] OnSnapshotReceived failed: " + ex.Message);
            clientAwaitingSnapshot = true;
            clientSnapshotRetryAt = Time.time + 0.4f;
        }
    }

    private void ApplyClientSpawn(TrainingSpawnRecord record)
    {
        if (Class1.Instance == null)
        {
            Debug.LogError("[FlamiePrac] Client spawn failed: plugin not loaded.");
            return;
        }

        ApplyClientDespawn(record.SyncId);

        GameObject obj = null;
        Vector3 pos = record.Position;
        Quaternion rot = record.Rotation;

        switch (record.Kind)
        {
            case SpawnPrefab:
            {
                GameObject prefab = Class1.Instance.GetPrefab(record.PrefabName);
                if (prefab == null)
                {
                    Debug.LogWarning("[FlamiePrac] Client missing prefab: " + record.PrefabName);
                    return;
                }

                obj = TrainingObjectFactory.BuildPrefab(
                    prefab,
                    record.PrefabName,
                    pos,
                    rot,
                    record.SyncId,
                    TrainingObjectFactory.BuildRole.ClientVisual);
                break;
            }
            case SpawnPasser:
                obj = TrainingObjectFactory.BuildPasser(
                    pos,
                    record.RotationY,
                    record.PasserSpeed,
                    record.Scale,
                    record.SyncId,
                    TrainingObjectFactory.BuildRole.ClientVisual,
                    positionIsFinalWorldCenter: true);
                break;
            case SpawnCircularTarget:
                obj = TrainingObjectFactory.BuildCircularTarget(
                    pos,
                    record.SyncId,
                    TrainingObjectFactory.BuildRole.ClientVisual);
                break;
        }

        if (obj != null)
        {
            // Keep mirrors across rink scene swaps until LevelDespawned clears them.
            // Parent the ownership root (PassBackAnchor for passers) — not the board child.
            // Passers are pose-locked; reparenting the child under DDOL used to put local
            // (0,0,0) at world origin (center ice) when slide sync was still writing poses.
            Transform clientRoot = EnsureClientVisualRoot();
            Transform ownership = GetClientOwnershipRoot(obj);
            ownership.SetParent(clientRoot, true);
            // Re-assert server pose after DDOL reparent (anchor + hierarchy can drift a hair).
            if (record.Kind == SpawnPasser)
                ownership.SetPositionAndRotation(record.Position, Quaternion.Euler(0f, record.RotationY, 0f));
            // Beams/speakers were detached from the hive for pose sync — park them on DDOL too.
            SlidableObstacleSync.AttachVisualsToClientRoot(record.SyncId, clientRoot);
            clientObjects[record.SyncId] = obj;
        }
    }

    /// <summary>
    /// Passers live under PassBackAnchor_* (DDOL ownership). Hive/prefab roots are themselves
    /// the ownership root.
    /// </summary>
    private static Transform GetClientOwnershipRoot(GameObject obj)
    {
        if (obj == null)
            return null;

        Transform t = obj.transform;
        Transform parent = t.parent;
        if (parent != null && parent.name.StartsWith("PassBackAnchor_"))
            return parent;

        return t;
    }

    private void ApplyClientDespawn(int syncId)
    {
        if (!clientObjects.TryGetValue(syncId, out GameObject obj))
            return;

        clientObjects.Remove(syncId);
        TrainingMotionSync.UnregisterSyncId(syncId);
        // Slidables were unparented from the hive for pose sync — destroy them explicitly.
        SlidableObstacleSync.DestroyVisualsForSyncId(syncId);
        DestroyClientOwnedObject(obj);
    }

    private void ClearClientObjects()
    {
        foreach (KeyValuePair<int, GameObject> pair in clientObjects)
            SlidableObstacleSync.DestroyVisualsForSyncId(pair.Key);

        foreach (GameObject obj in clientObjects.Values)
            DestroyClientOwnedObject(obj);

        clientObjects.Clear();
        TrainingMotionSync.UnregisterAll();
        SlidableObstacleSync.UnregisterAll();
    }

    private void DestroyClientOwnedObject(GameObject obj)
    {
        if (obj == null)
            return;

        Transform ownership = GetClientOwnershipRoot(obj);
        if (ownership != null)
            Destroy(ownership.gameObject);
        else
            Destroy(obj);
    }

    private static FastBufferWriter BuildSpawnWriter(TrainingSpawnRecord record)
    {
        FastBufferWriter writer = new FastBufferWriter(128, Allocator.Temp);
        WriteSpawnRecord(writer, record);
        return writer;
    }

    private static void WriteSpawnRecord(FastBufferWriter writer, TrainingSpawnRecord record)
    {
        writer.WriteValueSafe(record.Kind);
        writer.WriteValueSafe(record.SyncId);
        writer.WriteValueSafe(record.Position.x);
        writer.WriteValueSafe(record.Position.y);
        writer.WriteValueSafe(record.Position.z);
        writer.WriteValueSafe(record.Rotation.x);
        writer.WriteValueSafe(record.Rotation.y);
        writer.WriteValueSafe(record.Rotation.z);
        writer.WriteValueSafe(record.Rotation.w);

        if (record.Kind == SpawnPrefab)
            WriteString(writer, record.PrefabName ?? "");
        else if (record.Kind == SpawnPasser)
        {
            writer.WriteValueSafe(record.RotationY);
            writer.WriteValueSafe(record.PasserSpeed);
            writer.WriteValueSafe(record.Scale.x);
            writer.WriteValueSafe(record.Scale.y);
            writer.WriteValueSafe(record.Scale.z);
        }
    }

    private static TrainingSpawnRecord ReadSpawnRecord(FastBufferReader reader)
    {
        reader.ReadValueSafe(out byte kind);
        reader.ReadValueSafe(out int syncId);
        reader.ReadValueSafe(out float px);
        reader.ReadValueSafe(out float py);
        reader.ReadValueSafe(out float pz);
        reader.ReadValueSafe(out float qx);
        reader.ReadValueSafe(out float qy);
        reader.ReadValueSafe(out float qz);
        reader.ReadValueSafe(out float qw);

        var record = new TrainingSpawnRecord
        {
            Kind = kind,
            SyncId = syncId,
            Position = new Vector3(px, py, pz),
            Rotation = new Quaternion(qx, qy, qz, qw)
        };

        if (kind == SpawnPrefab)
            record.PrefabName = ReadString(reader);
        else if (kind == SpawnPasser)
        {
            reader.ReadValueSafe(out float rotY);
            reader.ReadValueSafe(out float speed);
            reader.ReadValueSafe(out float sx);
            reader.ReadValueSafe(out float sy);
            reader.ReadValueSafe(out float sz);
            record.RotationY = rotY;
            record.PasserSpeed = speed;
            record.Scale = new Vector3(sx, sy, sz);
        }

        return record;
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
}

public struct TrainingSpawnRecord
{
    public byte Kind;
    public int SyncId;
    public string PrefabName;
    public Vector3 Position;
    public Quaternion Rotation;
    public float RotationY;
    public float PasserSpeed;
    public Vector3 Scale;
}
