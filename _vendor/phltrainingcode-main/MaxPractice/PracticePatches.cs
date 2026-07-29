// PracticePatches.cs - Harmony patches for practice features

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using MaxPractice;

/// <summary>
/// Detects if a CompTweaks-style mod is loaded.
/// This includes the original CompetitivePuckTweaks mod and the newer CompetitiveAdjustments package.
/// </summary>
public static class CompTweaksDetector
{
    private static bool? _isCompTweaksLoaded = null;
    
    /// <summary>
    /// Check if a CompTweaks-style mod is loaded. Result is cached after first check.
    /// </summary>
    public static bool IsCompTweaksLoaded
    {
        get
        {
            if (_isCompTweaksLoaded.HasValue)
                return _isCompTweaksLoaded.Value;
            
            _isCompTweaksLoaded = DetectCompTweaks();
            return _isCompTweaksLoaded.Value;
        }
    }
    
    private static bool DetectCompTweaks()
    {
        try
        {
            // Check for known assemblies from the old and new comp tweak packs.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (name.Contains("CompetitivePuckTweaks") ||
                    name.Contains("CompetitiveAdjustments") ||
                    name.Contains("CPT") ||
                    name.Contains("COMPADJUST"))
                    return true;
            }
            
            // Also check for types from the old standalone mod and the new combined mod.
            var knownTypeNames = new[]
            {
                "CompetitivePuckTweaks.src.PluginCore, CompetitivePuckTweaks",
                "CompetitivePuckTweaks.src.PluginCore, CompetitiveAdjustments",
                "CompetitiveAdjustmentsGameMod, CompetitiveAdjustments",
                "CompetitiveCompanion.PluginCore, CompetitiveAdjustments"
            };

            foreach (var typeName in knownTypeNames)
            {
                if (Type.GetType(typeName) != null)
                    return true;
            }
        }
        catch { }
        
        return false;
    }
    
    /// <summary>
    /// Force re-check for CompTweaks (useful if mods load after our check)
    /// </summary>
    public static void Reset()
    {
        _isCompTweaksLoaded = null;
    }
}

/// <summary>
/// PUBLIC API: Helper to detect if a player is a fake AI player from MaxPractice.
/// Other mods (like CurvedStick) can use this to skip their patches for our fake players.
/// </summary>
public static class FakePlayerDetector
{
    // Fake player client IDs used by MaxPractice for AI GOALIES only
    public const ulong FAKE_RED_CLIENT_ID = 1111112;
    public const ulong FAKE_BLUE_CLIENT_ID = 1111111;
    
    // Client ID range for traffic dummies. SkaterAI builds IDs as
    //   3333333 + uniqueTrafficNum + (isRedTeam ? 0 : 1000)
    // — so the blue-team offset alone pushes IDs to 3334333+. End needs to
    // sit well above 3334333 + max-spawns-per-session. Original 3333500 cap
    // dropped every blue traffic entry on the floor; widened to a 100k slot
    // so we can't outgrow it in any realistic session.
    public const ulong TRAFFIC_CLIENT_ID_START = 3333333;
    public const ulong TRAFFIC_CLIENT_ID_END   = 3433333;

    // Client ID range for passer AIs. Same +1000 blue-team offset pattern
    // as traffic; same fix.
    public const ulong PASSER_CLIENT_ID_START  = 4444444;
    public const ulong PASSER_CLIENT_ID_END    = 4544444;
    
    /// <summary>
    /// PUBLIC API: Check if a player is ANY type of MaxPractice fake player.
    /// Use this to skip patches/modifications for AI players.
    /// Returns true for: AI goalies (DummyRed/DummyBlue), Traffic dummies, Passer AIs
    /// </summary>
    public static bool IsMaxPracticeFakePlayer(Player player)
    {
        return IsAnyFakePlayer(player);
    }
    
    /// <summary>
    /// PUBLIC API: Check if a player body belongs to a MaxPractice fake player.
    /// </summary>
    public static bool IsMaxPracticeFakePlayerBody(object body)
    {
        return IsAnyFakePlayerBody(body);
    }
    
    /// <summary>
    /// PUBLIC API: Check if a stick belongs to a MaxPractice fake player.
    /// </summary>
    public static bool IsMaxPracticeFakePlayerStick(Stick stick)
    {
        if (stick == null) return false;
        try { return IsAnyFakePlayer(stick.Player); }
        catch { return false; }
    }
    
    /// <summary>
    /// True if `clientId` falls in any range MaxPractice uses for fake players
    /// (AI goalies, traffic dummies, passer AIs). Works on stale ConnectedClientsList
    /// entries whose Player/PlayerObject have already been despawned, since it
    /// only inspects the ID.
    /// </summary>
    public static bool IsAnyFakeClientId(ulong clientId)
    {
        return clientId == FAKE_RED_CLIENT_ID
            || clientId == FAKE_BLUE_CLIENT_ID
            || (clientId >= TRAFFIC_CLIENT_ID_START && clientId < TRAFFIC_CLIENT_ID_END)
            || (clientId >= PASSER_CLIENT_ID_START && clientId < PASSER_CLIENT_ID_END);
    }

