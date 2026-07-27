using System;
using System.Reflection;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Practice sheets mirror the hangar cubemap when ice/glass smoothness is high.
    /// Force TRL's in-memory profile and rink-1 materials to 0 on practice — never
    /// persist ReskinProfile.json (same idea as <see cref="CptThinSkaterOverride"/>).
    /// </summary>
    internal static class TrlPracticeSmoothnessOverride
    {
        private const string ProfileManagerTypeName = "ToasterReskinLoader.ReskinProfileManager";

        internal static void Apply()
        {
            if (!ShouldApply()) return;

            bool profileChanged = ForceProfileZero();
            bool iceChanged = ApplyIceTopSmoothness(0f);
            int glassChanged = ApplyGlassSmoothness(0f);

            if (profileChanged || iceChanged || glassChanged > 0)
            {
                Debug.Log("[PHLPractice] Practice smoothness override — ice/glass _Smoothness=0" +
                          " (profile=" + (profileChanged ? "updated" : "ok") +
                          ", iceMat=" + (iceChanged ? "updated" : "ok") +
                          ", glassRenderers=" + glassChanged + ").");
            }
        }

        private static bool ShouldApply()
        {
            if (!MultiSheetClientSettings.AllowRinkChanges) return false;
            try
            {
                if (PracticeFlow.ServerActive) return true;
                return PracticeFlowClient.IsOnPracticeServer;
            }
            catch { return false; }
        }

        private static bool ForceProfileZero()
        {
            object profile = TryGetTrlProfile();
            if (profile == null) return false;

            bool changed = false;
            changed |= TrySetFloatField(profile, "iceSmoothness", 0f);
            changed |= TrySetFloatField(profile, "glassSmoothness", 0f);
            return changed;
        }

        private static bool ApplyIceTopSmoothness(float value)
        {
            GameObject iceTop = GameObject.Find("Ice Top");
            if (iceTop == null) return false;

            Renderer[] renderers = iceTop.GetComponentsInChildren<Renderer>(true);
            bool changed = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                changed |= SetMaterialSmoothness(renderer.material, value);
                changed |= SetMaterialSmoothness(renderer.sharedMaterial, value);
            }
            return changed;
        }

        private static int ApplyGlassSmoothness(float value)
        {
            int changed = 0;
            MeshRenderer[] renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer mr = renderers[i];
                if (mr == null || !UsesMaterialNamed(mr, "Glass")) continue;
                if (SetMaterialSmoothness(mr.material, value)) changed++;
            }
            return changed;
        }

        private static bool UsesMaterialNamed(Renderer renderer, string materialName)
        {
            Material[] mats = renderer.sharedMaterials;
            if (mats == null) return false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material mat = mats[i];
                if (mat == null) continue;
                if (MaterialNameMatches(mat.name, materialName)) return true;
            }
            return false;
        }

        private static bool MaterialNameMatches(string actual, string target)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(target)) return false;
            int idx = actual.IndexOf(" (Instance)", StringComparison.Ordinal);
            if (idx >= 0) actual = actual.Substring(0, idx);
            return actual.Trim().Equals(target, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SetMaterialSmoothness(Material mat, float value)
        {
            if (mat == null || !mat.HasProperty("_Smoothness")) return false;
            if (Mathf.Approximately(mat.GetFloat("_Smoothness"), value)) return false;
            mat.SetFloat("_Smoothness", value);
            return true;
        }

        private static object TryGetTrlProfile()
        {
            Type manager = FindType(ProfileManagerTypeName);
            if (manager == null) return null;

            PropertyInfo prop = manager.GetProperty("currentProfile", BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null);
        }

        private static bool TrySetFloatField(object target, string fieldName, float value)
        {
            if (target == null) return false;
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(float)) return false;
            float current = (float)field.GetValue(target);
            if (Mathf.Approximately(current, value)) return false;
            field.SetValue(target, value);
            return true;
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
    }
}
