using System;
using MaxPractice;
using Unity.Netcode;using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Pick the puck closing on a goalie fastest (used for track-puck / threat targeting).
    /// On goalie/tip-practice rinks, prefer the queued / in-flight practice shot first,
    /// and never force a look at something behind the goalie.
    /// </summary>
    internal static class GoalieThreatPuckSelector
    {
        private const float MinClosingSpeed = 0.75f;
        /// <summary>Cosine threshold: below this vs body forward counts as "behind".</summary>
        private const float BehindDotThreshold = 0.05f;

        internal static Puck SelectForPlayer(Player player)
        {
            if (player == null || player.PlayerBody == null)
                return null;

            Vector3 observer = player.PlayerBody.transform.position;

            if (TryResolvePlayerRink(player, out int rinkIndex)
                && IsPracticeLookRink(rinkIndex))
            {
                Puck practice = SelectPracticeLookPuck(player, rinkIndex);
                if (practice != null)
                    return practice;

                return SelectFastestApproaching(observer, p => !IsBehindGoalie(player, p));
            }

            return SelectFastestApproaching(observer, null);
        }

        /// <summary>
        /// Pass-practice and tip-practice track puck for any role; goalie-practice look
        /// stays goalie-only.
        /// </summary>
        internal static bool TryGetPracticeTrackPuck(Player player, out Puck puck)
        {
            puck = null;
            if (player == null || !TryResolvePlayerRink(player, out int rinkIndex))
                return false;

            RinkStripMode mode = ResolveRinkMode(rinkIndex);
            bool passPractice = mode == RinkStripMode.StretchPassing
                || mode == RinkStripMode.PointPassing
                || mode == RinkStripMode.LowCyclePassing;

            if (passPractice || mode == RinkStripMode.TipPractice)
            {
                puck = SelectPracticeLookPuck(player, rinkIndex);
                return puck != null;
            }

            if (player.Role != PlayerRole.Goalie || !IsPracticeLookRink(rinkIndex))
                return false;

            puck = SelectForPlayer(player);
            return puck != null;
        }

        private static Puck SelectPracticeLookPuck(Player player, int rinkIndex)
        {
            RinkStripMode mode = ResolveRinkMode(rinkIndex);
            bool passPractice = mode == RinkStripMode.StretchPassing
                || mode == RinkStripMode.PointPassing
                || mode == RinkStripMode.LowCyclePassing;

            if (passPractice)
            {
                Puck look = ResolvePublishedLookPuck(rinkIndex);
                if (look != null && IsPassPracticeLookTarget(player, look))
                    return look;

                Puck queued = ResolvePublishedQueuedLookPuck(rinkIndex);
                if (queued != null && IsPassPracticeLookTarget(player, queued))
                    return queued;

                return null;
            }

            // Goalie practice and its tip-practice clone share the same look rules —
            // the tipping skater gets exactly what the goalie gets.
            if (GoaliePracticeLookTarget.TryGetLookPuck(rinkIndex, out Puck goalieLook)
                && goalieLook != null
                && !IsBehindGoalie(player, goalieLook)
                && IsGoaliePracticeLookThreat(player, goalieLook))
            {
                return goalieLook;
            }

            if (GoaliePracticeLookTarget.TryGetQueuedLookPuck(rinkIndex, out Puck goalieQueued)
                && goalieQueued != null
                && !IsBehindGoalie(player, goalieQueued))
            {
                return goalieQueued;
            }

            return null;
        }

        private static Puck ResolvePublishedLookPuck(int rinkIndex)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                    return RinkPracticeDrills.ResolveLookPuck(rinkIndex);
            }
            catch { }

            return GoaliePracticeLookTarget.TryGetLookPuck(rinkIndex, out Puck look) ? look : null;
        }

        private static Puck ResolvePublishedQueuedLookPuck(int rinkIndex)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                    return RinkPracticeDrills.ResolveQueuedLookPuck(rinkIndex);
            }
            catch { }

            return GoaliePracticeLookTarget.TryGetQueuedLookPuck(rinkIndex, out Puck queued) ? queued : null;
        }

        /// <summary>Pass feeds — track drill holders even before launch velocity.</summary>
        private static bool IsPassPracticeLookTarget(Player skater, Puck puck)
        {
            if (skater?.PlayerBody == null || puck?.transform == null)
                return false;

            if (puck.gameObject == null)
                return false;

            if (puck.Rigidbody == null)
                return true;

            if (puck.Rigidbody.isKinematic)
                return true;

            Vector3 vel = puck.Rigidbody.linearVelocity;
            if (vel.magnitude < PracticeConstants.SettledPuckVelocity)
                return true;

            Vector3 toSkater = skater.PlayerBody.transform.position - puck.transform.position;
            toSkater.y = 0f;
            if (toSkater.sqrMagnitude < 0.01f)
                return true;

            float closing = Vector3.Dot(vel, toSkater.normalized);
            return closing > -0.5f;
        }

        private static RinkStripMode ResolveRinkMode(int rinkIndex)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                    return RinkStripVote.GetServerMode(rinkIndex);
            }
            catch { }

            if (RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload)
                && payload?.StripModes != null
                && rinkIndex >= 0
                && rinkIndex < payload.StripModes.Count)
            {
                return payload.StripModes[rinkIndex];
            }

            return RinkStripMode.Empty;
        }

        /// <summary>
        /// Saved pucks in the crease sit still or rebound away — don't keep camera on them.
        /// Queued spawn holders are far from the goalie with zero velocity and still count as valid.
        /// </summary>
        private static bool IsGoaliePracticeLookThreat(Player goalie, Puck puck)
        {
            if (goalie?.PlayerBody == null || puck?.transform == null)
                return false;

            if (puck.Rigidbody == null)
                return true;

            Vector3 vel = puck.Rigidbody.linearVelocity;
            float speed = vel.magnitude;
            if (speed >= PracticeConstants.SettledPuckVelocity)
            {
                Vector3 toGoalie = goalie.PlayerBody.transform.position - puck.transform.position;
                toGoalie.y = 0f;
                if (toGoalie.sqrMagnitude < 0.01f)
                    return false;

                float closing = Vector3.Dot(vel, toGoalie.normalized);
                return closing >= MinClosingSpeed;
            }

            // Nearly stopped: crease save vs queued holder at the spawn point.
            float dist = Vector3.Distance(puck.transform.position, goalie.PlayerBody.transform.position);
            return dist > 5f;
        }

        private static bool IsBehindGoalie(Player player, Puck puck)
        {
            if (player?.PlayerBody == null || puck?.transform == null)
                return false;

            Vector3 forward = player.PlayerBody.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return false;
            forward.Normalize();

            Vector3 toPuck = puck.transform.position - player.PlayerBody.transform.position;
            toPuck.y = 0f;
            if (toPuck.sqrMagnitude < 0.01f)
                return false;

            return Vector3.Dot(toPuck.normalized, forward) < BehindDotThreshold;
        }

        private static bool TryResolvePlayerRink(Player player, out int rinkIndex)
        {
            rinkIndex = -1;
            if (player == null)
                return false;

            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                {
                    int assigned = MultiRinkService.GetActiveRinkIndex(player.OwnerClientId);
                    if (assigned >= 0)
                    {
                        rinkIndex = assigned;
                        return true;
                    }
                }

                // Owning client track-look: use the same local-rink resolver as MOTD/UI.
                if (nm != null && nm.IsClient && player.OwnerClientId == nm.LocalClientId)
                {
                    rinkIndex = ActiveRinkResolver.ResolveLocalRinkIndex();
                    return rinkIndex >= 0;
                }
            }
            catch { }

            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || player.PlayerBody == null)
                return false;

            rinkIndex = RinkLocator.NearestRink(cfg, player.PlayerBody.transform.position);
            return rinkIndex >= 0;
        }

        private static bool IsPracticeLookRink(int rinkIndex)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                {
                    RinkStripMode mode = RinkStripVote.GetServerMode(rinkIndex);
                    return mode == RinkStripMode.GoaliePractice
                        || mode == RinkStripMode.TipPractice
                        || mode == RinkStripMode.StretchPassing
                        || mode == RinkStripMode.PointPassing
                        || mode == RinkStripMode.LowCyclePassing;
                }
            }
            catch { }

            if (RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload)
                && payload?.StripModes != null
                && rinkIndex >= 0
                && rinkIndex < payload.StripModes.Count)
            {
                RinkStripMode mode = payload.StripModes[rinkIndex];
                return mode == RinkStripMode.GoaliePractice
                    || mode == RinkStripMode.TipPractice
                    || mode == RinkStripMode.StretchPassing
                    || mode == RinkStripMode.PointPassing
                    || mode == RinkStripMode.LowCyclePassing;
            }

            return false;
        }

        internal static Puck SelectFastestApproaching(Vector3 observerPos, Func<Puck, bool> extraFilter)
        {
            PuckManager puckManager = PuckManager.Instance;
            if (puckManager == null)
                return null;

            var pucks = puckManager.GetPucks(false);
            if (pucks == null || pucks.Count == 0)
                return null;

            Puck best = null;
            float bestClosing = float.MinValue;
            Puck fallback = null;
            float fallbackDist = float.MaxValue;

            for (int i = 0; i < pucks.Count; i++)
            {
                Puck puck = pucks[i];
                if (!IsValidPuck(puck))
                    continue;

                if (extraFilter != null && !extraFilter(puck))
                    continue;

                Vector3 puckPos = puck.transform.position;
                Vector3 toObserver = observerPos - puckPos;
                float dist = toObserver.magnitude;
                if (dist < 0.05f)
                    continue;

                if (dist < fallbackDist)
                {
                    fallbackDist = dist;
                    fallback = puck;
                }

                Vector3 vel = puck.Rigidbody != null ? puck.Rigidbody.linearVelocity : Vector3.zero;
                float closing = Vector3.Dot(vel, toObserver / dist);
                if (closing <= MinClosingSpeed)
                    continue;

                if (closing > bestClosing)
                {
                    bestClosing = closing;
                    best = puck;
                }
            }

            return best != null ? best : fallback;
        }

        private static bool IsValidPuck(Puck puck)
        {
            if (puck == null || puck.gameObject == null || puck.transform == null)
                return false;

            try
            {
                if (puck.IsReplay != null && puck.IsReplay.Value)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }
    }
}
