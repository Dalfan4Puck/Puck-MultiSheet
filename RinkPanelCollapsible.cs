using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>Keybinds + Radio info collapsible sections for RinkPanelBuilder.</summary>
    internal static class RinkPanelCollapsible
    {
        private const string PhlRadioLibraryUrl = "https://phlstats.com/radio";
        private const int KeybindRowHeight = 36;
        private const int KeybindRightWidth = 80;
        private const int KeybindButtonHeight = 28;
        private const int KeybindFontSize = 12;
        private static bool settingsSectionOpen;
        private static bool keybindsSectionOpen;
        private static bool radioInfoSectionOpen;
        private static readonly Color VoteOrange = new Color(0.90f, 0.49f, 0.13f, 1f);
        private static readonly Color NewStickerBg = new Color(0.95f, 0.65f, 0.15f, 1f);
        private static readonly Color CyanAccent = new Color(0.20f, 0.75f, 0.85f, 1f);

        internal static void ResetCollapsibleSections()
        {
            settingsSectionOpen = false;
            keybindsSectionOpen = false;
            radioInfoSectionOpen = false;
        }

        internal static void FillSettingsSection(
            VisualElement host,
            RinkMotdPayload payload,
            RinkPanelBuilder.Callbacks callbacks,
            bool embedded,
            out VisualElement roleSectionHost,
            out VisualElement lightingSectionHost)
        {
            if (host == null)
            {
                roleSectionHost = null;
                lightingSectionHost = null;
                return;
            }

            host.Clear();

            bool open = settingsSectionOpen;
            VisualElement shell = BuildCollapsibleShell(
                host,
                "Settings",
                open,
                embedded,
                () =>
                {
                    settingsSectionOpen = !settingsSectionOpen;
                    FillSettingsSection(host, payload, callbacks, embedded, out _, out _);
                    RinkScoreboardTab.RefreshBoardLayout();
                });

            roleSectionHost = null;
            lightingSectionHost = null;
            if (!open) return;

            VisualElement body = shell.Q<VisualElement>("CollapsibleBody");
            if (body == null) return;

            VisualElement controlsRow = new VisualElement();
            controlsRow.style.flexDirection = FlexDirection.Row;
            controlsRow.style.alignItems = Align.Stretch;
            controlsRow.style.justifyContent = Justify.Center;
            controlsRow.style.flexShrink = 0;
            body.Add(controlsRow);

            VisualElement roleColumnHost = new VisualElement();
            roleColumnHost.style.flexGrow = 1;
            roleColumnHost.style.flexShrink = 0;
            roleColumnHost.style.flexBasis = 0;
            roleColumnHost.style.minWidth = 0;
            roleColumnHost.style.flexDirection = FlexDirection.Column;
            controlsRow.Add(roleColumnHost);

            roleSectionHost = new VisualElement();
            roleSectionHost.style.flexShrink = 0;
            roleColumnHost.Add(roleSectionHost);
            RinkPanelBuilder.FillRoleSection(roleSectionHost, payload, callbacks, embedded);

            VisualElement vDivider = new VisualElement();
            vDivider.style.width = 1;
            vDivider.style.alignSelf = Align.Stretch;
            vDivider.style.backgroundColor = RinkPanelBuilder.ColumnRule;
            vDivider.style.marginLeft = embedded ? 10 : 14;
            vDivider.style.marginRight = embedded ? 10 : 14;
            vDivider.style.marginTop = embedded ? 10 : 14;
            vDivider.style.marginBottom = embedded ? 8 : 12;
            vDivider.pickingMode = PickingMode.Ignore;
            controlsRow.Add(vDivider);

            lightingSectionHost = new VisualElement();
            lightingSectionHost.style.flexGrow = 1;
            lightingSectionHost.style.flexShrink = 0;
            lightingSectionHost.style.flexBasis = 0;
            lightingSectionHost.style.minWidth = 0;
            controlsRow.Add(lightingSectionHost);
            RinkPanelBuilder.FillLightingSection(lightingSectionHost, embedded);
        }

        internal static void FillKeybindsSection(VisualElement host, bool embedded)
        {
            if (host == null) return;
            host.Clear();

            bool open = keybindsSectionOpen;
            VisualElement shell = BuildCollapsibleShell(
                host,
                "Keybinds",
                open,
                embedded,
                () =>
                {
                    keybindsSectionOpen = !keybindsSectionOpen;
                    FillKeybindsSection(host, embedded);
                    RinkScoreboardTab.RefreshBoardLayout();
                });

            if (!open) return;

            VisualElement body = shell.Q<VisualElement>("CollapsibleBody");
            if (body == null) return;

            AddKeybindRow(host, body, embedded, "spawn",
                "Spawn puck",
                () => MultiSheetClientSettings.SpawnPuckKey,
                v => MultiSheetClientSettings.SpawnPuckKey = v);
            AddKeybindRow(host, body, embedded, "role",
                "Toggle skater / goalie (in place)",
                () => MultiSheetClientSettings.ToggleRoleKey,
                v => MultiSheetClientSettings.ToggleRoleKey = v);
            AddSlidableKeybindRow(host, body, embedded);
            AddDualRow(body, embedded, "Tab = Open this menu", null);
            AddDualRow(body, embedded, "Chat: /passer — pass bump board in front of you", null);
            AddDualRow(body, embedded, "Chat: /sheet — flat pushable sheet", null);
        }

        internal static void FillRadioInfoSection(VisualElement host, bool embedded)
        {
            if (host == null) return;
            host.Clear();

            bool open = radioInfoSectionOpen;
            VisualElement shell = BuildCollapsibleShell(
                host,
                "Radio",
                open,
                embedded,
                () =>
                {
                    radioInfoSectionOpen = !radioInfoSectionOpen;
                    FillRadioInfoSection(host, embedded);
                    RinkScoreboardTab.RefreshBoardLayout();
                });

            if (!open) return;

            VisualElement body = shell.Q<VisualElement>("CollapsibleBody");
            if (body == null) return;

            VisualElement welcomeRow = new VisualElement();
            welcomeRow.style.flexDirection = FlexDirection.Row;
            welcomeRow.style.alignItems = Align.Center;
            welcomeRow.style.justifyContent = Justify.SpaceBetween;
            welcomeRow.style.width = new Length(100, LengthUnit.Percent);
            welcomeRow.style.marginBottom = 4;
            body.Add(welcomeRow);

            Label welcome = RinkPanelBuilder.MakeLabel(
                "Welcome to PHL Radio.",
                embedded ? 11 : 12,
                RinkPanelBuilder.TextColor,
                FontStyle.Bold);
            welcome.style.flexGrow = 1;
            welcome.style.flexShrink = 1;
            welcome.style.marginBottom = 0;
            welcomeRow.Add(welcome);

            Button link = RinkPanelBuilder.MakeButton(
                "PHLStats.com/Radio",
                RinkPanelBuilder.ButtonBg,
                RinkPanelBuilder.ButtonHover,
                () => RinkPanelBuilder.OpenExternalUrlPublic(PhlRadioLibraryUrl));
            link.style.flexShrink = 0;
            link.style.width = StyleKeyword.Auto;
            link.style.minWidth = StyleKeyword.Null;
            link.style.height = embedded ? 24 : 28;
            link.style.marginLeft = 8;
            link.style.color = CyanAccent;
            link.style.unityFontStyleAndWeight = FontStyle.Bold;
            link.style.fontSize = embedded ? 11 : 12;
            link.style.unityTextAlign = TextAnchor.MiddleRight;
            link.style.borderBottomWidth = 1;
            link.style.borderBottomColor = CyanAccent;
            link.style.backgroundColor = Color.clear;
            link.style.paddingLeft = 0;
            link.style.paddingRight = 0;
            welcomeRow.Add(link);

            Label premium = RinkPanelBuilder.MakeLabel(
                "Premium users can add up to fifteen songs to the library at the following link.",
                embedded ? 11 : 12,
                RinkPanelBuilder.MutedText,
                FontStyle.Normal);
            premium.style.whiteSpace = WhiteSpace.Normal;
            premium.style.marginBottom = 8;
            body.Add(premium);

            bool playEverywhere = MultiSheetClientSettings.RadioPlayEverywhere;
            Button playModeBtn = RinkPanelBuilder.MakeStateButton(
                playEverywhere ? "Play Everywhere: ON" : "Near Speakers Only",
                playEverywhere,
                delegate
                {
                    MultiSheetClientSettings.RadioPlayEverywhere = !MultiSheetClientSettings.RadioPlayEverywhere;
                    MultiSheetClientSettings.Save();
                    MultiSheetClientSettings.Flush();
                    FillRadioInfoSection(host, embedded);
                });
            playModeBtn.style.width = new Length(100, LengthUnit.Percent);
            playModeBtn.style.height = embedded ? 30 : 34;
            playModeBtn.style.marginBottom = 8;
            playModeBtn.style.fontSize = embedded ? 11 : 12;
            body.Add(playModeBtn);

            float speakerRange = MultiSheetClientSettings.RadioSpeakerRange;
            VisualElement rangeHeader = new VisualElement();
            rangeHeader.style.flexDirection = FlexDirection.Row;
            rangeHeader.style.alignItems = Align.Center;
            rangeHeader.style.justifyContent = Justify.SpaceBetween;
            rangeHeader.style.marginBottom = 4;
            body.Add(rangeHeader);

            Label rangeLabel = RinkPanelBuilder.MakeLabel(
                "Speaker Range",
                embedded ? 11 : 12,
                RinkPanelBuilder.TextColor,
                FontStyle.Bold);
            rangeHeader.Add(rangeLabel);

            Label rangeValue = RinkPanelBuilder.MakeLabel(
                Mathf.RoundToInt(speakerRange) + "m",
                embedded ? 11 : 12,
                RinkPanelBuilder.MutedText,
                FontStyle.Normal);
            rangeValue.style.unityTextAlign = TextAnchor.MiddleRight;
            rangeHeader.Add(rangeValue);

            RangeSlider rangeSlider = new RangeSlider(
                5f,
                100f,
                speakerRange,
                v =>
                {
                    MultiSheetClientSettings.RadioSpeakerRange = v;
                    MultiSheetClientSettings.Save();
                    rangeValue.text = Mathf.RoundToInt(v) + "m";
                },
                () => MultiSheetClientSettings.Flush());
            rangeSlider.style.width = new Length(100, LengthUnit.Percent);
            rangeSlider.style.height = embedded ? 24 : 28;
            rangeSlider.style.marginBottom = 4;
            body.Add(rangeSlider);

            if (playEverywhere)
            {
                rangeSlider.SetEnabled(false);
                rangeSlider.style.opacity = 0.45f;
            }
        }

        private static VisualElement BuildCollapsibleShell(
            VisualElement host,
            string title,
            bool open,
            bool embedded,
            Action onToggle)
        {
            VisualElement shell = new VisualElement();
            shell.style.borderTopWidth = 1;
            shell.style.borderRightWidth = 1;
            shell.style.borderBottomWidth = 1;
            shell.style.borderLeftWidth = 1;
            shell.style.borderTopColor = RinkPanelBuilder.BorderStrong;
            shell.style.borderRightColor = RinkPanelBuilder.BorderStrong;
            shell.style.borderBottomColor = RinkPanelBuilder.BorderStrong;
            shell.style.borderLeftColor = RinkPanelBuilder.BorderStrong;
            shell.style.backgroundColor = RinkPanelBuilder.ElevatedBg;
            shell.style.flexShrink = 0;
            host.Add(shell);

            Button header = RinkPanelBuilder.MakeButton(
                (open ? "▾ " : "▸ ") + title,
                RinkPanelBuilder.HeaderBg,
                RinkPanelBuilder.ButtonHover,
                onToggle);
            header.style.width = new Length(100, LengthUnit.Percent);
            header.style.height = embedded ? 30 : 34;
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.fontSize = embedded ? 11 : 12;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.color = RinkPanelBuilder.TextColor;
            shell.Add(header);

            Label showHide = RinkPanelBuilder.MakeLabel(
                open ? "Hide" : "Show",
                10,
                RinkPanelBuilder.MutedText,
                FontStyle.Normal);
            showHide.style.position = Position.Absolute;
            showHide.style.right = 10;
            showHide.style.top = embedded ? 8 : 10;
            showHide.pickingMode = PickingMode.Ignore;
            shell.Add(showHide);

            if (open)
            {
                VisualElement body = new VisualElement { name = "CollapsibleBody" };
                body.style.paddingLeft = embedded ? 10 : 12;
                body.style.paddingRight = embedded ? 10 : 12;
                body.style.paddingTop = embedded ? 8 : 10;
                body.style.paddingBottom = embedded ? 10 : 12;
                body.style.borderTopWidth = 1;
                body.style.borderTopColor = RinkPanelBuilder.ColumnRule;
                shell.Add(body);
            }

            return shell;
        }

        private static void AddKeybindRow(
            VisualElement sectionHost,
            VisualElement body,
            bool embedded,
            string listenId,
            string labelPrefix,
            Func<string> getKey,
            Action<string> setKey)
        {
            string key = ClientKeybindHelper.NormalizeDisplayKey(getKey());
            bool listening = body.userData is string ud && ud == listenId;

            VisualElement[] cols = BuildDualRowShell(body);
            Label left = RinkPanelBuilder.MakeLabel(
                labelPrefix + " = " + key,
                KeybindFontSize,
                RinkPanelBuilder.TextColor,
                FontStyle.Bold);
            left.style.unityTextAlign = TextAnchor.MiddleLeft;
            cols[0].Add(left);

            Button rebind = RinkPanelBuilder.MakeButton(
                listening ? "…" : key,
                RinkPanelBuilder.ButtonBg,
                RinkPanelBuilder.ButtonHover,
                () => { });
            StyleKeybindButton(rebind, embedded);
            rebind.focusable = true;
            rebind.RegisterCallback<ClickEvent>(evt =>
            {
                body.userData = listenId;
                rebind.text = "…";
                rebind.Focus();
                evt.StopPropagation();
            });
            rebind.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (body.userData as string != listenId) return;
                if (evt.keyCode == KeyCode.Escape)
                {
                    body.userData = null;
                    FillKeybindsSection(sectionHost, embedded);
                    evt.StopPropagation();
                    return;
                }
                if (evt.keyCode == KeyCode.Tab || evt.keyCode == KeyCode.LeftShift ||
                    evt.keyCode == KeyCode.RightShift || evt.keyCode == KeyCode.LeftControl ||
                    evt.keyCode == KeyCode.RightControl || evt.keyCode == KeyCode.LeftAlt ||
                    evt.keyCode == KeyCode.RightAlt)
                    return;

                string pressed = evt.keyCode.ToString();
                if (pressed.Length == 1) pressed = pressed.ToUpperInvariant();
                setKey(pressed);
                MultiSheetClientSettings.Save();
                body.userData = null;
                FillKeybindsSection(sectionHost, embedded);
                evt.StopPropagation();
            });
            rebind.RegisterCallback<BlurEvent>(_ =>
            {
                if (body.userData as string == listenId)
                {
                    body.userData = null;
                    FillKeybindsSection(sectionHost, embedded);
                }
            });
            if (listening)
            {
                RinkPanelBuilder.SetBorder(rebind, 2, CyanAccent);
                rebind.schedule.Execute(() => rebind.Focus()).ExecuteLater(1);
            }
            cols[1].Add(rebind);
        }

        private static void AddSlidableKeybindRow(VisualElement sectionHost, VisualElement body, bool embedded)
        {
            bool enabled = GetSlidablePhysicsEnabled();
            string key = ClientKeybindHelper.NormalizeDisplayKey(MultiSheetClientSettings.SlidableToggleKey);
            bool listening = body.userData is string ud && ud == "slidable";

            VisualElement[] cols = BuildDualRowShell(body);

            VisualElement left = new VisualElement();
            left.style.flexDirection = FlexDirection.Row;
            left.style.alignItems = Align.Center;
            left.style.flexGrow = 1;
            left.style.minWidth = 0;
            left.style.overflow = Overflow.Hidden;

            Label main = RinkPanelBuilder.MakeLabel(
                "Slidable props (" + (enabled ? "Enabled" : "Disabled") + ") = " + key,
                KeybindFontSize,
                RinkPanelBuilder.TextColor,
                FontStyle.Bold);
            main.style.flexShrink = 1;
            main.style.overflow = Overflow.Hidden;
            main.style.textOverflow = TextOverflow.Ellipsis;
            left.Add(main);

            Label sticker = RinkPanelBuilder.MakeLabel("NEW", 8, Color.black, FontStyle.Bold);
            sticker.style.backgroundColor = NewStickerBg;
            sticker.style.paddingLeft = 3;
            sticker.style.paddingRight = 3;
            sticker.style.marginLeft = 4;
            sticker.style.marginRight = 4;
            sticker.style.flexShrink = 0;
            left.Add(sticker);

            Label sub = RinkPanelBuilder.MakeLabel(
                "· may cause fps dips",
                KeybindFontSize,
                RinkPanelBuilder.MutedText,
                FontStyle.Normal);
            sub.style.flexShrink = 0;
            left.Add(sub);
            cols[0].Add(left);

            Button rebind = RinkPanelBuilder.MakeButton(
                listening ? "…" : key,
                RinkPanelBuilder.ButtonBg,
                RinkPanelBuilder.ButtonHover,
                () => { });
            StyleKeybindButton(rebind, embedded);
            rebind.tooltip = "Rebind slidable toggle key (default L)";
            rebind.focusable = true;
            rebind.RegisterCallback<ClickEvent>(evt =>
            {
                body.userData = "slidable";
                rebind.text = "…";
                rebind.Focus();
                evt.StopPropagation();
            });
            rebind.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (body.userData as string != "slidable") return;
                if (evt.keyCode == KeyCode.Escape)
                {
                    body.userData = null;
                    FillKeybindsSection(sectionHost, embedded);
                    evt.StopPropagation();
                    return;
                }
                if (evt.keyCode == KeyCode.Tab || evt.keyCode == KeyCode.LeftShift ||
                    evt.keyCode == KeyCode.RightShift || evt.keyCode == KeyCode.LeftControl ||
                    evt.keyCode == KeyCode.RightControl || evt.keyCode == KeyCode.LeftAlt ||
                    evt.keyCode == KeyCode.RightAlt)
                    return;

                string pressed = evt.keyCode.ToString();
                if (pressed.Length == 1) pressed = pressed.ToUpperInvariant();
                MultiSheetClientSettings.SlidableToggleKey = pressed;
                MultiSheetClientSettings.Save();
                body.userData = null;
                FillKeybindsSection(sectionHost, embedded);
                evt.StopPropagation();
            });
            rebind.RegisterCallback<BlurEvent>(_ =>
            {
                if (body.userData as string == "slidable")
                {
                    body.userData = null;
                    FillKeybindsSection(sectionHost, embedded);
                }
            });
            if (listening)
            {
                RinkPanelBuilder.SetBorder(rebind, 2, CyanAccent);
                rebind.schedule.Execute(() => rebind.Focus()).ExecuteLater(1);
            }
            cols[1].Add(rebind);
        }

        private static void StyleKeybindButton(Button button, bool embedded)
        {
            if (button == null) return;
            button.style.width = KeybindRightWidth;
            button.style.minWidth = KeybindRightWidth;
            button.style.maxWidth = KeybindRightWidth;
            button.style.height = KeybindButtonHeight;
            button.style.minHeight = KeybindButtonHeight;
            button.style.maxHeight = KeybindButtonHeight;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            button.style.marginTop = 0;
            button.style.marginBottom = 0;
            button.style.fontSize = KeybindFontSize;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.justifyContent = Justify.Center;
            button.style.alignItems = Align.Center;
            CenterKeybindButtonLabel(button);
        }

        private static void CenterKeybindButtonLabel(Button button)
        {
            void Apply()
            {
                Label label = button.Q<Label>();
                if (label == null)
                    return;

                label.style.fontSize = KeybindFontSize;
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.flexGrow = 1;
                label.style.width = new Length(100, LengthUnit.Percent);
                label.style.height = new Length(100, LengthUnit.Percent);
                label.style.paddingLeft = 0;
                label.style.paddingRight = 0;
                label.style.paddingTop = 0;
                label.style.paddingBottom = 0;
                label.style.marginLeft = 0;
                label.style.marginRight = 0;
                label.style.marginTop = 0;
                label.style.marginBottom = 0;
                label.style.alignSelf = Align.Center;
            }

            Apply();
            button.RegisterCallback<GeometryChangedEvent>(_ => Apply());
        }

        private static bool GetSlidablePhysicsEnabled()
        {
            return ActiveRinkResolver.IsSlidableEnabledForLocalRink();
        }

        private static void AddDualRow(VisualElement body, bool embedded, string text, VisualElement right)
        {
            VisualElement[] cols = BuildDualRowShell(body);
            Label left = RinkPanelBuilder.MakeLabel(
                text,
                KeybindFontSize,
                RinkPanelBuilder.MutedText,
                FontStyle.Normal);
            left.style.unityTextAlign = TextAnchor.MiddleLeft;
            cols[0].Add(left);
            if (right != null) cols[1].Add(right);
        }

        private static VisualElement[] BuildDualRowShell(VisualElement body)
        {
            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = KeybindRowHeight;
            row.style.marginBottom = 4;
            body.Add(row);

            VisualElement leftCol = new VisualElement();
            leftCol.style.flexGrow = 1;
            leftCol.style.minWidth = 0;
            leftCol.style.height = KeybindRowHeight;
            leftCol.style.justifyContent = Justify.Center;
            row.Add(leftCol);

            VisualElement rightCol = new VisualElement();
            rightCol.style.width = KeybindRightWidth;
            rightCol.style.flexShrink = 0;
            rightCol.style.height = KeybindRowHeight;
            rightCol.style.justifyContent = Justify.Center;
            rightCol.style.alignItems = Align.FlexEnd;
            row.Add(rightCol);

            return new[] { leftCol, rightCol };
        }

        /// <summary>Horizontal 5–100 slider for speaker hear distance (meters).</summary>
        private sealed class RangeSlider : VisualElement
        {
            private readonly VisualElement track;
            private readonly VisualElement fill;
            private readonly VisualElement thumb;
            private readonly Action<float> onChanged;
            private readonly Action onCommit;
            private readonly float min;
            private readonly float max;
            private float value;
            private bool dragging;

            internal RangeSlider(
                float min,
                float max,
                float initial,
                Action<float> onChanged,
                Action onCommit)
            {
                this.min = min;
                this.max = max;
                this.onChanged = onChanged;
                this.onCommit = onCommit;
                value = Mathf.Clamp(initial, min, max);
                pickingMode = PickingMode.Position;

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
                track.pickingMode = PickingMode.Ignore;
                Add(track);

                fill = new VisualElement();
                fill.style.position = Position.Absolute;
                fill.style.left = 0;
                fill.style.height = 6;
                fill.style.top = new Length(50, LengthUnit.Percent);
                fill.style.marginTop = -3;
                fill.style.backgroundColor = CyanAccent;
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

                RegisterCallback<GeometryChangedEvent>(_ => LayoutThumb());
                RegisterCallback<PointerDownEvent>(OnPointerDown);
                RegisterCallback<PointerMoveEvent>(OnPointerMove);
                RegisterCallback<PointerUpEvent>(OnPointerUp);
                RegisterCallback<PointerCaptureOutEvent>(_ => dragging = false);
            }

            private void OnPointerDown(PointerDownEvent evt)
            {
                dragging = true;
                this.CapturePointer(evt.pointerId);
                SetFromPointer(evt.localPosition.x);
                evt.StopPropagation();
            }

            private void OnPointerMove(PointerMoveEvent evt)
            {
                if (!dragging) return;
                SetFromPointer(evt.localPosition.x);
                evt.StopPropagation();
            }

            private void OnPointerUp(PointerUpEvent evt)
            {
                if (!dragging) return;
                dragging = false;
                this.ReleasePointer(evt.pointerId);
                onCommit?.Invoke();
                evt.StopPropagation();
            }

            private void SetFromPointer(float localX)
            {
                float w = resolvedStyle.width;
                if (w <= 1f) return;
                float t = Mathf.Clamp01(localX / w);
                value = Mathf.Lerp(min, max, t);
                onChanged?.Invoke(value);
                LayoutThumb();
            }

            private void LayoutThumb()
            {
                float w = resolvedStyle.width;
                if (w <= 1f) return;
                float t = (value - min) / (max - min);
                float x = t * w;
                fill.style.width = x;
                thumb.style.left = x;
                thumb.style.marginLeft = -7;
            }
        }
    }
}
