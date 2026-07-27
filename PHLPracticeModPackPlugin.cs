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

        public bool OnEnable()
        {
            try
            {
                harmony = new Harmony(HarmonyId);
                RinkScoreboardTab.ResetForEnable();
                ModPatchInstaller.InstallAll(harmony);

                MultiRinkConfig.LoadServerConfig();
                PracticeLog.Verbose = MultiRinkConfig.Current.VerboseLogging;
                MultiSheetClientSettings.Load();
                LargeLevelHost.TryEnable(harmony);
                RinkMotdService.Initialize();

                runtimeObject = new GameObject("PHLPracticeModPackRuntime");
                runtimeObject.AddComponent<PHLPracticeModPackRuntime>();
                if (ModRuntimeContext.ShouldInstallClientPresentation())
                {
                    runtimeObject.AddComponent<StockPuckHider>();
                    CptThinSkaterOverride.Apply();
                }
                UnityEngine.Object.DontDestroyOnLoad(runtimeObject);

                string buildStamp = System.IO.File.GetLastWriteTime(
                    typeof(PHLPracticeModPackPlugin).Assembly.Location).ToString("yyyy-MM-dd HH:mm:ss");
                var commandList = new System.Collections.Generic.List<string>();
                foreach (RinkSlot slot in MultiRinkConfig.Current.Rinks)
                {
                    if (slot != null && !string.IsNullOrEmpty(slot.Command)) commandList.Add(slot.Command);
                }
                Debug.Log($"[PHLPractice] Enabled role={ModRuntimeContext.RoleLabel} patches={ModPatchInstaller.InstalledCount} " +
                          $"(build {buildStamp}, {commandList.Count} rinks). Commands: " +
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
                RinkMotdService.Teardown();
                LargeLevelHost.Disable();
                MultiRinkService.Reset();
                PracticeFlowClient.Reset();
                PracticeFlowServer.Reset();
                TrlReskinBridge.Clear();
                ChatOutbound.Clear();
                PracticeMotdAssets.Teardown();
                ModRuntimeContext.Reset();
                MinimapRinkView.Reset();

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
