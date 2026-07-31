using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server-side AFK detection — enter voluntary nap after 5 minutes without movement.
    /// </summary>
    internal static class NapIdleService
    {
        private const float IdleSeconds = 300f;
        private const float TickInterval = 1f;
        private const float MoveDistance = 0.12f;
        private const float MoveSpeed = 0.35f;

        private static readonly Dictionary<ulong, Tracker> trackers = new Dictionary<ulong, Tracker>();
        private static float nextTick;

        private struct Tracker
        {
            internal Vector3 LastPos;
            internal float LastActiveTime;
        }

        internal static void Tick()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return;

            float now = Time.time;
            if (now < nextTick)
                return;
            nextTick = now + TickInterval;

            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null)
                return;

            HashSet<ulong> seen = new HashSet<ulong>();
            foreach (Player player in pm.GetPlayers())
            {
                if (player == null)
                    continue;

                ulong clientId = player.OwnerClientId;
                seen.Add(clientId);
                EvaluatePlayer(clientId, player, now);
            }

            List<ulong> stale = null;
            foreach (KeyValuePair<ulong, Tracker> pair in trackers)
            {
                if (seen.Contains(pair.Key))
                    continue;

                stale ??= new List<ulong>();
                stale.Add(pair.Key);
            }

            if (stale == null)
                return;

            for (int i = 0; i < stale.Count; i++)
                trackers.Remove(stale[i]);
        }

        internal static void Teardown()
        {
            trackers.Clear();
            nextTick = 0f;
        }

        internal static void ResetClient(ulong clientId)
        {
            trackers.Remove(clientId);
        }

        internal static void MarkActive(ulong clientId, PlayerBody body)
        {
            if (body == null)
            {
                trackers.Remove(clientId);
                return;
            }

            trackers[clientId] = new Tracker
            {
                LastPos = body.transform.position,
                LastActiveTime = Time.time,
            };
        }

        private static void EvaluatePlayer(ulong clientId, Player player, float now)
        {
            if (ShouldSkip(player, clientId))
                return;

            PlayerBody body = player.PlayerBody;
            Vector3 pos = body.transform.position;

            if (!trackers.TryGetValue(clientId, out Tracker tracker))
            {
                trackers[clientId] = new Tracker
                {
                    LastPos = pos,
                    LastActiveTime = now,
                };
                return;
            }

            if (HasMoved(ref tracker, body, pos))
            {
                tracker.LastPos = pos;
                tracker.LastActiveTime = now;
                trackers[clientId] = tracker;
                return;
            }

            if (now - tracker.LastActiveTime < IdleSeconds)
                return;

            PlayerNapService.ToggleNap(clientId, body);
            tracker.LastActiveTime = now;
            trackers[clientId] = tracker;
        }

        private static bool ShouldSkip(Player player, ulong clientId)
        {
            if (player.PlayerBody == null)
                return true;

            try
            {
                if (player.IsReplay.Value)
                    return true;
            }
            catch { }

            if (FakePlayerDetector.IsAnyFakeClientId(clientId))
                return true;

            if (PlayerNapService.IsVoluntary(clientId))
                return true;

            return false;
        }

        private static bool HasMoved(ref Tracker tracker, PlayerBody body, Vector3 pos)
        {
            Vector3 delta = pos - tracker.LastPos;
            delta.y = 0f;
            if (delta.sqrMagnitude >= MoveDistance * MoveDistance)
                return true;

            Rigidbody rb = body.Rigidbody;
            if (rb == null)
                return false;

            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            return vel.sqrMagnitude >= MoveSpeed * MoveSpeed;
        }
    }

    [HarmonyPatch(typeof(PlayerNapService), nameof(PlayerNapService.StandUp))]
    internal static class NapIdleService_StandUpPatch
    {
        private static void Postfix(ulong clientId, PlayerBody body)
        {
            NapIdleService.MarkActive(clientId, body);
        }
    }

    [HarmonyPatch(typeof(PlayerNapService), nameof(PlayerNapService.ClearClient))]
    internal static class NapIdleService_ClearClientPatch
    {
        private static void Postfix(ulong clientId)
        {
            NapIdleService.ResetClient(clientId);
        }
    }
}
