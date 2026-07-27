using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Collapsible UITK radio panel on UIManager.RootVisualElement (MultiSheet / Toaster pattern).
/// Hidden by default — small bottom-left chip opens controls. Does not fullscreen-block join UI.
/// </summary>
public static class RadioHudUI
{
    private const string HostName = "FlamiePrac_RadioHudHost";
    private const string PanelName = "FlamiePrac_RadioPanel";
    private const string ChipName = "FlamiePrac_RadioChip";
    private const string VolumeSliderName = "FlamiePrac_VolumeSlider";
    private const string TitleName = "FlamiePrac_RadioTitle";
    private const string NextName = "FlamiePrac_RadioNext";
    private const string TimeName = "FlamiePrac_RadioTime";
    private const string VolumeLabelName = "FlamiePrac_RadioVolumeLabel";
    private const string ProgressFillName = "FlamiePrac_RadioProgressFill";

    private static VisualElement host;
    private static VisualElement panel;
    private static Button chipButton;
    private static Label titleLabel;
    private static Label nextLabel;
    private static Label timeLabel;
    private static Label volumeLabel;
    private static VolumeSlider volumeSlider;
    private static VisualElement progressFill;
    private static bool expanded;
    private static bool attached;
    private static float nextAttachAttempt;
    private static RadioController subscribedRadio;

    private static readonly Color PanelBg = new Color(0.075f, 0.075f, 0.075f, 0.94f);
    private static readonly Color HeaderBg = new Color(0f, 0f, 0f, 1f);
    private static readonly Color TextColor = new Color(0.929f, 0.929f, 0.929f, 1f);
    private static readonly Color MutedText = new Color(0.58f, 0.58f, 0.58f, 1f);
    private static readonly Color ButtonBg = new Color(0.165f, 0.165f, 0.165f, 1f);
    private static readonly Color ButtonHover = new Color(0.239f, 0.239f, 0.239f, 1f);
    private static readonly Color Accent = new Color(0.25f, 0.75f, 1f, 1f);

    public static void Tick()
    {
        if (Application.isBatchMode)
        {
            TearDown();
            return;
        }

        CleanupLegacyUi();

        if (!attached && Time.unscaledTime >= nextAttachAttempt)
        {
            nextAttachAttempt = Time.unscaledTime + 1f;
            TryAttach();
        }

        if (attached)
            RefreshFromController();
    }

    public static void TearDown()
    {
        if (subscribedRadio != null)
        {
            try
            {
                subscribedRadio.StateChanged -= RefreshFromController;
            }
            catch { }

            subscribedRadio = null;
        }

        if (host != null)
        {
            try
            {
                host.RemoveFromHierarchy();
            }
            catch { }
        }

        host = null;
        panel = null;
        chipButton = null;
        titleLabel = null;
        nextLabel = null;
        timeLabel = null;
        volumeLabel = null;
        volumeSlider = null;
        progressFill = null;
        attached = false;
        expanded = false;
    }

    public static void CleanupLegacyUi()
    {
        GameObject canvas = GameObject.Find("RadioCanvas");
        if (canvas != null)
            UnityEngine.Object.Destroy(canvas);

        GameObject legacyEventSystem = GameObject.Find("FlamiePrac_EventSystem");
        if (legacyEventSystem != null)
            UnityEngine.Object.Destroy(legacyEventSystem);
    }

