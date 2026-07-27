using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace PHLPracticeModPack
{
    internal enum RinkCloneRole
    {
        Server,
        Client,
    }

    /// <summary>
    /// Duplicates rink templates at each configured origin.
    /// Cloning uses full-template Instantiate (preserves colliders/materials).
    /// ArenaRenderCatalog is scan/dump only — see /multirink-dump.
    /// </summary>
    internal static class VanillaRinkCloner
    {
        // Scoreboards are deliberately absent — the hanging jumbotrons look bad
        // duplicated across the open multi-sheet layout.
        private static readonly string[] DefaultTemplates =
        {
            "Rink",
            "Goal Blue",
            "Goal Red",
        };

        private const float DefaultIceSurfaceY = 0.03f;
        private const float DefaultPlayerHeightAboveIce = 0.9f;

        private static float cachedIceSurfaceY = float.NaN;
        private static int combinedMeshRebakeCount;
        private static List<Light> cachedArenaLightSources;
        private static bool cachedUseSyntheticFill;
        private static bool arenaLightSourcesCached;

        internal static float IceSurfaceY =>
            float.IsNaN(cachedIceSurfaceY) ? DefaultIceSurfaceY : cachedIceSurfaceY;

        internal static float PlayerSpawnY => IceSurfaceY + DefaultPlayerHeightAboveIce;

        internal static void ResetCache()
        {
            cachedIceSurfaceY = float.NaN;
            combinedMeshRebakeCount = 0;
            cachedArenaLightSources = null;
            arenaLightSourcesCached = false;
            cachedUseSyntheticFill = false;
        }

        internal static GameObject SpawnMultiRinkLayout(List<RinkSlot> rinks, MultiRinkConfig cfg, RinkCloneRole role)
        {
            if (rinks == null || rinks.Count == 0) return null;

            Level level = UnityEngine.Object.FindFirstObjectByType<Level>();
            if (level == null)
            {
                Debug.LogError("[PHLPractice] VanillaRinkCloner: Level not found.");
                return null;
            }

            Transform levelRoot = level.transform;
            CacheIceSurfaceY(levelRoot);

            string[] templates = cfg?.CloneTemplates != null && cfg.CloneTemplates.Count > 0
                ? cfg.CloneTemplates.ToArray()
                : DefaultTemplates;

            ScanAndDumpCatalog(levelRoot, templates, role);

            var templateMap = FindTemplates(levelRoot, templates);
            if (templateMap.Count == 0)
            {
                Debug.LogError("[PHLPractice] VanillaRinkCloner: no template objects found under Level.");
                return null;
            }

            if (PracticeLog.Verbose && role == RinkCloneRole.Client &&
                templateMap.TryGetValue("Rink", out GameObject rinkTemplate) && rinkTemplate != null)
                LogTemplateInventory(rinkTemplate.transform, "Rink template");

            string rootName = role == RinkCloneRole.Server
                ? "PHL_VanillaMultiRink_Server"
                : "PHL_VanillaMultiRink_Client";
            var root = new GameObject(rootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            RinkSlot primary = rinks[0];
            Vector3 primaryOrigin = primary?.Origin ?? Vector3.zero;

            if (cfg != null && cfg.HideHangar && role == RinkCloneRole.Server)
                SetActiveByName(levelRoot, "Hangar", false);

            int iceLayer = LayerMask.NameToLayer("Ice");
            int cloneCount = 0;
            combinedMeshRebakeCount = 0;

            if (role == RinkCloneRole.Client)
            {
                CloneVisualProxy.Clear();
                ArenaLighting.ClearRinkLights();
            }

            for (int i = 0; i < rinks.Count; i++)
            {
                RinkSlot slot = rinks[i];
                if (slot == null) continue;

                Vector3 offset = slot.Origin - primaryOrigin;
                if (i == 0 && offset.sqrMagnitude < 0.01f)
                {
                    PracticeLog.Info("[PHLPractice] Rink slot '" + slot.Id + "' uses original level geometry at " + slot.Origin);
                    continue;
                }

                foreach (KeyValuePair<string, GameObject> kv in templateMap)
                {
                    GameObject src = kv.Value;
                    if (src == null) continue;

                    bool isRink = string.Equals(kv.Key, "Rink", StringComparison.OrdinalIgnoreCase);
                    GameObject clone = BuildOffsetHierarchyClone(
                        src, offset, root.transform, SanitizeCloneName(kv.Key, slot.Id),
                        role, iceLayer, isRink, kv.Key);
                    if (clone == null) continue;

                    cloneCount++;
                    if (PracticeLog.Verbose)
                        PracticeLog.Info("[PHLPractice] Cloned (" + role + ") " + kv.Key + " → " + clone.name +
                                  " offset=" + offset);
                }
            }

            Debug.Log("[PHLPractice] Vanilla multi-rink " + role + " layout ready (" + cloneCount +
                      " clones, iceY=" + IceSurfaceY.ToString("F3") +
                      ", meshRebakes=" + combinedMeshRebakeCount + ").");

            if (role == RinkCloneRole.Client)
            {
                // DrawMesh clones cannot carry lightmaps. Always use the offset fill-light
                // rig + SH ambient. An earlier lightmap-stamp attempt skipped fills and
                // left boards pitch-black on rinks 2+.
                CloneRinkLights(levelRoot, root.transform, rinks, primaryOrigin);
                if (PracticeLog.Verbose) LogCloneGeometryHealth(root, rinks, primaryOrigin);
                TrlReskinBridge.RegisterClientRoot(root);
                if (PracticeLog.Verbose)
                {
                    try
                    {
                        string healthPath = CloneDiagnostics.WriteCloneHealthReport("autoload");
                        PracticeLog.Info("[PHLPractice] Clone health JSON: " + healthPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[PHLPractice] Clone health report failed: " + ex.Message);
                    }
                }
            }

            return root;
        }

        private static void ScanAndDumpCatalog(Transform levelRoot, string[] templates, RinkCloneRole role)
        {
            // Diagnostic only: hierarchy scan + JSON dump on every level load is wasted
            // work in normal operation. /multirink-dump still scans on demand.
            if (!PracticeLog.Verbose) return;
            try
            {
                string roleLabel = role == RinkCloneRole.Server ? "Server" : "Client";
                ArenaRenderCatalog catalog = ArenaRenderCatalog.Scan(levelRoot, roleLabel, templates);
                catalog.LogSummary("pre-clone");
                string catalogPath = catalog.WriteJsonFile("autoload");
                PracticeLog.Info("[PHLPractice] Arena catalog JSON: " + catalogPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Arena catalog scan/dump failed (clone continues): " + ex.Message);
            }
        }

        /// <summary>
        /// Instantiate the full template hierarchy once at the offset (preserves collider parent/child relationships).
        /// </summary>
        private static GameObject BuildOffsetHierarchyClone(
            GameObject source, Vector3 offset, Transform parent, string name,
            RinkCloneRole role, int iceLayer, bool addIceCollider, string templatePath)
        {
            Vector3 worldPos = source.transform.position + offset;
            GameObject clone = UnityEngine.Object.Instantiate(source, worldPos, source.transform.rotation, parent);
            clone.name = name;
            clone.SetActive(true);

            TrlReskinBridge.TagClone(clone, templatePath);

            // Two full hierarchy walks purely to fill a log line — the concatenation and
            // the arrays are built even when PracticeLog swallows the message.
            if (PracticeLog.Verbose)
            {
                int rendererCount = clone.GetComponentsInChildren<Renderer>(true).Length;
                int colliderCount = clone.GetComponentsInChildren<Collider>(true).Length;
                PracticeLog.Info("[PHLPractice] Full template clone '" + name + "' (" + role + ") renderers=" + rendererCount +
                          " colliders=" + colliderCount);
            }

            if (role == RinkCloneRole.Server)
            {
                if (addIceCollider)
                    AddIceSurfaceCollider(parent, name, offset, iceLayer);
                PrepareServerClone(clone, iceLayer);
            }
            else
            {
                // Client-only visuals raycast down onto the ice: the puck elevation
                // indicator, goalie leg pads, skate grounding and stick-blade placement
                // all run on every client with no server guard. Rink 1 keeps the level's
                // own colliders, so without an equivalent surface here those effects
                // silently switch off on the offset sheets. A host already has the server
                // root's boxes in the same physics scene, so it must not stack a second.
                if (addIceCollider && !IsLocalServer())
                    AddIceSurfaceCollider(parent, name, offset, iceLayer);
                FixClientCombinedMeshes(clone, source, offset, worldPos.z);
                PrepareClientClone(clone);
            }

            return clone;
        }

        private static bool IsLocalServer()
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm != null && nm.IsServer;
        }

        private static void PrepareClientClone(GameObject clone)
        {
            foreach (Collider c in clone.GetComponentsInChildren<Collider>(true))
                if (c != null) UnityEngine.Object.Destroy(c);

            foreach (Renderer r in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;

                // Collider-visualization meshes have no material; force-enabling them
                // drew a black slab at ice height on the clones. Keep them off.
                if (r.sharedMaterial == null)
                {
                    r.enabled = false;
                    continue;
                }

                // Clone glass/nets across eight sheets must not cast/receive shadows —
                // that alone was a large share of the MultiSheet FPS hit.
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                // Offset rinks sit far from the baked probe volume around rink 1 — sampling
                // it produces muddy/dark lighting. Realtime cloned arena lights + the
                // global sun handle illumination instead.
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
        }

        /// <summary>
        /// Combined meshes are not CPU-readable in player builds (GetTriangles/vertices
        /// return empty — mesh surgery silently fails). Instead: disable the clone's
        /// combined-mesh renderers and register GPU submesh draws via CloneVisualProxy.
        /// Static-batched vertices are baked in world space, so the draw matrix is a
        /// pure translation by the rink offset.
        /// </summary>
        private static void FixClientCombinedMeshes(
            GameObject clone, GameObject source, Vector3 worldOffset, float expectedCenterZ)
        {
            if (clone == null || source == null || worldOffset.sqrMagnitude < 0.01f) return;

            var sourceByPath = new Dictionary<string, Renderer>(StringComparer.OrdinalIgnoreCase);
            Transform sourceRoot = source.transform;
            Renderer[] sourceRs = source.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < sourceRs.Length; i++)
            {
                Renderer r = sourceRs[i];
                if (r == null) continue;
                string path = GetRelativePath(sourceRoot, r.transform);
                if (!string.IsNullOrEmpty(path))
                    sourceByPath[path] = r;
            }

            Matrix4x4 translate = Matrix4x4.Translate(worldOffset);

            Transform cloneRoot = clone.transform;
            Renderer[] cloneRs = clone.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < cloneRs.Length; i++)
            {
                Renderer cloneR = cloneRs[i];
                if (cloneR == null || !cloneR.enabled) continue;

                string path = GetRelativePath(cloneRoot, cloneR.transform);
                if (string.IsNullOrEmpty(path) || !sourceByPath.TryGetValue(path, out Renderer sourceR))
                    continue;

                MeshRenderer sourceMr = sourceR as MeshRenderer;
                MeshFilter sourceMf = sourceMr != null ? sourceMr.GetComponent<MeshFilter>() : null;

                if (sourceMr != null && sourceMr.isPartOfStaticBatch &&
                    sourceMf != null && sourceMf.sharedMesh != null &&
                    StaticBatchMeshHelper.TryGetBatchSlice(sourceMr, out int firstSub, out int subCount))
                {
                    // The clone renderer lost its static-batch registration and would
                    // draw submesh 0 of the whole combined mesh (garbage). Turn it off
                    // and GPU-draw the exact slice, resolving materials live from the
                    // source so TRL reskins on rink 1 propagate to the clones.
                    cloneR.enabled = false;

                    Mesh srcMesh = sourceMf.sharedMesh;
                    for (int k = 0; k < subCount; k++)
                        CloneVisualProxy.AddDraw(sourceMr, k, srcMesh, firstSub + k, translate);

                    combinedMeshRebakeCount++;
                    // Built once per proxy entry — hundreds of throwaway strings per build.
                    if (PracticeLog.Verbose)
                        PracticeLog.Info("[PHLPractice] Proxy draw " + clone.name + "/" + path +
                                  " mesh=" + srcMesh.name +
                                  " sub=" + firstSub + "+" + subCount +
                                  " offsetZ=" + worldOffset.z.ToString("F1"));
                }
                else if (cloneR.sharedMaterial != null)
                {
                    // Real clone renderer (glass, nets): keep its materials pointed at
                    // the source's current instances so TRL edits mirror over.
                    CloneVisualProxy.AddMirror(sourceR, cloneR);
                }
            }

            RenameTrlTargets(cloneRoot);
        }

        // TRL locates reskin targets by exact GameObject name (GameObject.Find("Ice
        // Bottom"), first two "Net Cloth" objects, ...). Clone children with identical
        // names can hijack those lookups and steal the reskin from rink 1, so prefix
        // them. Clones inherit the visuals anyway via live material resolution.
        private static readonly string[] TrlFindTargets = { "Ice Top", "Ice Bottom", "Net Cloth" };

        private static void RenameTrlTargets(Transform cloneRoot)
        {
            foreach (Transform t in cloneRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == cloneRoot) continue;
                for (int i = 0; i < TrlFindTargets.Length; i++)
                {
                    if (string.Equals(t.name, TrlFindTargets[i], StringComparison.Ordinal))
                    {
                        t.name = "PHLC " + t.name;
                        break;
                    }
                }
            }
        }

        private static void PrepareServerClone(GameObject clone, int iceLayer)
        {
            foreach (Renderer r in clone.GetComponentsInChildren<Renderer>(true))
                if (r != null) r.enabled = false;

            foreach (Collider col in clone.GetComponentsInChildren<Collider>(true))
            {
                if (col == null) continue;
                col.enabled = true;
                // Keep the vanilla layer (Instantiate already copied it). Flattening
                // everything onto the Ice layer made the puck collide with player-only
                // colliders (Goal Player Collider), wedging it inside the net. The
                // skate-ground Ice layer is provided by AddIceSurfaceCollider's box.
            }
        }

        private static int CountMeshTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            int total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                total += mesh.GetTriangles(s).Length / 3;
            return total;
        }

        private static void LogTemplateInventory(Transform root, string label)
        {
            if (root == null) return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            PracticeLog.Info("[PHLPractice] Template inventory (" + label + "): " + renderers.Length + " renderer(s).");
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;

                Transform t = r.transform;
                MeshFilter mf = t.GetComponent<MeshFilter>();
                string meshName = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "no-mesh";
                PracticeLog.Info("[PHLPractice]   [" + i + "] " + GetRelativePath(root, t) +
                          " type=" + r.GetType().Name + " mesh=" + meshName +
                          " boundsZ=" + r.bounds.center.z.ToString("F1"));
            }
        }

        private static void LogCloneGeometryHealth(GameObject root, List<RinkSlot> rinks, Vector3 primaryOrigin)
        {
            if (root == null || rinks == null) return;

            for (int i = 0; i < rinks.Count; i++)
            {
                RinkSlot slot = rinks[i];
                if (slot == null) continue;

                Vector3 offset = slot.Origin - primaryOrigin;
                if (offset.sqrMagnitude < 0.01f) continue;

                Transform rinkClone = root.transform.Find("Rink_" + slot.Id);
                if (rinkClone == null)
                {
                    Debug.LogWarning("[PHLPractice] Geometry check: Rink_" + slot.Id + " not found.");
                    continue;
                }

                Renderer[] renderers = rinkClone.GetComponentsInChildren<Renderer>(true);
                int count = renderers != null ? renderers.Length : 0;
                float minZ = float.MaxValue;
                float maxZ = float.MinValue;
                for (int r = 0; r < count; r++)
                {
                    if (renderers[r] == null) continue;
                    float z = renderers[r].bounds.center.z;
                    minZ = Mathf.Min(minZ, z);
                    maxZ = Mathf.Max(maxZ, z);
                }

                float expectedZ = slot.Origin.z;
                float zErr = count > 0 ? Mathf.Abs(((minZ + maxZ) * 0.5f) - expectedZ) : 999f;

                PracticeLog.Info("[PHLPractice] Geometry check Rink_" + slot.Id + ": renderers=" + count +
                          " boundsZ=[" + minZ.ToString("F1") + "," + maxZ.ToString("F1") +
                          "] expectedZ~=" + expectedZ.ToString("F1") +
                          " zErr=" + zErr.ToString("F1"));

                if (count == 0 || zErr > 8f)
                    Debug.LogWarning("[PHLPractice] Geometry check FAILED for Rink_" + slot.Id);
            }
        }

        /// <summary>Above this, cloning rink 1's rig nine times would wreck light culling.</summary>
        private const int MaxClonedLightsPerRink = 16;

        // Rink 1 is lit by baked lightmaps plus the arena's own light objects; the clones
        // get neither (Graphics.DrawMesh slices cannot carry lightmaps). Replicate rink 1's
        // rig at each offset as realtime lights — including sources marked Baked, which do
        // not emit at runtime but still describe the fixtures we need. When rink 1 has no
        // usable point/spot fixtures, fall back to a synthetic overhead rig so the offset
        // sheets are not lit by the sun alone.
        private static void CloneRinkLights(Transform levelRoot, Transform parent, List<RinkSlot> rinks, Vector3 primaryOrigin)
        {
            if (levelRoot == null || parent == null || rinks == null) return;

            ArenaLighting.ClearRinkLights();
            EnsureArenaLightSourceCache(levelRoot, primaryOrigin);

            int cloned = 0;
            for (int i = 0; i < rinks.Count; i++)
            {
                RinkSlot slot = rinks[i];
                if (slot == null) continue;
                Vector3 offset = slot.Origin - primaryOrigin;
                if (offset.sqrMagnitude < 0.01f) continue;
                cloned += CloneLightsForSlot(levelRoot, parent, slot, primaryOrigin);
            }

            Debug.Log("[PHLPractice] Light clone: " +
                      (cachedArenaLightSources != null ? cachedArenaLightSources.Count : 0) +
                      " arena fixture(s) near rink 1 → " +
                      (cachedUseSyntheticFill
                          ? "synthetic overhead fill rig per offset rink"
                          : cloned + " realtime clone(s) across offset rinks"));
        }

        /// <summary>Append fill/fixture lights for one offset slot (does not clear the registry).</summary>
        private static int CloneLightsForSlot(
            Transform levelRoot, Transform parent, RinkSlot slot, Vector3 primaryOrigin)
        {
            if (levelRoot == null || parent == null || slot == null) return 0;

            EnsureArenaLightSourceCache(levelRoot, primaryOrigin);
            Vector3 offset = slot.Origin - primaryOrigin;
            if (offset.sqrMagnitude < 0.01f) return 0;

            if (cachedUseSyntheticFill)
            {
                SpawnOffsetFillLights(parent, slot, offset);
                return 3;
            }

            int cloned = 0;
            List<Light> sources = cachedArenaLightSources;
            if (sources == null) return 0;
            for (int s = 0; s < sources.Count; s++)
            {
                Light src = sources[s];
                if (src == null) continue;
                var go = new GameObject("Light_" + slot.Id + "_" + src.name);
                go.transform.SetParent(parent, false);
                go.transform.position = src.transform.position + offset;
                go.transform.rotation = src.transform.rotation;

                Light dst = go.AddComponent<Light>();
                CopyLightProperties(src, dst);
                dst.shadows = LightShadows.None;
                dst.renderMode = LightRenderMode.ForceVertex;
                ArenaLighting.RegisterRinkLight(dst, slot.Origin);
                cloned++;
            }
            return cloned;
        }

        private static void EnsureArenaLightSourceCache(Transform levelRoot, Vector3 primaryOrigin)
        {
            if (arenaLightSourcesCached) return;
            arenaLightSourcesCached = true;

            var sources = new List<Light>();
            var seen = new HashSet<int>();
            CollectRinkLightSources(levelRoot, primaryOrigin, sources, seen);

            Transform lightsRoot = ArenaRenderCatalog.FindPath(levelRoot, "Lights");
            if (lightsRoot != null)
                CollectRinkLightSources(lightsRoot, primaryOrigin, sources, seen);

            CollectSceneRinkLightSources(primaryOrigin, sources, seen);

            cachedArenaLightSources = sources;
            cachedUseSyntheticFill = sources.Count == 0 || sources.Count > MaxClonedLightsPerRink;

            if (!PracticeLog.Verbose) return;
            for (int s = 0; s < sources.Count; s++)
            {
                Light l = sources[s];
                if (l == null) continue;
                PracticeLog.Info("[PHLPractice]   fixture '" + l.name + "' type=" + l.type +
                          " pos=" + l.transform.position.ToString("F1") +
                          " intensity=" + l.intensity.ToString("F2") +
                          " range=" + l.range.ToString("F1") +
                          " baked=" + l.bakingOutput.isBaked);
            }
        }

        /// <summary>
        /// Overhead fill lights for offset rinks. Clone proxy geometry has no lightmaps
        /// and ignores rink-1 probes, so without a dedicated rig the ice reads very dark
        /// in MOTD previews and in-game on rinks 2+.
        ///
        /// Intensities stay deliberately modest: rinks 2–9 already receive the global sun
        /// plus a flat SH ambient on DrawMesh. The old 1.85/1.35/1.35 rig stacked on top
        /// of that and read as an uncanny bright wash next to rink 1's baked lightmaps.
        /// </summary>
        private static void SpawnOffsetFillLights(Transform parent, RinkSlot slot, Vector3 offset)
        {
            if (parent == null || slot == null) return;

            Vector3 origin = slot.Origin;
            var rig = new GameObject("FillLights_" + slot.Id);
            rig.transform.SetParent(parent, false);
            rig.transform.position = origin;

            // Neutral white — any tint shows up as a colour cast on skaters.
            Color bulb = new Color(1f, 0.99f, 0.96f);
            AddFillLight(rig.transform, origin + new Vector3(0f, 22f, 0f), 0.42f, 70f, bulb, origin);
            AddFillLight(rig.transform, origin + new Vector3(0f, 18f, -28f), 0.28f, 55f, bulb, origin);
            AddFillLight(rig.transform, origin + new Vector3(0f, 18f, 28f), 0.28f, 55f, bulb, origin);
        }

        // Shadowless on purpose: nine sheets of shadow-casting fill lights would blow the
        // realtime shadow budget, and the sun already grounds skaters with a shadow.
        private static void AddFillLight(
            Transform parent, Vector3 worldPos, float intensity, float range, Color color, Vector3 rinkOrigin)
        {
            var go = new GameObject("Fill");
            go.transform.SetParent(parent, false);
            go.transform.position = worldPos;

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
            // Vertex path is far cheaper for wide arena fill; pixel lights × 8 sheets tanks FPS.
            light.renderMode = LightRenderMode.ForceVertex;
            light.enabled = true;

            // Authored intensity is the noon value; ArenaLighting scales it up as the sun
            // drops so the clones hold rink 1's constant baked brightness.
            ArenaLighting.RegisterRinkLight(light, rinkOrigin);
        }

        private static void CollectSceneRinkLightSources(
            Vector3 primaryOrigin, List<Light> sources, HashSet<int> seen)
        {
            Light[] all = UnityEngine.Object.FindObjectsByType<Light>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                TryAddRinkLightSource(all[i], primaryOrigin, sources, seen);
        }

        private static void CollectRinkLightSources(
            Transform root, Vector3 primaryOrigin, List<Light> sources, HashSet<int> seen)
        {
            if (root == null) return;
            foreach (Light light in root.GetComponentsInChildren<Light>(true))
                TryAddRinkLightSource(light, primaryOrigin, sources, seen);
        }

        private static void TryAddRinkLightSource(
            Light light, Vector3 primaryOrigin, List<Light> sources, HashSet<int> seen)
        {
            if (light == null || light.type == LightType.Directional) return;
            if (!seen.Add(light.GetInstanceID())) return;

            Vector3 p = light.transform.position - primaryOrigin;
            if (Mathf.Abs(p.x) > 80f || Mathf.Abs(p.z) > 90f || Mathf.Abs(p.y) > 80f) return;

            sources.Add(light);
        }

        private static void CopyLightProperties(Light src, Light dst)
        {
            dst.type = src.type;
            dst.color = src.color;
            dst.range = src.range;
            dst.spotAngle = src.spotAngle;
            dst.innerSpotAngle = src.innerSpotAngle;
            dst.shadows = src.shadows;
            dst.shadowStrength = src.shadowStrength;
            dst.shadowBias = src.shadowBias;
            dst.shadowNormalBias = src.shadowNormalBias;
            dst.shadowNearPlane = src.shadowNearPlane;
            dst.cullingMask = src.cullingMask;
            dst.renderMode = src.renderMode;
            dst.bounceIntensity = src.bounceIntensity;
            // Baked arena lights are disabled at runtime; revive at a soft intensity.
            // Keep well below the old 1.2 default — clones also get sun + SH ambient, and
            // matching baked fixture brightness 1:1 as realtime over-lights the sheet.
            float intensity = src.intensity;
            if ((!src.enabled || src.bakingOutput.isBaked) && intensity < 0.05f)
                intensity = 0.35f;
            else
                intensity *= 0.45f;
            dst.intensity = intensity;
            dst.enabled = true;
        }

        private static void AddIceSurfaceCollider(Transform parent, string rinkName, Vector3 offset, int iceLayer)
        {
            string colliderName = rinkName + "_IceSurface";
            if (parent != null && parent.Find(colliderName) != null) return;

            var iceObj = new GameObject(colliderName);
            iceObj.transform.SetParent(parent, false);
            iceObj.transform.position = new Vector3(offset.x, IceSurfaceY, offset.z);
            iceObj.transform.rotation = Quaternion.identity;

            var box = iceObj.AddComponent<BoxCollider>();
            box.size = new Vector3(45f, 0.2f, 91.5f);
            // Top face flush with the vanilla ice plane. Centering the box AT
            // IceSurfaceY put the surface 10 cm above rink 1's, floating pucks/skaters.
            box.center = new Vector3(0f, -box.size.y * 0.5f, 0f);
            if (iceLayer >= 0) iceObj.layer = iceLayer;
        }

        internal static Vector3 ResolveSpawnPoint(RinkSlot slot)
        {
            if (slot == null) return Vector3.zero;

            Vector3 spawn = slot.SpawnPoint;
            if (spawn.y <= 0.01f)
                spawn.y = slot.Origin.y + IceSurfaceY + DefaultPlayerHeightAboveIce;
            else
                spawn.y = Mathf.Max(spawn.y, slot.Origin.y + IceSurfaceY + 0.05f);

            return spawn;
        }

        private static void CacheIceSurfaceY(Transform levelRoot)
        {
            if (!float.IsNaN(cachedIceSurfaceY)) return;

            Transform iceTop = FindNamedChildRecursive(levelRoot, "Ice Top");
            if (iceTop != null)
            {
                MeshRenderer mr = iceTop.GetComponent<MeshRenderer>();
                cachedIceSurfaceY = mr != null ? mr.bounds.center.y : iceTop.position.y;
                PracticeLog.Info("[PHLPractice] Ice surface Y from 'Ice Top' y=" + cachedIceSurfaceY.ToString("F3"));
                return;
            }

            cachedIceSurfaceY = DefaultIceSurfaceY;
        }

        private static Dictionary<string, GameObject> FindTemplates(Transform levelRoot, string[] paths)
        {
            var map = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path) || map.ContainsKey(path)) continue;

                Transform t = ArenaRenderCatalog.FindPath(levelRoot, path);
                if (t != null)
                    map[path] = t.gameObject;
                else
                    Debug.LogWarning("[PHLPractice] VanillaRinkCloner: template not found: " + path);
            }
            return map;
        }

        private static string SanitizeCloneName(string templatePath, string slotId)
        {
            string safe = templatePath.Replace('/', '_').Replace('\\', '_').Trim();
            return safe + "_" + slotId;
        }

        private static void SetActiveByName(Transform levelRoot, string name, bool active)
        {
            Transform t = ArenaRenderCatalog.FindPath(levelRoot, name);
            if (t != null)
                t.gameObject.SetActive(active);
        }

        private static Transform FindNamedChildRecursive(Transform parent, string name)
        {
            if (parent == null) return null;
            if (string.Equals(parent.name, name, StringComparison.OrdinalIgnoreCase)) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindNamedChildRecursive(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static string GetRelativePath(Transform root, Transform node)
        {
            if (root == null || node == null || node == root) return string.Empty;
            var parts = new List<string>();
            Transform current = node;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
