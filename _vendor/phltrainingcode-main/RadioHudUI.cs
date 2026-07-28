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
    private const string HostName = "FlamiePrac_RadioHudHost_v8";
    private const float FooterStripWidth = 348f;
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
    private static VisualElement footerStripHost;
    private static VisualElement footerVolumePopover;
    private static bool footerVolumeOpen;
    private static VisualElement footerVolumeDismissRoot;
    private static EventCallback<PointerDownEvent> footerVolumeDismissHandler;
    private static Label footerTitleLabel;
    private static Label footerTimeLabel;
    private static VisualElement footerProgressFill;
    private static Button footerRestartBtn;
    private static Label footerRestartBadge;
    private static Button footerPlayBtn;
    private static Button footerSkipBtn;
    private static Label footerSkipBadge;
    private static Button footerVolumeBtn;
    private static VolumeSlider footerVolumeSlider;
    private static Label footerVolumePctLabel;
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
    private static readonly Color VoteOrange = new Color(0.90f, 0.49f, 0.13f, 1f);

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

        bool shouldRefreshFooter = footerStripHost != null;
        bool shouldRefresh = (((attached || embeddedHost != null) && expanded) || shouldRefreshFooter);
        if (shouldRefresh && Time.unscaledTime >= nextHudRefreshTime)
        {
            nextHudRefreshTime = Time.unscaledTime + 0.2f;
            if (footerStripHost != null)
                RefreshFooterStrip();
            else
                RefreshFromController();
        }
    }

    /// <summary>Compact horizontal radio strip for MultiSheet MOTD / Rinks tab footer.</summary>
    public static void AttachFooterStrip(VisualElement sectionHost)
    {
        if (sectionHost == null)
            return;

        RadioSync.EnsureClientRadio(TrainingSync.Instance);
        DetachEmbedded();
        footerStripHost = sectionHost;
        footerVolumeOpen = false;
        sectionHost.Clear();

        VisualElement wrap = new VisualElement { name = HostName + "_FooterWrap" };
        wrap.style.position = Position.Relative;
        wrap.style.flexShrink = 0;
        wrap.style.width = FooterStripWidth;
        wrap.style.minWidth = FooterStripWidth;
        wrap.style.maxWidth = FooterStripWidth;
        sectionHost.Add(wrap);

        footerVolumePopover = new VisualElement();
        footerVolumePopover.style.position = Position.Absolute;
        footerVolumePopover.style.right = 0;
        footerVolumePopover.style.bottom = 36;
        footerVolumePopover.style.width = 32;
        footerVolumePopover.style.height = 96;
        footerVolumePopover.style.flexDirection = FlexDirection.Column;
        footerVolumePopover.style.alignItems = Align.Center;
        footerVolumePopover.style.justifyContent = Justify.FlexStart;
        footerVolumePopover.style.paddingTop = 8;
        footerVolumePopover.style.paddingBottom = 10;
        footerVolumePopover.style.paddingLeft = 4;
        footerVolumePopover.style.paddingRight = 4;
        footerVolumePopover.style.backgroundColor = HeaderBg;
        footerVolumePopover.style.borderTopWidth = 1;
        footerVolumePopover.style.borderRightWidth = 1;
        footerVolumePopover.style.borderBottomWidth = 0;
        footerVolumePopover.style.borderLeftWidth = 1;
        footerVolumePopover.style.borderTopColor = new Color(0.25f, 0.25f, 0.25f);
        footerVolumePopover.style.borderRightColor = footerVolumePopover.style.borderTopColor.value;
        footerVolumePopover.style.borderLeftColor = footerVolumePopover.style.borderTopColor.value;
        footerVolumePopover.style.borderTopLeftRadius = 6;
        footerVolumePopover.style.borderTopRightRadius = 6;
        footerVolumePopover.style.display = DisplayStyle.None;
        wrap.Add(footerVolumePopover);

        footerVolumePctLabel = new Label("0%");
        footerVolumePctLabel.style.width = 28;
        footerVolumePctLabel.style.color = Accent;
        footerVolumePctLabel.style.fontSize = 11;
        footerVolumePctLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        footerVolumePctLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        footerVolumePctLabel.style.marginBottom = 6;
        footerVolumePctLabel.style.flexShrink = 0;
        footerVolumePopover.Add(footerVolumePctLabel);

        footerVolumeSlider = new VolumeSlider(GetInitialVolumePercent(), pct =>
        {
            if (RadioController.Instance == null) return;
            RadioController.Instance.Volume = pct / 100f;
            UpdateFooterVolumeLabel();
        }, vertical: true, compactVertical: true);
        footerVolumeSlider.style.width = 16;
        footerVolumeSlider.style.height = 64;
        footerVolumeSlider.style.flexShrink = 0;
        footerVolumeSlider.style.flexGrow = 1;
        footerVolumePopover.Add(footerVolumeSlider);

        VisualElement strip = new VisualElement();
        strip.style.flexDirection = FlexDirection.Row;
        strip.style.alignItems = Align.Center;
        strip.style.width = new Length(100, LengthUnit.Percent);
        strip.style.height = 36;
        strip.style.paddingLeft = 6;
        strip.style.paddingRight = 6;
        strip.style.backgroundColor = ButtonBg;
        strip.style.borderTopWidth = 1;
        strip.style.borderRightWidth = 1;
        strip.style.borderBottomWidth = 1;
        strip.style.borderLeftWidth = 1;
        strip.style.borderTopColor = new Color(0.25f, 0.25f, 0.25f);
        strip.style.borderRightColor = strip.style.borderTopColor.value;
        strip.style.borderBottomColor = strip.style.borderTopColor.value;
        strip.style.borderLeftColor = strip.style.borderTopColor.value;
        strip.style.overflow = Overflow.Hidden;
        wrap.Add(strip);

        VisualElement meta = new VisualElement();
        meta.style.flexDirection = FlexDirection.Row;
        meta.style.alignItems = Align.Center;
        meta.style.flexGrow = 1;
        meta.style.flexShrink = 1;
        meta.style.minWidth = 0;
        meta.style.overflow = Overflow.Hidden;
        meta.style.marginRight = 4;
        strip.Add(meta);

        Label note = new Label("♪");
        note.style.width = 18;
        note.style.flexShrink = 0;
        note.style.fontSize = 14;
        note.style.color = Accent;
        note.style.unityTextAlign = TextAnchor.MiddleCenter;
        note.pickingMode = PickingMode.Ignore;
        meta.Add(note);

        VisualElement progressTrack = new VisualElement();
        progressTrack.style.width = 28;
        progressTrack.style.height = 3;
        progressTrack.style.flexShrink = 0;
        progressTrack.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
        progressTrack.style.marginLeft = 4;
        progressTrack.style.marginRight = 4;
        progressTrack.style.overflow = Overflow.Hidden;
        footerProgressFill = new VisualElement();
        footerProgressFill.style.height = 3;
        footerProgressFill.style.width = new Length(0, LengthUnit.Percent);
        footerProgressFill.style.backgroundColor = Accent;
        progressTrack.Add(footerProgressFill);
        meta.Add(progressTrack);

        footerTitleLabel = new Label("…");
        footerTitleLabel.style.fontSize = 9;
        footerTitleLabel.style.color = MutedText;
        footerTitleLabel.style.width = 72;
        footerTitleLabel.style.flexShrink = 0;
        footerTitleLabel.style.overflow = Overflow.Hidden;
        footerTitleLabel.style.textOverflow = TextOverflow.Ellipsis;
        footerTitleLabel.style.whiteSpace = WhiteSpace.NoWrap;
        meta.Add(footerTitleLabel);

        footerTimeLabel = new Label("0:00");
        footerTimeLabel.style.fontSize = 9;
        footerTimeLabel.style.color = MutedText;
        footerTimeLabel.style.width = 32;
        footerTimeLabel.style.flexShrink = 0;
        footerTimeLabel.style.unityTextAlign = TextAnchor.MiddleRight;
        footerTimeLabel.style.marginLeft = 2;
        meta.Add(footerTimeLabel);

        VisualElement controls = new VisualElement();
        controls.style.flexDirection = FlexDirection.Row;
        controls.style.alignItems = Align.Center;
        controls.style.flexShrink = 0;
        strip.Add(controls);

        footerRestartBtn = MakeFooterIconButton("⏮", () =>
            RadioController.Instance?.RequestTrackChange(RadioController.CmdPrev));
        controls.Add(WrapVoteButton(footerRestartBtn, out footerRestartBadge));

        footerPlayBtn = MakeFooterIconButton("▶", () => RadioController.Instance?.TogglePlayPause());
        controls.Add(footerPlayBtn);

        footerSkipBtn = MakeFooterIconButton("⏭", () =>
            RadioController.Instance?.RequestTrackChange(RadioController.CmdNext));
        controls.Add(WrapVoteButton(footerSkipBtn, out footerSkipBadge));

        footerVolumeBtn = MakeFooterVolumeButton(() => SetFooterVolumeOpen(!footerVolumeOpen));
        controls.Add(footerVolumeBtn);

        EnsureRadioSubscription();
        RefreshFooterStrip();
    }

    private static void SetFooterVolumeOpen(bool open)
    {
        footerVolumeOpen = open;
        if (footerVolumePopover != null)
            footerVolumePopover.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
        if (footerVolumeBtn != null)
            footerVolumeBtn.style.backgroundColor = open ? Accent : ButtonBg;

        if (open)
            RegisterFooterVolumeDismiss();
        else
            UnregisterFooterVolumeDismiss();
    }

    private static void RegisterFooterVolumeDismiss()
    {
        UnregisterFooterVolumeDismiss();
        if (footerStripHost?.panel == null)
            return;

        footerVolumeDismissRoot = footerStripHost.panel.visualTree;
        footerVolumeDismissHandler = OnFooterVolumeDismissPointerDown;
        footerVolumeDismissRoot.RegisterCallback(footerVolumeDismissHandler, TrickleDown.TrickleDown);
    }

    private static void UnregisterFooterVolumeDismiss()
    {
        if (footerVolumeDismissRoot != null && footerVolumeDismissHandler != null)
            footerVolumeDismissRoot.UnregisterCallback(footerVolumeDismissHandler, TrickleDown.TrickleDown);

        footerVolumeDismissRoot = null;
        footerVolumeDismissHandler = null;
    }

    private static void OnFooterVolumeDismissPointerDown(PointerDownEvent evt)
    {
        if (!footerVolumeOpen)
            return;

        if (evt.target is not VisualElement target)
            return;

        if (footerVolumePopover != null && footerVolumePopover.Contains(target))
            return;

        if (footerVolumeBtn != null && footerVolumeBtn.Contains(target))
            return;

        SetFooterVolumeOpen(false);
    }

    private static VisualElement WrapVoteButton(Button button, out Label badge)
    {
        VisualElement wrap = new VisualElement();
        wrap.style.position = Position.Relative;
        wrap.style.flexShrink = 0;
        wrap.style.marginLeft = 2;
        wrap.Add(button);
        badge = new Label("");
        badge.style.position = Position.Absolute;
        badge.style.top = -2;
        badge.style.right = -2;
        badge.style.fontSize = 8;
        badge.style.unityFontStyleAndWeight = FontStyle.Bold;
        badge.style.color = Color.white;
        badge.style.backgroundColor = VoteOrange;
        badge.style.paddingLeft = 4;
        badge.style.paddingRight = 4;
        badge.style.display = DisplayStyle.None;
        badge.pickingMode = PickingMode.Ignore;
        wrap.Add(badge);
        return wrap;
    }

    private static Button MakeFooterIconButton(string text, Action onClick, bool compactLabel = false)
    {
        Button button = new Button(onClick) { text = text };
        button.style.width = 28;
        button.style.height = 28;
        button.style.minWidth = 28;
        button.style.minHeight = 28;
        button.style.maxWidth = 28;
        button.style.maxHeight = 28;
        button.style.paddingLeft = 0;
        button.style.paddingRight = 0;
        button.style.paddingTop = 0;
        button.style.paddingBottom = 0;
        button.style.marginLeft = 2;
        button.style.marginRight = 0;
        button.style.marginTop = 0;
        button.style.marginBottom = 0;
        button.style.fontSize = compactLabel ? 8 : 12;
        button.style.color = TextColor;
        button.style.backgroundColor = ButtonBg;
        button.style.unityTextAlign = TextAnchor.MiddleCenter;
        button.style.alignItems = Align.Center;
        button.style.justifyContent = Justify.Center;
        button.style.borderTopWidth = 1;
        button.style.borderRightWidth = 1;
        button.style.borderBottomWidth = 1;
        button.style.borderLeftWidth = 1;
        button.style.borderTopColor = new Color(0.25f, 0.25f, 0.25f);
        button.style.borderRightColor = button.style.borderTopColor.value;
        button.style.borderBottomColor = button.style.borderTopColor.value;
        button.style.borderLeftColor = button.style.borderTopColor.value;
        return button;
    }

    private static Button MakeFooterVolumeButton(Action onClick)
    {
        Button button = MakeFooterIconButton(string.Empty, onClick);
        button.text = string.Empty;
        button.Add(BuildSpeakerIcon(TextColor));
        return button;
    }

    /// <summary>Monochrome speaker + sound waves for the footer volume chip.</summary>
    private static VisualElement BuildSpeakerIcon(Color color)
    {
        VisualElement wrap = new VisualElement();
        wrap.style.width = 16;
        wrap.style.height = 14;
        wrap.style.flexDirection = FlexDirection.Row;
        wrap.style.alignItems = Align.Center;
        wrap.style.justifyContent = Justify.Center;
        wrap.pickingMode = PickingMode.Ignore;

        VisualElement box = new VisualElement();
        box.style.width = 4;
        box.style.height = 7;
        box.style.backgroundColor = color;
        box.style.borderTopLeftRadius = 1;
        box.style.borderBottomLeftRadius = 1;
        wrap.Add(box);

        VisualElement horn = new VisualElement();
        horn.style.width = 0;
        horn.style.height = 0;
        horn.style.borderTopWidth = 3;
        horn.style.borderBottomWidth = 3;
        horn.style.borderLeftWidth = 4;
        horn.style.borderTopColor = Color.clear;
        horn.style.borderBottomColor = Color.clear;
        horn.style.borderLeftColor = color;
        wrap.Add(horn);

        VisualElement wave1 = new VisualElement();
        wave1.style.width = 3;
        wave1.style.height = 6;
        wave1.style.marginLeft = 2;
        wave1.style.borderTopWidth = 1;
        wave1.style.borderRightWidth = 1;
        wave1.style.borderBottomWidth = 1;
        wave1.style.borderTopColor = color;
        wave1.style.borderRightColor = color;
        wave1.style.borderBottomColor = color;
        wave1.style.borderTopRightRadius = 3;
        wave1.style.borderBottomRightRadius = 3;
        wave1.style.backgroundColor = Color.clear;
        wrap.Add(wave1);

        VisualElement wave2 = new VisualElement();
        wave2.style.width = 3;
        wave2.style.height = 9;
        wave2.style.marginLeft = 1;
        wave2.style.borderTopWidth = 1;
        wave2.style.borderRightWidth = 1;
        wave2.style.borderBottomWidth = 1;
        wave2.style.borderTopColor = color;
        wave2.style.borderRightColor = color;
        wave2.style.borderBottomColor = color;
        wave2.style.borderTopRightRadius = 4;
        wave2.style.borderBottomRightRadius = 4;
        wave2.style.backgroundColor = Color.clear;
        wrap.Add(wave2);

        return wrap;
    }

    private static void RefreshFooterStrip()
    {
        if (footerStripHost == null)
            return;

        RadioController radio = RadioController.Instance;
        if (radio == null)
            return;

        if (footerTitleLabel != null)
        {
            string title = radio.CurrentTrackTitle;
            if (string.IsNullOrEmpty(title))
                title = string.IsNullOrEmpty(radio.StatusMessage) ? "Loading…" : radio.StatusMessage;
            footerTitleLabel.text = title;
        }

        if (footerTimeLabel != null)
        {
            string time = radio.TimeText ?? "";
            int slash = time.IndexOf('/');
            footerTimeLabel.text = slash > 0 ? time.Substring(0, slash).Trim() : time;
        }

        if (footerProgressFill != null)
            footerProgressFill.style.width = new Length(radio.Progress01 * 100f, LengthUnit.Percent);

        if (footerPlayBtn != null)
            footerPlayBtn.text = radio.ListeningEnabled ? "⏸" : "▶";

        StyleFooterVoteBadge(footerRestartBadge, radio.IsSyncedPlayback, radio.SyncRestartVoteCount, radio.SyncVoteNeed);
        StyleFooterVoteBadge(footerSkipBadge, radio.IsSyncedPlayback, radio.SyncVoteCount, radio.SyncVoteNeed);

        if (footerVolumeSlider != null && !footerVolumeSlider.Dragging)
        {
            float pct = radio.Volume * 100f;
            if (Math.Abs(footerVolumeSlider.Percent - pct) > 0.5f)
                footerVolumeSlider.SetPercent(pct);
        }
        UpdateFooterVolumeLabel();
    }

    private static void StyleFooterVoteBadge(Label badge, bool synced, int count, int need)
    {
        if (badge == null) return;
        if (synced && need > 0 && count > 0)
        {
            badge.text = count + "/" + need;
            badge.style.display = DisplayStyle.Flex;
        }
        else
        {
            badge.text = "";
            badge.style.display = DisplayStyle.None;
        }
    }

    private static void UpdateFooterVolumeLabel()
    {
        if (footerVolumePctLabel == null) return;
        int pct = footerVolumeSlider != null
            ? Mathf.RoundToInt(footerVolumeSlider.Percent)
            : (RadioController.Instance != null
                ? Mathf.RoundToInt(RadioController.Instance.Volume * 100f)
                : 0);
        footerVolumePctLabel.text = pct + "%";
    }

    /// <summary>Build collapsible radio controls into a Rinks-tab / MOTD section host.</summary>
    public static void AttachEmbedded(VisualElement sectionHost)
    {
        if (sectionHost == null)
            return;

        RadioSync.EnsureClientRadio(TrainingSync.Instance);

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
        UnregisterFooterVolumeDismiss();
        embeddedHost = null;
        footerStripHost = null;
        footerVolumePopover = null;
        footerVolumeOpen = false;
        footerTitleLabel = null;
        footerTimeLabel = null;
        footerProgressFill = null;
        footerRestartBtn = null;
        footerRestartBadge = null;
        footerPlayBtn = null;
        footerSkipBtn = null;
        footerSkipBadge = null;
        footerVolumeBtn = null;
        footerVolumeSlider = null;
        footerVolumePctLabel = null;
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

        return PlayerPrefs.GetFloat("FlamiePrac_RadioVolume", 0.1f) * 100f;
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
        private const float CompactThumb = 10f;
        private const float CompactTrack = 6f;

        private readonly VisualElement track;
        private readonly VisualElement fill;
        private readonly VisualElement thumb;
        private readonly Label thumbLabel;
        private readonly Action<float> onChanged;
        private readonly bool vertical;
        private readonly bool compactVertical;
        private float percent;
        private bool dragging;

        internal bool Dragging => dragging;
        internal float Percent => percent;

        internal VolumeSlider(
            float initialPercent,
            Action<float> onChanged,
            bool vertical = false,
            bool compactVertical = false)
        {
            this.onChanged = onChanged;
            this.vertical = vertical;
            this.compactVertical = vertical && compactVertical;
            percent = Mathf.Clamp(initialPercent, 0f, 100f);
            pickingMode = PickingMode.Position;

            style.justifyContent = Justify.Center;
            style.alignItems = Align.Center;

            float trackThickness = this.compactVertical ? CompactTrack : 5f;

            track = new VisualElement();
            track.style.position = Position.Absolute;
            track.style.backgroundColor = this.compactVertical
                ? new Color(0.16f, 0.16f, 0.18f, 1f)
                : new Color(0.22f, 0.22f, 0.24f, 1f);
            track.style.borderTopLeftRadius = trackThickness * 0.5f;
            track.style.borderTopRightRadius = trackThickness * 0.5f;
            track.style.borderBottomLeftRadius = trackThickness * 0.5f;
            track.style.borderBottomRightRadius = trackThickness * 0.5f;
            track.pickingMode = PickingMode.Ignore;
            if (vertical)
            {
                track.style.top = 0;
                track.style.bottom = 0;
                track.style.left = new Length(50, LengthUnit.Percent);
                track.style.width = trackThickness;
                track.style.marginLeft = -trackThickness * 0.5f;
            }
            else
            {
                track.style.left = 0;
                track.style.right = 0;
                track.style.height = trackThickness;
                track.style.top = new Length(50, LengthUnit.Percent);
                track.style.marginTop = -trackThickness * 0.5f;
            }
            Add(track);

            fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.backgroundColor = Accent;
            fill.pickingMode = PickingMode.Ignore;
            if (vertical)
            {
                fill.style.left = new Length(50, LengthUnit.Percent);
                fill.style.width = trackThickness;
                fill.style.marginLeft = -trackThickness * 0.5f;
                fill.style.bottom = 0;
                fill.style.borderTopLeftRadius = trackThickness * 0.5f;
                fill.style.borderTopRightRadius = trackThickness * 0.5f;
            }
            else
            {
                fill.style.left = 0;
                fill.style.height = trackThickness;
                fill.style.top = new Length(50, LengthUnit.Percent);
                fill.style.marginTop = -trackThickness * 0.5f;
                fill.style.borderTopLeftRadius = trackThickness * 0.5f;
                fill.style.borderBottomLeftRadius = trackThickness * 0.5f;
            }
            Add(fill);

            thumb = new VisualElement();
            thumb.style.position = Position.Absolute;
            thumb.style.justifyContent = Justify.Center;
            thumb.style.alignItems = Align.Center;
            thumb.style.backgroundColor = compactVertical
                ? new Color(0.92f, 0.94f, 0.97f, 1f)
                : new Color(0.85f, 0.90f, 0.95f, 1f);
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
            if (vertical && compactVertical)
            {
                thumb.style.width = CompactThumb;
                thumb.style.height = CompactThumb;
                thumb.style.left = new Length(50, LengthUnit.Percent);
                thumb.style.marginLeft = -CompactThumb * 0.5f;
                thumb.style.borderTopLeftRadius = CompactThumb * 0.5f;
                thumb.style.borderTopRightRadius = CompactThumb * 0.5f;
                thumb.style.borderBottomLeftRadius = CompactThumb * 0.5f;
                thumb.style.borderBottomRightRadius = CompactThumb * 0.5f;
                thumbLabel.style.display = DisplayStyle.None;
                return;
            }

            if (vertical)
            {
                if (dragging)
                {
                    thumb.style.width = ThumbDragH;
                    thumb.style.height = ThumbDragW;
                    thumb.style.left = new Length(50, LengthUnit.Percent);
                    thumb.style.marginLeft = -ThumbDragH * 0.5f;
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
                    thumb.style.left = new Length(50, LengthUnit.Percent);
                    thumb.style.marginLeft = -ThumbRest * 0.5f;
                    thumb.style.borderTopLeftRadius = ThumbRest * 0.5f;
                    thumb.style.borderTopRightRadius = ThumbRest * 0.5f;
                    thumb.style.borderBottomLeftRadius = ThumbRest * 0.5f;
                    thumb.style.borderBottomRightRadius = ThumbRest * 0.5f;
                    thumbLabel.style.display = DisplayStyle.None;
                }
                return;
            }

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
            if (vertical)
            {
                float h = layout.height;
                if (h <= 1f)
                    return;

                float thumbH = compactVertical ? CompactThumb : (dragging ? ThumbDragW : ThumbRest);
                float fillH = (percent / 100f) * h;
                fill.style.height = fillH;
                thumb.style.bottom = Mathf.Clamp(fillH - thumbH * 0.5f, 0f, Mathf.Max(0f, h - thumbH));

                if (dragging && !compactVertical)
                    thumbLabel.text = Mathf.RoundToInt(percent) + "%";
                return;
            }

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
            if (vertical)
                ApplyFromLocalY(evt.localPosition.y);
            else
                ApplyFromLocalX(evt.localPosition.x);
            UpdateVolumeLabel();
            UpdateFooterVolumeLabel();
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!dragging)
                return;

            if (vertical)
                ApplyFromLocalY(evt.localPosition.y);
            else
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
            UpdateFooterVolumeLabel();
        }

        private void ApplyFromLocalX(float localX)
        {
            float w = layout.width;
            if (w <= 1f)
                return;

            float next = Mathf.Clamp01(localX / w) * 100f;
            ApplyPercent(next);
        }

        private void ApplyFromLocalY(float localY)
        {
            float h = layout.height;
            if (h <= 1f)
                return;

            float next = (1f - Mathf.Clamp01(localY / h)) * 100f;
            ApplyPercent(next);
        }

        private void ApplyPercent(float next)
        {
            if (Mathf.Abs(next - percent) < 0.001f)
            {
                if (dragging && !compactVertical)
                    thumbLabel.text = Mathf.RoundToInt(percent) + "%";
                return;
            }

            percent = next;
            LayoutThumb();
            onChanged?.Invoke(percent);
        }
    }
}
