using System.Collections.Generic;
using Unity.Netcode;
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
        /// <summary>Server-authoritative slidable physics per rink (synced on status v7+).</summary>
        internal readonly List<bool> SlidableByRink = new List<bool>();

        internal bool IsSlidableEnabledForRink(int rinkIndex)
        {
            if (rinkIndex >= 0 && rinkIndex < SlidableByRink.Count)
                return SlidableByRink[rinkIndex];
            return false;
        }

        /// <summary>Join cap for one rink tile (practice modes override the server default).</summary>
        internal int CapacityForRink(int rinkIndex)
        {
            RinkStripMode mode = RinkStripMode.Empty;
            if (rinkIndex >= 0 && rinkIndex < StripModes.Count)
                mode = StripModes[rinkIndex];
            return RinkStripModeUtil.GetJoinCapacity(mode, Capacity);
        }

        /// <summary>Deprecated v4 single flag — use <see cref="IsSlidableEnabledForRink"/>.</summary>
        internal bool SlidablePhysicsEnabled;

        /// <summary>
        /// True when occupancy, capacity, strip mode, role, or the local player's rink highlight
        /// changed enough to warrant rebuilding the tile buttons (not preview textures).
        /// </summary>
        internal bool NeedsRinkTileRefresh(RinkMotdPayload other, int localRink, int otherLocalRink)
        {
            if (other == null) return true;
            if (Capacity != other.Capacity || LocalRole != other.LocalRole) return true;
            if (localRink != otherLocalRink) return true;
            if (Rinks.Count != other.Rinks.Count) return true;
            if (StripModes.Count != other.StripModes.Count) return true;

            for (int i = 0; i < Rinks.Count; i++)
            {
                RinkStatusEntry a = Rinks[i];
                RinkStatusEntry b = other.Rinks[i];
                if (a == null || b == null) return true;
                if (a.Count != b.Count) return true;

                RinkStripMode modeA = i < StripModes.Count ? StripModes[i] : RinkStripMode.Empty;
                RinkStripMode modeB = i < other.StripModes.Count ? other.StripModes[i] : RinkStripMode.Empty;
                if (modeA != modeB) return true;
                if (CapacityForRink(i) != other.CapacityForRink(i)) return true;
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

    internal static class ActiveRinkResolver
    {
        /// <summary>Rink the local player picked in the Rinks tab before teleport completes.</summary>
        private static int rememberedLocalRinkIndex = -1;

        internal static void RememberLocalRink(int rinkIndex)
        {
            if (rinkIndex >= 0)
                rememberedLocalRinkIndex = rinkIndex;
        }

        internal static void ClearRememberedLocalRink()
        {
            rememberedLocalRinkIndex = -1;
        }

        internal static int ResolveLocalRinkIndex()
        {
            if (rememberedLocalRinkIndex >= 0)
                return rememberedLocalRinkIndex;

            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsConnectedClient)
                {
                    int assigned = MultiRinkService.GetActiveRinkIndex(nm.LocalClientId);
                    if (assigned >= 0)
                        return assigned;
                }
            }
            catch { }

            if (RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload) && payload != null && payload.Rinks.Count > 0)
            {
                Vector3? pos = RinkLocator.LocalPlayerBodyPosition();
                if (pos.HasValue)
                    return RinkLocator.NearestRink(payload, pos.Value);
            }

            MultiRinkConfig cfg = MultiRinkConfig.Current;
            Vector3? bodyPos = RinkLocator.LocalPlayerBodyPosition();
            if (cfg?.Rinks != null && cfg.Rinks.Count > 0 && bodyPos.HasValue)
                return RinkLocator.NearestRink(cfg, bodyPos.Value);

            return 0;
        }

        internal static bool IsSlidableEnabledForLocalRink()
        {
            int rinkIndex = ResolveLocalRinkIndex();
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
                return FlamiePracFeatures.IsSlidablePhysicsEnabled(rinkIndex);

            if (RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload) && payload != null)
                return payload.IsSlidableEnabledForRink(rinkIndex);

            return FlamiePracFeatures.IsSlidablePhysicsEnabled(rinkIndex);
        }
    }
}
