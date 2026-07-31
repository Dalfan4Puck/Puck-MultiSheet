using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Client presentation toggles from the Rinks tab: render scope and whether
    /// MultiSheet may restyle the arena (day/night, glare, TRL re-layer).
    /// </summary>
    internal static class PracticePresentation
    {
        internal static bool RenderAllRinks
        {
            get { return MultiSheetClientSettings.RenderAllRinks; }
        }

        internal static bool AllowRinkChanges
        {
            get { return MultiSheetClientSettings.AllowRinkChanges; }
        }

        internal static void SetRenderAllRinks(bool renderAll)
        {
            if (MultiSheetClientSettings.RenderAllRinks == renderAll) return;
            MultiSheetClientSettings.RenderAllRinks = renderAll;
            MultiSheetClientSettings.Save();
            ArenaLighting.RefreshRinkLightCulling();
            PracticeLog.Info("[PHLPractice] Render scope: " + (renderAll ? "all rinks" : "local rink only"));
            CloneVisualProxy.LogRenderScopeStatus();
        }

        /// <summary>
        /// Allow = MultiSheet day/night, glare, outdoor sync, TRL compatibility hooks.
        /// Limit = unpatch TRL hooks, restore stock TRL/lighting (same idea as leaving
        /// the practice cosmetics), keep multi-rink geometry.
        /// </summary>
        internal static void SetAllowRinkChanges(bool allow)
        {
            if (MultiSheetClientSettings.AllowRinkChanges == allow)
            {
                MultiSheetClientSettings.Save();
                return;
            }

            MultiSheetClientSettings.AllowRinkChanges = allow;
            MultiSheetClientSettings.Save();

            if (allow)
                EnterAllowMode();
            else
                EnterLimitMode();
        }

        /// <summary>Apply the persisted mode after client clones/lights exist.</summary>
        internal static void ApplyAfterClientBuild()
        {
            MultiSheetClientSettings.Load();
            if (AllowRinkChanges)
            {
                TrlReskinBridge.SetCompatibilityEnabled(true);
                // ArenaLighting.Apply is the normal path from CustomLevelBridge.
            }
            else
            {
                EnterLimitMode();
            }
        }

        private static void EnterLimitMode()
        {
            TrlReskinBridge.SetCompatibilityEnabled(false);
            ArenaLighting.EnterStockLook();
            TrlReskinBridge.RequestTrlRebuild();
            PracticeLog.Info("[PHLPractice] Limit Rink Changes — stock TRL + simple lighting.");
        }

        private static void EnterAllowMode()
        {
            TrlReskinBridge.SetCompatibilityEnabled(true);
            ArenaLighting.ExitStockLook();
            TrlReskinBridge.RequestTrlRebuild();
            PracticeLog.Info("[PHLPractice] Allow Rink Changes — MultiSheet presentation active.");
        }
    }
}
