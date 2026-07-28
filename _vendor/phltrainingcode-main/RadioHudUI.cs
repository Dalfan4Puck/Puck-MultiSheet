using System;
using Object = UnityEngine.Object;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UITK radio controls — standalone top-left ♪ chip (standalone FlamiePrac) or collapsible
/// ♪ chip embedded in MultiSheet Rinks tab / MOTD via <see cref="AttachEmbedded"/>.
/// </summary>
public static class RadioHudUI
{
    // Bump host name when layout changes so stale UITK trees are rebuilt.
    private const string HostName = "FlamiePrac_RadioHudHost_v5";
    private const string PanelName = "FlamiePrac_RadioPanel";
    private const string ChipName = "FlamiePrac_RadioChip";
    private const string VolumeSliderName = "FlamiePrac_VolumeSlider";
    private const string TitleName = "FlamiePrac_RadioTitle";
    private const string NextName = "FlamiePrac_RadioNext";
    private const string TimeName = "FlamiePrac_RadioTime";
    private const string VolumeLabelName = "FlamiePrac_RadioVolumeLabel";
    private const string ProgressFillName = "FlamiePrac_RadioProgressFill";
    private const string ListenButtonName = "FlamiePrac_RadioListen";
    private const string RestartButtonName = "FlamiePrac_RadioRestart";
    private const string SkipButtonName = "FlamiePrac_RadioSkip";

    /// <summary>When true, skip the top-left chip (MultiSheet Rinks tab owns radio UI).</summary>
    public static Func<bool> ShouldSuppressStandalone { get; set; }

    private static VisualElement host;
    private static VisualElement embeddedHost;
    private static VisualElement panel;
    private static Button chipButton;
    private static Button listenButton;
    private static Button restartButton;
    private static Button skipButton;
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
    private static bool legacyUiCleaned;
    private static float nextHudRefreshTime;

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

        if (!legacyUiCleaned)
        {
            CleanupLegacyUi();
            legacyUiCleaned = true;
        }

        bool suppressStandalone = ShouldSuppressStandalone != null && ShouldSuppressStandalone();
        if (suppressStandalone && attached)
            TearDownStandalone();

        if (!suppressStandalone && !attached && Time.unscaledTime >= nextAttachAttempt)
        {
            nextAttachAttempt = Time.unscaledTime + 1f;
            TryAttachStandalone();
        }