    public static bool IsFakePlayer(Player player)
    {
        if (player == null) return false;
        
        // Check by client ID first (fastest) - only AI goalie IDs
        try
        {
            ulong clientId = player.OwnerClientId;
            if (clientId == FAKE_RED_CLIENT_ID || clientId == FAKE_BLUE_CLIENT_ID)
                return true;
        }
        catch { }
        
        // Check by username containing "Dummy" (AI goalie names are DummyRed/DummyBlue)
        // Traffic dummies have names like "Traffic1", "Traffic2" etc - we DON'T want those
        try
        {
            if (player.Username != null)
            {
                string username = player.Username.Value.ToString();
                if (username != null && username.Contains("Dummy"))
                    return true;
            }
        }
        catch { }
        
        return false;
    }
    
    public static bool IsFakePlayerBody(object body)
    {
        if (body == null) return false;
        try
        {
            // Get Player property from body (works for both PlayerBodyV2 and PlayerBody)
            var playerProp = body.GetType().GetProperty("Player");
            if (playerProp == null) return false;
            Player player = (Player)playerProp.GetValue(body);
            return IsFakePlayer(player);
        }
        catch { return false; }
    }
    
    public static bool IsFakePlayerMovement(Movement movement)
    {
        if (movement == null) return false;
        try
        {
            return IsFakePlayerBody(movement.PlayerBody);
        }
        catch { return false; }
    }
    
    public static bool IsFakePlayerStickPositioner(StickPositioner positioner)
    {
        if (positioner == null) return false;
        try
        {
            return IsFakePlayer(positioner.Player);
        }
        catch { return false; }
    }
    
    /// <summary>
    /// Check if a player is a traffic dummy or passer (not AI goalie)
    /// </summary>
    public static bool IsTrafficDummy(Player player)
    {
        if (player == null) return false;
        try
        {
            // Traffic dummies use client IDs starting at 3333333
            // Passers use client IDs starting at 4444444
            ulong clientId = player.OwnerClientId;
            if (clientId >= TRAFFIC_CLIENT_ID_START && clientId < TRAFFIC_CLIENT_ID_END)
                return true;
            if (clientId >= PASSER_CLIENT_ID_START && clientId < PASSER_CLIENT_ID_END)
                return true;
                
            // Or check username
            if (player.Username != null)
            {
                string username = player.Username.Value.ToString();
                if (username != null && (username.Contains("Traffic") || username.Contains("Passer")))
                    return true;
            }
        }
        catch { }
        return false;
    }
    
    public static bool IsTrafficDummyBody(object body)
    {
        if (body == null) return false;
        try
        {
            // Get Player property from body (works for both PlayerBodyV2 and PlayerBody)
            var playerProp = body.GetType().GetProperty("Player");
            if (playerProp == null) return false;
            Player player = (Player)playerProp.GetValue(body);
            return IsTrafficDummy(player);
        }
        catch { return false; }
    }
    
    /// <summary>
    /// Check if a player is ANY fake player (goalie or traffic)
    /// </summary>
    public static bool IsAnyFakePlayer(Player player)
    {
        return IsFakePlayer(player) || IsTrafficDummy(player);
    }

    /// <summary>
    /// True for AI goalies / traffic / passers — exclude from population UI and slot counts.
    /// </summary>
    public static bool ShouldExcludeFromPopulation(Player player)
    {
        if (player == null)
            return false;
        if (MaxPracticePlugin.FakePlayers.Contains(player))
            return true;
        return IsAnyFakePlayer(player);
    }

    /// <summary>
    /// Human-only connected clients (excludes reserved fake client IDs).
    /// </summary>
    public static int CountRealConnectedClients()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm?.ConnectedClientsList == null)
            return 0;

        int n = 0;
        foreach (NetworkClient client in nm.ConnectedClientsList)
        {
            if (!IsAnyFakeClientId(client.ClientId))
                n++;
        }

        return n;
    }

    /// <summary>Fake client slots in ConnectedClientsList (browser preview / join cap).</summary>
    public static int CountFakeConnectedClients()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm?.ConnectedClientsList == null)
            return 0;

        int n = 0;
        foreach (NetworkClient client in nm.ConnectedClientsList)
        {
            if (IsAnyFakeClientId(client.ClientId))
                n++;
        }

        return n;
    }

    /// <summary>
    /// Human-only player list size for scoreboard / browser population display.
    /// Always applies fake detection even while GoalieAIManager is mid-spawn.
    /// </summary>
    public static int CountRealPopulationPlayers()
    {
        PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
        if (pm == null)
            return 0;

        int n = 0;
        foreach (Player player in pm.GetPlayers())
        {
            if (player == null || ShouldExcludeFromPopulation(player))
                continue;
            n++;
        }

        return n;
    }
    
    public static bool IsAnyFakePlayerBody(object body)
    {
        if (body == null) return false;
        try
        {
            // Get Player property from body (works for both PlayerBodyV2 and PlayerBody)
            var playerProp = body.GetType().GetProperty("Player");
            if (playerProp == null) return false;
            Player player = (Player)playerProp.GetValue(body);
            return IsAnyFakePlayer(player);
        }
        catch { return false; }
    }
}

// ============================================================================
// SKIP COMPTWEAKS PATCHES FOR FAKE PLAYERS (AI Goalies AND Traffic Dummies)
// These use FINALIZERS to catch exceptions from CompTweaks postfixes
// since returning false from prefix would skip the original method we need.
// 
// The physics adjustments (drag) only apply to AI GOALIES, not traffic.
// ============================================================================

