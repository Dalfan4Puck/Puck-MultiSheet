using System;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Single owner for post-TRL material and environment state.
    /// TRL writes base arena materials; MultiSheet re-layers once through here.
    /// </summary>
    internal static class PresentationPipeline
    {
        internal static void ApplyAfterTrl()
        {
            if (!MultiSheetClientSettings.AllowRinkChanges) return;

            try
            {
                if (ArenaGlare.IsActive)
                    ArenaGlare.ReapplyAfterTrl();

                ArenaLighting.SyncReflectionBaselineFromScene();

                if (ArenaLighting.IsActive)
                    ArenaLighting.ApplyEnvironment();

                TrlPracticeSmoothnessOverride.Apply();
                CloneVisualProxy.RefreshMaterials();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Presentation pipeline after TRL failed: " + ex.Message);
            }
        }
    }
}
