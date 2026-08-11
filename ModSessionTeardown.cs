using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Central disconnect / disable cleanup so session patches, visuals, and handlers
    /// never leak into the menu or the next server.
    /// </summary>
    internal static class ModSessionTeardown
    {
        private static bool disconnectHandled;

        internal static void MarkSessionActive() => disconnectHandled = false;

        /// <summary>Local client left a server (mod may stay enabled for reconnect).</summary>
        internal static void OnLocalDisconnect(bool wasHosting)
        {
            if (ModRuntimeContext.IsDedicatedGameServer)
                return;

            if (disconnectHandled)
                return;

            disconnectHandled = true;

            PracticeFlowClient.OnLocalDisconnected();

            NapSleepSync.OnLocalDisconnect();

            MinimapSessionOverride.RestoreOnDisconnect();
            MinimapSessionOverride.ResetJoinSession();
            GoalieShotPhysics.ResetCeilingCache();

            RinkMotdUI.OnDisconnected();
            RinkScoreboardTab.OnDisconnected();
            RinkPreview.Teardown();

            StopFlamieSession();
            CustomLevelPlugin.OnPracticeConnectionLost(wasHosting);

            MultiSheetClientSettings.ResetJoinPresentationState();
            RinkRenderFocus.Clear();
            MinimapRinkView.Reset();

            PracticeLog.Info("[PHLPractice] Session teardown complete (local disconnect).");
        }

        /// <summary>Mod plugin disabled — full cleanup before Harmony unpatch.</summary>
        internal static void OnModDisable()
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                bool wasHosting = nm != null && nm.IsServer;
                OnLocalDisconnect(wasHosting);
            }
            catch
            {
                CustomLevelPlugin.OnPracticeConnectionLost(wasHosting: true);
            }

            TrlReskinBridge.SetCompatibilityEnabled(false);
            TrlReskinBridge.Clear();
            PracticeMotdAssets.Teardown();
            ChatOutbound.Clear();
            MultiRinkService.Reset();
            PracticeGoalieSpawn.Reset();
            CptSpawnCompat.Reset();
            RSpawnPuckDebounce.Reset();
        }

        private static void StopFlamieSession()
        {
            try
            {
                RadioController.StopSessionPlayback();
            }
            catch (System.Exception ex)
            {
                PracticeLog.Info("[PHLPractice] Radio session stop: " + ex.Message);
            }

            try
            {
                RadioSync.Reset();
            }
            catch (System.Exception ex)
            {
                PracticeLog.Info("[PHLPractice] RadioSync reset: " + ex.Message);
            }

            try
            {
                RadioHudUI.TearDown();
                RadioHudUI.CleanupLegacyUi();
            }
            catch { }

            try
            {
                FlamiePracTrainingGoalie.Despawn();
            }
            catch { }

            try
            {
                PuckChasersMode.StopAll();
            }
            catch { }

            try
            {
                TrainingSync.Instance?.NotifySessionDisconnected();
            }
            catch (System.Exception ex)
            {
                PracticeLog.Info("[PHLPractice] TrainingSync session stop: " + ex.Message);
            }
        }
    }
}