    private static void TryAttach()
    {
        if (RadioController.Instance == null)
            return;

        UIManager manager = MonoBehaviourSingleton<UIManager>.Instance;
        VisualElement root = manager != null ? manager.RootVisualElement : null;
        if (root == null)
            return;

        VisualElement existing = root.Q(HostName);
        if (existing != null)
        {
            host = existing;
            BindElementRefs();

            if (volumeSlider == null)
            {
                try { host.RemoveFromHierarchy(); } catch { }
                host = null;
                panel = null;
                chipButton = null;
                titleLabel = null;
                nextLabel = null;
                timeLabel = null;
                volumeLabel = null;
                progressFill = null;
            }
            else
            {
                attached = true;

                if (subscribedRadio == null && RadioController.Instance != null)
                {
                    subscribedRadio = RadioController.Instance;
                    subscribedRadio.StateChanged -= RefreshFromController;
                    subscribedRadio.StateChanged += RefreshFromController;
                }

                RefreshFromController();
                return;
            }
        }

        BuildUi(root);
        attached = true;
        SetExpanded(false);

        subscribedRadio = RadioController.Instance;
        subscribedRadio.StateChanged -= RefreshFromController;
        subscribedRadio.StateChanged += RefreshFromController;
        RefreshFromController();

        Debug.Log("[FlamiePrac] Radio HUD attached (UITK, collapsible).");
    }

    private static void BindElementRefs()
    {
        if (host == null)
            return;

        panel = host.Q(PanelName);
        chipButton = host.Q<Button>(ChipName);
        titleLabel = host.Q<Label>(TitleName);
        nextLabel = host.Q<Label>(NextName);
        timeLabel = host.Q<Label>(TimeName);
        volumeLabel = host.Q<Label>(VolumeLabelName);
        volumeSlider = host.Q<VolumeSlider>(VolumeSliderName);
        progressFill = host.Q(ProgressFillName);
    }

