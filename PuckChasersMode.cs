using System;
using System.Collections.Generic;
using PuckChasers;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Per-rink Puck Chasers strip mode: 4 Red skaters (2F/2D via C/RW/LD/RD) + 1 Red goalie.
    /// Only one sheet may run Chasers at a time (shared bot client IDs + rink geometry origin).
    /// </summary>
    internal static class PuckChasersMode
    {
        private static int activeRinkIndex = -1;
        private static bool configLoaded;

        internal static int ActiveRinkIndex => activeRinkIndex;
        internal static bool IsActive => activeRinkIndex >= 0;

        internal static void Apply(int rinkIndex, RinkStripMode mode)
        {
            if (mode == RinkStripMode.PuckChasers)
                StartOnRink(rinkIndex);
            else if (activeRinkIndex == rinkIndex)
                Stop();
        }

        internal static void StopAll() => Stop();

        internal static void Tick()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !IsActive) return;

            EnsureConfig();
            GoalieAIManager.Tick();
            SkaterAIManager.Tick();

            var gm = NetworkBehaviourSingleton<GameManager>.Instance;
            if (gm != null)
            {
                GoalieAIManager.NotifyPhase(gm.Phase);
                SkaterAIManager.NotifyPhase(gm.Phase);
            }

            PossessionTracker.Tick();
            IceSituation.Tick();
        }

        private static void StartOnRink(int rinkIndex)
        {
            if (!TryGetRinkOrigin(rinkIndex, out Vector3 origin))
            {
                PracticeLog.Info("[PuckChasers] Cannot start — missing rink " + (rinkIndex + 1));
                return;
            }

            // Only one Chasers sheet — bot IDs and geometry origin are global.
            if (activeRinkIndex >= 0 && activeRinkIndex != rinkIndex)
                Stop();

            EnsureConfig();
            RinkGeometry.ActiveOrigin = origin;
            RinkGeometry.MarkerReferenceOrigin = GetPrimaryRinkOrigin();
            activeRinkIndex = rinkIndex;

            GoalieAIManager.SetAutoEnabled(true);
            SkaterAIManager.SetEnabled(true);

            SpawnCenterIcePuck(origin);

            PracticeLog.Info("[PuckChasers] Enabled on rink " + (rinkIndex + 1) +
                             " origin=" + origin.ToString("F1"));
        }

        /// <summary>
        /// Mode entry clears the sheet — give the chase a single puck at center ice so the
        /// bots have something on their own rink to play with.
        /// </summary>
        private static void SpawnCenterIcePuck(Vector3 origin)
        {
            try
            {
                PuckManager pm = MonoBehaviourSingleton<PuckManager>.Instance;
                if (pm == null) return;

                Vector3 pos = new Vector3(
                    origin.x,
                    origin.y + VanillaRinkCloner.IceSurfaceY + 0.25f,
                    origin.z);

                Puck puck = null;
                try
                {
                    var spawn = pm.GetType().GetMethod(
                        "Server_SpawnPuck",
                        new Type[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
                    if (spawn != null)
                        puck = spawn.Invoke(pm, new object[] { pos, Quaternion.identity, false }) as Puck;
                }
                catch { }

                if (puck == null)
                    puck = pm.Server_SpawnPuck(pos, Quaternion.identity);

                PracticeLog.Info("[PuckChasers] Center-ice puck spawned: " + (puck != null));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PuckChasers] Center puck spawn failed: " + ex.Message);
            }
        }

        private static void Stop()
        {
            if (!IsActive && !GoalieAIManager.AutoEnabled && !SkaterAIManager.Enabled)
                return;

            try { SkaterAIManager.SetEnabled(false); } catch { }
            try { SkaterAIManager.DespawnAll(); } catch { }
            try { GoalieAIManager.SetAutoEnabled(false); } catch { }
            try { GoalieAIManager.DespawnAll(); } catch { }

            foreach (ulong id in StandaloneFakePlayerDetector.AllFakeClientIds)
                PuckChasersRuntime.CleanupFakeClient(id);

            PuckChasersRuntime.ClearAll();
            PossessionTracker.Reset();
            IceSituation.Reset();
            RinkGeometry.ActiveOrigin = Vector3.zero;
            RinkGeometry.MarkerReferenceOrigin = Vector3.zero;
            activeRinkIndex = -1;
            PracticeLog.Info("[PuckChasers] Stopped.");
        }

        private static void EnsureConfig()
        {
            if (configLoaded) return;
            try { BackcheckerConfig.LoadServerConfig(); } catch { }
            configLoaded = true;
        }

        private static bool TryGetRinkOrigin(int rinkIndex, out Vector3 origin)
        {
            origin = Vector3.zero;
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || rinkIndex < 0 || rinkIndex >= cfg.Rinks.Count)
                return false;
            RinkSlot slot = cfg.Rinks[rinkIndex];
            if (slot == null) return false;
            origin = slot.Origin;
            return true;
        }

        private static Vector3 GetPrimaryRinkOrigin()
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            RinkSlot primary = cfg?.Rinks != null && cfg.Rinks.Count > 0 ? cfg.Rinks[0] : null;
            return primary != null ? primary.Origin : Vector3.zero;
        }

        /// <summary>True when the given world position sits on the active Chasers rink.</summary>
        internal static bool IsWorldOnActiveRink(Vector3 worldPos)
        {
            if (!IsActive) return false;
            Vector3 local = worldPos - RinkGeometry.ActiveOrigin;
            return Mathf.Abs(local.x) <= RinkGeometry.HalfWidth + 4f
                   && Mathf.Abs(local.z) <= RinkGeometry.HalfLength + 4f;
        }

        /// <summary>Despawn every puck currently on the active Chasers rink (R-key one-puck rule).</summary>
        internal static void DespawnPucksOnActiveRink(Puck except = null)
        {
            if (!IsActive) return;
            try
            {
                PuckManager pm = MonoBehaviourSingleton<PuckManager>.Instance;
                if (pm == null) return;

                List<Puck> all = null;
                try { all = pm.GetPucks(false); } catch { }
                if (all == null) return;

                for (int i = 0; i < all.Count; i++)
                {
                    Puck puck = all[i];
                    if (puck == null || puck == except || puck.gameObject == null) continue;
                    if (!IsWorldOnActiveRink(puck.transform.position)) continue;
                    try
                    {
                        CustomLevelPlugin.ProtectedPucks.Remove(puck);
                        pm.Server_DespawnPuck(puck);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PuckChasers] Puck clear failed: " + ex.Message);
            }
        }
    }
}
