using System.Collections.Generic;
using PHLPracticeModPack;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Client/host: hide Flamie training visuals that are not on the focused sheet when
/// MultiSheet "render just my rink" is on. Rink clones use CloneVisualProxy; Flamie
/// props live under FlamiePrac_ClientVisuals (and server roots on listen-host).
/// </summary>
public static class FlamiePracRinkVisibility
{
    private const float CullIntervalSeconds = 0.12f;

    private static readonly HashSet<int> ForcedHidden = new HashSet<int>();
    private static readonly List<Transform> Scratch = new List<Transform>(64);
    private static bool wasCulling;
    private static int lastFocusRink = int.MinValue;
    private static int lastRootCount = -1;
    private static float nextCullTime;

    public static void Tick(Transform clientVisualRoot)
    {
        if (Application.isBatchMode)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsClient)
        {
            if (wasCulling)
            {
                RestoreTracked();
                wasCulling = false;
            }
            return;
        }

        bool renderAll = RinkRenderFocus.RenderAll;
        if (renderAll)
        {
            if (wasCulling)
            {
                RestoreTracked();
                wasCulling = false;
            }
            lastFocusRink = int.MinValue;
            lastRootCount = -1;
            return;
        }

        wasCulling = true;

        MultiRinkConfig cfg = MultiRinkConfig.Current;
        bool haveRinks = cfg?.Rinks != null && cfg.Rinks.Count > 0;

        // Match CloneVisualProxy: no invented "show everything" when just-my-rink is on.
        float fx = 0f, fz = 0f;
        bool haveFocus = RinkRenderFocus.TryGetGameplayFocus(out fx, out fz);
        int focusRink = -1;
        if (haveFocus && haveRinks)
            focusRink = RinkLocator.NearestRink(cfg, new Vector3(fx, 0f, fz));
        else if (!haveFocus && haveRinks)
            focusRink = 0; // pre-spawn: only sheet 1 (same as StockPuckHider default)

        int rootCount = CountRoots(clientVisualRoot);
        if (nm.IsServer)
            rootCount += TrainingObjectManager.Instance != null ? 1 : 0; // force recheck with server roots

        bool focusChanged = focusRink != lastFocusRink;
        bool due = Time.unscaledTime >= nextCullTime;
        bool rootsChanged = rootCount != lastRootCount;
        if (!focusChanged && !due && !rootsChanged && Scratch.Count > 0)
            return;

        nextCullTime = Time.unscaledTime + CullIntervalSeconds;
        lastFocusRink = focusRink;
        lastRootCount = rootCount;

        Scratch.Clear();
        CollectRoots(clientVisualRoot, Scratch);

        // Listen-host also has authoritative server props outside the DDOL mirror root.
        if (nm.IsServer)
            TrainingObjectManager.Instance?.CollectCullRoots(Scratch);

        for (int i = 0; i < Scratch.Count; i++)
        {
            Transform t = Scratch[i];
            if (t == null)
                continue;

            bool show;
            if (RinkPreview.IsOriginInCapture(t.position.x, t.position.z))
            {
                show = true;
            }
            else if (!haveRinks || focusRink < 0)
            {
                // Config not ready — hide all Flamie visuals rather than leak other sheets.
                show = false;
            }
            else
            {
                int objRink = RinkLocator.NearestRink(cfg, t.position);
                show = objRink == focusRink;
            }

            SetShown(t.gameObject, show);
        }
    }

    public static void Clear()
    {
        RestoreTracked();
        wasCulling = false;
        lastFocusRink = int.MinValue;
        lastRootCount = -1;
        nextCullTime = 0f;
    }

    private static int CountRoots(Transform clientVisualRoot)
    {
        return clientVisualRoot != null ? clientVisualRoot.childCount : 0;
    }

    private static void CollectRoots(Transform clientVisualRoot, List<Transform> into)
    {
        if (clientVisualRoot == null)
            return;

        for (int i = 0; i < clientVisualRoot.childCount; i++)
        {
            Transform child = clientVisualRoot.GetChild(i);
            if (child != null)
                into.Add(child);
        }
    }

    private static void SetShown(GameObject root, bool show)
    {
        if (root == null)
            return;

        int id = root.GetInstanceID();

        if (!show)
        {
            if (root.activeSelf)
            {
                ForcedHidden.Add(id);
                root.SetActive(false);
            }
            return;
        }

        if (ForcedHidden.Remove(id) && !root.activeSelf)
            root.SetActive(true);
    }

    private static void RestoreTracked()
    {
        if (ForcedHidden.Count == 0)
            return;

        // Active/inactive objects may still be findable via TrainingSync / TOM lists next tick;
        // restore anything we still know about by scanning current roots.
        Scratch.Clear();
        TrainingSync sync = TrainingSync.Instance;
        if (sync != null)
            CollectRoots(sync.ClientVisualRoot, Scratch);
        TrainingObjectManager.Instance?.CollectCullRoots(Scratch);

        for (int i = 0; i < Scratch.Count; i++)
        {
            Transform t = Scratch[i];
            if (t == null)
                continue;
            SetShown(t.gameObject, true);
        }

        ForcedHidden.Clear();
    }
}
