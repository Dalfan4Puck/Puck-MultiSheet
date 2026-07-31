using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Goalie crease poses copied from the level's PlayerPosition markers (rink 1),
    /// then offset onto whichever sheet the player selected.
    /// </summary>
    internal static class PracticeGoalieSpawn
    {
        private static Vector3 blueLocalPos;
        private static Quaternion blueLocalRot = Quaternion.identity;
        private static Vector3 redLocalPos;
        private static Quaternion redLocalRot = Quaternion.identity;
        private static bool cached;

        internal static void Reset()
        {
            cached = false;
        }

        internal static PlayerTeam ResolveGoalieTeam(RinkSlot slot, ulong clientId)
        {
            if (slot == null) return PlayerTeam.Blue;
            if (HasHumanGoalieOnRink(slot, PlayerTeam.Blue, clientId))
                return PlayerTeam.Red;
            return PlayerTeam.Blue;
        }

        internal static bool HasHumanGoalieOnRink(RinkSlot slot, PlayerTeam team, ulong excludeClientId)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || slot == null) return false;

            try
            {
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm == null) return false;

                foreach (Player player in pm.GetPlayers())
                {
                    if (player == null || player.OwnerClientId == excludeClientId) continue;
                    if (player.IsReplay.Value) continue;
                    if (player.Role != PlayerRole.Goalie || player.Team != team) continue;

                    string assigned = MultiRinkService.GetActiveRinkId(player.OwnerClientId);
                    if (assigned != null && assigned == slot.Id) return true;

                    if (player.PlayerBody != null)
                    {
                        int rink = RinkLocator.NearestRink(cfg, player.PlayerBody.transform.position);
                        if (rink >= 0 && rink < cfg.Rinks.Count
                            && cfg.Rinks[rink] != null
                            && cfg.Rinks[rink].Id == slot.Id)
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        internal static bool TryGetGoaliePose(RinkSlot slot, PlayerTeam team, out Vector3 position, out Quaternion rotation)
        {
            position = default(Vector3);
            rotation = Quaternion.identity;
            if (slot == null) return false;

            EnsureCached();
            Vector3 local = team == PlayerTeam.Red ? redLocalPos : blueLocalPos;
            rotation = team == PlayerTeam.Red ? redLocalRot : blueLocalRot;

            position = slot.Origin + new Vector3(local.x, 0f, local.z);
            position.y = slot.Origin.y + VanillaRinkCloner.IceSurfaceY + 0.9f;
            return true;
        }

        private static void EnsureCached()
        {
            if (cached) return;
            cached = true;

            // Markers are cached as an offset from rink 1 and then re-based onto whichever
            // sheet the player picked, so only rink 1's own markers may be used. Anything
            // further out belongs to another sheet (or to a clone) and would throw the
            // goalie onto the wrong rink entirely.
            Vector3 primaryOrigin = PrimaryRinkOrigin();

            try
            {
                PlayerPosition[] positions = UnityEngine.Object.FindObjectsByType<PlayerPosition>(FindObjectsSortMode.None);
                for (int i = 0; i < positions.Length; i++)
                {
                    PlayerPosition pp = positions[i];
                    if (pp == null || pp.Role != PlayerRole.Goalie) continue;
                    if (IsUnderCloneRoot(pp.transform)) continue;

                    Vector3 local = pp.transform.position - primaryOrigin;
                    local.y = 0f;
                    if (Mathf.Abs(local.x) > 30f || Mathf.Abs(local.z) > 60f) continue;

                    if (pp.Team == PlayerTeam.Blue)
                    {
                        blueLocalPos = local;
                        blueLocalRot = pp.transform.rotation;
                    }
                    else if (pp.Team == PlayerTeam.Red)
                    {
                        redLocalPos = local;
                        redLocalRot = pp.transform.rotation;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Goalie marker scan failed: " + ex.Message);
            }

            if (blueLocalPos == Vector3.zero && redLocalPos == Vector3.zero)
            {
                blueLocalPos = new Vector3(0f, 0f, 40.2f);
                redLocalPos = new Vector3(0f, 0f, -40.2f);
                blueLocalRot = Quaternion.LookRotation(Vector3.back, Vector3.up);
                redLocalRot = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                Debug.LogWarning("[PHLPractice] Goalie PlayerPosition markers not found — using NetZ fallback.");
            }

            PracticeLog.Info("[PHLPractice] Goalie crease offsets: blue=" + blueLocalPos.ToString("F2") +
                      " red=" + redLocalPos.ToString("F2") +
                      " (rink1 origin " + primaryOrigin.ToString("F1") + ").");
        }

        private static Vector3 PrimaryRinkOrigin()
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            RinkSlot primary = cfg?.Rinks != null && cfg.Rinks.Count > 0 ? cfg.Rinks[0] : null;
            return primary != null ? primary.Origin : Vector3.zero;
        }

        /// <summary>True when the transform lives under one of our spawned clone roots.</summary>
        private static bool IsUnderCloneRoot(Transform t)
        {
            while (t != null)
            {
                if (t.name.StartsWith("PHL_VanillaMultiRink", System.StringComparison.Ordinal) ||
                    t.name.StartsWith("PHLMultiRink", System.StringComparison.Ordinal))
                    return true;
                t = t.parent;
            }
            return false;
        }
    }
}
