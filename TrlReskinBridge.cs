using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// TRL compatibility — react to TRL's own apply points instead of polling.
    ///
    /// TRL writes ice/glass/skybox/probes only from SetAll, UI callbacks, and presets
    /// (see ToastersReskinLoader SwapperManager / IceSwapper / ArenaSwapper /
    /// GlossSwapper / SkyboxSwapper). We postfix those methods and re-layer MultiSheet
    /// practice overrides after TRL finishes, so we never fight a per-frame loop and
    /// never write ReskinProfile.json.
    ///
    /// Clone materials are not property-copied (that caused magenta clones). Clones
    /// already resolve live materials from rink-1 sources via <see cref="CloneVisualProxy"/>;
    /// we only ask the proxy to refresh when TRL says something changed.
    /// </summary>
    internal static class TrlReskinBridge
    {
        private static readonly string[] PatchTargets =
        {
            "ToasterReskinLoader.swappers.SwapperManager|SetAll",
            "ToasterReskinLoader.swappers.IceSwapper|UpdateIceSmoothness",
            "ToasterReskinLoader.swappers.IceSwapper|SetIceTexture",
            "ToasterReskinLoader.swappers.ArenaSwapper|UpdateGlassAndPillars",
            "ToasterReskinLoader.display.GlossSwapper|ApplyReflectionIntensity",
            "ToasterReskinLoader.swappers.SkyboxSwapper|UpdateSkybox",
        };

        private static Harmony harmony;
        private static bool patchesInstalled;
        private static GameObject clientCloneRoot;
        private static readonly List<MethodBase> patchedMethods = new List<MethodBase>(8);

        internal static void SetHarmony(Harmony owner)
        {
            harmony = owner;
            if (MultiSheetClientSettings.AllowRinkChanges)
                InstallCompatibilityPatches();
        }

        internal static void RegisterClientRoot(GameObject root)
        {
            clientCloneRoot = root;
            LogCloneMaterialHealth("post-clone");
            if (MultiSheetClientSettings.AllowRinkChanges)
                InstallCompatibilityPatches();
            TrlPracticeSmoothnessOverride.Apply();
            // Clones just appeared — pick up whatever TRL already wrote to rink 1.
            // Glare/day-night re-layer waits until ArenaLighting.Apply (IsActive).
            CloneVisualProxy.RefreshMaterials();
        }

        internal static void Clear()
        {
            clientCloneRoot = null;
        }

        /// <summary>
        /// Limit Rink Changes: remove our TRL postfixes so TRL owns the arena again.
        /// Allow Rink Changes: reinstall them.
        /// </summary>
        internal static void SetCompatibilityEnabled(bool enabled)
        {
            if (enabled)
            {
                InstallCompatibilityPatches();
                return;
            }

            UnpatchCompatibility();
        }

        /// <summary>Ask TRL to rebuild from the user's profile (no-op if TRL absent).</summary>
        internal static void RequestTrlRebuild()
        {
            try
            {
                Type type = FindType("ToasterReskinLoader.swappers.SwapperManager");
                if (type == null) return;
                MethodInfo setAll = type.GetMethod("SetAll", BindingFlags.Public | BindingFlags.Static);
                if (setAll == null) return;
                setAll.Invoke(null, null);
                CloneVisualProxy.RefreshMaterials();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] TRL SetAll rebuild skipped: " + ex.Message);
            }
        }

        internal static void TagClone(GameObject clone, string templatePath)
        {
            if (clone == null || string.IsNullOrEmpty(templatePath)) return;
            MultiRinkCloneMeta meta = clone.GetComponent<MultiRinkCloneMeta>() ?? clone.AddComponent<MultiRinkCloneMeta>();
            meta.TemplatePath = templatePath;
        }

        private static void InstallCompatibilityPatches()
        {
            if (patchesInstalled || harmony == null) return;

            int patched = 0;
            for (int i = 0; i < PatchTargets.Length; i++)
            {
                string[] parts = PatchTargets[i].Split('|');
                if (parts.Length != 2) continue;
                if (TryPatch(parts[0], parts[1])) patched++;
            }

            // TRL absent (or older) is fine — MultiSheet still applies on its own build.
            if (patched > 0)
            {
                patchesInstalled = true;
                Debug.Log("[PHLPractice] TRL compatibility: hooked " + patched + " apply point(s).");
            }
        }

        private static void UnpatchCompatibility()
        {
            if (!patchesInstalled || harmony == null)
            {
                patchesInstalled = false;
                patchedMethods.Clear();
                return;
            }

            MethodInfo postfix = typeof(TrlReskinBridge).GetMethod(
                nameof(OnTrlWroteArena), BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < patchedMethods.Count; i++)
            {
                try
                {
                    if (patchedMethods[i] != null && postfix != null)
                        harmony.Unpatch(patchedMethods[i], postfix);
                }
                catch { }
            }
            patchedMethods.Clear();
            patchesInstalled = false;
            Debug.Log("[PHLPractice] TRL compatibility hooks removed (Limit Rink Changes).");
        }

        private static bool TryPatch(string typeName, string methodName)
        {
            try
            {
                Type type = FindType(typeName);
                if (type == null) return false;
                MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                if (method == null) return false;

                harmony.Patch(method, postfix: new HarmonyMethod(typeof(TrlReskinBridge), nameof(OnTrlWroteArena)));
                patchedMethods.Add(method);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] TRL hook " + typeName + "." + methodName + " skipped: " + ex.Message);
                return false;
            }
        }

        /// <summary>Harmony postfix target for every TRL apply point above.</summary>
        public static void OnTrlWroteArena()
        {
            try
            {
                if (!MultiSheetClientSettings.AllowRinkChanges) return;

                // Only re-layer while MultiSheet's practice environment is active.
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
                Debug.LogWarning("[PHLPractice] TRL compatibility re-layer failed: " + ex.Message);
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static void LogCloneMaterialHealth(string stage)
        {
            if (!PracticeLog.Verbose || clientCloneRoot == null) return;

            int renderers = 0;
            int bad = 0;
            foreach (Renderer r in clientCloneRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                renderers++;
                Material[] mats = r.sharedMaterials;
                if (mats == null) continue;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || (mats[i].shader != null && mats[i].shader.name == "Hidden/InternalErrorShader"))
                        bad++;
                }
            }
            PracticeLog.Info("[PHLPractice] Clone material health (" + stage + "): renderers=" + renderers + " badSlots=" + bad);
        }
    }
}
