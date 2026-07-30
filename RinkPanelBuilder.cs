using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// UITK layout for the practice MOTD card: welcome text + one hoverable tile per
    /// rink (static preview, name, occupancy) that teleports the player on click.
    /// Visual language mirrors the PHL public MOTD (dark charcoal, silver accents).
    /// </summary>
    internal static class RinkPanelBuilder
    {
        internal static readonly Color OverlayBg = new Color(0f, 0f, 0f, 0.72f);
        internal static readonly Color PanelBg = new Color(0.075f, 0.075f, 0.075f, 1f);
        internal static readonly Color HeaderBg = new Color(0f, 0f, 0f, 1f);
        internal static readonly Color ElevatedBg = new Color(0.102f, 0.102f, 0.102f, 1f);
        internal static readonly Color BorderColor = new Color(0.165f, 0.165f, 0.165f, 1f);
        internal static readonly Color BorderStrong = new Color(0.251f, 0.251f, 0.251f, 1f);
        internal static readonly Color TextColor = new Color(0.929f, 0.929f, 0.929f, 1f);
        internal static readonly Color MutedText = new Color(0.580f, 0.580f, 0.580f, 1f);
        internal static readonly Color AccentBright = new Color(1f, 1f, 1f, 1f);
        internal static readonly Color CtaBg = new Color(0.22f, 0.62f, 0.30f, 1f);
        internal static readonly Color CtaHover = new Color(0.28f, 0.72f, 0.36f, 1f);
        internal static readonly Color CtaText = new Color(1f, 1f, 1f, 1f);
        internal static readonly Color FullRed = new Color(0.85f, 0.25f, 0.25f, 1f);
        internal static readonly Color ColumnRule = new Color(1f, 1f, 1f, 0.08f);
        internal static readonly Color ButtonBg = new Color(0.165f, 0.165f, 0.165f, 1f);
        internal static readonly Color ButtonHover = new Color(0.239f, 0.239f, 0.239f, 1f);
        internal static readonly Color BadgeCyan = new Color(0.20f, 0.75f, 0.85f, 1f);
        internal static readonly Color VoteBadgeOrange = new Color(0.90f, 0.49f, 0.13f, 1f);
        internal static readonly Color YoutubeBg = new Color(0.90f, 0.10f, 0.10f, 1f);
        internal static readonly Color YoutubeHover = new Color(1f, 0.20f, 0.20f, 1f);

        // PHL community links (same set as the PHL public MOTD footer).
        private const string PhlstatsUrl = "https://phlstats.com";
        private const string DiscordUrl = "https://discord.gg/puckhockeyleague";
        private const string YoutubeUrl = "https://www.youtube.com/@PHLnextshift";
        private const string TwitchUrl = "https://www.twitch.tv/puckhockeyleaguenetwork";

        /// <summary>Welcome overlay card sizing — reused by the scoreboard Rinks tab.</summary>
        internal const float PanelWidthPercent = 73.5f;
        internal const float PanelMaxWidth = 1071f;
        internal const float PanelMinHeight = 780f;
        internal const float PanelMaxHeightScreenFraction = 0.90f;

        internal sealed class Callbacks
        {
            public Action OnContinue;
            public Action<int> OnSelectRink;
            public Action<int> OnSelectRole;
            public Action<int, RinkStripMode> OnVoteStrip;
        }

        internal sealed class Result
        {
            public VisualElement Card;
            public VisualElement RoleSectionHost;
            public VisualElement SettingsSectionHost;
            public VisualElement RinkSectionHost;
            public VisualElement StripSectionHost;
            public VisualElement KeybindsSectionHost;
            public VisualElement RadioInfoSectionHost;
            public VisualElement RadioSectionHost;
        }

        /// <summary>Which rink the strip dropdown targets (0-based). Persists across repaints.</summary>
        internal static int SelectedStripRinkIndex = 0;

        /// <summary>Per-rink dropdown selection before confirm (persists across MOTD repaints).</summary>
        private static readonly Dictionary<int, RinkStripMode> StripDropdownSelection =
            new Dictionary<int, RinkStripMode>();

        /// <summary>Scoreboard Rinks tab — welcome-sized layout, fills the sized board pane.</summary>
        internal static Result BuildForScoreboard(RinkMotdPayload payload, Callbacks callbacks)
        {
            Result result = BuildCard(payload, callbacks, embedded: false, scoreboardHost: true);
            VisualElement card = result.Card;
            if (card == null) return result;

            card.style.flexGrow = 1;
            card.style.flexShrink = 1;
            card.style.width = new Length(100, LengthUnit.Percent);
            card.style.height = new Length(100, LengthUnit.Percent);
            card.style.maxWidth = StyleKeyword.Null;
            card.style.minHeight = StyleKeyword.Null;
            card.style.maxHeight = new Length(100, LengthUnit.Percent);
            return result;
        }

        private static Result BuildCard(
            RinkMotdPayload payload,
            Callbacks callbacks,
            bool embedded,
            bool scoreboardHost = false)
        {
            if (payload == null) payload = new RinkMotdPayload();
            if (callbacks == null) callbacks = new Callbacks();

            VisualElement card = new VisualElement();
            if (embedded)
            {
                card.style.flexGrow = 1;
                card.style.flexShrink = 1;
                card.style.minHeight = 0;
                card.style.width = new Length(100, LengthUnit.Percent);
                card.style.height = new Length(100, LengthUnit.Percent);
                card.style.maxHeight = new Length(100, LengthUnit.Percent);
            }
            else
            {
                card.style.width = new Length(PanelWidthPercent, LengthUnit.Percent);
                card.style.maxWidth = PanelMaxWidth;
                card.style.minHeight = PanelMinHeight;
                card.style.maxHeight = new Length(PanelMaxHeightScreenFraction * 100f, LengthUnit.Percent);
            }
            card.style.backgroundColor = PanelBg;
            card.style.flexDirection = FlexDirection.Column;
            SetBorder(card, embedded ? 0 : 1, BorderStrong);
            card.style.overflow = Overflow.Hidden;

            // Header: brand left, tag centered, red close X right.
            VisualElement header = new VisualElement();
            header.style.backgroundColor = HeaderBg;
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.paddingLeft = 22;
            header.style.paddingRight = 22;
            header.style.paddingTop = 10;
            header.style.paddingBottom = 10;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = BorderColor;
            header.style.flexShrink = 0;
            card.Add(header);

            VisualElement headerLeft = new VisualElement();
            headerLeft.style.flexDirection = FlexDirection.Row;
            headerLeft.style.alignItems = Align.Center;
            headerLeft.style.flexShrink = 0;
            headerLeft.style.width = 160;
            header.Add(headerLeft);

            Texture2D wordmark;
            if (PracticeMotdAssets.TryGetTexture(PracticeMotdAssets.PhlWordmark, out wordmark))
            {
                VisualElement logo = new VisualElement();
                logo.style.width = 160;
                logo.style.height = 40;
                logo.style.backgroundColor = Color.clear;
                logo.style.backgroundImage = new StyleBackground(wordmark);
#pragma warning disable CS0618
                logo.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
#pragma warning restore CS0618
                logo.tooltip = "Open phlstats.com";
                MakeClickable(logo, () => OpenExternalUrl(PhlstatsUrl));
                headerLeft.Add(logo);
            }
            else
            {
                Label brand = MakeLabel("PHL", 22, AccentBright, FontStyle.Bold);
                try { brand.style.letterSpacing = 4; } catch { }
                brand.tooltip = "Open phlstats.com";
                MakeClickable(brand, () => OpenExternalUrl(PhlstatsUrl));
                headerLeft.Add(brand);
            }

            Label headerTag = MakeLabel(
                string.IsNullOrWhiteSpace(payload.Title)
                    ? "Welcome to PHL MultiSheet Practice"
                    : payload.Title,
                embedded ? 14 : 18,
                TextColor,
                FontStyle.Bold);
            headerTag.style.position = Position.Absolute;
            headerTag.style.left = 0;
            headerTag.style.right = 0;
            headerTag.style.unityTextAlign = TextAnchor.MiddleCenter;
            headerTag.pickingMode = PickingMode.Ignore;
            header.Add(headerTag);

            Color closeNormal = new Color(0.90f, 0.22f, 0.22f, 1f);
            Color closeHover = new Color(1f, 0.35f, 0.35f, 1f);
            Label closeX = MakeLabel("✕", 18, closeNormal, FontStyle.Bold);
            closeX.tooltip = scoreboardHost
                ? "Close scoreboard"
                : embedded
                    ? "Back to scoreboard"
                    : "Close";
            closeX.style.unityTextAlign = TextAnchor.MiddleCenter;
            closeX.style.width = 28;
            closeX.style.height = 28;
            closeX.style.flexShrink = 0;
            MakeClickable(closeX, callbacks.OnContinue ?? (() => { }));
            closeX.RegisterCallback<MouseEnterEvent>(delegate { closeX.style.color = closeHover; });
            closeX.RegisterCallback<MouseLeaveEvent>(delegate { closeX.style.color = closeNormal; });
            header.Add(closeX);

            // Scroll when Keybinds/Radio expand — never flex-shrink the rink grid (strip
            // buttons would collide with Position / Lighting headings).
            ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.flexShrink = 1;
            scroll.style.minHeight = 0;
            scroll.style.maxHeight = new Length(100, LengthUnit.Percent);
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            ConfigureOutwardScrollbar(scroll);
            VisualElement contentHost = scroll;
            card.Add(contentHost);

            VisualElement body = new VisualElement();
            body.style.flexShrink = 0;
            body.style.flexGrow = 0;
            body.style.paddingLeft = embedded ? 10 : 26;
            body.style.paddingRight = embedded ? 10 : 26;
            body.style.paddingTop = embedded ? 6 : 12;
            body.style.paddingBottom = embedded ? 2 : 8;
            contentHost.Add(body);

            VisualElement rinkSectionHost = new VisualElement();
            rinkSectionHost.style.flexShrink = 0;
            if (embedded)
            {
                rinkSectionHost.style.flexGrow = 1;
                rinkSectionHost.style.minHeight = 0;
            }
            else
            {
                rinkSectionHost.style.flexGrow = 0;
            }
            body.Add(rinkSectionHost);
            FillRinkSection(rinkSectionHost, payload, callbacks, embedded);

            // Position + Lighting inside a Settings collapsible (matches canvas).
            VisualElement settingsSectionHost = new VisualElement();
            settingsSectionHost.style.flexShrink = 0;
            settingsSectionHost.style.marginTop = embedded ? 10 : 16;
            settingsSectionHost.style.marginBottom = 0;
            body.Add(settingsSectionHost);

            VisualElement roleSectionHost = null;
            VisualElement lightingSectionHost = null;
            RinkPanelCollapsible.FillSettingsSection(
                settingsSectionHost,
                payload,
                callbacks,
                embedded,
                out roleSectionHost,
                out lightingSectionHost);

            VisualElement stripSectionHost = new VisualElement();
            stripSectionHost.style.display = DisplayStyle.None;
            stripSectionHost.style.flexShrink = 0;
            body.Add(stripSectionHost);

            VisualElement collapsibleHost = new VisualElement();
            collapsibleHost.style.flexShrink = 0;
            collapsibleHost.style.marginTop = 0;
            collapsibleHost.style.marginBottom = embedded ? 6 : 10;
            body.Add(collapsibleHost);

            VisualElement keybindsSectionHost = new VisualElement();
            keybindsSectionHost.style.flexShrink = 0;
            keybindsSectionHost.style.marginBottom = 0;
            collapsibleHost.Add(keybindsSectionHost);
            RinkPanelCollapsible.FillKeybindsSection(keybindsSectionHost, embedded);

            VisualElement radioInfoSectionHost = new VisualElement();
            radioInfoSectionHost.style.flexShrink = 0;
            collapsibleHost.Add(radioInfoSectionHost);
            RinkPanelCollapsible.FillRadioInfoSection(radioInfoSectionHost, embedded);

            // Sticky footer — radio left, community right; Continue only on fullscreen welcome.
            VisualElement footer = new VisualElement();
            footer.style.flexShrink = 0;
            footer.style.flexGrow = 0;
            footer.style.paddingLeft = embedded ? 10 : 26;
            footer.style.paddingRight = embedded ? 10 : 26;
            footer.style.paddingTop = embedded ? 6 : 8;
            footer.style.paddingBottom = embedded ? 8 : 10;
            footer.style.backgroundColor = PanelBg;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = BorderColor;
            footer.style.alignItems = Align.Stretch;
            card.Add(footer);

            VisualElement footerRow = new VisualElement();
            footerRow.style.flexDirection = FlexDirection.Row;
            footerRow.style.flexWrap = Wrap.NoWrap;
            footerRow.style.justifyContent = Justify.SpaceBetween;
            footerRow.style.alignItems = Align.Center;
            footerRow.style.width = new Length(100, LengthUnit.Percent);
            footerRow.style.minHeight = 36;
            footer.Add(footerRow);

            VisualElement radioSectionHost = new VisualElement();
            radioSectionHost.style.flexShrink = 0;
            radioSectionHost.style.flexGrow = 0;
            radioSectionHost.style.width = 348;
            radioSectionHost.style.minWidth = 348;
            radioSectionHost.style.maxWidth = 348;
            radioSectionHost.style.marginRight = 8;
            footerRow.Add(radioSectionHost);
            FillRadioSection(radioSectionHost, embedded);

            VisualElement communityBlock = new VisualElement();
            communityBlock.style.flexDirection = FlexDirection.Row;
            communityBlock.style.alignItems = Align.Center;
            communityBlock.style.flexShrink = 0;
            footerRow.Add(communityBlock);

            Label community = MakeLabel("JOIN THE COMMUNITY", 10, MutedText, FontStyle.Bold);
            try { community.style.letterSpacing = 1; } catch { }
            community.style.marginRight = 8;
            community.style.whiteSpace = WhiteSpace.NoWrap;
            communityBlock.Add(community);

            VisualElement linkRow = new VisualElement();
            linkRow.style.flexDirection = FlexDirection.Row;
            linkRow.style.flexWrap = Wrap.NoWrap;
            linkRow.style.justifyContent = Justify.Center;
            linkRow.style.alignItems = Align.Center;
            linkRow.style.flexShrink = 0;
            communityBlock.Add(linkRow);

            AddCommunityButton(linkRow, "phlstats.com", PhlstatsUrl, PracticeMotdAssets.PhlstatsIcon);
            AddCommunityButton(linkRow, "Discord", DiscordUrl, PracticeMotdAssets.DiscordIcon);
            AddCommunityButton(linkRow, "YouTube", YoutubeUrl, PracticeMotdAssets.YoutubeIcon);
            AddCommunityButton(linkRow, "Twitch", TwitchUrl, PracticeMotdAssets.TwitchIcon);

            if (!embedded && !scoreboardHost)
            {
                Button continueButton = MakeButton("Continue", CtaBg, CtaHover, callbacks.OnContinue ?? (() => { }));
                continueButton.style.color = CtaText;
                continueButton.style.unityFontStyleAndWeight = FontStyle.Bold;
                continueButton.style.height = 36;
                continueButton.style.minWidth = 100;
                continueButton.style.flexShrink = 0;
                continueButton.style.marginLeft = 8;
                footerRow.Add(continueButton);
            }

            return new Result
            {
                Card = card,
                RoleSectionHost = roleSectionHost,
                SettingsSectionHost = settingsSectionHost,
                RinkSectionHost = rinkSectionHost,
                StripSectionHost = stripSectionHost,
                KeybindsSectionHost = keybindsSectionHost,
                RadioInfoSectionHost = radioInfoSectionHost,
                RadioSectionHost = radioSectionHost
            };
        }

        internal static void FillKeybindsSection(VisualElement host, bool embedded)
        {
            RinkPanelCollapsible.FillKeybindsSection(host, embedded);
        }

        internal static void FillRadioInfoSection(VisualElement host, bool embedded)
        {
            RinkPanelCollapsible.FillRadioInfoSection(host, embedded);
        }

        internal static void OpenExternalUrlPublic(string url)
        {
            OpenExternalUrl(url);
        }

        /// <summary>phlstats training radio — compact ♪ chip in Rinks tab / MOTD, expands on click.</summary>
        internal static void FillRadioSection(VisualElement host, bool embedded)
        {
            if (host == null || !FlamiePracFeatures.EnableRadio)
                return;

            host.Clear();
            RadioHudUI.DetachEmbedded();
            RadioSync.EnsureClientRadio(TrainingSync.Instance);
            RadioHudUI.AttachFooterStrip(host);
        }

        /// <summary>Legacy strip section — tools votes live on each rink tile now.</summary>
        internal static void FillStripSection(
            VisualElement host,
            RinkMotdPayload payload,
            Callbacks callbacks,
            bool embedded)
        {
            if (host == null) return;
            host.Clear();
            host.style.display = DisplayStyle.None;
        }

        private static RinkStripMode GetStripMode(RinkMotdPayload payload, int rinkIndex)
        {
            if (payload?.StripModes != null && rinkIndex >= 0 && rinkIndex < payload.StripModes.Count)
                return payload.StripModes[rinkIndex];
            return rinkIndex == 0 ? RinkStripMode.PhlTools : RinkStripMode.Empty;
        }

        /// <summary>Skater / goalie toggle row — repainted when status arrives.</summary>
        internal static void FillRoleSection(
            VisualElement host,
            RinkMotdPayload payload,
            Callbacks callbacks,
            bool embedded)
        {
            if (host == null || payload == null) return;
            if (callbacks == null) callbacks = new Callbacks();
            host.Clear();

            bool isGoalie = payload.LocalRole > 0;

            Label heading = MakeLabel("Position", embedded ? 13 : 14, MutedText, FontStyle.Bold);
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            heading.style.marginTop = embedded ? 6 : 8;
            heading.style.marginBottom = 6;
            host.Add(heading);

            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.alignItems = Align.Stretch;
            row.style.flexGrow = 0;
            host.Add(row);

            row.Add(MakeRoleButton("Skater", !isGoalie, embedded, () => callbacks.OnSelectRole?.Invoke(0)));
            row.Add(MakeRoleButton("Goalie", isGoalie, embedded, () => callbacks.OnSelectRole?.Invoke(1)));
        }

        private static void AddControlsColumnSeparator(VisualElement host, bool embedded)
        {
            VisualElement sep = new VisualElement();
            sep.style.height = 1;
            sep.style.backgroundColor = ColumnRule;
            sep.style.marginTop = embedded ? 6 : 8;
            sep.style.marginBottom = embedded ? 4 : 6;
            host.Add(sep);
        }

        /// <summary>
        /// Render-scope + presentation toggles (client-only) — stacked in the lighting column.
        /// </summary>
        private static void AddPerformanceToggleRow(VisualElement lightingHost, bool embedded)
        {
            if (lightingHost == null) return;

            AddControlsColumnSeparator(lightingHost, embedded);

            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Stretch;
            row.style.flexShrink = 0;
            row.style.width = new Length(100, LengthUnit.Percent);
            lightingHost.Add(row);

            bool renderAll = PracticePresentation.RenderAllRinks;
            bool limitChanges = !PracticePresentation.AllowRinkChanges;
            Button renderBtn = MakeRoleButton(
                renderAll ? "Render All Rinks: ON" : "Render All Rinks: OFF",
                renderAll,
                embedded,
                delegate
                {
                    PracticePresentation.SetRenderAllRinks(!PracticePresentation.RenderAllRinks);
                    FillLightingSection(lightingHost, embedded);
                });
            StyleLightingGridButton(renderBtn, embedded, leftCell: true);
            row.Add(renderBtn);

            Button changesBtn = MakeRoleButton(
                "Limit Rink Changes",
                limitChanges,
                embedded,
                delegate
                {
                    if (PracticePresentation.AllowRinkChanges)
                    {
                        PracticePresentation.SetAllowRinkChanges(false);
                        PracticePresentation.SetRenderAllRinks(false);
                    }
                    else
                    {
                        PracticePresentation.SetAllowRinkChanges(true);
                    }
                    FillLightingSection(lightingHost, embedded);
                });
            StyleLightingGridButton(changesBtn, embedded, leftCell: false);
            row.Add(changesBtn);
        }

        private static void StyleLightingGridButton(Button button, bool embedded, bool leftCell)
        {
            if (button == null) return;

            float h = embedded ? 34 : 40;
            button.style.flexGrow = 1;
            button.style.flexBasis = 0;
            button.style.flexShrink = 1;
            button.style.minWidth = 0;
            button.style.maxWidth = StyleKeyword.Null;
            button.style.width = StyleKeyword.Null;
            button.style.minHeight = h;
            button.style.height = h;
            button.style.maxHeight = h;
            button.style.marginTop = 0;
            button.style.marginBottom = 0;
            button.style.marginLeft = 0;
            button.style.marginRight = leftCell ? 4 : 0;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            button.style.whiteSpace = WhiteSpace.NoWrap;
            button.style.overflow = Overflow.Hidden;
        }

        /// <summary>
        /// Day/night opt-in plus a 0–24h drag timeline. Client-side only.
        /// </summary>
        internal static void FillLightingSection(VisualElement host, bool embedded)
        {
            if (host == null) return;
            host.Clear();

            host.style.flexShrink = 0;
            host.style.minHeight = StyleKeyword.Null;

            bool arenaOn = ArenaLighting.ArenaLightingEnabled;
            bool dayNight = ArenaLighting.DayNightEnabled;

            Label heading = MakeLabel("Lighting & Performance", embedded ? 13 : 14, MutedText, FontStyle.Bold);
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            heading.style.marginTop = embedded ? 6 : 8;
            heading.style.marginBottom = 6;
            heading.style.flexShrink = 0;
            host.Add(heading);

            VisualElement toggleRow = new VisualElement();
            toggleRow.style.flexDirection = FlexDirection.Row;
            toggleRow.style.alignItems = Align.Stretch;
            toggleRow.style.flexShrink = 0;
            toggleRow.style.width = new Length(100, LengthUnit.Percent);
            host.Add(toggleRow);

            // FPS escape: off = every cloned sheet light disabled and stock sun/sky/ambient
            // restored. On = normal MultiSheet lighting with the local rink's rig only.
            Button arenaToggle = MakeRoleButton(
                arenaOn ? "Arena Lighting: ON" : "Arena Lighting: OFF",
                arenaOn,
                embedded,
                delegate
                {
                    ArenaLighting.SetArenaLightingEnabled(!ArenaLighting.ArenaLightingEnabled);
                    FillLightingSection(host, embedded);
                });
            StyleLightingGridButton(arenaToggle, embedded, leftCell: true);
            toggleRow.Add(arenaToggle);

            // Day/night is independent of render scope and Limit Rink Changes.
            Button toggle = MakeRoleButton(
                dayNight ? "Day/Night Cycle: ON" : "Day/Night Cycle: OFF",
                dayNight,
                embedded,
                delegate
                {
                    ArenaLighting.SetDayNightEnabled(!ArenaLighting.DayNightEnabled);
                    FillLightingSection(host, embedded);
                });
            StyleLightingGridButton(toggle, embedded, leftCell: false);
            toggleRow.Add(toggle);

            // Performance row sits above the timeline so the invisible timeline slot
            // never steals clicks from Render All / Limit Rink Changes.
            AddPerformanceToggleRow(host, embedded);

            // Timeline only when day/night cycle is on — fully removed from layout when off.
            bool timelineLive = dayNight;
            VisualElement timeRow = new VisualElement();
            timeRow.style.flexDirection = FlexDirection.Row;
            timeRow.style.alignItems = Align.Center;
            timeRow.style.justifyContent = Justify.Center;
            timeRow.style.flexShrink = 0;
            timeRow.style.width = new Length(100, LengthUnit.Percent);
            if (timelineLive)
            {
                timeRow.style.display = DisplayStyle.Flex;
                timeRow.style.height = embedded ? 26 : 30;
                timeRow.style.marginTop = 8;
                timeRow.pickingMode = PickingMode.Position;
            }
            else
            {
                timeRow.style.display = DisplayStyle.None;
                timeRow.pickingMode = PickingMode.Ignore;
            }
            host.Add(timeRow);

            Label clock = MakeLabel(ArenaLighting.FormatHour(ArenaLighting.Hour), embedded ? 12 : 14, TextColor, FontStyle.Bold);
            clock.style.minWidth = 42;
            clock.style.unityTextAlign = TextAnchor.MiddleCenter;
            timeRow.Add(clock);

            TimelineSlider timeline = new TimelineSlider(
                ArenaLighting.Hour,
                hour =>
                {
                    ArenaLighting.SetManualHour(hour);
                    clock.text = ArenaLighting.FormatHour(hour);
                });
            timeline.style.flexGrow = 1;
            timeline.style.flexShrink = 1;
            timeline.style.minWidth = embedded ? 80 : 120;
            timeline.style.height = embedded ? 20 : 24;
            timeline.style.marginLeft = 6;
            timeline.style.marginRight = 6;
            timeRow.Add(timeline);

            Button auto = null;
            auto = MakeButton("Auto", ButtonBg, ButtonHover, delegate
            {
                ArenaLighting.SetManualHour(-1f);
                timeline.SetHour(ArenaLighting.Hour);
                clock.text = ArenaLighting.FormatHour(ArenaLighting.Hour);
                auto.style.color = MutedText;
            });
            auto.style.height = embedded ? 26 : 30;
            auto.style.minWidth = 56;
            auto.style.fontSize = embedded ? 11 : 12;
            auto.style.color = ArenaLighting.IsManualHour ? TextColor : MutedText;
            auto.pickingMode = timelineLive ? PickingMode.Position : PickingMode.Ignore;
            timeRow.Add(auto);

            // Follow the wall clock until the user pins an hour; also sync /timeset.
            timeline.schedule.Execute((Action)delegate
            {
                if (!ArenaLighting.DayNightEnabled) return;
                if (ArenaLighting.IsManualHour)
                {
                    auto.style.color = TextColor;
                    return;
                }
                float now = ArenaLighting.Hour;
                timeline.SetHour(now);
                clock.text = ArenaLighting.FormatHour(now);
                auto.style.color = MutedText;
            }).Every(1000);
        }

        /// <summary>
        /// Visible 0–24h drag bar. UITK's stock Slider is nearly invisible on our dark
        /// charcoal panel (no USS), so this draws its own track + thumb.
        /// </summary>
        private sealed class TimelineSlider : VisualElement
        {
            private readonly VisualElement track;
            private readonly VisualElement fill;
            private readonly VisualElement thumb;
            private readonly Action<float> onChanged;
            private float hour;
            private bool dragging;

            internal TimelineSlider(float initialHour, Action<float> onChanged)
            {
                this.onChanged = onChanged;
                hour = Mathf.Clamp(initialHour, 0f, 24f);

                style.justifyContent = Justify.Center;
                style.alignItems = Align.Center;

                track = new VisualElement();
                track.style.position = Position.Absolute;
                track.style.left = 0;
                track.style.right = 0;
                track.style.height = 6;
                track.style.top = new Length(50, LengthUnit.Percent);
                track.style.marginTop = -3;
                track.style.backgroundColor = new Color(0.22f, 0.22f, 0.24f, 1f);
                track.style.borderTopLeftRadius = 3;
                track.style.borderTopRightRadius = 3;
                track.style.borderBottomLeftRadius = 3;
                track.style.borderBottomRightRadius = 3;
                Add(track);

                // Hour ticks at 0 / 6 / 12 / 18 / 24.
                for (int t = 0; t <= 4; t++)
                {
                    VisualElement tick = new VisualElement();
                    tick.style.position = Position.Absolute;
                    tick.style.width = 2;
                    tick.style.height = 10;
                    tick.style.top = new Length(50, LengthUnit.Percent);
                    tick.style.marginTop = -5;
                    tick.style.left = new Length(t * 25f, LengthUnit.Percent);
                    tick.style.marginLeft = -1;
                    tick.style.backgroundColor = new Color(0.40f, 0.40f, 0.42f, 1f);
                    tick.pickingMode = PickingMode.Ignore;
                    Add(tick);
                }

                fill = new VisualElement();
                fill.style.position = Position.Absolute;
                fill.style.left = 0;
                fill.style.height = 6;
                fill.style.top = new Length(50, LengthUnit.Percent);
                fill.style.marginTop = -3;
                fill.style.backgroundColor = new Color(0.22f, 0.52f, 0.62f, 0.85f);
                fill.style.borderTopLeftRadius = 3;
                fill.style.borderBottomLeftRadius = 3;
                fill.pickingMode = PickingMode.Ignore;
                Add(fill);

                thumb = new VisualElement();
                thumb.style.position = Position.Absolute;
                thumb.style.width = 14;
                thumb.style.height = 14;
                thumb.style.top = new Length(50, LengthUnit.Percent);
                thumb.style.marginTop = -7;
                thumb.style.backgroundColor = new Color(0.85f, 0.90f, 0.95f, 1f);
                thumb.style.borderTopLeftRadius = 7;
                thumb.style.borderTopRightRadius = 7;
                thumb.style.borderBottomLeftRadius = 7;
                thumb.style.borderBottomRightRadius = 7;
                thumb.pickingMode = PickingMode.Ignore;
                Add(thumb);

                RegisterCallback<GeometryChangedEvent>(delegate { LayoutThumb(); });
                RegisterCallback<PointerDownEvent>(OnPointerDown);
                RegisterCallback<PointerMoveEvent>(OnPointerMove);
                RegisterCallback<PointerUpEvent>(OnPointerUp);
                RegisterCallback<PointerCaptureOutEvent>(delegate { dragging = false; });
            }

            internal void SetHour(float value)
            {
                hour = Mathf.Clamp(value, 0f, 24f);
                LayoutThumb();
            }

            private void LayoutThumb()
            {
                float w = layout.width;
                if (w <= 1f) return;
                float x = (hour / 24f) * w;
                fill.style.width = x;
                thumb.style.left = x - 7f;
            }

            private void OnPointerDown(PointerDownEvent evt)
            {
                dragging = true;
                this.CapturePointer(evt.pointerId);
                ApplyFromLocalX(evt.localPosition.x);
                evt.StopPropagation();
            }

            private void OnPointerMove(PointerMoveEvent evt)
            {
                if (!dragging) return;
                ApplyFromLocalX(evt.localPosition.x);
                evt.StopPropagation();
            }

            private void OnPointerUp(PointerUpEvent evt)
            {
                if (!dragging) return;
                dragging = false;
                if (this.HasPointerCapture(evt.pointerId))
                    this.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            }

            private void ApplyFromLocalX(float localX)
            {
                float w = layout.width;
                if (w <= 1f) return;
                float next = Mathf.Clamp01(localX / w) * 24f;
                if (Mathf.Abs(next - hour) < 0.001f) return;
                hour = next;
                LayoutThumb();
                onChanged?.Invoke(hour);
            }
        }

        private static Button MakeRoleButton(string label, bool active, bool embedded, Action onClick)
        {
            Color idleBg = new Color(0.12f, 0.12f, 0.13f, 1f);
            Color activeBg = new Color(0.22f, 0.52f, 0.62f, 1f);
            Color hoverBg = active ? activeBg : new Color(0.18f, 0.18f, 0.20f, 1f);
            Color baseBg = active ? activeBg : idleBg;

            Button button = new Button(onClick ?? (() => { })) { text = label };
            button.style.minWidth = embedded ? 96 : 120;
            button.style.height = embedded ? 34 : 40;
            button.style.marginLeft = 4;
            button.style.marginRight = 4;
            button.style.fontSize = embedded ? 12 : 14;
            button.style.color = active ? CtaText : TextColor;
            button.style.backgroundColor = baseBg;
            button.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.borderTopWidth = 1;
            button.style.borderRightWidth = 1;
            button.style.borderBottomWidth = 1;
            button.style.borderLeftWidth = 1;
            button.style.borderTopColor = active ? CtaBg : BorderStrong;
            button.style.borderRightColor = active ? CtaBg : BorderStrong;
            button.style.borderBottomColor = active ? CtaBg : BorderStrong;
            button.style.borderLeftColor = active ? CtaBg : BorderStrong;
            button.RegisterCallback<MouseEnterEvent>(delegate { button.style.backgroundColor = hoverBg; });
            button.RegisterCallback<MouseLeaveEvent>(delegate { button.style.backgroundColor = baseBg; });
            return button;
        }

        /// <summary>
        /// (Re)paint the rink tiles. Called on build and whenever occupancy or the local
        /// highlight changes — not when preview textures finish capturing (see
        /// UpdatePreviewTextures).
        /// </summary>
        internal static void FillRinkSection(
            VisualElement host,
            RinkMotdPayload payload,
            Callbacks callbacks,
            bool embedded)
        {
            if (host == null || payload == null) return;
            if (callbacks == null) callbacks = new Callbacks();
            host.Clear();

            if (embedded)
            {
                host.style.flexGrow = 1;
                host.style.flexShrink = 0;
                host.style.minHeight = 0;
            }

            Label heading = MakeLabel("Choose Your Rink", embedded ? 15 : 18, TextColor, FontStyle.Bold);
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            heading.style.marginBottom = embedded ? 4 : 8;
            heading.style.flexShrink = 0;
            host.Add(heading);

            const int perRow = 3;
            int localRink = GetLocalRinkIndex(payload);
            int total = payload.Rinks.Count;
            bool dense = total > 6;
            int previewH = GetRinkPreviewHeight(dense, embedded);
            int stripH = embedded ? 28 : 32;
            int hereBannerH = embedded ? 18 : 22;
            int rowMinH = previewH + stripH + 8;
            host.style.minHeight = ComputeRinkSectionMinHeight(total, embedded);

            for (int start = 0; start < total; start += perRow)
            {
                int end = Mathf.Min(start + perRow, total);
                VisualElement row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.flexWrap = Wrap.NoWrap;
                row.style.width = new Length(100, LengthUnit.Percent);
                row.style.justifyContent = Justify.SpaceBetween;
                row.style.alignItems = Align.FlexStart;
                row.style.marginBottom = end < total ? (embedded ? 6 : 10) : 0;
                row.style.flexShrink = 0;
                if (embedded)
                {
                    row.style.flexGrow = 1;
                    row.style.minHeight = rowMinH;
                }
                else
                {
                    row.style.minHeight = rowMinH;
                }
                host.Add(row);

                for (int i = start; i < end; i++)
                {
                    bool isLast = i == end - 1;
                    row.Add(MakeRinkTile(payload, i, localRink, callbacks, embedded, isLast, hereBannerH));
                }
            }
        }

        private static int GetRinkPreviewHeight(bool dense, bool embedded)
        {
            if (embedded)
                return dense ? 96 : 112;
            return dense ? 156 : 184;
        }

        /// <summary>
        /// Keep the vertical scroller from stealing content width — overlay on the right edge.
        /// </summary>
        private static void ConfigureOutwardScrollbar(ScrollView scroll)
        {
            if (scroll == null) return;
            scroll.schedule.Execute(() =>
            {
                try
                {
                    VisualElement scroller = scroll.verticalScroller;
                    if (scroller == null) return;
                    scroller.style.position = Position.Absolute;
                    scroller.style.right = 0;
                    scroller.style.top = 0;
                    scroller.style.bottom = 0;
                    scroller.style.width = 12;
                    VisualElement viewport = scroll.contentViewport;
                    if (viewport != null)
                    {
                        viewport.style.marginRight = 0;
                        viewport.style.paddingRight = 0;
                    }
                }
                catch { }
            }).ExecuteLater(0);
        }

        private static int ComputeRinkSectionMinHeight(int rinkCount, bool embedded)
        {
            if (rinkCount <= 0) return embedded ? 120 : 140;
            bool dense = rinkCount > 6;
            int previewH = GetRinkPreviewHeight(dense, embedded);
            int stripH = embedded ? 28 : 32;
            int rowMinH = previewH + stripH + 8;
            int rows = (rinkCount + 2) / 3;
            int heading = embedded ? 28 : 34;
            int rowGap = embedded ? 6 : 10;
            return heading + rows * rowMinH + Mathf.Max(0, rows - 1) * rowGap;
        }

        /// <summary>
        /// Swap preview textures on existing tiles without clearing the section.
        /// Rebuilding here would steal UITK focus and force a double-click to pick a rink.
        /// </summary>
        internal static void UpdatePreviewTextures(VisualElement host)
        {
            if (host == null) return;
            int tileIndex = 0;
            foreach (VisualElement child in host.Children())
            {
                if (child is Label) continue;
                foreach (VisualElement column in child.Children())
                {
                    VisualElement preview = column.Q<VisualElement>("RinkPreview_" + tileIndex);
                    if (preview == null)
                    {
                        VisualElement tile = column.Q<VisualElement>("RinkTile_" + tileIndex);
                        preview = tile != null && tile.childCount > 0 ? tile[0] : null;
                    }
                    if (preview == null) continue;
                    ApplyPreviewSurface(preview, tileIndex);
                    tileIndex++;
                }
            }
        }

        /// <summary>Claim keyboard/pointer focus once the overlay is in the panel.</summary>
        internal static void FocusForInput(VisualElement root)
        {
            if (root == null) return;
            root.focusable = true;
            root.pickingMode = PickingMode.Position;
            root.schedule.Execute(() =>
            {
                try { root.Focus(); }
                catch { }
            }).ExecuteLater(1);
        }

        /// <summary>
        /// The rink the local player is standing on, or -1 before they have spawned —
        /// no tile shows "YOU ARE HERE" until a rink has actually been picked.
        /// </summary>
        internal static int GetLocalRinkIndex(RinkMotdPayload payload)
        {
            Vector3? pos = RinkLocator.LocalPlayerBodyPosition();
            return pos.HasValue ? RinkLocator.NearestRink(payload, pos.Value) : -1;
        }

        private static VisualElement MakeRinkTile(
            RinkMotdPayload payload,
            int index,
            int localRink,
            Callbacks callbacks,
            bool embedded,
            bool isLast,
            int hereBannerH)
        {
            RinkStatusEntry entry = payload.Rinks[index];
            bool isHere = index == localRink;
            int rinkCapacity = payload.CapacityForRink(index);
            bool isFull = rinkCapacity > 0 && entry.Count >= rinkCapacity && !isHere;

            VisualElement column = new VisualElement();
            column.style.flexGrow = 1;
            column.style.flexShrink = 0;
            column.style.flexBasis = 0;
            column.style.minWidth = 0;
            column.style.marginRight = isLast ? 0 : (embedded ? 6 : 10);
            column.style.flexDirection = FlexDirection.Column;
            column.style.alignItems = Align.Stretch;

            VisualElement tile = BuildRinkTileSurface(
                payload, index, entry, isHere, isFull, rinkCapacity, callbacks, embedded, hereBannerH);
            column.Add(tile);
            VisualElement stripBtn = MakeRinkStripButton(payload, index, callbacks, embedded);
            stripBtn.style.marginTop = 4;
            column.Add(stripBtn);
            return column;
        }

        private static VisualElement BuildRinkTileSurface(
            RinkMotdPayload payload,
            int index,
            RinkStatusEntry entry,
            bool isHere,
            bool isFull,
            int rinkCapacity,
            Callbacks callbacks,
            bool embedded,
            int hereBannerH)
        {
            RinkStripMode stripMode = GetStripMode(payload, index);
            string capacityHint = RinkStripModeUtil.GetJoinCapacityHint(stripMode);
            // Full-bleed preview; tile height follows preview + strip only (no flex-grow gap).
            bool dense = payload.Rinks.Count > 6;
            int previewH = GetRinkPreviewHeight(dense, embedded);

            VisualElement tile = new VisualElement();
            tile.name = "RinkTile_" + index;
            tile.style.minWidth = 0;
            tile.style.backgroundColor = ElevatedBg;
            tile.style.flexDirection = FlexDirection.Column;
            tile.style.overflow = Overflow.Hidden;
            tile.style.flexShrink = 0;
            tile.style.flexGrow = 0;
            // Constant 2 px border — UITK borders are part of layout, so hover must
            // only swap the color, never the width, or the whole grid shifts around.
            Color idleBorder = isHere ? CtaBg : isFull ? FullRed : BorderStrong;
            SetBorder(tile, 2, idleBorder);

            VisualElement preview = new VisualElement();
            preview.name = "RinkPreview_" + index;
            preview.style.flexGrow = 0;
            preview.style.flexShrink = 0;
            preview.style.height = previewH;
            preview.style.minHeight = previewH;
            preview.style.backgroundColor = new Color(0.03f, 0.03f, 0.035f, 1f);
            preview.style.overflow = Overflow.Hidden;
            preview.pickingMode = PickingMode.Ignore;
            ApplyPreviewSurface(preview, index);
            tile.Add(preview);

            // Name (top-left) + occupancy (top-right) over the photo.
            VisualElement overlay = new VisualElement();
            overlay.name = "RinkOverlay_" + index;
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0;
            overlay.style.right = 0;
            overlay.style.top = 0;
            overlay.style.bottom = 0;
            overlay.pickingMode = PickingMode.Ignore;
            tile.Add(overlay);

            Color nameColor = isHere ? CtaText : TextColor;
            Color countColor = isFull ? FullRed : isHere ? CtaBg : TextColor;

            VisualElement topLeftRow = new VisualElement();
            topLeftRow.style.position = Position.Absolute;
            topLeftRow.style.left = 6;
            topLeftRow.style.top = 6;
            topLeftRow.style.flexDirection = FlexDirection.Row;
            topLeftRow.style.alignItems = Align.Center;
            topLeftRow.style.flexWrap = Wrap.NoWrap;
            topLeftRow.style.maxWidth = new Length(72, LengthUnit.Percent);
            topLeftRow.pickingMode = PickingMode.Ignore;

            VisualElement nameChip = MakeInlineOverlayChip(
                entry.Label ?? ("Rink " + (index + 1)),
                embedded ? 12 : 14,
                nameColor);
            topLeftRow.Add(nameChip);
            AppendRinkFeatureBadges(topLeftRow, payload, entry, index, embedded);
            overlay.Add(topLeftRow);

            VisualElement countChip = MakeOverlayChip(
                entry.Count + "/" + rinkCapacity,
                embedded ? 12 : 14,
                countColor,
                Align.FlexEnd);
            countChip.name = "RinkCount_" + index;
            countChip.style.right = 6;
            countChip.style.top = 6;
            overlay.Add(countChip);

            if (isHere)
            {
                VisualElement hereBar = new VisualElement();
                hereBar.name = "RinkHereBar_" + index;
                hereBar.style.position = Position.Absolute;
                hereBar.style.left = 0;
                hereBar.style.right = 0;
                hereBar.style.bottom = 0;
                hereBar.style.height = hereBannerH;
                hereBar.style.backgroundColor = ElevatedBg;
                hereBar.style.borderTopWidth = 1;
                hereBar.style.borderTopColor = ColumnRule;
                hereBar.style.justifyContent = Justify.Center;
                hereBar.style.alignItems = Align.Center;
                hereBar.pickingMode = PickingMode.Ignore;

                Label here = MakeLabel("YOU ARE HERE", embedded ? 9 : 10, CtaBg, FontStyle.Bold);
                here.style.unityTextAlign = TextAnchor.MiddleCenter;
                try { here.style.letterSpacing = 1; } catch { }
                here.pickingMode = PickingMode.Ignore;
                hereBar.Add(here);
                overlay.Add(hereBar);
            }

            tile.tooltip = isFull
                ? (entry.Label + " is full"
                    + (string.IsNullOrEmpty(capacityHint) ? "" : " — " + capacityHint))
                : isHere
                    ? "Respawn at " + entry.Label
                    : string.IsNullOrEmpty(capacityHint)
                        ? "Teleport to " + entry.Label
                        : "Teleport to " + entry.Label + " (" + capacityHint + ")";

            // Static overview snap only — no live hover camera (FPS).
            // --- LIVE HOVER (disabled) — restore with RinkPreview live block ---
            // int previewIndex = index;
            // VisualElement previewSurface = preview;
            // tile.RegisterCallback<MouseEnterEvent>(delegate
            // {
            //     RinkPreview.SetLiveRink(previewIndex, previewSurface);
            // });
            // tile.RegisterCallback<MouseLeaveEvent>(delegate
            // {
            //     RinkPreview.SetLiveRink(-1, null);
            // });

            if (isFull)
            {
                tile.style.opacity = 0.55f;
            }
            else
            {
                int rinkIndex = index;
                tile.pickingMode = PickingMode.Position;
                tile.focusable = true;
                tile.RegisterCallback<PointerDownEvent>(delegate(PointerDownEvent evt)
                {
                    if (evt.button != 0) return;
                    callbacks.OnSelectRink?.Invoke(rinkIndex);
                    evt.StopPropagation();
                });
                tile.RegisterCallback<MouseEnterEvent>(delegate
                {
                    if (!isHere) SetBorder(tile, 2, AccentBright);
                });
                tile.RegisterCallback<MouseLeaveEvent>(delegate
                {
                    SetBorder(tile, 2, idleBorder);
                });
            }

            return tile;
        }

        private static RinkStripMode GetStripDropdownSelection(RinkMotdPayload payload, int rinkIndex)
        {
            RinkStripMode current = GetStripMode(payload, rinkIndex);
            if (StripDropdownSelection.TryGetValue(rinkIndex, out RinkStripMode pending))
                return pending;
            if (RinkStripModeUtil.IsPracticeMode(current))
                return current;
            return RinkStripMode.Empty;
        }

        private static void SetStripDropdownSelection(int rinkIndex, RinkStripMode mode)
        {
            StripDropdownSelection[rinkIndex] = mode;
        }

        private static void PruneStripDropdownSelection(RinkMotdPayload payload, int rinkIndex)
        {
            if (!StripDropdownSelection.TryGetValue(rinkIndex, out RinkStripMode pending))
                return;

            RinkStripMode current = GetStripMode(payload, rinkIndex);
            if (!RinkStripModeUtil.IsPracticeMode(current))
                StripDropdownSelection.Remove(rinkIndex);
        }

        internal static void ClearStripDropdownSelection(int rinkIndex)
        {
            StripDropdownSelection.Remove(rinkIndex);
        }

        /// <summary>Dismiss any open strip practice dropdown (e.g. scoreboard closed mid-menu).</summary>
        internal static void CloseStripPracticeMenu()
        {
            if (activeStripMenuOverlay != null)
            {
                try { activeStripMenuOverlay.RemoveFromHierarchy(); }
                catch { }
                activeStripMenuOverlay = null;
            }
        }

        private static VisualElement activeStripMenuOverlay;

        private const int StripMenuRowHeight = 28;
        private const string StripMenuOverlayName = "StripPracticeMenuOverlay";

        private static VisualElement MakeStripPracticeDropdown(
            int rinkIndex,
            RinkStripMode selected,
            int barH,
            int fontSize,
            Action<RinkStripMode> onSelected)
        {
            VisualElement host = new VisualElement();
            host.name = "StripDropdownHost_" + rinkIndex;
            host.style.flexGrow = 1;
            host.style.flexShrink = 1;
            host.style.flexBasis = 0;
            host.style.minWidth = 0;
            host.style.height = barH;
            host.style.borderRightWidth = 1;
            host.style.borderRightColor = BorderStrong;

            Color idleText = StripDropdownTextColor(selected);
            Color idleBg = Color.clear;

            VisualElement trigger = new VisualElement();
            trigger.name = "StripDropdown_" + rinkIndex;
            trigger.style.width = new Length(100, LengthUnit.Percent);
            trigger.style.height = barH;
            trigger.style.minHeight = barH;
            trigger.style.backgroundColor = idleBg;
            trigger.style.flexDirection = FlexDirection.Row;
            trigger.style.alignItems = Align.Center;
            trigger.style.justifyContent = Justify.Center;
            trigger.pickingMode = PickingMode.Position;
            trigger.focusable = true;
            trigger.tooltip = selected == RinkStripMode.Empty
                ? "No practice mode — pick one below, then press →"
                : "Selected: " + RinkStripModeUtil.DisplayName(selected);

            Label caption = MakeStripBarCaption(
                RinkStripModeUtil.DropdownLabel(selected),
                fontSize,
                idleText);
            caption.name = "StripDropdownCaption";
            trigger.Add(caption);

            trigger.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                ShowCompactStripMenu(trigger, fontSize, mode =>
                {
                    onSelected?.Invoke(mode);
                    caption.text = RinkStripModeUtil.DropdownLabel(mode);
                    caption.style.color = StripDropdownTextColor(mode);
                    trigger.tooltip = mode == RinkStripMode.Empty
                        ? "No practice mode — pick one below, then press →"
                        : "Selected: " + RinkStripModeUtil.DisplayName(mode);
                });
                evt.StopPropagation();
            });
            trigger.RegisterCallback<MouseEnterEvent>(_ => trigger.style.backgroundColor = ButtonHover);
            trigger.RegisterCallback<MouseLeaveEvent>(_ => trigger.style.backgroundColor = idleBg);
            host.Add(trigger);
            return host;
        }

        private static Label MakeStripBarCaption(string text, int fontSize, Color color)
        {
            Label label = new Label(text ?? "");
            label.style.fontSize = fontSize;
            label.style.color = color;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.marginTop = 0;
            label.style.marginBottom = 0;
            label.style.paddingTop = 0;
            label.style.paddingBottom = 0;
            label.style.paddingLeft = 4;
            label.style.paddingRight = 4;
            label.pickingMode = PickingMode.Ignore;
            return label;
        }

        private static Button MakeStripBarButton(string text, int barH, int fontSize, Color textColor, Color bgColor)
        {
            Button button = new Button(() => { }) { text = text ?? "" };
            button.style.width = new Length(100, LengthUnit.Percent);
            button.style.height = barH;
            button.style.minHeight = barH;
            button.style.fontSize = fontSize;
            button.style.color = textColor;
            button.style.backgroundColor = bgColor;
            button.style.marginTop = 0;
            button.style.marginBottom = 0;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            button.style.paddingLeft = 4;
            button.style.paddingRight = 4;
            button.style.borderTopWidth = 0;
            button.style.borderRightWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.borderLeftWidth = 0;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.justifyContent = Justify.Center;
            button.style.alignItems = Align.Center;
            button.style.whiteSpace = WhiteSpace.NoWrap;
            button.style.overflow = Overflow.Hidden;
            return button;
        }

        private static void ShowCompactStripMenu(VisualElement anchor, int fontSize, Action<RinkStripMode> onPick)
        {
            IPanel panel = anchor?.panel;
            if (panel == null) return;

            VisualElement root = panel.visualTree;
            if (root == null) return;

            CloseStripPracticeMenu();

            VisualElement overlay = new VisualElement { name = StripMenuOverlayName };
            activeStripMenuOverlay = overlay;
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0;
            overlay.style.top = 0;
            overlay.style.right = 0;
            overlay.style.bottom = 0;
            overlay.pickingMode = PickingMode.Position;

            Rect anchorBounds = anchor.worldBound;
            VisualElement menu = new VisualElement();
            menu.style.position = Position.Absolute;
            menu.style.left = anchorBounds.x;
            menu.style.top = anchorBounds.yMax;
            menu.style.width = anchorBounds.width;
            menu.style.backgroundColor = ElevatedBg;
            menu.style.flexDirection = FlexDirection.Column;
            SetBorder(menu, 1, BorderStrong);

            void CloseMenu()
            {
                CloseStripPracticeMenu();
            }

            overlay.RegisterCallback<DetachFromPanelEvent>(_ => activeStripMenuOverlay = null);

            menu.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

            for (int i = 0; i < RinkStripModeUtil.DropdownModes.Length; i++)
            {
                RinkStripMode mode = RinkStripModeUtil.DropdownModes[i];
                string itemLabel = RinkStripModeUtil.DisplayName(mode);
                Button item = MakeStripBarButton(itemLabel, StripMenuRowHeight, fontSize, TextColor, ElevatedBg);
                item.style.width = new Length(100, LengthUnit.Percent);
                item.style.borderTopWidth = i > 0 ? 1 : 0;
                item.style.borderTopColor = ColumnRule;
                item.RegisterCallback<MouseEnterEvent>(_ => item.style.backgroundColor = ButtonHover);
                item.RegisterCallback<MouseLeaveEvent>(_ => item.style.backgroundColor = ElevatedBg);
                item.clicked += delegate
                {
                    onPick?.Invoke(mode);
                    CloseMenu();
                };
                menu.Add(item);
            }

            overlay.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target is VisualElement target && menu.Contains(target))
                    return;
                CloseMenu();
            });

            overlay.Add(menu);
            root.Add(overlay);
        }

        private static VisualElement MakeRinkStripButton(
            RinkMotdPayload payload,
            int rinkIndex,
            Callbacks callbacks,
            bool embedded)
        {
            int barH = embedded ? 28 : 32;
            int fontSize = embedded ? 10 : 11;

            RinkStripMode current = GetStripMode(payload, rinkIndex);
            bool activeMode = RinkStripModeUtil.IsPracticeMode(current);

            RinkStripVoteProgress voteProgress = payload?.StripVoteProgress ?? RinkStripVoteProgress.None;
            bool votingHere = voteProgress.Active && voteProgress.RinkIndex == rinkIndex;
            bool votingToClear = votingHere && voteProgress.Mode == RinkStripMode.Empty;

            VisualElement wrap = new VisualElement();
            wrap.style.position = Position.Relative;
            wrap.style.flexShrink = 0;
            wrap.style.height = barH;

            if (activeMode)
            {
                string modeLabel = RinkStripModeUtil.DisplayName(current);
                string capHint = RinkStripModeUtil.GetJoinCapacityHint(current);
                if (!string.IsNullOrEmpty(capHint))
                    modeLabel = modeLabel + " · " + capHint;
                string removeLabel = RinkStripModeUtil.RemoveBarLabel(current);

                Color idleBorder = votingToClear ? FullRed : CtaBg;
                Color idleText = votingToClear ? FullRed : TextColor;
                Color idleBg = votingToClear
                    ? new Color(FullRed.r, FullRed.g, FullRed.b, 0.28f)
                    : ButtonBg;

                Button remove = MakeStripBarButton(modeLabel, barH, fontSize, idleText, idleBg);
                remove.clicked += delegate
                {
                    callbacks?.OnVoteStrip?.Invoke(rinkIndex, RinkStripMode.Empty);
                };
                remove.name = "StripRemove_" + rinkIndex;
                SetBorder(remove, 2, idleBorder);
                remove.RegisterCallback<MouseEnterEvent>(delegate
                {
                    remove.text = removeLabel;
                    remove.style.color = FullRed;
                    remove.style.backgroundColor = new Color(FullRed.r, FullRed.g, FullRed.b, 0.28f);
                    SetBorder(remove, 2, FullRed);
                });
                remove.RegisterCallback<MouseLeaveEvent>(delegate
                {
                    remove.text = modeLabel;
                    remove.style.color = idleText;
                    remove.style.backgroundColor = idleBg;
                    SetBorder(remove, 2, idleBorder);
                });
                wrap.Add(remove);

                if (votingToClear && !string.IsNullOrEmpty(voteProgress.BadgeText))
                    wrap.Add(MakeStripVoteBadge(voteProgress.BadgeText));

                return wrap;
            }

            RinkStripMode selected = GetStripDropdownSelection(payload, rinkIndex);
            PruneStripDropdownSelection(payload, rinkIndex);
            selected = GetStripDropdownSelection(payload, rinkIndex);
            bool votingToApply = votingHere && voteProgress.Mode != RinkStripMode.Empty;
            Color voteAccent = CtaBg;
            Color barBg = votingHere
                ? new Color(voteAccent.r, voteAccent.g, voteAccent.b, 0.28f)
                : ElevatedBg;

            VisualElement bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Stretch;
            bar.style.width = new Length(100, LengthUnit.Percent);
            bar.style.height = barH;
            bar.style.backgroundColor = barBg;
            bar.style.overflow = Overflow.Hidden;
            if (votingHere) SetBorder(bar, 2, voteAccent);
            else SetBorder(bar, 2, BorderStrong);
            wrap.Add(bar);

            VisualElement dropdown = MakeStripPracticeDropdown(
                rinkIndex,
                selected,
                barH,
                fontSize,
                delegate(RinkStripMode mode) { SetStripDropdownSelection(rinkIndex, mode); });
            bar.Add(dropdown);

            Color confirmBase = ElevatedBg;
            Color confirmHover = ButtonHover;

            Button confirm = MakeStripBarButton(
                "\u2192",
                barH,
                fontSize + 2,
                votingToApply ? TextColor : MutedText,
                confirmBase);
            confirm.clicked += delegate
            {
                if (!StripDropdownSelection.TryGetValue(rinkIndex, out RinkStripMode pending)
                    || !RinkStripModeUtil.IsPracticeMode(pending))
                    return;
                callbacks?.OnVoteStrip?.Invoke(rinkIndex, pending);
            };
            confirm.name = "StripConfirm_" + rinkIndex;
            confirm.style.width = embedded ? 30 : 34;
            confirm.style.minWidth = embedded ? 30 : 34;
            confirm.style.flexShrink = 0;
            confirm.RegisterCallback<MouseEnterEvent>(delegate { confirm.style.backgroundColor = confirmHover; });
            confirm.RegisterCallback<MouseLeaveEvent>(delegate { confirm.style.backgroundColor = confirmBase; });
            bar.Add(confirm);

            if (votingHere && !string.IsNullOrEmpty(voteProgress.BadgeText))
                wrap.Add(MakeStripVoteBadge(voteProgress.BadgeText));

            return wrap;
        }

        private static Color StripDropdownTextColor(RinkStripMode mode)
        {
            return mode == RinkStripMode.Empty ? MutedText : TextColor;
        }

        private static Label MakeStripVoteBadge(string badgeText)
        {
            Label badge = MakeLabel(badgeText, 8, CtaText, FontStyle.Bold);
            badge.pickingMode = PickingMode.Ignore;
            badge.style.position = Position.Absolute;
            badge.style.top = -2;
            badge.style.right = -2;
            badge.style.backgroundColor = VoteBadgeOrange;
            badge.style.paddingLeft = 4;
            badge.style.paddingRight = 4;
            badge.style.paddingTop = 0;
            badge.style.paddingBottom = 0;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.style.borderTopLeftRadius = 6;
            badge.style.borderTopRightRadius = 6;
            badge.style.borderBottomLeftRadius = 6;
            badge.style.borderBottomRightRadius = 6;
            return badge;
        }

        private static void StyleDropdownField(DropdownField dropdown, int barH, int fontSize)
        {
            dropdown.style.width = new Length(100, LengthUnit.Percent);
            dropdown.style.alignSelf = Align.Stretch;
            dropdown.style.flexDirection = FlexDirection.Row;

            VisualElement input = dropdown.Q(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.position = Position.Relative;
                input.style.flexGrow = 1;
                input.style.flexShrink = 1;
                input.style.width = new Length(100, LengthUnit.Percent);
                input.style.height = barH;
                input.style.minHeight = barH;
                input.style.backgroundColor = Color.clear;
                input.style.borderTopWidth = 0;
                input.style.borderRightWidth = 0;
                input.style.borderBottomWidth = 0;
                input.style.borderLeftWidth = 0;
                input.style.paddingLeft = 8;
                input.style.paddingRight = 22;
                input.style.flexDirection = FlexDirection.Row;
                input.style.alignItems = Align.Center;
                input.style.justifyContent = Justify.SpaceBetween;
            }

            Label textInput = dropdown.Q<Label>(className: "unity-base-field__input");
            if (textInput != null)
            {
                textInput.style.flexGrow = 1;
                textInput.style.width = new Length(100, LengthUnit.Percent);
                textInput.style.fontSize = fontSize;
                textInput.style.unityFontStyleAndWeight = FontStyle.Bold;
                textInput.style.unityTextAlign = TextAnchor.MiddleCenter;
            }

            VisualElement arrow = dropdown.Q(className: "unity-base-popup-field__arrow");
            if (arrow != null)
            {
                arrow.style.position = Position.Absolute;
                arrow.style.right = 8;
                arrow.style.top = 0;
                arrow.style.bottom = 0;
                arrow.style.unityBackgroundImageTintColor = MutedText;
            }

            dropdown.RegisterCallback<MouseEnterEvent>(delegate
            {
                if (input != null) input.style.backgroundColor = ButtonHover;
            });
            dropdown.RegisterCallback<MouseLeaveEvent>(delegate
            {
                if (input != null) input.style.backgroundColor = Color.clear;
            });
        }

        private static RinkSlot GetRinkSlotByIndex(int index)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || index < 0 || index >= cfg.Rinks.Count) return null;
            return cfg.Rinks[index];
        }

        /// <summary>Feature pills beside the rink name (TOOLS, …).</summary>
        private static void AppendRinkFeatureBadges(
            VisualElement row,
            RinkMotdPayload payload,
            RinkStatusEntry entry,
            int index,
            bool embedded)
        {
            RinkStripMode stripMode = GetStripMode(payload, index);
            string badgeText = RinkStripModeUtil.FeatureBadge(stripMode);
            if (!string.IsNullOrEmpty(badgeText))
            {
                Color badgeColor = stripMode == RinkStripMode.PhlTools
                    ? CtaBg
                    : stripMode == RinkStripMode.GoaliePractice
                        ? BadgeCyan
                        : stripMode == RinkStripMode.PuckChasers
                            ? new Color(0.85f, 0.35f, 0.35f, 1f)
                            : VoteBadgeOrange;
                row.Add(MakeFeatureBadge(badgeText, badgeColor, embedded));
            }

            string capBadge = RinkStripModeUtil.GetJoinCapacityBadge(stripMode);
            if (!string.IsNullOrEmpty(capBadge))
                row.Add(MakeFeatureBadge(capBadge, FullRed, embedded));
        }

        private static VisualElement MakeInlineOverlayChip(string text, int fontSize, Color color)
        {
            VisualElement chip = new VisualElement();
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.flexShrink = 1;
            chip.style.minWidth = 0;
            chip.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            chip.style.paddingLeft = 7;
            chip.style.paddingRight = 7;
            chip.style.paddingTop = 2;
            chip.style.paddingBottom = 2;
            chip.style.minHeight = fontSize + 8;
            chip.style.justifyContent = Justify.Center;
            chip.style.borderTopLeftRadius = 3;
            chip.style.borderTopRightRadius = 3;
            chip.style.borderBottomLeftRadius = 3;
            chip.style.borderBottomRightRadius = 3;
            chip.pickingMode = PickingMode.Ignore;

            Label label = MakeLabel(text, fontSize, color, FontStyle.Bold);
            StyleOverlayLabel(label, TextAnchor.MiddleLeft);
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.flexShrink = 1;
            label.pickingMode = PickingMode.Ignore;
            chip.Add(label);
            return chip;
        }

        private static VisualElement MakeFeatureBadge(string text, Color accent, bool embedded)
        {
            int fontSize = embedded ? 12 : 14;
            VisualElement pill = new VisualElement();
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.alignItems = Align.Center;
            pill.style.flexShrink = 0;
            pill.style.marginLeft = 4;
            pill.style.paddingLeft = 7;
            pill.style.paddingRight = 7;
            pill.style.paddingTop = 2;
            pill.style.paddingBottom = 2;
            pill.style.minHeight = fontSize + 8;
            pill.style.justifyContent = Justify.Center;
            pill.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            pill.style.borderTopWidth = 1;
            pill.style.borderRightWidth = 1;
            pill.style.borderBottomWidth = 1;
            pill.style.borderLeftWidth = 1;
            pill.style.borderTopColor = accent;
            pill.style.borderRightColor = accent;
            pill.style.borderBottomColor = accent;
            pill.style.borderLeftColor = accent;
            pill.style.borderTopLeftRadius = 3;
            pill.style.borderTopRightRadius = 3;
            pill.style.borderBottomLeftRadius = 3;
            pill.style.borderBottomRightRadius = 3;
            pill.pickingMode = PickingMode.Ignore;

            Label label = MakeLabel(text, fontSize, accent, FontStyle.Bold);
            StyleOverlayLabel(label, TextAnchor.MiddleCenter);
            label.pickingMode = PickingMode.Ignore;
            pill.Add(label);
            return pill;
        }

        private static VisualElement MakeOverlayChip(
            string text, int fontSize, Color color, Align horizontal)
        {
            VisualElement chip = new VisualElement();
            chip.style.position = Position.Absolute;
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            chip.style.paddingLeft = 7;
            chip.style.paddingRight = 7;
            chip.style.paddingTop = 2;
            chip.style.paddingBottom = 2;
            chip.style.minHeight = fontSize + 8;
            chip.style.justifyContent = Justify.Center;
            chip.style.borderTopLeftRadius = 3;
            chip.style.borderTopRightRadius = 3;
            chip.style.borderBottomLeftRadius = 3;
            chip.style.borderBottomRightRadius = 3;
            chip.pickingMode = PickingMode.Ignore;

            Label label = MakeLabel(text, fontSize, color, FontStyle.Bold);
            StyleOverlayLabel(label, horizontal == Align.FlexEnd
                ? TextAnchor.MiddleRight
                : TextAnchor.MiddleLeft);
            label.pickingMode = PickingMode.Ignore;
            chip.Add(label);
            return chip;
        }

        private static void ApplyPreviewSurface(VisualElement preview, int index)
        {
            if (preview == null) return;
            Texture texture = RinkPreview.GetTexture(index);
            RenderTexture rt = texture as RenderTexture;
            if (rt != null)
            {
                preview.Clear();
                preview.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(rt));
#pragma warning disable CS0618
                preview.style.unityBackgroundScaleMode = ScaleMode.ScaleAndCrop;
#pragma warning restore CS0618
                return;
            }

            if (preview.childCount > 0) return;
            Label noCam = MakeLabel("· · ·", 16, MutedText, FontStyle.Bold);
            noCam.style.unityTextAlign = TextAnchor.MiddleCenter;
            noCam.style.flexGrow = 1;
            noCam.pickingMode = PickingMode.Ignore;
            preview.Add(noCam);
        }

        internal static void SetBorder(VisualElement element, int width, Color color)
        {
            element.style.borderTopWidth = width;
            element.style.borderRightWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderTopColor = color;
            element.style.borderRightColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
        }

        private static void StyleOverlayLabel(Label label, TextAnchor align)
        {
            if (label == null) return;

            label.style.unityTextAlign = align;
            label.style.marginTop = 0;
            label.style.marginBottom = 0;
            label.style.paddingTop = 0;
            label.style.paddingBottom = 0;
            label.style.alignSelf = Align.Center;
        }

        internal static Label MakeLabel(string text, int size, Color color, FontStyle style)
        {
            Label label = new Label(text ?? "");
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityFontStyleAndWeight = style;
            return label;
        }

        internal static Button MakeButton(string text, Color color, Color hoverColor, Action clicked)
        {
            Button button = new Button(clicked) { text = text };
            button.style.height = 44;
            button.style.minWidth = 145;
            button.style.paddingLeft = 16;
            button.style.paddingRight = 16;
            button.style.fontSize = 15;
            button.style.color = TextColor;
            button.style.backgroundColor = color;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.justifyContent = Justify.Center;
            button.style.alignItems = Align.Center;
            button.style.borderTopWidth = 0;
            button.style.borderRightWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.borderLeftWidth = 0;
            button.RegisterCallback<MouseEnterEvent>(delegate { button.style.backgroundColor = hoverColor; });
            button.RegisterCallback<MouseLeaveEvent>(delegate { button.style.backgroundColor = color; });
            return button;
        }

        /// <summary>On/off pill — active state stays green (including while hovered).</summary>
        internal static Button MakeStateButton(string text, bool active, Action clicked)
        {
            Color baseBg = active ? CtaBg : ButtonBg;
            Color hoverBg = active ? CtaHover : ButtonHover;

            Button button = new Button(clicked ?? (() => { })) { text = text };
            button.style.color = active ? CtaText : TextColor;
            button.style.backgroundColor = baseBg;
            button.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.justifyContent = Justify.Center;
            button.style.alignItems = Align.Center;
            button.style.borderTopWidth = 0;
            button.style.borderRightWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.borderLeftWidth = 0;
            button.RegisterCallback<MouseEnterEvent>(delegate { button.style.backgroundColor = hoverBg; });
            button.RegisterCallback<MouseLeaveEvent>(delegate { button.style.backgroundColor = baseBg; });
            return button;
        }

        /// <summary>
        /// One footer link tile, styled like the PHL public MOTD: phlstats gets a wide
        /// wordmark rectangle; Discord/Twitch PNGs are full brand tiles painted
        /// edge-to-edge; YouTube's transparent mark sits on YouTube red.
        /// </summary>
        private static void AddCommunityButton(
            VisualElement parent, string name, string url, string iconName)
        {
            const int tileH = 36;
            const int tileSquare = 36;
            const int tilePhlstatsW = 85;

            bool isPhlstats = iconName == PracticeMotdAssets.PhlstatsIcon;
            bool isYoutube = iconName == PracticeMotdAssets.YoutubeIcon;
            bool fullBrandTile = iconName == PracticeMotdAssets.DiscordIcon
                || iconName == PracticeMotdAssets.TwitchIcon;

            int tileW = isPhlstats ? tilePhlstatsW : tileSquare;

            Color normal = ButtonBg;
            Color hover = ButtonHover;
            if (fullBrandTile)
            {
                normal = Color.clear;
                hover = new Color(1f, 1f, 1f, 0.10f);
            }
            else if (isYoutube) { normal = YoutubeBg; hover = YoutubeHover; }

            VisualElement button = new VisualElement();
            button.style.flexDirection = FlexDirection.Column;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            // Lock size — UITK flex can otherwise stretch and squash logos.
            button.style.width = tileW;
            button.style.height = tileH;
            button.style.minWidth = tileW;
            button.style.maxWidth = tileW;
            button.style.minHeight = tileH;
            button.style.maxHeight = tileH;
            button.style.flexGrow = 0;
            button.style.flexShrink = 0;
            button.style.marginLeft = 5;
            button.style.marginRight = 5;
            button.style.paddingLeft = isPhlstats ? 6 : 0;
            button.style.paddingRight = isPhlstats ? 6 : 0;
            button.style.overflow = Overflow.Hidden;
            button.style.backgroundColor = normal;
            button.tooltip = name;
            if (isPhlstats)
                SetBorder(button, 1, BorderStrong);
            parent.Add(button);

            Texture2D texture;
            if (PracticeMotdAssets.TryGetTexture(iconName, out texture))
            {
                VisualElement icon = new VisualElement();
                int iconW = fullBrandTile ? tileSquare : (isPhlstats ? tileW - 12 : 26);
                int iconH = fullBrandTile ? tileSquare : (isPhlstats ? 24 : 26);
                icon.style.width = iconW;
                icon.style.height = iconH;
                icon.style.minWidth = iconW;
                icon.style.maxWidth = iconW;
                icon.style.minHeight = iconH;
                icon.style.maxHeight = iconH;
                icon.style.flexGrow = 0;
                icon.style.flexShrink = 0;
                icon.style.backgroundColor = Color.clear;
                icon.style.backgroundImage = new StyleBackground(texture);
#pragma warning disable CS0618
                icon.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
#pragma warning restore CS0618
                icon.pickingMode = PickingMode.Ignore;
                button.Add(icon);
            }
            else
            {
                Label label = MakeLabel(name, isPhlstats ? 13 : 11, TextColor, FontStyle.Bold);
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.pickingMode = PickingMode.Ignore;
                button.Add(label);
            }

            button.pickingMode = PickingMode.Position;
            button.RegisterCallback<ClickEvent>(delegate { OpenExternalUrl(url); });
            button.RegisterCallback<MouseEnterEvent>(delegate
            {
                button.style.backgroundColor = hover;
                button.style.opacity = 0.92f;
            });
            button.RegisterCallback<MouseLeaveEvent>(delegate
            {
                button.style.backgroundColor = normal;
                button.style.opacity = 1f;
            });
        }

        /// <summary>
        /// Steam overlay when available (keeps the player in-game), else system browser.
        /// Steamworks is resolved via reflection so MultiSheet.dll takes no hard
        /// dependency on the Steamworks.NET assembly.
        /// </summary>
        private static void OpenExternalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                Type steamUtils = FindSteamType("Steamworks.SteamUtils");
                Type steamFriends = FindSteamType("Steamworks.SteamFriends");
                if (steamUtils != null && steamFriends != null)
                {
                    var overlayEnabled = steamUtils.GetMethod(
                        "IsOverlayEnabled", Type.EmptyTypes)?.Invoke(null, null);
                    if (overlayEnabled is bool enabled && enabled)
                    {
                        foreach (var method in steamFriends.GetMethods())
                        {
                            if (method.Name != "ActivateGameOverlayToWebPage") continue;
                            var parameters = method.GetParameters();
                            if (parameters.Length == 1)
                            {
                                method.Invoke(null, new object[] { url });
                                return;
                            }
                            if (parameters.Length == 2)
                            {
                                object mode = parameters[1].ParameterType.IsEnum
                                    ? Enum.ToObject(parameters[1].ParameterType, 0)
                                    : null;
                                method.Invoke(null, new object[] { url, mode });
                                return;
                            }
                        }
                    }
                }
            }
            catch { }

            try { Application.OpenURL(url); }
            catch (Exception ex) { Debug.LogError("[PHLPractice] Could not open URL: " + ex.Message); }
        }

        private static Type FindSteamType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        private static void MakeClickable(VisualElement element, Action onClick)
        {
            if (element == null || onClick == null) return;
            element.pickingMode = PickingMode.Position;
            element.RegisterCallback<ClickEvent>(delegate { onClick(); });
            element.RegisterCallback<MouseEnterEvent>(delegate { element.style.opacity = 0.85f; });
            element.RegisterCallback<MouseLeaveEvent>(delegate { element.style.opacity = 1f; });
        }
    }
}