/// <summary>
/// Catch exceptions from CompetitivePuckTweaks' PlayerBodyPatch (postfix on
/// PlayerBody.OnNetworkPostSpawn). CompTweaks 0.6b-b3 added an unguarded
/// `__instance.Player.PlayerPosition.Name == "C"` access for the CenterSpawnOffset
/// fix. Our AI goalies / traffic dummies are spawned without claiming a
/// PlayerPosition slot, so PlayerPosition is null and CompTweaks throws NRE —
/// which breaks the network spawn flow for the fake player. Finalizer swallows
/// the exception only for fake players, leaving real players untouched. Also
/// applies AI-goalie drag (CompTweaks zeros it out elsewhere).
/// </summary>
[HarmonyPatch(typeof(PlayerBody), "OnNetworkPostSpawn")]
public static class SkipCompTweaks_PlayerBodyOnNetworkPostSpawnPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(PlayerBody __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            if (FakePlayerDetector.IsAnyFakePlayerBody(__instance))
            {
                if (FakePlayerDetector.IsFakePlayerBody(__instance))
                    ApplyAIGoaliePhysics(__instance);
                return null;
            }
        }
        catch { }
        return __exception;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(PlayerBody __instance)
    {
        try
        {
            if (FakePlayerDetector.IsFakePlayerBody(__instance))
                ApplyAIGoaliePhysics(__instance);
        }
        catch { }
    }

    private static void ApplyAIGoaliePhysics(PlayerBody body)
    {
        if (!CompTweaksDetector.IsCompTweaksLoaded) return;
        try
        {
            var rb = body.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearDamping = 4.0f;
                rb.angularDamping = 1.0f;
            }
        }
        catch { }
    }
}

/// <summary>
/// Catch exceptions from CompetitivePuckTweaks' PlayerSpawnPatch (postfix on
/// Player.Server_SpawnCharacter). Same root cause as the PlayerBody finalizer
/// above — CompTweaks' CenterSpawnOffset path dereferences PlayerPosition.Name
/// without a null check, and our fake players don't claim a position.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.Server_SpawnCharacter))]
public static class SkipCompTweaks_PlayerServerSpawnCharacterPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(Player __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            if (__instance != null && FakePlayerDetector.IsAnyFakePlayer(__instance))
                return null;
        }
        catch { }
        return __exception;
    }
}

/// <summary>
/// When any fake-client Player despawns, also drop its NetworkClient record
/// from NetworkManager. NetworkObject.Despawn doesn't trigger the normal
/// OnClientDisconnectFromServer flow for our manufactured clients (they have
/// no transport socket to close), so without this the entries linger in
/// ConnectedClientsList until the server restarts — which inflates
/// ConnectedClientsList.Count for the TCP server-browser preview and
/// ConnectionApprovalManager.GetConnectionRejectionCode.
/// Covers every despawn path (CleanupDummies, DespawnAIGoalie, /removedummy,
/// SkaterAI traffic cleanup, etc.) from one central postfix.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.OnNetworkDespawn))]
public static class FakeClientNetworkManagerCleanupPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player __instance)
    {
        try
        {
            if (__instance == null) return;
            MaxPracticePlugin.RemoveFakeClientFromNetworkManager(__instance.OwnerClientId);
        }
        catch { }
    }
}

/// <summary>
/// Skip CompetitivePuckTweaks' MovementPatch (Start) for fake players.
/// CompTweaks accesses Player.IsReplay.Value which throws null for fake players.
/// </summary>
[HarmonyPatch(typeof(Movement), "Start")]
[HarmonyPriority(Priority.First)]
public static class SkipCompTweaks_MovementStartPatch
{
    public static bool Prefix(Movement __instance)
    {
        try
        {
            if (FakePlayerDetector.IsFakePlayerMovement(__instance))
            {
                // Apply our own movement settings for the AI goalie
                ApplyAIGoalieMovement(__instance);
                return false; // Skip original AND CompetitivePuckTweaks patch
            }
        }
        catch { }
        return true;
    }
    
    private static void ApplyAIGoalieMovement(Movement movement)
    {
        try
        {
            // Set reasonable goalie movement values (similar to what CompTweaks would set)
            // These are accessed via reflection since they're private
            var type = typeof(Movement);
            
            // Use reasonable defaults for goalie movement
            SetPrivateField(type, movement, "maxBackwardsSpeed", 4.5f);
            SetPrivateField(type, movement, "maxBackwardsSprintSpeed", 5.5f);
            SetPrivateField(type, movement, "maxForwardsSpeed", 4.5f);
            SetPrivateField(type, movement, "maxForwardsSprintSpeed", 5.5f);
            SetPrivateField(type, movement, "turnMaxSpeed", 180f);
            SetPrivateField(type, movement, "turnAcceleration", 720f);
            SetPrivateField(type, movement, "turnBrakeAcceleration", 720f);
            SetPrivateField(type, movement, "turnDrag", 10f);
        }
        catch { }
    }
    
    private static void SetPrivateField(Type type, object obj, string fieldName, float value)
    {
        try
        {
            var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }
        catch { }
    }
}