    private static void BuildUi(VisualElement root)
    {
        host = new VisualElement { name = HostName };
        host.style.position = Position.Absolute;
        host.style.left = 16;
        host.style.bottom = 16;
        host.style.flexDirection = FlexDirection.Column;
        host.style.alignItems = Align.FlexStart;
        host.pickingMode = PickingMode.Ignore;

        panel = new VisualElement { name = PanelName };
        panel.style.width = 320;
        panel.style.backgroundColor = PanelBg;
        panel.style.flexDirection = FlexDirection.Column;
        panel.style.marginBottom = 6;
        panel.style.borderTopWidth = 1;
        panel.style.borderRightWidth = 1;
        panel.style.borderBottomWidth = 1;
        panel.style.borderLeftWidth = 1;
        panel.style.borderTopColor = new Color(0.25f, 0.25f, 0.25f);
        panel.style.borderRightColor = panel.style.borderTopColor.value;
        panel.style.borderBottomColor = panel.style.borderTopColor.value;
        panel.style.borderLeftColor = panel.style.borderTopColor.value;
        panel.pickingMode = PickingMode.Position;
        panel.style.display = DisplayStyle.None;

        VisualElement header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.justifyContent = Justify.SpaceBetween;
        header.style.backgroundColor = HeaderBg;
        header.style.paddingLeft = 10;
        header.style.paddingRight = 6;
        header.style.paddingTop = 6;
        header.style.paddingBottom = 6;

        Label headerTitle = new Label("Training Radio");
        headerTitle.style.color = TextColor;
        headerTitle.style.fontSize = 13;
        header.Add(headerTitle);

        Button closeBtn = MakeCompactButton("✕", 28, () => SetExpanded(false));
        closeBtn.style.color = new Color(0.95f, 0.35f, 0.35f);
        header.Add(closeBtn);
        panel.Add(header);

        VisualElement body = new VisualElement();
        body.style.paddingLeft = 10;
        body.style.paddingRight = 10;
        body.style.paddingTop = 8;
        body.style.paddingBottom = 10;

        titleLabel = new Label("Loading…") { name = TitleName };
        titleLabel.style.color = TextColor;
        titleLabel.style.fontSize = 15;
        titleLabel.style.whiteSpace = WhiteSpace.Normal;
        body.Add(titleLabel);

        nextLabel = new Label("Next: —") { name = NextName };
        nextLabel.style.color = MutedText;
        nextLabel.style.fontSize = 12;
        nextLabel.style.marginTop = 2;
        body.Add(nextLabel);

        VisualElement progressTrack = new VisualElement();
        progressTrack.style.height = 8;
        progressTrack.style.marginTop = 8;
        progressTrack.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
        progressTrack.style.overflow = Overflow.Hidden;

        progressFill = new VisualElement { name = ProgressFillName };
        progressFill.style.height = 8;
        progressFill.style.width = new Length(0, LengthUnit.Percent);
        progressFill.style.backgroundColor = Accent;
        progressTrack.Add(progressFill);
        body.Add(progressTrack);

        timeLabel = new Label("0:00 / 0:00") { name = TimeName };
        timeLabel.style.color = MutedText;
        timeLabel.style.fontSize = 11;
        timeLabel.style.marginTop = 4;
        body.Add(timeLabel);

        VisualElement controls = new VisualElement();
        controls.style.flexDirection = FlexDirection.Row;
        controls.style.justifyContent = Justify.SpaceBetween;
        controls.style.marginTop = 8;

        controls.Add(MakeCompactButton("◀ Prev", 88, () => RadioController.Instance?.RequestTrackChange(RadioController.CmdPrev)));
        controls.Add(MakeCompactButton("⏯", 44, () => RadioController.Instance?.TogglePlayPause()));
        controls.Add(MakeCompactButton("Next ▶", 88, () => RadioController.Instance?.RequestTrackChange(RadioController.CmdNext)));
        body.Add(controls);

        VisualElement volumeRow = new VisualElement();
        volumeRow.style.flexDirection = FlexDirection.Row;
        volumeRow.style.alignItems = Align.Center;
        volumeRow.style.marginTop = 8;
        volumeRow.style.height = 28;
        volumeRow.pickingMode = PickingMode.Position;

        volumeLabel = new Label("Vol") { name = VolumeLabelName };
        volumeLabel.style.color = MutedText;
        volumeLabel.style.fontSize = 11;
        volumeLabel.style.width = 34;
        volumeLabel.pickingMode = PickingMode.Ignore;
        volumeRow.Add(volumeLabel);

        volumeSlider = new VolumeSlider(GetInitialVolumePercent(), pct =>
        {
            if (RadioController.Instance == null)
                return;

            RadioController.Instance.Volume = pct / 100f;
            UpdateVolumeLabel();
        })
        { name = VolumeSliderName };
        volumeSlider.style.flexGrow = 1;
        volumeSlider.style.height = 28;
        volumeRow.Add(volumeSlider);
        body.Add(volumeRow);

        panel.Add(body);
        host.Add(panel);

        chipButton = new Button(() => SetExpanded(!expanded)) { name = ChipName, text = "♪ Radio" };
        chipButton.style.height = 28;
        chipButton.style.minWidth = 88;
        chipButton.style.paddingLeft = 10;
        chipButton.style.paddingRight = 10;
        chipButton.style.fontSize = 12;
        chipButton.style.color = TextColor;
        chipButton.style.backgroundColor = ButtonBg;
        chipButton.style.borderTopWidth = 0;
        chipButton.style.borderRightWidth = 0;
        chipButton.style.borderBottomWidth = 0;
        chipButton.style.borderLeftWidth = 0;
        chipButton.pickingMode = PickingMode.Position;
        chipButton.RegisterCallback<MouseEnterEvent>(_ => chipButton.style.backgroundColor = ButtonHover);
        chipButton.RegisterCallback<MouseLeaveEvent>(_ => chipButton.style.backgroundColor = ButtonBg);
        host.Add(chipButton);

        host.pickingMode = PickingMode.Ignore;
        root.Add(host);
    }

    private static Button MakeCompactButton(string text, float width, Action onClick)
    {
        Button button = new Button(onClick) { text = text };
        button.style.height = 32;
        button.style.width = width;
        button.style.fontSize = 12;
        button.style.color = TextColor;
        button.style.backgroundColor = ButtonBg;
        button.style.borderTopWidth = 0;
        button.style.borderRightWidth = 0;
        button.style.borderBottomWidth = 0;
        button.style.borderLeftWidth = 0;
        button.pickingMode = PickingMode.Position;
        button.RegisterCallback<MouseEnterEvent>(_ => button.style.backgroundColor = ButtonHover);
        button.RegisterCallback<MouseLeaveEvent>(_ => button.style.backgroundColor = ButtonBg);
        return button;
    }

