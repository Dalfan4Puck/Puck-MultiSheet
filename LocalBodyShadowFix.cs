using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace PHLPracticeModPack
{
    /// <summary>
    /// First-person enables PlayerCamera, which calls <see cref="MeshRendererHider.HideMeshRenderers"/>
    /// on the local body (renderer.enabled = false). That removes the body shadow on ice while the
    /// stick — not in the hider list — still casts one. Re-enable body renderers as ShadowsOnly.
    /// </summary>
    internal static class LocalBodyShadowFix
    {
        internal static void ApplyShadowOnly(MeshRendererHider hider)
        {
            if (hider == null || !IsLocalOwner(hider)) return;

            List<MeshRenderer> list = EnsureMeshRenderers(hider);
            for (int i = 0; i < list.Count; i++)
            {
                MeshRenderer mr = list[i];
                if (mr == null) continue;
                mr.enabled = true;
                mr.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
        }

        internal static void ApplyFullCast(MeshRendererHider hider)
        {
            if (hider == null || !IsLocalOwner(hider)) return;

            List<MeshRenderer> list = EnsureMeshRenderers(hider);
            for (int i = 0; i < list.Count; i++)
            {
                MeshRenderer mr = list[i];
                if (mr == null) continue;
                mr.enabled = true;
                mr.shadowCastingMode = ShadowCastingMode.On;
            }
        }

        private static bool IsLocalOwner(MeshRendererHider hider)
        {
            PlayerBody body = hider.GetComponent<PlayerBody>();
            return body != null && body.IsOwner;
        }

        /// <summary>Mirror MeshRendererHider.Awake if Hide runs before the list is built.</summary>
        private static List<MeshRenderer> EnsureMeshRenderers(MeshRendererHider hider)
        {
            List<MeshRenderer> list = hider.meshRenderers;
            if (list != null && list.Count > 0) return list;
            if (!hider.useChildrenMeshRenderers) return list ?? new List<MeshRenderer>(0);

            list = new List<MeshRenderer>(hider.GetComponentsInChildren<MeshRenderer>(includeInactive: true));
            List<MeshRenderer> blacklist = hider.meshRendererBlacklist;
            if (blacklist != null && blacklist.Count > 0)
                list.RemoveAll(blacklist.Contains);
            hider.meshRenderers = list;
            return list;
        }
    }

    [HarmonyPatch(typeof(MeshRendererHider), nameof(MeshRendererHider.HideMeshRenderers))]
    internal static class LocalBodyShadowHidePatch
    {
        [HarmonyPostfix]
        private static void Postfix(MeshRendererHider __instance) =>
            LocalBodyShadowFix.ApplyShadowOnly(__instance);
    }

    /// <summary>Preview/MOTD capture needs the full body visible, not shadow-only.</summary>
    [HarmonyPatch(typeof(MeshRendererHider), nameof(MeshRendererHider.ShowMeshRenderers))]
    internal static class LocalBodyShadowShowPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MeshRendererHider __instance) =>
            LocalBodyShadowFix.ApplyFullCast(__instance);
    }
}