/// <summary>
/// Catch exceptions from CompetitivePuckTweaks' ScalingTurnSpeedPatch for fake players.
/// We use a Finalizer to catch the exception but let the original method run.
/// </summary>
[HarmonyPatch(typeof(Movement), "FixedUpdate")]
public static class SkipCompTweaks_MovementFixedUpdatePatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(Movement __instance, Exception __exception)
    {
        // If there was an exception and this is ANY fake player (goalie or traffic), suppress it
        if (__exception != null)
        {
            try
            {
                if (__instance?.PlayerBody != null && FakePlayerDetector.IsAnyFakePlayerBody(__instance.PlayerBody))
                {
                    return null; // Suppress the exception for fake players
                }
            }
            catch { }
        }
        return __exception; // Let other exceptions through
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' PlayerBodyFixedUpdatePatch on fake players.
/// CompTweaks dereferences IsUpright/IsSideways and Player.Role inside its Postfix —
/// both can be null/uninitialized on our AI goalies and traffic dummies. Also
/// re-applies AI-goalie drag here so CompTweaks' zero-drag config can't override it.
/// </summary>
[HarmonyPatch(typeof(PlayerBody), "FixedUpdate")]
public static class SkipCompTweaks_PlayerBodyFixedUpdatePatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(PlayerBody __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            if (FakePlayerDetector.IsAnyFakePlayerBody(__instance))
                return null;
        }
        catch { }
        return __exception;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(PlayerBody __instance)
    {
        try
        {
            // Hot path: skip entirely when no AI goalies/dummies exist.
            if (MaxPracticePlugin.FakePlayers == null || MaxPracticePlugin.FakePlayers.Count == 0)
                return;
            if (!NetworkManager.Singleton.IsServer) return;
            if (__instance == null) return;
            if (!FakePlayerDetector.IsFakePlayerBody(__instance)) return;

            var rb = __instance.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearDamping = 4.0f;
                rb.angularDamping = 1.0f;
            }
        }
        catch { }
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' PlayerBodyDashLeftPatch on fake players
/// (CompTweaks dereferences Player.IsReplay.Value in the prefix).
/// </summary>
[HarmonyPatch(typeof(PlayerBody), "DashLeft")]
public static class SkipCompTweaks_DashLeftPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(PlayerBody __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            if (FakePlayerDetector.IsAnyFakePlayerBody(__instance))
                return null;
        }
        catch { }
        return __exception;
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' PlayerBodyDashRightPatch on fake players.
/// </summary>
[HarmonyPatch(typeof(PlayerBody), "DashRight")]
public static class SkipCompTweaks_DashRightPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(PlayerBody __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            if (FakePlayerDetector.IsAnyFakePlayerBody(__instance))
                return null;
        }
        catch { }
        return __exception;
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' StickPatch (Postfix on Stick.OnNetworkPostSpawn).
/// Two failure modes:
///   1. Direct: __instance is a fake player's stick whose StickMesh is null
///      (CurvedStick interaction) — CompTweaks NREs reading __instance.Rigidbody.mass
///      or iterating its colliders.
///   2. Cross-iteration: CompTweaks iterates EVERY Stick in the scene to set up
///      collision ignores. If any fake-player stick anywhere has a null mesh, the
///      inner loop NREs — even though __instance is a real player. Without a
///      fallback, the real player's character spawn breaks downstream.
/// We suppress for both cases.
/// </summary>
[HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
public static class SkipCompTweaks_StickOnNetworkPostSpawnPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(Stick __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            if (__instance != null && FakePlayerDetector.IsAnyFakePlayer(__instance.Player))
                return null;

            // Cross-iteration fallback: any fake player in the scene means
            // CompTweaks could have NRE'd reaching one of them inside its
            // FindObjectsByType<Stick> loop. Only swallow null-deref exceptions
            // so genuine bugs still surface.
            if (MaxPracticePlugin.FakePlayers.Count > 0 &&
                (__exception is NullReferenceException || __exception is System.ArgumentNullException))
                return null;
        }
        catch { }
        return __exception;
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' PuckPatch (Postfix on Puck.OnNetworkPostSpawn).
/// When EnableMidStickCollider is on, CompTweaks iterates every Stick to set up
/// IgnoreCollision with each stick's BoxCollider. A fake-player stick without
/// the BoxCollider yet would pass null to Physics.IgnoreCollision and throw
/// ArgumentNullException — breaking the puck's network spawn for everyone.
/// </summary>
[HarmonyPatch(typeof(Puck), "OnNetworkPostSpawn")]
public static class SkipCompTweaks_PuckOnNetworkPostSpawnPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(Puck __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            // Same cross-iteration logic as StickOnNetworkPostSpawn — any fake
            // player in the scene means the FindObjectsByType<Stick> loop could
            // have hit one of them.
            if (MaxPracticePlugin.FakePlayers.Count > 0 &&
                (__exception is NullReferenceException || __exception is System.ArgumentNullException))
                return null;
        }
        catch { }
        return __exception;
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' StickDespawnPatch (Prefix on Stick.OnNetworkDespawn).
/// CompTweaks calls `componentInChildren.GetComponentsInChildren&lt;MeshCollider&gt;()` on
/// a possibly-null StickMesh; crashes if our fake player's stick mesh was already torn down.
/// </summary>
[HarmonyPatch(typeof(Stick), "OnNetworkDespawn")]
public static class SkipCompTweaks_StickOnNetworkDespawnPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(Stick __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            if (__instance != null && FakePlayerDetector.IsAnyFakePlayer(__instance.Player))
                return null;
        }
        catch { }
        return __exception;
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' StickFixedUpdatePatch (Postfix on
/// StickPositioner.FixedUpdate). Fires every frame and dereferences
/// `__instance.Player.Role` and `__instance.Stick.Rigidbody` — both can be null
/// briefly during spawn/despawn for fake players, so this would spam NREs.
/// </summary>
[HarmonyPatch(typeof(StickPositioner), "FixedUpdate")]
public static class SkipCompTweaks_StickPositionerFixedUpdatePatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(StickPositioner __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            if (__instance != null && FakePlayerDetector.IsAnyFakePlayer(__instance.Player))
                return null;
        }
        catch { }
        return __exception;
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' PlayerLegPadFixedUpdate (Prefix on
/// PlayerLegPad.FixedUpdate). Only active when ExtraLegPadTweening is enabled,
/// but when it is, it dereferences LegPadHelper without checking the parent
/// PlayerBody. Walk up to the body and skip for fake players.
/// </summary>
[HarmonyPatch(typeof(PlayerLegPad), "FixedUpdate")]
public static class SkipCompTweaks_PlayerLegPadFixedUpdatePatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(PlayerLegPad __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            var body = __instance != null ? __instance.GetComponentInParent<PlayerBody>() : null;
            if (FakePlayerDetector.IsAnyFakePlayerBody(body))
                return null;
        }
        catch { }
        return __exception;
    }
}

