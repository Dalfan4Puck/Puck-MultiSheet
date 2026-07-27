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
                TrlReskinBridge.SetHarmony(harmony);
                RinkScoreboardTab.ResetForEnable();
                harmony.PatchAll(typeof(PHLPracticeModPackPlugin).Assembly);
                PracticeFlowServer.InstallSpawnPatch(harmony);

                MultiRinkConfig.LoadServerConfig();
                PracticeLog.Verbose = MultiRinkConfig.Current.VerboseLogging;
                // Force client JSON load + one-line skip dump into Player.log immediately.
                MultiSheetClientSettings.Load();
                LargeLevelHost.TryEnable(harmony);
                RinkMotdService.Initialize();

                // CPT loads with ThinSkaterBodies=true and does not sync it from the server.
                // Force it off in memory (+ persist client JSON) so joiners get normal bodies.
                CptThinSkaterOverride.Apply();

                runtimeObject = new GameObject("PHLPracticeModPackRuntime");
                runtimeObject.AddComponent<PHLPracticeModPackRuntime>();
                runtimeObject.AddComponent<StockPuckHider>();
                UnityEngine.Object.DontDestroyOnLoad(runtimeObject);

                string buildStamp = System.IO.File.GetLastWriteTime(
                    typeof(PHLPracticeModPackPlugin).Assembly.Location).ToString("yyyy-MM-dd HH:mm:ss");
                var commandList = new System.Collections.Generic.List<string>();
                foreach (RinkSlot slot in MultiRinkConfig.Current.Rinks)
                {
                    if (slot != null && !string.IsNullOrEmpty(slot.Command)) commandList.Add(slot.Command);
                }
                Debug.Log($"[PHLPractice] Enabled (build {buildStamp}, {commandList.Count} rinks). Commands: " +
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
