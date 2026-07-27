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
        /// <summary>Frames the rig stays enabled per capture request.</summary>
        private const int CaptureFrames = 3;
        /// <summary>Extra frames after client clones + lights exist so proxy draws land.</summary>
        private const int CaptureFramesAfterBuild = 5;

        // Whole sheet from an elevated side angle (long axis horizontal).
        private const float OverviewHeight = 38f;
        private const float OverviewSide = 42f;
        private const float OverviewFov = 54f;

        // --- LIVE HOVER (disabled) — re-enable with SetLiveRink + LateTick live block ---
        // // Fixed on center ice, ~26 m of the length in frame.
        // private const float ActionHeight = 9f;
        // private const float ActionSide = 12f;
        // private const float ActionFov = 52f;
        // private static int liveIndex = -1;
        // private static int enabledLiveIndex = -1;
        // private static VisualElement liveTile;

        private static readonly List<Camera> cameras = new List<Camera>();
        private static readonly List<RenderTexture> textures = new List<RenderTexture>();
        private static readonly List<Vector3> origins = new List<Vector3>();
        private static bool visible;
        private static bool camerasEnabled;
        private static bool hooksInstalled;
        private static bool restoreLocalBody;
        private static int pendingCaptureFrames;
        private static int censusLogsLeft;
        private static GameObject rigRoot;

        /// <summary>All tiles share the rink-1 overview snap (same sheet look at each origin).</summary>
        internal static Texture GetTexture(int rinkIndex)
        {
            return textures.Count > 0 ? textures[0] : null;
        }

        /// <summary>
        /// One camera + one RT at rink 1. Every MOTD/scoreboard tile reuses that snap.
        /// </summary>
        internal static void EnsureRig(RinkMotdPayload payload)
        {
            if (MultiSheetClientSettings.SkipMotdUi) return;
            if (ModRuntimeContext.IsDedicatedGameServer || payload?.Rinks == null || payload.Rinks.Count == 0) return;
            if (rigRoot != null && cameras.Count == 1 && textures.Count == 1) return;

            Teardown();
            rigRoot = new GameObject("MultiSheetRinkPreviewRig");
            UnityEngine.Object.DontDestroyOnLoad(rigRoot);
            rigRoot.hideFlags = HideFlags.HideAndDontSave;

            int uiLayer = LayerMask.NameToLayer("UI");
            int mask = uiLayer >= 0 ? ~(1 << uiLayer) : ~0;

            RinkStatusEntry entry = payload.Rinks[0];
            Vector3 origin = new Vector3(entry.OriginX, IceY, entry.OriginZ);
            origins.Add(origin);

            RenderTexture rt = new RenderTexture(TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32);
            rt.name = "MultiSheetPreview_Shared";
            rt.Create();
            textures.Add(rt);

            GameObject camGo = new GameObject("PreviewCam_Rink1");
            camGo.transform.SetParent(rigRoot.transform, false);

            Camera cam = camGo.AddComponent<Camera>();
            cam.targetTexture = rt;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 300f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 1f);
            cam.depth = -50f;
            cam.cullingMask = mask;
            cam.enabled = false;
            cameras.Add(cam);

            ApplyOverviewPose(0);
            InstallRenderHooks();

            PracticeLog.Info("[PHLPractice] Rink preview rig built (shared rink-1 snap for " +
                      payload.Rinks.Count + " tile(s)).");
            censusLogsLeft = 2;
        }

        /// <summary>Called by the UI when the MOTD card is shown/hidden.</summary>
        internal static void SetVisible(bool value)
        {
            visible = value;
            if (!visible)
            {
                pendingCaptureFrames = 0;
                // SetLiveRink(-1, null); // LIVE HOVER (disabled)
                SetCamerasEnabled(false);
                return;
            }
            RequestCapture();
        }

        /// <summary>
        /// Live-hover entry point (disabled). Tiles stay on the frozen overview snap.
        /// Restore the body below + LateTick live block + RinkPanelBuilder hover wiring.
        /// </summary>
        internal static void SetLiveRink(int index, VisualElement tile)
        {
            // Static previews only — do not enable cameras or re-snap on hover.

            // --- LIVE HOVER (disabled) ---
            // if (index == liveIndex) return;
            //
            // int previous = liveIndex;
            // liveIndex = index;
            // liveTile = index >= 0 ? tile : null;
            //
            // // Leaving a tile: put the camera back on the wide framing and re-snap, so the
            // // tile doesn't keep a stale close-up as its idle picture.
            // if (previous >= 0)
            // {
            //     ApplyOverviewPose(previous);
            //     if (visible) RequestCapture();
            // }
        }

        /// <summary>Re-snap every tile once client-side clones and fill lights exist.</summary>
        internal static void NotifyClientBuildComplete()
        {
            if (!visible || cameras.Count == 0 || MultiSheetClientSettings.SkipMotdUi) return;
            pendingCaptureFrames = Mathf.Max(pendingCaptureFrames, CaptureFramesAfterBuild);
        }

        /// <summary>Re-snap every tile — e.g. once the client-side rink clones exist.</summary>
        internal static void RequestCapture()
        {
            if (!visible || cameras.Count == 0 || MultiSheetClientSettings.SkipMotdUi) return;
            pendingCaptureFrames = CaptureFrames;
            if (censusLogsLeft > 0)
            {
                censusLogsLeft--;
                if (PracticeLog.Verbose) LogCensus();
            }
        }

        /// <summary>Runs from the plugin's LateUpdate, before the frame is rendered.</summary>
        internal static void LateTick()
        {
            if (MultiSheetClientSettings.SkipMotdUi)
            {
                if (cameras.Count > 0) Teardown();
                return;
            }
            if (cameras.Count == 0) return;

            if (pendingCaptureFrames > 0)
            {
                SetCamerasEnabled(true);
                pendingCaptureFrames--;
                return;
            }

            if (camerasEnabled)
            {
                SetCamerasEnabled(false);
                if (visible) RinkMotdUI.RefreshPreviewTiles();
            }

            // --- LIVE HOVER (disabled) — after static capture finishes ---
            // int want = visible ? liveIndex : -1;
            // if (want != enabledLiveIndex)
            // {
            //     SetCameraEnabled(enabledLiveIndex, false);
            //     ApplyActionPose(want);
            //     SetCameraEnabled(want, true);
            //     enabledLiveIndex = want;
            // }
            //
            // if (enabledLiveIndex < 0) return;
            // if (liveTile != null && liveTile.panel != null)
            //     liveTile.MarkDirtyRepaint();
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
            pendingCaptureFrames = 0;
            // liveIndex = -1;
            // enabledLiveIndex = -1;
            // liveTile = null;
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
            if (rigRoot != null)
            {
                try { UnityEngine.Object.Destroy(rigRoot); } catch { }
                rigRoot = null;
            }
        }

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

        // --- LIVE HOVER (disabled) ---
        // /// <summary>Fixed live pose: same side angle as the overview, zoomed on center ice.</summary>
        // private static void ApplyActionPose(int index)
        // {
        //     if (index < 0 || index >= cameras.Count || index >= origins.Count) return;
        //     Camera cam = cameras[index];
        //     if (cam == null) return;
        //
        //     Vector3 origin = origins[index];
        //     cam.transform.position = origin + new Vector3(-ActionSide, ActionHeight, 0f);
        //     cam.transform.LookAt(origin + new Vector3(0f, 0.4f, 0f));
        //     cam.fieldOfView = ActionFov;
        // }
        //
        // private static void SetCameraEnabled(int index, bool value)
        // {
        //     if (index < 0 || index >= cameras.Count) return;
        //     if (cameras[index] != null) cameras[index].enabled = value;
        // }

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

        internal static bool IsPreviewCamera(Camera cam) => IsActivePreviewCamera(cam);

        /// <summary>
        /// Shared rink-1 snap only needs stock level geometry — never force every offset
        /// sheet's DrawMesh/fill lights on for MOTD capture.
        /// </summary>
        internal static bool NeedsAllSheetLighting => false;

        /// <summary>Live hover disabled — always false. Restore body below if re-enabled.</summary>
        internal static bool TryGetLivePreviewOrigin(out float x, out float z)
        {
            x = 0f;
            z = 0f;
            return false;

            // --- LIVE HOVER (disabled) ---
            // int idx = enabledLiveIndex >= 0 ? enabledLiveIndex : liveIndex;
            // if (idx < 0 || idx >= origins.Count) return false;
            // x = origins[idx].x;
            // z = origins[idx].z;
            // return true;
        }

        /// <summary>Rink origin for a preview camera (used to draw only that sheet's proxy).</summary>
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

                PuckManager puckManager = TryGetPuckManager();
                List<Puck> pucks = puckManager != null ? puckManager.GetPucks() : null;
                sb.Append(" pucks=").Append(pucks != null ? pucks.Count : -1);
                if (pucks != null && pucks.Count > 0 && pucks[0] != null)
                {
                    int layer = pucks[0].gameObject.layer;
                    sb.Append(" puckLayer=").Append(layer)
                      .Append('/').Append(LayerMask.LayerToName(layer));
                    int mask = cameras.Count > 0 && cameras[0] != null ? cameras[0].cullingMask : 0;
                    sb.Append(" inMask=").Append((mask & (1 << layer)) != 0);
                }

                PlayerManager playerManager = TryGetPlayerManager();
                List<Player> players = playerManager != null ? playerManager.GetPlayers() : null;
                int bodies = 0;
                if (players != null)
                {
                    for (int i = 0; i < players.Count; i++)
                        if (players[i] != null && players[i].PlayerBody != null) bodies++;
                }
                sb.Append(" players=").Append(players != null ? players.Count : -1)
                  .Append(" bodies=").Append(bodies);

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
            // enabledLiveIndex = -1; // LIVE HOVER (disabled)
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i] != null) cameras[i].enabled = value;
            }
        }
    }
}