/// <summary>
/// Catch NRE from CompetitivePuckTweaks' PlayerLegPadTweenPatch (Prefix on
/// PlayerLegPad.OnStateChanged). Dereferences `componentInParent.Player.Role`
/// without a null guard; fires on every leg-pad state transition.
/// </summary>
[HarmonyPatch(typeof(PlayerLegPad), "OnStateChanged")]
public static class SkipCompTweaks_PlayerLegPadOnStateChangedPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(PlayerLegPad __instance, Exception __exception)
    {
        if (__exception == null) return null;
        try
        {
            var body = __instance != null ? __instance.GetComponentInParent<PlayerBody>() : null;
            if (FakePlayerDetector.IsAnyFakePlayerBody(body))
                return null;
        }
        catch { }
        return __exception;
    }
}

public static class PracticeStaminaPatch
{
    public static void Postfix(object __instance)
    {
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;

            var body = __instance as PlayerBody;
            if (body == null) return;
            var player = body.Player;
            if (player == null) return;

            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            if (MaxPracticePlugin.InfiniteStaminaPlayers.Contains(steamId))
            {
                body.Stamina.Value = 1f;
            }
        }
        catch (Exception) { }
    }
}

// Patch to detect when a puck ENTERS contact with a stick - for initial yoyo tracking
[HarmonyPatch(typeof(Puck), "OnCollisionEnter")]
public static class PuckCollisionEnterPatch
{
    public static void Postfix(Puck __instance, Collision collision)
    {
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;
            
            // Check if collision was with a stick
            Stick stick = collision.gameObject.GetComponent<Stick>();
            if (stick == null) return;
            
            // Get the player who owns this stick
            var player = stick.Player;
            if (player == null) return;
            
            // Get the player's steam ID
            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            ulong ownerId = player.NetworkObject != null ? player.NetworkObject.OwnerClientId : 0;
            if (steamId == 0)
                steamId = ownerId;
            
            // Always track last touched puck for /pop command
            YoyoManager.TrackLastTouchedPuckForPlayer(__instance, player);
            
            // Check if this player has yoyo mode enabled - if so, track their puck
            if (MaxPracticePlugin.YoyoPlayers.Contains(steamId))
            {
                // Notify the YoyoManager that this player touched this puck
                if (YoyoManager.Instance != null)
                {
                    YoyoManager.Instance.OnPuckFired(__instance, steamId);
                }
            }
            else
            {
                // Another player touched this puck - if they touch it, it becomes THEIR puck
                // and detaches from the previous owner
                if (YoyoManager.Instance != null)
                {
                    YoyoManager.Instance.OnPuckTouchedByOther(__instance, steamId);
                }
            }
        }
        catch (Exception) { }
    }
}

// Patch to detect when a puck leaves a stick (shot) for yoyo mode
[HarmonyPatch(typeof(Puck), "OnCollisionExit")]
public static class PuckCollisionExitPatch
{
    public static void Postfix(Puck __instance, Collision collision)
    {
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;
            
            // Check if collision was with a stick
            Stick stick = collision.gameObject.GetComponent<Stick>();
            if (stick == null) return;
            
            // Get the player who owns this stick
            var player = stick.Player;
            if (player == null) return;
            
            // Get the player's steam ID
            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            if (steamId == 0 && player.NetworkObject != null)
                steamId = player.NetworkObject.OwnerClientId;
            
            // Check if this player has yoyo mode enabled - if so, ensure their puck is tracked
            if (MaxPracticePlugin.YoyoPlayers.Contains(steamId))
            {
                // Notify the YoyoManager that this player fired this puck
                if (YoyoManager.Instance != null)
                {
                    YoyoManager.Instance.OnPuckFired(__instance, steamId);
                }
            }
            // Note: Don't detach on exit - only on enter by another player
        }
        catch (Exception) { }
    }
}

// ============================================================================
// CURVEDSTICK MOD COMPATIBILITY - Ensure stick collision for fake players
// CurvedStick may disable colliders or modify physics during stick initialization.
// These patches run AFTER CurvedStick to restore proper collision for our AI players.
// ============================================================================

