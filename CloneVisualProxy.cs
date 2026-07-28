using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Draws combined-mesh submesh slices for rink clones via Graphics.DrawMesh.
    ///
    /// Why GPU draws: the arena's static-batched combined meshes are NOT CPU-readable
    /// in player builds — GetTriangles()/vertices return empty arrays, so any mesh
    /// surgery silently produces garbage. GPU submesh draws need no vertex access, and
    /// batched vertices are baked in world space, so a pure translation matrix places
    /// geometry exactly at the clone offset.
    ///
    /// Lighting: flat SH ambient via <see cref="AmbientProbeBlock"/> plus per-rink
    /// fill lights from <see cref="VanillaRinkCloner"/>.
    ///
    /// Perf: draws are bucketed per rink; just-my-rink submits only the active bucket;
    /// rink 1 (primary) never has proxy entries and early-outs; no-focus does not fall
    /// back to drawing every sheet.
    /// </summary>
    internal sealed class CloneVisualProxy : MonoBehaviour
    {
        private struct DrawEntry
        {
            public MeshRenderer Source;
            public int Slot;
            public Mesh Mesh;
            public int SubMesh;
            public Matrix4x4 Matrix;
            public Material Resolved;
            public float OriginX;
            public float OriginZ;
        }

        private struct MirrorPair
        {
            public Renderer Source;
            public Renderer Clone;
            public float OriginX;
            public float OriginZ;
            public bool WantEnabled;
        }

        private struct RinkKey : IEquatable<RinkKey>
        {
            public int X;
            public int Z;

            public static RinkKey From(float x, float z)
            {
                return new RinkKey
                {
                    X = Mathf.RoundToInt(x * 10f),
                    Z = Mathf.RoundToInt(z * 10f)
                };
            }

            public bool Equals(RinkKey other) => X == other.X && Z == other.Z;
            public override bool Equals(object obj) => obj is RinkKey other && Equals(other);
            public override int GetHashCode() => (X * 397) ^ Z;
        }

        private static CloneVisualProxy _instance;
        private readonly Dictionary<RinkKey, List<DrawEntry>> _buckets =
            new Dictionary<RinkKey, List<DrawEntry>>();
        private readonly List<MirrorPair> _mirrors = new List<MirrorPair>();
        private static bool _renderHookInstalled;

        private float _nextMirrorCull;
        private float _lastCullFx = float.NaN;
        private float _lastCullFz = float.NaN;
        private bool _lastCullShowAll;
        private float _lastLightFx = float.NaN;
        private float _lastLightFz = float.NaN;
        private bool _lastLightShowAll;
        private int _lastSubmitted;
        private int _lastMirrorOn;

        private const float MirrorCullSeconds = 0.25f;

        internal static int EntryCount
        {
            get
            {
                if (_instance == null) return 0;
                int n = 0;
                foreach (var kv in _instance._buckets) n += kv.Value.Count;
                return n;
            }
        }

        internal static int MirrorCount => _instance != null ? _instance._mirrors.Count : 0;
        internal static int LastSubmittedCount => _instance != null ? _instance._lastSubmitted : 0;
        internal static int LastMirrorOnCount => _instance != null ? _instance._lastMirrorOn : 0;

        internal static void Clear()
        {
            if (_instance == null) return;
            _instance._buckets.Clear();
            _instance._mirrors.Clear();
            _instance._lastSubmitted = 0;
            _instance._lastMirrorOn = 0;
            RinkRenderFocus.Clear();
        }

        internal static void RefreshMaterials()
        {
            if (_instance == null) return;
            _instance.Refresh();
        }

        internal static void DestroyInstance()
        {
            RemoveRenderHook();
            if (_instance == null) return;
            Destroy(_instance.gameObject);
            _instance = null;
            RinkRenderFocus.Clear();
        }

        /// <summary>One-shot status line after the render-scope toggle flips.</summary>
        internal static void LogRenderScopeStatus()
        {
            bool renderAll = RinkRenderFocus.RenderAll;
            bool haveFocus = RinkRenderFocus.TryGetGameplayFocus(out float fx, out float fz);
            string focus = haveFocus
                ? ("(" + fx.ToString("F0") + "," + fz.ToString("F0") + ")")
                : "none";
            Debug.Log("[PHLPractice] Render scope status: renderAll=" + renderAll +
                      " needsAllSheet=" + RinkPreview.NeedsAllSheetLighting +
                      " focus=" + focus +
                      " drawEntries=" + EntryCount +
                      " lastSubmitted=" + LastSubmittedCount +
                      " mirrorsOn=" + LastMirrorOnCount + "/" + MirrorCount);
        }

        internal static void AddDraw(MeshRenderer source, int slot, Mesh mesh, int subMesh, Matrix4x4 matrix)
        {
            if (source == null || mesh == null) return;
            if (subMesh < 0 || subMesh >= mesh.subMeshCount) return;

            EnsureInstance();

            var entry = new DrawEntry
            {
                Source = source,
                Slot = slot,
                Mesh = mesh,
                SubMesh = subMesh,
                Matrix = matrix,
                OriginX = matrix.m03,
                OriginZ = matrix.m23,
            };
            ResolveMaterial(ref entry);

            RinkKey key = RinkKey.From(entry.OriginX, entry.OriginZ);
            if (!_instance._buckets.TryGetValue(key, out List<DrawEntry> list))
            {
                list = new List<DrawEntry>(64);
                _instance._buckets[key] = list;
            }
            list.Add(entry);
        }

        internal static void AddMirror(Renderer source, Renderer clone)
        {
            if (source == null || clone == null || source == clone) return;
            EnsureInstance();

            Vector3 p = clone.transform.position;
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            float ox = p.x, oz = p.z;
            if (cfg?.Rinks != null && cfg.Rinks.Count > 0)
            {
                int idx = RinkLocator.NearestRink(cfg, p);
                if (idx >= 0 && idx < cfg.Rinks.Count && cfg.Rinks[idx] != null)
                {
                    Vector3 o = cfg.Rinks[idx].Origin;
                    ox = o.x;
                    oz = o.z;
                }
            }

            _instance._mirrors.Add(new MirrorPair
            {
                Source = source,
                Clone = clone,
                OriginX = ox,
                OriginZ = oz,
                WantEnabled = true
            });
        }

        private static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("PHL_CloneVisualProxy");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<CloneVisualProxy>();
            InstallRenderHook();
        }

        private static void InstallRenderHook()
        {
            if (_renderHookInstalled) return;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            _renderHookInstalled = true;
        }

        private static void RemoveRenderHook()
        {
            if (!_renderHookInstalled) return;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            _renderHookInstalled = false;
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_instance == null || camera == null) return;
            if (!RinkPreview.IsPreviewCamera(camera)) return;
            if (!RinkPreview.TryGetPreviewOrigin(camera, out float ox, out float oz)) return;
            _instance.DrawBucketForOrigin(camera, ox, oz);
        }

        private static void ResolveMaterial(ref DrawEntry entry)
        {
            if (entry.Source == null) return;

            if (entry.Slot == 0)
            {
                Material m = entry.Source.sharedMaterial;
                if (m != null) entry.Resolved = m;
                return;
            }

            Material[] mats = entry.Source.sharedMaterials;
            if (mats != null && entry.Slot < mats.Length && mats[entry.Slot] != null)
                entry.Resolved = mats[entry.Slot];
        }

        private void LateUpdate()
        {
            _lastSubmitted = 0;
            MaybeCullMirrors();

            bool renderAll = RinkRenderFocus.RenderAll;
            float fx = 0f, fz = 0f;
            bool focused = !renderAll && RinkRenderFocus.TryGetGameplayFocus(out fx, out fz);

            // Clone fill lights must follow teleports even when there are no mirrors.
            bool lightFocusChanged = renderAll != _lastLightShowAll
                || (focused && (fx != _lastLightFx || fz != _lastLightFz))
                || (!focused && !renderAll && !float.IsNaN(_lastLightFx));
            if (lightFocusChanged)
            {
                _lastLightShowAll = renderAll;
                _lastLightFx = focused ? fx : float.NaN;
                _lastLightFz = focused ? fz : float.NaN;
                ArenaLighting.RefreshRinkLightCulling();
            }

            if (_buckets.Count == 0) return;

            if (renderAll)
            {
                DrawAllBuckets(RinkRenderFocus.FindGameplayCamera());
                return;
            }

            // No body and no last focus: submit nothing (never fall back to all sheets).
            if (!focused) return;

            // Proxy entries only exist for offset clones — rink 1 uses vanilla geometry.
            if (RinkRenderFocus.IsPrimaryOrigin(fx, fz))
                return;

            DrawBucketForOrigin(RinkRenderFocus.FindGameplayCamera(), fx, fz);
        }

        private void MaybeCullMirrors()
        {
            if (_mirrors.Count == 0) return;

            bool renderAll = RinkRenderFocus.RenderAll;
            float fx = 0f, fz = 0f;
            bool focused = !renderAll && RinkRenderFocus.TryGetGameplayFocus(out fx, out fz);
            float lx = 0f, lz = 0f;
            bool live = !renderAll && RinkPreview.TryGetLivePreviewOrigin(out lx, out lz);

            bool focusChanged = renderAll != _lastCullShowAll
                || (focused && (fx != _lastCullFx || fz != _lastCullFz))
                || Time.unscaledTime >= _nextMirrorCull;

            if (!focusChanged) return;

            _nextMirrorCull = Time.unscaledTime + MirrorCullSeconds;
            _lastCullShowAll = renderAll;
            _lastCullFx = fx;
            _lastCullFz = fz;

            int onCount = 0;
            for (int i = 0; i < _mirrors.Count; i++)
            {
                MirrorPair m = _mirrors[i];
                if (m.Clone == null) continue;
                bool on = renderAll
                    || (focused && ArenaLighting.SameRink(m.OriginX, m.OriginZ, fx, fz))
                    || (live && ArenaLighting.SameRink(m.OriginX, m.OriginZ, lx, lz))
                    || RinkPreview.IsOriginInCapture(m.OriginX, m.OriginZ);
                // No focus yet: leave mirrors off in just-my-rink (MOTD capture uses preview cams).
                if (!renderAll && !focused && !live) on = false;
                if (m.WantEnabled != on || m.Clone.enabled != on)
                {
                    m.WantEnabled = on;
                    if (m.Clone.enabled != on) m.Clone.enabled = on;
                    _mirrors[i] = m;
                }
                if (on) onCount++;
            }
            _lastMirrorOn = onCount;
        }

        private void DrawAllBuckets(Camera camera)
        {
            foreach (var kv in _buckets)
                SubmitList(kv.Value, camera);
        }

        private void DrawBucketForOrigin(Camera camera, float originX, float originZ)
        {
            RinkKey key = RinkKey.From(originX, originZ);
            if (_buckets.TryGetValue(key, out List<DrawEntry> list))
            {
                SubmitList(list, camera);
                return;
            }

            // Float key mismatch fallback — rare; scan bucket origins with SameRink.
            foreach (var kv in _buckets)
            {
                if (kv.Value.Count == 0) continue;
                DrawEntry sample = kv.Value[0];
                if (!ArenaLighting.SameRink(sample.OriginX, sample.OriginZ, originX, originZ))
                    continue;
                SubmitList(kv.Value, camera);
                return;
            }
        }

        private void SubmitList(List<DrawEntry> list, Camera camera)
        {
            if (list == null || list.Count == 0) return;

            MaterialPropertyBlock block = AmbientProbeBlock.Get();
            for (int i = 0; i < list.Count; i++)
            {
                DrawEntry e = list[i];
                if (e.Mesh == null || e.Resolved == null) continue;
                Graphics.DrawMesh(
                    e.Mesh, e.Matrix, e.Resolved, 0, camera, e.SubMesh, block,
                    ShadowCastingMode.Off, false, null, LightProbeUsage.Off, null);
                _lastSubmitted++;
            }
        }

        private void Refresh()
        {
            foreach (var kv in _buckets)
            {
                List<DrawEntry> list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    DrawEntry e = list[i];
                    ResolveMaterial(ref e);
                    list[i] = e;
                }
            }

            for (int i = 0; i < _mirrors.Count; i++)
            {
                MirrorPair m = _mirrors[i];
                if (m.Source == null || m.Clone == null) continue;

                Material[] src = m.Source.sharedMaterials;
                Material[] dst = m.Clone.sharedMaterials;
                if (src == null || dst == null || src.Length != dst.Length) continue;
                bool same = true;
                for (int k = 0; k < src.Length; k++)
                {
                    if (!ReferenceEquals(src[k], dst[k])) { same = false; break; }
                }
                if (!same) m.Clone.sharedMaterials = src;
            }
        }
    }
}
