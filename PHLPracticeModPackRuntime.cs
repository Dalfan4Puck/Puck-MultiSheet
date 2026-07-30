using HarmonyLib;
using UnityEngine;

namespace PHLPracticeModPack
{
    internal sealed class PHLPracticeModPackRuntime : MonoBehaviour
    {
        private void Update()
        {
            RinkMotdService.Tick();
            RinkStripVote.Tick();
            ChatOutbound.Tick();
            if (ModRuntimeContext.ShouldInstallClientPresentation())
            {
                ClientKeybindRuntime.Tick();
                MinimapSessionOverride.Tick();
                MultiSheetClientSettings.TickDeferredJoinApply();
                MultiSheetClientSettings.Flush();
            }
        }

        private void LateUpdate()
        {
            if (!ModRuntimeContext.ShouldInstallClientPresentation()) return;

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