/// <summary>
/// Ensure fake player sticks have proper collision enabled after all other mods process them.
/// Runs at Priority.Last to execute after CurvedStick's modifications.
/// </summary>
[HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
public static class FakePlayerStickCollisionPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(Stick __instance)
    {
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;
            
            // Check if this stick belongs to a MaxPractice fake player
            var player = __instance.Player;
            if (player == null)
            {
                ConfigManager.Dbg("[MaxPractice] FakePlayerStickCollisionPatch: player is null");
                return;
            }
            
            bool isFake = FakePlayerDetector.IsAnyFakePlayer(player);
            string username = "unknown";
            try { username = player.Username.Value.ToString(); } catch { }
            
            ConfigManager.Dbg($"[MaxPractice] FakePlayerStickCollisionPatch: Stick spawned for {username}, isFake={isFake}, clientId={player.OwnerClientId}");
            
            if (!isFake)
                return;
            
            // Re-enable all colliders on this stick to fix CurvedStick disabling them
            ConfigManager.Log($"[MaxPractice] Enabling stick collision for fake player: {username}");
            EnableStickCollision(__instance);
        }
        catch (Exception e) { ConfigManager.Dbg($"[MaxPractice] FakePlayerStickCollisionPatch error: {e.Message}"); }
    }
    
    /// <summary>
    /// Re-enables all colliders on a stick and ensures proper physics setup.
    /// Specifically targets the blade and shaft colliders in StickMesh.
    /// Also handles CurvedStick mod which may replace the blade mesh.
    /// </summary>
    public static void EnableStickCollision(Stick stick)
    {
        if (stick == null) return;
        
        try
        {
            int enabledCount = 0;
            
            // Get the StickMesh which contains the blade and shaft colliders
            var stickMesh = stick.StickMesh;
            if (stickMesh != null)
            {
                // Explicitly enable blade collider
                var bladeCollider = stickMesh.BladeCollider;
                if (bladeCollider != null)
                {
                    bladeCollider.enabled = true;
                    bladeCollider.isTrigger = false; // Ensure it's not a trigger (solid collision)
                    enabledCount++;
                    
                    // If it's a MeshCollider with null mesh, restore from another player's stick
                    var meshCol = bladeCollider as MeshCollider;
                    if (meshCol != null && meshCol.sharedMesh == null)
                    {
                        ConfigManager.Dbg("[MaxPractice] Blade MeshCollider has NULL mesh - searching for valid mesh from other sticks");
                        
                        // Find a valid blade mesh from another player's stick
                        Mesh validBladeMesh = FindValidBladeMesh(stick);
                        if (validBladeMesh != null)
                        {
                            meshCol.sharedMesh = validBladeMesh;
                            meshCol.convex = true;
                            ConfigManager.Dbg($"[MaxPractice] Restored blade mesh from another stick: {validBladeMesh.name}");
                        }
                        else
                        {
                            ConfigManager.Dbg("[MaxPractice] Could not find valid blade mesh from any stick!");
                        }
                    }
                    else if (meshCol != null)
                    {
                        meshCol.convex = true;
                        ConfigManager.Dbg($"[MaxPractice] Blade MeshCollider: convex={meshCol.convex}, mesh={meshCol.sharedMesh?.name}");
                    }
                    
                    ConfigManager.Dbg($"[MaxPractice] Blade collider: {bladeCollider.name}, type={bladeCollider.GetType().Name}, enabled={bladeCollider.enabled}");
                }
                else
                {
                    ConfigManager.Dbg("[MaxPractice] BladeCollider is null - CurvedStick may have replaced it");
                }
                
                // Explicitly enable shaft collider
                var shaftCollider = stickMesh.ShaftCollider;
                if (shaftCollider != null)
                {
                    shaftCollider.enabled = true;
                    shaftCollider.isTrigger = false;
                    enabledCount++;
                    
                    var meshCol = shaftCollider as MeshCollider;
                    if (meshCol != null)
                    {
                        meshCol.convex = true;
                    }
                    ConfigManager.Dbg($"[MaxPractice] Enabled shaft collider: {shaftCollider.name}, type={shaftCollider.GetType().Name}");
                }
            }
            else
            {
                ConfigManager.Dbg("[MaxPractice] StickMesh is null!");
            }
            
            // Enable ALL colliders on the stick and its children
            var allColliders = stick.GetComponentsInChildren<Collider>(true);
            foreach (var col in allColliders)
            {
                if (col != null)
                {
                    bool wasDisabled = !col.enabled;
                    col.enabled = true;
                    col.isTrigger = false;
                    
                    var meshCol = col as MeshCollider;
                    if (meshCol != null && meshCol.sharedMesh != null)
                    {
                        meshCol.convex = true;
                    }
                    
                    if (wasDisabled)
                    {
                        enabledCount++;
                        ConfigManager.Dbg($"[MaxPractice] Enabled collider: {col.name}, type={col.GetType().Name}");
                    }
                }
            }
            
            ConfigManager.Dbg($"[MaxPractice] Total colliders on stick: {allColliders.Length}, newly enabled: {enabledCount}");
            
            // Ensure rigidbody is set up properly
            var rb = stick.Rigidbody;
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.detectCollisions = true;
            }
        }
        catch (Exception e) { ConfigManager.Dbg($"[MaxPractice] EnableStickCollision error: {e.Message}"); }
    }
    
    /// <summary>
    /// Cache for the valid blade mesh so we don't search every time
    /// </summary>
    public static Mesh _cachedBladeMesh = null;
    
    /// <summary>
    /// Find a valid blade mesh from another player's stick
    /// </summary>
    private static Mesh FindValidBladeMesh(Stick excludeStick)
    {
        // Return cached mesh if we already found one
        if (_cachedBladeMesh != null)
            return _cachedBladeMesh;
        
        try
        {
            // Search all sticks in the scene
            var allSticks = UnityEngine.Object.FindObjectsByType<Stick>(FindObjectsSortMode.None);
            foreach (var otherStick in allSticks)
            {
                if (otherStick == null || otherStick == excludeStick)
                    continue;
                
                // Skip fake player sticks - they might also have null mesh
                if (otherStick.Player != null && FakePlayerDetector.IsAnyFakePlayer(otherStick.Player))
                    continue;
                
                var otherStickMesh = otherStick.StickMesh;
                if (otherStickMesh == null)
                    continue;
                
                var otherBladeCollider = otherStickMesh.BladeCollider as MeshCollider;
                if (otherBladeCollider != null && otherBladeCollider.sharedMesh != null)
                {
                    _cachedBladeMesh = otherBladeCollider.sharedMesh;
                    ConfigManager.Dbg($"[MaxPractice] Found valid blade mesh from {otherStick.Player?.Username.Value}: {_cachedBladeMesh.name}");
                    return _cachedBladeMesh;
                }
            }
        }
        catch (Exception e)
        {
            ConfigManager.Dbg($"[MaxPractice] Error finding valid blade mesh: {e.Message}");
        }
        
        return null;
    }
}

