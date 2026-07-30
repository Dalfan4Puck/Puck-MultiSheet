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
        private static readonly Color VoteOrange = new Color(0.90f, 0.49f, 0.13f, 1f);
        private static readonly Color NewStickerBg = new Color(0.95f, 0.65f, 0.15f, 1f);
        private static readonly Color CyanAccent = new Color(0.20f, 0.75f, 0.85f, 1f);

        internal static void FillKeybindsSection(VisualElement host, bool embedded)
        {
            if (host == null) return;
            host.Clear();

            bool open = MultiSheetClientSettings.KeybindsSectionOpen;
            VisualElement shell = BuildCollapsibleShell(
                host,
                "Keybinds",
                open,
                embedded,
                () =>
                {
                    MultiSheetClientSettings.KeybindsSectionOpen = !MultiSheetClientSettings.KeybindsSectionOpen;
                    MultiSheetClientSettings.Save();
                    FillKeybindsSection(host, embedded);
                    RinkScoreboardTab.RefreshBoardLayout();
                });

            if (!open) return;

            VisualElement body = shell.Q<VisualElement>("CollapsibleBody");
            if (body == null) return;

            AddSpawnPuckRow(host, body, embedded);
            AddSlidableRow(host, body, embedded);
            AddDualRow(body, embedded, "Tab = Open this menu", null);
            AddDualRow(body, embedded, "Chat: /passer — pass bump board in front of you", null);
            AddDualRow(body, embedded, "Chat: /sheet — flat pushable sheet", null);
        }

        internal static void FillRadioInfoSection(VisualElement host, bool embedded)
        {
            if (host == null) return;
            host.Clear();

            bool open = MultiSheetClientSettings.RadioInfoSectionOpen;
            VisualElement shell = BuildCollapsibleShell(
                host,
                "Radio",
                open,
                embedded,
                () =>
                {
                    MultiSheetClientSettings.RadioInfoSectionOpen = !MultiSheetClientSettings.RadioInfoSectionOpen;
                    MultiSheetClientSettings.Save();
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

        private static void AddSpawnPuckRow(VisualElement sectionHost, VisualElement body, bool embedded)
        {
            string key = MultiSheetClientSettings.SpawnPuckKey;
            if (string.IsNullOrWhiteSpace(key)) key = "R";
            if (key.Length == 1) key = key.ToUpperInvariant();

            bool listening = body.userData is string ud && ud == "spawnListening";

            VisualElement[] cols = BuildDualRowShell(body);
            Label left = RinkPanelBuilder.MakeLabel(
                "Spawn puck = " + key,
                embedded ? 11 : 12,
                RinkPanelBuilder.TextColor,
                FontStyle.Bold);
            left.style.unityTextAlign = TextAnchor.MiddleLeft;
            cols[0].Add(left);

            Button rebind = RinkPanelBuilder.MakeButton(
                listening ? "…" : key,
                RinkPanelBuilder.ButtonBg,
                RinkPanelBuilder.ButtonHover,
                () => { });
            rebind.style.width = embedded ? 72 : KeybindRightWidth;
            rebind.style.height = 28;
            rebind.focusable = true;
            rebind.RegisterCallback<ClickEvent>(evt =>
            {
                body.userData = "spawnListening";
                rebind.text = "…";
                rebind.Focus();
                evt.StopPropagation();
            });
            rebind.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (body.userData as string != "spawnListening") return;
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
                MultiSheetClientSettings.SpawnPuckKey = pressed;
                MultiSheetClientSettings.Save();
                body.userData = null;
                FillKeybindsSection(sectionHost, embedded);
                evt.StopPropagation();
            });
            rebind.RegisterCallback<BlurEvent>(_ =>
            {
                if (body.userData as string == "spawnListening")
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

        private static void AddSlidableRow(VisualElement sectionHost, VisualElement body, bool embedded)
        {
            bool enabled = GetSlidablePhysicsEnabled();
            VisualElement[] cols = BuildDualRowShell(body);

            VisualElement left = new VisualElement();
            left.style.flexDirection = FlexDirection.Row;
            left.style.alignItems = Align.Center;
            left.style.flexGrow = 1;
            left.style.minWidth = 0;
            left.style.overflow = Overflow.Hidden;

            Label main = RinkPanelBuilder.MakeLabel(
                "Slidable Props (Speakers / Foam Pad)",
                embedded ? 11 : 12,
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
                embedded ? 10 : 11,
                RinkPanelBuilder.MutedText,
                FontStyle.Normal);
            sub.style.flexShrink = 0;
            left.Add(sub);
            cols[0].Add(left);

            Button toggle = RinkPanelBuilder.MakeStateButton(
                enabled ? "Enabled" : "Disabled",
                enabled,
                () => ToggleSlidablePhysics(sectionHost, embedded));
            toggle.style.width = embedded ? 72 : KeybindRightWidth;
            toggle.style.height = 28;
            toggle.style.minWidth = embedded ? 72 : KeybindRightWidth;
            toggle.style.maxWidth = embedded ? 72 : KeybindRightWidth;
            toggle.style.fontSize = 10;
            toggle.style.paddingLeft = 0;
            toggle.style.paddingRight = 0;
            toggle.style.unityTextAlign = TextAnchor.MiddleCenter;
            toggle.tooltip = "Toggle slidable physics (host/admin)";
            cols[1].Add(toggle);
        }

        private static bool GetSlidablePhysicsEnabled()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
                return FlamiePracFeatures.SlidablePhysicsEnabled;

            if (RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload) && payload != null)
                return payload.SlidablePhysicsEnabled;

            return FlamiePracFeatures.SlidablePhysicsEnabled;
        }

        private static void ToggleSlidablePhysics(VisualElement sectionHost, bool embedded)
        {
            bool next = !GetSlidablePhysicsEnabled();
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient)
                return;

            if (nm.IsServer)
            {
                FlamiePracFeatures.SetSlidablePhysicsEnabled(next);
                RinkMotdService.BroadcastStatus();
            }
            else
            {
                RinkMotdService.ClientRequestSetSlidable(next);
                if (RinkMotdUI.TryGetLastPayload(out RinkMotdPayload payload) && payload != null)
                    payload.SlidablePhysicsEnabled = next;
            }

            FillKeybindsSection(sectionHost, embedded);
        }

        private static void AddDualRow(VisualElement body, bool embedded, string text, VisualElement right)
        {
            VisualElement[] cols = BuildDualRowShell(body);
            Label left = RinkPanelBuilder.MakeLabel(
                text,
                embedded ? 11 : 12,
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
