using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Client-side minimap fix for multi-rink layouts.
    ///
    /// The base game draws the minimap by normalizing each object's world position
    /// against UIMinimap.Bounds (copied from Level.Bounds on level spawn). Multi-rink
    /// mode expands Level.Bounds to span every rink, so the vanilla map graphic no
    /// longer matches: dots are squished and objects on offset rinks plot off-map.
    ///
    /// Every dot (player, puck, stick) is positioned through one private method,
    /// UIMinimap.ApplyMinimapTranslate(VisualElement, Vector3). This prefix:
    ///  1. Restores the captured vanilla single-rink bounds on the minimap.
    ///  2. Hides dots whose world position is on a different rink than the local player.
    ///  3. Shifts same-rink positions by the rink origin so they plot in rink-local
    ///     coordinates, exactly like vanilla rink 1.
    /// Switching rinks therefore switches the minimap to the new rink automatically.
    /// </summary>
    internal static class MinimapRinkView
    {
        private static bool _installed;
        private static Bounds _vanillaBounds;
        private static bool _hasVanillaBounds;

        // Local rink is recomputed at most once per rendered frame.
        private static int _localRink;
        private static int _localRinkFrame = -1;

        /// <summary>Called by ExpandWorldBounds with Level.Bounds BEFORE it is expanded.</summary>
        internal static void CaptureVanillaBounds(Bounds levelBounds)
        {
            if (_hasVanillaBounds) return;
            _vanillaBounds = levelBounds;
            _hasVanillaBounds = true;
            PracticeLog.Info($"[PHLPractice] Minimap: captured vanilla rink bounds center={levelBounds.center} size={levelBounds.size}.");
        }

        /// <summary>Pre-expansion single-rink bounds center (typically rink 1).</summary>
        internal static bool TryGetVanillaCenter(out Vector3 center)
        {
            if (_hasVanillaBounds)
            {
                center = _vanillaBounds.center;
                return true;
            }

            center = default;
            return false;
        }

        internal static void InstallPatch(Harmony harmony)
        {
            if (_installed || harmony == null) return;
            if (ModRuntimeContext.IsDedicatedGameServer) return;
            if (MultiSheetClientSettings.SkipMinimap) return;

            MethodInfo target = typeof(UIMinimap).GetMethod("ApplyMinimapTranslate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (target == null)
            {
                Debug.LogWarning("[PHLPractice] Minimap: UIMinimap.ApplyMinimapTranslate not found; rink-local minimap disabled.");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(MinimapRinkView), nameof(TranslatePrefix)));
            _installed = true;
            PracticeLog.Info("[PHLPractice] Minimap: rink-local view patch installed.");
        }

        internal static void Reset()
        {
            _installed = false;
            _hasVanillaBounds = false;
            _localRink = 0;
            _localRinkFrame = -1;
        }

        public static bool TranslatePrefix(UIMinimap __instance, VisualElement element, ref Vector3 worldPosition)
        {
            if (MultiSheetClientSettings.SkipMinimap) return true;

            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (!cfg.EnableMultiRink || cfg.Rinks == null || cfg.Rinks.Count < 2)
                return true;

            if (!PracticeFlowClient.IsOnPracticeServer)
                return true;

            // The controller copies the expanded Level.Bounds onto the minimap at level
            // spawn; keep forcing the vanilla single-rink bounds so the map graphic and
            // the normalization agree again.
            if (_hasVanillaBounds)
                __instance.Bounds = _vanillaBounds;

            int localRink = GetLocalRink(cfg);
            int posRink = NearestRink(cfg, worldPosition);

            if (posRink != localRink)
            {
                element.style.display = DisplayStyle.None;
                return false; // skip vanilla translate for hidden dots
            }

            element.style.display = DisplayStyle.Flex;
            worldPosition -= cfg.Rinks[localRink].Origin; // rink-local, like vanilla rink 1
            return true;
        }

        private static int GetLocalRink(MultiRinkConfig cfg)
        {
            if (_localRinkFrame == Time.frameCount) return _localRink;
            _localRinkFrame = Time.frameCount;

            Vector3? pos = null;
            try
            {
                Player local = MonoBehaviourSingleton<PlayerManager>.Instance?.GetLocalPlayer();
                if (local != null && local.PlayerBody != null)
                    pos = local.PlayerBody.transform.position;
            }
            catch (Exception) { }

            if (pos == null && Camera.main != null)
                pos = Camera.main.transform.position; // spectating / no body yet

            if (pos != null)
                _localRink = NearestRink(cfg, pos.Value);
            // else: keep last known rink

            return _localRink;
        }

        private static int NearestRink(MultiRinkConfig cfg, Vector3 worldPosition)
        {
            return RinkLocator.NearestRink(cfg, worldPosition);
        }
    }
}
