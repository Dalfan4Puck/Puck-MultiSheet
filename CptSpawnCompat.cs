using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MaxPractice;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// MultiSheet spawns at computed rink coordinates without vanilla position select,
    /// so <see cref="Player.PlayerPosition"/> stays null. CompetitivePuckTweaks gates
    /// Movement.Start tweaks on PlayerPosition.Role (not Player.Role); we apply CPT
    /// values from Player.Role in MovementStartCompatPatch and never claim markers.
    /// </summary>
    internal static class CptSpawnCompat
    {
        private const string PluginCoreTypeName = "CompetitivePuckTweaks.src.PluginCore";
        private const string FloatComponentTypeName = "CompetitivePuckTweaks.src.FloatComponent";

        private static List<PlayerPosition> cachedMarkers;
        private static bool cacheReady;

        internal static void Reset()
        {
            cacheReady = false;
            cachedMarkers = null;
        }

        /// <summary>No-op — practice players stay positionless; CPT compat uses Player.Role.</summary>
        internal static void PreparePlayer(Player player)
        {
        }

        private static bool IsServerAuthority()
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm != null && nm.IsServer;
        }

        /// <summary>
        /// Server sim + local client prediction both need CPT movement values at Movement.Start.
        /// Remote clients stay vanilla — server authority owns their sim.
        /// </summary>
        private static bool ShouldApplyMovementCompat(Movement movement)
        {
            if (!IsPracticeContext()) return false;

            PlayerBody body = movement != null ? movement.PlayerBody : null;
            Player player = body != null ? body.Player : null;
            if (player == null || player.IsReplay.Value) return false;
            if (FakePlayerDetector.IsFakePlayer(player)) return false;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null) return false;
            if (nm.IsServer) return true;
            return body.IsOwner;
        }

        internal static bool IsPracticeContext()
        {
            try
            {
                if (PracticeFlow.ServerActive) return true;
                return PracticeFlowClient.IsOnPracticeServer;
            }
            catch { return false; }
        }

        /// <summary>Disabled — marker claims fight positionless practice flow and overwrite goalie role.</summary>
        [HarmonyPatch(typeof(Movement), "Start")]
        private static class MovementStartClaimPrefixPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Movement __instance)
            {
            }
        }

        /// <summary>
        /// Backup when marker claim failed — applies CPT movement values without PlayerPosition.
        /// Registered via PatchAll — does not depend on CPT load order.
        /// </summary>
        [HarmonyPatch(typeof(Movement), "Start")]
        private static class MovementStartCompatPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(
                Movement __instance,
                ref float ___turnAcceleration,
                ref float ___turnBrakeAcceleration,
                ref float ___turnMaxSpeed,
                ref float ___turnDrag,
                ref float ___maxBackwardsSpeed,
                ref float ___maxBackwardsSprintSpeed,
                ref float ___maxForwardsSpeed,
                ref float ___maxForwardsSprintSpeed)
            {
                if (!ShouldApplyMovementCompat(__instance)) return;

                PlayerBody body = __instance != null ? __instance.PlayerBody : null;
                Player player = body != null ? body.Player : null;
                if (player == null) return;

                // Always apply on practice spawns: CPT Movement.Start reads PlayerPosition.Role
                // and often skips on clients before marker sync; FixedUpdate scaling needs FloatComponent.
                ApplyMovementStart(
                    __instance,
                    player,
                    ref ___turnAcceleration,
                    ref ___turnBrakeAcceleration,
                    ref ___turnMaxSpeed,
                    ref ___turnDrag,
                    ref ___maxBackwardsSpeed,
                    ref ___maxBackwardsSprintSpeed,
                    ref ___maxForwardsSpeed,
                    ref ___maxForwardsSprintSpeed);
            }
        }

        /// <summary>
        /// CPT MovementPatch.Postfix NREs when PlayerPosition is still null after claim attempts.
        /// Swallow that and apply CPT values here so spawn is not aborted.
        /// </summary>
        [HarmonyPatch(typeof(Movement), "Start")]
        private static class MovementStartNreFinalizerPatch
        {
            [HarmonyFinalizer]
            private static Exception Finalizer(
                Exception __exception,
                Movement __instance,
                ref float ___turnAcceleration,
                ref float ___turnBrakeAcceleration,
                ref float ___turnMaxSpeed,
                ref float ___turnDrag,
                ref float ___maxBackwardsSpeed,
                ref float ___maxBackwardsSprintSpeed,
                ref float ___maxForwardsSpeed,
                ref float ___maxForwardsSprintSpeed)
            {
                if (__exception == null) return null;
                if (!ShouldApplyMovementCompat(__instance)) return __exception;
                if (!(__exception is NullReferenceException)) return __exception;

                PlayerBody body = __instance != null ? __instance.PlayerBody : null;
                Player player = body != null ? body.Player : null;
                if (player == null) return __exception;

                ApplyMovementStart(
                    __instance,
                    player,
                    ref ___turnAcceleration,
                    ref ___turnBrakeAcceleration,
                    ref ___turnMaxSpeed,
                    ref ___turnDrag,
                    ref ___maxBackwardsSpeed,
                    ref ___maxBackwardsSprintSpeed,
                    ref ___maxForwardsSpeed,
                    ref ___maxForwardsSprintSpeed);
                return null;
            }
        }

        private static void ApplyMovementStart(
            Movement movement,
            Player player,
            ref float turnAcceleration,
            ref float turnBrakeAcceleration,
            ref float turnMaxSpeed,
            ref float turnDrag,
            ref float maxBackwardsSpeed,
            ref float maxBackwardsSprintSpeed,
            ref float maxForwardsSpeed,
            ref float maxForwardsSprintSpeed)
        {
            object config = TryGetCptConfig();
            if (config == null) return;

            turnDrag = ReadFloat(config, "TurnDrag", turnDrag);

            PlayerRole role = player.Role == PlayerRole.Goalie ? PlayerRole.Goalie : PlayerRole.Attacker;
            if (role == PlayerRole.Attacker)
            {
                maxBackwardsSpeed = ReadFloat(config, "MaxBackwardsSpeed", maxBackwardsSpeed);
                maxBackwardsSprintSpeed = ReadFloat(config, "MaxBackwardsSprintSpeed", maxBackwardsSprintSpeed);
                maxForwardsSpeed = ReadFloat(config, "MaxForwardsSpeed", maxForwardsSpeed);
                maxForwardsSprintSpeed = ReadFloat(config, "MaxForwardsSprintSpeed", maxForwardsSprintSpeed);
                TryAddFloatComponent(movement.gameObject);
            }
            else
            {
                maxBackwardsSpeed = ReadFloat(config, "GoalieMaxBackwardsSpeed", maxBackwardsSpeed);
                maxBackwardsSprintSpeed = ReadFloat(config, "GoalieMaxBackwardsSprintSpeed", maxBackwardsSprintSpeed);
                maxForwardsSpeed = ReadFloat(config, "GoalieMaxForwardsSpeed", maxForwardsSpeed);
                maxForwardsSprintSpeed = ReadFloat(config, "GoalieMaxForwardsSprintSpeed", maxForwardsSprintSpeed);
                turnMaxSpeed = ReadFloat(config, "GoalieTurnMaxSpeed", turnMaxSpeed);
                turnAcceleration = ReadFloat(config, "GoalieTurnAcceleration", turnAcceleration);
                turnBrakeAcceleration = ReadFloat(config, "GoalieTurnBrakeAcceleration", turnBrakeAcceleration);
                turnDrag = ReadFloat(config, "GoalieTurnDrag", turnDrag);
            }
        }

        private static bool TryClaimLogicalPosition(Player player)
        {
            if (player.PlayerPosition != null)
            {
                PlayerRole want = player.Role == PlayerRole.Goalie ? PlayerRole.Goalie : PlayerRole.Attacker;
                PlayerPosition held = player.PlayerPosition;
                if (held.Role == want && held.Team == player.Team)
                    return true;

                try { held.Server_Unclaim(); }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] CPT marker unclaim failed: " + ex.Message);
                }
            }

            EnsureMarkerCache();
            if (cachedMarkers == null || cachedMarkers.Count == 0) return false;

            PlayerRole role = player.Role == PlayerRole.Goalie ? PlayerRole.Goalie : PlayerRole.Attacker;
            PlayerPosition pick = FindAvailableMarker(player.Team, role);
            if (pick == null) return false;

            try
            {
                pick.Server_Claim(player);
                return player.PlayerPosition != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] CPT marker claim failed: " + ex.Message);
                return false;
            }
        }

        private static PlayerPosition FindAvailableMarker(PlayerTeam team, PlayerRole role)
        {
            PlayerPosition fallback = null;
            for (int i = 0; i < cachedMarkers.Count; i++)
            {
                PlayerPosition pp = cachedMarkers[i];
                if (pp == null || pp.Team != team || pp.Role != role) continue;
                if (!pp.IsClaimed) return pp;
                if (fallback == null) fallback = pp;
            }
            return fallback;
        }

        private static void EnsureMarkerCache()
        {
            if (cacheReady) return;
            cacheReady = true;
            cachedMarkers = new List<PlayerPosition>(32);

            Vector3 primaryOrigin = PrimaryRinkOrigin();
            try
            {
                PlayerPosition[] positions = UnityEngine.Object.FindObjectsByType<PlayerPosition>(FindObjectsSortMode.None);
                for (int i = 0; i < positions.Length; i++)
                {
                    PlayerPosition pp = positions[i];
                    if (pp == null || IsUnderCloneRoot(pp.transform)) continue;

                    Vector3 local = pp.transform.position - primaryOrigin;
                    if (Mathf.Abs(local.x) > 35f || Mathf.Abs(local.z) > 65f) continue;
                    cachedMarkers.Add(pp);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] CPT marker scan failed: " + ex.Message);
            }

            Debug.Log("[PHLPractice] CPT spawn markers cached: " + cachedMarkers.Count);
        }

        private static Vector3 PrimaryRinkOrigin()
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            RinkSlot primary = cfg?.Rinks != null && cfg.Rinks.Count > 0 ? cfg.Rinks[0] : null;
            return primary != null ? primary.Origin : Vector3.zero;
        }

        private static bool IsUnderCloneRoot(Transform t)
        {
            while (t != null)
            {
                if (t.name.StartsWith("PHL_VanillaMultiRink", StringComparison.Ordinal) ||
                    t.name.StartsWith("PHLMultiRink", StringComparison.Ordinal))
                    return true;
                t = t.parent;
            }
            return false;
        }

        private static object TryGetCptConfig()
        {
            Type pluginCore = FindType(PluginCoreTypeName);
            if (pluginCore == null) return null;
            FieldInfo configField = pluginCore.GetField("config", BindingFlags.Public | BindingFlags.Static);
            return configField?.GetValue(null);
        }

        private static float ReadFloat(object config, string propertyName, float fallback)
        {
            try
            {
                PropertyInfo prop = config.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(float))
                    return (float)prop.GetValue(config);
            }
            catch { }
            return fallback;
        }

        private static void TryAddFloatComponent(GameObject go)
        {
            if (go == null) return;
            Type floatType = FindType(FloatComponentTypeName);
            if (floatType == null) return;
            if (go.GetComponent(floatType) != null) return;
            try { go.AddComponent(floatType); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Could not add CPT FloatComponent: " + ex.Message);
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
