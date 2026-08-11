using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>Client-side keybind actions wired to multisheet_client.json.</summary>
    internal static class ClientKeybindRuntime
    {
        private const float ActionCooldownSeconds = 0.25f;
        private static float lastRoleToggleTime = -999f;
        private static float lastSlidableToggleTime = -999f;
        private static float lastMinimapToggleTime = -999f;

        internal static void Tick()
        {
            if (ModRuntimeContext.IsDedicatedGameServer)
                return;

            if (ClientKeybindHelper.WasKeyPressedThisFrame(MultiSheetClientSettings.MinimapToggleKey))
                TryToggleMinimap();

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient)
                return;

            if (ClientKeybindHelper.WasKeyPressedThisFrame(MultiSheetClientSettings.ToggleRoleKey))
                TryToggleRole();

            if (ClientKeybindHelper.WasKeyPressedThisFrame(MultiSheetClientSettings.SlidableToggleKey))
                TryToggleSlidable();
        }

        private static void TryToggleRole()
        {
            if (!PracticeFlowClient.IsOnPracticeServer)
                return;
            if (Time.unscaledTime < lastRoleToggleTime + ActionCooldownSeconds)
                return;

            lastRoleToggleTime = Time.unscaledTime;

            byte nextRole = 0;
            if (RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload) && payload != null)
                nextRole = payload.LocalRole > 0 ? (byte)0 : (byte)1;
            else
            {
                try
                {
                    Player local = MonoBehaviourSingleton<PlayerManager>.Instance?.GetLocalPlayer();
                    if (local != null)
                        nextRole = local.Role == PlayerRole.Goalie ? (byte)0 : (byte)1;
                }
                catch { }
            }

            RinkMotdService.ClientRequestSetRole(nextRole);
        }

        private static void TryToggleMinimap()
        {
            if (Time.unscaledTime < lastMinimapToggleTime + ActionCooldownSeconds)
                return;

            lastMinimapToggleTime = Time.unscaledTime;
            MinimapSessionOverride.SetSuppressed(!MinimapSessionOverride.Suppressed);
        }

        private static void TryToggleSlidable()
        {
            if (Time.unscaledTime < lastSlidableToggleTime + ActionCooldownSeconds)
                return;

            lastSlidableToggleTime = Time.unscaledTime;

            int rinkIndex = ActiveRinkResolver.ResolveLocalRinkIndex();
            if (RinkStripModeUtil.IsSlidableToggleBlocked(rinkIndex))
            {
                RinkMotdService.ClientRequestSlidableBlockedNotice(rinkIndex);
                return;
            }

            bool next = !ActiveRinkResolver.IsSlidableEnabledForLocalRink();

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient)
                return;

            if (nm.IsServer)
            {
                FlamiePracFeatures.SetSlidablePhysicsEnabled(rinkIndex, next);
                RinkMotdService.BroadcastStatus();
            }
            else
            {
                RinkMotdService.ClientRequestSetSlidable(rinkIndex, next);
                if (RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload) && payload != null)
                {
                    while (payload.SlidableByRink.Count <= rinkIndex)
                        payload.SlidableByRink.Add(false);
                    payload.SlidableByRink[rinkIndex] = next;
                }
            }
        }
    }
}
