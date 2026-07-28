using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Session-only minimap hide from the Rinks panel. Does not touch Puck settings /
    /// PlayerPrefs — snapshots whether the minimap was visible, hides for this session,
    /// and restores that visibility when toggled off or disconnecting.
    /// </summary>
    internal static class MinimapSessionOverride
    {
        private static bool suppressed;
        private static bool restoreVisible;

        internal static bool Suppressed => suppressed;

        internal static void SetSuppressed(bool hide)
        {
            if (suppressed == hide)
                return;

            suppressed = hide;
            ApplyToMinimap();
        }

        /// <summary>Restore vanilla visibility when leaving a practice server.</summary>
        internal static void RestoreOnDisconnect()
        {
            if (!suppressed)
                return;

            suppressed = false;
            ApplyToMinimap();
        }

        private static void ApplyToMinimap()
        {
            if (ModRuntimeContext.IsDedicatedGameServer)
                return;

            UIMinimap minimap = MonoBehaviourSingleton<UIManager>.Instance?.Minimap;
            if (minimap == null)
                return;

            if (suppressed)
            {
                restoreVisible = IsMinimapVisible(minimap);
                minimap.Hide();
                return;
            }

            if (restoreVisible)
                minimap.Show();

            restoreVisible = false;
        }

        private static bool IsMinimapVisible(UIMinimap minimap)
        {
            VisualElement view = minimap?.View;
            if (view == null)
                return false;

            IResolvedStyle resolved = view.resolvedStyle;
            return resolved.display != DisplayStyle.None && resolved.visibility != Visibility.Hidden;
        }

        [HarmonyPatch(typeof(UIMinimap), nameof(UIMinimap.Show))]
        internal static class BlockShowWhileSuppressedPatch
        {
            private static bool Prefix()
            {
                return !suppressed;
            }
        }
    }
}
