using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server chat <c>/ep</c> (and <c>/emptypucks</c>): clear loose pucks but keep the
    /// closest one to each connected player — same idea as MaxPractice /emptypucks.
    /// MultiSheet protected (cloned / R-spawned) pucks are un-guarded first so despawn works.
    /// </summary>
    internal static class EmptyPucksCommand
    {
        internal static bool TryHandle(string command, ulong clientId, out string broadcast)
        {
            broadcast = null;
            if (command != "/ep" && command != "/emptypucks" && command != "/clearpucks")
                return false;

            PuckManager puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
            if (puckManager == null)
            {
                broadcast = "Could not find puck manager.";
                return true;
            }

            List<Puck> pucks = puckManager.GetPucks(true);
            if (pucks == null || pucks.Count == 0)
            {
                broadcast = "No pucks to clear.";
                return true;
            }

            int before = pucks.Count;
            HashSet<Puck> keep = FindClosestPuckPerPlayer(pucks);
            int cleared = 0;

            foreach (Puck puck in pucks.ToArray())
            {
                if (puck == null || keep.Contains(puck)) continue;
                // Guard blocks Server_DespawnPuck while the puck is protected.
                CustomLevelPlugin.ProtectedPucks.Remove(puck);
                try
                {
                    puckManager.Server_DespawnPuck(puck);
                    cleared++;
                }
                catch
                {
                    try
                    {
                        Object.Destroy(puck.gameObject);
                        cleared++;
                    }
                    catch { /* ignore */ }
                }
            }

            int after = puckManager.GetPucks(true)?.Count ?? 0;
            string who = ResolveUsername(clientId);
            broadcast = who + " cleared " + cleared + " puck(s), kept " + after +
                        " closest to players (was " + before + ").";
            return true;
        }

        private static HashSet<Puck> FindClosestPuckPerPlayer(List<Puck> pucks)
        {
            var keep = new HashSet<Puck>();
            NetworkManager nm = NetworkManager.Singleton;
            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (nm == null || pm == null) return keep;

            IReadOnlyList<ulong> clientIds = nm.ConnectedClientsIds;
            for (int c = 0; c < clientIds.Count; c++)
            {
                ulong clientId = clientIds[c];
                Player player = pm.GetPlayerByClientId(clientId);
                if (player == null || player.Stick == null) continue;

                Vector3 bladePos;
                try { bladePos = player.Stick.BladeHandlePosition; }
                catch { continue; }

                Puck closest = null;
                float best = float.MaxValue;
                for (int i = 0; i < pucks.Count; i++)
                {
                    Puck puck = pucks[i];
                    if (puck == null) continue;
                    float dist = Vector3.Distance(puck.transform.position, bladePos);
                    if (dist < best)
                    {
                        best = dist;
                        closest = puck;
                    }
                }
                if (closest != null) keep.Add(closest);
            }

            return keep;
        }

        private static string ResolveUsername(ulong clientId)
        {
            try
            {
                Player player = MonoBehaviourSingleton<PlayerManager>.Instance?
                    .GetPlayerByClientId(clientId);
                if (player?.Username != null)
                {
                    FixedString32Bytes name = player.Username.Value;
                    string s = name.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { /* ignore */ }
            return "Player";
        }
    }
}
