using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Dual tabs on the stock Tab scoreboard: native Scoreboard vs embedded Rinks card.
    ///
    /// Coexistence with StatsTooltip (Harmony id oomtm450_stats), same rules as the PHL
    /// public MOTD: only add our own MultiSheet* siblings on the scoreboard container,
    /// never touch Content/Header or Player rows, and keep the Rinks pane display:none +
    /// pickingMode Ignore while the native tab is active so tooltips stay interactive.
    /// </summary>
    internal static class RinkScoreboardTab
    {
        private const string TabBarName = "MultiSheetScoreboardTabBar";
        private const string ScoreboardTabName = "MultiSheetTabScoreboard";
        private const string MenuTabName = "MultiSheetTabRinks";
        private const string BadgeName = "MultiSheetRinksTabVoteBadge";
        private const string PaneName = "MultiSheetRinkPane";
        internal const string StatsTooltipHarmonyId = "oomtm450_stats";
        private const float TabBarHeight = 34f;
        /// <summary>Tall enough for header + flex rink grid + Position|Lighting + perf toggles +
        /// community footer. The rink section flex-grows into this height (tiles absorb the
        /// space — do not "fix" emptiness by raising this without giving the grid flexGrow).</summary>
        private const float MenuMinBoardHeight = 860f;

        private static readonly Color TabBarBg = new Color(0.06f, 0.06f, 0.07f, 1f);
        private static readonly Color TabIdleBg = new Color(0.10f, 0.10f, 0.11f, 1f);
        private static readonly Color TabActiveBg = new Color(0.16f, 0.16f, 0.18f, 1f);
        private static readonly Color TabBorder = new Color(0.35f, 0.35f, 0.38f, 1f);
        private static readonly Color TabText = new Color(0.92f, 0.92f, 0.92f, 1f);
        private static readonly Color TabMuted = new Color(0.65f, 0.65f, 0.68f, 1f);
        private static readonly Color VoteBadgeBg = new Color(0.90f, 0.49f, 0.13f, 1f);
        private static readonly Color VoteBadgeText = Color.white;

        private static readonly FieldInfo ScoreboardField =
            typeof(UIScoreboard).GetField("scoreboard", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool enabled = true;
        private static bool menuPaneActive;
        private static VisualElement boardRoot;
        private static VisualElement rinkPane;
        private static VisualElement roleSectionHost;
        private static VisualElement rinkSectionHost;
        private static VisualElement stripSectionHost;
        private static VisualElement radioSectionHost;
        private static Button scoreboardTabButton;
        private static Button menuTabButton;
        /// <summary>Set while we intentionally close the board so the hold-open patch lets it through.</summary>
        private static bool allowHideOnce;
        /// <summary>Vertical bump applied once per scoreboard open — never per tab switch.</summary>
        private static bool boardPositionApplied;
        private static float appliedMarginTop = -1f;

        internal static bool IsMenuPaneActive { get { return menuPaneActive; } }

        /// <summary>True while the Tab-toggled scoreboard is on screen (either tab).</summary>
        internal static bool IsScoreboardOpen()
        {
            try
            {
                UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
                UIScoreboard scoreboard = ui != null ? ui.Scoreboard : null;
                return scoreboard != null && scoreboard.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Esc while the scoreboard is open: close it. If the pause menu was also left
        /// up (Scoreboard tab + Esc bug), close that too so Esc never gets stuck.
        /// </summary>
        internal static bool TryHandleEsc()
        {
            if (!enabled || !PracticeFlowClient.IsOnPracticeServer || !IsScoreboardOpen())
                return false;

            CloseBoard();

            try
            {
                UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
                UIPauseMenu pause = ui != null ? ui.PauseMenu : null;
                if (pause != null && pause.IsVisible)
                    pause.Hide();
            }
            catch { }

            return true;
        }

        /// <summary>
        /// While the Rinks tab is open the scoreboard stays up after Tab is released;
        /// it closes when the player picks a rink or clicks the red X.
        /// </summary>
        internal static bool ShouldBlockHide()
        {
            return enabled && !MultiSheetClientSettings.SkipScoreboardUi &&
                   menuPaneActive && !allowHideOnce;
        }

        internal static void Install(UIScoreboard scoreboard)
        {
            if (!enabled || MultiSheetClientSettings.SkipScoreboardUi ||
                scoreboard == null || ModRuntimeContext.IsDedicatedGameServer) return;

            // No Rinks tab on servers that don't run MultiSheet — Install is re-run on
            // every scoreboard Show, so the tab appears once the payload arrives.
            if (!PracticeFlowClient.IsOnPracticeServer) return;

            VisualElement board = GetScoreboardContainer(scoreboard);
            if (board == null) return;
            boardRoot = board;

            if (board.Q(TabBarName) != null)
            {
                rinkPane = board.Q(PaneName);
                scoreboardTabButton = board.Q<Button>(ScoreboardTabName);
                menuTabButton = board.Q<Button>(MenuTabName);
                if (menuTabButton != null)
                    EnsureMenuTabVoteBadge(menuTabButton);
                ApplyVoteProgress(RinkStripVote.CurrentProgress);
                ApplyPaneHitTesting();
                return;
            }

            try { board.style.overflow = Overflow.Visible; } catch { }

            VisualElement tabBar = BuildTabBar();
            tabBar.style.position = Position.Absolute;
            tabBar.style.left = 0;
            tabBar.style.right = 0;
            tabBar.style.height = TabBarHeight;
            tabBar.style.top = -TabBarHeight;
            board.Add(tabBar);

            rinkPane = new VisualElement { name = PaneName };
            rinkPane.style.display = DisplayStyle.None;
            rinkPane.pickingMode = PickingMode.Ignore;
            rinkPane.style.position = Position.Absolute;
            rinkPane.style.left = 0;
            rinkPane.style.right = 0;
            rinkPane.style.top = 0;
            rinkPane.style.bottom = 0;
            rinkPane.style.backgroundColor = RinkPanelBuilder.PanelBg;
            board.Add(rinkPane);
        }

        /// <summary>Fresh status while the Rinks tab is open — repaint the tiles in place.</summary>
        internal static void OnPayloadAvailable(RinkMotdPayload payload)
        {
            if (!enabled || !menuPaneActive || payload == null) return;
            try
            {
                RinkPanelBuilder.Callbacks callbacks = CreateEmbedCallbacks();
                if (rinkPane != null && rinkPane.childCount > 0 && rinkSectionHost != null)
                {
                    if (roleSectionHost != null)
                        RinkPanelBuilder.FillRoleSection(roleSectionHost, payload, callbacks, embedded: true);
                    RinkPanelBuilder.FillRinkSection(rinkSectionHost, payload, callbacks, embedded: true);
                    if (stripSectionHost != null)
                        RinkPanelBuilder.FillStripSection(stripSectionHost, payload, callbacks, embedded: true);
                }
                else
                {
                    RebuildEmbed(payload);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Scoreboard rink refresh failed: " + ex.Message);
            }
        }

        /// <summary>Repaint embed tiles after static preview textures are captured.</summary>
        internal static void RefreshPreviewTiles(RinkMotdPayload payload)
        {
            if (!enabled || !menuPaneActive || payload == null || rinkSectionHost == null) return;
            try
            {
                RinkPanelBuilder.UpdatePreviewTextures(rinkSectionHost);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Scoreboard preview refresh failed: " + ex.Message);
            }
        }

        /// <summary>Rink pick and red X both close the whole scoreboard (it was held open).</summary>
        private static RinkPanelBuilder.Callbacks CreateEmbedCallbacks()
        {
            return new RinkPanelBuilder.Callbacks
            {
                OnContinue = CloseBoard,
                OnSelectRink = delegate(int rinkIndex)
                {
                    RinkMotdService.ClientRequestTeleport(rinkIndex);
                    CloseBoard();
                },
                OnSelectRole = delegate(int role)
                {
                    RinkMotdService.ClientRequestSetRole((byte)role);
                },
                OnVoteStrip = delegate(int rinkIndex, RinkStripMode mode)
                {
                    RinkStripVote.ClientRequestVote(rinkIndex, mode);
                }
            };
        }

        internal static void ApplyStripVoteProgress(RinkStripVoteProgress progress)
        {
            if (!enabled) return;
            ApplyVoteProgress(progress);

            if (!menuPaneActive) return;
            try
            {
                if (!RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload) || payload == null)
                    return;
                payload.StripVoteProgress = progress;
                if (stripSectionHost != null)
                    RinkPanelBuilder.FillStripSection(
                        stripSectionHost, payload, CreateEmbedCallbacks(), embedded: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Strip vote UI refresh failed: " + ex.Message);
            }
        }

        /// <summary>Called when server status updates strip modes — refresh that rink's tile snap.</summary>
        internal static void OnStripModesUpdated(RinkMotdPayload payload)
        {
            if (!enabled || payload == null) return;
            ApplyVoteProgress(RinkStripVote.CurrentProgress);

            if (!menuPaneActive) return;
            try
            {
                if (rinkPane != null && rinkPane.childCount > 0 && rinkSectionHost != null)
                {
                    RinkPanelBuilder.Callbacks callbacks = CreateEmbedCallbacks();
                    RinkPanelBuilder.FillRinkSection(rinkSectionHost, payload, callbacks, embedded: true);
                    if (stripSectionHost != null)
                        RinkPanelBuilder.FillStripSection(stripSectionHost, payload, callbacks, embedded: true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Scoreboard strip refresh failed: " + ex.Message);
            }
        }

        private static Label EnsureMenuTabVoteBadge(Button tab)
        {
            if (tab == null) return null;
            Label badge = tab.Q<Label>(BadgeName);
            if (badge != null) return badge;

            tab.style.position = Position.Relative;
            try { tab.style.overflow = Overflow.Visible; } catch { }

            badge = new Label("")
            {
                name = BadgeName,
                pickingMode = PickingMode.Ignore
            };
            badge.style.position = Position.Absolute;
            badge.style.top = 2;
            badge.style.right = 2;
            badge.style.display = DisplayStyle.None;
            badge.style.fontSize = 10;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = VoteBadgeText;
            badge.style.backgroundColor = VoteBadgeBg;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.style.paddingLeft = 5;
            badge.style.paddingRight = 5;
            badge.style.paddingTop = 1;
            badge.style.paddingBottom = 1;
            badge.style.borderTopLeftRadius = 8;
            badge.style.borderTopRightRadius = 8;
            badge.style.borderBottomLeftRadius = 8;
            badge.style.borderBottomRightRadius = 8;
            tab.Add(badge);
            return badge;
        }

        private static void ApplyVoteProgress(RinkStripVoteProgress progress)
        {
            if (menuTabButton != null)
                StyleVoteBadge(menuTabButton, progress);
            foreach (Button tab in FindMenuTabButtons())
                StyleVoteBadge(tab, progress);
        }

        private static void StyleVoteBadge(Button tab, RinkStripVoteProgress progress)
        {
            if (tab == null) return;
            Label badge = EnsureMenuTabVoteBadge(tab);
            if (badge == null) return;

            string text = progress.BadgeText;
            if (progress.Active && !string.IsNullOrEmpty(text))
            {
                badge.text = text;
                badge.style.display = DisplayStyle.Flex;
                tab.tooltip = "Tools strip vote " + text;
            }
            else
            {
                badge.text = "";
                badge.style.display = DisplayStyle.None;
                tab.tooltip = "Rinks";
            }
        }

        private static System.Collections.Generic.List<Button> FindMenuTabButtons()
        {
            var tabs = new System.Collections.Generic.List<Button>();
            UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
            if (ui == null) return tabs;
            try
            {
                if (ui.Scoreboard != null && ui.Scoreboard.View != null)
                    ui.Scoreboard.View.Query<Button>(MenuTabName).ForEach(t => { if (t != null) tabs.Add(t); });
            }
            catch { }
            return tabs;
        }

        /// <summary>Esc with the Rinks tab open behaves like clicking the red X.</summary>
        internal static void CloseFromEsc()
        {
            TryHandleEsc();
        }

        private static void CloseBoard()
        {
            ShowScoreboardTab();
            try
            {
                UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
                UIScoreboard scoreboard = ui != null ? ui.Scoreboard : null;
                if (scoreboard != null)
                {
                    allowHideOnce = true;
                    try { scoreboard.Hide(); }
                    finally { allowHideOnce = false; }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Scoreboard close failed: " + ex.Message);
            }
        }

        internal static void RefreshRadioSection()
        {
            if (!enabled || !menuPaneActive || radioSectionHost == null) return;
            try { RinkPanelBuilder.FillRadioSection(radioSectionHost, embedded: true); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Scoreboard radio refresh failed: " + ex.Message);
            }
        }

        internal static void InvalidateCardCache()
        {
            RadioHudUI.DetachEmbedded();
            if (rinkPane == null) return;
            try { rinkPane.Clear(); } catch { }
            roleSectionHost = null;
            rinkSectionHost = null;
            stripSectionHost = null;
            radioSectionHost = null;
        }

        internal static void OnScoreboardHidden()
        {
            if (!enabled) return;
            ClearBoardVerticalPosition();
            try { ShowScoreboardTab(); }
            catch { }
        }

        /// <summary>
        /// Called when Tab opens the scoreboard. Bumps the whole board up once based on
        /// roster size — tab switches must not touch vertical position again.
        /// </summary>
        /// <summary>Tab toggle: open on Rinks, press Tab again to close.</summary>
        internal static void HandleTabPressed(UIManager ui)
        {
            if (!enabled || MultiSheetClientSettings.SkipScoreboardUi ||
                !PracticeFlowClient.IsOnPracticeServer || ui == null) return;

            UIScoreboard scoreboard = ui.Scoreboard;
            if (scoreboard == null) return;

            if (scoreboard.IsVisible)
            {
                CloseBoard();
                return;
            }

            scoreboard.Show();
            ShowMenuTab();
        }

        internal static void OnScoreboardShown()
        {
            if (!enabled || MultiSheetClientSettings.SkipScoreboardUi ||
                !PracticeFlowClient.IsOnPracticeServer) return;
            ApplyBoardVerticalPosition();
            if (!menuPaneActive)
                ShowMenuTab();
        }

        internal static void OnDisconnected()
        {
            InvalidateCardCache();
            menuPaneActive = false;
            RemoveAllInjected();
            boardRoot = null;
            rinkPane = null;
            scoreboardTabButton = null;
            menuTabButton = null;
        }

        internal static void Teardown()
        {
            enabled = false;
            menuPaneActive = false;
            allowHideOnce = false;
            boardPositionApplied = false;
            appliedMarginTop = -1f;
            rinkSectionHost = null;
            stripSectionHost = null;
            radioSectionHost = null;
            scoreboardTabButton = null;
            menuTabButton = null;
            rinkPane = null;
            boardRoot = null;
            try { RemoveAllInjected(); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Scoreboard tab teardown failed: " + ex.Message);
            }
        }

        internal static void ResetForEnable()
        {
            enabled = true;
            boardPositionApplied = false;
            appliedMarginTop = -1f;
        }

        private static VisualElement GetScoreboardContainer(UIScoreboard scoreboard)
        {
            if (scoreboard == null) return null;
            try
            {
                if (ScoreboardField != null)
                {
                    VisualElement fromField = ScoreboardField.GetValue(scoreboard) as VisualElement;
                    if (fromField != null) return fromField;
                }
            }
            catch { }
            VisualElement view = scoreboard.View;
            return view != null ? view.Q("Scoreboard") : null;
        }

        private static VisualElement BuildTabBar()
        {
            VisualElement bar = new VisualElement { name = TabBarName };
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.flexShrink = 0;
            bar.style.width = new Length(100, LengthUnit.Percent);
            bar.style.backgroundColor = TabBarBg;
            bar.style.borderBottomWidth = 1;
            bar.style.borderBottomColor = TabBorder;
            bar.style.alignItems = Align.Stretch;

            scoreboardTabButton = MakeTopTab(ScoreboardTabName, "Scoreboard", SelectScoreboardTab);
            menuTabButton = MakeTopTab(MenuTabName, "Rinks", SelectMenuTab);

            bar.Add(scoreboardTabButton);
            bar.Add(menuTabButton);
            EnsureMenuTabVoteBadge(menuTabButton);
            ApplyVoteProgress(RinkStripVote.CurrentProgress);
            return bar;
        }

        private static Button MakeTopTab(string name, string label, Action onClick)
        {
            Button tab = new Button(delegate { onClick?.Invoke(); }) { name = name };
            tab.text = "";
            tab.style.flexGrow = 1;
            tab.style.flexBasis = 0;
            tab.style.height = new Length(100, LengthUnit.Percent);
            tab.style.marginLeft = 0;
            tab.style.marginRight = 0;
            tab.style.paddingLeft = 10;
            tab.style.paddingRight = 10;
            tab.style.backgroundColor = TabIdleBg;
            tab.style.flexDirection = FlexDirection.Row;
            tab.style.alignItems = Align.Center;
            tab.style.justifyContent = Justify.Center;
            tab.style.borderTopWidth = 0;
            tab.style.borderBottomWidth = 0;
            tab.style.borderLeftWidth = 0;
            tab.style.borderRightWidth = 1;
            tab.style.borderRightColor = TabBorder;
            tab.style.borderTopLeftRadius = 0;
            tab.style.borderTopRightRadius = 0;
            tab.style.borderBottomLeftRadius = 0;
            tab.style.borderBottomRightRadius = 0;

            Label text = new Label(label)
            {
                name = name + "Label",
                pickingMode = PickingMode.Ignore
            };
            text.style.fontSize = 13;
            text.style.unityFontStyleAndWeight = FontStyle.Bold;
            text.style.color = TabMuted;
            text.style.unityTextAlign = TextAnchor.MiddleCenter;
            tab.Add(text);
            return tab;
        }

        private static void SelectScoreboardTab()
        {
            if (!enabled) return;
            ShowScoreboardTab();
        }

        private static void SelectMenuTab()
        {
            if (!enabled) return;
            ShowMenuTab();
        }

        private static void ShowScoreboardTab()
        {
            menuPaneActive = false;

            if (rinkPane != null)
            {
                rinkPane.style.display = DisplayStyle.None;
                rinkPane.pickingMode = PickingMode.Ignore;
            }

            ClearMenuBoardHeight();
            if (!RinkMotdUI.IsVisible)
                RinkPreview.SetVisible(false);

            StyleTabActive(scoreboardTabButton, true);
            StyleTabActive(menuTabButton, false);
        }

        private static void ShowMenuTab()
        {
            if (MultiSheetClientSettings.SkipScoreboardUi) return;

            menuPaneActive = true;

            if (rinkPane != null)
            {
                rinkPane.style.display = DisplayStyle.Flex;
                rinkPane.pickingMode = PickingMode.Position;
                try { rinkPane.BringToFront(); } catch { }
            }

            ApplyMenuBoardHeight();
            if (!MultiSheetClientSettings.SkipMotdUi)
                RinkPreview.SetVisible(true);

            StyleTabActive(scoreboardTabButton, false);
            StyleTabActive(menuTabButton, true);

            RinkMotdPayload payload;
            if (RinkMotdUI.TryGetLastPayload(out payload) && payload != null)
                RebuildEmbed(payload);
            else
            {
                ShowLoading();
                RinkMotdService.ClientRequestShow();
            }
        }

        private static void ApplyMenuBoardHeight()
        {
            VisualElement board = boardRoot ?? rinkPane?.parent;
            if (board == null) return;
            try
            {
                // Three tile rows (7+ rinks) need more vertical room than two.
                bool dense = false;
                try
                {
                    dense = RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload)
                            && payload?.Rinks != null && payload.Rinks.Count > 6;
                }
                catch { }

                // Size to content only — growing toward the screen height just opens a
                // blank gap between the position row and the community footer.
                float target = dense ? MenuMinBoardHeight + 50f : MenuMinBoardHeight;
                try { target = Mathf.Min(target, Screen.height * 0.92f); }
                catch { }

                board.style.minHeight = target;
            }
            catch { }
        }

        private static void ClearMenuBoardHeight()
        {
            VisualElement board = boardRoot ?? rinkPane?.parent;
            if (board == null) return;
            try { board.style.minHeight = StyleKeyword.Null; }
            catch { }
        }

        private static void ApplyBoardVerticalPosition()
        {
            VisualElement board = boardRoot ?? rinkPane?.parent;
            if (board == null) return;

            int playerCount = CountConnectedPlayers();
            // Modest fixed lift for short rosters — never a worldBound delta (that
            // stacked on every reopen and jumped when minHeight changed between tabs).
            float margin = playerCount <= 2 ? 36f
                : playerCount <= 5 ? 24f
                : 12f;

            if (boardPositionApplied && Mathf.Approximately(appliedMarginTop, margin))
                return;

            boardPositionApplied = true;
            appliedMarginTop = margin;
            try { board.style.marginTop = margin; }
            catch { }
        }

        private static void ClearBoardVerticalPosition()
        {
            boardPositionApplied = false;
            appliedMarginTop = -1f;
            VisualElement board = boardRoot ?? rinkPane?.parent;
            if (board == null) return;
            try
            {
                board.style.marginTop = StyleKeyword.Null;
                board.style.top = StyleKeyword.Null;
            }
            catch { }
        }

        private static int CountConnectedPlayers()
        {
            try
            {
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm == null) return 0;
                int count = 0;
                foreach (Player player in pm.GetPlayers())
                    if (player != null) count++;
                return count;
            }
            catch { return 0; }
        }

        private static void ApplyPaneHitTesting()
        {
            if (rinkPane == null) return;
            if (menuPaneActive)
            {
                rinkPane.style.display = DisplayStyle.Flex;
                rinkPane.pickingMode = PickingMode.Position;
                try { rinkPane.BringToFront(); } catch { }
            }
            else
            {
                rinkPane.style.display = DisplayStyle.None;
                rinkPane.pickingMode = PickingMode.Ignore;
            }
        }

        private static void ShowLoading()
        {
            if (rinkPane == null) return;
            rinkPane.Clear();
            rinkSectionHost = null;

            Label loading = new Label("Loading rinks…")
            {
                pickingMode = PickingMode.Ignore
            };
            loading.style.color = TabMuted;
            loading.style.fontSize = 14;
            loading.style.unityTextAlign = TextAnchor.MiddleCenter;
            loading.style.flexGrow = 1;
            loading.style.alignSelf = Align.Center;
            loading.style.marginTop = 40;
            rinkPane.Add(loading);
        }

        private static void RebuildEmbed(RinkMotdPayload payload)
        {
            if (rinkPane == null || payload == null || !menuPaneActive) return;

            rinkPane.Clear();
            RinkPanelBuilder.Result built = RinkPanelBuilder.BuildEmbedded(
                payload, CreateEmbedCallbacks());

            if (built.Card != null)
                rinkPane.Add(built.Card);
            roleSectionHost = built.RoleSectionHost;
            rinkSectionHost = built.RinkSectionHost;
            stripSectionHost = built.StripSectionHost;
            radioSectionHost = built.RadioSectionHost;
        }

        private static void StyleTabActive(Button tab, bool active)
        {
            if (tab == null) return;
            tab.style.backgroundColor = active ? TabActiveBg : TabIdleBg;
            Label label = tab.Q<Label>(tab.name + "Label");
            if (label != null)
                label.style.color = active ? TabText : TabMuted;
        }

        private static void RemoveAllInjected()
        {
            UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
            if (ui == null) return;

            try
            {
                if (ui.Scoreboard != null)
                {
                    VisualElement board = GetScoreboardContainer(ui.Scoreboard);
                    if (board != null)
                        RemoveInjectedUnder(board);
                    else if (ui.Scoreboard.View != null)
                        RemoveInjectedUnder(ui.Scoreboard.View);
                }
            }
            catch { }

            try
            {
                if (ui.RootVisualElement != null)
                    RemoveInjectedUnder(ui.RootVisualElement);
            }
            catch { }
        }

        private static void RemoveInjectedUnder(VisualElement root)
        {
            if (root == null) return;
            ClearMenuBoardHeight();
            ClearBoardVerticalPosition();
            RemoveNamed(root, TabBarName);
            RemoveNamed(root, PaneName);
        }

        private static void RemoveNamed(VisualElement root, string name)
        {
            for (int guard = 0; guard < 16; guard++)
            {
                VisualElement el = root.Q(name);
                if (el == null) break;
                try { el.RemoveFromHierarchy(); } catch { break; }
            }
        }
    }

    [HarmonyPatch(typeof(UIScoreboard), "Initialize")]
    [HarmonyAfter(RinkScoreboardTab.StatsTooltipHarmonyId)]
    internal static class RinkScoreboardTabInitializePatch
    {
        private static void Postfix(UIScoreboard __instance)
        {
            try { RinkScoreboardTab.Install(__instance); }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Scoreboard tabs install failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(UIView), "Show")]
    [HarmonyAfter(RinkScoreboardTab.StatsTooltipHarmonyId)]
    internal static class RinkScoreboardTabShowPatch
    {
        private static void Postfix(UIView __instance)
        {
            UIScoreboard scoreboard = __instance as UIScoreboard;
            if (scoreboard == null) return;
            try
            {
                RinkScoreboardTab.Install(scoreboard);
                RinkScoreboardTab.OnScoreboardShown();
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(UIView), "Hide")]
    [HarmonyAfter(RinkScoreboardTab.StatsTooltipHarmonyId)]
    internal static class RinkScoreboardTabHidePatch
    {
        private static bool Prefix(UIView __instance)
        {
            if (!(__instance is UIScoreboard)) return true;
            return !RinkScoreboardTab.ShouldBlockHide();
        }

        private static void Postfix(UIView __instance, bool __runOriginal)
        {
            if (!__runOriginal || !(__instance is UIScoreboard)) return;
            try { RinkScoreboardTab.OnScoreboardHidden(); }
            catch { }
        }
    }

    /// <summary>
    /// Practice servers: Tab toggles the scoreboard on the Rinks pane (press again to close).
    /// </summary>
    [HarmonyPatch(typeof(UIManager), "OnScoreboardActionStarted")]
    internal static class PracticeScoreboardTabTogglePatch
    {
        private static bool Prefix(UIManager __instance)
        {
            if (!PracticeFlowClient.IsOnPracticeServer) return true;
            if (MultiSheetClientSettings.SkipScoreboardUi) return true;
            try
            {
                if (GlobalStateManager.UIState.Phase != UIPhase.Playing) return true;
            }
            catch { return true; }

            RinkScoreboardTab.HandleTabPressed(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIManager), "OnScoreboardActionCanceled")]
    internal static class PracticeScoreboardTabCancelPatch
    {
        private static bool Prefix()
        {
            if (MultiSheetClientSettings.SkipScoreboardUi) return true;
            return !PracticeFlowClient.IsOnPracticeServer;
        }
    }
}
