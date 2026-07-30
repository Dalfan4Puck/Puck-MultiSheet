using System;
using Object = UnityEngine.Object;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using MyMod;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class TrainingObjectManager : MonoBehaviour
{
    private struct SpawnedTrainingObject
    {
        public int Id;

        public string PrefabName;

        public GameObject Object;

        public ulong SpawnedBy;

        public float SpawnTime;

        public int RinkIndex;
    }

    [CompilerGenerated]
    private sealed class _003CStartTrainingModeWhenReady_003Ed__27 : IEnumerator<object>, IEnumerator, IDisposable
    {
        private int _003C_003E1__state;

        private object _003C_003E2__current;

        public TrainingObjectManager _003C_003E4__this;

        private float _003Cwaited_003E5__2;

        object IEnumerator<object>.Current
        {
            [DebuggerHidden]
            get
            {
                return _003C_003E2__current;
            }
        }

        object IEnumerator.Current
        {
            [DebuggerHidden]
            get
            {
                return _003C_003E2__current;
            }
        }

        [DebuggerHidden]
        public _003CStartTrainingModeWhenReady_003Ed__27(int _003C_003E1__state)
        {
            this._003C_003E1__state = _003C_003E1__state;
        }

        [DebuggerHidden]
        void IDisposable.Dispose()
        {
            _003C_003E1__state = -2;
        }

        private bool MoveNext()
        {
            //IL_00a7: Unknown result type (might be due to invalid IL or missing references)
            //IL_00b1: Expected O, but got Unknown
            //IL_007a: Unknown result type (might be due to invalid IL or missing references)
                        int num = _003C_003E1__state;
            TrainingObjectManager trainingObjectManager = _003C_003E4__this;
            switch (num)
            {
            default:
                return false;
            case 0:
                _003C_003E1__state = -1;
                _003Cwaited_003E5__2 = 0f;
                goto IL_0094;
            case 1:
                _003C_003E1__state = -1;
                goto IL_0094;
            case 2:
                {
                    _003C_003E1__state = -1;
                    if (SkipAutoStartForMultiRink)
                    {
                        FlamieLog.Info("[FlamiePrac] MultiSheet per-rink strip — AutoStart deferred to RinkStripVote.");
                        return false;
                    }
                    trainingObjectManager.StartTrainingMode();
                    return false;
                }
                IL_0094:
                if (_003Cwaited_003E5__2 < 45f)
                {
                    if (trainingObjectManager.shutDown)
                    {
                        return false;
                    }
                    if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                    {
                        return false;
                    }
                    if (!HasUsableRinkIce())
                    {
                        _003Cwaited_003E5__2 += 0.5f;
                        _003C_003E2__current = (object)new WaitForSeconds(0.5f);
                        _003C_003E1__state = 1;
                        return true;
                    }
                }
                _003C_003E2__current = (object)new WaitForSeconds(1.5f);
                _003C_003E1__state = 2;
                return true;
            }
        }

        bool IEnumerator.MoveNext()
        {
            //ILSpy generated this explicit interface implementation from .override directive in MoveNext
            return this.MoveNext();
        }

        [DebuggerHidden]
        void IEnumerator.Reset()
        {
            throw new NotSupportedException();
        }
    }

    private readonly Dictionary<int, SpawnedTrainingObject> spawnedObjects = new Dictionary<int, SpawnedTrainingObject>();

    private readonly Dictionary<int, TrainingSpawnRecord> spawnRecords = new Dictionary<int, TrainingSpawnRecord>();

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

    private readonly Dictionary<int, HashSet<int>> objectsByRink = new Dictionary<int, HashSet<int>>();

    private readonly HashSet<int> toolsEnabledRinks = new HashSet<int>();

    public static TrainingObjectManager Instance { get; private set; }

    public static bool IsModEnabled
    {
        get
        {
            if (Instance != null)
            {
                return Instance.modEnabled;
            }
            return false;
        }
    }

    public static bool SkipAutoStartForMultiRink { get; set; }

    private void Awake()
    {
        if (Instance != null)
        {
            Object.Destroy(this);
            return;
        }
        Instance = this;
        FlamieLog.Info("[FlamiePrac] TrainingObjectManager Awake()");
    }

    private void Start()
    {
        if (!(NetworkManager.Singleton == null) && NetworkManager.Singleton.IsServer)
        {
            try
            {
                _onChatCommand = OnChatCommand;
                EventManager.AddEventListener("Event_Server_OnChatCommand", _onChatCommand);
                FlamieLog.Info("[FlamiePrac] Registered chat command listener");
            }
            catch (Exception ex)
            {
                FlamieLog.Error("[FlamiePrac] Failed to register chat command listener: " + ex.Message);
            }
            if (!SkipAutoStartForMultiRink)
                this.StartCoroutine(StartTrainingModeWhenReady());
            else
                FlamieLog.Info("[FlamiePrac] MultiSheet per-rink strip — legacy AutoStart coroutine skipped.");
        }
    }

    [IteratorStateMachine(typeof(_003CStartTrainingModeWhenReady_003Ed__27))]
    private IEnumerator StartTrainingModeWhenReady()
    {
        //yield-return decompiler failed: Unexpected instruction in Iterator.Dispose()
        return new _003CStartTrainingModeWhenReady_003Ed__27(0)
        {
            _003C_003E4__this = this
        };
    }

    private static bool HasUsableRinkIce()
    {
                                                //IL_006c: Unknown result type (might be due to invalid IL or missing references)
        int num = LayerMask.NameToLayer("Ice");
        if (num < 0)
        {
            return true;
        }
        RaycastHit val = default(RaycastHit);
        if (Physics.Raycast(new Vector3(0f, 8f, 0f), Vector3.down, out val, 20f, 1 << num, QueryTriggerInteraction.Ignore))
        {
            if (Vector3.Dot(val.normal, Vector3.up) > 0.7f && val.point.y > -2f)
            {
                return val.point.y < 3f;
            }
            return false;
        }
        return false;
    }

    private void OnDestroy()
    {
        Shutdown();
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public void RespawnFromLayout()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer || shutDown)
        {
            return;
        }
        TrainingLayoutConfig.Reload();
        TrainingPrefabRenamer.Reload();
        foreach (int item in spawnedObjects.Keys.ToList())
        {
            SlidableObstacle.DestroyForSyncId(item);
            if (spawnedObjects.TryGetValue(item, out var value))
            {
                if (value.Object != null)
                {
                    Object.Destroy(value.Object);
                }
                TrainingSync.Instance?.BroadcastDespawn(item);
            }
        }
        spawnedObjects.Clear();
        spawnRecords.Clear();
        playerObjects.Clear();
        activeTargets.Clear();
        TrainingMotionSync.UnregisterAll();
        FlamiePracTrainingGoalie.Despawn();
        modEnabled = false;
        StartTrainingMode();
        FlamieLog.Info("[FlamiePrac] RespawnFromLayout complete.");
    }

    public void Shutdown()
    {
        if (shutDown)
        {
            return;
        }
        shutDown = true;
        modEnabled = false;
        this.CancelInvoke();
        if (_onChatCommand != null)
        {
            try
            {
                EventManager.RemoveEventListener("Event_Server_OnChatCommand", _onChatCommand);
            }
            catch (Exception ex)
            {
                FlamieLog.Warn("[FlamiePrac] RemoveEventListener failed: " + ex.Message);
            }
            _onChatCommand = null;
        }
        List<int> list = spawnedObjects.Keys.ToList();
        foreach (int item in list)
        {
            if (spawnedObjects.TryGetValue(item, out var value) && value.Object != null)
            {
                Object.Destroy(value.Object);
            }
        }
        spawnedObjects.Clear();
        spawnRecords.Clear();
        playerObjects.Clear();
        activeTargets.Clear();
        TrainingMotionSync.UnregisterAll();
        SlidableObstacle.DestroyAll();
        FlamiePracTrainingGoalie.Despawn();
        FlamieLog.Info("[FlamiePrac] TrainingObjectManager shutdown (" + list.Count + " object(s) removed).");
    }

    private void Update()
    {
        if (!(NetworkManager.Singleton == null) && NetworkManager.Singleton.IsServer && !(Time.time < nextCleanupTime))
        {
            nextCleanupTime = Time.time + 30f;
            CleanupStaleReferences();
        }
    }

    public List<TrainingSpawnRecord> GetSpawnRecords()
    {
        return spawnRecords.Values.ToList();
    }

    public void CollectCullRoots(List<Transform> into)
    {
        if (into == null)
        {
            return;
        }
        foreach (SpawnedTrainingObject value in spawnedObjects.Values)
        {
            if (!(value.Object == null))
            {
                Transform val = value.Object.transform;
                Transform parent = val.parent;
                if (parent != null && parent.name.StartsWith("PassBackAnchor_"))
                {
                    val = parent;
                }
                if (!into.Contains(val))
                {
                    into.Add(val);
                }
            }
        }
    }

    private void StartTrainingMode()
    {
                if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer || modEnabled)
        {
            return;
        }
        if (SkipAutoStartForMultiRink)
        {
            FlamieLog.Info("[FlamiePrac] MultiSheet per-rink strip — StartTrainingMode skipped.");
            return;
        }
        modEnabled = true;
        FlamieLog.InfoOnce("train-start", "[FlamiePrac] Starting training mode");
        TrainingLayoutConfig.LayoutFile current = TrainingLayoutConfig.Current;
        if (!current.AutoStart)
        {
            FlamieLog.Info("[FlamiePrac] AutoStart disabled in layout.");
            return;
        }
        if (!HasUsableRinkIce())
        {
            FlamieLog.Warn("[FlamiePrac] AutoStart with no ice raycast hit — spawning anyway (timeout path).");
        }
        TrainingLayoutConfig.SpawnEntry[] spawns = current.Spawns;
        foreach (TrainingLayoutConfig.SpawnEntry entry in spawns)
        {
            ApplyLayoutEntry(entry, 0uL, spawnGoalie: true, 0, Vector3.zero);
        }
        TrainingSync.Instance?.QueueSnapshotToAllClients();
    }

    public void EnsureTrainingRunningAfterLevelSpawn()
    {
        EnsureTrainingRunningIfIceReady(forceIceCheck: false);
    }

    public void EnsureTrainingRunningIfIceReady(bool forceIceCheck = true)
    {
        if (shutDown || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            return;
        }
        CleanupStaleReferences();
        bool flag = false;
        foreach (SpawnedTrainingObject value in spawnedObjects.Values)
        {
            if (value.Object != null)
            {
                flag = true;
                break;
            }
        }
        if (flag)
        {
            TrainingSync.Instance?.QueueSnapshotToAllClients();
            return;
        }
        if (spawnRecords.Count > 0)
        {
            if (!flag && SkipAutoStartForMultiRink)
            {
                FlamieLog.ServerSyncThrottled(
                    "catchup-records-stale-multisheet",
                    "[FlamiePrac] Catch-up: spawn records exist but hive empty — reconciling strip spawns.",
                    10f);
                ReconcileEnabledRinkSpawns();
                return;
            }

            FlamieLog.ServerSyncThrottled(
                "catchup-records-no-live",
                "[FlamiePrac] Catch-up: " + spawnRecords.Count + " spawn record(s) exist — queueing snapshot (not restarting).",
                10f);
            TrainingSync.Instance?.QueueSnapshotToAllClients();
            return;
        }
        if (SkipAutoStartForMultiRink)
        {
            FlamieLog.ServerSyncThrottled(
                "catchup-multisheet-wait-strip",
                "[FlamiePrac] MultiSheet catch-up: no spawn records yet — waiting on strip defaults.",
                10f);
            return;
        }
        if (forceIceCheck && !HasUsableRinkIce())
        {
            FlamieLog.Info("[FlamiePrac] Catch-up: rink ice not ready yet — AutoStart coroutine will spawn.");
            return;
        }
        FlamieLog.Info("[FlamiePrac] Level/catch-up with no live training props — restarting AutoStart.");
        modEnabled = false;
        spawnRecords.Clear();
        spawnedObjects.Clear();
        playerObjects.Clear();
        activeTargets.Clear();
        StartTrainingMode();
    }

    public void SetRinkToolsEnabled(int rinkIndex, bool enabled)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer || shutDown)
        {
            return;
        }
        if (enabled)
        {
            // UI / serverModes can say "tools on" while the hive was wiped (layout race).
            if (toolsEnabledRinks.Contains(rinkIndex) && !HasLiveToolsOnRink(rinkIndex))
                toolsEnabledRinks.Remove(rinkIndex);

            if (!toolsEnabledRinks.Contains(rinkIndex))
                SpawnLayoutForRink(rinkIndex);
        }
        else if (toolsEnabledRinks.Contains(rinkIndex))
        {
            ClearRinkTools(rinkIndex);
        }
    }

    public bool HasLiveToolsOnRink(int rinkIndex)
    {
        if (!objectsByRink.TryGetValue(rinkIndex, out HashSet<int> ids) || ids == null || ids.Count == 0)
            return false;

        foreach (int id in ids)
        {
            if (spawnedObjects.TryGetValue(id, out SpawnedTrainingObject entry) && entry.Object != null)
                return true;
        }

        return false;
    }

    private void ReconcileEnabledRinkSpawns()
    {
        if (toolsEnabledRinks.Count == 0)
            return;

        List<int> rinks = toolsEnabledRinks.ToList();
        foreach (int rinkIndex in rinks)
        {
            if (HasLiveToolsOnRink(rinkIndex))
                continue;

            toolsEnabledRinks.Remove(rinkIndex);
            SpawnLayoutForRink(rinkIndex);
        }
    }

    public bool IsRinkToolsEnabled(int rinkIndex)
    {
        return toolsEnabledRinks.Contains(rinkIndex);
    }

    private void SpawnLayoutForRink(int rinkIndex)
    {
        //IL_002c: Unknown result type (might be due to invalid IL or missing references)
                        TrainingLayoutConfig.LayoutFile current = TrainingLayoutConfig.Current;
        if (!current.AutoStart)
        {
            FlamieLog.Info("[FlamiePrac] AutoStart disabled — skipping strip spawn for rink " + (rinkIndex + 1));
            return;
        }
        Vector3 worldOffset = RinkOrigin.OriginFor(rinkIndex + 1);
        FlamieLog.Info("[FlamiePrac] Spawning PHL tools on rink " + (rinkIndex + 1) + " at " + worldOffset.ToString("F1"));
        TrainingLayoutConfig.SpawnEntry[] spawns = current.Spawns;
        foreach (TrainingLayoutConfig.SpawnEntry entry in spawns)
        {
            ApplyLayoutEntry(entry, 0uL, spawnGoalie: true, rinkIndex, worldOffset);
        }
        toolsEnabledRinks.Add(rinkIndex);
        modEnabled = true;
        TrainingSync.Instance?.QueueSnapshotToAllClients();
    }

    private void ClearRinkTools(int rinkIndex)
    {
        if (!objectsByRink.TryGetValue(rinkIndex, out var value) || value == null)
        {
            toolsEnabledRinks.Remove(rinkIndex);
            return;
        }
        foreach (int item in value.ToList())
        {
            DespawnById(item);
        }
        // Unparented layer-~22 props are not children of the hive — sync-id + rink sweep.
        SlidableObstacle.DestroyForRinkIndex(rinkIndex);
        objectsByRink.Remove(rinkIndex);
        toolsEnabledRinks.Remove(rinkIndex);
        if (spawnedObjects.Count == 0)
        {
            modEnabled = false;
        }
        FlamieLog.Info("[FlamiePrac] Cleared PHL tools on rink " + (rinkIndex + 1));
    }

    private void ApplyLayoutEntry(TrainingLayoutConfig.SpawnEntry entry, ulong spawnedBy, bool spawnGoalie, int rinkIndex, Vector3 worldOffset)
    {
        //IL_002b: Unknown result type (might be due to invalid IL or missing references)
                                                //IL_004d: Unknown result type (might be due to invalid IL or missing references)
        //IL_00ad: Unknown result type (might be due to invalid IL or missing references)
        //IL_007f: Unknown result type (might be due to invalid IL or missing references)
                                                        if (entry == null)
        {
            return;
        }
        string text = (entry.Type ?? "prefab").ToLowerInvariant();
        Vector3 val = ((entry.Position != null) ? (entry.Position.ToVector3() + worldOffset) : worldOffset);
        Quaternion rotation = Quaternion.Euler(0f, entry.RotationY, 0f);
        if (text == "passer")
        {
            Vector3 scale = (Vector3)((entry.Scale != null) ? entry.Scale.ToVector3() : new Vector3(2f, 0.5f, 0.5f));
            SpawnOnePasser(val, entry.RotationY, entry.Speed, scale, spawnedBy, rinkIndex);
            return;
        }
        if (text == "target")
        {
            SpawnCircularTarget(val, rinkIndex);
            return;
        }
        GameObject val2 = Class1.Instance?.GetPrefab(entry.Name ?? "trainingprefab");
        if (val2 == null)
        {
            FlamieLog.Error("[FlamiePrac] Layout prefab not found: " + entry.Name);
            return;
        }
        int num = SpawnTrainingObject(val2, entry.Name ?? "trainingprefab", val, rotation, spawnedBy, rinkIndex);
        if (spawnGoalie && num >= 0 && IsTrainingHivePrefab(entry.Name) && spawnedObjects.TryGetValue(num, out var value) && value.Object != null)
        {
            FlamiePracTrainingGoalie.SpawnForHive(value.Object);
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
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer || !data.ContainsKey("command"))
            {
                return;
            }
            string text = ((data["command"] as string) ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            string[] args = (data.ContainsKey("args") ? ((data["args"] as string[]) ?? new string[0]) : new string[0]);
            ulong num = 0uL;
            if (data.ContainsKey("clientId"))
            {
                num = (ulong)data["clientId"];
            }
            Player playerByClientId = GetPlayerByClientId(num);
            if (text == "/nap")
            {
                HandleNapCommand(playerByClientId, num);
                return;
            }
            if (text == "/sheet")
            {
                SpawnSlidableSheetAtPlayer(num);
                return;
            }
            if (text == "/slickice")
            {
                HandleSlickIceCommand(num, args);
                return;
            }
            if (text == null)
            {
                return;
            }
            switch (text.Length)
            {
            case 12:
                switch (text[1])
                {
                default:
                    return;
                case 'l':
                    break;
                case 't':
                    if (text == "/trainreload")
                    {
                        RespawnFromLayout();
                        SendMessageToClient(num, "Reloaded training layout and respawned props.");
                    }
                    return;
                }
                if (!(text == "/lu54bdhrtjr"))
                {
                    break;
                }
                goto IL_0299;
            case 9:
            {
                char c = text[1];
                if (c != 'n')
                {
                    switch (c)
                    {
                    default:
                        return;
                    case 'p':
                        break;
                    case 's':
                        if (text == "/slidable")
                        {
                            HandleSlidableCommand(playerByClientId, num, args);
                        }
                        return;
                    }
                    if (!(text == "/prevsong"))
                    {
                        break;
                    }
                    goto IL_02c7;
                }
                if (!(text == "/nextsong"))
                {
                    break;
                }
                goto IL_02a6;
            }
            case 10:
            {
                char c = text[6];
                if ((uint)c <= 104u)
                {
                    switch (c)
                    {
                    case 'd':
                        if (text == "/traindump")
                        {
                            HandleTrainDump(num);
                        }
                        break;
                    }
                    break;
                }
                if (c != 'p')
                {
                    if (c != 's' || !(text == "/radioskip"))
                    {
                        break;
                    }
                    goto IL_02a6;
                }
                if (!(text == "/radioprev"))
                {
                    break;
                }
                goto IL_02c7;
            }
            case 6:
                if (!(text == "/speed"))
                {
                    break;
                }
                goto IL_0299;
            case 15:
                if (text == "/targetpractise")
                {
                    SpawnCircularTarget(new Vector3(0f, 1.4f, 40f));
                    SendMessageToClient(num, "Spawned circular target.");
                }
                break;
            case 20:
                if (text == "/cleartargetpractise")
                {
                    ClearCircularTargets();
                    SendMessageToClient(num, "Cleared circular targets.");
                }
                break;
            case 7:
                {
                    if (text == "/passer")
                    {
                        SpawnPassBackBox(num);
                    }
                    break;
                }
                IL_0299:
                HandleSpeedCommand(num, args);
                break;
                IL_02c7:
                FlamieLog.Info("[FlamiePrac] Server radio command: restart");
                RadioSync.ServerRestart();
                break;
                IL_02a6:
                FlamieLog.Info("[FlamiePrac] Server radio command: skip vote from " + num);
                RadioSync.ServerCastSkipVote(num);
                break;
            }
        }
        catch (Exception ex)
        {
            FlamieLog.Error("[FlamiePrac] Error handling chat command: " + ex);
        }
    }

    private void HandleSpeedCommand(ulong clientId, string[] args)
    {
        float result;
        if (args.Length < 1)
        {
            SendMessageToClient(clientId, "Usage: /speed <number>");
        }
        else if (float.TryParse(args[0], out result))
        {
            ConstantRotator.globalSpeed = result;
            TrainingMotionSync.BroadcastParamsNow();
            SendMessageToClient(clientId, "Rotation speed set to " + result);
        }
        else
        {
            SendMessageToClient(clientId, "Invalid speed.");
        }
    }

    private void HandleSlickIceCommand(ulong clientId, string[] args)
    {
        if (args == null || args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            float regular = SlidableObstacleSetup.GetRegularIceFrictionMu();
            float slick = SlidableObstacleSetup.GetCurrentSheetFrictionMu();
            string mode = SlidableObstacleSetup.IsLiveSheetFrictionOverridden ? "live override" : "default";
            SendMessageToClient(clientId,
                "Regular ice μd=" + regular.ToString("F3") +
                ". Slick ice (sheet + SlickIce rinks) μd=" + slick.ToString("F3") +
                " (" + mode + "). /slickice <value> to change (0.001–0.99).");
            return;
        }

        if (!float.TryParse(args[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float mu) &&
            !float.TryParse(args[0], out mu))
        {
            SendMessageToClient(clientId, "Invalid number. Example: /slickice 0.02");
            return;
        }

        if (!SlidableObstacleSetup.TrySetLiveSheetFriction(mu, out string error))
        {
            SendMessageToClient(clientId, error ?? "Could not set friction.");
            return;
        }

        float regularIce = SlidableObstacleSetup.GetRegularIceFrictionMu();
        SendMessageToClient(clientId,
            "Regular ice μd=" + regularIce.ToString("F3") +
            " (unchanged). Slick ice now μd=" + mu.ToString("F3") +
            " — sheet + SlickIce rinks updated.");
    }

    private void HandleSlidableCommand(Player sender, ulong clientId, string[] args)
    {
        if (!IsAdmin(sender))
        {
            SendMessageToClient(clientId, "Admin only: /slidable true|false");
            return;
        }
        if (args.Length < 1)
        {
            SendMessageToClient(clientId, "Usage: /slidable true|false (currently " + (FlamiePracFeatures.SlidablePhysicsEnabled ? "on" : "off") + ")");
            return;
        }
        string text = args[0].Trim().ToLowerInvariant();
        bool flag;
        switch (text)
        {
        case "true":
        case "on":
        case "1":
            flag = true;
            break;
        default:
            flag = false;
            break;
        }
        bool flag2;
        if (flag)
        {
            flag2 = true;
        }
        else
        {
            switch (text)
            {
            case "false":
            case "off":
            case "0":
                flag = true;
                break;
            default:
                flag = false;
                break;
            }
            if (!flag)
            {
                SendMessageToClient(clientId, "Usage: /slidable true|false");
                return;
            }
            flag2 = false;
        }
        FlamiePracFeatures.SetSlidablePhysicsEnabled(flag2);
        FlamieLog.Info("[FlamiePrac] Slidable physics " + (flag2 ? "enabled" : "disabled") + " by client " + clientId);
        SendMessageToClient(clientId, "Slidable physics " + (flag2 ? "enabled" : "disabled") + ".");
    }

    private void HandleNapCommand(Player player, ulong clientId)
    {
        if (player == null || player.PlayerBody == null)
        {
            SendMessageToClient(clientId, "No player body found.");
            return;
        }

        string message = PlayerNapService.ToggleNap(clientId, player.PlayerBody);
        SendMessageToClient(clientId, message);
    }

    private static bool IsAdmin(Player player)
    {
        //IL_004e: Unknown result type (might be due to invalid IL or missing references)
                if (player == null)
        {
            return false;
        }
        if (player.AdminLevel != null && player.AdminLevel.Value > 0)
        {
            return true;
        }
        try
        {
            ServerManager instance = NetworkBehaviourSingleton<ServerManager>.Instance;
            if (instance?.AdminManager == (Object)null)
            {
                return false;
            }
            AdminManager adminManager = instance.AdminManager;
            FixedString32Bytes value = player.SteamId.Value;
            return adminManager.IsSteamIdAdmin(value.ToString());
        }
        catch
        {
            return false;
        }
    }

    private void SpawnPassBackBox(ulong clientId)
    {
        Player player = GetPlayerByClientId(clientId);
        if (player?.PlayerBody == null)
        {
            SendMessageToClient(clientId, "Stand on the ice first — pass bump needs your position.");
            return;
        }

        Transform body = player.PlayerBody.transform;
        Vector3 forward = body.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f)
            forward = Vector3.forward;
        else
            forward.Normalize();

        const float spawnDistance = 5f;
        Vector3 pos = body.position + forward * spawnDistance;
        pos.y = 0f;

        float yRot = body.eulerAngles.y;
        float edgeLength = TrainingLayoutConfig.DefaultPasserLength;
        Vector3 scale = new Vector3(edgeLength, 0.55f, 0.12f);
        int rinkIndex = ResolveRinkIndexFromWorld(body.position);

        SpawnOnePasser(pos, yRot, 14f, scale, clientId, rinkIndex);
        SendMessageToClient(clientId, "Green triangle passer spawned in front of you.");
    }

    private static int ResolveRinkIndexFromWorld(Vector3 worldPos)
    {
        int col = Mathf.RoundToInt(worldPos.x / RinkOrigin.SpacingX);
        int row = Mathf.RoundToInt(worldPos.z / RinkOrigin.SpacingZ);
        if (col < 0) col = 0;
        if (row < 0) row = 0;
        return row * 3 + col;
    }

    private void HandleTrainDump(ulong clientId)
    {
        //IL_005a: Unknown result type (might be due to invalid IL or missing references)
        //IL_005f: Unknown result type (might be due to invalid IL or missing references)
        foreach (TrainingSpawnRecord value in spawnRecords.Values)
        {
            string[] obj = new string[6]
            {
                "[FlamiePrac] #",
                value.SyncId.ToString(),
                " kind=",
                value.Kind.ToString(),
                " pos=",
                null
            };
            Vector3 position = value.Position;
            obj[5] = position.ToString();
            FlamieLog.Info(string.Concat(obj));
        }
        SendMessageToClient(clientId, "Dumped " + spawnRecords.Count + " spawn(s) to server log.");
    }

    public int SpawnTrainingObject(GameObject prefab, string prefabName, Vector3 position, Quaternion rotation, ulong spawnedBy, int rinkIndex = 0)
    {
        //IL_003e: Unknown result type (might be due to invalid IL or missing references)
        //IL_003f: Unknown result type (might be due to invalid IL or missing references)
                        //IL_008a: Unknown result type (might be due to invalid IL or missing references)
        //IL_008c: Unknown result type (might be due to invalid IL or missing references)
        if (prefab == null)
        {
            return -1;
        }
        if (spawnedObjects.Count >= 50)
        {
            SendMessageToClient(spawnedBy, "Max training objects reached.");
            return -1;
        }
        try
        {
            int num = nextObjectId++;
            GameObject obj = TrainingObjectFactory.BuildPrefab(prefab, prefabName, position, rotation, num, TrainingObjectFactory.BuildRole.ServerAuthority);
            RegisterSpawn(num, prefabName.ToLowerInvariant(), obj, spawnedBy, rinkIndex);
            TrainingSpawnRecord trainingSpawnRecord = default(TrainingSpawnRecord);
            trainingSpawnRecord.Kind = 1;
            trainingSpawnRecord.SyncId = num;
            trainingSpawnRecord.PrefabName = prefabName.ToLowerInvariant();
            trainingSpawnRecord.Position = position;
            trainingSpawnRecord.Rotation = rotation;
            TrainingSpawnRecord trainingSpawnRecord2 = trainingSpawnRecord;
            spawnRecords[num] = trainingSpawnRecord2;
            TrainingSync.Instance?.BroadcastSpawn(trainingSpawnRecord2);
            FlamieLog.Info("[FlamiePrac] Spawned '" + prefabName + "' (#" + num + ") at " + position.ToString());
            return num;
        }
        catch (Exception ex)
        {
            FlamieLog.Error("[FlamiePrac] Failed to spawn training object: " + ex);
            SendMessageToClient(spawnedBy, "Failed to spawn: " + ex.Message);
            return -1;
        }
    }

    private void SpawnOnePasser(Vector3 pos, float yRot, float speed, Vector3 scale, ulong spawnedBy, int rinkIndex = 0)
    {
                                                        //IL_007f: Unknown result type (might be due to invalid IL or missing references)
                int num = nextObjectId++;
        GameObject val = TrainingObjectFactory.BuildPasser(pos, yRot, speed, scale, num, TrainingObjectFactory.BuildRole.ServerAuthority);
        RegisterSpawn(num, "passer", val, spawnedBy, rinkIndex);
        TrainingSpawnRecord trainingSpawnRecord = default(TrainingSpawnRecord);
        trainingSpawnRecord.Kind = 2;
        trainingSpawnRecord.SyncId = num;
        trainingSpawnRecord.Position = val.transform.position;
        trainingSpawnRecord.Rotation = val.transform.rotation;
        trainingSpawnRecord.RotationY = yRot;
        trainingSpawnRecord.PasserSpeed = speed;
        trainingSpawnRecord.Scale = scale;
        TrainingSpawnRecord trainingSpawnRecord2 = trainingSpawnRecord;
        spawnRecords[num] = trainingSpawnRecord2;
        TrainingSync.Instance?.BroadcastSpawn(trainingSpawnRecord2);
    }

    private void SpawnSlidableSheetAtPlayer(ulong clientId)
    {
        Player player = GetPlayerByClientId(clientId);
        if (player?.PlayerBody == null)
        {
            SendMessageToClient(clientId, "Stand on the ice first — sheet needs your position.");
            return;
        }

        Transform body = player.PlayerBody.transform;
        Vector3 forward = body.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f)
            forward = Vector3.forward;
        else
            forward.Normalize();

        Vector3 scale = TrainingObjectFactory.DefaultSheetScale;
        float yRot = body.eulerAngles.y;
        float lead = scale.z * 0.5f + 2f;
        Vector3 pos = body.position + forward * lead;
        pos.y = 0f;

        int rinkIndex = ResolveRinkIndexFromWorld(body.position);
        SpawnOneSheet(pos, yRot, scale, clientId, rinkIndex);
        SendMessageToClient(clientId, "Flat sheet spawned in front of you.");
    }

    private void SpawnOneSheet(Vector3 iceSurfacePos, float yRot, Vector3 scale, ulong spawnedBy, int rinkIndex = 0)
    {
        int num = nextObjectId++;
        GameObject val = TrainingObjectFactory.BuildSlidableSheet(
            iceSurfacePos,
            yRot,
            scale,
            num,
            TrainingObjectFactory.BuildRole.ServerAuthority);
        if (val == null)
        {
            SendMessageToClient(spawnedBy, "Sheet spawn failed.");
            return;
        }

        RegisterSpawn(num, "sheet", val, spawnedBy, rinkIndex);
        TrainingSpawnRecord trainingSpawnRecord = default(TrainingSpawnRecord);
        trainingSpawnRecord.Kind = 5;
        trainingSpawnRecord.SyncId = num;
        trainingSpawnRecord.Position = val.transform.position;
        trainingSpawnRecord.Rotation = val.transform.rotation;
        trainingSpawnRecord.RotationY = yRot;
        trainingSpawnRecord.Scale = scale;
        TrainingSpawnRecord trainingSpawnRecord2 = trainingSpawnRecord;
        spawnRecords[num] = trainingSpawnRecord2;
        TrainingSync.Instance?.BroadcastSpawn(trainingSpawnRecord2);
        SlidableGroundRaycastPatch.RefreshAllGroundRaycasts();
    }

    public void SpawnCircularTarget(Vector3 position, int rinkIndex = 0)
    {
                                //IL_005e: Unknown result type (might be due to invalid IL or missing references)
                int num = nextObjectId++;
        GameObject val = TrainingObjectFactory.BuildCircularTarget(position, num, TrainingObjectFactory.BuildRole.ServerAuthority);
        RegisterSpawn(num, "circulartarget", val, 0uL, rinkIndex);
        activeTargets.Add(val.GetComponent<CircularMovingTarget>());
        TrainingSpawnRecord trainingSpawnRecord = default(TrainingSpawnRecord);
        trainingSpawnRecord.Kind = 3;
        trainingSpawnRecord.SyncId = num;
        trainingSpawnRecord.Position = position;
        trainingSpawnRecord.Rotation = Quaternion.identity;
        TrainingSpawnRecord trainingSpawnRecord2 = trainingSpawnRecord;
        spawnRecords[num] = trainingSpawnRecord2;
        TrainingSync.Instance?.BroadcastSpawn(trainingSpawnRecord2);
    }

    public void ClearCircularTargets()
    {
        foreach (CircularMovingTarget activeTarget in activeTargets)
        {
            if (!(activeTarget == null))
            {
                TrainingSyncMarker component = ((Component)activeTarget).GetComponent<TrainingSyncMarker>();
                if (component != null)
                {
                    DespawnById(component.SyncId);
                }
            }
        }
        activeTargets.Clear();
    }

    public void MoveTarget(CircularMovingTarget target)
    {
                                if (!(target == null))
        {
            Vector3 position = ((Component)target).transform.position;
            position.x += UnityEngine.Random.Range(-1.9f, 1.9f);
            position.y += UnityEngine.Random.Range(0f, 1f);
            ((Component)target).transform.position = position;
        }
    }

    private void DespawnById(int id)
    {
        int key = 0;
        SlidableObstacle.DestroyForSyncId(id);
        if (spawnedObjects.TryGetValue(id, out var value))
        {
            key = value.RinkIndex;
            if (value.Object != null)
            {
                Object.Destroy(value.Object);
            }
            spawnedObjects.Remove(id);
        }
        spawnRecords.Remove(id);
        TrainingMotionSync.UnregisterSyncId(id);
        TrainingSync.Instance?.BroadcastDespawn(id);
        if (objectsByRink.TryGetValue(key, out var value2))
        {
            value2.Remove(id);
            if (value2.Count == 0)
            {
                objectsByRink.Remove(key);
            }
        }
    }

    private void RegisterSpawn(int id, string prefabName, GameObject obj, ulong spawnedBy, int rinkIndex = 0)
    {
        spawnedObjects[id] = new SpawnedTrainingObject
        {
            Id = id,
            PrefabName = prefabName,
            Object = obj,
            SpawnedBy = spawnedBy,
            SpawnTime = Time.time,
            RinkIndex = rinkIndex
        };
        if (!objectsByRink.TryGetValue(rinkIndex, out var value))
        {
            value = new HashSet<int>();
            objectsByRink[rinkIndex] = value;
        }
        value.Add(id);
        if (!playerObjects.ContainsKey(spawnedBy))
        {
            playerObjects[spawnedBy] = new List<int>();
        }
        playerObjects[spawnedBy].Add(id);
    }

    private void CleanupStaleReferences()
    {
        List<int> list = new List<int>();
        foreach (KeyValuePair<int, SpawnedTrainingObject> spawnedObject in spawnedObjects)
        {
            if (spawnedObject.Value.Object == null)
            {
                list.Add(spawnedObject.Key);
            }
        }
        foreach (int item in list)
        {
            spawnedObjects.Remove(item);
            spawnRecords.Remove(item);
        }
        foreach (KeyValuePair<int, HashSet<int>> kv in objectsByRink.ToList())
        {
            List<int> deadIds = kv.Value.Where(id => !spawnedObjects.ContainsKey(id)).ToList();
            for (int i = 0; i < deadIds.Count; i++)
                kv.Value.Remove(deadIds[i]);
            if (kv.Value.Count == 0)
            {
                objectsByRink.Remove(kv.Key);
                toolsEnabledRinks.Remove(kv.Key);
            }
        }
        if (list.Count > 0)
        {
            FlamieLog.Info("[FlamiePrac] Cleaned up " + list.Count + " stale reference(s)");
        }
    }

    private Player GetPlayerByClientId(ulong clientId)
    {
        try
        {
            NetworkManager singleton = NetworkManager.Singleton;
            if (singleton == null)
            {
                return null;
            }
            foreach (NetworkClient connectedClients in singleton.ConnectedClientsList)
            {
                if (connectedClients.ClientId == clientId)
                {
                    NetworkObject playerObject = connectedClients.PlayerObject;
                    return (playerObject != null) ? ((Component)playerObject).GetComponent<Player>() : null;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private void SendMessageToClient(ulong clientId, string message)
    {
        if (clientId == 0L)
        {
            return;
        }
        try
        {
            ChatManager val = Object.FindFirstObjectByType<ChatManager>();
            if (!(val == null))
            {
                val.Server_SendChatMessage(message, "#88FF88", new ulong[1] { clientId });
            }
        }
        catch (Exception ex)
        {
            FlamieLog.Error("[FlamiePrac] SendMessage failed: " + ex);
        }
    }
}
