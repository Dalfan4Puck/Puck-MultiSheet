using HarmonyLib;
using UnityEngine;

namespace PHLPracticeModPack
{
    internal sealed class PHLPracticeModPackRuntime : MonoBehaviour
    {
        private void Update()
        {
            RinkMotdService.Tick();
            ChatOutbound.Tick();
            // Settings flush used to live on ArenaLightingEnforcer; lean lighting may
            // not spawn that object (fixed indoor / pinned hour).
            MultiSheetClientSettings.Flush();
        }

        private void LateUpdate()
        {
            // After every other mod's Update so the practice clock wins the frame.
            PracticeFlowClient.LateTick();
            RinkPreview.LateTick();
        }

        private void OnDestroy()
        {
            RinkMotdService.Teardown();
        }
    }

    internal static class LargeLevelHost
    {
        private static Harmony largeLevelHarmony;
        internal static bool IsEnabled { get; private set; }

        internal static bool TryEnable(Harmony sharedHarmony)
        {
            if (IsEnabled) return true;

            largeLevelHarmony = new Harmony("PHL.PHLPracticeModPack.LargeLevel");
            if (!CustomLevelPlugin.TryInstall(largeLevelHarmony))
                return false;

            IsEnabled = true;
            return true;
        }

        internal static void Disable()
        {
            if (!IsEnabled) return;

            CustomLevelPlugin.Teardown();
            try { largeLevelHarmony?.UnpatchSelf(); }
            catch { }

            largeLevelHarmony = null;
            IsEnabled = false;
        }
    }
}
