using System;
using HarmonyLib;
using UnityEngine;

namespace PHLPracticeModPack
{
    public sealed class PHLPracticeModPackPlugin : IPuckPlugin
    {
        private const string HarmonyId = "PHL.PHLPracticeModPack";
        private Harmony harmony;
        private GameObject runtimeObject;
        private MyMod.Class1 flamiePrac;

        public bool OnEnable()
        {
            try
            {
                harmony = new Harmony(HarmonyId);
                RinkScoreboardTab.ResetForEnable();
                ModPatchInstaller.InstallAll(harmony);

                MultiRinkConfig.LoadServerConfig();
                TrainingObjectManager.SkipAutoStartForMultiRink = MultiRinkConfig.Current.EnableMultiRink;
                PracticeLog.Verbose = MultiRinkConfig.Current.VerboseLogging;
                FlamieLog.Verbose = PracticeLog.Verbose;
                MultiSheetClientSettings.Load();
                MultiSheetClientSettings.ApplyLoadedPreferences();
                FlamiePracFeatures.RadioServerDrivenOnly = true;
                FlamiePracFeatures.IsSlidablePhysicsLocked = RinkStripModeUtil.IsSlidablePhysicsLocked;
                // Never show the legacy top-left radio chip — Rinks tab embeds radio when ready.
                RadioHudUI.ShouldSuppressStandalone = () => true;
                LargeLevelHost.TryEnable(harmony);
                RinkMotdService.Initialize();
                RinkStripVote.Initialize();
                GoaliePracticeLookTarget.Initialize();
                NapSleepSync.Initialize();

                runtimeObject = new GameObject("PHLPracticeModPackRuntime");
                runtimeObject.AddComponent<PHLPracticeModPackRuntime>();
                PremiumMembersAnnouncer.Install(runtimeObject);
                if (ModRuntimeContext.ShouldInstallClientPresentation())
                {
                    runtimeObject.AddComponent<StockPuckHider>();
                    CptThinSkaterOverride.Apply();
                }
                UnityEngine.Object.DontDestroyOnLoad(runtimeObject);

                flamiePrac = new MyMod.Class1();
                if (!flamiePrac.OnEnable())
                {
                    Debug.LogError("[PHLPractice] FlamiePrac failed to enable — MultiSheet continues without training props.");
                    flamiePrac = null;
                }

                string buildStamp = System.IO.File.GetLastWriteTime(
                    typeof(PHLPracticeModPackPlugin).Assembly.Location).ToString("yyyy-MM-dd HH:mm:ss");
                var commandList = new System.Collections.Generic.List<string>();
                foreach (RinkSlot slot in MultiRinkConfig.Current.Rinks)
                {
                    if (slot != null && !string.IsNullOrEmpty(slot.Command)) commandList.Add(slot.Command);
                }
                Debug.Log($"[PHLPractice] Enabled role={ModRuntimeContext.RoleLabel} patches={ModPatchInstaller.InstalledCount} " +
                          $"(build {buildStamp}, {commandList.Count} rinks, flamie={(flamiePrac != null)}). Commands: " +
                          string.Join(" ", commandList) + " /rinks /ep /multirink-dump");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[PHLPractice] Enable failed: " + ex);
                return false;
            }
        }

        public bool OnDisable()
        {
            try
            {
                ModSessionTeardown.OnModDisable();

                if (flamiePrac != null)
                {
                    try { flamiePrac.OnDisable(); }
                    catch (Exception ex) { Debug.LogWarning("[PHLPractice] FlamiePrac disable failed: " + ex.Message); }
                    flamiePrac = null;
                }

                RinkMotdService.Teardown();
                PremiumMembersAnnouncer.Teardown();
                RinkStripVote.Teardown();
                GoaliePracticeLookTarget.Teardown();
                NapSleepSync.Teardown();
                NapIdleService.Teardown();
                FlamiePracFeatures.RadioServerDrivenOnly = false;
                FlamiePracFeatures.IsSlidablePhysicsLocked = null;
                RadioHudUI.ShouldSuppressStandalone = null;
                LargeLevelHost.Disable();
                PracticeFlowClient.Reset();
                PracticeFlowServer.Reset();
                ModRuntimeContext.Reset();

                try { harmony?.UnpatchSelf(); }
                catch (Exception ex) { Debug.LogWarning("[PHLPractice] UnpatchSelf failed: " + ex.Message); }
                harmony = null;

                if (runtimeObject != null)
                {
                    UnityEngine.Object.Destroy(runtimeObject);
                    runtimeObject = null;
                }

                PracticeLog.Info("[PHLPractice] Disabled.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[PHLPractice] Disable failed: " + ex);
                return false;
            }
        }
    }
}
