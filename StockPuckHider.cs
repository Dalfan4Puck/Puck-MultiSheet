using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Client-side puck renderer cull:
    ///   - just-my-rink: hide pucks whose nearest sheet is not the local focus rink
    ///   - hideStockPucks (FPS A/B): hide everything except the local R-spawned puck
    /// Network/physics still run; this is visibility only.
    /// </summary>
    internal sealed class StockPuckHider : MonoBehaviour
    {
        private const float ClaimRadius = 6f;
        private const float ClaimWindowSeconds = 3f;

        private static StockPuckHider instance;

        private Puck allowedPuck;
        private bool awaitingClaim;
        private Vector3 claimCenter;
        private float claimDeadline;

        private readonly Dictionary<int, bool[]> hiddenRendererState = new Dictionary<int, bool[]>();
        private bool wasCulling;

        internal static void NotifyLocalSpawnRequest(Vector3 playerPos, Vector3 forward)
        {
            if (instance == null || !MultiSheetClientSettings.HideStockPucks) return;
            instance.BeginClaim(playerPos + forward * 2f);
        }

        private void Awake()
        {
            instance = this;
        }

        private void OnEnable()
        {
            EventManager.AddEventListener("Event_Everyone_OnPuckSpawned", OnPuckSpawned);
            EventManager.AddEventListener("Event_Everyone_OnPuckDespawned", OnPuckDespawned);
        }

        private void OnDisable()
        {
            EventManager.RemoveEventListener("Event_Everyone_OnPuckSpawned", OnPuckSpawned);
            EventManager.RemoveEventListener("Event_Everyone_OnPuckDespawned", OnPuckDespawned);
            RestoreAll();
            if (instance == this) instance = null;
        }

        private void LateUpdate()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient)
            {
                if (wasCulling) { RestoreAll(); wasCulling = false; }
                return;
            }

            bool hideStock = MultiSheetClientSettings.HideStockPucks;
            bool justMyRink = !RinkRenderFocus.RenderAll;
            bool culling = hideStock || justMyRink;

            if (!culling)
            {
                if (wasCulling)
                {
                    RestoreAll();
                    allowedPuck = null;
                    awaitingClaim = false;
                    wasCulling = false;
                }
                return;
            }

            wasCulling = true;

            if (awaitingClaim && Time.unscaledTime > claimDeadline)
                awaitingClaim = false;
            if (allowedPuck != null && !allowedPuck)
                allowedPuck = null;

            int focusRink = -1;
            if (!hideStock && justMyRink)
            {
                MultiRinkConfig cfg = MultiRinkConfig.Current;
                if (cfg?.Rinks == null || cfg.Rinks.Count == 0) return;
                if (!RinkRenderFocus.TryGetGameplayFocus(out float fx, out float fz))
                {
                    // No body/focus yet — hide all offset-sheet pucks (nearest to non-primary).
                    ApplyVisibility(hideStock, justMyRink, focusRink: -2);
                    return;
                }
                focusRink = RinkLocator.NearestRink(cfg, new Vector3(fx, 0f, fz));
            }

            ApplyVisibility(hideStock, justMyRink, focusRink);
        }

        private void BeginClaim(Vector3 center)
        {
            allowedPuck = null;
            awaitingClaim = true;
            claimCenter = center;
            claimCenter.y = 0f;
            claimDeadline = Time.unscaledTime + ClaimWindowSeconds;
        }

        private void OnPuckSpawned(Dictionary<string, object> message)
        {
            if (!MultiSheetClientSettings.HideStockPucks || !awaitingClaim) return;
            if (message == null || !message.TryGetValue("puck", out object raw) || !(raw is Puck puck) || !puck)
                return;

            Vector3 p = puck.transform.position;
            p.y = 0f;
            if ((p - claimCenter).sqrMagnitude > ClaimRadius * ClaimRadius) return;

            allowedPuck = puck;
            awaitingClaim = false;
            PracticeLog.Info("[PHLPractice] hideStockPucks — claimed R-spawn puck netId=" +
                             puck.NetworkObjectId);
        }

        private void OnPuckDespawned(Dictionary<string, object> message)
        {
            if (message == null || !message.TryGetValue("puck", out object raw) || !(raw is Puck puck))
                return;
            if (allowedPuck == puck)
                allowedPuck = null;
            Forget(puck);
        }

        /// <param name="focusRink">
        /// Nearest rink index for gameplay focus; -2 means hide every puck not on rink 0
        /// while focus is unknown; ignored when hideStock is set.
        /// </param>
        private void ApplyVisibility(bool hideStock, bool justMyRink, int focusRink)
        {
            PuckManager manager = MonoBehaviourSingleton<PuckManager>.Instance;
            if (manager == null) return;

            List<Puck> pucks = manager.GetPucks(false);
            if (pucks == null) return;

            MultiRinkConfig cfg = MultiRinkConfig.Current;

            for (int i = 0; i < pucks.Count; i++)
            {
                Puck puck = pucks[i];
                if (!puck) continue;

                bool show;
                if (hideStock)
                    show = allowedPuck != null && puck == allowedPuck;
                else if (justMyRink)
                {
                    if (cfg?.Rinks == null || cfg.Rinks.Count == 0)
                        show = true;
                    else if (focusRink == -2)
                    {
                        // Pre-spawn / MOTD without focus: only show rink-1 (origin) pucks.
                        int rink = RinkLocator.NearestRink(cfg, puck.transform.position);
                        show = rink == 0;
                    }
                    else
                    {
                        int rink = RinkLocator.NearestRink(cfg, puck.transform.position);
                        show = rink == focusRink;
                    }
                }
                else
                    show = true;

                SetShown(puck, show);
            }
        }

        private void SetShown(Puck puck, bool show)
        {
            int id = puck.GetInstanceID();
            Renderer[] renderers = puck.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0) return;

            if (!show)
            {
                if (!hiddenRendererState.ContainsKey(id))
                {
                    var prior = new bool[renderers.Length];
                    for (int i = 0; i < renderers.Length; i++)
                        prior[i] = renderers[i] != null && renderers[i].enabled;
                    hiddenRendererState[id] = prior;
                }

                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null) renderers[i].enabled = false;
                }
                return;
            }

            if (hiddenRendererState.TryGetValue(id, out bool[] saved))
            {
                for (int i = 0; i < renderers.Length && i < saved.Length; i++)
                {
                    if (renderers[i] != null) renderers[i].enabled = saved[i];
                }
                hiddenRendererState.Remove(id);
            }
            else
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null) renderers[i].enabled = true;
                }
            }
        }

        private void Forget(Puck puck)
        {
            if (puck != null)
                hiddenRendererState.Remove(puck.GetInstanceID());
        }

        private void RestoreAll()
        {
            if (hiddenRendererState.Count == 0) return;

            PuckManager manager = MonoBehaviourSingleton<PuckManager>.Instance;
            List<Puck> pucks = manager != null ? manager.GetPucks(true) : null;
            if (pucks != null)
            {
                for (int i = 0; i < pucks.Count; i++)
                {
                    Puck puck = pucks[i];
                    if (!puck) continue;
                    if (!hiddenRendererState.TryGetValue(puck.GetInstanceID(), out bool[] saved))
                        continue;

                    Renderer[] renderers = puck.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < renderers.Length && r < saved.Length; r++)
                    {
                        if (renderers[r] != null) renderers[r].enabled = saved[r];
                    }
                }
            }

            hiddenRendererState.Clear();
        }
    }
}
