using UnityEngine;
using Unity.Netcode;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MyMod;

/// <summary>
/// Server-side spawn authority for training tools. Clients mirror via TrainingSync.
/// </summary>
public class TrainingObjectManager : MonoBehaviour
{
    public static TrainingObjectManager Instance { get; private set; }

    public static bool IsModEnabled =>
        Instance != null && Instance.modEnabled;

    private readonly Dictionary<int, SpawnedTrainingObject> spawnedObjects =
        new Dictionary<int, SpawnedTrainingObject>();

    private readonly Dictionary<int, TrainingSpawnRecord> spawnRecords =
        new Dictionary<int, TrainingSpawnRecord>();

    private readonly Dictionary<ulong, List<int>> playerObjects = new Dictionary<ulong, List<int>>();
    private readonly List<CircularMovingTarget> activeTargets = new List<CircularMovingTarget>();

    private int nextObjectId = 1;
    private bool modEnabled;
    private float nextCleanupTime;

    private const int MaxObjects = 50;
    private const byte RadioNext = 1;
    private const byte RadioPrev = 2;

    private Action<Dictionary<string, object>> _onChatCommand;
    private bool shutDown;

    private struct SpawnedTrainingObject
    {
        public int Id;
        public string PrefabName;
        public GameObject Object;
        public ulong SpawnedBy;
        public float SpawnTime;
    }

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        Debug.Log("[FlamiePrac] TrainingObjectManager Awake()");
    }

    private void Start()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return;

        try
        {
            _onChatCommand = OnChatCommand;
            EventManager.AddEventListener(
                "Event_Server_OnChatCommand",
                _onChatCommand);
            Debug.Log("[FlamiePrac] Registered chat command listener");
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] Failed to register chat command listener: " + ex.Message);
        }

        // Dedicated boots can finish netcode before rink ice exists — wait, then AutoStart.
        StartCoroutine(StartTrainingModeWhenReady());
    }

    private IEnumerator StartTrainingModeWhenReady()
    {
        float waited = 0f;
        const float maxWait = 45f;

        while (waited < maxWait)
        {
            if (shutDown)
                yield break;

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                yield break;

            if (HasUsableRinkIce())
                break;

            waited += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }

        // Small settle after ice appears (or timeout) before spawning hive/passers.
        yield return new WaitForSeconds(1.5f);
        StartTrainingMode();
    }

    private static bool HasUsableRinkIce()
    {
        int ice = LayerMask.NameToLayer("Ice");
        if (ice < 0)
            return true;

        Vector3 origin = new Vector3(0f, 8f, 0f);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 20f, 1 << ice,
                QueryTriggerInteraction.Ignore))
        {
            return Vector3.Dot(hit.normal, Vector3.up) > 0.7f && hit.point.y > -2f && hit.point.y < 3f;
        }

        return false;
    }

    private void OnDestroy()
    {
        Shutdown();

        if (Instance == this)
            Instance = null;
    }

    /// <summary>Idempotent server teardown — despawn props, unregister chat, cancel pending invokes.</summary>
    public void Shutdown()
    {
        if (shutDown)
            return;

        shutDown = true;
        modEnabled = false;

        CancelInvoke();

        if (_onChatCommand != null)
        {
            try
            {
                EventManager.RemoveEventListener(
                    "Event_Server_OnChatCommand",
                    _onChatCommand);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FlamiePrac] RemoveEventListener failed: " + ex.Message);
            }

            _onChatCommand = null;
        }

        List<int> ids = spawnedObjects.Keys.ToList();
        foreach (int id in ids)
        {
            if (!spawnedObjects.TryGetValue(id, out SpawnedTrainingObject entry))
                continue;

            if (entry.Object != null)
                Destroy(entry.Object);
        }

        spawnedObjects.Clear();
        spawnRecords.Clear();
        playerObjects.Clear();
        activeTargets.Clear();
        TrainingMotionSync.UnregisterAll();

        FlamiePracTrainingGoalie.Despawn();

        Debug.Log("[FlamiePrac] TrainingObjectManager shutdown (" + ids.Count + " object(s) removed).");
    }

    private void Update()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return;

        if (Time.time < nextCleanupTime)
            return;

        nextCleanupTime = Time.time + 30f;
        CleanupStaleReferences();
    }

    public List<TrainingSpawnRecord> GetSpawnRecords()
    {
        return spawnRecords.Values.ToList();
    }

    private void StartTrainingMode()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return;

        if (modEnabled)
            return;

        modEnabled = true;
        Debug.Log("[FlamiePrac] Starting training mode");

        TrainingLayoutConfig.LayoutFile layout = TrainingLayoutConfig.Current;
        if (!layout.AutoStart)
        {
            Debug.Log("[FlamiePrac] AutoStart disabled in layout.");
            return;
        }

        foreach (TrainingLayoutConfig.SpawnEntry entry in layout.Spawns)
            ApplyLayoutEntry(entry, 0);

        // Late joiners that requested while records were empty need a push now.
        TrainingSync.Instance?.QueueSnapshotToAllClients();
    }

    /// <summary>
    /// After a rink reload, scene-parented hive props may be gone while modEnabled stays true.
    /// Respawn from layout when nothing live remains.
    /// </summary>
    public void EnsureTrainingRunningAfterLevelSpawn()
    {
        EnsureTrainingRunningIfIceReady(forceIceCheck: false);
    }

    /// <summary>
    /// Workshop mid-session server enable / network catch-up: only AutoStart when ice exists.
    /// Avoids spawning into a pre-level void that LevelSpawned then destroys.
    /// </summary>
    public void EnsureTrainingRunningIfIceReady(bool forceIceCheck = true)
    {
        if (shutDown || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return;

        CleanupStaleReferences();

        bool anyLive = false;
        foreach (SpawnedTrainingObject entry in spawnedObjects.Values)
        {
            if (entry.Object != null)
            {
                anyLive = true;
                break;
            }
        }

        if (anyLive)
        {
            TrainingSync.Instance?.QueueSnapshotToAllClients();
            return;
        }

        if (forceIceCheck && !HasUsableRinkIce())
        {
            Debug.Log("[FlamiePrac] Catch-up: rink ice not ready yet — AutoStart coroutine will spawn.");
            return;
        }

        Debug.Log("[FlamiePrac] Level/catch-up with no live training props — restarting AutoStart.");
        modEnabled = false;
        spawnRecords.Clear();
        spawnedObjects.Clear();
        playerObjects.Clear();
        activeTargets.Clear();
        StartTrainingMode();
    }

    private void ApplyLayoutEntry(TrainingLayoutConfig.SpawnEntry entry, ulong spawnedBy)
    {
        if (entry == null)
            return;

        string type = (entry.Type ?? "prefab").ToLowerInvariant();
        Vector3 pos = entry.Position != null ? entry.Position.ToVector3() : Vector3.zero;
        Quaternion rot = Quaternion.Euler(0f, entry.RotationY, 0f);

        if (type == "passer")
        {
            Vector3 scale = entry.Scale != null ? entry.Scale.ToVector3() : new Vector3(2f, 0.5f, 0.5f);
            SpawnOnePasser(pos, entry.RotationY, entry.Speed, scale, spawnedBy);
            return;
        }

        if (type == "target")
        {
            SpawnCircularTarget(pos);
            return;
        }

        GameObject prefab = Class1.Instance?.GetPrefab(entry.Name ?? "trainingprefab");
        if (prefab == null)
        {
            Debug.LogError("[FlamiePrac] Layout prefab not found: " + entry.Name);
            return;
        }

        int id = SpawnTrainingObject(prefab, entry.Name ?? "trainingprefab", pos, rot, spawnedBy);
        if (id >= 0 && IsTrainingHivePrefab(entry.Name))
        {
            if (spawnedObjects.TryGetValue(id, out SpawnedTrainingObject hiveEntry) && hiveEntry.Object != null)
                FlamiePracTrainingGoalie.SpawnForHive(hiveEntry.Object);
        }
    }

    private static bool IsTrainingHivePrefab(string name)
    {
        return string.Equals(name ?? string.Empty, "trainingprefab", StringComparison.OrdinalIgnoreCase);
    }

    private void OnChatCommand(Dictionary<string, object> data)
    {
        try
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            if (!data.ContainsKey("command"))
                return;

            string command = (data["command"] as string ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(command))
                return;

            string[] args = data.ContainsKey("args")
                ? (data["args"] as string[] ?? new string[0])
                : new string[0];

            ulong senderClientId = 0;
            if (data.ContainsKey("clientId"))
                senderClientId = (ulong)data["clientId"];

            Player senderPlayer = GetPlayerByClientId(senderClientId);

            switch (command)
            {
                case "/speed":
                case "/lu54bdhrtjr":
                    HandleSpeedCommand(senderClientId, args);
                    break;

                case "/nextsong":
                case "/radioskip":  // alias; /skip is a stock admin command
                    Debug.Log("[FlamiePrac] Server radio command: next");
                    TrainingSync.Instance?.BroadcastRadioCommand(RadioNext);
                    break;

                case "/prevsong":
                case "/radioprev":  // alias; avoid future conflicts
                    Debug.Log("[FlamiePrac] Server radio command: prev");
                    TrainingSync.Instance?.BroadcastRadioCommand(RadioPrev);
                    break;

                case "/trainhere":
                    HandleTrainHere(senderPlayer, senderClientId, args);
                    break;

                case "/traindump":
                    HandleTrainDump(senderClientId);
                    break;

                case "/trainreload":
                    TrainingLayoutConfig.Reload();
                    TrainingPrefabRenamer.Reload();
                    SendMessageToClient(senderClientId, "Reloaded training_layout.json + training_prefab_names.json");
                    break;

                case "/targetpractise":
                    SpawnCircularTarget(new Vector3(0f, 1.4f, 40f));
                    SendMessageToClient(senderClientId, "Spawned circular target.");
                    break;

                case "/cleartargetpractise":
                    ClearCircularTargets();
                    SendMessageToClient(senderClientId, "Cleared circular targets.");
                    break;

                case "/passer":
                    SpawnPassBackBox(senderClientId);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] Error handling chat command: " + ex);
        }
    }

    private void HandleSpeedCommand(ulong clientId, string[] args)
    {
        if (args.Length < 1)
        {
            SendMessageToClient(clientId, "Usage: /speed <number>");
            return;
        }

        if (float.TryParse(args[0], out float newSpeed))
        {
            ConstantRotator.globalSpeed = newSpeed;
            TrainingMotionSync.BroadcastParamsNow();
            SendMessageToClient(clientId, "Rotation speed set to " + newSpeed);
        }
        else
        {
            SendMessageToClient(clientId, "Invalid speed.");
        }
    }

    private void HandleTrainHere(Player player, ulong clientId, string[] args)
    {
        if (player == null)
        {
            SendMessageToClient(clientId, "Player not found.");
            return;
        }

        string prefabName = args.Length > 0 ? args[0].ToLowerInvariant() : "trainingprefab";
        GameObject prefab = Class1.Instance?.GetPrefab(prefabName);
        if (prefab == null)
        {
            SendMessageToClient(clientId, "Prefab not found: " + prefabName);
            return;
        }

        Vector3 pos = player.transform.position;
        pos.y = 0f;
        int id = SpawnTrainingObject(prefab, prefabName, pos, Quaternion.identity, clientId);

        if (id >= 0)
        {
            TrainingLayoutConfig.AppendSpawn(new TrainingLayoutConfig.SpawnEntry
            {
                Type = "prefab",
                Name = prefabName,
                Position = new TrainingLayoutConfig.Vec3 { x = pos.x, y = pos.y, z = pos.z }
            });
            SendMessageToClient(clientId,
                "Spawned + saved " + prefabName + " at (" +
                pos.x.ToString("F1") + ", " + pos.y.ToString("F1") + ", " + pos.z.ToString("F1") + ")");
        }
    }

    private void HandleTrainDump(ulong clientId)
    {
        foreach (TrainingSpawnRecord record in spawnRecords.Values)
        {
            string line = "[FlamiePrac] #" + record.SyncId + " kind=" + record.Kind +
                            " pos=" + record.Position;
            Debug.Log(line);
        }

        SendMessageToClient(clientId, "Dumped " + spawnRecords.Count + " spawn(s) to server log.");
    }

    public int SpawnTrainingObject(
        GameObject prefab,
        string prefabName,
        Vector3 position,
        Quaternion rotation,
        ulong spawnedBy)
    {
        if (prefab == null)
            return -1;

        if (spawnedObjects.Count >= MaxObjects)
        {
            SendMessageToClient(spawnedBy, "Max training objects reached.");
            return -1;
        }

        try
        {
            int id = nextObjectId++;
            GameObject obj = TrainingObjectFactory.BuildPrefab(
                prefab,
                prefabName,
                position,
                rotation,
                id,
                TrainingObjectFactory.BuildRole.ServerAuthority);

            RegisterSpawn(id, prefabName.ToLowerInvariant(), obj, spawnedBy);

            var record = new TrainingSpawnRecord
            {
                Kind = 1,
                SyncId = id,
                PrefabName = prefabName.ToLowerInvariant(),
                Position = position,
                Rotation = rotation
            };
            spawnRecords[id] = record;
            TrainingSync.Instance?.BroadcastSpawn(record);

            Debug.Log("[FlamiePrac] Spawned '" + prefabName + "' (#" + id + ") at " + position);
            return id;
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] Failed to spawn training object: " + ex);
            SendMessageToClient(spawnedBy, "Failed to spawn: " + ex.Message);
            return -1;
        }
    }

    private void SpawnPassBackBox(ulong clientId)
    {
        float length = TrainingLayoutConfig.DefaultPasserLength;
        float goalZ = TrainingLayoutConfig.PasserCenterZ(length, TrainingLayoutConfig.DefaultPasserRotationY);
        Vector3 scale = new Vector3(length, 0.55f, 0.5f);
        SpawnOnePasser(new Vector3(6f, 0f, goalZ), TrainingLayoutConfig.DefaultPasserRotationY, 14f, scale, clientId);
        SpawnOnePasser(new Vector3(-6f, 0f, goalZ), -TrainingLayoutConfig.DefaultPasserRotationY, 14f, scale, clientId);
        SendMessageToClient(clientId, "2 puck passers spawned.");
    }

    private void SpawnOnePasser(Vector3 pos, float yRot, float speed, Vector3 scale, ulong spawnedBy)
    {
        int id = nextObjectId++;
        GameObject box = TrainingObjectFactory.BuildPasser(
            pos,
            yRot,
            speed,
            scale,
            id,
            TrainingObjectFactory.BuildRole.ServerAuthority);

        RegisterSpawn(id, "passer", box, spawnedBy);

        // Broadcast the seated world center — clients must not re-apply layout Y lift / ice guess.
        var record = new TrainingSpawnRecord
        {
            Kind = 2,
            SyncId = id,
            Position = box.transform.position,
            Rotation = box.transform.rotation,
            RotationY = yRot,
            PasserSpeed = speed,
            Scale = scale
        };
        spawnRecords[id] = record;
        TrainingSync.Instance?.BroadcastSpawn(record);
    }

    public void SpawnCircularTarget(Vector3 position)
    {
        int id = nextObjectId++;
        GameObject targetObj = TrainingObjectFactory.BuildCircularTarget(
            position,
            id,
            TrainingObjectFactory.BuildRole.ServerAuthority);

        RegisterSpawn(id, "circulartarget", targetObj, 0);
        activeTargets.Add(targetObj.GetComponent<CircularMovingTarget>());

        var record = new TrainingSpawnRecord
        {
            Kind = 3,
            SyncId = id,
            Position = position,
            Rotation = Quaternion.identity
        };
        spawnRecords[id] = record;
        TrainingSync.Instance?.BroadcastSpawn(record);
    }

    public void ClearCircularTargets()
    {
        foreach (CircularMovingTarget target in activeTargets)
        {
            if (target == null)
                continue;

            TrainingSyncMarker marker = target.GetComponent<TrainingSyncMarker>();
            if (marker != null)
                DespawnById(marker.SyncId);
        }

        activeTargets.Clear();
    }

    public void MoveTarget(CircularMovingTarget target)
    {
        if (target == null)
            return;

        Vector3 newPos = target.transform.position;
        newPos.x += UnityEngine.Random.Range(-1.9f, 1.9f);
        newPos.y += UnityEngine.Random.Range(0f, 1f);
        target.transform.position = newPos;
    }

    private void DespawnById(int id)
    {
        if (spawnedObjects.TryGetValue(id, out SpawnedTrainingObject entry))
        {
            if (entry.Object != null)
                Destroy(entry.Object);
            spawnedObjects.Remove(id);
        }

        spawnRecords.Remove(id);
        TrainingMotionSync.UnregisterSyncId(id);
        TrainingSync.Instance?.BroadcastDespawn(id);
    }

    private void RegisterSpawn(int id, string prefabName, GameObject obj, ulong spawnedBy)
    {
        spawnedObjects[id] = new SpawnedTrainingObject
        {
            Id = id,
            PrefabName = prefabName,
            Object = obj,
            SpawnedBy = spawnedBy,
            SpawnTime = Time.time
        };

        if (!playerObjects.ContainsKey(spawnedBy))
            playerObjects[spawnedBy] = new List<int>();
        playerObjects[spawnedBy].Add(id);
    }

    private void CleanupStaleReferences()
    {
        var toRemove = new List<int>();
        foreach (KeyValuePair<int, SpawnedTrainingObject> kvp in spawnedObjects)
        {
            if (kvp.Value.Object == null)
                toRemove.Add(kvp.Key);
        }

        foreach (int id in toRemove)
        {
            spawnedObjects.Remove(id);
            spawnRecords.Remove(id);
        }

        if (toRemove.Count > 0)
            Debug.Log("[FlamiePrac] Cleaned up " + toRemove.Count + " stale reference(s)");
    }

    private Player GetPlayerByClientId(ulong clientId)
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null)
                return null;

            foreach (NetworkClient client in nm.ConnectedClientsList)
            {
                if (client.ClientId == clientId)
                    return client.PlayerObject?.GetComponent<Player>();
            }
        }
        catch { }

        return null;
    }

    private void SendMessageToClient(ulong clientId, string message)
    {
        if (clientId == 0)
            return;

        try
        {
            ChatManager chat = FindFirstObjectByType<ChatManager>();
            if (chat == null)
                return;

            chat.Server_SendChatMessage(message, "#88FF88", clientId);
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] SendMessage failed: " + ex);
        }
    }
}
