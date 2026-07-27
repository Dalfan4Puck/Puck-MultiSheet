using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Shared "which sheet should be drawn/lit" policy for DrawMesh proxies, mirror
    /// renderers, and clone fill lights. One definition so the render-scope toggle
    /// cannot disagree with itself.
    /// </summary>
    internal static class RinkRenderFocus
    {
        private static float _lastX;
        private static float _lastZ;
        private static bool _haveLast;

        internal static bool RenderAll =>
            MultiSheetClientSettings.RenderAllRinks || RinkPreview.NeedsAllSheetLighting;

        /// <summary>Primary (rink 1) origin from the active layout — proxy draws never target this.</summary>
        internal static bool TryGetPrimaryOrigin(out float x, out float z)
        {
            x = 0f;
            z = 0f;
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0 || cfg.Rinks[0] == null)
                return true; // vanilla origin
            Vector3 o = cfg.Rinks[0].Origin;
            x = o.x;
            z = o.z;
            return true;
        }

        internal static bool IsPrimaryOrigin(float x, float z)
        {
            TryGetPrimaryOrigin(out float px, out float pz);
            return ArenaLighting.SameRink(x, z, px, pz);
        }

        /// <summary>
        /// Gameplay focus for just-my-rink mode. Prefers the local body; otherwise the
        /// last known focus. Never invents "all sheets" when the body is missing.
        /// </summary>
        internal static bool TryGetGameplayFocus(out float x, out float z)
        {
            x = 0f;
            z = 0f;
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0) return false;

            Vector3? pos = RinkLocator.LocalPlayerBodyPosition();
            if (pos.HasValue)
            {
                int idx = RinkLocator.NearestRink(cfg, pos.Value);
                if (idx >= 0 && idx < cfg.Rinks.Count && cfg.Rinks[idx] != null)
                {
                    Vector3 o = cfg.Rinks[idx].Origin;
                    x = o.x;
                    z = o.z;
                    _lastX = x;
                    _lastZ = z;
                    _haveLast = true;
                    return true;
                }
            }

            if (_haveLast)
            {
                x = _lastX;
                z = _lastZ;
                return true;
            }

            return false;
        }

        internal static void Clear()
        {
            _haveLast = false;
            _lastX = 0f;
            _lastZ = 0f;
        }

        /// <summary>Gameplay camera for DrawMesh so preview cams are not amplified.</summary>
        internal static Camera FindGameplayCamera()
        {
            Camera main = Camera.main;
            if (main != null && !RinkPreview.IsPreviewCamera(main)) return main;

            Camera[] cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c == null || !c.isActiveAndEnabled) continue;
                if (RinkPreview.IsPreviewCamera(c)) continue;
                if (c.targetTexture != null) continue;
                return c;
            }
            return null;
        }
    }
}
