using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Client welcome MOTD for MultiSheet: fullscreen overlay on join (once per
    /// connection) and F9 reopen. Mouse claim/sustain/release mirrors the PHL public
    /// MOTD — UIState is claimed once, sustained per frame, and released through
    /// UIManager.CheckMouseRequirement so stock views recompute the cursor.
    /// </summary>
    internal static class RinkMotdUI
    {
        private const Key ReopenKey = Key.F9;

        private static VisualElement overlay;
        private static VisualElement roleSectionHost;
        private static VisualElement rinkSectionHost;
        private static RinkMotdPayload lastPayload;
        private static RinkMotdPayload lastRenderedPayload;
        private static int lastRenderedLocalRink = -2;
        private static RinkMotdPayload pending;
        private static MethodInfo checkMouseRequirement;
        private static bool mouseClaimed;
        private static bool autoShownThisConnection;
        private static float nextSectionRefresh;

        internal static bool IsVisible { get { return overlay != null; } }

        internal static bool TryGetLastPayload(out RinkMotdPayload payload)
        {
            payload = lastPayload;
            return payload != null;
        }

        /// <summary>Status packet from the server (show: 0 update, 1 forced, 2 welcome).</summary>
        internal static void OnStatusReceived(RinkMotdPayload payload, byte show)
        {
            if (IsDedicatedServer() || payload == null) return;
            lastPayload = payload;

            if (MultiSheetClientSettings.SkipMotdUi)
            {
                // Still feed the scoreboard embed if that UI is active; no overlay/cameras.
                if (!MultiSheetClientSettings.SkipScoreboardUi && RinkScoreboardTab.IsMenuPaneActive)
                    RinkScoreboardTab.OnPayloadAvailable(payload);
                return;
            }

            RinkPreview.EnsureRig(payload);

            bool forced = show == 1;
            bool welcome = show == 2;

            // Scoreboard Rinks tab owns the UI — refresh embed, no fullscreen overlay.
            if (RinkScoreboardTab.IsMenuPaneActive)
            {
                RinkScoreboardTab.OnPayloadAvailable(payload);
                if (!forced && !welcome) return;
                if (welcome && autoShownThisConnection) return;
                autoShownThisConnection = true;
                return;
            }

            if (IsVisible)
            {
                if (NeedsRinkSectionRefresh(payload))
                    RefreshRinkSection();
                return;
            }

            if (forced || (welcome && !autoShownThisConnection))
            {
                if (welcome) autoShownThisConnection = true;
                pending = payload;
                TryShowPending();
            }
        }

        internal static void Tick()
        {
            if (MultiSheetClientSettings.SkipMotdUi)
            {
                if (IsVisible) Hide();
                pending = null;
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (IsVisible && keyboard.escapeKey.wasPressedThisFrame)
                {
                    Hide();
                    return;
                }

                if (keyboard[ReopenKey].wasPressedThisFrame)
                {
                    if (IsVisible) Hide();
                    else OpenMenu();
                    return;
                }
            }

            if (IsVisible)
            {
                SustainMouse();

                // Local rink highlight can change without a server packet (walking across).
                if (Time.unscaledTime >= nextSectionRefresh && lastPayload != null)
                {
                    nextSectionRefresh = Time.unscaledTime + 1.0f;
                    if (NeedsRinkSectionRefresh(lastPayload))
                        RefreshRinkSection();
                }
            }

            if (overlay == null && pending != null) TryShowPending();
        }

        /// <summary>Open fullscreen MOTD (F9). Requests fresh counts from the server.</summary>
        internal static void OpenMenu()
        {
            if (MultiSheetClientSettings.SkipMotdUi) return;
            ClaimMouse();
            if (lastPayload != null)
            {
                pending = lastPayload;
                TryShowPending();
            }
            RinkMotdService.ClientRequestShow();
        }

        internal static void Hide()
        {
            pending = null;
            roleSectionHost = null;
            rinkSectionHost = null;
            if (overlay != null)
            {
                try { overlay.RemoveFromHierarchy(); } catch { }
                overlay = null;
            }
            if (!RinkScoreboardTab.IsMenuPaneActive)
                RinkPreview.SetVisible(false);
            ReleaseMouse();
        }

        internal static void OnDisconnected()
        {
            Hide();
            lastPayload = null;
            lastRenderedPayload = null;
            lastRenderedLocalRink = -2;
            autoShownThisConnection = false;
            RinkScoreboardTab.InvalidateCardCache();
        }

        internal static void Teardown()
        {
            Hide();
            lastPayload = null;
            lastRenderedPayload = null;
            lastRenderedLocalRink = -2;
            pending = null;
            autoShownThisConnection = false;
            checkMouseRequirement = null;
        }

        internal static RinkPanelBuilder.Callbacks CreateSharedCallbacks(Action onContinue)
        {
            return new RinkPanelBuilder.Callbacks
            {
                OnContinue = onContinue ?? Hide,
                OnSelectRink = OnSelectRink,
                OnSelectRole = OnSelectRole
            };
        }

        private static void OnSelectRole(int role)
        {
            RinkMotdService.ClientRequestSetRole((byte)role);
        }

        private static void OnSelectRink(int rinkIndex)
        {
            RinkMotdService.ClientRequestTeleport(rinkIndex);
            // Close the fullscreen overlay so the player sees the teleport; the
            // scoreboard embed stays open (its status refresh repaints the tiles).
            if (IsVisible) Hide();
        }

        private static void RefreshRinkSection()
        {
            if (overlay == null || lastPayload == null) return;
            RinkPanelBuilder.Callbacks callbacks = CreateSharedCallbacks(Hide);
            try
            {
                if (roleSectionHost != null)
                {
                    RinkPanelBuilder.FillRoleSection(
                        roleSectionHost, lastPayload, callbacks, embedded: false);
                }
                if (rinkSectionHost != null)
                {
                    RinkPanelBuilder.FillRinkSection(
                        rinkSectionHost, lastPayload, callbacks, embedded: false);
                }
                NoteRenderedPayload(lastPayload);
                RinkPanelBuilder.FocusForInput(overlay);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] MOTD rink refresh failed: " + ex.Message);
            }
        }

        /// <summary>Repaint rink tiles after static preview textures are captured.</summary>
        internal static void RefreshPreviewTiles()
        {
            if (lastPayload == null) return;
            if (IsVisible && rinkSectionHost != null)
            {
                try { RinkPanelBuilder.UpdatePreviewTextures(rinkSectionHost); }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] MOTD preview refresh failed: " + ex.Message);
                }
            }
            RinkScoreboardTab.RefreshPreviewTiles(lastPayload);
        }

        private static bool NeedsRinkSectionRefresh(RinkMotdPayload payload)
        {
            if (payload == null) return false;
            int localRink = RinkPanelBuilder.GetLocalRinkIndex(payload);
            return payload.NeedsRinkTileRefresh(lastRenderedPayload, localRink, lastRenderedLocalRink);
        }

        private static void NoteRenderedPayload(RinkMotdPayload payload)
        {
            lastRenderedPayload = payload;
            lastRenderedLocalRink = payload != null
                ? RinkPanelBuilder.GetLocalRinkIndex(payload)
                : -2;
        }

        private static void TryShowPending()
        {
            if (MultiSheetClientSettings.SkipMotdUi)
            {
                pending = null;
                return;
            }

            UIManager manager = MonoBehaviourSingleton<UIManager>.Instance;
            VisualElement root = manager != null ? manager.RootVisualElement : null;
            if (root == null || pending == null) return;

            RinkMotdPayload data = pending;
            if (overlay != null)
            {
                try { overlay.RemoveFromHierarchy(); } catch { }
                overlay = null;
                roleSectionHost = null;
                rinkSectionHost = null;
            }
            pending = null;
            ClaimMouse();

            RinkPanelBuilder.Result built = RinkPanelBuilder.Build(data, CreateSharedCallbacks(Hide));
            overlay = built.Overlay;
            roleSectionHost = built.RoleSectionHost;
            rinkSectionHost = built.RinkSectionHost;
            root.Add(overlay);
            ClaimMouse();
            NoteRenderedPayload(data);
            RinkPanelBuilder.FocusForInput(overlay);
            RinkPreview.SetVisible(true);
        }

        // ------------------------------------------------------------- mouse

        private static void ClaimMouse()
        {
            bool alreadyRequired = false;
            try { alreadyRequired = GlobalStateManager.UIState.IsMouseRequired; }
            catch { }

            try
            {
                if (!alreadyRequired)
                {
                    GlobalStateManager.SetUIState(new Dictionary<string, object>
                    {
                        { "isMouseRequired", true }
                    });
                }
                else if (!mouseClaimed)
                {
                    ApplicationManager.SetMouseVisibility(true);
                }
            }
            catch { }

            mouseClaimed = true;
            ApplyUnlockedCursor();
        }

        private static void SustainMouse()
        {
            try
            {
                if (!GlobalStateManager.UIState.IsMouseRequired)
                {
                    mouseClaimed = false;
                    ClaimMouse();
                    return;
                }
            }
            catch
            {
                ClaimMouse();
                return;
            }

            mouseClaimed = true;
            ApplyUnlockedCursor();
        }

        private static void ApplyUnlockedCursor()
        {
            try
            {
                if (UnityEngine.Cursor.lockState != CursorLockMode.None)
                    UnityEngine.Cursor.lockState = CursorLockMode.None;
                if (!UnityEngine.Cursor.visible)
                    UnityEngine.Cursor.visible = true;
            }
            catch { }
        }

        private static void ReleaseMouse()
        {
            if (IsVisible) return;
            mouseClaimed = false;

            try
            {
                UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
                if (ui != null)
                {
                    if (checkMouseRequirement == null)
                    {
                        checkMouseRequirement = typeof(UIManager).GetMethod(
                            "CheckMouseRequirement",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    }
                    checkMouseRequirement?.Invoke(ui, null);
                }
                else
                {
                    GlobalStateManager.SetUIState(new Dictionary<string, object>
                    {
                        { "isMouseRequired", false }
                    });
                }
            }
            catch
            {
                try
                {
                    GlobalStateManager.SetUIState(new Dictionary<string, object>
                    {
                        { "isMouseRequired", false }
                    });
                }
                catch { }
            }

            try
            {
                ApplicationManager.SetMouseVisibility(GlobalStateManager.UIState.IsMouseRequired);
            }
            catch { }
        }

        private static bool IsDedicatedServer()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        }
    }

    /// <summary>
    /// Esc priority: (1) MOTD overlay open → overlay's own Esc handling closes it,
    /// (2) scoreboard open (either tab) → close the board, (3) otherwise let the stock
    /// pause menu toggle.
    /// </summary>
    [HarmonyPatch(typeof(UIManager), "OnPauseActionPerformed")]
    internal static class RinkMotdPausePatch
    {
        private static bool Prefix()
        {
            if (RinkMotdUI.IsVisible) return false;
            if (RinkScoreboardTab.TryHandleEsc()) return false;
            return true;
        }
    }
}
