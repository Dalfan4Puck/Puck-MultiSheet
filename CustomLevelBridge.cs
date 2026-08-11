using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using PHLPracticeModPack;

/// <summary>
/// PuckLargeLevel compatibility surface — harmony targets and static state used by
/// vendored PuckSpawnSync. Not an IPuckPlugin; PHLPracticeModPackPlugin owns lifecycle.
/// </summary>
public static class CustomLevelPlugin
{
    private static GameObject spawnedServerRoot;
    private static GameObject spawnedClientRoot;
    private static AssetBundle bundle;
    private static Harmony harmony;
    private static bool practiceHelperPatched;
    private static bool levelPatchesInstalled;
    private static bool clientBuildPending;
    private static bool clientBuildStarted;
    private static bool clientBuildDone;
    private static GameObject clientBuildHost;
    private static Coroutine clientBuildCoroutine;
    private static Action clientBuildWhenReady;

    private static readonly List<Renderer> hiddenScoreboardRenderers = new List<Renderer>();

    internal static readonly HashSet<Puck> ProtectedPucks = new HashSet<Puck>();
    internal static bool ChatInputActive;

    public static AssetBundle GetBundle() => bundle;
    public static bool IsGeometryLoaded => spawnedServerRoot != null || spawnedClientRoot != null;
    public static bool IsClientLayoutReady =>
        spawnedClientRoot != null && VanillaRinkCloner.LayoutReady;

    internal static bool TryInstall(Harmony ownerHarmony)
    {
        try
        {
            harmony = ownerHarmony ?? new Harmony("PHL.PHLPracticeModPack.LargeLevel");

            if (!levelPatchesInstalled)
            {
                InstallLevelPatches();
                levelPatchesInstalled = true;
            }

            CL_NetworkBoundsPatch.EnsurePatched();

            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg.UseAssetBundle)
            {
                if (!TryLoadBundle(cfg))
                    Debug.LogWarning("[PHLPractice] UseAssetBundle=true but asset bundle missing.");
            }
            else if (cfg.EnableMultiRink)
            {
                PracticeLog.Info("[PHLPractice] Multi-rink mode: vanilla base-game rink clones (no AssetBundle).");
            }

            if (GameObject.FindFirstObjectByType<LevelController>() != null)
            {
                PracticeLog.Info("[PHLPractice] Level already active — running level hooks now.");
                OnLevelAwake();
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("[PHLPractice] LargeLevel install failed: " + ex);
            return false;
        }
    }

    internal static void Teardown()
    {
        // Normally already done by OnPracticeConnectionLost, but the enforcer object is
        // DontDestroyOnLoad + HideAndDontSave: if disable ever runs without a clean
        // disconnect first, an un-findable object would keep driving the scene's lighting
        // for the rest of the process.
        ArenaLighting.Restore();
        RestoreVanillaScoreboards();

        MultiRinkSyncThrottle.Disable();
        CL_NetworkBoundsPatch.Disable();
        try { new Harmony("com.testlevel.minimap").UnpatchSelf(); } catch { }

        practiceHelperPatched = false;
        levelPatchesInstalled = false;
        clientBuildPending = false;
        clientBuildStarted = false;
        clientBuildDone = false;
        clientBuildWhenReady = null;
        harmony = null;
        ProtectedPucks.Clear();
        ChatInputActive = false;
        LevelDayNightCycle.ManualHour = null;

        if (spawnedServerRoot != null) { UnityEngine.Object.Destroy(spawnedServerRoot); spawnedServerRoot = null; }
        if (spawnedClientRoot != null) { UnityEngine.Object.Destroy(spawnedClientRoot); spawnedClientRoot = null; }
        CloneVisualProxy.DestroyInstance();
        DestroyPuckSpawnSync();

        if (bundle != null)
        {
            bundle.Unload(true);
            bundle = null;
        }

        VanillaRinkCloner.ResetCache();
        MinimapRinkView.Reset();
    }

    private static void DestroyPuckSpawnSync()
    {
        GameObject syncObj = GameObject.Find("PHL_PuckSpawnSync");
        if (syncObj != null) UnityEngine.Object.Destroy(syncObj);
    }

    private static bool TryLoadBundle(MultiRinkConfig cfg)
    {
        string pluginDir = Path.GetDirectoryName(typeof(CustomLevelPlugin).Assembly.Location);
        string bundlePath = Path.IsPathRooted(cfg.AssetBundleFile)
            ? cfg.AssetBundleFile
            : Path.Combine(pluginDir ?? ".", cfg.AssetBundleFile);

        PracticeLog.Info("[PHLPractice] Looking for level bundle at: " + bundlePath);
        bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            Debug.LogError("[PHLPractice] Failed to load AssetBundle at " + bundlePath);
            return false;
        }

