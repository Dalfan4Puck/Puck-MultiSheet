using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Post-clone health report — what actually exists on client clone roots after spawn.
    /// </summary>
    internal static class CloneDiagnostics
    {
        internal sealed class RendererRow
        {
            public string CloneRoot;
            public string Path;
            public string Type;
            public bool Enabled;
            public string MeshName;
            public bool MeshReadable;
            public int VertexCount;
            public int TriangleCount;
            public int SubMeshCount;
            public string Material;
            public string Shader;
            public int LightmapIndex;
            public bool IsStaticBatch;
            public int SubMeshStartIndex;
            public Vec3Dto BoundsCenter;
            public Vec3Dto BoundsSize;
        }

        internal sealed class Report
        {
            public string generatedUtc;
            public string scene;
            public Vec3Dto playerPosition;
            public string playerRinkGuess;
            public int proxyDrawEntries;
            public int materialMirrorPairs;
            public CameraInfo camera;
            public List<LightRow> lights = new List<LightRow>();
            public List<RinkSummary> rinks = new List<RinkSummary>();
            public List<RendererRow> renderers = new List<RendererRow>();
        }

        internal sealed class LightRow
        {
            public string name;
            public string path;
            public string type;
            public bool enabled;
            public Vec3Dto position;
            public float intensity;
            public float range;
            public string shadows;
            public float shadowStrength;
            public string bakeType;
            public bool isBaked;
        }

        internal sealed class CameraInfo
        {
            public string name;
            public Vec3Dto position;
            public float farClipPlane;
            public bool useOcclusionCulling;
            public int cullingMask;
            public bool fogEnabled;
            public float fogEndDistance;
            public int lightmapCount;
        }

        internal sealed class RinkSummary
        {
            public string name;
            public int rendererCount;
            public int totalTriangles;
            public float boundsMinZ;
            public float boundsMaxZ;
            public float boundsCenterZ;
            public IcePairSummary ice;
        }

        internal sealed class IcePairSummary
        {
            public RendererRow iceTop;
            public RendererRow iceBottom;
        }

        internal sealed class Vec3Dto
        {
            public float x, y, z;
            public static Vec3Dto From(Vector3 v) => new Vec3Dto { x = v.x, y = v.y, z = v.z };
        }

        internal static string WriteCloneHealthReport(string suffix)
        {
            string dir = ResolveDumpDirectory();
            Directory.CreateDirectory(dir);

            Report report = BuildReport();

            // Unique filename per capture: rink guess + timestamp, so back-to-back
            // /multirink-diag runs on rink2 and rink3 never overwrite each other.
            string rinkTag = string.IsNullOrEmpty(report.playerRinkGuess) ? "unknown" : report.playerRinkGuess;
            string stamp = DateTime.Now.ToString("HHmmss");
            string path = Path.Combine(dir,
                "multirink_clone_health_" + suffix + "_" + rinkTag + "_" + stamp + "_client.json");

            File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented), Encoding.UTF8);
            LogSummary(report);
            return path;
        }

        internal static bool TryHandleDiagCommand(string command, out string reply)
        {
            reply = null;
            if (!string.Equals(command, "/multirink-diag", StringComparison.OrdinalIgnoreCase))
                return false;

            string path = WriteCloneHealthReport("manual");
            reply = "[PHLPractice] Wrote clone health report (" + path + ")";
            return true;
        }

        private static Report BuildReport()
        {
            var report = new Report
            {
                generatedUtc = DateTime.UtcNow.ToString("o"),
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            };

            TryFillPlayerContext(report);
            TryFillCameraContext(report);
            report.proxyDrawEntries = CloneVisualProxy.EntryCount;
            report.materialMirrorPairs = CloneVisualProxy.MirrorCount;
            TryFillLights(report);

            GameObject clientRoot = GameObject.Find("PHL_VanillaMultiRink_Client");
            if (clientRoot == null)
                return report;

            var byRink = new Dictionary<string, RinkSummary>(StringComparer.OrdinalIgnoreCase);

            Renderer[] renderers = clientRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;

                string cloneRoot = GetCloneRootName(r.transform, clientRoot.transform);
                if (!byRink.TryGetValue(cloneRoot, out RinkSummary summary))
                {
                    summary = new RinkSummary { name = cloneRoot };
                    byRink[cloneRoot] = summary;
                }

                RendererRow row = BuildRow(r, cloneRoot);
                report.renderers.Add(row);

                if (!row.Enabled) continue;
                summary.rendererCount++;
                summary.totalTriangles += row.TriangleCount;
                summary.boundsMinZ = summary.rendererCount == 1 ? row.BoundsCenter.z : Mathf.Min(summary.boundsMinZ, row.BoundsCenter.z);
                summary.boundsMaxZ = summary.rendererCount == 1 ? row.BoundsCenter.z : Mathf.Max(summary.boundsMaxZ, row.BoundsCenter.z);
            }

            foreach (KeyValuePair<string, RinkSummary> kv in byRink)
            {
                RinkSummary s = kv.Value;
                if (s.rendererCount > 0)
                    s.boundsCenterZ = (s.boundsMinZ + s.boundsMaxZ) * 0.5f;
                s.ice = BuildIcePair(report.renderers, kv.Key);
                report.rinks.Add(s);
            }

            report.rinks.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return report;
        }

        private static RendererRow BuildRow(Renderer r, string cloneRoot)
        {
            var row = new RendererRow
            {
                CloneRoot = cloneRoot,
                Path = GetRelativePath(r.transform, cloneRoot),
                Type = r.GetType().Name,
                Enabled = r.enabled,
                LightmapIndex = r.lightmapIndex,
                BoundsCenter = Vec3Dto.From(r.bounds.center),
                BoundsSize = Vec3Dto.From(r.bounds.size),
            };

            Material mat = r.sharedMaterial;
            if (mat != null)
            {
                row.Material = mat.name;
                row.Shader = mat.shader != null ? mat.shader.name : "null";
            }

            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Mesh mesh = mf.sharedMesh;
                row.MeshName = mesh.name;
                row.MeshReadable = mesh.isReadable;
                row.VertexCount = mesh.vertexCount;
                row.SubMeshCount = mesh.subMeshCount;
                row.TriangleCount = CountTriangles(mesh);

                MeshRenderer mr = r as MeshRenderer;
                if (mr != null)
                {
                    row.IsStaticBatch = mr.isPartOfStaticBatch;
                    try { row.SubMeshStartIndex = mr.subMeshStartIndex; }
                    catch { row.SubMeshStartIndex = -1; }
                }
            }
            else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
            {
                row.MeshName = smr.sharedMesh.name;
                row.VertexCount = smr.sharedMesh.vertexCount;
                row.TriangleCount = CountTriangles(smr.sharedMesh);
            }

            return row;
        }

        private static int CountTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            int total = 0;
            int sub = mesh.subMeshCount;
            for (int s = 0; s < sub; s++)
                total += mesh.GetTriangles(s).Length / 3;
            return total;
        }

        private static void TryFillPlayerContext(Report report)
        {
            try
            {
                PlayerBody local = NetworkBehaviourSingleton<PlayerBody>.Instance;
                if (local == null)
                    local = UnityEngine.Object.FindFirstObjectByType<PlayerBody>();
                if (local == null) return;

                report.playerPosition = Vec3Dto.From(local.transform.position);
                float z = local.transform.position.z;
                if (z < 64f) report.playerRinkGuess = "rink1";
                else if (z < 192f) report.playerRinkGuess = "rink2";
                else report.playerRinkGuess = "rink3";
            }
            catch { }
        }

        private static void TryFillLights(Report report)
        {
            try
            {
                foreach (Light l in UnityEngine.Object.FindObjectsByType<Light>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (l == null) continue;
                    report.lights.Add(new LightRow
                    {
                        name = l.name,
                        path = GetFullPath(l.transform),
                        type = l.type.ToString(),
                        enabled = l.enabled && l.gameObject.activeInHierarchy,
                        position = Vec3Dto.From(l.transform.position),
                        intensity = l.intensity,
                        range = l.range,
                        shadows = l.shadows.ToString(),
                        shadowStrength = l.shadowStrength,
                        bakeType = l.bakingOutput.lightmapBakeType.ToString(),
                        isBaked = l.bakingOutput.isBaked,
                    });
                }
            }
            catch { }
        }

        private static string GetFullPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void TryFillCameraContext(Report report)
        {
            try
            {
                Camera cam = Camera.main;
                if (cam == null)
                {
                    Camera[] cams = Camera.allCameras;
                    if (cams != null && cams.Length > 0) cam = cams[0];
                }
                if (cam == null) return;

                report.camera = new CameraInfo
                {
                    name = cam.name,
                    position = Vec3Dto.From(cam.transform.position),
                    farClipPlane = cam.farClipPlane,
                    useOcclusionCulling = cam.useOcclusionCulling,
                    cullingMask = cam.cullingMask,
                    fogEnabled = RenderSettings.fog,
                    fogEndDistance = RenderSettings.fogEndDistance,
                    lightmapCount = LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0,
                };
            }
            catch { }
        }

        private static IcePairSummary BuildIcePair(List<RendererRow> rows, string cloneRoot)
        {
            var pair = new IcePairSummary();
            for (int i = 0; i < rows.Count; i++)
            {
                RendererRow row = rows[i];
                if (!string.Equals(row.CloneRoot, cloneRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (row.Path != null && row.Path.EndsWith("Ice Top", StringComparison.OrdinalIgnoreCase))
                    pair.iceTop = row;
                else if (row.Path != null && row.Path.EndsWith("Ice Bottom", StringComparison.OrdinalIgnoreCase))
                    pair.iceBottom = row;
            }
            return pair;
        }

        private static void LogSummary(Report report)
        {
            PracticeLog.Info("[PHLPractice] Clone health: playerZ=" +
                      (report.playerPosition != null ? report.playerPosition.z.ToString("F1") : "?") +
                      " guess=" + (report.playerRinkGuess ?? "?") +
                      " cloneRenderers=" + report.renderers.Count +
                      " proxyDraws=" + report.proxyDrawEntries);

            if (report.camera != null)
            {
                PracticeLog.Info("[PHLPractice]   camera=" + report.camera.name +
                          " z=" + report.camera.position.z.ToString("F1") +
                          " farClip=" + report.camera.farClipPlane.ToString("F0") +
                          " occlusionCulling=" + report.camera.useOcclusionCulling +
                          " fog=" + report.camera.fogEnabled +
                          " fogEnd=" + report.camera.fogEndDistance.ToString("F0") +
                          " lightmaps=" + report.camera.lightmapCount);
            }

            for (int i = 0; i < report.rinks.Count; i++)
            {
                RinkSummary s = report.rinks[i];
                PracticeLog.Info("[PHLPractice]   " + s.name + ": renderers=" + s.rendererCount +
                          " tris=" + s.totalTriangles +
                          " boundsZ~=" + s.boundsCenterZ.ToString("F1"));
                LogIcePair(s.name, s.ice);
            }

            int logLimit = Mathf.Min(report.renderers.Count, 25);
            for (int i = 0; i < logLimit; i++)
            {
                RendererRow row = report.renderers[i];
                if (!row.Enabled) continue;
                PracticeLog.Info("[PHLPractice]   [" + i + "] " + row.CloneRoot + "/" + row.Path +
                          " tris=" + row.TriangleCount +
                          " mat=" + (row.Material ?? "?") +
                          " shader=" + (row.Shader ?? "?") +
                          " lm=" + row.LightmapIndex +
                          " boundsZ=" + row.BoundsCenter.z.ToString("F1"));
            }
        }

        private static void LogIcePair(string rinkName, IcePairSummary ice)
        {
            if (ice == null) return;
            LogIceRow(rinkName, "Ice Top", ice.iceTop);
            LogIceRow(rinkName, "Ice Bottom", ice.iceBottom);
        }

        private static void LogIceRow(string rinkName, string label, RendererRow row)
        {
            if (row == null)
            {
                Debug.LogWarning("[PHLPractice]     " + rinkName + " " + label + ": MISSING");
                return;
            }

            PracticeLog.Info("[PHLPractice]     " + rinkName + " " + label +
                      ": tris=" + row.TriangleCount +
                      " enabled=" + row.Enabled +
                      " boundsZ=" + row.BoundsCenter.z.ToString("F1") +
                      " lm=" + row.LightmapIndex +
                      " batchSub=" + row.SubMeshStartIndex +
                      " mat=" + (row.Material ?? "?"));
        }

        private static string GetCloneRootName(Transform t, Transform clientRoot)
        {
            Transform current = t;
            while (current != null && current.parent != clientRoot)
                current = current.parent;
            return current != null ? current.name : t.name;
        }

        private static string GetRelativePath(Transform t, string cloneRootName)
        {
            Transform current = t;
            while (current != null && !string.Equals(current.name, cloneRootName, StringComparison.OrdinalIgnoreCase))
                current = current.parent;
            if (current == null) return t.name;
            return GetRelativePath(current, t);
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

        private static string ResolveDumpDirectory()
        {
            try
            {
                string dataDump = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data Dump");
                Directory.CreateDirectory(dataDump);
                return dataDump;
            }
            catch { }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PHLPracticeModPack",
                "dumps");
        }
    }
}