/// <summary>
/// Periodically re-check stick collision for fake players in case other mods disable it.
/// Only runs once every ~3 seconds (150 frames at 50fps) to minimize performance impact.
/// </summary>
[HarmonyPatch(typeof(Stick), "FixedUpdate")]
public static class FakePlayerStickFixedUpdatePatch
{
    private static int frameCounter = 0;
    private const int CHECK_INTERVAL = 150; // ~3 seconds at 50fps
    
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(Stick __instance)
    {
        frameCounter++;
        if (frameCounter % CHECK_INTERVAL != 0) return;
        
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;
            
            var player = __instance.Player;
            if (player == null || !FakePlayerDetector.IsAnyFakePlayer(player))
                return;
            
            // Re-enable collision (silent - no logging in periodic check)
            EnableStickCollisionSilent(__instance);
        }
        catch { }
    }
    
    /// <summary>
    /// Silent version of EnableStickCollision without debug logging for periodic checks
    /// </summary>
    private static void EnableStickCollisionSilent(Stick stick)
    {
        if (stick == null) return;
        
        try
        {
            var stickMesh = stick.StickMesh;
            if (stickMesh != null)
            {
                var bladeCollider = stickMesh.BladeCollider;
                if (bladeCollider != null)
                {
                    bladeCollider.enabled = true;
                    bladeCollider.isTrigger = false;
                    
                    var meshCol = bladeCollider as MeshCollider;
                    if (meshCol != null)
                    {
                        if (meshCol.sharedMesh == null && FakePlayerStickCollisionPatch._cachedBladeMesh != null)
                            meshCol.sharedMesh = FakePlayerStickCollisionPatch._cachedBladeMesh;
                        if (meshCol.sharedMesh != null)
                            meshCol.convex = true;
                    }
                }
                
                var shaftCollider = stickMesh.ShaftCollider;
                if (shaftCollider != null)
                {
                    shaftCollider.enabled = true;
                    shaftCollider.isTrigger = false;
                }
            }
            
            var rb = stick.Rigidbody;
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.detectCollisions = true;
            }        }
        catch { }
    }
}

// ============================================================================
// AUTO-SHOW CHAT ON NEW MESSAGE — client-side
// Without this, the chat panel may be hidden when our mod sends system messages
// (vote results, "Puck spawned.", etc.), so players don't see the feedback.
// Patches UIChat.AddChatMessage (client-only) to call Show() right after a message
// is added to the message list. On dedicated servers UIChat instances don't exist,
// so the patch is dormant there.
// ============================================================================
[HarmonyPatch(typeof(UIChat), nameof(UIChat.AddChatMessage))]
public static class UIChat_AutoShow_Patch
{
    [HarmonyPostfix]
    public static void Postfix(UIChat __instance)
    {
        try { if (__instance != null) __instance.Show(); }
        catch { }
    }
}

// ============================================================================
// VOTE SYSTEM PATCHES - Exclude fake players from vote calculations
// The game calculates votes needed using PlayerManager.GetPlayers().Count
// We patch VoteManagerController to exclude our fake players from that count.
// ============================================================================