        PracticeLog.Info("[PHLPractice] Loaded level AssetBundle.");
        return true;
    }

    private static void InstallLevelPatches()
    {
        MethodInfo awake = typeof(LevelController).GetMethod("Awake",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (awake != null)
            harmony.Patch(awake, postfix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(OnLevelAwake)));

        MethodInfo spawnPucks = typeof(PuckManager).GetMethod("Server_SpawnPucksForPhase",
            BindingFlags.Instance | BindingFlags.Public);
        if (spawnPucks != null)
            harmony.Patch(spawnPucks, prefix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(PreventPuckSpawn)));

        if (ModRuntimeContext.IsDedicatedGameServer) return;

        MethodInfo chatStart = typeof(UIChat).GetMethod("StartInput", BindingFlags.Instance | BindingFlags.Public);
        if (chatStart != null)
            harmony.Patch(chatStart, postfix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(OnChatStartInput)));

        MethodInfo chatStop = typeof(UIChat).GetMethod("StopInput", BindingFlags.Instance | BindingFlags.Public);
        if (chatStop != null)
            harmony.Patch(chatStop, postfix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(OnChatStopInput)));

        MethodInfo sendChat = typeof(ChatManager).GetMethod("Client_SendChatMessage",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (sendChat != null)
            harmony.Patch(sendChat, prefix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(OnClientSendChatMessage)));
    }

    /// <summary>Legacy hook — minimap Show blocking is owned by MinimapSessionOverride.</summary>
    public static bool MinimapShowPrefix() => !MinimapSessionOverride.Suppressed;

    public static void OnChatStartInput() => ChatInputActive = true;
    public static void OnChatStopInput() => ChatInputActive = false;

    public static bool OnClientSendChatMessage(string content, bool isQuickChat, bool isTeamChat)
    {
        if (isQuickChat || string.IsNullOrEmpty(content)) return true;
        string trimmed = content.Trim();

        if (string.Equals(trimmed, "/multirink-dump", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "/mutlirink-dump", StringComparison.OrdinalIgnoreCase))
        {
            if (ArenaRenderCatalog.TryHandleDumpCommand(trimmed, false, out string reply))
                ShowLocalChat(reply ?? "[PHLPractice] Catalog dump failed.");
            else
                ShowLocalChat("[PHLPractice] Catalog dump failed.");
            return false;
        }

        if (string.Equals(trimmed, "/multirink-diag", StringComparison.OrdinalIgnoreCase))
        {
            if (ArenaRenderCatalog.TryHandleDumpCommand(trimmed, false, out string reply))
                ShowLocalChat(reply ?? "[PHLPractice] Diag failed.");
            else
                ShowLocalChat("[PHLPractice] Diag failed.");
            return false;
        }

        if (!trimmed.StartsWith("/timeset", StringComparison.OrdinalIgnoreCase)) return true;

        // Chat equivalent of the Lighting slider on the rink panel. Local-only.
        string arg = trimmed.Length > 8 ? trimmed.Substring(8).Trim() : "";
        if (!ArenaLighting.DayNightEnabled)
        {
            ShowLocalChat("[PHLPractice] Day/night is off — turn it on under Lighting on the rink panel.");
        }
        else if (arg.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            ArenaLighting.SetManualHour(-1f);
            ShowLocalChat("[PHLPractice] Time of day follows your system clock.");
        }
        else if (int.TryParse(arg, out int hhmm) && hhmm / 100 >= 0 && hhmm / 100 <= 23 && hhmm % 100 < 60)
        {
            float hour = hhmm / 100 + (hhmm % 100) / 60f;
            ArenaLighting.SetManualHour(hour);
            ShowLocalChat("[PHLPractice] Time set to " + ArenaLighting.FormatHour(hour) + ".");
        }
        else
        {
            ShowLocalChat("[PHLPractice] Usage: /timeset HHMM  or  /timeset auto");
        }

        return false;
    }

    private static void ShowLocalChat(string msg)
    {
        try
        {
            UIChat uiChat = GameObject.FindFirstObjectByType<UIChat>();
            if (uiChat == null) return;
            MethodInfo addMsg = typeof(UIChat).GetMethod("AddChatMessage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (addMsg == null) return;

            Type chatMsgType = typeof(ChatMessage);
            object chatMsg = Activator.CreateInstance(chatMsgType);
            FieldInfo contentField = chatMsgType.GetField("Content",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo isSystemField = chatMsgType.GetField("IsSystem",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (contentField != null)
            {
                object fsInstance = Activator.CreateInstance(contentField.FieldType, msg);
                contentField.SetValue(chatMsg, fsInstance);
            }
            isSystemField?.SetValue(chatMsg, true);
            addMsg.Invoke(uiChat, new object[] { chatMsg, Units.Imperial, false });
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PHLPractice] ShowLocalChat failed: " + ex.Message);
        }
    }

    public static bool PreventPuckSpawn() =>
        MultiRinkConfig.Current.UseAssetBundle ? false : true;

    public static bool PreventPuckDespawn(Puck puck) => !ProtectedPucks.Contains(puck);

    public static bool PreventPuckMove(Puck __instance, ref Vector3 position)
    {
        if (ProtectedPucks.Contains(__instance)) return false;
        return true;
    }

    public static bool PreventClamp(object __instance, ref Vector3 __result, Vector3 position)
    {
        __result = position;
        return false;
    }

    public static bool ExpandVirtualRinkBounds(object __instance, ref bool __result,
        ref float xMin, ref float xMax, ref float zMin, ref float zMax, ref float cornerRadius)
    {
        xMin = -500f;
        xMax = 500f;
        zMin = -500f;
        zMax = 500f;
        cornerRadius = 0f;
        __result = true;
        return false;
    }

    public static void OnLevelAwake()
    {
        try
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sceneName != "level_default")
            {
                PracticeLog.Info("[PHLPractice] Skipping level hooks in scene '" + sceneName + "'");
                return;
            }

            if (spawnedServerRoot != null) { UnityEngine.Object.Destroy(spawnedServerRoot); spawnedServerRoot = null; }
            if (spawnedClientRoot != null) { UnityEngine.Object.Destroy(spawnedClientRoot); spawnedClientRoot = null; }

            ModPatchInstaller.TryInstallDeferredCtuCompat(null);
            TryPatchPracticeHelpers();

            bool isServer = NetworkManager.Singleton?.IsServer ?? true;
            bool isClient = NetworkManager.Singleton?.IsClient ?? true;
            if (!isServer && !isClient) { isServer = true; isClient = true; }

            MultiRinkConfig cfg = MultiRinkConfig.Current;

            if (!isServer)
            {
                // Chunk announcements only need decoding on pure clients; the server
                // (and a host) reads true world positions in-process.
                try
                {
                    NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                        CL_ChunkSyncServer.CmmName, OnClientChunkMessage);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Chunk CMM registration failed: " + ex.Message);
                }
            }

            if (isServer)
            {
                // Pin each rink to its own dedicated chunk before the sync server seeds
                // objects, so nothing is ever chunk-encoded mid-rink.
                var rinkOrigins = new List<Vector3>();
                foreach (RinkSlot slot in cfg.Rinks)
                    if (slot != null) rinkOrigins.Add(slot.Origin);
                CL_ChunkSyncServer.ConfigureRinkZones(rinkOrigins);

                CL_NetworkBoundsPatch.EnableChunks();
                MultiRinkSyncThrottle.Enable(cfg);
            }
            // Pure clients arm chunk decode lazily (see OnClientChunkMessage /
            // ConfirmPracticeServer): with decode active but no chunk announcements —
            // i.e. on a vanilla server — every synced object would be frozen in place.

            if (cfg.UseAssetBundle && bundle != null)
                HideVanillaRinkGeometry();

            if (cfg.EnableMultiRink && !cfg.UseAssetBundle)
            {
                VanillaRinkCloner.ResetCache();
                if (isServer)
                {
                    spawnedServerRoot = VanillaRinkCloner.SpawnMultiRinkLayout(cfg.Rinks, cfg, RinkCloneRole.Server);
                    MultiRinkPuckSpawner.Begin(cfg.Rinks);
                }
                if (isClient)
                {
                    if (isServer)
                    {
                        // Host / offline practice: this process IS the practice server.
                        BuildClientSide(cfg);
                    }
                    else
                    {
                        // Pure client: don't touch the world until the server proves it
                        // runs MultiSheet (first rink-status payload). On vanilla servers
                        // nothing is ever built and the mod stays dormant.
                        clientBuildPending = true;
                        clientBuildStarted = false;
                        clientBuildDone = false;
                        PracticeLog.Info("[PHLPractice] Client rink visuals deferred until the server confirms MultiSheet.");
                    }
                }
            }

            if (cfg.UseAssetBundle && bundle != null)
            {
                GameObject prefab = bundle.LoadAsset<GameObject>(cfg.LevelPrefabName);
                if (prefab == null)
                {
                    Debug.LogError("[PHLPractice] Prefab not found in bundle: " + cfg.LevelPrefabName);
                }
                else
                {
                    if (isServer)
                        spawnedServerRoot = SpawnServerRinks(prefab, cfg.Rinks);
                    if (isClient)
                        spawnedClientRoot = SpawnClientRinks(prefab, cfg.Rinks);
                }
            }

            EnsurePuckSpawnSync(isServer, isClient);
            PatchPuckGuardsIfNeeded(isServer);

            // Pure clients only expand bounds once the server is confirmed (see
            // ConfirmPracticeServer) so vanilla servers keep their vanilla bounds.
            if (isServer)
            {
                ExpandWorldBounds(cfg.Rinks);
                if (cfg.EnableMultiRink && !cfg.UseAssetBundle)
                    CtuArenaMultiRinkCompat.NotifyMultiRinkLayoutReady();
            }
            PracticeLog.Info("[PHLPractice] Level hooks complete (vanillaMultiRink=" +
                (cfg.EnableMultiRink && !cfg.UseAssetBundle) + ", bundle=" + (cfg.UseAssetBundle && bundle != null) + ").");
        }
        catch (Exception ex)
        {
            Debug.LogError("[PHLPractice] OnLevelAwake error: " + ex.Message);
        }
    }

    /// <summary>
    /// First chunk announcement from the server — proof it runs MultiSheet, so it is
    /// safe (and necessary) to arm client-side chunk decode before forwarding.
    /// </summary>
    private static void OnClientChunkMessage(ulong senderId, Unity.Netcode.FastBufferReader reader)
    {
        ArmClientChunkDecode();
        CL_ChunkSyncClient.OnChunkMessage(senderId, reader);
    }

    private static void ArmClientChunkDecode()
    {
        if (CL_ChunkRegistry.ChunksActive) return;
        if (MultiSheetClientSettings.SkipChunkClient)
        {
            PracticeLog.Info("[PHLPractice] skipChunkClient — client chunk decode not armed (FPS A/B).");
            return;
        }
        CL_NetworkBoundsPatch.EnsurePatched();
        CL_NetworkBoundsPatch.EnableChunks();
    }

    /// <summary>
    /// First rink-status payload arrived from the server — it definitely runs
    /// MultiSheet, so it is now safe to build the deferred client-side visuals.
    /// Pure-client clones are spread across frames; <paramref name="whenLayoutReady"/>
    /// runs after the build finishes (or immediately if already ready / skipped).
    /// </summary>
    internal static void ConfirmPracticeServer(RinkMotdPayload payload = null, Action whenLayoutReady = null)
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            bool pureClient = nm != null && nm.IsClient && !nm.IsServer;
            if (!pureClient)
            {
                whenLayoutReady?.Invoke();
                return;
            }

            if (clientBuildDone)
            {
                whenLayoutReady?.Invoke();
                return;
            }

            if (clientBuildStarted)
            {
                // Keep the latest MOTD/strip callback; earlier status may be stale.
                if (whenLayoutReady != null)
                    clientBuildWhenReady = whenLayoutReady;
                return;
            }

            if (payload != null)
                MultiRinkConfig.ApplyClientLayoutFromServer(payload);

            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (!cfg.EnableMultiRink || cfg.UseAssetBundle)
            {
                whenLayoutReady?.Invoke();
                return;
            }

            clientBuildStarted = true;
            clientBuildPending = false;
            clientBuildWhenReady = whenLayoutReady;
            // CPT may have finished loading after our OnEnable — re-assert thin skaters on client.
            CptThinSkaterOverride.Apply();
            TrlPracticeSmoothnessOverride.Apply();
            ArmClientChunkDecode();
            ExpandWorldBounds(cfg.Rinks);

            if (MultiSheetClientSettings.SkipClientBuild)
            {
                clientBuildDone = true;
                PracticeLog.Info("[PHLPractice] skipClientBuild — client rink visuals skipped (FPS A/B).");
                InvokeClientBuildWhenReady();
                return;
            }

            MonoBehaviour host = EnsureClientBuildHost();
            clientBuildCoroutine = host.StartCoroutine(BuildClientSideAsync(cfg));
        }
        catch (Exception ex)
        {
            Debug.LogError("[PHLPractice] Deferred client build failed: " + ex);
            clientBuildDone = true;
            InvokeClientBuildWhenReady();
        }
    }

    private static void InvokeClientBuildWhenReady()
    {
        Action cb = clientBuildWhenReady;
        clientBuildWhenReady = null;
        try { cb?.Invoke(); }
        catch (Exception ex)
        {
            Debug.LogWarning("[PHLPractice] Client build ready callback failed: " + ex.Message);
        }
    }

    private static MonoBehaviour EnsureClientBuildHost()
    {
        if (clientBuildHost != null)
        {
            MonoBehaviour existing = clientBuildHost.GetComponent<ClientBuildHost>();
            if (existing != null) return existing;
        }

        clientBuildHost = new GameObject("PHL_ClientBuildHost");
        UnityEngine.Object.DontDestroyOnLoad(clientBuildHost);
        clientBuildHost.hideFlags = HideFlags.HideAndDontSave;
        return clientBuildHost.AddComponent<ClientBuildHost>();
    }

    private sealed class ClientBuildHost : MonoBehaviour { }

    private static IEnumerator BuildClientSideAsync(MultiRinkConfig cfg)
    {
        if (spawnedClientRoot != null)
        {
            UnityEngine.Object.Destroy(spawnedClientRoot);
            spawnedClientRoot = null;
        }

        GameObject root = null;
        yield return VanillaRinkCloner.SpawnMultiRinkLayoutAsync(
            cfg.Rinks, cfg, RinkCloneRole.Client, finished => root = finished);

        if (root == null)
        {
            clientBuildDone = true;
            clientBuildCoroutine = null;
            Debug.LogWarning("[PHLPractice] Async client layout build produced no root.");
            InvokeClientBuildWhenReady();
            yield break;
        }

        spawnedClientRoot = root;
        // Must run after the client root exists — ArenaLighting owns fill lights under it.
        ArenaLighting.Apply();
        PracticePresentation.ApplyAfterClientBuild();
        MultiSheetClientSettings.ApplyLoadedPreferences();
        SpawnGroundPlane(cfg.Rinks, spawnedClientRoot);
        HideVanillaScoreboards();
        RinkPreview.NotifyClientBuildComplete();
        clientBuildDone = true;
        clientBuildCoroutine = null;
        PracticeLog.Info("[PHLPractice] Server confirmed MultiSheet — client layout built (" +
                  (cfg.Rinks?.Count ?? 0) + " rink slot(s), frame-spread).");
        PracticeLog.Info("[PHLPractice] Client layout ready.");
        if (cfg.EnableMultiRink && !cfg.UseAssetBundle)
            CtuArenaMultiRinkCompat.NotifyMultiRinkLayoutReady();
        InvokeClientBuildWhenReady();
    }

    /// <summary>Local client left the server — re-arm the deferral for the next join.</summary>
    internal static void OnPracticeConnectionLost(bool wasHosting)
    {
        if (clientBuildCoroutine != null && clientBuildHost != null)
        {
            try { clientBuildHost.GetComponent<ClientBuildHost>()?.StopCoroutine(clientBuildCoroutine); }
            catch { }
            clientBuildCoroutine = null;
        }

        if (clientBuildHost != null)
        {
            UnityEngine.Object.Destroy(clientBuildHost);
            clientBuildHost = null;
        }

        clientBuildWhenReady = null;
        clientBuildPending = false;
        clientBuildStarted = false;
        clientBuildDone = false;

        // Leave nothing behind for the next (possibly vanilla) session: the clone
        // visuals root and puck spawn-sync object are DontDestroyOnLoad, so they
        // would otherwise follow the player onto other servers.
        if (spawnedClientRoot != null) { UnityEngine.Object.Destroy(spawnedClientRoot); spawnedClientRoot = null; }
        if (wasHosting && spawnedServerRoot != null)
        {
            UnityEngine.Object.Destroy(spawnedServerRoot);
            spawnedServerRoot = null;
        }

        // Full destroy rather than Clear: the proxy object is DontDestroyOnLoad and holds
        // a beginCameraRendering subscription, both of which would ride along to the next
        // server. AddDraw re-creates it on the next MultiSheet join.
        CloneVisualProxy.DestroyInstance();
        TrlReskinBridge.Clear();
        TrlReskinBridge.SetCompatibilityEnabled(false);
        ArenaLighting.Restore();
        RestoreVanillaScoreboards();
        VanillaRinkCloner.ResetCache();
        LevelDayNightCycle.ManualHour = null;
        ProtectedPucks.Clear();

        NetworkManager serverCheck = NetworkManager.Singleton;
        if (serverCheck == null || !serverCheck.IsServer)
            DestroyPuckSpawnSync();

        DisarmClientChunkDecode();

        if (wasHosting)
        {
            MultiRinkSyncThrottle.Disable();
            CL_NetworkBoundsPatch.Disable();
            MultiRinkPuckSpawner.Stop();
            RinkPracticeDrills.StopAll();
        }

        // Chunk decode must never leak into a later vanilla-server session: with it
        // active but no chunk announcements, every synced object would freeze.
        NetworkManager nm = NetworkManager.Singleton;
        bool serverish = nm != null && nm.IsServer;
        if (!serverish && CL_ChunkRegistry.ChunksActive)
        {
            CL_ChunkSyncClient.Disable();
            CL_ChunkRegistry.ChunksActive = false;
            CL_ChunkRegistry.Clear();
            PracticeLog.Info("[PHLPractice] Chunk decode disarmed (left practice server).");
        }
    }

    private static void DisarmClientChunkDecode()
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            nm?.CustomMessagingManager?.UnregisterNamedMessageHandler(CL_ChunkSyncServer.CmmName);
        }
        catch { }

        if (CL_ChunkRegistry.ChunksActive)
        {
            CL_ChunkSyncClient.Disable();
            CL_ChunkRegistry.ChunksActive = false;
            CL_ChunkRegistry.Clear();
        }
    }

    private static void BuildClientSide(MultiRinkConfig cfg)
    {
        if (MultiSheetClientSettings.SkipClientBuild)
        {
            PracticeLog.Info("[PHLPractice] skipClientBuild — BuildClientSide no-op (FPS A/B).");
            return;
        }

        if (spawnedClientRoot != null)
        {
            UnityEngine.Object.Destroy(spawnedClientRoot);
            spawnedClientRoot = null;
        }

        spawnedClientRoot = VanillaRinkCloner.SpawnMultiRinkLayout(cfg.Rinks, cfg, RinkCloneRole.Client);
        // Must run after the client root exists — ArenaLighting owns fill lights under it.
        ArenaLighting.Apply();
        // Honour Limit Rink Changes (unpatch TRL + stock look) after Apply.
        PracticePresentation.ApplyAfterClientBuild();
        MultiSheetClientSettings.ApplyLoadedPreferences();
        SpawnGroundPlane(cfg.Rinks, spawnedClientRoot);
        HideVanillaScoreboards();
        RinkPreview.NotifyClientBuildComplete();
        PracticeLog.Info("[PHLPractice] Client layout ready.");
    }

    /// <summary>
    /// One big matte slab under the whole rink grid so the cloned sheets don't look
    /// like they're floating in the skybox. Purely cosmetic — no collider.
    /// </summary>
    private static void SpawnGroundPlane(List<RinkSlot> rinks, GameObject parent)
    {
        if (rinks == null || rinks.Count == 0) return;

        Vector3 min = rinks[0].Origin, max = rinks[0].Origin;
        for (int i = 1; i < rinks.Count; i++)
        {
            if (rinks[i] == null) continue;
            min = Vector3.Min(min, rinks[i].Origin);
            max = Vector3.Max(max, rinks[i].Origin);
        }

        // Pad well past the boards (rink is ~26 m wide on X, ~61 m long on Z).
        const float marginX = 55f;
        const float marginZ = 80f;
        float sizeX = (max.x - min.x) + marginX * 2f;
        float sizeZ = (max.z - min.z) + marginZ * 2f;
        Vector3 center = new Vector3((min.x + max.x) * 0.5f, -0.07f, (min.z + max.z) * 0.5f);

        GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        plane.name = "MultiSheetGround";
        if (parent != null) plane.transform.SetParent(parent.transform, false);
        plane.transform.position = center;
        plane.transform.localScale = new Vector3(sizeX / 10f, 1f, sizeZ / 10f); // Plane primitive is 10x10 m

        Collider planeCollider = plane.GetComponent<Collider>();
        if (planeCollider != null) UnityEngine.Object.Destroy(planeCollider);

        var r = plane.GetComponent<MeshRenderer>();
        if (r != null)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit != null)
            {
                var mat = new Material(lit) { name = "MultiSheetGroundMat" };
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", new Color(0.33f, 0.34f, 0.36f, 1f));
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", 0.12f);
                r.sharedMaterial = mat;
            }
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
        }

        PracticeLog.Info($"[PHLPractice] Ground plane spawned: center={center} size=({sizeX:F0} x {sizeZ:F0}).");
    }

    /// <summary>
    /// The hanging jumbotrons look wrong in the open multi-sheet layout (they are no
    /// longer cloned at all) — hide the vanilla rink-1 pair too. Renderers only, so
    /// any game logic touching the scoreboard objects keeps working.
    /// </summary>
    private static void HideVanillaScoreboards()
    {
        string[] names = { "Scoreboard Blue", "Scoreboard Red" };
        foreach (string name in names)
        {
            GameObject go = GameObject.Find(name);
            if (go == null) continue;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                hiddenScoreboardRenderers.Add(r);
            }
            PracticeLog.Info("[PHLPractice] Hid vanilla " + name + ".");
        }
    }

    /// <summary>
    /// These are the level's own renderers, not clones, so leaving them off would follow
    /// the player to the next server if the scene happens not to reload.
    /// </summary>
    private static void RestoreVanillaScoreboards()
    {
        for (int i = 0; i < hiddenScoreboardRenderers.Count; i++)
        {
            Renderer r = hiddenScoreboardRenderers[i];
            if (r != null) r.enabled = true;
        }
        hiddenScoreboardRenderers.Clear();
    }

    private static void HideVanillaRinkGeometry()
    {
        var hideNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Rink", "Hangar", "Blue Goal", "Red Goal", "Goal Blue", "Goal Red",
            "Scoreboard Blue", "Scoreboard Red", "Lights",
            "Spectator Booth 1","Spectator Booth 2","Spectator Booth 3","Spectator Booth 4",
            "Spectator Booth 5","Spectator Booth 6","Spectator Booth 7","Spectator Booth 8",
            "Spectator Booth 9","Spectator Booth 10",
            "Net", "Net Collider", "Goal Post Collider", "Goal Trigger", "Goal Player Collider",
        };

        foreach (GameObject go in GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (go != null && hideNames.Contains(go.name))
                go.SetActive(false);
        }
    }

    private static GameObject SpawnServerRinks(GameObject prefab, List<RinkSlot> rinks)
    {
        var root = new GameObject("PHLMultiRink_Server");
        int iceLayer = LayerMask.NameToLayer("Ice");

        for (int i = 0; i < rinks.Count; i++)
        {
            RinkSlot slot = rinks[i];
            if (slot == null) continue;

            GameObject instance = UnityEngine.Object.Instantiate(prefab, slot.Origin, Quaternion.identity, root.transform);
            instance.name = "CustomLevel_Server_" + slot.Id;
            PrepareServerInstance(instance, iceLayer);
            PracticeLog.Info("[PHLPractice] Server rink spawned: " + slot.Id + " @ " + slot.Origin);
        }

        return root;
    }

    private static GameObject SpawnClientRinks(GameObject prefab, List<RinkSlot> rinks)
    {
        var root = new GameObject("PHLMultiRink_Client");

        var fixerObj = new GameObject("TerrainBasemapEnforcer");
        fixerObj.AddComponent<TerrainBasemapEnforcer>();

        Dictionary<string, Material> materials = LoadBundleMaterials();
        Dictionary<string, string> meshToMaterial = BuildMeshMaterialMap(prefab);
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");

        for (int i = 0; i < rinks.Count; i++)
        {
            RinkSlot slot = rinks[i];
            if (slot == null) continue;

            GameObject instance = UnityEngine.Object.Instantiate(prefab, slot.Origin, Quaternion.identity, root.transform);
            instance.name = "CustomLevel_Client_" + slot.Id;

            if (i == 0)
                instance.AddComponent<LevelDayNightCycle>();

            foreach (Collider c in instance.GetComponentsInChildren<Collider>(true))
                if (c != null) UnityEngine.Object.Destroy(c);

            ApplyClientMaterials(instance, meshToMaterial, materials, shader);
            PracticeLog.Info("[PHLPractice] Client rink spawned: " + slot.Id + " @ " + slot.Origin);
        }

        HideMinimap();
        return root;
    }

    private static void PrepareServerInstance(GameObject instance, int iceLayer)
    {
        foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = iceLayer >= 0 ? iceLayer : t.gameObject.layer;

        foreach (MeshFilter mf in instance.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null) continue;
            MeshCollider mc = mf.gameObject.GetComponent<MeshCollider>() ?? mf.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = false;
            if (iceLayer >= 0) mf.gameObject.layer = iceLayer;
        }

        foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
            if (r != null) r.enabled = false;

        foreach (Light l in instance.GetComponentsInChildren<Light>(true))
            if (l != null) l.enabled = false;
    }

    private static Dictionary<string, Material> LoadBundleMaterials()
    {
        var materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        if (bundle == null) return materials;

        foreach (string assetName in bundle.GetAllAssetNames())
        {
            if (!assetName.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
            Material mat = bundle.LoadAsset<Material>(assetName);
            if (mat != null) materials[mat.name.ToLowerInvariant()] = mat;
        }
        return materials;
    }

    private static Dictionary<string, string> BuildMeshMaterialMap(GameObject prefab)
    {
        var meshToMaterial = new Dictionary<string, string>();
        foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            for (int i = 0; i < r.sharedMaterials.Length; i++)
            {
                if (r.sharedMaterials[i] == null) continue;
                meshToMaterial[r.gameObject.name + "_" + i] = r.sharedMaterials[i].name.ToLowerInvariant();
            }
        }
        return meshToMaterial;
    }

    private static void ApplyClientMaterials(
        GameObject instance,
        Dictionary<string, string> meshToMaterial,
        Dictionary<string, Material> materials,
        Shader shader)
    {
        foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            Material[] mats = r.materials;
            for (int i = 0; i < mats.Length; i++)
            {
                string key = r.gameObject.name + "_" + i;
                if (meshToMaterial.TryGetValue(key, out string matName) &&
                    materials.TryGetValue(matName, out Material bundleMat))
                    mats[i] = bundleMat;

                if (shader != null && mats[i] != null)
                {
                    mats[i].shader = shader;
                    if (mats[i].HasProperty("_Surface")) mats[i].SetFloat("_Surface", 0f);
                    if (mats[i].HasProperty("_EmissionColor")) mats[i].DisableKeyword("_EMISSION");
                }
            }
            r.materials = mats;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        }
    }

    private static void HideMinimap()
    {
        UIMinimap minimap = GameObject.FindFirstObjectByType<UIMinimap>();
        minimap?.Hide();
    }

    private static void EnsurePuckSpawnSync(bool isServer, bool isClient)
    {
        if (GameObject.Find("PHL_PuckSpawnSync") != null) return;

        var syncObj = new GameObject("PHL_PuckSpawnSync");
        syncObj.AddComponent<PuckSpawnSync>();
        UnityEngine.Object.DontDestroyOnLoad(syncObj);
        PracticeLog.Info("[PHLPractice] PuckSpawnSync added (server=" + isServer + ", client=" + isClient + ").");
    }

    private static void PatchPuckGuardsIfNeeded(bool isServer)
    {
        if (!isServer || harmony == null) return;

        MethodInfo despawnPuck = typeof(PuckManager).GetMethod("Server_DespawnPuck",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (despawnPuck != null)
            harmony.Patch(despawnPuck, prefix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(PreventPuckDespawn)));

        string[] moveMethods =
        {
            "Server_MovePuck", "Server_ResetPuck", "Server_FaceOffDrop",
            "Server_MovePuckToFaceOff", "Server_SetPuckPosition"
        };
        foreach (string methodName in moveMethods)
        {
            MethodInfo m = typeof(PuckManager).GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null)
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(PreventPuckMove)));
        }
    }

    private static void ExpandWorldBounds(List<RinkSlot> rinks)
    {
        Level level = GameObject.FindFirstObjectByType<Level>();
        if (level == null || rinks == null || rinks.Count == 0) return;

        // The minimap needs the original single-rink bounds to draw correctly.
        MinimapRinkView.CaptureVanillaBounds(level.Bounds);

        Vector3 min = rinks[0].Origin;
        Vector3 max = rinks[0].Origin;
        for (int i = 1; i < rinks.Count; i++)
        {
            Vector3 o = rinks[i].Origin;
            min = Vector3.Min(min, o);
            max = Vector3.Max(max, o);
        }

        Vector3 center = (min + max) * 0.5f;
        Vector3 size = max - min + new Vector3(120f, 50f, 120f);
        level.Bounds = new Bounds(center, size);
    }

    private static void TryPatchPracticeHelpers()
    {
        if (practiceHelperPatched || harmony == null) return;

        Type helpers = null;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "MaxPractice")
            {
                helpers = asm.GetType("MaxPractice.PracticeHelpers");
                break;
            }
        }

        if (helpers == null) return;

        MethodInfo clamp = helpers.GetMethod("ClampToVirtualRink",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (clamp != null)
            harmony.Patch(clamp, prefix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(PreventClamp)));

        MethodInfo bounds = helpers.GetMethod("TryGetVirtualRinkBounds",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (bounds != null)
            harmony.Patch(bounds, prefix: new HarmonyMethod(typeof(CustomLevelPlugin), nameof(ExpandVirtualRinkBounds)));

        practiceHelperPatched = true;
    }
}
