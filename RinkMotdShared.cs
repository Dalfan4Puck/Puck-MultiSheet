using System.Collections.Generic;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>One rink's live status as sent from the server.</summary>
    internal sealed class RinkStatusEntry
    {
        internal string Id;
        internal string Label;
        internal int Count;
        internal float OriginX;
        internal float OriginZ;
        /// <summary>Low-friction cloned ice (synced MOTD v5+).</summary>
        internal bool SlickIce;

        internal Vector3 Origin => new Vector3(OriginX, 0f, OriginZ);
    }

    /// <summary>Server → client rink status payload for the practice MOTD.</summary>
    internal sealed class RinkMotdPayload
    {
        internal int Capacity = 5;
        internal string Title;
        internal string Subtitle;
        internal readonly List<RinkStatusEntry> Rinks = new List<RinkStatusEntry>();
        /// <summary>0 = skater (attacker), 1 = goalie — this client's role choice.</summary>
        internal byte LocalRole;
        /// <summary>Per-rink tools strip (Empty / PHL Tools). Index matches Rinks.</summary>
        internal readonly List<RinkStripMode> StripModes = new List<RinkStripMode>();
        /// <summary>Live strip vote for MOTD badges (server → client).</summary>
        internal RinkStripVoteProgress StripVoteProgress;
        /// <summary>Server-authoritative slidable physics flag (synced on status v4+).</summary>
        internal bool SlidablePhysicsEnabled;

        /// <summary>
        /// True when occupancy, capacity, role, or the local player's rink highlight
        /// changed enough to warrant rebuilding the tile buttons (not preview textures).
        /// </summary>
        internal bool NeedsRinkTileRefresh(RinkMotdPayload other, int localRink, int otherLocalRink)
        {
            if (other == null) return true;
            if (Capacity != other.Capacity || LocalRole != other.LocalRole) return true;
            if (localRink != otherLocalRink) return true;
            if (Rinks.Count != other.Rinks.Count) return true;
            for (int i = 0; i < Rinks.Count; i++)
            {
                RinkStatusEntry a = Rinks[i];
                RinkStatusEntry b = other.Rinks[i];
                if (a == null || b == null) return true;
                if (a.Count != b.Count) return true;
            }
            return false;
        }
    }

    /// <summary>Shared nearest-rink math (minimap, MOTD UI, occupancy all agree).</summary>
    internal static class RinkLocator
    {
        internal static int NearestRink(MultiRinkConfig cfg, Vector3 worldPosition)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            if (cfg?.Rinks == null) return best;
            for (int i = 0; i < cfg.Rinks.Count; i++)
            {
                RinkSlot slot = cfg.Rinks[i];
                if (slot == null) continue;
                Vector3 o = slot.Origin;
                float dx = worldPosition.x - o.x;
                float dz = worldPosition.z - o.z;
                float d = dx * dx + dz * dz;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Nearest rink by payload origins (client side, server truth).</summary>
        internal static int NearestRink(RinkMotdPayload payload, Vector3 worldPosition)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            if (payload?.Rinks == null) return best;
            for (int i = 0; i < payload.Rinks.Count; i++)
            {
                RinkStatusEntry entry = payload.Rinks[i];
                if (entry == null) continue;
                float dx = worldPosition.x - entry.OriginX;
                float dz = worldPosition.z - entry.OriginZ;
                float d = dx * dx + dz * dz;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Local player's body position only — null until their character spawns.</summary>
        internal static Vector3? LocalPlayerBodyPosition()
        {
            try
            {
                Player local = MonoBehaviourSingleton<PlayerManager>.Instance?.GetLocalPlayer();
                if (local != null && local.PlayerBody != null)
                    return local.PlayerBody.transform.position;
            }
            catch { }
            return null;
        }

        /// <summary>Local player's (or spectate camera's) current rink, from local config.</summary>
        internal static Vector3? LocalPlayerPosition()
        {
            try
            {
                Player local = MonoBehaviourSingleton<PlayerManager>.Instance?.GetLocalPlayer();
                if (local != null && local.PlayerBody != null)
                    return local.PlayerBody.transform.position;
            }
            catch { }

            if (Camera.main != null)
                return Camera.main.transform.position;
            return null;
        }
    }
}