/// <summary>
/// Patch VoteManager.Server_AddVote to (a) block /vs and /vw on practice-only
/// servers when DisableVoting=true, and (b) set requiredVotes from human-only
/// counts so AI goalies / traffic bots never inflate the threshold (solo = 1/1).
/// rink-strip votes are excluded — RinkStripVote already sets required from
/// CountHumanPlayersOnRink for the target rink.
/// </summary>
[HarmonyPatch(typeof(VoteManager), "Server_AddVote")]
public static class VoteManager_Server_AddVote_Patch
{
    private const string RinkStripVoteName = "rink-strip";

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(string name, string title, string description, PlayerTeam[] teams, float timeout, string steamId, ref int requiredVotes, object data = null)
    {
        try
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return true;

            if (ConfigManager.Config.DisableVoting && (name == "start" || name == "warmup"))
            {
                FlamieLog.Info($"[MaxPractice] Blocked {name} vote (DisableVoting=true)");
                return false;
            }

            // Per-rink threshold is computed in RinkStripVote.StartNewVote.
            if (string.Equals(name, RinkStripVoteName, System.StringComparison.Ordinal))
                return true;

            var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (playerManager == null)
                return true;

            int humans = CountHumanPlayersOnTeams(playerManager, teams);

            if (humans < 1) humans = 1;

            int corrected = Utils.GetVoteMajority(humans);
            if (corrected != requiredVotes)
            {
                FlamieLog.Info("[MaxPractice] " + name + " vote requiredVotes: " + requiredVotes +
                               " → " + corrected + " (humans:" + humans + ")");
                requiredVotes = corrected;
            }
        }
        catch (System.Exception ex)
        {
            FlamieLog.Error($"[MaxPractice] VoteManager.Server_AddVote patch error: {ex.Message}\n{ex.StackTrace}");
        }
        return true;
    }

    private static int CountHumanPlayers(System.Collections.Generic.List<Player> players)
    {
        if (players == null) return 0;
        int count = 0;
        foreach (Player p in players)
        {
            if (IsHumanVoter(p)) count++;
        }
        return count;
    }

    private static int CountHumanPlayersOnTeams(PlayerManager pm, PlayerTeam[] teams)
    {
        if (pm == null) return 0;
        System.Collections.Generic.List<Player> onTeams = teams != null && teams.Length > 0
            ? pm.GetPlayersByTeams(teams)
            : pm.GetPlayers(false);
        return CountHumanPlayers(onTeams);
    }

    private static bool IsHumanVoter(Player p)
    {
        if (p == null) return false;
        if (FakePlayerDetector.IsAnyFakePlayer(p)) return false;
        try
        {
            if (FakePlayerDetector.IsAnyFakeClientId(p.OwnerClientId)) return false;
        }
        catch { }
        try { if (p.IsReplay != null && p.IsReplay.Value) return false; } catch { }
        return true;
    }
}

// ============================================================================
// WARMUP TIMER PAUSE - For practice-only servers
// When ModConfig.PauseWarmupTimer is true, the warmup countdown is suppressed
// so the server never transitions out of warmup on its own. Manual phase
// changes (admin commands, etc.) still work because we only skip the periodic
// game-state tick while Phase == Warmup.
//
// Reference (B310 decompile, GameManager.cs):
//   private void Server_Tick() {
//     int? tick = Mathf.Max(0, GameState.Value.Tick - 1);
//     Server_SetGameState(null, tick);
//   }
// Phase transitions happen in BaseGameMode.OnGameStateChanged when Tick hits 0
// (BaseGameMode.cs:205-213). Skipping Server_Tick keeps Tick > 0 so the
// transition never fires while we're in warmup.
// ============================================================================

/// <summary>
/// Patched dynamically in MaxPracticePlugin.OnEnable() against
/// GameManager.Server_Tick (B310) / Server_OnGameStateTick (B202) — we cannot
/// rely on a compile-time HarmonyPatch attribute because if the method ever
/// gets renamed in a future build, PatchAll() would fail and take every other
/// patch down with it.
/// </summary>
public static class WarmupTimerPausePatch
{
    public static bool Prefix()
    {
        try
        {
            if (!ConfigManager.Config.PauseWarmupTimer) return true;
            if (!NetworkManager.Singleton.IsServer) return true;

            var gm = NetworkBehaviourSingleton<GameManager>.Instance;
            if (gm == null) return true;

            // Only freeze the tick while we're in warmup so non-warmup phases
            // (face-off, playing, replay, etc.) continue ticking normally.
            if (gm.Phase == GamePhase.Warmup)
                return false;
        }
        catch { }
        return true;
    }
}

/// <summary>
/// Patch PlayerManager.GetPlayerSteamIds to exclude fake players from server browser player list.
/// The master server uses this list to show player count - we don't want AI players showing.
/// </summary>
/* B310: PlayerManager.GetPlayerSteamIds no longer exists
[HarmonyPatch(typeof(PlayerManager), "GetPlayerSteamIds")]
public static class PlayerManager_GetPlayerSteamIds_Patch
{
    /// <summary>
    /// Postfix to filter out fake player steam IDs from the returned array.
    /// </summary>
    [HarmonyPostfix]
    public static void Postfix(ref string[] __result)
    {
        try
        {
            // Only run on server
            if (!NetworkManager.Singleton.IsServer) return;
            
            // If no fake players, no filtering needed
            if (MaxPracticePlugin.FakePlayers.Count == 0) return;
            
            // Get the steam IDs of fake players to exclude
            var fakePlayerSteamIds = new System.Collections.Generic.HashSet<string>();
            foreach (var fakePlayer in MaxPracticePlugin.FakePlayers)
            {
                if (fakePlayer != null)
                {
                    try
                    {
                        string steamId = fakePlayer.SteamId.Value.ToString();
                        if (!string.IsNullOrEmpty(steamId))
                        {
                            fakePlayerSteamIds.Add(steamId);
                        }
                    }
                    catch { }
                }
            }
            
            if (fakePlayerSteamIds.Count == 0) return;
            
            // Filter the result array to exclude fake player steam IDs
            __result = __result.Where(steamId => !fakePlayerSteamIds.Contains(steamId)).ToArray();
        }
        catch (System.Exception ex)
        {
            FlamieLog.Error($"[MaxPractice] GetPlayerSteamIds patch error: {ex.Message}");
        }
    }
}
*/