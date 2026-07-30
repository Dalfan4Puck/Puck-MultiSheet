using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Client-side rink status cache. The scoreboard Rinks tab owns all UI; this class
    /// holds the last server payload and triggers auto-open on join.
    /// </summary>
    internal static class RinkMotdUI
    {
        private const Key ReopenKey = Key.F9;

        private static RinkMotdPayload lastPayload;

        internal static bool TryGetLastPayload(out RinkMotdPayload payload)
        {
            payload = lastPayload;
            return payload != null;
        }

        /// <summary>Status packet from the server (show: 0 update, 1 forced, 2 welcome).</summary>
        internal static void OnStatusReceived(RinkMotdPayload payload, byte show)
        {
            if (ModRuntimeContext.IsDedicatedGameServer || payload == null) return;

            RinkMotdPayload previous = lastPayload;
            lastPayload = payload;

            RinkPreview.EnsureRig(payload);
            RequestRecaptureForStripChanges(previous, payload);

            int localRink = RinkPanelBuilder.GetLocalRinkIndex(payload);
            int prevLocalRink = previous != null ? RinkPanelBuilder.GetLocalRinkIndex(previous) : -1;
            bool tilesNeedRefresh = payload.NeedsRinkTileRefresh(previous, localRink, prevLocalRink);

            if (RinkScoreboardTab.IsMenuPaneActive)
            {
                if (tilesNeedRefresh)
                    RinkScoreboardTab.OnPayloadAvailable(payload);
                else
                    RinkScoreboardTab.RefreshPreviewTiles(payload);
            }

            bool forced = show == 1;
            bool welcome = show == 2;
            if (forced)
                RinkScoreboardTab.OpenRinksTab();
            else if (welcome)
                RinkScoreboardTab.RequestAutoOpenOnJoin();
        }

        internal static void Tick()
        {
            RinkScoreboardTab.TickAutoOpen();

            if (MultiSheetClientSettings.SkipScoreboardUi) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[ReopenKey].wasPressedThisFrame)
                RinkScoreboardTab.OpenRinksTab();
        }

        internal static void OnDisconnected()
        {
            lastPayload = null;
            RinkScoreboardTab.OnDisconnected();
        }

        internal static void Teardown()
        {
            lastPayload = null;
        }

        internal static void ApplyStripVoteProgress(RinkStripVoteProgress progress)
        {
            if (lastPayload != null)
                lastPayload.StripVoteProgress = progress;

            RinkScoreboardTab.ApplyStripVoteProgress(progress);
        }

        internal static void RefreshPreviewTiles()
        {
            if (lastPayload == null) return;
            RinkScoreboardTab.RefreshPreviewTiles(lastPayload);
        }

        private static void RequestRecaptureForStripChanges(RinkMotdPayload previous, RinkMotdPayload next)
        {
            if (previous == null || next?.StripModes == null || next.StripModes.Count == 0) return;
            if (!RinkScoreboardTab.IsMenuPaneActive) return;

            for (int i = 0; i < next.StripModes.Count; i++)
            {
                RinkStripMode mode = next.StripModes[i];
                bool changed = previous.StripModes == null
                    || i >= previous.StripModes.Count
                    || previous.StripModes[i] != mode;
                if (!changed) continue;

                RinkPreview.RequestCapture(i, extendedFrames: true);
            }
        }
    }

    /// <summary>Esc priority: scoreboard open → close board, else stock pause menu.</summary>
    [HarmonyPatch(typeof(UIManager), "OnPauseActionPerformed")]
    internal static class RinkMotdPausePatch
    {
        private static bool Prefix()
        {
            if (RinkScoreboardTab.TryHandleEsc()) return false;
            return true;
        }
    }
}