    private static void SetExpanded(bool open)
    {
        expanded = open;
        if (panel != null)
            panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private static float GetInitialVolumePercent()
    {
        if (RadioController.Instance != null)
            return RadioController.Instance.Volume * 100f;

        return PlayerPrefs.GetFloat("FlamiePrac_RadioVolume", 0.75f) * 100f;
    }

    private static void RefreshFromController()
    {
        if (!attached)
            return;

        RadioController radio = RadioController.Instance;
        if (radio == null)
            return;

        if (titleLabel != null)
        {
            string title = radio.CurrentTrackTitle;
            if (string.IsNullOrEmpty(title))
                title = string.IsNullOrEmpty(radio.StatusMessage) ? "Loading…" : radio.StatusMessage;
            titleLabel.text = title;
        }

        if (nextLabel != null)
            nextLabel.text = "Next: " + (string.IsNullOrEmpty(radio.NextTrackTitle) ? "—" : radio.NextTrackTitle);

        if (timeLabel != null)
            timeLabel.text = radio.TimeText;

        if (progressFill != null)
            progressFill.style.width = new Length(radio.Progress01 * 100f, LengthUnit.Percent);

        if (volumeSlider != null && !volumeSlider.Dragging)
        {
            float pct = radio.Volume * 100f;
            if (Math.Abs(volumeSlider.Percent - pct) > 0.5f)
                volumeSlider.SetPercent(pct);
        }

        UpdateVolumeLabel();
    }

    private static void UpdateVolumeLabel()
    {
        if (volumeLabel == null || RadioController.Instance == null)
            return;

        volumeLabel.text = Mathf.RoundToInt(RadioController.Instance.Volume * 100f) + "%";
    }

    /// <summary>
    /// Visible 0–100 drag bar. UITK's stock Slider is nearly invisible on dark panels.
    /// </summary>
    private sealed class VolumeSlider : VisualElement
    {
        private readonly VisualElement track;
        private readonly VisualElement fill;
        private readonly VisualElement thumb;
        private readonly Action<float> onChanged;
        private float percent;
        private bool dragging;

        internal bool Dragging => dragging;
        internal float Percent => percent;

        internal VolumeSlider(float initialPercent, Action<float> onChanged)
        {
            this.onChanged = onChanged;
            percent = Mathf.Clamp(initialPercent, 0f, 100f);
            pickingMode = PickingMode.Position;

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
            track.pickingMode = PickingMode.Ignore;
            Add(track);

            fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.left = 0;
            fill.style.height = 6;
            fill.style.top = new Length(50, LengthUnit.Percent);
            fill.style.marginTop = -3;
            fill.style.backgroundColor = Accent;
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

        internal void SetPercent(float value, bool notify = false)
        {
            percent = Mathf.Clamp(value, 0f, 100f);
            LayoutThumb();
            if (notify)
                onChanged?.Invoke(percent);
        }

        private void LayoutThumb()
        {
            float w = layout.width;
            if (w <= 1f)
                return;

            float x = (percent / 100f) * w;
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
            if (!dragging)
                return;

            ApplyFromLocalX(evt.localPosition.x);
            evt.StopPropagation();
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!dragging)
                return;

            dragging = false;
            if (this.HasPointerCapture(evt.pointerId))
                this.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void ApplyFromLocalX(float localX)
        {
            float w = layout.width;
            if (w <= 1f)
                return;

            float next = Mathf.Clamp01(localX / w) * 100f;
            if (Mathf.Abs(next - percent) < 0.001f)
                return;

            percent = next;
            LayoutThumb();
            onChanged?.Invoke(percent);
        }
    }
}
