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
        internal static readonly Color YoutubeBg = new Color(0.90f, 0.10f, 0.10f, 1f);
        internal static readonly Color YoutubeHover = new Color(1f, 0.20f, 0.20f, 1f);

        // PHL community links (same set as the PHL public MOTD footer).
        private const string PhlstatsUrl = "https://phlstats.com";
        private const string DiscordUrl = "https://discord.gg/puckhockeyleague";
        private const string YoutubeUrl = "https://www.youtube.com/@PHLnextshift";
        private const string TwitchUrl = "https://www.twitch.tv/puckhockeyleaguenetwork";

        internal sealed class Callbacks
        {
            public Action OnContinue;
            public Action<int> OnSelectRink;
            public Action<int> OnSelectRole;
            public Action<int, RinkStripMode> OnVoteStrip;
        }

        internal sealed class Result
        {
            public VisualElement Overlay;
            public VisualElement Card;
            public VisualElement RoleSectionHost;
            public VisualElement RinkSectionHost;
            public VisualElement StripSectionHost;
            public VisualElement RadioSectionHost;
        }

        /// <summary>Which rink the strip dropdown targets (0-based). Persists across repaints.</summary>
        internal static int SelectedStripRinkIndex = 0;

        /// <summary>Fullscreen dimmed overlay + card (join welcome / F9).</summary>
        internal static Result Build(RinkMotdPayload payload, Callbacks callbacks)
        {
            Result cardResult = BuildCard(payload, callbacks, embedded: false);

            VisualElement overlay = new VisualElement();
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0;
            overlay.style.right = 0;
            overlay.style.top = 0;
            overlay.style.bottom = 0;
            overlay.style.alignItems = Align.Center;
            overlay.style.justifyContent = Justify.Center;
            overlay.style.backgroundColor = OverlayBg;
            overlay.focusable = true;
            overlay.pickingMode = PickingMode.Position;
            overlay.Add(cardResult.Card);

            return new Result
            {
                Overlay = overlay,
                Card = cardResult.Card,
                RoleSectionHost = cardResult.RoleSectionHost,
                RinkSectionHost = cardResult.RinkSectionHost,
                StripSectionHost = cardResult.StripSectionHost,
                RadioSectionHost = cardResult.RadioSectionHost
            };
        }

        /// <summary>Card sized to fill a host (scoreboard Rinks tab).</summary>
        internal static Result BuildEmbedded(RinkMotdPayload payload, Callbacks callbacks)
        {
            return BuildCard(payload, callbacks, embedded: true);
        }

        private static Result BuildCard(RinkMotdPayload payload, Callbacks callbacks, bool embedded)
        {
            if (payload == null) payload = new RinkMotdPayload();
            if (callbacks == null) callbacks = new Callbacks();

            VisualElement card = new VisualElement();
            if (embedded)
            {
                card.style.flexGrow = 1;
                card.style.width = new Length(100, LengthUnit.Percent);
                card.style.height = new Length(100, LengthUnit.Percent);
            }
            else
            {
                card.style.width = new Length(70, LengthUnit.Percent);
                card.style.maxWidth = 1020;
                card.style.maxHeight = new Length(90, LengthUnit.Percent);
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

            Label headerTag = MakeLabel("PRACTICE SERVER", 11, MutedText, FontStyle.Bold);
            try { headerTag.style.letterSpacing = 2; } catch { }
            headerTag.style.position = Position.Absolute;
            headerTag.style.left = 0;
            headerTag.style.right = 0;
            headerTag.style.unityTextAlign = TextAnchor.MiddleCenter;
            headerTag.pickingMode = PickingMode.Ignore;
            header.Add(headerTag);

            Color closeNormal = new Color(0.90f, 0.22f, 0.22f, 1f);
            Color closeHover = new Color(1f, 0.35f, 0.35f, 1f);
            Label closeX = MakeLabel("✕", 18, closeNormal, FontStyle.Bold);
            closeX.tooltip = embedded ? "Back to scoreboard" : "Close";
            closeX.style.unityTextAlign = TextAnchor.MiddleCenter;
            closeX.style.width = 28;
            closeX.style.height = 28;
            closeX.style.flexShrink = 0;
            MakeClickable(closeX, callbacks.OnContinue ?? (() => { }));
            closeX.RegisterCallback<MouseEnterEvent>(delegate { closeX.style.color = closeHover; });
            closeX.RegisterCallback<MouseLeaveEvent>(delegate { closeX.style.color = closeNormal; });
            header.Add(closeX);

            // Embedded MOTD must stay one viewport tall (no scrollbar). Fullscreen welcome
            // can scroll on short displays.
            VisualElement contentHost;
            if (embedded)
            {
                contentHost = new VisualElement();
                contentHost.style.flexGrow = 1;
                contentHost.style.flexShrink = 1;
                contentHost.style.minHeight = 0;
            }
            else
            {
                ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
                scroll.style.flexGrow = 1;
                scroll.style.flexShrink = 1;
                scroll.style.minHeight = 0;
                scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
                contentHost = scroll;
            }
            card.Add(contentHost);

            VisualElement body = new VisualElement();
            body.style.flexGrow = 1;
            body.style.flexShrink = 1;
            body.style.minHeight = 0;
            body.style.paddingLeft = embedded ? 10 : 26;
            body.style.paddingRight = embedded ? 10 : 26;
            body.style.paddingTop = embedded ? 6 : 12;
            body.style.paddingBottom = embedded ? 2 : 8;
            contentHost.Add(body);

            string title = string.IsNullOrWhiteSpace(payload.Title)
                ? "Welcome to PHL MultiSheet Practice"
                : payload.Title;
            Label titleLabel = MakeLabel(title, embedded ? 17 : 26, TextColor, FontStyle.Bold);
            titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            titleLabel.style.marginBottom = embedded ? 4 : 10;
            titleLabel.style.flexShrink = 0;
            body.Add(titleLabel);

            VisualElement sep = new VisualElement();
            sep.style.height = 1;
            sep.style.backgroundColor = ColumnRule;
            sep.style.marginBottom = embedded ? 6 : 10;
            sep.style.flexShrink = 0;
            body.Add(sep);

            // Rink grid owns leftover height so tiles grow instead of leaving a blank band
            // above the Position/Lighting row.
            VisualElement rinkSectionHost = new VisualElement();
            rinkSectionHost.style.flexGrow = 1;
            rinkSectionHost.style.flexShrink = 1;
            rinkSectionHost.style.minHeight = 0;
            body.Add(rinkSectionHost);
            FillRinkSection(rinkSectionHost, payload, callbacks, embedded);

            // Position | Lighting side-by-side — half the vertical stack of stacking them.
            VisualElement controlsRow = new VisualElement();
            controlsRow.style.flexDirection = FlexDirection.Row;
            controlsRow.style.alignItems = Align.Stretch;
            controlsRow.style.justifyContent = Justify.Center;
            controlsRow.style.flexShrink = 0;
            controlsRow.style.marginTop = embedded ? 4 : 6;
            controlsRow.style.marginBottom = embedded ? 4 : 8;
            body.Add(controlsRow);

            VisualElement roleSectionHost = new VisualElement();
            roleSectionHost.style.flexGrow = 1;
            roleSectionHost.style.flexShrink = 1;
            roleSectionHost.style.flexBasis = 0;
            roleSectionHost.style.minWidth = 0;
            controlsRow.Add(roleSectionHost);
            FillRoleSection(roleSectionHost, payload, callbacks, embedded);

            VisualElement vDivider = new VisualElement();
            vDivider.style.width = 1;
            vDivider.style.alignSelf = Align.Stretch;
            vDivider.style.backgroundColor = ColumnRule;
            vDivider.style.marginLeft = embedded ? 10 : 14;
            vDivider.style.marginRight = embedded ? 10 : 14;
            vDivider.style.marginTop = embedded ? 10 : 14;
            vDivider.style.marginBottom = embedded ? 8 : 12;
            vDivider.pickingMode = PickingMode.Ignore;
            controlsRow.Add(vDivider);

            // Purely local display preference — outside the role host so status
            // repaints cannot drop the timeline mid-drag.
            VisualElement lightingSectionHost = new VisualElement();
            lightingSectionHost.style.flexGrow = 1;
            lightingSectionHost.style.flexShrink = 1;
            lightingSectionHost.style.flexBasis = 0;
            lightingSectionHost.style.minWidth = 0;
            controlsRow.Add(lightingSectionHost);
            FillLightingSection(lightingSectionHost, embedded);

            // Performance toggles — below Position | Lighting, outside role host so
            // occupancy refreshes do not wipe them mid-session.
            VisualElement perfSep = new VisualElement();
            perfSep.style.height = 1;
            perfSep.style.backgroundColor = ColumnRule;
            perfSep.style.flexShrink = 0;
            perfSep.style.marginTop = embedded ? 2 : 6;
            perfSep.style.marginBottom = embedded ? 4 : 8;
            body.Add(perfSep);

            VisualElement perfSectionHost = new VisualElement();
            perfSectionHost.style.flexShrink = 0;
            body.Add(perfSectionHost);
            FillPerfSection(perfSectionHost, lightingSectionHost, embedded);

            VisualElement stripSectionHost = new VisualElement();
            stripSectionHost.style.flexShrink = 0;
            body.Add(stripSectionHost);
            FillStripSection(stripSectionHost, payload, callbacks, embedded);

            VisualElement radioSectionHost = new VisualElement();
            radioSectionHost.style.flexShrink = 0;
            body.Add(radioSectionHost);
            FillRadioSection(radioSectionHost, embedded);

            // Sticky footer — community icons; Continue only on the fullscreen welcome.
            VisualElement footer = new VisualElement();
            footer.style.flexShrink = 0;
            footer.style.flexGrow = 0;
            footer.style.paddingLeft = embedded ? 16 : 26;
            footer.style.paddingRight = embedded ? 16 : 26;
            footer.style.paddingTop = 8;
            footer.style.paddingBottom = 10;
            footer.style.backgroundColor = PanelBg;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = BorderColor;
            footer.style.alignItems = Align.Stretch;
            card.Add(footer);

            VisualElement footerRow = new VisualElement();
            footerRow.style.flexDirection = FlexDirection.Row;
            footerRow.style.flexWrap = Wrap.NoWrap;
            footerRow.style.justifyContent = embedded ? Justify.Center : Justify.SpaceBetween;
            footerRow.style.alignItems = Align.FlexEnd;
            footerRow.style.width = new Length(100, LengthUnit.Percent);
            footer.Add(footerRow);

            // Title + icons share one column so the heading centers over the buttons.
            VisualElement communityBlock = new VisualElement();
            communityBlock.style.flexDirection = FlexDirection.Column;
            communityBlock.style.alignItems = Align.Center;
            communityBlock.style.flexShrink = 0;
            footerRow.Add(communityBlock);

            Label community = MakeLabel("JOIN THE COMMUNITY", 14, MutedText, FontStyle.Bold);
            try { community.style.letterSpacing = 2; } catch { }
            community.style.unityTextAlign = TextAnchor.MiddleCenter;
            community.style.marginBottom = 6;
            communityBlock.Add(community);

            VisualElement linkRow = new VisualElement();
            linkRow.style.flexDirection = FlexDirection.Row;
            linkRow.style.flexWrap = Wrap.NoWrap;
            linkRow.style.justifyContent = Justify.Center;
            linkRow.style.alignItems = Align.Center;
            linkRow.style.flexShrink = 0;
            communityBlock.Add(linkRow);

            // Fixed social order: phlstats → Discord → YouTube → Twitch.
            AddCommunityButton(linkRow, "phlstats.com", PhlstatsUrl, PracticeMotdAssets.PhlstatsIcon);
            AddCommunityButton(linkRow, "Discord", DiscordUrl, PracticeMotdAssets.DiscordIcon);
            AddCommunityButton(linkRow, "YouTube", YoutubeUrl, PracticeMotdAssets.YoutubeIcon);
            AddCommunityButton(linkRow, "Twitch", TwitchUrl, PracticeMotdAssets.TwitchIcon);

            if (!embedded)
            {
                Button continueButton = MakeButton("Continue", CtaBg, CtaHover, callbacks.OnContinue ?? (() => { }));
                continueButton.style.color = CtaText;
                continueButton.style.unityFontStyleAndWeight = FontStyle.Bold;
                continueButton.style.height = 72;
                continueButton.style.minWidth = 160;
                continueButton.style.flexShrink = 0;
                continueButton.style.marginLeft = 16;
                footerRow.Add(continueButton);
            }

            return new Result
            {
                Overlay = null,
                Card = card,
                RoleSectionHost = roleSectionHost,
                RinkSectionHost = rinkSectionHost,
                StripSectionHost = stripSectionHost,
                RadioSectionHost = radioSectionHost
            };
        }

        /// <summary>phlstats training radio — compact ♪ chip in Rinks tab / MOTD, expands on click.</summary>
        internal static void FillRadioSection(VisualElement host, bool embedded)
        {
            if (host == null || !FlamiePracFeatures.EnableRadio)
                return;

            host.Clear();
            RadioHudUI.DetachEmbedded();
            RadioHudUI.AttachEmbedded(host);
        }

        /// <summary>
        /// Per-rink tools strip: rink picker + PHL Tools / Empty vote buttons
        /// (same orange vote badge pattern as PHL Public game-mode UI).
        /// </summary>
        internal static void FillStripSection(
            VisualElement host,
            RinkMotdPayload payload,
            Callbacks callbacks,
            bool embedded)
        {
            if (host == null || payload == null) return;
            if (callbacks == null) callbacks = new Callbacks();
            host.Clear();

            int rinkCount = payload.Rinks != null ? payload.Rinks.Count : 0;
            if (rinkCount <= 0) return;

            int localRink = GetLocalRinkIndex(payload);
            if (localRink >= 0 && localRink < rinkCount)
                SelectedStripRinkIndex = localRink;
            SelectedStripRinkIndex = Mathf.Clamp(SelectedStripRinkIndex, 0, rinkCount - 1);

            VisualElement sep = new VisualElement();
            sep.style.height = 1;
            sep.style.backgroundColor = ColumnRule;
            sep.style.marginTop = embedded ? 4 : 6;
            sep.style.marginBottom = embedded ? 6 : 8;
            host.Add(sep);

            Label heading = MakeLabel("Tools Strip", embedded ? 13 : 14, MutedText, FontStyle.Bold);
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            heading.style.marginBottom = 6;
            host.Add(heading);

            VisualElement pickerRow = new VisualElement();
            pickerRow.style.flexDirection = FlexDirection.Row;
            pickerRow.style.justifyContent = Justify.Center;
            pickerRow.style.alignItems = Align.Center;
            pickerRow.style.flexWrap = Wrap.Wrap;
            pickerRow.style.marginBottom = 6;
            host.Add(pickerRow);

            Label pickerLabel = MakeLabel("Rink:", embedded ? 11 : 12, MutedText, FontStyle.Normal);
            pickerLabel.style.marginRight = 6;
            pickerRow.Add(pickerLabel);

            var choices = new List<string>();
            for (int i = 0; i < rinkCount; i++)
            {
                RinkStatusEntry entry = payload.Rinks[i];
                choices.Add(entry?.Label ?? ("Rink " + (i + 1)));
            }

            int selectedIndex = SelectedStripRinkIndex;
            DropdownField dropdown = new DropdownField(choices, selectedIndex);
            dropdown.style.minWidth = embedded ? 120 : 160;
            dropdown.style.maxWidth = embedded ? 200 : 260;
            dropdown.style.fontSize = embedded ? 11 : 12;
            dropdown.style.color = TextColor;
            dropdown.RegisterValueChangedCallback(evt =>
            {
                int idx = choices.IndexOf(evt.newValue);
                if (idx >= 0) SelectedStripRinkIndex = idx;
                FillStripSection(host, payload, callbacks, embedded);
            });
            pickerRow.Add(dropdown);

            RinkStripMode current = GetStripMode(payload, SelectedStripRinkIndex);
            RinkStripVoteProgress voteProgress = payload.StripVoteProgress;

            VisualElement modeRow = new VisualElement();
            modeRow.style.flexDirection = FlexDirection.Row;
            modeRow.style.justifyContent = Justify.Center;
            modeRow.style.alignItems = Align.Stretch;
            modeRow.style.width = new Length(100, LengthUnit.Percent);
            host.Add(modeRow);

            AddStripModeButton(
                modeRow,
                RinkStripMode.PhlTools,
                current,
                callbacks,
                voteProgress,
                embedded,
                isLast: false);
            AddStripModeButton(
                modeRow,
                RinkStripMode.Empty,
                current,
                callbacks,
                voteProgress,
                embedded,
                isLast: true);
        }

        private static RinkStripMode GetStripMode(RinkMotdPayload payload, int rinkIndex)
        {
            if (payload?.StripModes != null && rinkIndex >= 0 && rinkIndex < payload.StripModes.Count)
                return payload.StripModes[rinkIndex];
            return rinkIndex == 0 ? RinkStripMode.PhlTools : RinkStripMode.Empty;
        }

        private static void AddStripModeButton(
            VisualElement parent,
            RinkStripMode mode,
            RinkStripMode current,
            Callbacks callbacks,
            RinkStripVoteProgress voteProgress,
            bool embedded,
            bool isLast)
        {
            bool active = mode == current;
            bool votingHere = voteProgress.Active
                && voteProgress.Mode == mode
                && voteProgress.RinkIndex == SelectedStripRinkIndex;

            Color normal = ButtonBg;
            Color hover = ButtonHover;
            if (votingHere && !active)
            {
                normal = new Color(0.90f, 0.49f, 0.13f, 0.28f);
                hover = new Color(0.90f, 0.49f, 0.13f, 0.42f);
            }

            VisualElement wrap = new VisualElement();
            wrap.style.flexGrow = 1;
            wrap.style.flexShrink = 1;
            wrap.style.flexBasis = 0;
            wrap.style.minWidth = embedded ? 100 : 120;
            wrap.style.marginRight = isLast ? 0 : 6;
            wrap.style.height = embedded ? 38 : 44;
            wrap.style.position = Position.Relative;
            parent.Add(wrap);

            string label = RinkStripModeUtil.DisplayName(mode) + (active ? "  ✓" : "");
            int rinkIndex = SelectedStripRinkIndex;
            RinkStripMode voteMode = mode;
            Button button = MakeButton(label, normal, hover, delegate
            {
                callbacks.OnVoteStrip?.Invoke(rinkIndex, voteMode);
            });
            button.style.flexGrow = 1;
            button.style.width = new Length(100, LengthUnit.Percent);
            button.style.height = embedded ? 38 : 44;
            button.style.fontSize = embedded ? 11 : 12;
            button.style.whiteSpace = WhiteSpace.Normal;
            if (votingHere) SetBorder(button, 2, new Color(0.90f, 0.49f, 0.13f, 1f));
            else if (active) SetBorder(button, 2, CtaBg);
            else SetBorder(button, 1, BorderStrong);
            wrap.Add(button);

            if (votingHere && !string.IsNullOrEmpty(voteProgress.BadgeText))
            {
                Label badge = MakeLabel(voteProgress.BadgeText, 10, CtaText, FontStyle.Bold);
                badge.pickingMode = PickingMode.Ignore;
                badge.style.position = Position.Absolute;
                badge.style.top = 3;
                badge.style.right = 3;
                badge.style.backgroundColor = new Color(0.90f, 0.49f, 0.13f, 1f);
                badge.style.paddingLeft = 5;
                badge.style.paddingRight = 5;
                badge.style.paddingTop = 1;
                badge.style.paddingBottom = 1;
                badge.style.unityTextAlign = TextAnchor.MiddleCenter;
                badge.style.borderTopLeftRadius = 8;
                badge.style.borderTopRightRadius = 8;
                badge.style.borderBottomLeftRadius = 8;
                badge.style.borderBottomRightRadius = 8;
                wrap.Add(badge);
            }
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
            row.style.flexGrow = 1;
            host.Add(row);

            row.Add(MakeRoleButton("Skater", !isGoalie, embedded, () => callbacks.OnSelectRole?.Invoke(0)));
            row.Add(MakeRoleButton("Goalie", isGoalie, embedded, () => callbacks.OnSelectRole?.Invoke(1)));
        }

        /// <summary>
        /// Render-scope + presentation toggles (client-only). Side by side under
        /// Position | Lighting.
        /// </summary>
        internal static void FillPerfSection(
            VisualElement host, VisualElement lightingSectionHost, bool embedded)
        {
            if (host == null) return;
            host.Clear();

            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Stretch;
            row.style.justifyContent = Justify.Center;
            host.Add(row);

            bool renderAll = PracticePresentation.RenderAllRinks;
            Button renderBtn = MakeRoleButton(
                renderAll ? "Render All Rinks" : "Render Just My Rink",
                renderAll,
                embedded,
                delegate
                {
                    PracticePresentation.SetRenderAllRinks(!PracticePresentation.RenderAllRinks);
                    FillPerfSection(host, lightingSectionHost, embedded);
                });
            renderBtn.style.flexGrow = 1;
            renderBtn.style.flexBasis = 0;
            renderBtn.style.minWidth = embedded ? 140 : 180;
            renderBtn.style.maxWidth = embedded ? 280 : 340;
            row.Add(renderBtn);

            bool allowChanges = PracticePresentation.AllowRinkChanges;
            Button changesBtn = MakeRoleButton(
                allowChanges ? "Allow Rink Changes" : "Limit Rink Changes",
                allowChanges,
                embedded,
                delegate
                {
                    PracticePresentation.SetAllowRinkChanges(!PracticePresentation.AllowRinkChanges);
                    FillPerfSection(host, lightingSectionHost, embedded);
                    if (lightingSectionHost != null)
                        FillLightingSection(lightingSectionHost, embedded);
                });
            changesBtn.style.flexGrow = 1;
            changesBtn.style.flexBasis = 0;
            changesBtn.style.minWidth = embedded ? 140 : 180;
            changesBtn.style.maxWidth = embedded ? 280 : 340;
            row.Add(changesBtn);
        }

        /// <summary>
        /// Day/night opt-in plus a 0–24h drag timeline. Client-side only. Height is
        /// fixed whether the cycle is on or off so toggling never reflows the rink grid.
        /// </summary>
        internal static void FillLightingSection(VisualElement host, bool embedded)
        {
            if (host == null) return;
            host.Clear();

            // Match the Position column height whether the cycle is on or off.
            host.style.minHeight = embedded ? 72 : 88;

            bool allowChanges = PracticePresentation.AllowRinkChanges;
            bool arenaOn = ArenaLighting.ArenaLightingEnabled;
            bool dayNight = ArenaLighting.DayNightEnabled;

            Label heading = MakeLabel("Lighting", embedded ? 13 : 14, MutedText, FontStyle.Bold);
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            heading.style.marginTop = embedded ? 6 : 8;
            heading.style.marginBottom = 6;
            host.Add(heading);

            VisualElement toggleRow = new VisualElement();
            toggleRow.style.flexDirection = FlexDirection.Row;
            toggleRow.style.justifyContent = Justify.Center;
            toggleRow.style.alignItems = Align.Stretch;
            toggleRow.style.flexWrap = Wrap.Wrap;
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
            arenaToggle.style.minWidth = embedded ? 140 : 170;
            arenaToggle.style.flexGrow = 1;
            arenaToggle.style.maxWidth = embedded ? 220 : 260;
            arenaToggle.style.marginRight = 6;
            toggleRow.Add(arenaToggle);

            // Day/night drives the MultiSheet lighting rig, so it needs arena lighting on.
            bool dayNightInteractive = allowChanges && arenaOn;
            Button toggle = MakeRoleButton(
                dayNight ? "Day/Night Cycle: ON" : "Day/Night Cycle: OFF",
                dayNight && dayNightInteractive,
                embedded,
                delegate
                {
                    if (!PracticePresentation.AllowRinkChanges || !ArenaLighting.ArenaLightingEnabled) return;
                    ArenaLighting.SetDayNightEnabled(!ArenaLighting.DayNightEnabled);
                    FillLightingSection(host, embedded);
                });
            toggle.style.minWidth = embedded ? 150 : 200;
            toggle.style.flexGrow = 1;
            toggle.style.maxWidth = embedded ? 240 : 300;
            toggle.style.opacity = dayNightInteractive ? 1f : 0.45f;
            toggle.SetEnabled(dayNightInteractive);
            toggleRow.Add(toggle);

            // Always allocate the timeline row — hide when cycle is off so layout stays put.
            bool timelineLive = dayNight && dayNightInteractive;
            VisualElement timeRow = new VisualElement();
            timeRow.style.flexDirection = FlexDirection.Row;
            timeRow.style.alignItems = Align.Center;
            timeRow.style.justifyContent = Justify.Center;
            timeRow.style.marginTop = 8;
            timeRow.style.height = embedded ? 26 : 30;
            timeRow.style.width = new Length(100, LengthUnit.Percent);
            timeRow.style.opacity = timelineLive ? 1f : 0f;
            timeRow.pickingMode = timelineLive ? PickingMode.Position : PickingMode.Ignore;
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
                if (!PracticePresentation.AllowRinkChanges
                    || !ArenaLighting.DayNightEnabled) return;
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
                host.style.flexShrink = 1;
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

            for (int start = 0; start < total; start += perRow)
            {
                int end = Mathf.Min(start + perRow, total);
                VisualElement row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.flexWrap = Wrap.NoWrap;
                row.style.width = new Length(100, LengthUnit.Percent);
                row.style.justifyContent = Justify.SpaceBetween;
                row.style.alignItems = Align.Stretch;
                row.style.marginBottom = end < total ? (embedded ? 6 : 10) : 0;
                if (embedded)
                {
                    // Equal share of the leftover board height — previews fill each cell.
                    row.style.flexGrow = 1;
                    row.style.flexShrink = 1;
                    row.style.minHeight = 0;
                }
                host.Add(row);

                for (int i = start; i < end; i++)
                {
                    bool isLast = i == end - 1;
                    row.Add(MakeRinkTile(payload, i, localRink, callbacks, embedded, isLast));
                }
            }
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
                foreach (VisualElement tile in child.Children())
                {
                    VisualElement preview = tile.Q<VisualElement>("RinkPreview_" + tileIndex);
                    if (preview == null && tile.childCount > 0) preview = tile[0];
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
            bool isLast)
        {
            RinkStatusEntry entry = payload.Rinks[index];
            bool isHere = index == localRink;
            bool isFull = payload.Capacity > 0 && entry.Count >= payload.Capacity && !isHere;

            // Full-bleed preview. Embedded scoreboard: tile/preview flex-grow so the grid
            // eats leftover board height instead of leaving a blank band. Fullscreen MOTD
            // keeps fixed heights (no flex parent to fill).
            bool dense = payload.Rinks.Count > 6;
            int previewH = dense ? 156 : 184;

            VisualElement tile = new VisualElement();
            tile.name = "RinkTile_" + index;
            tile.style.flexGrow = 1;
            tile.style.flexShrink = 1;
            tile.style.flexBasis = 0;
            tile.style.minWidth = 0;
            tile.style.marginRight = isLast ? 0 : (embedded ? 6 : 10);
            tile.style.backgroundColor = ElevatedBg;
            tile.style.flexDirection = FlexDirection.Column;
            tile.style.overflow = Overflow.Hidden;
            if (embedded)
            {
                tile.style.height = new Length(100, LengthUnit.Percent);
                tile.style.minHeight = dense ? 96 : 112;
            }
            // Constant 2 px border — UITK borders are part of layout, so hover must
            // only swap the color, never the width, or the whole grid shifts around.
            Color idleBorder = isHere ? CtaBg : isFull ? FullRed : BorderStrong;
            SetBorder(tile, 2, idleBorder);

            VisualElement preview = new VisualElement();
            preview.name = "RinkPreview_" + index;
            if (embedded)
            {
                preview.style.flexGrow = 1;
                preview.style.flexShrink = 1;
                preview.style.minHeight = dense ? 96 : 112;
            }
            else
            {
                preview.style.height = previewH;
                preview.style.flexShrink = 0;
            }
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

            VisualElement nameChip = MakeOverlayChip(
                entry.Label ?? ("Rink " + (index + 1)),
                embedded ? 12 : 14,
                nameColor,
                Align.FlexStart);
            nameChip.style.left = 6;
            nameChip.style.top = 6;
            overlay.Add(nameChip);

            VisualElement countChip = MakeOverlayChip(
                entry.Count + "/" + payload.Capacity,
                embedded ? 12 : 14,
                countColor,
                Align.FlexEnd);
            countChip.style.right = 6;
            countChip.style.top = 6;
            overlay.Add(countChip);

            tile.tooltip = isFull
                ? (entry.Label + " is full")
                : isHere ? "Respawn at " + entry.Label : "Teleport to " + entry.Label;

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
            chip.style.paddingTop = 3;
            chip.style.paddingBottom = 3;
            chip.style.borderTopLeftRadius = 3;
            chip.style.borderTopRightRadius = 3;
            chip.style.borderBottomLeftRadius = 3;
            chip.style.borderBottomRightRadius = 3;
            chip.pickingMode = PickingMode.Ignore;

            Label label = MakeLabel(text, fontSize, color, FontStyle.Bold);
            label.style.unityTextAlign = horizontal == Align.FlexEnd
                ? TextAnchor.MiddleRight
                : TextAnchor.MiddleLeft;
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

        /// <summary>
        /// One footer link tile, styled like the PHL public MOTD: phlstats gets a wide
        /// wordmark rectangle; Discord/Twitch PNGs are full brand tiles painted
        /// edge-to-edge; YouTube's transparent mark sits on YouTube red.
        /// </summary>
        private static void AddCommunityButton(
            VisualElement parent, string name, string url, string iconName)
        {
            const int tileH = 43;
            const int tileSquare = 43;
            const int tilePhlstatsW = 101;

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
