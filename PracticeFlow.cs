using System;
using System.Collections.Generic;
using HarmonyLib;
using MaxPractice;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Practice join flow. Server: every joiner is auto-assigned to Blue (no
    /// team-select prompt), then spawns on rink 1 by default (or their rink pick).
    /// Client: the stock team/position select screens are suppressed, the pre-spawn
    /// camera becomes a wide overhead shot of all rinks, and the HUD clock counts UP
    /// from the moment the local player joined (each client sees their own timer).
    /// Esc-menu "Select Team" still works: that request is deliberate, so the stock
    /// team screen shows and Red/Spectator remain reachable.
    /// </summary>
    internal static class PracticeFlow
    {
        internal static bool ServerActive
        {
            get
            {
                try
                {
                    return MultiRinkConfig.Current.EnableMultiRink
                        && NetworkManager.Singleton != null
                        && NetworkManager.Singleton.IsServer;
                }
                catch { return false; }
            }
        }
    }

    // ==================================================================== server

    internal static class PracticeFlowServer
    {
        private static readonly HashSet<ulong> autoTeamed = new HashSet<ulong>();
        private static readonly Dictionary<ulong, RoleSwitchVelocity> pendingRoleVelocities =
            new Dictionary<ulong, RoleSwitchVelocity>();

        private struct RoleSwitchVelocity
        {
            internal Vector3 Linear;
            internal Vector3 Angular;
            internal int FramesLeft;
        }

        /// <summary>Server frame tick: auto-team new joiners, respawn team-switchers.</summary>
        internal static void Tick()
        {
            if (!PracticeFlow.ServerActive) return;

            PlayerManager pm;
            try { pm = MonoBehaviourSingleton<PlayerManager>.Instance; }
            catch { return; }
            if (pm == null) return;

            ApplyPendingRoleSwitchVelocities(pm);

            foreach (Player player in pm.GetPlayers())
            {
                if (player == null) continue;
                try
                {
                    if (player.IsReplay.Value) continue;

                    if (player.Phase == PlayerPhase.TeamSelect && !autoTeamed.Contains(player.OwnerClientId))
                    {
                        // First TeamSelect for this client = the join prompt. Later
                        // TeamSelect phases come from the Esc menu and are left alone.
                        autoTeamed.Add(player.OwnerClientId);
                        player.Server_SetGameState(PlayerPhase.PositionSelect, PlayerTeam.Blue, PlayerRole.Attacker);
                        PracticeLog.Info("[PHLPractice] Auto-assigned client " + player.OwnerClientId +
                                  " to Blue (awaiting rink pick).");
                    }
                    else if (player.Phase == PlayerPhase.PositionSelect
                        && !player.IsCharacterSpawned
                        && MultiRinkService.GetActiveRinkId(player.OwnerClientId) == null
                        && autoTeamed.Contains(player.OwnerClientId))
                    {
                        // First-time joiners: spawn on rink 1 if they never picked a sheet
                        // (closing the tab without clicking still lands them on ice).
                        MultiRinkConfig cfg = MultiRinkConfig.Current;
                        if (cfg?.Rinks != null && cfg.Rinks.Count > 0 && VanillaRinkCloner.LayoutReady)
                        {
                            RinkSlot defaultSlot = cfg.Rinks[0];
                            if (defaultSlot != null)
                            {
                                if (RinkMotdService.IsRinkFullFor(player.OwnerClientId, defaultSlot, out string fullMessage))
                                {
                                    if (!string.IsNullOrEmpty(fullMessage))
                                        RinkMotdService.QueuePrivateChatForClient(player.OwnerClientId, fullMessage);
                                }
                                else if (MultiRinkService.TryAssignRink(player.OwnerClientId, defaultSlot, out _))
                                {
                                    PracticeLog.Info("[PHLPractice] Auto-spawned client " + player.OwnerClientId +
                                              " on rink 1.");
                                }
                            }
                        }
                    }
                    else if (player.Phase == PlayerPhase.PositionSelect
                        && !player.IsCharacterSpawned
                        && (player.Team == PlayerTeam.Blue || player.Team == PlayerTeam.Red)
                        && MultiRinkService.GetActiveRinkId(player.OwnerClientId) != null)
                    {
                        // Back from an Esc-menu team switch: they already have a rink,
                        // so skip position select entirely and respawn them on it.
                        RinkSlot slot = FindRinkSlot(MultiRinkConfig.Current, MultiRinkService.GetActiveRinkId(player.OwnerClientId));
                        if (slot != null && RinkMotdService.IsRinkFullFor(player.OwnerClientId, slot, out string fullMessage))
                        {
                            MultiRinkService.ClearActiveRink(player.OwnerClientId);
                            if (!string.IsNullOrEmpty(fullMessage))
                                RinkMotdService.QueuePrivateChatForClient(player.OwnerClientId, fullMessage);
                            continue;
                        }

                        player.Server_SetGameState(PlayerPhase.Play);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Practice flow tick failed: " + ex.Message);
                }
            }
        }

        /// <summary>Spawn a positionless player at their chosen rink (center ice or crease).</summary>
        /// <param name="spawnPosition">When set with <paramref name="spawnRotation"/>, spawn here instead of rink defaults (role switches).</param>
        /// <param name="inheritLinearVelocity">When set, carry momentum from the despawned body (role switches).</param>
        internal static void SpawnPositionless(
            Player player,
            Vector3? spawnPosition = null,
            Quaternion? spawnRotation = null,
            Vector3? inheritLinearVelocity = null,
            Vector3? inheritAngularVelocity = null)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0) return;

            RinkSlot slot = FindRinkSlot(cfg, MultiRinkService.GetActiveRinkId(player.OwnerClientId));
            if (slot == null) return;

            bool spawnInPlace = spawnPosition.HasValue && spawnRotation.HasValue;
            if (!spawnInPlace && RinkMotdService.IsRinkFullFor(player.OwnerClientId, slot, out string fullMessage))
            {
                MultiRinkService.ClearActiveRink(player.OwnerClientId);
                if (!string.IsNullOrEmpty(fullMessage))
                    RinkMotdService.QueuePrivateChatForClient(player.OwnerClientId, fullMessage);
                try
                {
                    player.Server_SetGameState(PlayerPhase.PositionSelect);
                }
                catch { }
                return;
            }

            // CPT Movement.Start compat applies from Player.Role — no marker claim.
            CptSpawnCompat.PreparePlayer(player);

            PlayerRole role = player.Role == PlayerRole.Goalie ? PlayerRole.Goalie : PlayerRole.Attacker;
            if (role == PlayerRole.Goalie)
            {
                PlayerTeam team = PracticeGoalieSpawn.ResolveGoalieTeam(slot, player.OwnerClientId);
                if (player.Team != team)
                    player.Server_SetGameState(null, team, PlayerRole.Goalie);

                Vector3 position;
                Quaternion rot;
                if (spawnInPlace)
                {
                    position = spawnPosition.Value;
                    rot = spawnRotation.Value;
                }
                else
                {
                    PracticeGoalieSpawn.TryGetGoaliePose(slot, player.Team, out position, out rot);
                }

                player.Server_SpawnCharacter(position, rot, PlayerRole.Goalie);
                MultiRinkService.RefreshChunkSlot(player.PlayerBody);
                ApplyInheritedRoleVelocity(player, inheritLinearVelocity, inheritAngularVelocity);
                PracticeLog.Info("[PHLPractice] Spawned client " + player.OwnerClientId + " as " + player.Team +
                          " goalie at " + slot.Id + " pos=" + position.ToString("F2") +
                          " body=" + (player.PlayerBody != null
                              ? player.PlayerBody.transform.position.ToString("F2")
                              : "<null>") + ".");
                return;
            }

            Vector3 skaterPos = spawnInPlace
                ? spawnPosition.Value
                : MultiRinkService.GetSpawnPosition(slot, player);
            Quaternion skaterRot = spawnInPlace
                ? spawnRotation.Value
                : MultiRinkService.GetSpawnRotation(player.Team);
            player.Server_SpawnCharacter(skaterPos, skaterRot, PlayerRole.Attacker);
            MultiRinkService.RefreshChunkSlot(player.PlayerBody);
            ApplyInheritedRoleVelocity(player, inheritLinearVelocity, inheritAngularVelocity);
            PracticeLog.Info("[PHLPractice] Spawned client " + player.OwnerClientId + " at " + slot.Id +
                      (spawnInPlace ? " (in place)." : " center ice (positionless)."));
        }

        /// <summary>Flip skater/goalie — same path as the menu buttons and the P key.</summary>
        internal static bool TryToggleRole(Player player, out string message)
        {
            message = null;
            if (player == null)
            {
                message = "Could not find your player.";
                return false;
            }

            PlayerRole current = player.Role == PlayerRole.Goalie ? PlayerRole.Goalie : PlayerRole.Attacker;
            PlayerRole next = current == PlayerRole.Goalie ? PlayerRole.Attacker : PlayerRole.Goalie;
            return TrySetRole(player.OwnerClientId, next, out message);
        }

        /// <summary>Switch skater/goalie from the MOTD UI or P key; respawn in place when bodied.</summary>
        internal static bool TrySetRole(ulong clientId, PlayerRole role, out string message)
        {
            message = null;
            MultiRinkConfig cfg = MultiRinkConfig.Current;

            PlayerManager pm;
            try { pm = MonoBehaviourSingleton<PlayerManager>.Instance; }
            catch { pm = null; }
            Player player = pm != null ? pm.GetPlayerByClientId(clientId) : null;
            if (player == null)
            {
                message = "Could not find your player.";
                return false;
            }

            PlayerRole normalized = role == PlayerRole.Goalie ? PlayerRole.Goalie : PlayerRole.Attacker;
            PlayerRole current = player.Role == PlayerRole.Goalie ? PlayerRole.Goalie : PlayerRole.Attacker;
            if (current == normalized)
            {
                message = normalized == PlayerRole.Goalie ? "Already goalie." : "Already skater.";
                return true;
            }

            if (!player.IsCharacterSpawned)
            {
                player.Server_SetGameState(null, null, normalized);
                message = normalized == PlayerRole.Goalie
                    ? "Goalie selected — pick a rink."
                    : "Skater selected — pick a rink.";
                NotifyRoleChanged();
                return true;
            }

            Vector3 respawnPos = default;
            Quaternion respawnRot = Quaternion.identity;
            bool respawnInPlace = false;
            Vector3? inheritLinearVelocity = null;
            Vector3? inheritAngularVelocity = null;
            if (player.PlayerBody != null)
            {
                respawnPos = player.PlayerBody.transform.position;
                respawnRot = player.PlayerBody.transform.rotation;
                respawnInPlace = true;
                if (TryCaptureBodyVelocity(player.PlayerBody, out Vector3 linear, out Vector3 angular))
                {
                    inheritLinearVelocity = linear;
                    inheritAngularVelocity = angular;
                }
            }

            RinkSlot slot = FindRinkSlot(cfg, MultiRinkService.GetActiveRinkId(clientId));
            if (slot == null && respawnInPlace && cfg?.Rinks != null)
            {
                int idx = RinkLocator.NearestRink(cfg, respawnPos);
                if (idx >= 0 && idx < cfg.Rinks.Count) slot = cfg.Rinks[idx];
            }

            // Tear the old character down BEFORE the role flips. Writing the role first
            // left a skater body wearing a goalie game state for a frame, and everything
            // that reacts to the role (equipment, jersey, stick prefab choice) ran against
            // the body that was about to be thrown away.
            ReleaseClaimedPosition(player);
            try { player.Server_DespawnCharacter(); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Role switch despawn failed: " + ex.Message);
            }

            if (slot == null)
            {
                player.Server_SetGameState(PlayerPhase.PositionSelect, null, normalized);
                message = normalized == PlayerRole.Goalie
                    ? "Goalie selected — pick a rink."
                    : "Skater selected — pick a rink.";
                NotifyRoleChanged();
                return true;
            }

            MultiRinkService.RememberActiveRink(clientId, slot.Id);

            // One game-state write with the final team AND role, so the respawn below sees
            // a fully consistent state.
            if (normalized == PlayerRole.Goalie)
            {
                PlayerTeam team = PracticeGoalieSpawn.ResolveGoalieTeam(slot, clientId);
                player.Server_SetGameState(null, team, PlayerRole.Goalie);
            }
            else
            {
                PlayerTeam team = player.Team == PlayerTeam.Red ? PlayerTeam.Red : PlayerTeam.Blue;
                player.Server_SetGameState(null, team, PlayerRole.Attacker);
            }

            // A handler inside the stock Server_SpawnCharacter call stack can throw
            // after the character is already spawned — the switch still worked, so
            // never announce a failure to the player; just log for diagnostics.
            try
            {
                if (respawnInPlace)
                    SpawnPositionless(player, respawnPos, respawnRot, inheritLinearVelocity, inheritAngularVelocity);
                else
                    SpawnPositionless(player);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Role switch respawn threw (spawn usually completed): " + ex.Message);
            }
            message = normalized == PlayerRole.Goalie ? "Switched to goalie." : "Switched to skater.";
            NotifyRoleChanged();
            return true;
        }

        private static void ReleaseClaimedPosition(Player player)
        {
            if (player?.PlayerPosition == null) return;
            try { player.PlayerPosition.Server_Unclaim(); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Position unclaim failed: " + ex.Message);
            }
        }

        /// <summary>Push LocalRole to clients so menu highlights match P-key toggles.</summary>
        private static void NotifyRoleChanged()
        {
            if (!PracticeFlow.ServerActive) return;
            try { RinkMotdService.BroadcastStatus(); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Role status broadcast failed: " + ex.Message);
            }
        }

        private static RinkSlot FindRinkSlot(MultiRinkConfig cfg, string rinkId)
        {
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0) return null;
            if (rinkId != null)
            {
                for (int i = 0; i < cfg.Rinks.Count; i++)
                {
                    if (cfg.Rinks[i] != null && cfg.Rinks[i].Id == rinkId)
                        return cfg.Rinks[i];
                }
            }
            return cfg.Rinks[0];
        }

        internal static void OnClientDisconnected(ulong clientId)
        {
            autoTeamed.Remove(clientId);
            pendingRoleVelocities.Remove(clientId);
        }

        internal static void Reset()
        {
            autoTeamed.Clear();
            pendingRoleVelocities.Clear();
            CptSpawnCompat.Reset();
        }

        private static bool TryCaptureBodyVelocity(PlayerBody body, out Vector3 linear, out Vector3 angular)
        {
            linear = Vector3.zero;
            angular = Vector3.zero;
            if (body == null) return false;

            Rigidbody rb = null;
            try { rb = body.Rigidbody; }
            catch { }
            if (rb == null)
            {
                try { rb = body.GetComponent<Rigidbody>(); }
                catch { }
            }
            if (rb == null) return false;

            linear = rb.linearVelocity;
            angular = rb.angularVelocity;
            return linear.sqrMagnitude > 0.01f || angular.sqrMagnitude > 0.01f;
        }

        private static void ApplyInheritedRoleVelocity(
            Player player,
            Vector3? inheritLinearVelocity,
            Vector3? inheritAngularVelocity)
        {
            if (!inheritLinearVelocity.HasValue || player?.PlayerBody == null)
                return;

            Vector3 linear = inheritLinearVelocity.Value;
            Vector3 angular = inheritAngularVelocity ?? Vector3.zero;
            if (TryApplyBodyVelocity(player.PlayerBody, linear, angular))
                QueueRoleSwitchVelocity(player.OwnerClientId, linear, angular);
        }

        private static void QueueRoleSwitchVelocity(ulong clientId, Vector3 linear, Vector3 angular)
        {
            pendingRoleVelocities[clientId] = new RoleSwitchVelocity
            {
                Linear = linear,
                Angular = angular,
                FramesLeft = 4,
            };
        }

        private static void ApplyPendingRoleSwitchVelocities(PlayerManager pm)
        {
            if (pendingRoleVelocities.Count == 0 || pm == null)
                return;

            List<ulong> finished = null;
            foreach (KeyValuePair<ulong, RoleSwitchVelocity> entry in pendingRoleVelocities)
            {
                Player player = pm.GetPlayerByClientId(entry.Key);
                if (player?.PlayerBody == null)
                    continue;

                if (!TryApplyBodyVelocity(player.PlayerBody, entry.Value.Linear, entry.Value.Angular))
                    continue;

                RoleSwitchVelocity next = entry.Value;
                next.FramesLeft--;
                if (next.FramesLeft <= 0)
                {
                    if (finished == null) finished = new List<ulong>();
                    finished.Add(entry.Key);
                }
                else
                {
                    pendingRoleVelocities[entry.Key] = next;
                }
            }

            if (finished == null) return;
            for (int i = 0; i < finished.Count; i++)
                pendingRoleVelocities.Remove(finished[i]);
        }

        private static bool TryApplyBodyVelocity(PlayerBody body, Vector3 linear, Vector3 angular)
        {
            if (body == null) return false;

            Rigidbody rb = null;
            try { rb = body.Rigidbody; }
            catch { }
            if (rb == null)
            {
                try { rb = body.GetComponent<Rigidbody>(); }
                catch { }
            }
            if (rb == null) return false;

            try
            {
                rb.linearVelocity = linear;
                rb.angularVelocity = angular;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Positionless players entering Play must not hit the stock spawn code — it
        /// reads player.PlayerPosition.transform and would null-ref. The method lives
        /// on a generic game mode, so it is patched manually against the closed type
        /// the game actually instantiates (PublicGameMode&lt;PublicGameModeConfig&gt;).
        /// </summary>
        internal static void InstallSpawnPatch(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(
                    typeof(StandardGameMode<PublicGameModeConfig>), "OnPlayerPhaseChanged");
                var prefix = AccessTools.Method(
                    typeof(PracticeFlowServer), nameof(OnPlayerPhaseChangedPrefix));
                if (target == null)
                {
                    Debug.LogWarning("[PHLPractice] StandardGameMode.OnPlayerPhaseChanged not found; positionless spawns unavailable.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                PracticeLog.Info("[PHLPractice] Positionless spawn patch installed.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[PHLPractice] Positionless spawn patch failed: " + ex);
            }
        }

        private static bool OnPlayerPhaseChangedPrefix(Player player, PlayerPhase oldPhase, PlayerPhase newPhase)
        {
            if (!PracticeFlow.ServerActive) return true;
            if (player == null) return true;

            // MaxPractice AI goalies spawn with PlayerPosition=null on purpose.
            // Puck Chasers bots (8111xxx) place their own bodies on the active Chasers
            // rink — SpawnPositionless would drop them at rink 1 (no active rink id).
            if (FakePlayerDetector.IsFakePlayer(player)
                || GoalieAIManager.IsAIGoalie(player)
                || GoalieAIManager.IsAIGoalieClientId(player.OwnerClientId)
                || PuckChasers.StandaloneFakePlayerDetector.IsAnyFakeClientId(player.OwnerClientId))
            {
                return false;
            }

            if (newPhase == PlayerPhase.PositionSelect && player.IsCharacterSpawned)
            {
                // Stock P used to land here and despawn — practice uses in-place role toggle instead.
                ReleaseClaimedPosition(player);
                try { player.Server_DespawnCharacter(); }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Position-select despawn blocked/failed: " + ex.Message);
                }
                return false;
            }

            if (newPhase != PlayerPhase.Play) return true;

            // Never let vanilla Play spawn read PlayerPosition (null-ref or RW skater respawn).
            try { SpawnPositionless(player); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Positionless spawn failed: " + ex.Message + "\n" + ex.StackTrace);
            }
            return false;
        }
    }

    /// <summary>
    /// Freeze the match in Warmup: any server attempt to advance to another phase
    /// (pregame, faceoff, votes, /start) is dropped. Practice never becomes a game.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "Server_SetGameState")]
    internal static class PracticePhaseLockPatch
    {
        private static bool warned;

        private static bool Prefix(GamePhase? phase)
        {
            if (!PracticeFlow.ServerActive) return true;
            if (!phase.HasValue || phase.Value == GamePhase.Warmup) return true;
            if (!warned)
            {
                warned = true;
                PracticeLog.Info("[PHLPractice] Blocked game phase change to " + phase.Value +
                          " (practice server stays in Warmup).");
            }
            return false;
        }
    }

    /// <summary>
    /// Keep the warmup timer from ever expiring: re-arm it long before it reaches
    /// zero, so the vanilla 60-second extend loop never runs. The server-side value
    /// is irrelevant to modded clients, whose HUD clock is a personal count-up.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "Server_Tick")]
    internal static class PracticeWarmupTimerPatch
    {
        private const int ReArmSeconds = 36000; // 10 hours

        private static bool Prefix(GameManager __instance)
        {
            if (!PracticeFlow.ServerActive) return true;
            if (__instance.Phase == GamePhase.Warmup && __instance.Tick <= 5)
            {
                __instance.Server_SetGameState(null, ReArmSeconds);
                return false;
            }
            return true;
        }
    }

    // ==================================================================== client

    internal static class PracticeFlowClient
    {
        private static float joinRealtime = -1f;
        private static bool manualTeamSelect;

        private static BaseCamera grabbedCamera;
        private static Vector3 originalLocalPos;
        private static Quaternion originalLocalRot;
        private static float originalFarClip;
        private static float originalFov;
        private static bool originalFog;

        private static UnityEngine.UIElements.Label clockLabel;
        private static UIGameState clockOwner;
        private static string lastClockText;

        /// <summary>True once this connection has received a MultiSheet rink payload.</summary>
        internal static bool IsOnPracticeServer
        {
            get
            {
                if (ModRuntimeContext.IsDedicatedGameServer) return false;
                RinkMotdPayload payload;
                return RinkMotdUI.TryGetLastPayload(out payload);
            }
        }

        internal static bool BlockTeamSelect
        {
            get { return IsOnPracticeServer && !manualTeamSelect; }
        }

        internal static void NoteManualTeamSelect() { manualTeamSelect = true; }
        internal static void ClearManualTeamSelect() { manualTeamSelect = false; }

        /// <summary>
        /// Esc-menu "Select Team" / "Select Position" → welcome page. Returns false on
        /// vanilla servers so the stock flow runs untouched.
        /// </summary>
        internal static bool TryOpenWelcomeFromPauseMenu()
        {
            if (!IsOnPracticeServer) return false;
            try
            {
                UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
                if (ui != null && ui.PauseMenu != null) ui.PauseMenu.Hide();
            }
            catch { }
            try { RinkScoreboardTab.OpenRinksTab(); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Welcome open from pause menu failed: " + ex.Message);
            }
            return true;
        }

        /// <summary>Personal practice timer: seconds since the local player joined.</summary>
        internal static bool TryGetPracticeSeconds(out int seconds)
        {
            float elapsed;
            bool ok = TryGetPracticeElapsed(out elapsed);
            seconds = ok ? (int)elapsed : 0;
            return ok;
        }

        private static bool TryGetPracticeElapsed(out float seconds)
        {
            seconds = 0f;
            if (joinRealtime < 0f || !IsOnPracticeServer) return false;
            seconds = Mathf.Max(0f, Time.realtimeSinceStartup - joinRealtime);
            return true;
        }

        /// <summary>
        /// Rewrite the HUD clock every frame from LateUpdate. Other client mods
        /// (CompTweaks' precise clock) re-render the label per frame assuming a
        /// countdown, which made the tenths run backwards over our count-up —
        /// writing last in the frame wins, tenths and all.
        /// </summary>
        internal static void LateTick()
        {
            if (ModRuntimeContext.IsDedicatedGameServer) return;
            if (MultiSheetClientSettings.SkipPracticeHud) return;

            float seconds;
            if (!TryGetPracticeElapsed(out seconds))
            {
                clockLabel = null;
                clockOwner = null;
                return;
            }

            try
            {
                UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
                UIGameState gameState = ui != null ? ui.GameState : null;
                if (gameState == null)
                {
                    clockLabel = null;
                    clockOwner = null;
                    return;
                }

                if (clockLabel == null || !ReferenceEquals(gameState, clockOwner))
                {
                    clockOwner = gameState;
                    clockLabel = AccessTools.Field(typeof(UIGameState), "timeLabel")
                        ?.GetValue(gameState) as UnityEngine.UIElements.Label;
                    lastClockText = null;
                }
                if (clockLabel != null)
                {
                    string text = FormatPracticeClock(seconds);
                    if (text != lastClockText)
                    {
                        clockLabel.text = text;
                        lastClockText = text;
                    }
                }
            }
            catch { }
        }

        private static string FormatPracticeClock(float seconds)
        {
            int total = (int)seconds;
            int hours = total / 3600;
            int minutes = (total % 3600) / 60;
            int secs = total % 60;
            if (hours > 0)
                return $"{hours:D2}:{minutes:D2}:{secs:D2}";
            int tenths = (int)((seconds - total) * 10f);
            return $"{minutes:D2}:{secs:D2}.{tenths}";
        }

        internal static void OnLocalConnected()
        {
            ModSessionTeardown.MarkSessionActive();
            if (joinRealtime < 0f) joinRealtime = Time.realtimeSinceStartup;
            manualTeamSelect = false;
            RinkPanelCollapsible.ResetCollapsibleSections();
            MultiSheetClientSettings.ApplyLoadedPreferences();
        }

        internal static void OnLocalDisconnected()
        {
            joinRealtime = -1f;
            manualTeamSelect = false;
            clockLabel = null;
            clockOwner = null;
            lastClockText = null;
            ReleaseCamera();
        }

        internal static void Reset()
        {
            OnLocalDisconnected();
        }

        /// <summary>Client frame tick: overhead camera while awaiting a rink pick.</summary>
        internal static void Tick()
        {
            if (ModRuntimeContext.IsDedicatedGameServer) return;

            bool wantOverhead = false;
            try
            {
                if (IsOnPracticeServer)
                {
                    Player local = MonoBehaviourSingleton<PlayerManager>.Instance?.GetLocalPlayer();
                    wantOverhead = local != null
                        && local.Phase == PlayerPhase.PositionSelect
                        && !local.IsCharacterSpawned;
                }
            }
            catch { }

            if (wantOverhead) DriveOverheadCamera();
            else ReleaseCamera();
        }

        /// <summary>
        /// Repoint the static position-select/cinematic camera at a wide overhead view
        /// of all rinks. Client-local only — the server never reads these transforms
        /// (same trick as TRL's position-select free-look).
        /// </summary>
        private static void DriveOverheadCamera()
        {
            BaseCamera cam = null;
            try { cam = CameraManager.GetActiveCamera(); }
            catch { }
            if (cam == null) { ReleaseCamera(); return; }

            if (cam.Type != CameraType.BluePositionSelection
                && cam.Type != CameraType.RedPositionSelection
                && cam.Type != CameraType.Cinematic)
            {
                ReleaseCamera();
                return;
            }

            if (!ReferenceEquals(cam, grabbedCamera))
            {
                ReleaseCamera();
                grabbedCamera = cam;
                originalLocalPos = cam.transform.localPosition;
                originalLocalRot = cam.transform.localRotation;

                // The scene cameras only need to see one rink, so their far clip is
                // short and scene fog washes out distant sheets — the overhead view
                // sits ~250-330 m from the far rinks. Extend the clip and kill fog
                // while the shot is active (both restored on release).
                Camera unity = cam.UnityCamera != null ? cam.UnityCamera : cam.GetComponent<Camera>();
                if (unity != null)
                {
                    originalFarClip = unity.farClipPlane;
                    originalFov = unity.fieldOfView;
                    unity.farClipPlane = Mathf.Max(unity.farClipPlane, 1500f);
                    unity.fieldOfView = 62f;
                }
                originalFog = RenderSettings.fog;
                RenderSettings.fog = false;
            }

            Vector3 pos;
            Quaternion rot;
            if (!TryGetOverheadPose(out pos, out rot)) return;
            cam.transform.position = pos;
            cam.transform.rotation = rot;
        }

        private static bool TryGetOverheadPose(out Vector3 pos, out Quaternion rot)
        {
            pos = default(Vector3);
            rot = default(Quaternion);

            RinkMotdPayload payload;
            if (!RinkMotdUI.TryGetLastPayload(out payload) || payload.Rinks.Count == 0) return false;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < payload.Rinks.Count; i++)
            {
                RinkStatusEntry entry = payload.Rinks[i];
                if (entry == null) continue;
                if (entry.OriginX < minX) minX = entry.OriginX;
                if (entry.OriginX > maxX) maxX = entry.OriginX;
                if (entry.OriginZ < minZ) minZ = entry.OriginZ;
                if (entry.OriginZ > maxZ) maxZ = entry.OriginZ;
            }
            if (minX > maxX) return false;

            // Near ice level at the front-left corner, looking diagonally across the
            // whole 3×2 grid so all six sheets read in perspective behind the MOTD.
            const float iceY = 0.03f;
            const float eyeHeight = 4f;
            const float margin = 30f;

            Vector3 center = new Vector3((minX + maxX) * 0.5f, iceY, (minZ + maxZ) * 0.5f);
            pos = new Vector3(minX - margin, iceY + eyeHeight, minZ - margin);
            Vector3 lookTarget = new Vector3(
                center.x + (maxX - minX) * 0.35f,
                iceY + 0.4f,
                center.z + (maxZ - minZ) * 0.35f);
            rot = Quaternion.LookRotation((lookTarget - pos).normalized, Vector3.up);
            return true;
        }

        private static void ReleaseCamera()
        {
            if (grabbedCamera == null) return;
            try
            {
                grabbedCamera.transform.localPosition = originalLocalPos;
                grabbedCamera.transform.localRotation = originalLocalRot;
                Camera unity = grabbedCamera.UnityCamera != null
                    ? grabbedCamera.UnityCamera
                    : grabbedCamera.GetComponent<Camera>();
                if (unity != null)
                {
                    unity.farClipPlane = originalFarClip;
                    unity.fieldOfView = originalFov;
                }
                RenderSettings.fog = originalFog;
            }
            catch { }
            grabbedCamera = null;
        }

    }

    /// <summary>
    /// Suppress the stock select screens on practice servers: position select always
    /// (positions don't exist here), team select only when it wasn't deliberately
    /// requested through the Esc menu.
    /// </summary>
    [HarmonyPatch(typeof(UIView), "Show")]
    internal static class PracticeSelectScreenShowPatch
    {
        private static bool Prefix(UIView __instance, ref bool __result)
        {
            if (__instance is UIPositionSelect && PracticeFlowClient.IsOnPracticeServer)
            {
                __result = false;
                return false;
            }
            if (__instance is UITeamSelect && PracticeFlowClient.BlockTeamSelect)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(UIView), "Hide")]
    internal static class PracticeTeamSelectHidePatch
    {
        private static void Postfix(UIView __instance)
        {
            if (__instance is UITeamSelect) PracticeFlowClient.ClearManualTeamSelect();
        }
    }

    /// <summary>Esc-menu "Select Team" is a deliberate request — let the screen show.</summary>
    [HarmonyPatch(typeof(Player), "Client_RequestTeamSelectRpc")]
    internal static class PracticeManualTeamSelectPatch
    {
        private static void Prefix()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsServer) return; // server-side RPC execution
            PracticeFlowClient.NoteManualTeamSelect();
        }
    }

    /// <summary>
    /// On MultiSheet servers the Esc-menu "Select Team" / "Select Position" buttons
    /// reopen the welcome page (rink + position picker) instead of the stock select
    /// screens. The prefix swallows the click event, so no RPC reaches the server.
    /// </summary>
    [HarmonyPatch(typeof(UIPauseMenu), "OnClickSelectTeam")]
    internal static class PracticePauseSelectTeamPatch
    {
        private static bool Prefix()
        {
            return !PracticeFlowClient.TryOpenWelcomeFromPauseMenu();
        }
    }

    [HarmonyPatch(typeof(UIPauseMenu), "OnClickSelectPosition")]
    internal static class PracticePauseSelectPositionPatch
    {
        private static bool Prefix()
        {
            return !PracticeFlowClient.TryOpenWelcomeFromPauseMenu();
        }
    }

    /// <summary>
    /// Block stock position select on practice servers — role toggle is client-keybound
    /// via <see cref="ClientKeybindRuntime"/> and MOTD RPC.
    /// </summary>
    [HarmonyPatch(typeof(StandardGameMode<PublicGameModeConfig>), "OnPlayerRequestPositionSelect")]
    internal static class PracticePositionSelectTogglePatch
    {
        private static bool Prefix(Player player)
        {
            if (!PracticeFlow.ServerActive) return true;
            if (player == null || player.IsReplay.Value) return true;
            return false;
        }
    }

    /// <summary>
    /// Practice players are positionless — never let a claimed RW/C marker overwrite
    /// skater/goalie role from the menu or P key (StandardGameMode.OnPlayerPositionChanged).
    /// </summary>
    [HarmonyPatch(typeof(StandardGameMode<PublicGameModeConfig>), "OnPlayerPositionChanged")]
    internal static class PracticeBlockPositionRolePatch
    {
        private static bool Prefix(Player player)
        {
            if (!PracticeFlow.ServerActive) return true;
            if (player == null || player.IsReplay.Value) return true;
            if (FakePlayerDetector.IsFakePlayer(player)
                || GoalieAIManager.IsAIGoalie(player)
                || GoalieAIManager.IsAIGoalieClientId(player.OwnerClientId))
                return true;
            return false;
        }
    }

    /// <summary>
    /// HUD clock counts UP per client — how long you've been practicing — instead of
    /// mirroring the server's warmup countdown. The stock formatter still runs, so
    /// mm:ss rolls into h:mm:ss automatically.
    /// </summary>
    [HarmonyPatch(typeof(UIGameState), "SetTick")]
    internal static class PracticeClockPatch
    {
        private static void Prefix(ref int tick)
        {
            int seconds;
            if (PracticeFlowClient.TryGetPracticeSeconds(out seconds)) tick = seconds;
        }
    }

    [HarmonyPatch(typeof(UIGameState), "SetPhase")]
    internal static class PracticePhaseLabelPatch
    {
        private static void Prefix(ref string text)
        {
            if (PracticeFlowClient.IsOnPracticeServer) text = "PRACTICE";
        }
    }
}
