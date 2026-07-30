using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Static rink thumbnails for MOTD / scoreboard tiles. One offscreen camera per rink
    /// snaps a wide side-angle shot into a small RenderTexture, then the cameras stay off.
    /// No live hover feed — hover must not enable cameras or force extra sheet draws.
    ///
    /// Capturing uses the engine render loop (not Camera.Render) so CloneVisualProxy
    /// DrawMesh ice lands in the RT. Local-body MeshRendererHider is briefly undone
    /// around preview camera passes via URP begin/endCameraRendering.
    /// </summary>
    internal static class RinkPreview
    {
        private const int TextureWidth = 640;
        private const int TextureHeight = 360;
        private const float IceY = 0.03f;
        /// <summary>Frames each camera stays enabled per capture request.</summary>
        private const int CaptureFrames = 3;
        /// <summary>Extra frames after clones / strip props land so the snap is accurate.</summary>
        private const int CaptureFramesAfterBuild = 6;

        // Whole sheet from an elevated side angle (long axis horizontal).
        private const float OverviewHeight = 38f;
        private const float OverviewSide = 42f;
        private const float OverviewFov = 54f;

        private static readonly List<Camera> cameras = new List<Camera>();
        private static readonly List<RenderTexture> textures = new List<RenderTexture>();
        private static readonly List<Vector3> origins = new List<Vector3>();
        private static int[] pendingCaptureFrames;
        private static bool captureAllRinks;
        private static bool visible;
        private static bool camerasEnabled;
        private static bool wasCapturing;
        private static bool hooksInstalled;
        private static bool restoreLocalBody;
        private static int censusLogsLeft;
        private static GameObject rigRoot;

        internal static Texture GetTexture(int rinkIndex)
        {
            if (rinkIndex < 0 || rinkIndex >= textures.Count) return null;
            return textures[rinkIndex];
        }

        /// <summary>One camera + one RT per rink tile.</summary>
        internal static void EnsureRig(RinkMotdPayload payload)
        {
            if (MultiSheetClientSettings.SkipScoreboardUi) return;
            if (ModRuntimeContext.IsDedicatedGameServer || payload?.Rinks == null || payload.Rinks.Count == 0) return;

            int count = payload.Rinks.Count;
            if (rigRoot != null && cameras.Count == count && OriginsMatch(payload))
                return;

            Teardown();
            rigRoot = new GameObject("MultiSheetRinkPreviewRig");
            UnityEngine.Object.DontDestroyOnLoad(rigRoot);
            rigRoot.hideFlags = HideFlags.HideAndDontSave;

            int uiLayer = LayerMask.NameToLayer("UI");
            int mask = uiLayer >= 0 ? ~(1 << uiLayer) : ~0;

            pendingCaptureFrames = new int[count];

            for (int i = 0; i < count; i++)
            {
                RinkStatusEntry entry = payload.Rinks[i];
                Vector3 origin = new Vector3(entry.OriginX, IceY, entry.OriginZ);
                origins.Add(origin);

                RenderTexture rt = new RenderTexture(TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32);
                rt.name = "MultiSheetPreview_Rink" + (i + 1);
                rt.Create();
                textures.Add(rt);

                GameObject camGo = new GameObject("PreviewCam_Rink" + (i + 1));
                camGo.transform.SetParent(rigRoot.transform, false);

                Camera cam = camGo.AddComponent<Camera>();
                cam.targetTexture = rt;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 300f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 1f);
                cam.depth = -50f - i;
                cam.cullingMask = mask;
                cam.enabled = false;
                cameras.Add(cam);

                ApplyOverviewPose(i);
            }

            InstallRenderHooks();

            PracticeLog.Info("[PHLPractice] Rink preview rig built (" + count + " static camera(s)).");
            censusLogsLeft = 2;
        }

        private static bool OriginsMatch(RinkMotdPayload payload)
        {
            if (payload?.Rinks == null || payload.Rinks.Count != origins.Count) return false;
            for (int i = 0; i < origins.Count; i++)
            {
                RinkStatusEntry entry = payload.Rinks[i];
                if (entry == null) return false;
                Vector3 o = new Vector3(entry.OriginX, IceY, entry.OriginZ);
                if ((origins[i] - o).sqrMagnitude > 0.25f) return false;
            }
            return true;
        }

        internal static void SetVisible(bool value)
        {
            visible = value;
            if (!visible)
            {
                captureAllRinks = false;
                ClearPendingCapture();
                SetCamerasEnabled(false);
                return;
            }
            RequestCapture();
        }

        internal static void NotifyClientBuildComplete()
        {
            if (!visible || cameras.Count == 0 || MultiSheetClientSettings.SkipScoreboardUi) return;
            RequestCapture(extendedFrames: true);
        }

        /// <summary>Re-snap one rink or every rink (default).</summary>
        internal static void RequestCapture(int rinkIndex = -1, bool extendedFrames = false)
        {
            if (!visible || cameras.Count == 0 || MultiSheetClientSettings.SkipScoreboardUi) return;
            EnsurePendingArray();

            int frames = extendedFrames ? CaptureFramesAfterBuild : CaptureFrames;
            captureAllRinks = rinkIndex < 0;

            if (rinkIndex < 0)
            {
                for (int i = 0; i < pendingCaptureFrames.Length; i++)
                    pendingCaptureFrames[i] = Mathf.Max(pendingCaptureFrames[i], frames);
            }
            else if (rinkIndex < pendingCaptureFrames.Length)
            {
                pendingCaptureFrames[rinkIndex] = Mathf.Max(pendingCaptureFrames[rinkIndex], frames);
            }

            if (censusLogsLeft > 0)
            {
                censusLogsLeft--;
                if (PracticeLog.Verbose) LogCensus();
            }
        }

        internal static void LateTick()
        {
            if (MultiSheetClientSettings.SkipScoreboardUi)
            {
                if (cameras.Count > 0) Teardown();
                return;
            }
            if (cameras.Count == 0) return;

            EnsurePendingArray();
            bool anyCapturing = false;

            for (int i = 0; i < cameras.Count; i++)
            {
                bool want = i < pendingCaptureFrames.Length && pendingCaptureFrames[i] > 0;
                if (cameras[i] != null)
                    cameras[i].enabled = want;
                if (want)
                {
                    anyCapturing = true;
                    pendingCaptureFrames[i]--;
                }
            }

            camerasEnabled = anyCapturing;

            if (wasCapturing && !anyCapturing)
            {
                captureAllRinks = false;
                if (visible) RinkMotdUI.RefreshPreviewTiles();
            }

            wasCapturing = anyCapturing;
        }

        internal static void Teardown()
        {
            if (restoreLocalBody)
            {
                MeshRendererHider hider = TryGetLocalHider();
                if (hider != null) hider.HideMeshRenderers();
                restoreLocalBody = false;
            }
            RemoveRenderHooks();
            visible = false;
            camerasEnabled = false;
            wasCapturing = false;
            captureAllRinks = false;
            ClearPendingCapture();
            cameras.Clear();
            origins.Clear();
            for (int i = 0; i < textures.Count; i++)
            {
                try
                {
                    if (textures[i] != null)
                    {
                        textures[i].Release();
                        UnityEngine.Object.Destroy(textures[i]);
                    }
                }
                catch { }
            }
            textures.Clear();
            pendingCaptureFrames = null;
            if (rigRoot != null)
            {
                try { UnityEngine.Object.Destroy(rigRoot); } catch { }
                rigRoot = null;
            }
        }

        /// <summary>True while MOTD capture needs every cloned sheet lit (full re-snap).</summary>
        internal static bool NeedsAllSheetLighting => camerasEnabled && captureAllRinks;

        /// <summary>True when a preview camera is actively rendering this sheet origin.</summary>
        internal static bool IsOriginInCapture(float x, float z)
        {
            if (!camerasEnabled) return false;
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i] == null || !cameras[i].enabled) continue;
                if (i >= origins.Count) continue;
                Vector3 o = origins[i];
                if (ArenaLighting.SameRink(o.x, o.z, x, z)) return true;
            }
            return false;
        }

        internal static bool TryGetLivePreviewOrigin(out float x, out float z)
        {
            x = 0f;
            z = 0f;
            return false;
        }

        internal static bool TryGetPreviewOrigin(Camera cam, out float x, out float z)
        {
            x = 0f;
            z = 0f;
            if (cam == null) return false;
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i] != cam) continue;
                if (i >= origins.Count) return false;
                x = origins[i].x;
                z = origins[i].z;
                return true;
            }
            return false;
        }

        internal static bool IsPreviewCamera(Camera cam) => IsActivePreviewCamera(cam);

        private static void ApplyOverviewPose(int index)
        {
            if (index < 0 || index >= cameras.Count || index >= origins.Count) return;
            Camera cam = cameras[index];
            if (cam == null) return;

            Vector3 origin = origins[index];
            cam.transform.position = origin + new Vector3(-OverviewSide, OverviewHeight, 0f);
            cam.transform.LookAt(origin + new Vector3(0f, 0.5f, 0f));
            cam.fieldOfView = OverviewFov;
        }

        private static void EnsurePendingArray()
        {
            if (pendingCaptureFrames == null || pendingCaptureFrames.Length != cameras.Count)
            {
                pendingCaptureFrames = new int[cameras.Count];
            }
        }

        private static void ClearPendingCapture()
        {
            if (pendingCaptureFrames == null) return;
            for (int i = 0; i < pendingCaptureFrames.Length; i++)
                pendingCaptureFrames[i] = 0;
        }

        private static void InstallRenderHooks()
        {
            if (hooksInstalled) return;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            hooksInstalled = true;
        }

        private static void RemoveRenderHooks()
        {
            if (!hooksInstalled) return;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            hooksInstalled = false;
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!IsPreviewCamera(cam)) return;

            MeshRendererHider hider = TryGetLocalHider();
            if (hider == null || hider.meshRenderers == null || hider.meshRenderers.Count == 0) return;

            MeshRenderer first = hider.meshRenderers[0];
            if (first == null || first.enabled) return;

            hider.ShowMeshRenderers();
            restoreLocalBody = true;
        }

        private static void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!restoreLocalBody || !IsPreviewCamera(cam)) return;

            MeshRendererHider hider = TryGetLocalHider();
            if (hider != null) hider.HideMeshRenderers();
            restoreLocalBody = false;
        }

        private static bool IsActivePreviewCamera(Camera cam)
        {
            if (cam == null || cameras.Count == 0) return false;
            for (int i = 0; i < cameras.Count; i++)
                if (cameras[i] == cam) return true;
            return false;
        }

        private static MeshRendererHider TryGetLocalHider()
        {
            try
            {
                PlayerManager manager = TryGetPlayerManager();
                Player local = manager != null ? manager.GetLocalPlayer() : null;
                PlayerBody body = local != null ? local.PlayerBody : null;
                return body != null ? body.MeshRendererHider : null;
            }
            catch { return null; }
        }

        private static void LogCensus()
        {
            try
            {
                var sb = new StringBuilder("[PHLPractice] Preview census:");
                sb.Append(" cams=").Append(cameras.Count);

                PuckManager puckManager = TryGetPuckManager();
                List<Puck> pucks = puckManager != null ? puckManager.GetPucks() : null;
                sb.Append(" pucks=").Append(pucks != null ? pucks.Count : -1);

                PracticeLog.Info(sb.ToString());
            }
            catch { }
        }

        private static PuckManager TryGetPuckManager()
        {
            try { return MonoBehaviourSingleton<PuckManager>.Instance; }
            catch { return null; }
        }

        private static PlayerManager TryGetPlayerManager()
        {
            try { return MonoBehaviourSingleton<PlayerManager>.Instance; }
            catch { return null; }
        }

        private static void SetCamerasEnabled(bool value)
        {
            camerasEnabled = value;
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i] != null) cameras[i].enabled = value;
            }
        }
    }
}
