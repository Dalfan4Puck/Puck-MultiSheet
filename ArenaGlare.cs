using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Practice-server-only glare reduction. Snapshot → reduce → restore; never writes
    /// TRL profiles or base-game assets.
    ///
    /// Only touches ReflectionProbe intensity (helmets/sticks/glass cubemap) and Ice Top
    /// smoothness. Intentionally does <b>not</b> scan every MeshRenderer for Glass or call
    /// <c>renderer.material</c> — that instantiated materials across nine sheets and blew
    /// SRP batching / competitive FPS. Probe intensity is the lever TRL documents for
    /// arena-cubemap glare; ice smoothness covers the ice bloom.
    /// </summary>
    internal static class ArenaGlare
    {
        private const float ProbeIntensityMul = 0.28f;
        private const float IceSmoothnessMul = 0.32f;

        private static readonly List<ProbeSnapshot> probes = new List<ProbeSnapshot>(8);
        private static readonly List<FloatPropSnapshot> floats = new List<FloatPropSnapshot>(4);
        private static readonly HashSet<int> knownProbeIds = new HashSet<int>();
        private static readonly HashSet<int> knownFloatKeys = new HashSet<int>();

        private static bool active;

        internal static bool IsActive => active;

        internal static void Apply()
        {
            active = true;
            Discover();
            Reassert();
        }

        /// <summary>
        /// TRL just overwrote ice/probes. Drop captures without restoring (those values
        /// are gone) and re-snapshot from the post-TRL world state.
        /// </summary>
        internal static void ReapplyAfterTrl()
        {
            if (!active) return;
            probes.Clear();
            knownProbeIds.Clear();
            floats.Clear();
            knownFloatKeys.Clear();
            Discover();
            Reassert();
        }

        internal static void Restore()
        {
            if (!active && probes.Count == 0 && floats.Count == 0) return;
            active = false;

            for (int i = 0; i < probes.Count; i++)
            {
                ReflectionProbe probe = probes[i].Probe;
                if (probe != null) probe.intensity = probes[i].OriginalIntensity;
            }
            probes.Clear();
            knownProbeIds.Clear();

            for (int i = 0; i < floats.Count; i++)
            {
                Material mat = floats[i].Material;
                if (mat == null) continue;
                if (mat.HasProperty(floats[i].Property))
                    mat.SetFloat(floats[i].Property, floats[i].Original);
            }
            floats.Clear();
            knownFloatKeys.Clear();
        }

        private static void Discover()
        {
            DiscoverProbes();
            DiscoverIceTop();
        }

        private static void DiscoverProbes()
        {
            ReflectionProbe[] found = UnityEngine.Object.FindObjectsByType<ReflectionProbe>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < found.Length; i++)
            {
                ReflectionProbe probe = found[i];
                if (probe == null) continue;
                int id = probe.GetInstanceID();
                if (!knownProbeIds.Add(id)) continue;

                probes.Add(new ProbeSnapshot
                {
                    Probe = probe,
                    OriginalIntensity = probe.intensity,
                    TargetIntensity = probe.intensity * ProbeIntensityMul
                });
            }
        }

        private static void DiscoverIceTop()
        {
            GameObject go = GameObject.Find("Ice Top");
            if (go == null) return;
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                // Prefer sharedMaterial so we do not spawn a new instance; TRL may already
                // have instanced via .material — either way we snapshot and restore.
                Material mat = renderer.sharedMaterial;
                if (mat == null) continue;
                CaptureFloat(mat, "_Smoothness", IceSmoothnessMul);
                if (mat.HasProperty("_Glossiness"))
                    CaptureFloat(mat, "_Glossiness", IceSmoothnessMul);
            }
        }

        private static void CaptureFloat(Material mat, string property, float mul)
        {
            if (mat == null || !mat.HasProperty(property)) return;

            int key = Hash(mat.GetInstanceID(), property);
            if (!knownFloatKeys.Add(key)) return;

            float original = mat.GetFloat(property);
            floats.Add(new FloatPropSnapshot
            {
                Material = mat,
                Property = property,
                Original = original,
                Target = original * mul
            });
        }

        private static void Reassert()
        {
            for (int i = 0; i < probes.Count; i++)
            {
                ReflectionProbe probe = probes[i].Probe;
                if (probe == null) continue;
                float target = probes[i].TargetIntensity;
                if (!Mathf.Approximately(probe.intensity, target))
                    probe.intensity = target;
            }

            for (int i = 0; i < floats.Count; i++)
            {
                Material mat = floats[i].Material;
                if (mat == null || !mat.HasProperty(floats[i].Property)) continue;
                float target = floats[i].Target;
                if (!Mathf.Approximately(mat.GetFloat(floats[i].Property), target))
                    mat.SetFloat(floats[i].Property, target);
            }
        }

        private static int Hash(int materialId, string property)
        {
            unchecked
            {
                return (materialId * 397) ^ (property != null ? property.GetHashCode() : 0);
            }
        }

        private struct ProbeSnapshot
        {
            public ReflectionProbe Probe;
            public float OriginalIntensity;
            public float TargetIntensity;
        }

        private struct FloatPropSnapshot
        {
            public Material Material;
            public string Property;
            public float Original;
            public float Target;
        }
    }
}
