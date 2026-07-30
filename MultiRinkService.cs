using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Tracks which rink each player selected and performs server-side teleports.
    /// </summary>
    internal static class MultiRinkService
    {
        private static readonly Dictionary<ulong, string> ActiveRinkByClient = new Dictionary<ulong, string>();

        internal static void Reset()
        {
            ActiveRinkByClient.Clear();
        }

        internal static bool TryAssignRink(ulong clientId, RinkSlot slot, out string message)
        {
            message = null;
            if (slot == null)
            {
                message = "Unknown rink.";
                return false;
            }

            if (!NetworkManager.Singleton.IsServer)
            {
                message = "Rink assignment is server-only.";
                return false;
            }

            Player player = null;
            try
            {
                var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                player = pm != null ? pm.GetPlayerByClientId(clientId) : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Player lookup failed: " + ex.Message);
            }

            if (player == null)
            {
                message = "Could not find your player.";
                return false;
            }

            if (RinkMotdService.IsRinkFullFor(clientId, slot, out string fullMessage))
            {
                message = fullMessage;
                return false;
            }

            if (player.PlayerBody == null)
            {
                // Pre-spawn rink pick (practice flow): remember the choice and flip the
                // player to Play — the positionless-spawn patch places them at this
                // rink's center ice. Spectators picking a rink join Blue.
                if (!PracticeFlow.ServerActive)
                {
                    message = "Could not find your character.";
                    return false;
                }
                if (player.Phase == PlayerPhase.TeamSelect)
                {
                    player.Server_SetGameState(
                        PlayerPhase.PositionSelect, PlayerTeam.Blue, PlayerRole.Attacker);
                }
                ActiveRinkByClient[clientId] = slot.Id;
                PlayerRole role = player.Role == PlayerRole.Goalie ? PlayerRole.Goalie : PlayerRole.Attacker;
                PlayerTeam? team = null;
                if (role == PlayerRole.Goalie)
                    team = PracticeGoalieSpawn.ResolveGoalieTeam(slot, clientId);
                else if (player.Team != PlayerTeam.Blue && player.Team != PlayerTeam.Red)
                    team = PlayerTeam.Blue;
                try
                {
                    player.Server_SetGameState(PlayerPhase.Play, team, role);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Practice spawn failed: " + ex.Message);
                    message = "Spawn failed.";
                    return false;
                }
                if (player.PlayerBody != null)
                {
                    message = $"Welcome to {slot.Label} ({slot.Id})!";
                    return true;
                }
                message = "Spawn failed.";
                return false;
            }

            Vector3 target;
            Quaternion rot = Quaternion.identity;
            if (player.Role == PlayerRole.Goalie)
            {
                PlayerTeam team = PracticeGoalieSpawn.ResolveGoalieTeam(slot, clientId);
                if (player.Team != team)
                    player.Server_SetGameState(null, team, PlayerRole.Goalie);
                PracticeGoalieSpawn.TryGetGoaliePose(slot, player.Team, out target, out rot);
            }
            else
            {
                target = GetSpawnPosition(slot, player);
                rot = GetSpawnRotation(player.Team);
            }
            float beforeY = target.y;

            try
            {
                TeleportBody(player.PlayerBody, target, rot);
                RefreshChunkSlot(player.PlayerBody);
                ActiveRinkByClient[clientId] = slot.Id;
                message = $"Moved to {slot.Label} ({slot.Id}).";
                PracticeLog.Info("[PHLPractice] Teleport client=" + clientId + " slot=" + slot.Id +
                          " target=" + target + " (configY=" + beforeY.ToString("F2") + ")");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Teleport failed: " + ex.Message);
                message = "Teleport failed.";
                return false;
            }
        }

        internal static string GetActiveRinkId(ulong clientId)
        {
            return ActiveRinkByClient.TryGetValue(clientId, out string id) ? id : null;
        }

        internal static int GetActiveRinkIndex(ulong clientId)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            string rinkId = GetActiveRinkId(clientId);
            if (cfg?.Rinks != null && !string.IsNullOrEmpty(rinkId))
            {
                for (int i = 0; i < cfg.Rinks.Count; i++)
                {
                    RinkSlot slot = cfg.Rinks[i];
                    if (slot != null && slot.Id == rinkId)
                        return i;
                }
            }

            try
            {
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                Player player = pm != null ? pm.GetPlayerByClientId(clientId) : null;
                if (player?.PlayerBody != null && cfg?.Rinks != null && cfg.Rinks.Count > 0)
                    return RinkLocator.NearestRink(cfg, player.PlayerBody.transform.position);
            }
            catch { }

            return -1;
        }

        internal static void RememberActiveRink(ulong clientId, string rinkId)
        {
            if (!string.IsNullOrEmpty(rinkId))
                ActiveRinkByClient[clientId] = rinkId;
        }

        internal static void OnClientDisconnected(ulong clientId)
        {
            ActiveRinkByClient.Remove(clientId);
        }

        /// <summary>Center-ice spawn point for a rink (shared by teleports and fresh spawns).</summary>
        internal static Vector3 GetSpawnPosition(RinkSlot slot, Player player = null)
        {
            Vector3 target = VanillaRinkCloner.ResolveSpawnPoint(slot);
            target.y = ResolveSpawnY(target, slot);

            PlayerTeam team = player != null ? player.Team : PlayerTeam.Blue;
            PlayerRole role = player != null ? player.Role : PlayerRole.Attacker;
            return ApplyAttackerSpawnOffset(target, team, role);
        }

        /// <summary>
        /// Attackers spawn behind center ice, so they must look up-ice at the pucks
        /// rather than into their own net. Blue sits on the +Z side, red is mirrored.
        /// </summary>
        internal static Quaternion GetSpawnRotation(PlayerTeam team)
        {
            return team == PlayerTeam.Red
                ? Quaternion.LookRotation(Vector3.forward, Vector3.up)
                : Quaternion.LookRotation(Vector3.back, Vector3.up);
        }

        /// <summary>
        /// Nudge attackers off the center dot toward their own goal (~4.5 rink steps on
        /// a 92-step / 91.5 m sheet). Goalies stay on center ice.
        /// </summary>
        private static Vector3 ApplyAttackerSpawnOffset(Vector3 worldPos, PlayerTeam team, PlayerRole role)
        {
            if (role == PlayerRole.Goalie) return worldPos;

            const float rinkLengthMeters = 91.5f;
            const float rinkLengthSteps = 92f;
            const float stepsBack = 4.5f;
            float back = stepsBack * (rinkLengthMeters / rinkLengthSteps);

            // Local +Z is the blue goal end; −Z is red (see WarmupGoalDetection).
            float dz = team == PlayerTeam.Red ? -back : back;
            worldPos.z += dz;
            return worldPos;
        }

        internal static string BuildHelpText()
        {
            var cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0)
                return "No rinks configured.";

            var parts = new List<string> { "Rinks:" };
            for (int i = 0; i < cfg.Rinks.Count; i++)
            {
                RinkSlot slot = cfg.Rinks[i];
                if (slot == null) continue;
                parts.Add($"{slot.Command} ({slot.Label})");
            }
            parts.Add("/rinks — list rinks");
            parts.Add("/ep — clear loose pucks (keep closest per player)");
            parts.Add("/multirink-dump — write render/collider catalog JSON");
            parts.Add("Press R for a puck.");
            return string.Join("  ", parts);
        }

        private static float ResolveSpawnY(Vector3 target, RinkSlot slot)
        {
            // Deterministic spawn height — raycasts were hitting wrong Ice-layer geometry
            // (barriers, offset meshes) producing Y=24 or negative values.
            float spawnY = slot.Origin.y + VanillaRinkCloner.IceSurfaceY + 0.9f;
            PracticeLog.Info("[PHLPractice] SpawnY for " + slot.Id + ": fixed=" + spawnY.ToString("F2") +
                      " (origin.z=" + slot.Origin.z + ", iceY=" + VanillaRinkCloner.IceSurfaceY.ToString("F3") + ")");
            return spawnY;
        }

        private static void TeleportBody(PlayerBody body, Vector3 position, Quaternion rotation)
        {
            body.Server_Teleport(position, rotation);
        }

        internal static void RefreshChunkSlot(PlayerBody body)
        {
            if (body == null || !LargeLevelHost.IsEnabled) return;
            try
            {
                SynchronizedObject sync = body.GetComponent<SynchronizedObject>();
                if (sync != null)
                    CL_ChunkSyncServer.InitSlot(sync);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Chunk refresh after teleport failed: " + ex.Message);
            }
        }
    }
}
