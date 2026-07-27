using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Runtime inventory of everything the game is actually rendering / colliding with under Level.
    /// Built fresh on each level load — no guessed object names from client dumps.
    /// </summary>
    internal sealed class ArenaRenderCatalog
    {
        internal sealed class Entry
        {
            public string HierarchyPath;
            public string TemplateRoot;
            public string RelativePath;
            public string GameObjectName;
            public string RendererType;
            public string MeshName;
            public int MeshVertexCount;
            public List<string> MaterialNames = new List<string>();
            public Vector3 BoundsCenter;
            public Vector3 BoundsSize;
            public string ColliderType;
            public int Layer;
            public bool Active;
            public bool RendererEnabled;
            public bool HasRenderer;
            public bool HasCollider;
            public bool HasMeshFilter;
            public bool SharedMeshNull;
            public bool IsStaticBatch;
            public int SubMeshStartIndex;
            public int MeshInstanceId;

            [JsonIgnore] internal Transform SourceTransform;
        }

        internal string SceneName { get; private set; }
        internal string LevelRootName { get; private set; }
        internal string RoleLabel { get; private set; }
        internal List<Entry> Entries { get; } = new List<Entry>();

        internal static ArenaRenderCatalog Scan(Transform levelRoot, string roleLabel, string[] templatePaths)
        {
            var catalog = new ArenaRenderCatalog
            {
                SceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                LevelRootName = levelRoot != null ? levelRoot.name : "null",
                RoleLabel = roleLabel,
            };

            if (levelRoot == null) return catalog;

            var templateRoots = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            if (templatePaths != null)
            {
                for (int i = 0; i < templatePaths.Length; i++)
                {
                    string path = templatePaths[i];
                    if (string.IsNullOrEmpty(path)) continue;
                    Transform t = FindPath(levelRoot, path);
                    if (t != null) templateRoots[path] = t;
                }
            }

            var seen = new HashSet<Transform>();
            Renderer[] renderers = levelRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || r.transform == null) continue;
                if (!seen.Add(r.transform)) continue;

                Entry entry = BuildEntry(levelRoot, r.transform, templateRoots);
                entry.HasRenderer = true;
                entry.RendererType = r.GetType().Name;
                entry.RendererEnabled = r.enabled;

                MeshFilter mf = r.GetComponent<MeshFilter>();
                entry.HasMeshFilter = mf != null;
                if (mf != null && mf.sharedMesh != null)
                {
                    entry.MeshName = mf.sharedMesh.name;
                    entry.MeshVertexCount = mf.sharedMesh.vertexCount;
                    entry.MeshInstanceId = mf.sharedMesh.GetInstanceID();
                    entry.SharedMeshNull = false;
                }
                else
                {
                    entry.SharedMeshNull = mf == null || mf.sharedMesh == null;
                }

                Material[] mats = r.sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] != null)
                        entry.MaterialNames.Add(mats[m].name);
                }

                Bounds b = r.bounds;
                entry.BoundsCenter = b.center;
                entry.BoundsSize = b.size;

                if (r is MeshRenderer mr)
                {
                    entry.IsStaticBatch = mr.isPartOfStaticBatch;
                    if (mr.isPartOfStaticBatch)
                    {
                        try { entry.SubMeshStartIndex = mr.subMeshStartIndex; }
                        catch { entry.SubMeshStartIndex = 0; }
                    }
                }

                Collider col = r.GetComponent<Collider>();
                if (col != null)
                {
                    entry.HasCollider = true;
                    entry.ColliderType = col.GetType().Name;
                }

                catalog.Entries.Add(entry);
            }

            Collider[] colliders = levelRoot.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];
                if (col == null || col.transform == null) continue;
                if (seen.Contains(col.transform)) continue;
                if (!seen.Add(col.transform)) continue;

                Entry entry = BuildEntry(levelRoot, col.transform, templateRoots);
                entry.HasCollider = true;
                entry.ColliderType = col.GetType().Name;
                catalog.Entries.Add(entry);
            }

            return catalog;
        }

        internal List<Entry> GetEntriesUnderTemplate(string templateRoot)
        {
            var list = new List<Entry>();
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                if (e != null && string.Equals(e.TemplateRoot, templateRoot, StringComparison.OrdinalIgnoreCase))
                    list.Add(e);
            }
            return list;
        }

        internal List<Entry> GetVisualEntriesUnderTemplate(string templateRoot)
        {
            var list = new List<Entry>();
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                if (e == null || !e.HasRenderer || !e.RendererEnabled) continue;
                if (!string.Equals(e.TemplateRoot, templateRoot, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(e);
            }
            return list;
        }

        internal List<Entry> GetColliderEntriesUnderTemplate(string templateRoot)
        {
            var list = new List<Entry>();
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                if (e == null || !e.HasCollider) continue;
                if (!string.Equals(e.TemplateRoot, templateRoot, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(e);
            }
            return list;
        }

        internal string WriteJsonFile(string suffix)
        {
            string dir = ResolveDumpDirectory();
            Directory.CreateDirectory(dir);
            string fileName = "multirink_catalog_" + suffix + "_" + RoleLabel.ToLowerInvariant() + ".json";
            string path = Path.Combine(dir, fileName);

            var dto = new CatalogDto
            {
                scene = SceneName,
                role = RoleLabel,
                levelRoot = LevelRootName,
                entryCount = Entries.Count,
                generatedUtc = DateTime.UtcNow.ToString("o"),
                entries = ToSerializableEntries(Entries),
            };

            File.WriteAllText(path, JsonConvert.SerializeObject(dto, Formatting.Indented), Encoding.UTF8);
            return path;
        }

        private static List<EntryDto> ToSerializableEntries(List<Entry> entries)
        {
            var list = new List<EntryDto>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e == null) continue;
                list.Add(new EntryDto
                {
                    HierarchyPath = e.HierarchyPath,
                    TemplateRoot = e.TemplateRoot,
                    RelativePath = e.RelativePath,
                    GameObjectName = e.GameObjectName,
                    RendererType = e.RendererType,
                    MeshName = e.MeshName,
                    MeshVertexCount = e.MeshVertexCount,
                    MaterialNames = e.MaterialNames,
                    BoundsCenter = Vec3Dto.From(e.BoundsCenter),
                    BoundsSize = Vec3Dto.From(e.BoundsSize),
                    ColliderType = e.ColliderType,
                    Layer = e.Layer,
                    Active = e.Active,
                    RendererEnabled = e.RendererEnabled,
                    HasRenderer = e.HasRenderer,
                    HasCollider = e.HasCollider,
                    HasMeshFilter = e.HasMeshFilter,
                    SharedMeshNull = e.SharedMeshNull,
                    IsStaticBatch = e.IsStaticBatch,
                    SubMeshStartIndex = e.SubMeshStartIndex,
                    MeshInstanceId = e.MeshInstanceId,
                });
            }
            return list;
        }

        internal void LogSummary(string reason)
        {
            int renderers = 0;
            int colliders = 0;
            int combined = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                if (e == null) continue;
                if (e.HasRenderer) renderers++;
                if (e.HasCollider) colliders++;
                if (e.MeshName != null && e.MeshName.IndexOf("Combined Mesh", StringComparison.OrdinalIgnoreCase) >= 0)
                    combined++;
            }

            PracticeLog.Info("[PHLPractice] Arena catalog (" + reason + ") role=" + RoleLabel +
                      " entries=" + Entries.Count + " renderers=" + renderers +
                      " colliders=" + colliders + " combinedMeshes=" + combined + ".");

            var byTemplate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                if (e == null || string.IsNullOrEmpty(e.TemplateRoot)) continue;
                if (!byTemplate.ContainsKey(e.TemplateRoot)) byTemplate[e.TemplateRoot] = 0;
                if (e.HasRenderer && e.RendererEnabled) byTemplate[e.TemplateRoot]++;
            }

            foreach (KeyValuePair<string, int> kv in byTemplate)
                PracticeLog.Info("[PHLPractice]   template '" + kv.Key + "' visible renderers=" + kv.Value);

            int logLimit = Mathf.Min(Entries.Count, 40);
            for (int i = 0; i < logLimit; i++)
            {
                Entry e = Entries[i];
                if (e == null || !e.HasRenderer) continue;
                PracticeLog.Info("[PHLPractice]   [" + i + "] " + e.HierarchyPath +
                          " mesh=" + (e.MeshName ?? "none") +
                          " meshId=" + e.MeshInstanceId +
                          (e.IsStaticBatch ? " batchSub=" + e.SubMeshStartIndex : "") +
                          " mats=" + string.Join("|", e.MaterialNames.ToArray()) +
                          " boundsZ=" + e.BoundsCenter.z.ToString("F1"));
            }

            if (Entries.Count > logLimit)
                PracticeLog.Info("[PHLPractice]   ... " + (Entries.Count - logLimit) + " more entries in JSON file.");
        }

        internal static bool TryHandleDumpCommand(string command, bool isServer, out string reply)
        {
            reply = null;
            if (!string.Equals(command, "/multirink-dump", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(command, "/mutlirink-dump", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(command, "/multirink-diag", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(command, "/multirink-diag", StringComparison.OrdinalIgnoreCase))
            {
                string diagPath = CloneDiagnostics.WriteCloneHealthReport("manual");
                reply = "[PHLPractice] Wrote clone health report to " + diagPath;
                return true;
            }

            Level level = UnityEngine.Object.FindFirstObjectByType<Level>();
            if (level == null)
            {
                reply = "[PHLPractice] Catalog dump failed: Level not found.";
                return true;
            }

            string role = isServer ? "Server" : "Client";
            string[] templates = MultiRinkConfig.Current?.CloneTemplates != null
                ? MultiRinkConfig.Current.CloneTemplates.ToArray()
                : null;

            ArenaRenderCatalog catalog = Scan(level.transform, role, templates);
            catalog.LogSummary("command");
            string path = catalog.WriteJsonFile("manual");
            reply = "[PHLPractice] Wrote " + role + " catalog (" + catalog.Entries.Count +
                    " entries) to " + path;
            return true;
        }

        private static Entry BuildEntry(
            Transform levelRoot, Transform node, Dictionary<string, Transform> templateRoots)
        {
            var entry = new Entry
            {
                SourceTransform = node,
                HierarchyPath = GetHierarchyPath(levelRoot, node),
                GameObjectName = node.name,
                Layer = node.gameObject.layer,
                Active = node.gameObject.activeInHierarchy,
            };

            foreach (KeyValuePair<string, Transform> kv in templateRoots)
            {
                Transform root = kv.Value;
                if (root == null) continue;
                if (node == root || node.IsChildOf(root))
                {
                    entry.TemplateRoot = kv.Key;
                    entry.RelativePath = GetRelativePath(root, node);
                    break;
                }
            }

            return entry;
        }

        private static string ResolveDumpDirectory()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string dataDump = Path.Combine(baseDir, "Data Dump");
                if (Directory.Exists(dataDump)) return dataDump;
                Directory.CreateDirectory(dataDump);
                return dataDump;
            }
            catch { }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PHLPracticeModPack",
                "dumps");
        }

        internal static Transform FindPath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;

            if (path.IndexOf('/') < 0)
            {
                for (int i = 0; i < root.childCount; i++)
                {
                    Transform child = root.GetChild(i);
                    if (child != null && string.Equals(child.name, path, StringComparison.OrdinalIgnoreCase))
                        return child;
                }
                return null;
            }

            string[] parts = path.Split('/');
            Transform current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                if (current == null) return null;
                Transform next = current.Find(parts[i]);
                if (next == null)
                {
                    next = FindNamedChild(current, parts[i]);
                    if (next == null) return null;
                }
                current = next;
            }
            return current;
        }

        internal static Transform ResolveEntryTransform(Transform levelRoot, Entry entry)
        {
            if (entry?.SourceTransform != null) return entry.SourceTransform;
            if (levelRoot == null || entry == null || string.IsNullOrEmpty(entry.RelativePath) ||
                string.IsNullOrEmpty(entry.TemplateRoot))
                return null;

            Transform template = FindPath(levelRoot, entry.TemplateRoot);
            if (template == null) return null;
            if (string.IsNullOrEmpty(entry.RelativePath)) return template;
            return FindPath(template, entry.RelativePath) ?? FindNamedChildRecursive(template, entry.GameObjectName);
        }

        private static Transform FindNamedChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child != null && string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }
            return null;
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

        private static string GetHierarchyPath(Transform root, Transform node)
        {
            if (root == null || node == null) return string.Empty;
            var parts = new List<string>();
            Transform current = node;
            while (current != null)
            {
                parts.Add(current.name);
                if (current == root) break;
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
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

        private sealed class CatalogDto
        {
            public string scene;
            public string role;
            public string levelRoot;
            public int entryCount;
            public string generatedUtc;
            public List<EntryDto> entries;
        }

        private sealed class EntryDto
        {
            public string HierarchyPath;
            public string TemplateRoot;
            public string RelativePath;
            public string GameObjectName;
            public string RendererType;
            public string MeshName;
            public int MeshVertexCount;
            public List<string> MaterialNames;
            public Vec3Dto BoundsCenter;
            public Vec3Dto BoundsSize;
            public string ColliderType;
            public int Layer;
            public bool Active;
            public bool RendererEnabled;
            public bool HasRenderer;
            public bool HasCollider;
            public bool HasMeshFilter;
            public bool SharedMeshNull;
            public bool IsStaticBatch;
            public int SubMeshStartIndex;
            public int MeshInstanceId;
        }

        private sealed class Vec3Dto
        {
            public float x, y, z;

            public static Vec3Dto From(Vector3 v) => new Vec3Dto { x = v.x, y = v.y, z = v.z };
        }
    }
}
