using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Minimap show/hide (default key M on the Keybinds panel). Preference is stored in
    /// multisheet_client.json and re-applied when joining a MultiSheet server.
    /// </summary>
    internal static class MinimapSessionOverride
    {
        private static bool suppressed;
        private static bool restoreVisible;

        internal static bool Suppressed => suppressed;

        internal static void ApplyPersistedPreference()
        {
            SetSuppressed(MultiSheetClientSettings.MinimapHidden, persist: false);
        }

        /// <summary>Re-hide after level/UI init if Show ran before the minimap existed.</summary>
        internal static void Tick()
        {
            if (!suppressed || ModRuntimeContext.IsDedicatedGameServer)
                return;

            UIMinimap minimap = MonoBehaviourSingleton<UIManager>.Instance?.Minimap;
            if (minimap == null)
                return;

            if (IsMinimapVisible(minimap))
                minimap.Hide();
        }

        internal static void ResetJoinSession()
        {
            // Keep persisted MinimapHidden; only clear the in-memory restore latch so
            // the next ApplyPersistedPreference() can hide again after disconnect showed it.
            restoreVisible = false;
        }

        internal static void SetSuppressed(bool hide, bool persist = true)
        {
            if (persist)
            {
                MultiSheetClientSettings.MinimapHidden = hide;
                MultiSheetClientSettings.Save();
            }

            if (suppressed == hide)
            {
                ApplyToMinimap(forceShowWhenVisible: !hide);
                return;
            }

            suppressed = hide;
            ApplyToMinimap(forceShowWhenVisible: !hide);
        }

        /// <summary>Restore vanilla visibility when leaving a practice server.</summary>
        internal static void RestoreOnDisconnect()
        {
            if (!suppressed)
                return;

            suppressed = false;
            ApplyToMinimap(forceShowWhenVisible: false);
        }

        private static void ApplyToMinimap(bool forceShowWhenVisible = false)
        {
            if (ModRuntimeContext.IsDedicatedGameServer)
                return;

            UIMinimap minimap = MonoBehaviourSingleton<UIManager>.Instance?.Minimap;
            if (minimap == null)
                return;

            if (suppressed)
            {
                if (IsMinimapVisible(minimap))
                    restoreVisible = true;
                minimap.Hide();
                return;
            }

            if (forceShowWhenVisible || restoreVisible)
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