        bool shouldRefresh = ((attached || embeddedHost != null) && expanded);
        if (shouldRefresh && Time.unscaledTime >= nextHudRefreshTime)
        {
            nextHudRefreshTime = Time.unscaledTime + 0.2f;
            RefreshFromController();
        }
    }

    /// <summary>Build collapsible radio controls into a Rinks-tab / MOTD section host.</summary>
    public static void AttachEmbedded(VisualElement sectionHost)
    {
        if (sectionHost == null)
            return;

        DetachEmbedded();
        embeddedHost = sectionHost;
        expanded = false;

        VisualElement container = new VisualElement { name = HostName + "_Embedded" };
        container.style.flexDirection = FlexDirection.Column;
        container.style.flexShrink = 0;
        container.style.alignItems = Align.FlexStart;
        container.style.marginTop = 4;
        container.pickingMode = PickingMode.Position;

        chipButton = new Button(() => SetExpanded(!expanded)) { name = ChipName, text = "♪" };
        StyleSquareButton(chipButton, 28);
        container.Add(chipButton);

        panel = new VisualElement { name = PanelName };
        panel.style.width = new Length(100, LengthUnit.Percent);
        panel.style.maxWidth = 280;
        panel.style.marginTop = 4;
        panel.style.backgroundColor = PanelBg;
        panel.style.flexDirection = FlexDirection.Column;
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
        header.style.paddingLeft = 8;
        header.style.paddingRight = 4;
        header.style.paddingTop = 4;
        header.style.paddingBottom = 4;
        header.style.minHeight = 24;

        Label headerTitle = new Label("Training Radio");
        headerTitle.style.color = TextColor;
        headerTitle.style.fontSize = 10;
        header.Add(headerTitle);

        Button closeBtn = MakeCompactButton("✕", 24, () => SetExpanded(false));
        closeBtn.style.height = 22;
        closeBtn.style.color = new Color(0.95f, 0.35f, 0.35f);
        header.Add(closeBtn);
        panel.Add(header);

        VisualElement body = new VisualElement();
        body.style.paddingLeft = 8;
        body.style.paddingRight = 8;
        body.style.paddingTop = 6;
        body.style.paddingBottom = 7;
        BuildPanelBody(body, embedded: true);
        panel.Add(body);

        container.Add(panel);
        sectionHost.Add(container);

        BindElementRefs(container);
        EnsureRadioSubscription();
        SetExpanded(false);
    }

    public static void DetachEmbedded()
    {
        embeddedHost = null;
        panel = null;
        chipButton = null;
        listenButton = null;
        restartButton = null;
        skipButton = null;
        titleLabel = null;
        nextLabel = null;
        timeLabel = null;
        volumeLabel = null;
        volumeSlider = null;
        progressFill = null;
        expanded = false;
        UnsubscribeRadio();
    }

    public static void TearDown()
    {
        TearDownStandalone();
        DetachEmbedded();
    }

    private static void TearDownStandalone()
    {
        UnsubscribeRadio();

        if (host != null)
        {
            try { host.RemoveFromHierarchy(); }
            catch { }
        }

        host = null;
        if (embeddedHost == null)
        {
            panel = null;
            chipButton = null;
            listenButton = null;
            restartButton = null;
            skipButton = null;
            titleLabel = null;
            nextLabel = null;
            timeLabel = null;
            volumeLabel = null;
            volumeSlider = null;
            progressFill = null;
        }

        attached = false;
        expanded = false;
        nextHudRefreshTime = 0f;
    }

    public static void CleanupLegacyUi()
    {
        GameObject canvas = GameObject.Find("RadioCanvas");
        if (canvas != null)
            UnityEngine.Object.Destroy(canvas);

        GameObject legacyEventSystem = GameObject.Find("FlamiePrac_EventSystem");
        if (legacyEventSystem != null)
            UnityEngine.Object.Destroy(legacyEventSystem);

        UIManager manager = MonoBehaviourSingleton<UIManager>.Instance;
        VisualElement root = manager != null ? manager.RootVisualElement : null;
        if (root == null)
            return;

        foreach (string legacyName in new[]
                 {
                     "FlamiePrac_RadioHudHost",
                     "FlamiePrac_RadioHudHost_v2",
                     "FlamiePrac_RadioHudHost_v3",
                     "FlamiePrac_RadioHudHost_v4"
                 })
        {
            VisualElement legacy = root.Q(legacyName);
            if (legacy != null)
            {
                try { legacy.RemoveFromHierarchy(); } catch { }
            }
        }
    }

    private static void TryAttachStandalone()
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
            BindElementRefs(host);

            if (volumeSlider == null)
            {
                try { host.RemoveFromHierarchy(); } catch { }
                host = null;
                panel = null;
                chipButton = null;
                listenButton = null;
                restartButton = null;
                skipButton = null;
                titleLabel = null;
                nextLabel = null;
                timeLabel = null;
                volumeLabel = null;
                progressFill = null;
            }
            else
            {
                attached = true;
                EnsureRadioSubscription();
                RefreshFromController();
                return;
            }
        }

        BuildStandaloneUi(root);
        attached = true;
        SetExpanded(false);
        EnsureRadioSubscription();
        RefreshFromController();

        FlamieLog.Info("[FlamiePrac] Radio HUD attached (UITK, top-left square).");
    }

    private static void EnsureRadioSubscription()
    {
        if (subscribedRadio != null || RadioController.Instance == null)
            return;

        subscribedRadio = RadioController.Instance;
        subscribedRadio.StateChanged -= RefreshFromController;
        subscribedRadio.StateChanged += RefreshFromController;
    }

    private static void UnsubscribeRadio()
    {
        if (subscribedRadio == null)
            return;

        try { subscribedRadio.StateChanged -= RefreshFromController; }
        catch { }

        subscribedRadio = null;
    }

    private static void BindElementRefs(VisualElement root)
    {
        if (root == null)
            return;

        panel = root.Q(PanelName) ?? root;
        chipButton = root.Q<Button>(ChipName);
        listenButton = root.Q<Button>(ListenButtonName);
        restartButton = root.Q<Button>(RestartButtonName);
        skipButton = root.Q<Button>(SkipButtonName);
        titleLabel = root.Q<Label>(TitleName);
        nextLabel = root.Q<Label>(NextName);
        timeLabel = root.Q<Label>(TimeName);
        volumeLabel = root.Q<Label>(VolumeLabelName);
        volumeSlider = root.Q<VolumeSlider>(VolumeSliderName);
        progressFill = root.Q(ProgressFillName);
    }

    private static void BuildStandaloneUi(VisualElement root)
    {
        host = new VisualElement { name = HostName };
        host.style.position = Position.Absolute;
        host.style.left = 12;
        host.style.top = 12;
        host.style.flexDirection = FlexDirection.Column;
        host.style.alignItems = Align.FlexStart;
        host.pickingMode = PickingMode.Ignore;

        chipButton = new Button(() => SetExpanded(!expanded)) { name = ChipName, text = "♪" };
        StyleSquareButton(chipButton, 34);
        host.Add(chipButton);

        panel = new VisualElement { name = PanelName };
        panel.style.width = 248;
        panel.style.marginTop = 6;
        panel.style.backgroundColor = PanelBg;
        panel.style.flexDirection = FlexDirection.Column;
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
        header.style.paddingLeft = 8;
        header.style.paddingRight = 4;
        header.style.paddingTop = 4;
        header.style.paddingBottom = 4;
        header.style.minHeight = 26;

        Label headerTitle = new Label("Training Radio");
        headerTitle.style.color = TextColor;
        headerTitle.style.fontSize = 11;
        header.Add(headerTitle);

        Button closeBtn = MakeCompactButton("✕", 24, () => SetExpanded(false));
        closeBtn.style.height = 22;
        closeBtn.style.color = new Color(0.95f, 0.35f, 0.35f);
        header.Add(closeBtn);
        panel.Add(header);

        VisualElement body = new VisualElement();
        body.style.paddingLeft = 8;
        body.style.paddingRight = 8;
        body.style.paddingTop = 6;
        body.style.paddingBottom = 7;
        BuildPanelBody(body, embedded: false);
        panel.Add(body);

        host.Add(panel);
        host.pickingMode = PickingMode.Ignore;
        root.Add(host);
    }

    private static void BuildPanelBody(VisualElement body, bool embedded)
    {
        titleLabel = new Label("Loading…") { name = TitleName };
        titleLabel.style.color = TextColor;
        titleLabel.style.fontSize = embedded ? 11 : 12;
        titleLabel.style.whiteSpace = WhiteSpace.Normal;
        titleLabel.style.marginBottom = 0;
        body.Add(titleLabel);

        nextLabel = new Label("Next: —") { name = NextName };
        nextLabel.style.color = MutedText;
        nextLabel.style.fontSize = 10;
        nextLabel.style.marginTop = 1;
        body.Add(nextLabel);

        VisualElement progressTrack = new VisualElement();
        progressTrack.style.height = 5;
        progressTrack.style.marginTop = 6;
        progressTrack.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
        progressTrack.style.overflow = Overflow.Hidden;

        progressFill = new VisualElement { name = ProgressFillName };
        progressFill.style.height = 5;
        progressFill.style.width = new Length(0, LengthUnit.Percent);
        progressFill.style.backgroundColor = Accent;
        progressTrack.Add(progressFill);
        body.Add(progressTrack);

        timeLabel = new Label("0:00 / 0:00") { name = TimeName };
        timeLabel.style.color = MutedText;
        timeLabel.style.fontSize = 10;
        timeLabel.style.marginTop = 3;
        body.Add(timeLabel);

        VisualElement controls = new VisualElement();
        controls.style.flexDirection = FlexDirection.Row;
        controls.style.justifyContent = Justify.SpaceBetween;
        controls.style.alignItems = Align.Center;
        controls.style.marginTop = 6;

        restartButton = MakeCompactButton("Restart", embedded ? 72 : 78,
            () => RadioController.Instance?.RequestTrackChange(RadioController.CmdPrev));
        restartButton.name = RestartButtonName;
        controls.Add(restartButton);

        listenButton = MakeCompactButton("On", 40, () => RadioController.Instance?.TogglePlayPause());
        listenButton.name = ListenButtonName;
        controls.Add(listenButton);

        skipButton = MakeCompactButton("Skip", embedded ? 72 : 78,
            () => RadioController.Instance?.RequestTrackChange(RadioController.CmdNext));
        skipButton.name = SkipButtonName;
        controls.Add(skipButton);
        body.Add(controls);

        VisualElement volumeRow = new VisualElement();
        volumeRow.style.flexDirection = FlexDirection.Row;
        volumeRow.style.alignItems = Align.Center;
        volumeRow.style.marginTop = 6;
        volumeRow.style.height = 26;
        volumeRow.pickingMode = PickingMode.Position;

        volumeLabel = new Label("0%") { name = VolumeLabelName };
        volumeLabel.style.color = TextColor;
        volumeLabel.style.fontSize = 11;
        volumeLabel.style.width = 36;
        volumeLabel.style.minWidth = 36;
        volumeLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
        volumeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
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
        volumeSlider.style.height = 26;
        volumeRow.Add(volumeSlider);
        body.Add(volumeRow);
    }

    private static void StyleSquareButton(Button button, float size)
    {
        button.style.width = size;
        button.style.height = size;
        button.style.minWidth = size;
        button.style.minHeight = size;
        button.style.maxWidth = size;
        button.style.maxHeight = size;
        button.style.paddingLeft = 0;
        button.style.paddingRight = 0;
        button.style.paddingTop = 0;
        button.style.paddingBottom = 0;
        button.style.marginLeft = 0;
        button.style.marginRight = 0;
        button.style.marginTop = 0;
        button.style.marginBottom = 0;
        button.style.fontSize = 16;
        button.style.color = TextColor;
        button.style.backgroundColor = ButtonBg;
        button.style.borderTopWidth = 0;
        button.style.borderRightWidth = 0;
        button.style.borderBottomWidth = 0;
        button.style.borderLeftWidth = 0;
        button.style.unityTextAlign = TextAnchor.MiddleCenter;
        button.style.justifyContent = Justify.Center;
        button.style.alignItems = Align.Center;
        button.pickingMode = PickingMode.Position;
        CenterButtonLabel(button);
        button.RegisterCallback<MouseEnterEvent>(_ => button.style.backgroundColor = ButtonHover);
        button.RegisterCallback<MouseLeaveEvent>(_ => button.style.backgroundColor = ButtonBg);
    }

    private static Button MakeCompactButton(string text, float width, Action onClick)
    {
        Button button = new Button(onClick) { text = text };
        button.style.height = 26;
        button.style.width = width;
        button.style.minHeight = 26;
        button.style.paddingLeft = 0;
        button.style.paddingRight = 0;
        button.style.paddingTop = 0;
        button.style.paddingBottom = 0;
        button.style.fontSize = 11;
        button.style.color = TextColor;
        button.style.backgroundColor = ButtonBg;
        button.style.borderTopWidth = 0;
        button.style.borderRightWidth = 0;
        button.style.borderBottomWidth = 0;
        button.style.borderLeftWidth = 0;
        button.style.unityTextAlign = TextAnchor.MiddleCenter;
        button.style.justifyContent = Justify.Center;
        button.style.alignItems = Align.Center;
        button.pickingMode = PickingMode.Position;
        CenterButtonLabel(button);
        button.RegisterCallback<MouseEnterEvent>(_ => button.style.backgroundColor = ButtonHover);
        button.RegisterCallback<MouseLeaveEvent>(_ => button.style.backgroundColor = ButtonBg);
        return button;
    }

    private static void CenterButtonLabel(Button button)
    {
        void Apply()
        {
            Label label = button.Q<Label>();
            if (label == null)
                return;

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

    private static void SetExpanded(bool open)
    {
        expanded = open;
        if (panel != null)
            panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;

        if (open)
        {
            EnsureRadioSubscription();
            RefreshFromController();
            if (RadioController.Instance != null)
                RadioController.Instance.RequestPlaylistRefreshFromHud();
        }
    }

    private static float GetInitialVolumePercent()
    {
        if (RadioController.Instance != null)
            return RadioController.Instance.Volume * 100f;

        return PlayerPrefs.GetFloat("FlamiePrac_RadioVolume", 0.2f) * 100f;
    }

    private static void RefreshFromController()
    {
        if (!attached && embeddedHost == null)
            return;

        if (!expanded)
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
        {
            if (radio.IsSyncedPlayback)
            {
                string skip = string.IsNullOrEmpty(radio.NextTrackTitle) ? "Skip: —" : radio.NextTrackTitle;
                string restart = radio.RestartVoteTitle;
                nextLabel.text = skip + " · " + restart;
            }
            else
            {
                nextLabel.text = "Next: " + (string.IsNullOrEmpty(radio.NextTrackTitle) ? "—" : radio.NextTrackTitle);
            }
        }

        if (timeLabel != null)
            timeLabel.text = radio.TimeText;

        if (progressFill != null)
            progressFill.style.width = new Length(radio.Progress01 * 100f, LengthUnit.Percent);

        if (listenButton != null)
            listenButton.text = radio.ListeningEnabled ? "On" : "Off";

        if (restartButton != null && radio.IsSyncedPlayback)
            restartButton.text = radio.SyncVoteNeed > 0
                ? (radio.SyncRestartVoteCount + "/" + radio.SyncVoteNeed)
                : "Restart";
        else if (restartButton != null)
            restartButton.text = "Restart";

        if (skipButton != null && radio.IsSyncedPlayback)
            skipButton.text = radio.SyncVoteNeed > 0
                ? (radio.SyncVoteCount + "/" + radio.SyncVoteNeed)
                : "Skip";
        else if (skipButton != null)
            skipButton.text = "Skip";

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
        if (volumeLabel == null)
            return;

        int pct = volumeSlider != null
            ? Mathf.RoundToInt(volumeSlider.Percent)
            : (RadioController.Instance != null
                ? Mathf.RoundToInt(RadioController.Instance.Volume * 100f)
                : 0);

        if (volumeSlider != null && volumeSlider.Dragging)
            volumeLabel.text = "Vol";
        else
            volumeLabel.text = pct + "%";
    }

    private sealed class VolumeSlider : VisualElement
    {
        private const float ThumbRest = 12f;
        private const float ThumbDragW = 34f;
        private const float ThumbDragH = 20f;

        private readonly VisualElement track;
        private readonly VisualElement fill;
        private readonly VisualElement thumb;
        private readonly Label thumbLabel;
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
            track.style.height = 5;
            track.style.top = new Length(50, LengthUnit.Percent);
            track.style.marginTop = -2.5f;
            track.style.backgroundColor = new Color(0.22f, 0.22f, 0.24f, 1f);
            track.style.borderTopLeftRadius = 2;
            track.style.borderTopRightRadius = 2;
            track.style.borderBottomLeftRadius = 2;
            track.style.borderBottomRightRadius = 2;
            track.pickingMode = PickingMode.Ignore;
            Add(track);

            fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.left = 0;
            fill.style.height = 5;
            fill.style.top = new Length(50, LengthUnit.Percent);
            fill.style.marginTop = -2.5f;
            fill.style.backgroundColor = Accent;
            fill.style.borderTopLeftRadius = 2;
            fill.style.borderBottomLeftRadius = 2;
            fill.pickingMode = PickingMode.Ignore;
            Add(fill);

            thumb = new VisualElement();
            thumb.style.position = Position.Absolute;
            thumb.style.justifyContent = Justify.Center;
            thumb.style.alignItems = Align.Center;
            thumb.style.backgroundColor = new Color(0.85f, 0.90f, 0.95f, 1f);
            thumb.pickingMode = PickingMode.Ignore;
            Add(thumb);

            thumbLabel = new Label();
            thumbLabel.style.color = new Color(0.08f, 0.08f, 0.1f, 1f);
            thumbLabel.style.fontSize = 9;
            thumbLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            thumbLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            thumbLabel.style.display = DisplayStyle.None;
            thumbLabel.pickingMode = PickingMode.Ignore;
            thumb.Add(thumbLabel);

            ApplyThumbChrome();

            RegisterCallback<GeometryChangedEvent>(_ => LayoutThumb());
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag());
        }

        internal void SetPercent(float value, bool notify = false)
        {
            percent = Mathf.Clamp(value, 0f, 100f);
            LayoutThumb();
            if (notify)
                onChanged?.Invoke(percent);
        }

        private void ApplyThumbChrome()
        {
            if (dragging)
            {
                thumb.style.width = ThumbDragW;
                thumb.style.height = ThumbDragH;
                thumb.style.top = new Length(50, LengthUnit.Percent);
                thumb.style.marginTop = -ThumbDragH * 0.5f;
                thumb.style.borderTopLeftRadius = 4;
                thumb.style.borderTopRightRadius = 4;
                thumb.style.borderBottomLeftRadius = 4;
                thumb.style.borderBottomRightRadius = 4;
                thumbLabel.style.display = DisplayStyle.Flex;
                thumbLabel.text = Mathf.RoundToInt(percent) + "%";
            }
            else
            {
                thumb.style.width = ThumbRest;
                thumb.style.height = ThumbRest;
                thumb.style.top = new Length(50, LengthUnit.Percent);
                thumb.style.marginTop = -ThumbRest * 0.5f;
                thumb.style.borderTopLeftRadius = ThumbRest * 0.5f;
                thumb.style.borderTopRightRadius = ThumbRest * 0.5f;
                thumb.style.borderBottomLeftRadius = ThumbRest * 0.5f;
                thumb.style.borderBottomRightRadius = ThumbRest * 0.5f;
                thumbLabel.style.display = DisplayStyle.None;
            }
        }

        private void LayoutThumb()
        {
            float w = layout.width;
            if (w <= 1f)
                return;

            float thumbW = dragging ? ThumbDragW : ThumbRest;
            float x = (percent / 100f) * w;
            fill.style.width = x;
            thumb.style.left = Mathf.Clamp(x - thumbW * 0.5f, 0f, Mathf.Max(0f, w - thumbW));

            if (dragging)
                thumbLabel.text = Mathf.RoundToInt(percent) + "%";
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            dragging = true;
            ApplyThumbChrome();
            this.CapturePointer(evt.pointerId);
            ApplyFromLocalX(evt.localPosition.x);
            UpdateVolumeLabel();
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

            if (this.HasPointerCapture(evt.pointerId))
                this.ReleasePointer(evt.pointerId);
            EndDrag();
            evt.StopPropagation();
        }

        private void EndDrag()
        {
            if (!dragging)
                return;

            dragging = false;
            ApplyThumbChrome();
            LayoutThumb();
            UpdateVolumeLabel();
        }

        private void ApplyFromLocalX(float localX)
        {
            float w = layout.width;
            if (w <= 1f)
                return;

            float next = Mathf.Clamp01(localX / w) * 100f;
            if (Mathf.Abs(next - percent) < 0.001f)
            {
                if (dragging)
                    thumbLabel.text = Mathf.RoundToInt(percent) + "%";
                return;
            }

            percent = next;
            LayoutThumb();
            onChanged?.Invoke(percent);
        }
    }
}
