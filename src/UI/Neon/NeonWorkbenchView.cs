using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SpirePatch;

internal sealed record NeonTransactionConfig(
    string Title,
    string Subtitle,
    bool GameRunning,
    string? BlockedReason,
    string? AutoSnapshotNote,
    string? BoundaryNote,
    string Token,
    IReadOnlyList<NeonDiffGroup> Groups,
    string ConfirmLabel,
    string CancelLabel = "取消",
    bool Danger = false,
    string FootNote = "确认前不会改动任何本地文件。",
    Func<Task>? OnConfirmAsync = null,
    UIElement? CustomContent = null,
    string? PlainSummary = null,
    bool ExpandTechnicalDetailsByDefault = false,
    Action? OnCancel = null);

internal enum NeonInlineTogglePhase
{
    Idle,
    Applying,
    AppliedScanPending,
    AppliedScanFailed,
    Failed
}

internal sealed record NeonInlineToggleStatus(
    string ModId,
    string ModName,
    bool TargetEnable,
    NeonInlineTogglePhase Phase,
    string Message,
    string? Detail = null);

internal sealed record NeonInspectorResolutionReceipt(
    string PrereqId,
    string PrereqName,
    NeonTone Tone,
    string Lead,
    string Body,
    bool ShowRescanAction = false);

internal sealed partial class NeonWorkbenchView : Grid
{
    // Callbacks wired to MainWindow's real C# domain operations
    public event Action? RequestRescanAll;
    public event Action? RequestRescanAndVerifyHealth;
    public event Action? RequestChooseGameRoot;
    public event Action? RequestDiscoverGameRoots;
    public event Action? RequestOpenGameFolder;
    public event Action? RequestOpenModsFolder;
    public event Action? RequestLaunchGame;
    public event Action<ManagedMod>? RequestPreviewToggleMod;
    public event Action<ManagedMod, string?>? RequestDirectToggleMod;
    public event Action<ManagedMod>? RequestPreviewUpdateMod;
    public event Action<ManagedMod>? RequestCheckModUpdate;
    public event Action<ManagedMod, string>? RequestQuickBindGitHub;
    public event Action<ManagedMod, ulong>? RequestQuickBindWorkshop;
    public event Action<ManagedMod>? RequestUnbindModSource;
    public event Action<ManagedMod>? RequestRestoreModBackup;
    public event Action<ManagedMod>? RequestOpenModFolder;
    public event Action<string[]>? RequestBatchToggleEnable;
    public event Action<string[]>? RequestBatchToggleDisable;
    public event Action<string[]>? RequestBatchCheckUpdates;
    public event Action<NeonRelatedPatch>? RequestPreviewRetirePatch;
    public event Action? RequestRefreshOnlinePatches;
    public event Action? RequestPreviewOrganizePatches;
    public event Action<string[]>? RequestPreviewPatchInstall;
    public event Action<bool>? RequestToggleOrphanAutoStop;
    public event Action<NeonRelatedPatch>? RequestAcknowledgeModReview;
    public event Action? RequestCheckAllModUpdates;
    public event Action? RequestCancelUpdateCheck;
    public event Action<ulong>? RequestPreviewWorkshopInstall;
    public event Action? RequestChooseLocalZipImport;
    public event Action? RequestDiscoverGitHubCandidates;
    public event Action? RequestExportCoopManifest;
    public event Action? RequestImportCoopPeerFile;
    public event Action? RequestRefreshCoopComparison;
    public event Action<string[]>? RequestPreviewCoopApply;
    public event Action<string>? RequestCreateEnvironmentSnapshot;
    public event Action<string>? RequestPreviewEnvironmentRestore;
    public event Action<string>? RequestDeleteEnvironmentSnapshot;
    public event Action? RequestPreviewPatchBatchRollback;
    public event Action<string>? RequestOpenPath;
    public event Action? RequestDiagnoseLog;
    public event Action<ManagerUpdateChannel>? RequestSelectManagerChannel;
    public event Action? RequestCheckManagerUpdate;
    public event Action? RequestOpenManagerUpdateModal;
    public event Action? RequestCleanupOldManagerFiles;
    public event Action? RequestOpenManagerReleasePage;
    public event Action? RequestOpenGitHubNetworkModal;
    public event Action? RequestDiagnoseGitHubNetwork;
    public event Action<string>? RequestSwitchTheme;
    public event Action? RequestSimulateExternalChange;

    // View state
    NeonWorkbenchSnapshot? currentSnapshot;
    string activeTab = "workbench";
    string workbenchMode = "list"; // "list" | "deps"
    string activeFilter = "all";
    string sortBy = "priority";
    bool depOnlyIssues = true;
    int depPage = 1;
    string? selectedModId;
    string? highlightedModId;
    readonly HashSet<string> checkedModFolders = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> selectedPatchIds = new(StringComparer.OrdinalIgnoreCase);
    bool patchesShowReady;
    readonly HashSet<string> coopCheckedIds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ScrollViewer> secondaryScrolls = new(StringComparer.Ordinal);
    readonly Dictionary<string, StackPanel> secondaryContainers = new(StringComparer.Ordinal);
    TextBox? workshopUrlBox;
    Grid? workshopInputHost;
    string? workshopInlineFeedbackText;
    NeonTone workshopInlineFeedbackTone = NeonTone.Sage;
    bool demoUtilityControlsVisible;

    // Cohesive motion generation tokens, jelly-toggle state history & lightweight row-selection map
    int tabAnimGeneration;
    int inspectorAnimGeneration;
    bool inspectorClosing;
    IInputElement? inspectorReturnFocus;
    int modalAnimGeneration;
    bool modalOpenLogical;
    bool modalClosing;
    IInputElement? modalReturnFocus;
    int drawerAnimGeneration;
    bool drawerOpenLogical;
    bool drawerClosing;
    IInputElement? drawerReturnFocus;
    int batchDockAnimGeneration;
    bool batchDockOpenLogical;
    bool batchDockClosing;
    int loadRevealGeneration;
    bool lastSnapshotHadInventory;
    bool lastSnapshotWasScanning;
    string? lastTrackedGameRoot;
    readonly Dictionary<string, bool> lastModEnabledById = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> pendingModFlipPrevState = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, NeonInlineToggleStatus> inlineToggleStatuses = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, NeonInspectorResolutionReceipt> inspectorReceipts = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> animatedReceiptIds = new(StringComparer.OrdinalIgnoreCase);
    bool? pendingDepFilterPrevOnlyIssues;
    bool hasInitializedCounts;
    int lastHardBlockCount;
    int lastNavPatchesCount;
    int lastNavAcquireCount;
    readonly Dictionary<string, int> lastFilterCounts = new(StringComparer.Ordinal);
    readonly TranslateTransform inspectorSlideTransform = new();
    readonly Dictionary<string, (Border Outer, Border LeftBar, bool HasDanger, bool HasWarn, CheckBox Check)> visibleRowVisuals = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (int Count, long Bytes)> modFileStatsCache = new(StringComparer.OrdinalIgnoreCase);

    // Persistent topbar controls
    readonly Border topbarBorder;
    readonly Grid navHostGrid;
    readonly Border navActiveIndicator;
    readonly Dictionary<string, (Button Tab, TextBlock Label, Border Underline)> navTabs = new(StringComparer.Ordinal);
    readonly Ellipse navWorkbenchDot;
    readonly Border navPatchesPill, navAcquirePill;
    readonly TextBlock navPatchesCount, navAcquireCount;
    readonly Button envPillButton, gearButton, launchButton;
    readonly Ellipse gearUpdateDot;
    readonly Ellipse envLampHalo, envLampCore;
    readonly TextBlock envPathLabel, launchLabel;
    readonly ContentControl launchPlayHost;
    readonly TextBlock brandVersionLabel;

    // Persistent watcher banner & stage containers
    readonly Border watcherBanner;
    readonly TextBlock watcherTitleText, watcherBodyText;
    readonly Button watcherRescanButton;
    readonly Grid workbenchStage;
    readonly ContentControl secondaryTabHost;

    // Persistent launch gate
    readonly Border launchGateBorder;
    readonly Border launchGateShadow;
    readonly StackPanel launchGateStack;

    // Floating post-scan load reveal pill ("✓ 已加载 X 个模组")
    readonly Border loadRevealPill;
    readonly TextBlock loadRevealPrimaryText, loadRevealMetaText;

    // Persistent workbench split & toolbar (SearchTextBox NEVER remounts!)
    readonly Grid splitBodyGrid;
    readonly ColumnDefinition splitGapCol, splitInspectorCol;
    readonly TextBox searchBox;
    readonly TextBlock searchPlaceholder;
    readonly Border searchKbdHint;
    readonly Button searchClearButton;
    readonly Grid modeSegHost;
    readonly Border modeSegThumb;
    readonly Button modeListButton, modeDepsButton;
    readonly ComboBox sortCombo;
    readonly WrapPanel filterPillsPanel;
    readonly Dictionary<string, (Button Btn, TextBlock Label, TextBlock Count)> filterPillControls = new(StringComparer.Ordinal);
    readonly TextBlock listSummaryMeta;
    readonly ScrollViewer deskScrollViewer;
    readonly StackPanel deskContentStack;

    // Floating batch dock
    readonly Border batchDock;
    readonly TextBlock batchCountText, batchHiddenWarnText;

    // Persistent inspector pane + narrow scrim
    readonly Border inspectorScrim;
    readonly Border inspectorPane;
    readonly Grid inspectorHeadGrid;
    readonly ScrollViewer inspectorScroll;
    readonly StackPanel inspectorBodyStack;
    readonly Border inspectorFootBorder;

    // Persistent footer
    readonly TextBlock footerScanStatus;
    readonly StackPanel footerRightStack;
    readonly Button footerSimWatcherButton;
    readonly Button footerRescanButton;

    // Overlays (Modal, Tools Drawer, Toast)
    readonly Border modalBackdrop;
    readonly Border modalSheet;
    readonly TextBlock modalTitleText, modalSubText, modalFootNoteText;
    readonly StackPanel modalBodyStack;
    readonly StackPanel modalFootButtons;
    Func<Task>? activeModalConfirmAsync;
    Action? activeModalCancelAction;
    bool modalExecuting;

    readonly Border drawerBackdrop;
    readonly StackPanel drawerBodyStack;

    readonly StackPanel toastRack;

    // Exposed read-only accessors for automated UI verification (zero bypasses)
    internal TextBox SearchBoxControl => searchBox;
    internal TextBox? WorkshopUrlBoxControl => workshopUrlBox;
    internal ScrollViewer DeskScrollControl => deskScrollViewer;
    internal ScrollViewer InspectorScrollControl => inspectorScroll;
    internal ScrollViewer? ActiveSecondaryScrollControl =>
        secondaryScrolls.TryGetValue(activeTab, out var sv) ? sv : null;
    internal ContentControl SecondaryTabHostControl => secondaryTabHost;
    internal string ActiveTab => activeTab;
    internal Border LaunchGateControl => launchGateBorder;
    internal Border WatcherBannerControl => watcherBanner;
    internal Border TopbarControl => topbarBorder;
    internal Grid WorkbenchStageControl => workbenchStage;
    internal Border InspectorPaneControl => inspectorPane;
    internal TranslateTransform InspectorSlideTransform => inspectorSlideTransform;
    internal Border ModalBackdropControl => modalBackdrop;
    internal StackPanel DeskContentControl => deskContentStack;
    internal StackPanel InspectorBodyControl => inspectorBodyStack;
    internal Ellipse NavWorkbenchDotControl => navWorkbenchDot;
    internal Border NavPatchesPillControl => navPatchesPill;
    internal TextBlock NavPatchesCountControl => navPatchesCount;
    internal Border NavAcquirePillControl => navAcquirePill;
    internal TextBlock NavAcquireCountControl => navAcquireCount;
    internal Ellipse EnvLampCoreControl => envLampCore;
    internal TextBlock EnvPathLabelControl => envPathLabel;
    internal string? SelectedModId => selectedModId;
    internal string ActiveFilter => activeFilter;
    internal string WorkbenchMode => workbenchMode;
    internal NeonWorkbenchSnapshot? CurrentSnapshot => currentSnapshot;
    internal bool IsModalOpen => modalOpenLogical;
    internal bool IsModalVisuallyVisible => modalBackdrop.Visibility == Visibility.Visible;
    internal bool IsModalClosing => modalClosing;
    internal FrameworkElement ModalSheetHostControl => (FrameworkElement)modalBackdrop.Child;
    internal bool IsDrawerOpen => drawerOpenLogical;
    internal bool IsDrawerVisuallyVisible => drawerBackdrop.Visibility == Visibility.Visible;
    internal FrameworkElement DrawerSheetHostControl => (FrameworkElement)drawerBackdrop.Child;
    internal bool IsBatchDockOpen => batchDockOpenLogical;
    internal Border BatchDockControl => batchDock;
    internal StackPanel ToastRackControl => toastRack;
    internal Border LoadRevealPillControl => loadRevealPill;
    internal bool IsLoadRevealVisible => loadRevealPill.Visibility == Visibility.Visible;
    internal Border NavActiveIndicatorControl => navActiveIndicator;
    internal Border ModeSegThumbControl => modeSegThumb;
    internal bool IsInspectorOpen => selectedModId != null && inspectorPane.Visibility == Visibility.Visible && !inspectorClosing;
    internal bool IsInspectorVisuallyVisible => inspectorPane.Visibility == Visibility.Visible;
    internal bool IsInspectorClosing => inspectorClosing;

    public bool DemoUtilityControlsVisible
    {
        get => demoUtilityControlsVisible;
        set
        {
            demoUtilityControlsVisible = value;
            footerSimWatcherButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    readonly TextBlock neonRecoveryWarningText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = NeonPalette.Cinnabar,
        FontSize = 12
    };
    readonly Border neonRecoveryWarningPanel = new() { Visibility = Visibility.Collapsed };

    internal bool RecoveryWarningVisible => neonRecoveryWarningPanel.Visibility == Visibility.Visible;
    internal void SetRecoveryWarning(string? warning)
    {
        neonRecoveryWarningText.Text = warning ?? "";
        neonRecoveryWarningPanel.Visibility = string.IsNullOrWhiteSpace(warning)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    public NeonWorkbenchView(string appVersion)
    {
        var neonResources = new ResourceDictionary
        {
            Source = new Uri("src/UI/Neon/NeonControlColors.xaml", UriKind.Relative)
        };
        Resources.MergedDictionaries.Add(neonResources);
        if (neonResources["NeonScrollBar"] is Style sbStyle)
            Resources[typeof(ScrollBar)] = sbStyle;

        Background = NeonPalette.BgGround;
        TextElement.SetFontFamily(this, NeonPalette.SansFont);
        TextElement.SetFontSize(this, 13.5);
        UseLayoutRounding = true;
        ClipToBounds = true;
        TextElement.SetForeground(this, NeonPalette.InkPrimary);

        // ====================================================================
        // LAYER 0: Main Application Shell (Topbar / Main Stage / Quiet Footer)
        // ====================================================================
        var appShell = new Grid();
        appShell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        appShell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        appShell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        // -------------------- TOPBAR --------------------
        topbarBorder = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 0, 18, 0),
            SnapsToDevicePixels = true
        };
        var topbar = topbarBorder;
        var topbarGrid = new Grid();
        topbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var topbarLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Brand
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
            ToolTip = "尖塔修补站 · 《杀戮尖塔 2》MOD 与修复补丁工作台"
        };
        brand.Children.Add(NeonWidgets.CreateBrandMark());
        var brandTitle = NeonWidgets.Text("尖塔修补站", 15.5, NeonPalette.InkPrimary, FontWeights.Bold, display: true);
        brandTitle.Margin = new Thickness(9, 0, 9, 0);
        brandTitle.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(brandTitle);
        brandVersionLabel = NeonWidgets.Text("v" + appVersion, 10.5, NeonPalette.InkMuted, mono: true);
        var verPill = new Border
        {
            Background = NeonPalette.BgWell,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1.5, 5, 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = brandVersionLabel
        };
        brand.Children.Add(verPill);
        topbarLeft.Children.Add(brand);

        // Navigation Tabs with signal dot, count pills & shared sliding indicator (FeelCurve nav-indicator)
        navHostGrid = new Grid { Height = 54 };
        var navRow = new StackPanel { Orientation = Orientation.Horizontal, Height = 54 };
        navWorkbenchDot = NeonWidgets.CreateNavSignalDot();
        (navPatchesPill, navPatchesCount) = NeonWidgets.CreateNavSignalCount();
        (navAcquirePill, navAcquireCount) = NeonWidgets.CreateNavSignalCount();

        navRow.Children.Add(BuildNavTab("workbench", "工作台", navWorkbenchDot));
        navRow.Children.Add(BuildNavTab("patches", "补丁", navPatchesPill));
        navRow.Children.Add(BuildNavTab("acquire", "获取与更新", navAcquirePill));
        navRow.Children.Add(BuildNavTab("coop", "联机", null));
        navRow.Children.Add(BuildNavTab("recovery", "恢复", null));
        navRow.SizeChanged += (_, _) => PositionNavIndicator(animate: false);

        navActiveIndicator = new Border
        {
            Height = 2.5,
            Width = 0,
            Background = NeonPalette.Sage,
            CornerRadius = new CornerRadius(2, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Visibility = Visibility.Collapsed
        };
        var navIndicatorCanvas = new Canvas { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, Height = 2.5 };
        navIndicatorCanvas.Children.Add(navActiveIndicator);

        navHostGrid.Children.Add(navRow);
        navHostGrid.Children.Add(navIndicatorCanvas);
        topbarLeft.Children.Add(navHostGrid);
        topbarGrid.Children.Add(topbarLeft);

        // Topbar Right: Env Pill + Gear Icon + Primary Launch Button
        var topbarRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var (lampHost, lampHalo, lampCore) = NeonWidgets.CreateStatusLamp(7);
        envLampHalo = lampHalo;
        envLampCore = lampCore;
        envPathLabel = NeonWidgets.Text("尚未选择游戏目录", 12, NeonPalette.InkSecondary, mono: true, ellipsis: true);
        envPathLabel.VerticalAlignment = VerticalAlignment.Center;
        envPathLabel.MaxWidth = 205;

        var envInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        lampHost.Margin = new Thickness(-3, -3, 4, -3);
        envInner.Children.Add(lampHost);
        envInner.Children.Add(envPathLabel);

        envPillButton = new Button
        {
            Height = 32,
            Padding = new Thickness(11, 0, 11, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Background = NeonPalette.BgGround,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            Foreground = NeonPalette.InkSecondary,
            Cursor = Cursors.Hand,
            ToolTip = "点击查看或切换游戏目录、打开环境诊断与设置",
            Content = envInner,
            Template = CreateRoundedButtonTemplate(6, NeonPalette.BgElevated, NeonPalette.BorderMedium)
        };
        AutomationProperties.SetName(envPillButton, "当前游戏目录");
        envPillButton.Click += (_, _) => OpenToolsDrawer();
        topbarRight.Children.Add(envPillButton);

        gearUpdateDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = NeonPalette.Amber,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 5, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        var gearContent = new Grid { Width = 32, Height = 32 };
        var gearSvg = NeonWidgets.CreateGearIcon();
        if (gearSvg is FrameworkElement gearEl)
        {
            gearEl.HorizontalAlignment = HorizontalAlignment.Center;
            gearEl.VerticalAlignment = VerticalAlignment.Center;
        }
        gearContent.Children.Add(gearSvg);
        gearContent.Children.Add(gearUpdateDot);

        gearButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "环境、日志诊断、代理与管理器设置",
            Content = gearContent,
            Template = CreateRoundedButtonTemplate(6, NeonPalette.BgElevated, NeonPalette.BorderMedium)
        };
        AutomationProperties.SetName(gearButton, "环境与设置");
        gearButton.Click += (_, _) => OpenToolsDrawer();
        topbarRight.Children.Add(gearButton);

        launchPlayHost = new ContentControl
        {
            Content = NeonWidgets.CreatePlayIcon(NeonPalette.SageOnPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        launchLabel = NeonWidgets.Text("启动游戏", 13, NeonPalette.SageOnPrimary, FontWeights.Medium);
        launchLabel.VerticalAlignment = VerticalAlignment.Center;
        var launchInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        launchInner.Children.Add(launchPlayHost);
        launchInner.Children.Add(launchLabel);

        launchButton = new Button
        {
            Height = 32,
            Padding = new Thickness(13, 0, 13, 0),
            Background = NeonPalette.Sage,
            BorderBrush = NeonPalette.Sage,
            BorderThickness = new Thickness(1),
            Foreground = NeonPalette.SageOnPrimary,
            Cursor = Cursors.Hand,
            Content = launchInner,
            Template = CreateRoundedButtonTemplate(6, NeonPalette.SageHover, NeonPalette.SageHover)
        };
        AutomationProperties.SetName(launchButton, "启动游戏");
        launchButton.Click += (_, _) => OnLaunchClicked();
        topbarRight.Children.Add(launchButton);

        Grid.SetColumn(topbarRight, 1);
        topbarGrid.Children.Add(topbarRight);
        topbar.Child = topbarGrid;
        appShell.Children.Add(topbar);

        // -------------------- MAIN STAGE --------------------
        var mainStage = new Grid();
        mainStage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Watcher banner
        mainStage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Watcher / Stale Health Invalidation Banner
        watcherTitleText = NeonWidgets.Text("检测到外部文件变动：", 12.5, NeonPalette.Amber, FontWeights.Bold);
        watcherBodyText = NeonWidgets.Text("mods 目录发生变化，旧的补丁健康结论已自动失效，请重新扫描核验。", 12.5, NeonPalette.Amber, wrap: true);
        var watcherTextPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        watcherTextPanel.Children.Add(watcherTitleText);
        watcherTextPanel.Children.Add(watcherBodyText);

        watcherRescanButton = new Button
        {
            Content = "重新扫描并核验",
            Height = 27,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Background = NeonPalette.BgElevated,
            Foreground = NeonPalette.InkPrimary,
            BorderBrush = NeonPalette.BorderMedium,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template = CreateRoundedButtonTemplate(6, NeonPalette.BgHover, NeonPalette.BorderStrong)
        };
        watcherRescanButton.Click += (_, _) => RequestRescanAndVerifyHealth?.Invoke();

        var watcherGrid = new Grid();
        watcherGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        watcherGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        watcherGrid.Children.Add(watcherTextPanel);
        Grid.SetColumn(watcherRescanButton, 1);
        watcherRescanButton.Margin = new Thickness(12, 0, 0, 0);
        watcherGrid.Children.Add(watcherRescanButton);

        watcherBanner = new Border
        {
            Margin = new Thickness(18, 10, 18, 0),
            Padding = new Thickness(14, 9, 14, 9),
            CornerRadius = new CornerRadius(6),
            Background = NeonPalette.AmberBg,
            BorderBrush = NeonPalette.AmberBorder,
            BorderThickness = new Thickness(1),
            Child = watcherGrid,
            Visibility = Visibility.Collapsed
        };
        mainStage.Children.Add(watcherBanner);

        // Secondary Tab Placeholder Host
        secondaryTabHost = new ContentControl { Visibility = Visibility.Collapsed };
        Grid.SetRow(secondaryTabHost, 1);
        mainStage.Children.Add(secondaryTabHost);

        // Workbench Stage
        workbenchStage = new Grid();
        workbenchStage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Launch gate
        workbenchStage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Split body

        // Adaptive Pre-Flight Launch Gate (shadow on sibling layer so card text stays 100% razor-sharp at 125% DPI)
        launchGateStack = new StackPanel();
        var launchGateHost = new Grid { Margin = new Thickness(18, 12, 18, 0) };
        launchGateShadow = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = NeonPalette.BgSurface,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        launchGateBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            Child = launchGateStack
        };
        launchGateHost.Children.Add(launchGateShadow);
        launchGateHost.Children.Add(launchGateBorder);
        workbenchStage.Children.Add(launchGateHost);

        // Split Body: Workbench Main + Contextual Causal Inspector
        splitBodyGrid = new Grid { Margin = new Thickness(18, 12, 18, 12), ClipToBounds = true };
        splitBodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        splitGapCol = new ColumnDefinition { Width = new GridLength(0) };
        splitInspectorCol = new ColumnDefinition { Width = new GridLength(0) };
        splitBodyGrid.ColumnDefinitions.Add(splitGapCol);
        splitBodyGrid.ColumnDefinitions.Add(splitInspectorCol);

        // Left: Workbench Main Card
        var workbenchMainCard = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true
        };
        var workbenchMainGrid = new Grid();
        workbenchMainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workbenchMainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- PERSISTENT DESK TOOLBAR (NEVER REMOUNTED!) ----
        var toolbarBorder = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10, 14, 10),
            SnapsToDevicePixels = true
        };
        var toolbarStack = new StackPanel();

        // Toolbar Row 1: Search Well + View Mode Switch + Sort Combo
        var toolbarRow1 = new Grid();
        toolbarRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbarRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchWell = new Grid
        {
            Height = 32,
            Width = 380,
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        searchBox = new TextBox
        {
            Height = 32,
            FontFamily = NeonPalette.SansFont,
            FontSize = 13,
            Foreground = NeonPalette.InkPrimary,
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderMedium,
            BorderThickness = new Thickness(1),
            CaretBrush = NeonPalette.Sage,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(32, 0, 28, 0),
            Template = CreateSearchBoxTemplate()
        };
        AutomationProperties.SetName(searchBox, "搜索 MOD 名称、ID、作者或依赖");
        searchBox.TextChanged += (_, _) => OnSearchTextChanged();
        searchWell.Children.Add(searchBox);

        var searchIcon = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            IsHitTestVisible = false,
            Child = NeonWidgets.CreateSearchIcon()
        };
        searchWell.Children.Add(searchIcon);

        searchPlaceholder = NeonWidgets.Text("搜索 MOD 名称、ID、作者或依赖…", 13, NeonPalette.InkMuted);
        searchPlaceholder.Margin = new Thickness(32, 0, 32, 0);
        searchPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        searchPlaceholder.IsHitTestVisible = false;
        searchWell.Children.Add(searchPlaceholder);

        searchKbdHint = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(5, 0.5, 5, 0.5),
            CornerRadius = new CornerRadius(3),
            Background = NeonPalette.BgGround,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = NeonWidgets.Text("/", 10.5, NeonPalette.InkMuted, mono: true)
        };
        searchWell.Children.Add(searchKbdHint);

        searchClearButton = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = NeonPalette.InkMuted,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Content = "×",
            ToolTip = "清空搜索",
            Visibility = Visibility.Collapsed,
            Template = CreateRoundedButtonTemplate(10, NeonPalette.BgWell, Brushes.Transparent)
        };
        AutomationProperties.SetName(searchClearButton, "清空搜索");
        searchClearButton.Click += (_, _) => ClearSearch();
        searchWell.Children.Add(searchClearButton);

        toolbarRow1.Children.Add(searchWell);

        // Right toolbar controls: Segmented switch + Sort combo
        var toolbarControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var segTrack = new Border
        {
            Background = NeonPalette.BgWell,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 8, 0)
        };
        modeSegHost = new Grid();
        modeSegThumb = new Border
        {
            Width = 0,
            Height = 26,
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#1B201D"),
                BlurRadius = 4,
                ShadowDepth = 1,
                Opacity = 0.08
            }
        };
        var modeSegCanvas = new Canvas { IsHitTestVisible = false, Height = 26 };
        modeSegCanvas.Children.Add(modeSegThumb);
        var segStack = new StackPanel { Orientation = Orientation.Horizontal };
        modeListButton = BuildSegButton("清单", "list");
        modeDepsButton = BuildSegButton("依赖图谱", "deps");
        segStack.Children.Add(modeListButton);
        segStack.Children.Add(modeDepsButton);
        segStack.SizeChanged += (_, _) => PositionModeSegThumb(animate: false);
        modeSegHost.Children.Add(modeSegCanvas);
        modeSegHost.Children.Add(segStack);
        segTrack.Child = modeSegHost;
        toolbarControls.Children.Add(segTrack);

        sortCombo = new ComboBox
        {
            Height = 30,
            MinWidth = 92,
            ItemsSource = new[] { "问题优先", "名称 A–Z", "启用优先" },
            SelectedIndex = 0,
            FontFamily = NeonPalette.SansFont,
            FontSize = 12,
            Foreground = NeonPalette.InkSecondary,
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderMedium,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(sortCombo, "排序方式");
        sortCombo.SelectionChanged += (_, _) =>
        {
            sortBy = sortCombo.SelectedIndex switch { 1 => "name", 2 => "enabled", _ => "priority" };
            if (currentSnapshot != null)
                RenderWorkbenchContent(preserveScroll: true, animateSettle: true);
        };
        toolbarControls.Children.Add(sortCombo);

        Grid.SetColumn(toolbarControls, 1);
        toolbarRow1.Children.Add(toolbarControls);
        toolbarStack.Children.Add(toolbarRow1);

        // Toolbar Row 2: Filter Pills + List Summary Meta
        var filterStrip = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        filterStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        filterPillsPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (id, label) in new[]
        {
            ("all", "全部"),
            ("issues", "需处理"),
            ("char-content", "角色与内容"),
            ("lib", "前置库"),
            ("util", "工具与汉化"),
            ("disabled", "已停用"),
            ("unbound", "未绑来源")
        })
        {
            filterPillsPanel.Children.Add(BuildFilterPill(id, label));
        }
        filterStrip.Children.Add(filterPillsPanel);

        listSummaryMeta = NeonWidgets.Text("显示 0 / 0 项", 11.5, NeonPalette.InkMuted);
        listSummaryMeta.VerticalAlignment = VerticalAlignment.Center;
        listSummaryMeta.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(listSummaryMeta, 1);
        filterStrip.Children.Add(listSummaryMeta);

        toolbarStack.Children.Add(filterStrip);
        toolbarBorder.Child = toolbarStack;
        workbenchMainGrid.Children.Add(toolbarBorder);

        // ---- PERSISTENT SCROLL AREA + FLOATING BATCH DOCK ----
        var contentOverlayGrid = new Grid();
        deskContentStack = new StackPanel { Margin = new Thickness(0, 0, 0, 68) };
        deskScrollViewer = new ScrollViewer
        {
            Content = deskContentStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false
        };
        NeonMotion.AttachSmoothWheelScroll(deskScrollViewer, this);
        contentOverlayGrid.Children.Add(deskScrollViewer);

        // Floating Post-Scan Load Reveal Card (bottom-center mineral card; sibling shadow layer keeps ClearType crisp)
        var revealCheckCircle = new Grid { Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        revealCheckCircle.Children.Add(new Ellipse { Width = 15, Height = 15, Fill = NeonPalette.Sage });
        revealCheckCircle.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M4.0,7.8 L6.6,10.3 L11.2,5.1"),
            Stroke = NeonPalette.SageOnPrimary,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
        loadRevealPrimaryText = NeonWidgets.Text("已加载 0 个模组", 12.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        loadRevealPrimaryText.VerticalAlignment = VerticalAlignment.Center;
        loadRevealMetaText = NeonWidgets.Text("", 12, NeonPalette.InkSecondary);
        loadRevealMetaText.VerticalAlignment = VerticalAlignment.Center;
        var revealContentRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        revealContentRow.Children.Add(revealCheckCircle);
        revealContentRow.Children.Add(loadRevealPrimaryText);
        revealContentRow.Children.Add(loadRevealMetaText);

        var revealGrid = new Grid();
        revealGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        revealGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var revealStripe = new Border { Background = NeonPalette.Sage };
        revealGrid.Children.Add(revealStripe);
        var revealPad = new Border
        {
            Padding = new Thickness(11, 7, 14, 7),
            Child = revealContentRow
        };
        Grid.SetColumn(revealPad, 1);
        revealGrid.Children.Add(revealPad);

        var revealInner = new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.SageBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = revealGrid
        };
        var revealShadow = new Border
        {
            Background = NeonPalette.BgElevated,
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#1B201D"),
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = 0.13
            }
        };
        var revealHost = new Grid();
        revealHost.Children.Add(revealShadow);
        revealHost.Children.Add(revealInner);

        loadRevealPill = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = revealHost
        };
        Panel.SetZIndex(loadRevealPill, 30);
        contentOverlayGrid.Children.Add(loadRevealPill);

        // Floating Batch Dock
        batchCountText = NeonWidgets.Text("已选 0 项", 12.5, NeonPalette.DarkDockInk, FontWeights.SemiBold);
        batchHiddenWarnText = NeonWidgets.Text("", 11.5, NeonPalette.DarkDockWarn);
        batchHiddenWarnText.Margin = new Thickness(8, 0, 0, 0);
        batchHiddenWarnText.Visibility = Visibility.Collapsed;

        var batchInfo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        batchInfo.Children.Add(batchCountText);
        batchInfo.Children.Add(batchHiddenWarnText);

        var batchBtns = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        batchBtns.Children.Add(BuildBatchButton("全选当前筛选", false, SelectAllVisibleMods));
        batchBtns.Children.Add(BuildBatchButton("批量启用", true, () => RequestBatchToggleEnable?.Invoke(checkedModFolders.ToArray())));
        batchBtns.Children.Add(BuildBatchButton("批量停用", false, () => RequestBatchToggleDisable?.Invoke(checkedModFolders.ToArray())));
        batchBtns.Children.Add(BuildBatchButton("检查更新", false, () => RequestBatchCheckUpdates?.Invoke(checkedModFolders.ToArray())));
        batchBtns.Children.Add(BuildBatchButton("取消", false, ClearCheckedMods));

        var batchGrid = new Grid();
        batchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        batchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        batchGrid.Children.Add(batchInfo);
        Grid.SetColumn(batchBtns, 1);
        batchGrid.Children.Add(batchBtns);

        var batchInner = new Border
        {
            Background = NeonPalette.DarkDockBg,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 9, 14, 9),
            Child = batchGrid
        };
        var batchShadow = new Border
        {
            Background = NeonPalette.DarkDockBg,
            CornerRadius = new CornerRadius(10),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#1B201D"),
                BlurRadius = 24,
                ShadowDepth = 6,
                Opacity = 0.20
            }
        };
        var batchHost = new Grid();
        batchHost.Children.Add(batchShadow);
        batchHost.Children.Add(batchInner);
        batchDock = new Border
        {
            Margin = new Thickness(16, 0, 16, 14),
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Child = batchHost
        };
        contentOverlayGrid.Children.Add(batchDock);

        Grid.SetRow(contentOverlayGrid, 1);
        workbenchMainGrid.Children.Add(contentOverlayGrid);
        workbenchMainCard.Child = workbenchMainGrid;
        splitBodyGrid.Children.Add(workbenchMainCard);

        // Right: Contextual Causal Inspector Pane
        inspectorHeadGrid = new Grid();
        var inspectorHeadBorder = new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(15, 13, 15, 11),
            Child = inspectorHeadGrid
        };

        inspectorBodyStack = new StackPanel { Margin = new Thickness(15, 14, 15, 18) };
        inspectorScroll = new ScrollViewer
        {
            Content = inspectorBodyStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false
        };
        NeonMotion.AttachSmoothWheelScroll(inspectorScroll, this);

        inspectorFootBorder = new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(15, 11, 15, 11)
        };

        var inspectorLayout = new Grid();
        inspectorLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inspectorLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        inspectorLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inspectorLayout.Children.Add(inspectorHeadBorder);
        Grid.SetRow(inspectorScroll, 1);
        inspectorLayout.Children.Add(inspectorScroll);
        Grid.SetRow(inspectorFootBorder, 2);
        inspectorLayout.Children.Add(inspectorFootBorder);

        inspectorPane = new Border
        {
            Width = 392,
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Visibility = Visibility.Collapsed,
            RenderTransform = inspectorSlideTransform,
            Child = inspectorLayout
        };
        Grid.SetColumn(inspectorPane, 2);
        splitBodyGrid.Children.Add(inspectorPane);

        Grid.SetRow(splitBodyGrid, 1);
        workbenchStage.Children.Add(splitBodyGrid);

        Grid.SetRow(workbenchStage, 1);
        mainStage.Children.Add(workbenchStage);

        Grid.SetRow(mainStage, 1);
        appShell.Children.Add(mainStage);

        // -------------------- QUIET FOOTER --------------------
        var footer = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 0, 18, 0),
            SnapsToDevicePixels = true
        };
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        footerScanStatus = NeonWidgets.Text("等待读取本地环境…", 11.5, NeonPalette.InkMuted);
        footerLeft.Children.Add(footerScanStatus);
        var sep = NeonWidgets.Text(" · ", 11.5, NeonPalette.InkMuted);
        footerLeft.Children.Add(sep);
        footerLeft.Children.Add(NeonWidgets.Text("所有文件改动均支持预览、备份与可逆回滚", 11.5, NeonPalette.InkMuted));
        footerGrid.Children.Add(footerLeft);

        footerRightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        footerSimWatcherButton = BuildFooterLink("模拟外部改动", () => RequestSimulateExternalChange?.Invoke(),
            "在当前隔离测试目录触发一次外部文件变更以验证健康结论即时失效");
        footerSimWatcherButton.Visibility = Visibility.Collapsed;
        footerRightStack.Children.Add(footerSimWatcherButton);
        footerRightStack.Children.Add(BuildFooterLink("打开 mods 目录", () => RequestOpenModsFolder?.Invoke(), "打开当前游戏的 mods 文件夹"));
        footerRescanButton = BuildFooterLink("刷新扫描", () => RequestRescanAndVerifyHealth?.Invoke(), "重新扫描本地清单与补丁健康状态");
        footerRightStack.Children.Add(footerRescanButton);
        Grid.SetColumn(footerRightStack, 1);
        footerGrid.Children.Add(footerRightStack);
        footer.Child = footerGrid;
        Grid.SetRow(footer, 2);
        appShell.Children.Add(footer);

        // Persistent across all tabs. Unlike a toast it remains visible until
        // the failed recovery family is checked successfully again.
        appShell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        neonRecoveryWarningPanel.Background = NeonPalette.BgSurface;
        neonRecoveryWarningPanel.BorderBrush = NeonPalette.Cinnabar;
        neonRecoveryWarningPanel.BorderThickness = new Thickness(0, 1, 0, 0);
        neonRecoveryWarningPanel.Padding = new Thickness(18, 8, 18, 8);
        neonRecoveryWarningPanel.Child = new ScrollViewer
        {
            Content = neonRecoveryWarningText,
            MaxHeight = 155,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(neonRecoveryWarningPanel, 3);
        appShell.Children.Add(neonRecoveryWarningPanel);

        Children.Add(appShell);

        // ====================================================================
        // LAYER 1: Narrow-window Inspector Scrim
        // ====================================================================
        inspectorScrim = new Border
        {
            Background = NeonPalette.DrawerBackdrop,
            Visibility = Visibility.Collapsed
        };
        inspectorScrim.MouseLeftButtonDown += (_, _) => CloseInspector();
        Panel.SetZIndex(inspectorScrim, 40);
        Children.Add(inspectorScrim);

        // ====================================================================
        // LAYER 2: Tools / Environment / Settings Slide-Over Drawer
        // ====================================================================
        drawerBodyStack = new StackPanel { Margin = new Thickness(18, 15, 18, 20) };
        drawerBackdrop = BuildToolsDrawerOverlay();
        Panel.SetZIndex(drawerBackdrop, 90);
        Children.Add(drawerBackdrop);

        // ====================================================================
        // LAYER 3: Unified Transaction Preview Modal Overlay
        // ====================================================================
        modalTitleText = NeonWidgets.Text("改动预览", 15.5, NeonPalette.InkPrimary, FontWeights.Bold);
        modalSubText = NeonWidgets.Text("", 12, NeonPalette.InkSecondary, wrap: true);
        modalBodyStack = new StackPanel();
        modalFootNoteText = NeonWidgets.Text("", 11.5, NeonPalette.InkMuted, wrap: true);
        modalFootButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        (modalBackdrop, modalSheet) = BuildTransactionModalOverlay();
        Panel.SetZIndex(modalBackdrop, 100);
        Children.Add(modalBackdrop);

        // ====================================================================
        // LAYER 4: Toast Rack (below Drawer/Modal scrims so toasts never overlap modal buttons)
        // ====================================================================
        toastRack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 18, 44),
            Background = null
        };
        Panel.SetZIndex(toastRack, 80);
        Children.Add(toastRack);

        // Keyboard shortcuts & responsive inspector layout
        PreviewKeyDown += OnPreviewKeyDown;
        SizeChanged += (_, _) => UpdateResponsiveInspectorLayout();
        Loaded += (_, _) =>
        {
            ApplyNeonComboStyle();
            PositionNavIndicator(animate: false);
            PositionModeSegThumb(animate: false);
        };
        UpdateNavTabStyles(animateIndicator: false);
        UpdateModeSwitchStyles();
    }

    void ApplyNeonComboStyle()
    {
        if (TryFindResource("NeonComboBox") is Style comboStyle)
            sortCombo.Style = comboStyle;
    }

    Button BuildNavTab(string tabId, string labelText, UIElement? signalBadge)
    {
        var label = NeonWidgets.Text(labelText, 13.5, NeonPalette.InkSecondary, FontWeights.Medium);
        label.VerticalAlignment = VerticalAlignment.Center;

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(label);
        if (signalBadge != null) content.Children.Add(signalBadge);

        var underline = new Border
        {
            Height = 2,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(2, 2, 0, 0),
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            SnapsToDevicePixels = true
        };

        var grid = new Grid { Height = 54 };
        var padded = new Border
        {
            Padding = new Thickness(13, 0, 13, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
        grid.Children.Add(padded);
        grid.Children.Add(underline);

        var btn = new Button
        {
            Content = grid,
            Height = 54,
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = NeonWidgets.CreateInteractiveBareButtonTemplate(pressScale: 0.96, hoverOpacity: 1.0)
        };
        AutomationProperties.SetName(btn, labelText);
        btn.MouseEnter += (_, _) => { if (activeTab != tabId) label.Foreground = NeonPalette.InkPrimary; };
        btn.MouseLeave += (_, _) => { if (activeTab != tabId) label.Foreground = NeonPalette.InkSecondary; };
        btn.Click += (_, _) => SwitchTab(tabId);
        navTabs[tabId] = (btn, label, underline);
        return btn;
    }

    Button BuildSegButton(string text, string mode)
    {
        var btn = new Button
        {
            Content = text,
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            FontFamily = NeonPalette.SansFont,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = NeonPalette.InkSecondary,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = CreateRoundedButtonTemplate(4, Brushes.Transparent, Brushes.Transparent)
        };
        AutomationProperties.SetName(btn, text);
        btn.Click += (_, _) =>
        {
            if (workbenchMode == mode) return;
            workbenchMode = mode;
            UpdateModeSwitchStyles(animateThumb: true);
            RenderWorkbenchContent(preserveScroll: false, animateSettle: true);
        };
        return btn;
    }

    Button BuildFilterPill(string filterId, string labelText)
    {
        var label = NeonWidgets.Text(labelText, 12, NeonPalette.InkSecondary, FontWeights.Medium);
        label.VerticalAlignment = VerticalAlignment.Center;
        var count = NeonWidgets.Text("0", 11, NeonPalette.InkSecondary, mono: true);
        count.Opacity = 0.78;
        count.Margin = new Thickness(5, 0, 0, 0);
        count.VerticalAlignment = VerticalAlignment.Center;

        var inner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        inner.Children.Add(label);
        inner.Children.Add(count);

        var btn = new Button
        {
            Height = 25,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(0, 0, 5, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Content = inner,
            Template = CreateFilterPillTemplate()
        };
        AutomationProperties.SetName(btn, labelText);
        btn.Click += (_, _) =>
        {
            if (activeFilter == filterId) return;
            activeFilter = filterId;
            UpdateFilterPillVisuals();
            RenderWorkbenchContent(preserveScroll: true, animateSettle: true);
        };
        filterPillControls[filterId] = (btn, label, count);
        return btn;
    }

    Button BuildBatchButton(string label, bool primary, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            Height = 27,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = NeonPalette.SansFont,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = NeonPalette.DarkDockInk,
            Background = primary ? NeonPalette.DarkDockPrimary : NeonPalette.DarkDockBtnBg,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = CreateRoundedButtonTemplate(4, primary ? NeonPalette.DarkDockPrimaryHover : NeonPalette.DarkDockBtnHover, Brushes.Transparent)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    Button BuildFooterLink(string text, Action onClick, string toolTip)
    {
        var btn = new Button
        {
            Content = text,
            Height = 22,
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = NeonPalette.SansFont,
            FontSize = 11.5,
            Foreground = NeonPalette.InkSecondary,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Template = CreateRoundedButtonTemplate(4, NeonPalette.BgWell, Brushes.Transparent)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ========================================================================
    // PUBLIC UPDATE & STATE SYNC API (Preserves Search, Focus, & Scroll!)
    // ========================================================================

    public void ApplySnapshot(NeonWorkbenchSnapshot snapshot, bool preserveViewport = true)
    {
        // Capture focus, search caret, and scroll positions BEFORE mutating visuals!
        var focusScope = FocusManager.GetFocusScope(searchBox);
        bool searchHadLogicalFocus = focusScope != null && ReferenceEquals(FocusManager.GetFocusedElement(focusScope), searchBox);
        bool searchHadKeyboardFocus = searchBox.IsKeyboardFocused;
        int caretIndex = searchBox.CaretIndex;
        int selStart = searchBox.SelectionStart;
        int selLength = searchBox.SelectionLength;
        double savedDeskOffset = preserveViewport ? deskScrollViewer.VerticalOffset : 0;
        double savedInspectorOffset = preserveViewport ? inspectorScroll.VerticalOffset : 0;
        double savedSecondaryOffset = preserveViewport && secondaryScrolls.TryGetValue(activeTab, out var activeSv)
            ? activeSv.VerticalOffset
            : 0;

        bool workshopHadKeyboardFocus = workshopUrlBox?.IsKeyboardFocused == true;
        var workshopScope = workshopUrlBox != null ? FocusManager.GetFocusScope(workshopUrlBox) : null;
        bool workshopHadLogicalFocus = workshopUrlBox != null && workshopScope != null &&
            ReferenceEquals(FocusManager.GetFocusedElement(workshopScope), workshopUrlBox);
        int workshopCaret = workshopUrlBox?.CaretIndex ?? 0;

        bool sameGameRoot = lastTrackedGameRoot != null &&
            string.Equals(lastTrackedGameRoot, snapshot.GameRoot, StringComparison.OrdinalIgnoreCase);
        bool prevHadInventory = sameGameRoot && lastSnapshotHadInventory;
        bool prevWasScanning = sameGameRoot && lastSnapshotWasScanning;
        if (!sameGameRoot)
        {
            ResetNeonListChildren();
            neonModPage = 1;
            checkedModFolders.Clear();
            if (selectedModId != null)
                FinishInspectorCollapseImmediate(preserveScroll: false);
            lastTrackedGameRoot = snapshot.GameRoot;
            lastModEnabledById.Clear();
            pendingModFlipPrevState.Clear();
            inlineToggleStatuses.Clear();
            inspectorReceipts.Clear();
            animatedReceiptIds.Clear();
            modFileStatsCache.Clear();
            hasInitializedCounts = false;
            lastFilterCounts.Clear();
        }
        else
        {
            if (prevWasScanning && !snapshot.IsScanning && snapshot.HasInventory)
                modFileStatsCache.Clear();
            if (snapshot.HasInventory)
            {
                foreach (var row in snapshot.AllRows)
                {
                    if (lastModEnabledById.TryGetValue(row.Mod.Id, out bool prevEnabled) && prevEnabled != row.Mod.Enabled)
                        pendingModFlipPrevState[row.Mod.Id] = prevEnabled;
                }
            }
        }
        if (snapshot.HasInventory)
        {
            lastModEnabledById.Clear();
            foreach (var row in snapshot.AllRows)
                lastModEnabledById[row.Mod.Id] = row.Mod.Enabled;
        }
        lastSnapshotHadInventory = snapshot.HasInventory;
        lastSnapshotWasScanning = snapshot.IsScanning;

        bool shouldCascadeRows = !prevHadInventory && snapshot.HasInventory && !snapshot.IsScanning;
        bool shouldShowReveal = snapshot.HasInventory && !snapshot.IsScanning && (!prevHadInventory || prevWasScanning);

        currentSnapshot = snapshot;

        // Prune checked folders that no longer exist in inventory
        if (snapshot.HasInventory)
        {
            var validFolders = snapshot.AllRows
                .Select(r => r.Mod.Folder)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            checkedModFolders.IntersectWith(validFolders);
        }

        // Prune checked Co-op rows that are no longer selectable
        if (snapshot.Coop.HasPeer)
        {
            var validCoop = snapshot.Coop.Rows.Where(r => r.Selectable).Select(r => r.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            coopCheckedIds.IntersectWith(validCoop);
        }
        else
        {
            coopCheckedIds.Clear();
        }

        // Prune selected patch IDs that are no longer selectable
        var selectablePatches = snapshot.AllCatalogPatches
            .Where(p => p.ScanState?.Status is "待修复" or "需联动")
            .Select(p => p.PatchId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        selectedPatchIds.IntersectWith(selectablePatches);

        // If selected MOD was removed during a background scan, collapse inspector quietly
        if (selectedModId != null && snapshot.HasInventory && !snapshot.AllRows.Any(r => r.Mod.Id.Equals(selectedModId, StringComparison.OrdinalIgnoreCase)))
            FinishInspectorCollapseImmediate(preserveScroll: preserveViewport, explicitSavedOffset: savedDeskOffset);

        UpdateChrome(snapshot);
        RenderLaunchGate(snapshot, animateChips: shouldCascadeRows);
        UpdateFilterPillVisuals();
        RenderWorkbenchContent(preserveScroll: preserveViewport, explicitSavedOffset: savedDeskOffset, cascadeReveal: shouldCascadeRows);
        RenderInspector(preserveScroll: preserveViewport, explicitSavedOffset: savedInspectorOffset);

        if (shouldShowReveal && activeTab == "workbench")
            TriggerLoadRevealBanner(snapshot, isRescan: prevHadInventory && prevWasScanning);
        else if (!snapshot.HasInventory)
        {
            loadRevealGeneration++;
            loadRevealPill.Visibility = Visibility.Collapsed;
        }

        if (activeTab != "workbench")
            RenderSecondaryPage(activeTab, snapshot, preserveScroll: preserveViewport, explicitSavedOffset: savedSecondaryOffset);
        if (drawerOpenLogical)
            RenderToolsDrawerContent(snapshot);

        // Guarantee search focus & caret are NEVER stolen by a background refresh
        if ((searchHadKeyboardFocus || searchHadLogicalFocus) && activeTab == "workbench" && !IsModalOpen && !drawerOpenLogical)
        {
            if (searchHadKeyboardFocus && !searchBox.IsKeyboardFocused)
                Keyboard.Focus(searchBox);
            else if (searchHadLogicalFocus && focusScope != null && !ReferenceEquals(FocusManager.GetFocusedElement(focusScope), searchBox))
                FocusManager.SetFocusedElement(focusScope, searchBox);
            int maxLen = searchBox.Text.Length;
            searchBox.SelectionStart = Math.Min(selStart, maxLen);
            searchBox.SelectionLength = Math.Min(selLength, Math.Max(0, maxLen - searchBox.SelectionStart));
            if (selLength == 0)
                searchBox.CaretIndex = Math.Min(caretIndex, maxLen);
        }
        else if ((workshopHadKeyboardFocus || workshopHadLogicalFocus) && activeTab == "acquire" && workshopUrlBox != null && !IsModalOpen && !drawerOpenLogical)
        {
            if (workshopHadKeyboardFocus && !workshopUrlBox.IsKeyboardFocused)
                Keyboard.Focus(workshopUrlBox);
            else if (workshopHadLogicalFocus && workshopScope != null && !ReferenceEquals(FocusManager.GetFocusedElement(workshopScope), workshopUrlBox))
                FocusManager.SetFocusedElement(workshopScope, workshopUrlBox);
            workshopUrlBox.CaretIndex = Math.Min(workshopCaret, workshopUrlBox.Text.Length);
        }
    }

    public void UpdateRunningStateOnly(bool gameRunning)
    {
        if (currentSnapshot == null || currentSnapshot.GameRunning == gameRunning) return;
        currentSnapshot = currentSnapshot with { GameRunning = gameRunning };
        UpdateChrome(currentSnapshot);
        RenderWorkbenchContent(preserveScroll: true);
    }

    public void UpdateOrphanAutoStopOnly(bool enabled, NeonRecoverySnapshotState recoveryState)
    {
        if (currentSnapshot == null) return;
        currentSnapshot = currentSnapshot with
        {
            OrphanAutoStopEnabled = enabled,
            Recovery = recoveryState
        };
    }

    internal NeonInlineToggleStatus? GetInlineToggleStatus(string modId) =>
        inlineToggleStatuses.TryGetValue(modId, out var st) ? st : null;

    internal NeonInspectorResolutionReceipt? GetInspectorReceipt(string modId) =>
        inspectorReceipts.TryGetValue(modId, out var r) ? r : null;

    public void SetInlineToggleStatus(string modId, NeonInlineToggleStatus status, bool rerender = true)
    {
        inlineToggleStatuses[modId] = status;
        if (!rerender || currentSnapshot == null) return;
        if (RefreshSingleModRowInPlace(modId))
        {
            if (IsInspectorOpen)
                RenderInspector(preserveScroll: true, explicitSavedOffset: inspectorScroll.VerticalOffset);
            return;
        }
        RerenderWorkbenchAndInspectorPreservingViewport();
    }

    bool RefreshSingleModRowInPlace(string modId)
    {
        if (currentSnapshot == null || activeTab != "workbench" || workbenchMode != "list") return false;
        if (!visibleRowVisuals.TryGetValue(modId, out var existing)) return false;
        var rowData = currentSnapshot.AllRows.FirstOrDefault(r => r.Mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (rowData == null) return false;
        int idx = deskContentStack.Children.IndexOf(existing.Outer);
        if (idx < 0) return false;

        var replacement = BuildModRowElement(rowData);
        deskContentStack.Children.RemoveAt(idx);
        deskContentStack.Children.Insert(idx, replacement);
        RememberNeonPageRow(rowData, replacement);
        return true;
    }

    public void ClearInlineToggleStatus(string modId, bool rerender = false)
    {
        bool removed = inlineToggleStatuses.Remove(modId);
        if (removed && rerender && currentSnapshot != null)
            RerenderWorkbenchAndInspectorPreservingViewport();
    }

    public void SetInspectorReceipt(string modId, NeonInspectorResolutionReceipt receipt, bool rerender = true)
    {
        inspectorReceipts[modId] = receipt;
        animatedReceiptIds.Remove(modId);
        if (rerender && currentSnapshot != null)
            RerenderWorkbenchAndInspectorPreservingViewport();
    }

    public void ClearInspectorReceipt(string modId, bool rerender = false)
    {
        bool removed = inspectorReceipts.Remove(modId);
        animatedReceiptIds.Remove(modId);
        if (removed && rerender && currentSnapshot != null)
            RerenderWorkbenchAndInspectorPreservingViewport();
    }

    public void ClearAllPendingScanStates()
    {
        foreach (var key in inlineToggleStatuses
            .Where(kv => kv.Value.Phase is NeonInlineTogglePhase.AppliedScanFailed or NeonInlineTogglePhase.AppliedScanPending)
            .Select(kv => kv.Key)
            .ToArray())
        {
            inlineToggleStatuses.Remove(key);
        }
        foreach (var key in inspectorReceipts
            .Where(kv => kv.Value.ShowRescanAction)
            .Select(kv => kv.Key)
            .ToArray())
        {
            inspectorReceipts.Remove(key);
            animatedReceiptIds.Remove(key);
        }
    }

    public void RefreshInspectorOnlyPreservingScroll()
    {
        if (currentSnapshot == null || !IsInspectorOpen) return;
        double savedInspectorOffset = inspectorScroll.VerticalOffset;
        RenderInspector(preserveScroll: true, explicitSavedOffset: savedInspectorOffset);
    }

    void RerenderWorkbenchAndInspectorPreservingViewport()
    {
        var focusScope = FocusManager.GetFocusScope(searchBox);
        bool searchHadLogicalFocus = focusScope != null && ReferenceEquals(FocusManager.GetFocusedElement(focusScope), searchBox);
        bool searchHadKeyboardFocus = searchBox.IsKeyboardFocused;
        int caretIndex = searchBox.CaretIndex;
        double savedDeskOffset = deskScrollViewer.VerticalOffset;
        double savedInspectorOffset = inspectorScroll.VerticalOffset;

        RenderWorkbenchContent(preserveScroll: true, explicitSavedOffset: savedDeskOffset);
        RenderInspector(preserveScroll: true, explicitSavedOffset: savedInspectorOffset);

        if ((searchHadKeyboardFocus || searchHadLogicalFocus) && activeTab == "workbench" && !IsModalOpen && !drawerOpenLogical)
        {
            if (searchHadKeyboardFocus && !searchBox.IsKeyboardFocused)
                Keyboard.Focus(searchBox);
            else if (searchHadLogicalFocus && focusScope != null && !ReferenceEquals(FocusManager.GetFocusedElement(focusScope), searchBox))
                FocusManager.SetFocusedElement(focusScope, searchBox);
            searchBox.CaretIndex = Math.Min(caretIndex, searchBox.Text.Length);
        }
    }

    internal void ClearToasts()
    {
        toastRack.Children.Clear();
        loadRevealGeneration++;
        loadRevealPill.Visibility = Visibility.Collapsed;
    }

    void TriggerLoadRevealBanner(NeonWorkbenchSnapshot s, bool isRescan)
    {
        int modCount = s.AllRows.Length;
        int issueCount = s.FilterCounts.TryGetValue("issues", out int ic) ? ic : 0;

        loadRevealPrimaryText.Text = isRescan
            ? $"已刷新 {modCount} 个模组"
            : $"已加载 {modCount} 个模组";

        if (modCount == 0)
            loadRevealMetaText.Text = " · 暂无普通模组";
        else if (issueCount > 0)
            loadRevealMetaText.Text = $" · {issueCount} 项待处理";
        else
            loadRevealMetaText.Text = " · 全部就绪";

        NeonMotion.ShowFloatingRevealPill(
            this,
            loadRevealPill,
            holdMs: 1750,
            () => loadRevealGeneration,
            ++loadRevealGeneration);
    }

    public void ShowToast(string message, NeonTone tone = NeonTone.Sage)
    {
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Fill = tone switch
            {
                NeonTone.Amber => NeonPalette.ToastWarnDot,
                NeonTone.Cinnabar => NeonPalette.ToastDangerDot,
                _ => NeonPalette.ToastOkDot
            }
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(0, 5, 9, 0);
        row.Children.Add(dot);
        var msgTb = NeonWidgets.Text(message, 12.5, NeonPalette.DarkDockInk, wrap: true);
        Grid.SetColumn(msgTb, 1);
        row.Children.Add(msgTb);

        var toastCard = new Border
        {
            Background = NeonPalette.DarkDockBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 400,
            Child = row
        };
        var toastShadow = new Border
        {
            Background = NeonPalette.DarkDockBg,
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#1B201D"),
                BlurRadius = 20,
                ShadowDepth = 5,
                Opacity = 0.22
            }
        };
        var toastHost = new Grid { Margin = new Thickness(0, 8, 0, 0), MaxWidth = 400, Cursor = Cursors.Hand };
        toastHost.Children.Add(toastShadow);
        toastHost.Children.Add(toastCard);

        while (toastRack.Children.Count >= 3)
            toastRack.Children.RemoveAt(0);
        toastRack.Children.Add(toastHost);
        NeonMotion.EnterToast(this, toastHost);

        bool dismissed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        void DismissToast()
        {
            if (dismissed) return;
            dismissed = true;
            timer.Stop();
            NeonMotion.ExitToast(this, toastHost, () => toastRack.Children.Remove(toastHost));
        }

        toastHost.MouseEnter += (_, _) =>
        {
            if (!dismissed) timer.Stop();
        };
        toastHost.MouseLeave += (_, _) =>
        {
            if (!dismissed)
            {
                timer.Interval = TimeSpan.FromMilliseconds(1600);
                timer.Start();
            }
        };
        toastHost.MouseLeftButtonUp += (_, _) => DismissToast();
        timer.Tick += (_, _) => DismissToast();
        timer.Start();
    }

    public void FocusMod(string modId, bool openUpdatePreview = false)
    {
        if (currentSnapshot == null) return;
        if (activeTab != "workbench") SwitchTab("workbench");
        if (workbenchMode != "list")
        {
            workbenchMode = "list";
            UpdateModeSwitchStyles();
        }

        var visible = NeonWorkbenchBuilder.FilterAndSort(currentSnapshot.AllRows, activeFilter, searchBox.Text, sortBy);
        if (!visible.Any(r => r.Mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase)))
        {
            activeFilter = "all";
            if (searchBox.Text.Length > 0) searchBox.Text = "";
            UpdateFilterPillVisuals();
        }

        highlightedModId = modId;
        bool wasAlreadyOpen = inspectorPane.Visibility == Visibility.Visible && !inspectorClosing && selectedModId != null;
        bool wasClosing = inspectorClosing;
        selectedModId = modId;
        EnsureNeonPageForMod(modId);
        RenderWorkbenchContent(preserveScroll: false);
        if (wasAlreadyOpen)
        {
            RenderInspector(preserveScroll: false);
        }
        else
        {
            int myGen = ++inspectorAnimGeneration;
            inspectorClosing = false;
            inspectorPane.IsHitTestVisible = true;
            RenderInspector(preserveScroll: false);
            NeonMotion.OpenInspector(
                this,
                inspectorPane,
                inspectorScrim,
                retargetFromClose: wasClosing,
                () => myGen == inspectorAnimGeneration);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!string.Equals(selectedModId, modId, StringComparison.OrdinalIgnoreCase)) return;
            ScrollModRowIntoView(modId);
            if (openUpdatePreview)
            {
                var row = currentSnapshot?.AllRows.FirstOrDefault(r => r.Mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
                if (row?.HasUpdate == true)
                    RequestPreviewUpdateMod?.Invoke(row.Mod);
            }
        }), DispatcherPriority.Loaded);
    }

    void ScrollModRowIntoView(string modId)
    {
        EnsureNeonPageForMod(modId);
        foreach (FrameworkElement child in deskContentStack.Children)
        {
            if (child.Tag is string id && id.Equals(modId, StringComparison.OrdinalIgnoreCase))
            {
                if (deskScrollViewer.ViewportHeight > 0 && child.ActualHeight > 0)
                {
                    try
                    {
                        var topLeft = child.TransformToAncestor(deskContentStack).Transform(new Point(0, 0));
                        double rowTop = topLeft.Y;
                        double rowBottom = rowTop + child.ActualHeight;
                        double viewTop = deskScrollViewer.VerticalOffset;
                        double topPad = 52.0;
                        double bottomPad = batchDockOpenLogical ? 116.0 : 52.0;
                        double viewBottom = viewTop + deskScrollViewer.ViewportHeight;

                        if (rowTop < viewTop + topPad)
                            deskScrollViewer.ScrollToVerticalOffset(Math.Max(0.0, rowTop - topPad));
                        else if (rowBottom > viewBottom - bottomPad)
                            deskScrollViewer.ScrollToVerticalOffset(Math.Min(
                                deskScrollViewer.ScrollableHeight,
                                rowBottom - deskScrollViewer.ViewportHeight + bottomPad));
                        return;
                    }
                    catch { }
                }
                child.BringIntoView();
                break;
            }
        }
    }

    // ========================================================================
    // CHROME, LAUNCH GATE & FILTER PILLS
    // ========================================================================

    void UpdateChrome(NeonWorkbenchSnapshot s)
    {
        // Nav signals — strictly hidden when 0; micro-pulse only when counts genuinely change!
        navWorkbenchDot.Visibility = s.HardBlockCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        NeonWidgets.UpdateNavSignalCount(navPatchesPill, navPatchesCount, s.PendingPatchesCount);
        NeonWidgets.UpdateNavSignalCount(navAcquirePill, navAcquireCount, s.AvailableUpdatesCount);

        if (hasInitializedCounts)
        {
            if (s.HardBlockCount != lastHardBlockCount && s.HardBlockCount > 0)
                NeonMotion.PulseBadge(this, navWorkbenchDot);
            if (s.PendingPatchesCount != lastNavPatchesCount && s.PendingPatchesCount > 0)
                NeonMotion.PulseBadge(this, navPatchesPill);
            if (s.AvailableUpdatesCount != lastNavAcquireCount && s.AvailableUpdatesCount > 0)
                NeonMotion.PulseBadge(this, navAcquirePill);
        }
        bool navCountsChanged = !hasInitializedCounts ||
            s.HardBlockCount != lastHardBlockCount ||
            s.PendingPatchesCount != lastNavPatchesCount ||
            s.AvailableUpdatesCount != lastNavAcquireCount;
        lastHardBlockCount = s.HardBlockCount;
        lastNavPatchesCount = s.PendingPatchesCount;
        lastNavAcquireCount = s.AvailableUpdatesCount;
        hasInitializedCounts = true;
        if (navCountsChanged)
            Dispatcher.BeginInvoke(new Action(() => PositionNavIndicator(animate: false)), DispatcherPriority.Loaded);

        // Path pill & status lamp
        envPathLabel.Text = s.GameRoot == null
            ? "选择游戏目录"
            : s.GameRunning
                ? "运行中 · " + NeonWorkbenchBuilder.ShortenGamePath(s.GameRoot)
                : NeonWorkbenchBuilder.ShortenGamePath(s.GameRoot);
        envPillButton.ToolTip = s.GameRoot ?? "尚未选择游戏目录，点击定位";

        if (s.GameRoot == null)
        {
            envPillButton.Background = NeonPalette.BgGround;
            envPillButton.BorderBrush = NeonPalette.BorderSubtle;
            envPathLabel.Foreground = NeonPalette.InkSecondary;
            NeonWidgets.SetStatusLamp(envLampHalo, envLampCore, NeonTone.Cinnabar, showHalo: false);
        }
        else if (s.GameRunning)
        {
            envPillButton.Background = NeonPalette.AmberBg;
            envPillButton.BorderBrush = NeonPalette.AmberBorder;
            envPathLabel.Foreground = NeonPalette.Amber;
            NeonWidgets.SetStatusLamp(envLampHalo, envLampCore, NeonTone.Amber, showHalo: true);
        }
        else
        {
            envPillButton.Background = NeonPalette.BgGround;
            envPillButton.BorderBrush = NeonPalette.BorderSubtle;
            envPathLabel.Foreground = NeonPalette.InkSecondary;
            NeonWidgets.SetStatusLamp(envLampHalo, envLampCore, NeonTone.Sage, showHalo: false);
        }

        // Launch button
        if (s.GameRoot == null)
        {
            ApplyLaunchButtonStyle(caution: false, label: "选择游戏目录");
        }
        else if (s.GameRunning)
        {
            ApplyLaunchButtonStyle(caution: true, label: "游戏运行中");
        }
        else if (s.HardBlockCount > 0)
        {
            ApplyLaunchButtonStyle(caution: true, label: $"仍有 {s.HardBlockCount} 项阻断");
        }
        else
        {
            ApplyLaunchButtonStyle(caution: false, label: "启动游戏");
        }

        gearUpdateDot.Visibility = s.ManagerUpdate?.HasNewerRelease == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Watcher / Stale Health Banner
        bool showStaleBanner = s.GameRoot != null && (s.ModInventoryDirty || s.LifecycleStale);
        watcherBanner.Visibility = showStaleBanner ? Visibility.Visible : Visibility.Collapsed;
        if (showStaleBanner)
        {
            if (s.IsScanning)
            {
                watcherTitleText.Text = "正在扫描核验：";
                watcherBodyText.Text = s.ScanStageText ?? "正在重新读取本地 MOD 清单与补丁健康状态，请稍候…";
            }
            else
            {
                watcherTitleText.Text = s.ModInventoryDirty ? "检测到外部文件变动：" : "补丁健康结论已过期：";
                watcherBodyText.Text = !string.IsNullOrWhiteSpace(s.LifecycleStatus)
                    ? s.LifecycleStatus
                    : "mods 目录发生变化，旧的补丁健康结论已自动失效，请重新扫描核验。";
            }
        }
        watcherRescanButton.Content = s.IsScanning ? "扫描中…" : "重新扫描并核验";
        watcherRescanButton.IsEnabled = !s.IsScanning;
        watcherRescanButton.Cursor = s.IsScanning ? Cursors.Arrow : Cursors.Hand;

        // Quiet Footer
        if (s.IsScanning)
        {
            footerScanStatus.Text = s.ScanStageText ?? "正在扫描本地 MOD 清单与补丁状态…";
            footerRescanButton.Content = "正在扫描…";
            footerRescanButton.IsEnabled = false;
            footerRescanButton.Cursor = Cursors.Arrow;
        }
        else
        {
            string timeLabel = s.LastScanTime.HasValue ? s.LastScanTime.Value.ToString("HH:mm:ss") : "尚未扫描";
            string healthNote = s.GameRoot == null
                ? "尚未选择游戏目录"
                : s.ModInventoryDirty || s.LifecycleStale
                    ? "检测到文件变化，健康结论已失效"
                    : s.IsLifecycleHealthValid
                        ? "清单完整 · 健康结论有效"
                        : s.IsPatchScanValid
                            ? "清单完整 · 待补丁健康核验"
                            : "清单已读取 · 补丁待扫描";
            footerScanStatus.Text = $"上次扫描：{timeLabel} · {healthNote}";
            footerRescanButton.Content = "刷新扫描";
            footerRescanButton.IsEnabled = true;
            footerRescanButton.Cursor = Cursors.Hand;
        }
    }

    void ApplyLaunchButtonStyle(bool caution, string label)
    {
        launchLabel.Text = label;
        AutomationProperties.SetName(launchButton, label);
        if (caution)
        {
            launchButton.Background = NeonPalette.AmberBg;
            launchButton.BorderBrush = NeonPalette.AmberBorder;
            launchLabel.Foreground = NeonPalette.Amber;
            launchPlayHost.Content = NeonWidgets.CreatePlayIcon(NeonPalette.Amber);
            launchButton.Template = CreateRoundedButtonTemplate(6, NeonPalette.AmberHoverBg, NeonPalette.AmberBorder);
        }
        else
        {
            launchButton.Background = NeonPalette.Sage;
            launchButton.BorderBrush = NeonPalette.Sage;
            launchLabel.Foreground = NeonPalette.SageOnPrimary;
            launchPlayHost.Content = NeonWidgets.CreatePlayIcon(NeonPalette.SageOnPrimary);
            launchButton.Template = CreateRoundedButtonTemplate(6, NeonPalette.SageHover, NeonPalette.SageHover);
        }
    }

    void RenderLaunchGate(NeonWorkbenchSnapshot s, bool animateChips = false)
    {
        launchGateStack.Children.Clear();
        launchGateShadow.Visibility = Visibility.Collapsed;
        launchGateShadow.Effect = null;

        if (s.GateMode == NeonGateMode.NoGameRoot)
        {
            launchGateBorder.Background = NeonPalette.BgSurface;
            launchGateBorder.BorderBrush = NeonPalette.BorderSubtle;
            var row = BuildGateHeaderRow(
                NeonTone.Neutral,
                "尚未关联游戏目录",
                "选择包含 SlayTheSpire2.exe 的目录后，自动读取真实 MOD 清单与依赖图。",
                "选择游戏目录",
                () => RequestChooseGameRoot?.Invoke(),
                padding: new Thickness(15, 11, 15, 11),
                primaryAction: true);
            launchGateStack.Children.Add(row);
            return;
        }

        if (!s.HasInventory)
        {
            launchGateBorder.Background = NeonPalette.BgSurface;
            launchGateBorder.BorderBrush = s.IsScanning ? NeonPalette.AmberBorder : NeonPalette.BorderSubtle;
            var pendingRow = s.IsScanning
                ? BuildGateHeaderRow(
                    NeonTone.Amber,
                    "正在扫描游戏目录",
                    s.ScanStageText ?? "正在读取本地 MOD 清单、依赖关系与补丁状态…",
                    "扫描中…",
                    () => { },
                    padding: new Thickness(15, 11, 15, 11),
                    actionDisabled: true,
                    pulseLamp: true)
                : BuildGateHeaderRow(
                    NeonTone.Neutral,
                    "尚未读取本地 MOD 清单",
                    "点击立即扫描读取当前目录的 MOD、依赖声明与补丁状态。",
                    "立即扫描",
                    () => RequestRescanAll?.Invoke(),
                    padding: new Thickness(15, 11, 15, 11),
                    primaryAction: true);
            launchGateStack.Children.Add(pendingRow);
            return;
        }

        if (s.GateMode == NeonGateMode.Ready)
        {
            launchGateBorder.Background = NeonPalette.ReadyGateBg;
            launchGateBorder.BorderBrush = NeonPalette.SageReadyBorder;

            // CRITICAL EPISTEMIC GUARD:
            // NEVER display "校验通过" unless IsLifecycleHealthValid == true!
            string metaText;
            if (s.IsLifecycleHealthValid)
            {
                string retireExtra = s.RetirablePatchesCount > 0
                    ? $"（另有 {s.RetirablePatchesCount} 项旧补丁可按需退役）"
                    : "";
                metaText = $"{s.EnabledModsCount} 项 MOD 已启用 · 全部声明依赖已满足 · {s.VerifiedInstalledPatchesCount} 项修复校验通过{retireExtra}";
            }
            else if (s.LifecycleScanning)
            {
                metaText = $"{s.EnabledModsCount} 项 MOD 已启用 · 全部声明依赖已满足 · {s.ScanStageText ?? "正在核验补丁健康…"}";
            }
            else if (s.LifecycleStale || s.ModInventoryDirty)
            {
                metaText = $"{s.EnabledModsCount} 项 MOD 已启用 · 全部声明依赖已满足 · 旧健康结论已撤下，需重新扫描核验";
            }
            else
            {
                metaText = $"{s.EnabledModsCount} 项 MOD 已启用 · 全部声明依赖已满足 · 尚未执行补丁健康核验";
            }

            string title = s.IsLifecycleHealthValid ? "当前环境就绪" : "声明依赖已满足";
            var readyRow = BuildGateHeaderRow(
                s.LifecycleStale || s.ModInventoryDirty || s.IsScanning ? NeonTone.Amber : NeonTone.Sage,
                title,
                metaText,
                s.IsScanning ? "扫描中…" : "重新扫描",
                () => RequestRescanAndVerifyHealth?.Invoke(),
                padding: new Thickness(15, 9, 15, 9),
                rightNote: "仅覆盖清单声明与已收录补丁",
                actionDisabled: s.IsScanning,
                pulseLamp: s.IsScanning);
            launchGateStack.Children.Add(readyRow);
            return;
        }

        // Diagnostic Unfold State (Blocked or Advisory)
        bool blocked = s.GateMode == NeonGateMode.Blocked;
        launchGateBorder.Background = NeonPalette.BgSurface;
        launchGateBorder.BorderBrush = blocked ? NeonPalette.CinnabarBorder : NeonPalette.AmberBorder;
        launchGateShadow.Visibility = Visibility.Visible;
        launchGateShadow.Effect = new DropShadowEffect
        {
            Color = (Color)ColorConverter.ConvertFromString(blocked ? "#B83525" : "#A15009"),
            BlurRadius = 10,
            ShadowDepth = 2,
            Opacity = 0.06
        };

        string headerTitle = blocked
            ? $"开局前建议处理 {s.Faults.Length} 项问题"
            : $"依赖正常 · 另有 {s.Faults.Length} 项补丁待处理";
        string headerSub = blocked
            ? "点任一项直接定位因果链与修复入口，全部处理后此提示带将自动收起为就绪状态。"
            : "基础依赖均已满足，建议开局前先核对下方修复补丁。";

        launchGateStack.Children.Add(BuildGateHeaderRow(
            blocked ? NeonTone.Cinnabar : NeonTone.Amber,
            headerTitle,
            headerSub,
            s.IsScanning ? "扫描中…" : "重新扫描",
            () => RequestRescanAndVerifyHealth?.Invoke(),
            padding: new Thickness(15, 11, 15, 11),
            actionDisabled: s.IsScanning,
            pulseLamp: s.IsScanning));

        // Surgical Fault Chips (Uniform horizontal grid when <=3, WrapPanel if more)
        Panel faultsHost;
        if (s.Faults.Length <= 3)
        {
            var ug = new UniformGrid { Rows = 1, Columns = s.Faults.Length, Margin = new Thickness(15, 0, 15, 12) };
            faultsHost = ug;
        }
        else
        {
            faultsHost = new WrapPanel { Margin = new Thickness(15, 0, 15, 12) };
        }

        for (int i = 0; i < s.Faults.Length; i++)
        {
            var chip = BuildFaultChip(s.Faults[i]);
            if (faultsHost is UniformGrid)
                chip.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == s.Faults.Length - 1 ? 0 : 4, 0);
            else
            {
                chip.MinWidth = 260;
                chip.Margin = new Thickness(0, 0, 8, 8);
            }
            if (animateChips)
                NeonMotion.PlayCascadeReveal(this, chip, i, maxAnimated: 4);
            faultsHost.Children.Add(chip);
        }
        launchGateStack.Children.Add(faultsHost);
    }

    Grid BuildGateHeaderRow(
        NeonTone beaconTone,
        string title,
        string subtitle,
        string actionLabel,
        Action onAction,
        Thickness padding,
        string? rightNote = null,
        bool primaryAction = false,
        bool actionDisabled = false,
        bool pulseLamp = false)
    {
        var grid = new Grid { Margin = padding };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new Grid();
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var (lampHost, halo, core) = NeonWidgets.CreateStatusLamp(9);
        NeonWidgets.SetStatusLamp(halo, core, beaconTone, showHalo: beaconTone != NeonTone.Neutral);
        if (pulseLamp)
            NeonMotion.StartSubtlePulse(this, halo);
        lampHost.Margin = new Thickness(-3, -3, 7, -3);
        left.Children.Add(lampHost);

        var titleTb = NeonWidgets.Text(title, 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        titleTb.VerticalAlignment = VerticalAlignment.Center;
        titleTb.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(titleTb, 1);
        left.Children.Add(titleTb);

        var metaTb = NeonWidgets.Text(subtitle, 12.5, NeonPalette.InkSecondary, ellipsis: true);
        metaTb.VerticalAlignment = VerticalAlignment.Center;
        metaTb.ToolTip = subtitle;
        Grid.SetColumn(metaTb, 2);
        left.Children.Add(metaTb);
        grid.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        if (rightNote != null)
        {
            var noteTb = NeonWidgets.Text(rightNote, 11.5, NeonPalette.InkMuted);
            noteTb.VerticalAlignment = VerticalAlignment.Center;
            noteTb.Margin = new Thickness(0, 0, 8, 0);
            right.Children.Add(noteTb);
        }

        var btn = new Button
        {
            Content = actionLabel,
            Height = primaryAction ? 28 : 24,
            Padding = new Thickness(primaryAction ? 11 : 8, 0, primaryAction ? 11 : 8, 0),
            FontFamily = NeonPalette.SansFont,
            FontSize = primaryAction ? 12 : 11.5,
            FontWeight = FontWeights.Medium,
            Background = primaryAction ? NeonPalette.Sage : Brushes.Transparent,
            Foreground = primaryAction ? NeonPalette.SageOnPrimary : NeonPalette.InkSecondary,
            BorderBrush = primaryAction ? NeonPalette.Sage : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            IsEnabled = !actionDisabled,
            Cursor = actionDisabled ? Cursors.Arrow : Cursors.Hand,
            Template = CreateRoundedButtonTemplate(6,
                primaryAction ? NeonPalette.SageHover : NeonPalette.BgWell,
                primaryAction ? NeonPalette.SageHover : Brushes.Transparent)
        };
        if (!actionDisabled)
            btn.Click += (_, _) => onAction();
        right.Children.Add(btn);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    Button BuildFaultChip(NeonFaultChip fault)
    {
        var (accent, bg, hoverBorder, glyph) = fault.Severity switch
        {
            NeonTone.Cinnabar => (NeonPalette.Cinnabar, NeonPalette.ChipDangerBg, NeonPalette.CinnabarBorder, "●"),
            NeonTone.Amber => (NeonPalette.Amber, NeonPalette.ChipWarnBg, NeonPalette.AmberBorder, "▲"),
            _ => (NeonPalette.Indigo, NeonPalette.ChipPatchBg, NeonPalette.IndigoBorder, "◆")
        };

        var outer = new Border
        {
            Background = bg,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var accentBar = new Border { Background = accent };
        innerGrid.Children.Add(accentBar);

        var bodyStack = new StackPanel { Margin = new Thickness(10, 8, 10, 8), VerticalAlignment = VerticalAlignment.Center };
        var kickerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var glyphTb = NeonWidgets.Text(glyph, 10.5, accent, FontWeights.SemiBold);
        glyphTb.VerticalAlignment = VerticalAlignment.Center;
        glyphTb.Margin = new Thickness(0, 0, 5, 0);
        kickerRow.Children.Add(glyphTb);
        kickerRow.Children.Add(NeonWidgets.Text(fault.Kicker, 11.5, accent, FontWeights.SemiBold));
        bodyStack.Children.Add(kickerRow);

        if (fault.VersionDiff != null)
        {
            var descTb = new TextBlock
            {
                FontFamily = NeonPalette.SansFont,
                FontSize = 12.5,
                Foreground = NeonPalette.InkPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                ToolTip = fault.Description
            };
            descTb.Inlines.Add(new Run(fault.VersionDiff.PrereqId + " ") { FontWeight = FontWeights.SemiBold });
            NeonWidgets.AppendVersionDiffInlines(
                descTb.Inlines,
                fault.VersionDiff.ActualVersion,
                fault.VersionDiff.RequiredVersion,
                fault.VersionDiff.Operator,
                warn: false,
                fontSize: 12);
            if (!string.IsNullOrEmpty(fault.VersionDiffTail))
                descTb.Inlines.Add(new Run(fault.VersionDiffTail));
            bodyStack.Children.Add(descTb);
        }
        else
        {
            var desc = NeonWidgets.Text(fault.Description, 12.5, NeonPalette.InkPrimary, ellipsis: true);
            desc.ToolTip = fault.Description;
            bodyStack.Children.Add(desc);
        }
        Grid.SetColumn(bodyStack, 1);
        innerGrid.Children.Add(bodyStack);

        var ctaPill = new Border
        {
            Background = NeonPalette.BgGround,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 8, 11, 8),
            VerticalAlignment = VerticalAlignment.Center,
            Child = NeonWidgets.Text(fault.CtaLabel + " →", 12, NeonPalette.InkSecondary, FontWeights.SemiBold)
        };
        Grid.SetColumn(ctaPill, 2);
        innerGrid.Children.Add(ctaPill);
        outer.Child = innerGrid;

        var btn = new Button
        {
            Content = outer,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = NeonWidgets.CreateInteractiveBareButtonTemplate(pressScale: 0.985, hoverOpacity: 1.0)
        };
        AutomationProperties.SetName(btn, $"{fault.Kicker}：{fault.Description}，{fault.CtaLabel}");
        btn.MouseEnter += (_, _) =>
        {
            outer.BorderBrush = hoverBorder;
            ctaPill.Background = NeonPalette.BgWell;
            if (ctaPill.Child is TextBlock tb) tb.Foreground = NeonPalette.InkPrimary;
            NeonMotion.NudgeHorizontal(this, ctaPill, 3.0);
        };
        btn.MouseLeave += (_, _) =>
        {
            outer.BorderBrush = NeonPalette.BorderSubtle;
            ctaPill.Background = NeonPalette.BgGround;
            if (ctaPill.Child is TextBlock tb) tb.Foreground = NeonPalette.InkSecondary;
            NeonMotion.NudgeHorizontal(this, ctaPill, 0.0);
        };
        btn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(fault.FocusModId))
                FocusMod(fault.FocusModId, fault.OpenUpdatePreview);
            else if (!string.IsNullOrEmpty(fault.NavigateTab))
                SwitchTab(fault.NavigateTab);
            else
                RequestRescanAll?.Invoke();
        };
        return btn;
    }

    void UpdateNavTabStyles(bool animateIndicator = false)
    {
        foreach (var (id, (btn, label, underline)) in navTabs)
        {
            bool active = id == activeTab;
            label.Foreground = active ? NeonPalette.InkPrimary : NeonPalette.InkSecondary;
            label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            underline.Background = Brushes.Transparent;
            AutomationProperties.SetItemStatus(btn, active ? "当前页面" : "");
        }
        PositionNavIndicator(animateIndicator);
    }

    void PositionNavIndicator(bool animate)
    {
        if (!navTabs.TryGetValue(activeTab, out var entry)) return;
        var tabBtn = entry.Tab;
        if (tabBtn.ActualWidth <= 1.0 || !IsLoaded)
        {
            Dispatcher.BeginInvoke(new Action(() => PositionNavIndicator(animate: false)), DispatcherPriority.Loaded);
            return;
        }
        try
        {
            var pt = tabBtn.TranslatePoint(new Point(0, 0), navHostGrid);
            const double pad = 10.0;
            double width = Math.Max(14.0, tabBtn.ActualWidth - pad * 2.0);
            double x = pt.X + pad;
            bool wasHidden = navActiveIndicator.Visibility != Visibility.Visible;
            navActiveIndicator.Visibility = Visibility.Visible;
            NeonMotion.PlaceSlidingIndicator(this, navActiveIndicator, x, width, animate && !wasHidden);
        }
        catch { }
    }

    void UpdateModeSwitchStyles(bool animateThumb = false)
    {
        void StyleSeg(Button btn, bool active)
        {
            btn.Background = Brushes.Transparent;
            btn.Foreground = active ? NeonPalette.InkPrimary : NeonPalette.InkSecondary;
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            btn.BorderBrush = Brushes.Transparent;
            btn.BorderThickness = new Thickness(0);
        }
        StyleSeg(modeListButton, workbenchMode == "list");
        StyleSeg(modeDepsButton, workbenchMode == "deps");
        PositionModeSegThumb(animateThumb);

        bool listMode = workbenchMode == "list";
        string hint = listMode ? "搜索 MOD 名称、ID、作者或依赖" : "搜索依赖双方的名称或 ID";
        searchPlaceholder.Text = hint + "…";
        AutomationProperties.SetName(searchBox, hint);
        filterPillsPanel.Visibility = sortCombo.Visibility = listMode ? Visibility.Visible : Visibility.Collapsed;
    }

    void PositionModeSegThumb(bool animate)
    {
        var target = workbenchMode == "deps" ? modeDepsButton : modeListButton;
        if (target.ActualWidth <= 1.0 || !IsLoaded)
        {
            Dispatcher.BeginInvoke(new Action(() => PositionModeSegThumb(animate: false)), DispatcherPriority.Loaded);
            return;
        }
        try
        {
            var pt = target.TranslatePoint(new Point(0, 0), modeSegHost);
            bool wasHidden = modeSegThumb.Visibility != Visibility.Visible;
            modeSegThumb.Visibility = Visibility.Visible;
            NeonMotion.PlaceSlidingIndicator(this, modeSegThumb, pt.X, target.ActualWidth, animate && !wasHidden);
        }
        catch { }
    }

    void UpdateFilterPillVisuals()
    {
        var counts = currentSnapshot?.FilterCounts;
        foreach (var (id, (btn, label, countTb)) in filterPillControls)
        {
            int n = counts != null && counts.TryGetValue(id, out var v) ? v : 0;
            bool changed = lastFilterCounts.TryGetValue(id, out int prev) && prev != n;
            lastFilterCounts[id] = n;
            countTb.Text = n.ToString();
            if (changed)
                NeonMotion.PulseBadge(this, countTb);

            bool isActive = id == activeFilter;
            bool isIssueHighlight = id == "issues" && n > 0;

            if (isIssueHighlight && isActive)
            {
                btn.Background = NeonPalette.Cinnabar;
                btn.BorderBrush = NeonPalette.Cinnabar;
                label.Foreground = Brushes.White;
                countTb.Foreground = Brushes.White;
            }
            else if (isIssueHighlight)
            {
                btn.Background = NeonPalette.CinnabarBg;
                btn.BorderBrush = NeonPalette.CinnabarBorder;
                label.Foreground = NeonPalette.Cinnabar;
                countTb.Foreground = NeonPalette.Cinnabar;
            }
            else if (isActive)
            {
                btn.Background = NeonPalette.InkPrimary;
                btn.BorderBrush = NeonPalette.InkPrimary;
                label.Foreground = NeonPalette.BgSurface;
                countTb.Foreground = NeonPalette.BgSurface;
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.BorderBrush = Brushes.Transparent;
                label.Foreground = NeonPalette.InkSecondary;
                countTb.Foreground = NeonPalette.InkSecondary;
            }
            AutomationProperties.SetName(btn, $"{label.Text} {n}");
        }
    }

    void OnSearchTextChanged()
    {
        bool hasQuery = searchBox.Text.Length > 0;
        searchPlaceholder.Visibility = hasQuery ? Visibility.Collapsed : Visibility.Visible;
        searchClearButton.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        searchKbdHint.Visibility = hasQuery ? Visibility.Collapsed : Visibility.Visible;
        depPage = 1;
        RenderWorkbenchContent(preserveScroll: workbenchMode == "list");
    }

    public void SwitchTab(string nextTab)
    {
        if (activeTab == nextTab && (nextTab == "workbench" || secondaryTabHost.Content != null))
            return;

        // If Inspector was mid-close when switching away, finalize collapse immediately so no stale exit callback fires later
        if (inspectorClosing)
            FinishInspectorCollapseImmediate(preserveScroll: true);

        // Cancel any previous in-flight tab fade cleanly before switching
        NeonMotion.ClearAnimations(workbenchStage);
        NeonMotion.ClearAnimations(secondaryTabHost);

        int myTabGen = ++tabAnimGeneration;
        activeTab = nextTab;
        UpdateNavTabStyles(animateIndicator: true);
        if (nextTab == "workbench")
        {
            secondaryTabHost.Visibility = Visibility.Collapsed;
            workbenchStage.Visibility = Visibility.Visible;
            NeonMotion.FadeInTabContent(this, workbenchStage, () => myTabGen == tabAnimGeneration);
        }
        else
        {
            workbenchStage.Visibility = Visibility.Collapsed;
            secondaryTabHost.Visibility = Visibility.Visible;
            if (currentSnapshot != null)
                RenderSecondaryPage(nextTab, currentSnapshot, preserveScroll: true);
            NeonMotion.FadeInTabContent(this, secondaryTabHost, () => myTabGen == tabAnimGeneration);
        }
    }

    internal void SetWorkshopFeedback(string? text, NeonTone tone = NeonTone.Sage)
    {
        workshopInlineFeedbackText = text;
        workshopInlineFeedbackTone = tone;
        if (activeTab == "acquire" && currentSnapshot != null)
            RenderSecondaryPage("acquire", currentSnapshot, preserveScroll: true);
    }

    (ScrollViewer Scroll, StackPanel Container) EnsureSecondaryTabSurface(string tabId)
    {
        if (!secondaryScrolls.TryGetValue(tabId, out var sv) || !secondaryContainers.TryGetValue(tabId, out var container))
        {
            container = new StackPanel
            {
                MaxWidth = 1120,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var pad = new Border
            {
                Padding = new Thickness(20, 16, 20, 36),
                Child = container
            };
            sv = new ScrollViewer
            {
                Content = pad,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Focusable = false
            };
            NeonMotion.AttachSmoothWheelScroll(sv, this);
            secondaryScrolls[tabId] = sv;
            secondaryContainers[tabId] = container;
        }
        return (sv, container);
    }

    void RenderSecondaryPage(string tabId, NeonWorkbenchSnapshot s, bool preserveScroll, double? explicitSavedOffset = null)
    {
        var (sv, container) = EnsureSecondaryTabSurface(tabId);
        double targetOffset = explicitSavedOffset ?? (preserveScroll ? sv.VerticalOffset : 0);

        if (!ReferenceEquals(secondaryTabHost.Content, sv))
            secondaryTabHost.Content = sv;

        // Detach persistent Workshop input cleanly before rebuilding Acquire visual tree
        if (tabId == "acquire" && workshopInputHost?.Parent is Panel prevParent)
            prevParent.Children.Remove(workshopInputHost);

        container.Children.Clear();
        switch (tabId)
        {
            case "patches":
                BuildPatchesPage(container, s);
                break;
            case "acquire":
                BuildAcquirePage(container, s);
                break;
            case "coop":
                BuildCoopPage(container, s);
                break;
            case "recovery":
                BuildRecoveryPage(container, s);
                break;
            default:
                BuildPatchesPage(container, s);
                break;
        }

        if (preserveScroll && targetOffset > 0)
        {
            sv.ScrollToVerticalOffset(targetOffset);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                sv.ScrollToVerticalOffset(Math.Min(targetOffset, sv.ScrollableHeight));
            }), DispatcherPriority.Loaded);
        }
        else if (!preserveScroll)
        {
            sv.ScrollToTop();
        }
    }

    // ========================================================================
    // WORKBENCH CONTENT: MOD LIST & DEPENDENCY LENS
    // ========================================================================

    void RenderWorkbenchContent(
        bool preserveScroll,
        double? explicitSavedOffset = null,
        bool animateSettle = false,
        bool cascadeReveal = false)
    {
        double targetOffset = explicitSavedOffset ?? (preserveScroll ? deskScrollViewer.VerticalOffset : 0);
        int scrollRequest = ++neonListScrollRequest;
        if (currentSnapshot == null || currentSnapshot.GameRoot == null)
            ResetNeonListChildren();
        else if (!currentSnapshot.HasInventory || workbenchMode == "deps")
            ResetNeonListChildren();

        if (currentSnapshot == null || currentSnapshot.GameRoot == null)
        {
            listSummaryMeta.Text = "尚未读取清单";
            deskContentStack.Children.Add(BuildEmptyState(
                "尚未选择游戏目录",
                "点击右上角或下方按钮定位《杀戮尖塔 2》安装目录。",
                "选择游戏目录",
                () => RequestChooseGameRoot?.Invoke()));
            SetBatchDockVisible(false);
            return;
        }

        if (!currentSnapshot.HasInventory)
        {
            if (currentSnapshot.IsScanning)
            {
                string stageText = currentSnapshot.ScanStageText ?? "正在读取 mods 目录、依赖声明与补丁状态，请稍候…";
                listSummaryMeta.Text = currentSnapshot.ScanStageText ?? "正在扫描本地清单…";
                deskContentStack.Children.Add(BuildEmptyState(
                    "正在扫描本地 MOD 清单…",
                    stageText,
                    "正在扫描…",
                    onCta: null,
                    actionDisabled: true,
                    showProgress: true));
            }
            else
            {
                listSummaryMeta.Text = "尚未读取清单";
                deskContentStack.Children.Add(BuildEmptyState(
                    "尚未读取本地 MOD 清单",
                    "点击下方按钮读取当前游戏目录的 MOD 清单与依赖关系。",
                    "立即扫描",
                    () => RequestRescanAll?.Invoke()));
            }
            SetBatchDockVisible(false);
            return;
        }

        var filtered = NeonWorkbenchBuilder.FilterAndSort(
            currentSnapshot.AllRows, activeFilter, searchBox.Text, sortBy);

        if (workbenchMode == "deps")
        {
            RenderDependencyLens(currentSnapshot);
            SetBatchDockVisible(false);
        }
        else
        {
            listSummaryMeta.Text = $"显示 {filtered.Length} / {currentSnapshot.AllRows.Length} 项";
            RenderModList(filtered, cascadeReveal: cascadeReveal);
            UpdateBatchDock(filtered);
        }

        if (!cascadeReveal && animateSettle)
            NeonMotion.PlayFilterSettle(this, deskContentStack);
        else if (!cascadeReveal)
            NeonMotion.ClearAnimations(deskContentStack);

        if (preserveScroll)
        {
            deskScrollViewer.ScrollToVerticalOffset(targetOffset);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (scrollRequest != neonListScrollRequest) return;
                double clamped = Math.Min(targetOffset, deskScrollViewer.ScrollableHeight);
                deskScrollViewer.ScrollToVerticalOffset(clamped);
            }), DispatcherPriority.Loaded);
        }
        else
        {
            deskScrollViewer.ScrollToTop();
        }
    }

    void RenderModList(NeonWorkbenchRow[] filtered, bool cascadeReveal = false)
    {
        if (currentSnapshot!.AllRows.Length == 0 || filtered.Length == 0)
        {
            ResetNeonListChildren();
            deskContentStack.Children.Add(currentSnapshot.AllRows.Length == 0
                ? BuildEmptyState("清单中暂无普通 MOD", "可以前往「获取与更新」导入本地 MOD 压缩包。",
                    "前往获取与更新", () => SwitchTab("acquire"))
                : BuildEmptyState("没有匹配的 MOD",
                    $"试试更换关键词，或清除筛选条件查看全部 {currentSnapshot.AllRows.Length} 项。",
                    "重置搜索与筛选", ResetSearchAndFilters));
            return;
        }
        ReconcileNeonListPage(filtered, cascadeReveal);
    }

    bool IsModSelected(string modId) =>
        selectedModId != null && selectedModId.Equals(modId, StringComparison.OrdinalIgnoreCase);

    static Brush ComputeRowBaseBg(bool isSelected, bool hasDanger) =>
        isSelected ? NeonPalette.BgSelected
        : hasDanger ? NeonPalette.RowDangerBg
        : NeonPalette.BgSurface;

    static Brush ComputeRowHoverBg(bool isSelected, bool hasDanger) =>
        isSelected ? NeonPalette.BgSelected
        : hasDanger ? NeonPalette.RowDangerHover
        : NeonPalette.BgHover;

    static Brush ComputeRowAccentBrush(bool isSelected, bool hasDanger, bool hasWarn) =>
        hasDanger ? NeonPalette.Cinnabar
        : hasWarn ? NeonPalette.Amber
        : isSelected ? NeonPalette.Sage
        : Brushes.Transparent;

    void UpdateRowSelectionVisualsOnly()
    {
        foreach (var (id, info) in visibleRowVisuals)
        {
            bool sel = IsModSelected(id);
            var targetBg = info.Outer.IsMouseOver
                ? ComputeRowHoverBg(sel, info.HasDanger)
                : ComputeRowBaseBg(sel, info.HasDanger);
            NeonMotion.AnimateRowBackground(this, info.Outer, targetBg);
            info.LeftBar.Background = ComputeRowAccentBrush(sel, info.HasDanger, info.HasWarn);
        }
    }

    Border BuildModRowElement(NeonWorkbenchRow row)
    {
        neonRowBuildCount++;
        bool isSelected = IsModSelected(row.Mod.Id);
        bool isHighlighted = highlightedModId != null && row.Mod.Id.Equals(highlightedModId, StringComparison.OrdinalIgnoreCase);
        bool isChecked = checkedModFolders.Contains(row.Mod.Folder);

        Brush baseBg = ComputeRowBaseBg(isSelected, row.HasDanger);
        Brush accentBarBrush = ComputeRowAccentBrush(isSelected, row.HasDanger, row.HasWarn);

        var outer = new Border
        {
            Tag = row.Mod.Id,
            Background = baseBg,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };
        AutomationProperties.SetName(outer, $"{row.Mod.Name} ({row.Mod.Id}) v{row.Mod.Version}");

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) }); // Left accent
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftBar = new Border { Background = accentBarBrush };
        rowGrid.Children.Add(leftBar);

        var contentGrid = new Grid
        {
            Margin = new Thickness(11, 10, 14, 10),
            Opacity = row.Mod.Enabled ? 1.0 : 0.72
        };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // CheckBox
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Category Emblem
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Identity
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Signals (Silence = Healthy!)
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Fixed Actions Column

        // 1. CheckBox
        var check = new CheckBox
        {
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0)
        };
        if (TryFindResource("NeonCheckBox") is Style cbStyle) check.Style = cbStyle;
        AutomationProperties.SetName(check, "勾选 " + row.Mod.Name);
        check.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
        check.Click += (_, e) =>
        {
            e.Handled = true;
            if (check.IsChecked == true) checkedModFolders.Add(row.Mod.Folder);
            else checkedModFolders.Remove(row.Mod.Folder);
            if (currentSnapshot != null)
            {
                var visible = NeonWorkbenchBuilder.FilterAndSort(currentSnapshot.AllRows, activeFilter, searchBox.Text, sortBy);
                UpdateBatchDock(visible);
            }
        };
        contentGrid.Children.Add(check);

        // 2. Category Emblem
        NeonTone emblemTone = row.HasDanger ? NeonTone.Cinnabar : row.HasWarn ? NeonTone.Amber : NeonTone.Neutral;
        var emblem = NeonWidgets.CreateCategoryEmblem(row.Category, emblemTone);
        emblem.Margin = new Thickness(0, 0, 11, 0);
        emblem.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(emblem, 1);
        contentGrid.Children.Add(emblem);

        // 3. Identity Column (Name + ID inline; Version · Author · Source · Disabled note)
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nameTb = NeonWidgets.Text(row.Mod.Name, 13.5, row.Mod.Enabled ? NeonPalette.InkPrimary : NeonPalette.InkSecondary, FontWeights.SemiBold, ellipsis: true);
        nameTb.MaxWidth = 340;
        nameTb.ToolTip = row.Mod.Name;
        nameTb.VerticalAlignment = VerticalAlignment.Bottom;
        titleGrid.Children.Add(nameTb);

        var idTb = NeonWidgets.Text(row.Mod.Id, 11, NeonPalette.InkMuted, mono: true, ellipsis: true);
        idTb.Margin = new Thickness(8, 0, 0, 1);
        idTb.MaxWidth = 190;
        idTb.HorizontalAlignment = HorizontalAlignment.Left;
        idTb.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(idTb, 1);
        titleGrid.Children.Add(idTb);
        identity.Children.Add(titleGrid);

        var subRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        subRow.Children.Add(NeonWidgets.Text("v" + NeonVersionDiff.StripPrefix(row.Mod.Version), 11.5, NeonPalette.InkSecondary, mono: true));
        AddSubSep(subRow);
        subRow.Children.Add(NeonWidgets.Text(string.IsNullOrWhiteSpace(row.Mod.Author) ? "未知作者" : row.Mod.Author, 12, NeonPalette.InkMuted));
        AddSubSep(subRow);
        subRow.Children.Add(NeonWidgets.Text(row.SourceMuted, 12, NeonPalette.InkMuted));
        if (!row.Mod.Enabled)
        {
            AddSubSep(subRow);
            subRow.Children.Add(NeonWidgets.Text("已移入停用保留区", 12, NeonPalette.InkSecondary));
        }
        identity.Children.Add(subRow);

        Grid.SetColumn(identity, 2);
        contentGrid.Children.Add(identity);

        // 4. Signal Cluster (STRICTLY earned signals only — Silence = Healthy!)
        var signals = BuildRowSignals(row);
        if (signals.Children.Count > 0)
        {
            signals.Margin = new Thickness(0, 0, 10, 0);
            signals.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(signals, 3);
            contentGrid.Children.Add(signals);
        }

        // 5. Fixed Action Column (Inline Lock Reason Pill + Jelly ToggleSwitch + Details Ghost Button)
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        bool gameRunning = currentSnapshot?.GameRunning == true;
        var inlineStatus = GetInlineToggleStatus(row.Mod.Id);
        bool isInlinePending = inlineStatus?.Phase is NeonInlineTogglePhase.Applying or NeonInlineTogglePhase.AppliedScanPending;

        if (row.IsLockedByDependents && row.ToggleBlockReason != null && !isInlinePending)
        {
            string lockLabel = FormatDependentLockShortLabel(row);
            var lockChip = BuildInlineLockPill(lockLabel, row.ToggleBlockReason, () => SelectModAndShowDependents(row.Mod.Id));
            lockChip.Margin = new Thickness(0, 0, 7, 0);
            actions.Children.Add(lockChip);
        }

        bool isVisualLocked = row.IsLockedByDependents || gameRunning;
        bool? prevModOn = pendingModFlipPrevState.Remove(row.Mod.Id, out bool prevVal) ? prevVal : null;
        var toggle = NeonWidgets.CreateToggleSwitch(
            isOn: row.Mod.Enabled,
            isLocked: isVisualLocked,
            onClick: () =>
            {
                if (gameRunning)
                {
                    SetInlineToggleStatus(
                        row.Mod.Id,
                        new NeonInlineToggleStatus(
                            row.Mod.Id,
                            row.Mod.Name,
                            !row.Mod.Enabled,
                            NeonInlineTogglePhase.Failed,
                            "退出游戏后可启停",
                            "退出游戏后可启停（检测到 SlayTheSpire2.exe 正在运行）。"),
                        rerender: true);
                    ShowToast("退出游戏后可启停", NeonTone.Amber);
                    return;
                }
                if (row.IsLockedByDependents)
                {
                    SelectModAndShowDependents(row.Mod.Id);
                    return;
                }
                RequestDirectToggleMod?.Invoke(row.Mod, selectedModId);
            },
            toolTip: isInlinePending
                ? inlineStatus!.Message
                : gameRunning
                    ? "退出游戏后可启停"
                    : row.IsLockedByDependents
                        ? $"{FormatDependentLockShortLabel(row)}，点击查看依赖列表"
                        : row.ToggleBlockReason ?? (row.Mod.Enabled ? "停用并移入保留区（不删除文件）" : "核验指纹并恢复启用"),
            automationName: (row.Mod.Enabled ? "停用 " : "启用 ") + row.Mod.Name,
            previousIsOn: prevModOn,
            animHost: this,
            isPending: isInlinePending);
        toggle.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(toggle);

        var detailBtn = new Button
        {
            Content = "详情",
            Height = 24,
            Padding = new Thickness(8, 0, 8, 0),
            FontFamily = NeonPalette.SansFont,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = NeonPalette.InkSecondary,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Template = CreateRoundedButtonTemplate(6, NeonPalette.BgWell, Brushes.Transparent)
        };
        AutomationProperties.SetName(detailBtn, "查看详情 " + row.Mod.Name);
        detailBtn.Click += (_, e) =>
        {
            e.Handled = true;
            SelectMod(row.Mod.Id);
        };
        actions.Children.Add(detailBtn);

        Grid.SetColumn(actions, 4);
        contentGrid.Children.Add(actions);

        Grid.SetColumn(contentGrid, 1);
        rowGrid.Children.Add(contentGrid);
        outer.Child = rowGrid;

        // Hover & Row Click (dynamic selection lookup so in-place selection swap never rebuilds rows!)
        visibleRowVisuals[row.Mod.Id] = (outer, leftBar, row.HasDanger, row.HasWarn, check);
        outer.MouseEnter += (_, _) =>
        {
            detailBtn.Foreground = NeonPalette.InkPrimary;
            NeonMotion.AnimateRowBackground(this, outer, ComputeRowHoverBg(IsModSelected(row.Mod.Id), row.HasDanger));
        };
        outer.MouseLeave += (_, _) =>
        {
            detailBtn.Foreground = NeonPalette.InkSecondary;
            NeonMotion.NudgeHorizontal(this, contentGrid, 0.0, 140);
            NeonMotion.AnimateRowBackground(this, outer, ComputeRowBaseBg(IsModSelected(row.Mod.Id), row.HasDanger));
        };
        outer.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            NeonMotion.AnimateRowBackground(this, outer, NeonPalette.BgWell, 55);
            NeonMotion.NudgeHorizontal(this, contentGrid, 2.0, 55);
        };
        outer.MouseLeftButtonUp += (_, e) =>
        {
            NeonMotion.NudgeHorizontal(this, contentGrid, 0.0, 140);
            if (e.Handled) return;
            NeonMotion.AnimateRowBackground(this, outer, outer.IsMouseOver
                ? ComputeRowHoverBg(IsModSelected(row.Mod.Id), row.HasDanger)
                : ComputeRowBaseBg(IsModSelected(row.Mod.Id), row.HasDanger), 140);
            // Toggle selection when clicking the row body
            if (IsModSelected(row.Mod.Id))
                CloseInspector();
            else
                SelectMod(row.Mod.Id);
        };

        if (isHighlighted)
        {
            highlightedModId = null;
            NeonMotion.FlashRowHighlight(
                this,
                outer,
                NeonPalette.RowFlashBg.Color,
                () => outer.IsMouseOver
                    ? ComputeRowHoverBg(IsModSelected(row.Mod.Id), row.HasDanger)
                    : ComputeRowBaseBg(IsModSelected(row.Mod.Id), row.HasDanger));
        }

        return outer;
    }

    StackPanel BuildRowSignals(NeonWorkbenchRow row)
    {
        var cluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        void AddSignal(FrameworkElement el)
        {
            if (cluster.Children.Count > 0) el.Margin = new Thickness(6, 0, 0, 0);
            cluster.Children.Add(el);
        }

        var inlineStatus = GetInlineToggleStatus(row.Mod.Id);
        if (inlineStatus is { Phase: NeonInlineTogglePhase.Applying or NeonInlineTogglePhase.AppliedScanPending })
        {
            string pendingLabel = inlineStatus.Phase == NeonInlineTogglePhase.AppliedScanPending
                ? "正在核验依赖…"
                : inlineStatus.TargetEnable ? "正在启用…" : "正在停用…";
            var pendingBadge = NeonWidgets.CreateBadge(
                pendingLabel,
                NeonTone.Amber,
                toolTip: inlineStatus.Message,
                automationName: row.Mod.Id + " " + pendingLabel);
            NeonMotion.StartSubtlePulse(this, pendingBadge);
            AddSignal(pendingBadge);
        }
        else if (inlineStatus is { Phase: NeonInlineTogglePhase.AppliedScanFailed })
        {
            string halfLabel = inlineStatus.TargetEnable ? "已恢复启用 · 依赖待核验 → 重扫" : "已移入停用区 · 依赖待核验 → 重扫";
            AddSignal(NeonWidgets.CreateBadge(
                halfLabel,
                NeonTone.Amber,
                onClick: () => RequestRescanAll?.Invoke(),
                toolTip: inlineStatus.Detail ?? inlineStatus.Message,
                automationName: "重新扫描 " + row.Mod.Id));
        }
        else if (inlineStatus is { Phase: NeonInlineTogglePhase.Failed })
        {
            bool isRunningBlock = inlineStatus.Message.Contains("退出游戏");
            AddSignal(NeonWidgets.CreateBadge(
                isRunningBlock ? "退出游戏后可启停" : (inlineStatus.TargetEnable ? "恢复未完成 · 查看原因" : "停用未完成 · 查看原因"),
                isRunningBlock ? NeonTone.Amber : NeonTone.Cinnabar,
                onClick: isRunningBlock ? () => OpenToolsDrawer() : () => SelectMod(row.Mod.Id),
                toolTip: inlineStatus.Detail ?? inlineStatus.Message,
                automationName: isRunningBlock ? "退出游戏后可启停 " + row.Mod.Id : "查看失败原因 " + row.Mod.Id));
        }
        else if (row.CausingDisabledBlock != null)
        {
            bool canQuickEnable = row.ToggleBlockReason == null;
            AddSignal(NeonWidgets.CreateBadge(
                $"已停用 · 阻断 {row.InboundActiveBroken.Length} 项 → 启用",
                NeonTone.Cinnabar,
                onClick: canQuickEnable ? () => RequestDirectToggleMod?.Invoke(row.Mod, selectedModId) : () => SelectMod(row.Mod.Id),
                toolTip: canQuickEnable
                    ? "点击直接核验指纹并恢复启用此前置库"
                    : row.ToggleBlockReason ?? "点击查看因果详情",
                automationName: "快速启用前置 " + row.Mod.Id));
        }
        else if (row.OwnDisabledPrereq != null)
        {
            AddSignal(NeonWidgets.CreateBadge(
                $"前置已停用 · {row.OwnDisabledPrereq.DependencyId}",
                NeonTone.Cinnabar,
                onClick: () => SelectMod(row.Mod.Id),
                toolTip: row.OwnDisabledPrereq.Message));
        }
        else if (row.OwnMissing != null)
        {
            AddSignal(NeonWidgets.CreateBadge(
                $"缺前置 · {row.OwnMissing.DependencyId}",
                NeonTone.Cinnabar,
                onClick: () => SelectMod(row.Mod.Id),
                toolTip: row.OwnMissing.Message));
        }
        else if (row.OwnOtherIssue != null)
        {
            string label = row.OwnOtherIssue.IsCycle
                ? "循环依赖"
                : ModDependencyGraphPresentation.Status(row.OwnOtherIssue.Status);
            AddSignal(NeonWidgets.CreateBadge(
                $"{label} · {row.OwnOtherIssue.DependencyId}",
                NeonTone.Cinnabar,
                onClick: () => SelectMod(row.Mod.Id),
                toolTip: row.OwnOtherIssue.Message));
        }

        if (row.CausingVersionMismatch != null)
        {
            AddSignal(NeonWidgets.CreateVersionDiffPill(
                row.CausingVersionMismatch.ActualVersion,
                row.CausingVersionMismatch.RequiredVersion,
                row.CausingVersionMismatch.Operator,
                toolTip: $"版本要求来源：{row.CausingVersionMismatch.SourceNote}"));
        }
        else if (row.OwnVersionMismatch != null)
        {
            AddSignal(NeonWidgets.CreateVersionDiffPill(
                row.OwnVersionMismatch.ActualVersion,
                row.OwnVersionMismatch.RequiredVersion,
                row.OwnVersionMismatch.Operator,
                prefix: row.OwnVersionMismatch.PrereqId,
                toolTip: $"版本要求来源：{row.OwnVersionMismatch.SourceNote}"));
        }

        if (row.HasUpdate)
        {
            string label = row.GitHubResult?.Status == "有更新" && row.GitHubResult.LatestVersion.Length > 0
                ? "↑ v" + NeonVersionDiff.StripPrefix(row.GitHubResult.LatestVersion)
                : "↑ 工坊新版本";
            AddSignal(NeonWidgets.CreateBadge(
                label,
                NeonTone.Amber,
                onClick: () => RequestPreviewUpdateMod?.Invoke(row.Mod),
                toolTip: "点击预览备份并更新"));
        }
        else if (row.IsUnbound && row.CandidateSource != null)
        {
            AddSignal(NeonWidgets.CreateBadge(
                "+ 确认来源",
                NeonTone.Amber,
                dashed: true,
                onClick: () => SelectMod(row.Mod.Id),
                toolTip: "发现候选来源，点击在详情页人工核对关联"));
        }

        return cluster;
    }

    internal static string FormatDependentLockShortLabel(NeonWorkbenchRow row)
    {
        static string StripBasePrefix(string raw)
        {
            string s = NeonWidgets.ShortDisplayName(raw);
            int cut = s.IndexOfAny(['：', ':', '（', '(', '/', ' ']);
            if (cut > 0) s = s[..cut].Trim();
            return s;
        }

        static string CompactName(string raw, string selfBase)
        {
            string s = NeonWidgets.ShortDisplayName(raw);
            string sBase = StripBasePrefix(raw);
            if (!string.IsNullOrEmpty(selfBase) &&
                (s.Equals(selfBase, StringComparison.OrdinalIgnoreCase) ||
                 sBase.Equals(selfBase, StringComparison.OrdinalIgnoreCase)))
            {
                int dot = raw.IndexOf('·');
                if (dot >= 0 && dot + 1 < raw.Length)
                    s = raw[(dot + 1)..].Trim();
            }
            int cut = s.IndexOfAny(['：', ':', '（', '(', '/', ' ']);
            if (cut > 0) s = s[..cut].Trim();
            if (s.Length > 4) s = s[..4];
            return s;
        }

        string selfBase = StripBasePrefix(row.Mod.Name);
        var activeEdges = row.InboundEdges
            .Where(e => e.Dependent.Enabled && !e.Dependent.Id.Equals(row.Mod.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var activeNormalMods = activeEdges
            .Where(e => !ModPresentation.IsIndependentPatch(e.Dependent))
            .Select(e => CompactName(e.Dependent.Name, selfBase))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n.Length)
            .ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        int totalActive = activeEdges
            .Select(e => e.Dependent.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (activeNormalMods.Length > 0)
            return totalActive == 1 ? $"{activeNormalMods[0]}正在使用" : $"{activeNormalMods[0]}等正在使用";

        if (totalActive > 0)
            return totalActive == 1 ? "1 项补丁正在使用" : $"{totalActive} 项补丁正在使用";

        if (!string.IsNullOrWhiteSpace(row.ToggleBlockReason))
        {
            int idx = row.ToggleBlockReason.IndexOf('：');
            if (idx >= 0 && idx + 1 < row.ToggleBlockReason.Length)
            {
                string[] parts = row.ToggleBlockReason[(idx + 1)..]
                    .Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 1) return $"{CompactName(parts[0], selfBase)}正在使用";
                if (parts.Length > 1) return $"{CompactName(parts[0], selfBase)}等正在使用";
            }
        }
        return "依赖项正在使用";
    }

    Button BuildInlineLockPill(string label, string toolTip, Action onClick)
    {
        var inner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var lockIcon = NeonWidgets.CreateLockIcon(toolTip);
        if (lockIcon is FrameworkElement lf)
        {
            lf.Margin = new Thickness(0, 0, 4, 0);
            lf.ToolTip = null;
        }
        inner.Children.Add(lockIcon);
        var tb = NeonWidgets.Text(label, 11, NeonPalette.Amber, FontWeights.Medium, ellipsis: true);
        tb.MaxWidth = 128;
        tb.VerticalAlignment = VerticalAlignment.Center;
        inner.Children.Add(tb);

        var btn = new Button
        {
            Content = inner,
            Height = 22,
            Padding = new Thickness(6, 0, 7, 0),
            Background = NeonPalette.AmberBg,
            BorderBrush = NeonPalette.AmberBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{toolTip}\n点击查看完整依赖列表",
            Template = CreateRoundedButtonTemplate(4, NeonPalette.AmberHoverBg, NeonPalette.AmberBorder)
        };
        AutomationProperties.SetName(btn, label + "，查看依赖列表");
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    public void SelectModAndShowDependents(string modId)
    {
        SelectMod(modId);
        // Ensure causal chain ("依赖它的模组与补丁") is cleanly in view when jumping from a dependency lock pill
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (selectedModId != null && selectedModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                inspectorScroll.ScrollToVerticalOffset(Math.Min(110, inspectorScroll.ScrollableHeight));
        }), DispatcherPriority.Loaded);
    }

    static void AddSubSep(Panel panel)
    {
        var sep = NeonWidgets.Text("·", 12, NeonPalette.InkMuted);
        sep.Opacity = 0.45;
        sep.Margin = new Thickness(7, 0, 7, 0);
        panel.Children.Add(sep);
    }

    // ========================================================================
    // DEPENDENCY LENS (Real ModDependencyGraph + ModDependencyPaging)
    // ========================================================================

    void RenderDependencyLens(NeonWorkbenchSnapshot s)
    {
        var graph = s.DependencyGraph;
        var allEdges = graph?.Edges ?? [];
        string q = searchBox.Text.Trim();

        IEnumerable<ModDependencyEdge> edges = allEdges;
        if (depOnlyIssues) edges = edges.Where(e => e.IsIssue);
        if (q.Length > 0)
        {
            edges = edges.Where(e =>
                $"{e.Dependent.Name} {e.Dependent.Id} {e.DependencyName} {e.DependencyId}"
                    .Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = edges
            .OrderByDescending(e => e.IsActiveIssue)
            .ThenByDescending(e => e.IsIssue)
            .ThenBy(e => e.Dependent.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        listSummaryMeta.Text = $"显示 {ordered.Length} / {allEdges.Length} 条依赖关系";

        int totalPages = ModDependencyPaging.Count(ordered.Length);
        depPage = ModDependencyPaging.Clamp(depPage, ordered.Length);
        var slice = ModDependencyPaging.Items(ordered, depPage);

        // Lens info bar
        var bar = new Border
        {
            Background = NeonPalette.BgGround,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 9, 14, 9)
        };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var barLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var filterTrack = new Border
        {
            Background = NeonPalette.BgWell,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 10, 0)
        };
        var filterHost = new Grid();
        var depThumb = new Border
        {
            Width = 0,
            Height = 24,
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Visibility = Visibility.Collapsed
        };
        var depThumbCanvas = new Canvas { IsHitTestVisible = false, Height = 24 };
        depThumbCanvas.Children.Add(depThumb);
        var filterStack = new StackPanel { Orientation = Orientation.Horizontal };
        var depIssuesBtn = BuildDepFilterButton($"仅看异常 ({allEdges.Count(e => e.IsIssue)})", true);
        var depAllBtn = BuildDepFilterButton($"全部关系 ({allEdges.Length})", false);
        filterStack.Children.Add(depIssuesBtn);
        filterStack.Children.Add(depAllBtn);
        filterHost.Children.Add(depThumbCanvas);
        filterHost.Children.Add(filterStack);
        bool? prevDepOnly = pendingDepFilterPrevOnlyIssues;
        pendingDepFilterPrevOnlyIssues = null;
        filterHost.Loaded += (_, _) =>
        {
            var activeBtn = depOnlyIssues ? depIssuesBtn : depAllBtn;
            var prevBtn = prevDepOnly.HasValue && prevDepOnly.Value != depOnlyIssues
                ? (prevDepOnly.Value ? depIssuesBtn : depAllBtn)
                : null;
            if (activeBtn.ActualWidth <= 1.0) return;
            try
            {
                depThumb.Visibility = Visibility.Visible;
                if (prevBtn != null && prevBtn.ActualWidth > 1.0 && NeonMotion.CanAnimate(this))
                {
                    var fromPt = prevBtn.TranslatePoint(new Point(0, 0), filterHost);
                    NeonMotion.PlaceSlidingIndicator(this, depThumb, fromPt.X, prevBtn.ActualWidth, animate: false);
                    var toPt = activeBtn.TranslatePoint(new Point(0, 0), filterHost);
                    NeonMotion.PlaceSlidingIndicator(this, depThumb, toPt.X, activeBtn.ActualWidth, animate: true);
                }
                else
                {
                    var toPt = activeBtn.TranslatePoint(new Point(0, 0), filterHost);
                    NeonMotion.PlaceSlidingIndicator(this, depThumb, toPt.X, activeBtn.ActualWidth, animate: false);
                }
            }
            catch { }
        };
        filterTrack.Child = filterHost;
        barLeft.Children.Add(filterTrack);

        var dirTb = new TextBlock
        {
            FontFamily = NeonPalette.SansFont,
            FontSize = 12,
            Foreground = NeonPalette.InkSecondary,
            VerticalAlignment = VerticalAlignment.Center
        };
        dirTb.Inlines.Add(new Run("方向："));
        dirTb.Inlines.Add(new Run("前置模组 ──▶ 依赖方") { FontWeight = FontWeights.SemiBold, Foreground = NeonPalette.InkPrimary });
        dirTb.Inlines.Add(new Run($" · 循环依赖 {graph?.Cycles.Length ?? 0} 组"));
        barLeft.Children.Add(dirTb);
        barGrid.Children.Add(barLeft);

        var scopeTb = NeonWidgets.Text("只读分析 · 仅覆盖清单与已收录补丁声明", 11.5, NeonPalette.InkMuted);
        scopeTb.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(scopeTb, 1);
        barGrid.Children.Add(scopeTb);
        bar.Child = barGrid;
        deskContentStack.Children.Add(bar);

        if (slice.Length == 0)
        {
            Border empty;
            if (allEdges.Length == 0)
                empty = BuildEmptyState("清单中暂无依赖关系", "当前清单未记录模组之间的依赖声明。", null, null);
            else if (depOnlyIssues && !allEdges.Any(edge => edge.IsIssue))
                empty = BuildEmptyState("当前没有依赖异常关系", "可以切换到全部关系查看已记录的依赖。",
                    "查看全部关系", () => { depOnlyIssues = false; depPage = 1; RenderWorkbenchContent(false); });
            else
                empty = BuildEmptyState("没有匹配的依赖关系",
                    depOnlyIssues ? "当前只显示异常关系。可以更换关键词，或清空搜索查看所有异常。" : "可以更换关键词，或清空搜索查看全部关系。",
                    "清空搜索", ClearSearch);
            deskContentStack.Children.Add(empty);
            return;
        }

        foreach (var edge in slice)
            deskContentStack.Children.Add(BuildDepEdgeRow(edge, s));

        if (totalPages > 1)
        {
            var pager = new Border
            {
                Background = NeonPalette.BgGround,
                BorderBrush = NeonPalette.BorderSubtle,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 10, 14, 10)
            };
            var pg = new Grid();
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pg.Children.Add(NeonWidgets.Text($"第 {depPage} / {totalPages} 页（每页 {ModDependencyPaging.PageSize} 条，共 {ordered.Length} 条）", 12, NeonPalette.InkSecondary));

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            var prev = NeonWidgets.CreateStyledButton(this, "上一页", "NeonSecondaryButton", () => { depPage--; RenderWorkbenchContent(false); }, "xs");
            prev.IsEnabled = depPage > 1;
            prev.Margin = new Thickness(0, 0, 6, 0);
            var next = NeonWidgets.CreateStyledButton(this, "下一页", "NeonSecondaryButton", () => { depPage++; RenderWorkbenchContent(false); }, "xs");
            next.IsEnabled = depPage < totalPages;
            btns.Children.Add(prev);
            btns.Children.Add(next);
            Grid.SetColumn(btns, 1);
            pg.Children.Add(btns);
            pager.Child = pg;
            deskContentStack.Children.Add(pager);
        }
    }

    Button BuildDepFilterButton(string text, bool onlyIssues)
    {
        bool active = depOnlyIssues == onlyIssues;
        var btn = new Button
        {
            Content = text,
            Height = 24,
            Padding = new Thickness(9, 0, 9, 0),
            FontFamily = NeonPalette.SansFont,
            FontSize = 11.5,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = active ? NeonPalette.InkPrimary : NeonPalette.InkSecondary,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = CreateRoundedButtonTemplate(4, Brushes.Transparent, Brushes.Transparent)
        };
        btn.Click += (_, _) =>
        {
            if (depOnlyIssues == onlyIssues) return;
            pendingDepFilterPrevOnlyIssues = depOnlyIssues;
            depOnlyIssues = onlyIssues;
            depPage = 1;
            RenderWorkbenchContent(false);
        };
        return btn;
    }

    Border BuildDepEdgeRow(ModDependencyEdge edge, NeonWorkbenchSnapshot s)
    {
        var outer = new Border
        {
            Background = edge.IsActiveIssue ? NeonPalette.RowDangerBg : NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            SnapsToDevicePixels = true
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(edge.IsActiveIssue ? 3 : 0) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (edge.IsActiveIssue)
            grid.Children.Add(new Border { Background = NeonPalette.Cinnabar });

        var cols = new Grid { Margin = new Thickness(14, 11, 14, 11) };
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Prerequisite node
        var prePill = BuildDepNodePill(
            edge.DependencyName,
            $"{edge.DependencyId} · {(edge.Prerequisite == null ? "未安装" : edge.Prerequisite.Enabled ? "v" + NeonVersionDiff.StripPrefix(edge.Prerequisite.Version) : "已停用")}",
            bad: edge.IsIssue,
            onClick: edge.Prerequisite != null ? () => SelectMod(edge.Prerequisite.Id) : null);
        cols.Children.Add(prePill);

        // Wire
        var wireStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        var wireLine = new Grid { Height = 10 };
        if (edge.IsIssue)
        {
            wireLine.Children.Add(new Line
            {
                X1 = 0, Y1 = 5, X2 = 1, Y2 = 5,
                Stretch = Stretch.Fill,
                Stroke = NeonPalette.Cinnabar,
                StrokeThickness = 1.8,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Margin = new Thickness(0, 0, 8, 0)
            });
        }
        else
        {
            wireLine.Children.Add(new Border
            {
                Height = 2,
                Background = NeonPalette.SageBorder,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
        }
        var arrow = NeonWidgets.Text("▶", 9, edge.IsIssue ? NeonPalette.Cinnabar : NeonPalette.Sage);
        arrow.HorizontalAlignment = HorizontalAlignment.Right;
        arrow.VerticalAlignment = VerticalAlignment.Center;
        wireLine.Children.Add(arrow);
        wireStack.Children.Add(wireLine);

        if (edge.Status == ModDependencyStatus.VersionMismatch && edge.Prerequisite != null)
        {
            bool isMax = string.IsNullOrWhiteSpace(edge.MinimumVersion) && !string.IsNullOrWhiteSpace(edge.MaximumVersion);
            var diffPill = NeonWidgets.CreateVersionDiffPill(
                edge.Prerequisite.Version,
                isMax ? edge.MaximumVersion : edge.MinimumVersion,
                isMax ? "≤" : "≥");
            diffPill.HorizontalAlignment = HorizontalAlignment.Center;
            diffPill.Margin = new Thickness(0, 3, 0, 0);
            wireStack.Children.Add(diffPill);
        }
        else
        {
            string label = edge.Status switch
            {
                ModDependencyStatus.DisabledPrerequisite => "前置已停用",
                ModDependencyStatus.Missing => "未安装前置",
                ModDependencyStatus.Satisfied when edge.MinimumVersion.Length > 0 => $"要求 ≥ v{NeonVersionDiff.StripPrefix(edge.MinimumVersion)} · 满足",
                ModDependencyStatus.Satisfied => "已启用 · 满足",
                _ => ModDependencyGraphPresentation.Status(edge.Status)
            };
            var lbl = NeonWidgets.Text(label, 11.5, edge.IsIssue ? NeonPalette.Cinnabar : NeonPalette.InkSecondary,
                edge.IsIssue ? FontWeights.Medium : FontWeights.Normal);
            lbl.HorizontalAlignment = HorizontalAlignment.Center;
            lbl.Margin = new Thickness(0, 3, 0, 0);
            wireStack.Children.Add(lbl);
        }
        Grid.SetColumn(wireStack, 1);
        cols.Children.Add(wireStack);

        // Dependent node
        bool dependentIsPatch = ModPresentation.IsIndependentPatch(edge.Dependent);
        var depPill = BuildDepNodePill(
            dependentIsPatch ? "[补丁] " + edge.Dependent.Name : edge.Dependent.Name,
            $"{edge.Dependent.Id} · v{NeonVersionDiff.StripPrefix(edge.Dependent.Version)}",
            bad: false,
            onClick: !dependentIsPatch ? () => SelectMod(edge.Dependent.Id) : () => SwitchTab("patches"));
        Grid.SetColumn(depPill, 2);
        cols.Children.Add(depPill);

        // Right CTA
        Button actionBtn;
        var preRow = edge.Prerequisite != null
            ? s.AllRows.FirstOrDefault(r => r.Mod.Id.Equals(edge.Prerequisite.Id, StringComparison.OrdinalIgnoreCase))
            : null;
        if (edge.Status == ModDependencyStatus.DisabledPrerequisite && edge.DisabledVersionFits && preRow is { ToggleBlockReason: null })
        {
            var preStatus = GetInlineToggleStatus(preRow.Mod.Id);
            bool prePending = preStatus?.Phase is NeonInlineTogglePhase.Applying or NeonInlineTogglePhase.AppliedScanPending;
            var btn = NeonWidgets.CreateStyledButton(
                this,
                prePending ? "正在启用…" : "启用前置",
                "NeonPrimaryButton",
                () => RequestDirectToggleMod?.Invoke(preRow.Mod, edge.Dependent.Id),
                "xs");
            btn.IsEnabled = !prePending;
            actionBtn = btn;
        }
        else if (edge.Status == ModDependencyStatus.VersionMismatch && preRow is { HasUpdate: true })
        {
            actionBtn = NeonWidgets.CreateStyledButton(this, "预览更新前置", "NeonPrimaryButton",
                () => RequestPreviewUpdateMod?.Invoke(preRow.Mod), "xs");
        }
        else
        {
            actionBtn = NeonWidgets.CreateStyledButton(this, "查因果", "NeonGhostButton",
                () => SelectMod(edge.Prerequisite?.Id ?? edge.Dependent.Id), "xs");
        }
        actionBtn.Margin = new Thickness(10, 0, 0, 0);
        actionBtn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actionBtn, 3);
        cols.Children.Add(actionBtn);

        Grid.SetColumn(cols, 1);
        grid.Children.Add(cols);
        outer.Child = grid;
        return outer;
    }

    Button BuildDepNodePill(string name, string meta, bool bad, Action? onClick)
    {
        var stack = new StackPanel();
        stack.Children.Add(NeonWidgets.Text(name, 12.5, NeonPalette.InkPrimary, FontWeights.SemiBold, ellipsis: true));
        stack.Children.Add(NeonWidgets.Text(meta, 11, NeonPalette.InkMuted, mono: true, ellipsis: true));
        var box = new Border
        {
            Background = bad ? NeonPalette.CinnabarBg : NeonPalette.BgElevated,
            BorderBrush = bad ? NeonPalette.CinnabarBorder : NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 6),
            Child = stack
        };
        var btn = new Button
        {
            Content = box,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow,
            Template = NeonWidgets.CreateBareButtonTemplate()
        };
        if (onClick != null)
        {
            btn.MouseEnter += (_, _) => box.BorderBrush = NeonPalette.Sage;
            btn.MouseLeave += (_, _) => box.BorderBrush = bad ? NeonPalette.CinnabarBorder : NeonPalette.BorderSubtle;
            btn.Click += (_, _) => onClick();
        }
        return btn;
    }

    // ========================================================================
    // SELECTION, BATCH DOCK & KEYBOARD SHORTCUTS
    // ========================================================================

    public void SelectMod(string modId)
    {
        bool alreadyOpenSteady = inspectorPane.Visibility == Visibility.Visible && !inspectorClosing && selectedModId != null;
        bool wasClosing = inspectorClosing;
        double savedDeskOffset = deskScrollViewer.VerticalOffset;

        selectedModId = modId;
        UpdateRowSelectionVisualsOnly();

        if (alreadyOpenSteady)
        {
            // Panel is already open (A -> B): keep panel in place and swap content directly!
            RenderInspector(preserveScroll: false);
            return;
        }

        // First open from closed, or interrupting a mid-flight close animation
        if (!wasClosing)
            inspectorReturnFocus = Keyboard.FocusedElement;
        int myGen = ++inspectorAnimGeneration;
        inspectorClosing = false;
        inspectorPane.IsHitTestVisible = true;
        RenderInspector(preserveScroll: false);

        if (savedDeskOffset > 0)
            deskScrollViewer.ScrollToVerticalOffset(Math.Min(savedDeskOffset, deskScrollViewer.ScrollableHeight));

        NeonMotion.OpenInspector(
            this,
            inspectorPane,
            inspectorScrim,
            retargetFromClose: wasClosing,
            () => myGen == inspectorAnimGeneration);
    }

    public void CloseInspector()
    {
        if (selectedModId == null) return;

        var returnTarget = inspectorReturnFocus;
        inspectorReturnFocus = null;
        selectedModId = null;
        int myGen = ++inspectorAnimGeneration;
        inspectorClosing = true;
        inspectorPane.IsHitTestVisible = false;
        UpdateRowSelectionVisualsOnly();

        NeonMotion.CloseInspector(
            this,
            inspectorPane,
            inspectorScrim,
            () => myGen == inspectorAnimGeneration,
            () =>
            {
                double currentDeskOffset = deskScrollViewer.VerticalOffset;
                inspectorClosing = false;
                inspectorPane.IsHitTestVisible = true;
                UpdateResponsiveInspectorLayout();
                if (currentDeskOffset > 0)
                {
                    deskScrollViewer.ScrollToVerticalOffset(currentDeskOffset);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        deskScrollViewer.ScrollToVerticalOffset(Math.Min(currentDeskOffset, deskScrollViewer.ScrollableHeight));
                    }), DispatcherPriority.Loaded);
                }
            });

        if (!searchBox.IsKeyboardFocused && !IsModalOpen && !drawerOpenLogical)
            TryRestoreFocus(returnTarget);
    }

    static void TryRestoreFocus(IInputElement? target)
    {
        if (target is FrameworkElement fe && fe.IsVisible && fe.IsEnabled && fe.Focusable)
            Keyboard.Focus(fe);
    }

    void FinishInspectorCollapseImmediate(bool preserveScroll, double? explicitSavedOffset = null)
    {
        double savedDeskOffset = explicitSavedOffset ?? (preserveScroll ? deskScrollViewer.VerticalOffset : 0);
        inspectorAnimGeneration++;
        inspectorClosing = false;
        selectedModId = null;
        NeonMotion.ClearAnimations(inspectorPane);
        NeonMotion.ClearAnimations(inspectorScrim);
        inspectorPane.IsHitTestVisible = true;
        UpdateRowSelectionVisualsOnly();
        UpdateResponsiveInspectorLayout();
        if (preserveScroll && savedDeskOffset > 0)
        {
            deskScrollViewer.ScrollToVerticalOffset(savedDeskOffset);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                deskScrollViewer.ScrollToVerticalOffset(Math.Min(savedDeskOffset, deskScrollViewer.ScrollableHeight));
            }), DispatcherPriority.Loaded);
        }
    }

    void ClearSearch()
    {
        searchBox.Clear();
        searchBox.Focus();
    }

    void ResetSearchAndFilters()
    {
        activeFilter = "all";
        if (searchBox.Text.Length > 0) searchBox.Text = "";
        UpdateFilterPillVisuals();
        RenderWorkbenchContent(preserveScroll: false, animateSettle: true);
    }

    void SelectAllVisibleMods()
    {
        if (currentSnapshot == null) return;
        var visible = NeonWorkbenchBuilder.FilterAndSort(currentSnapshot.AllRows, activeFilter, searchBox.Text, sortBy);
        foreach (var row in visible)
        {
            checkedModFolders.Add(row.Mod.Folder);
            if (visibleRowVisuals.TryGetValue(row.Mod.Id, out var vis))
                vis.Check.IsChecked = true;
        }
        UpdateBatchDock(visible);
    }

    internal void ClearCheckedMods()
    {
        checkedModFolders.Clear();
        foreach (var (_, vis) in visibleRowVisuals)
            vis.Check.IsChecked = false;
        if (currentSnapshot != null)
        {
            var visible = NeonWorkbenchBuilder.FilterAndSort(currentSnapshot.AllRows, activeFilter, searchBox.Text, sortBy);
            UpdateBatchDock(visible);
        }
    }

    void UpdateBatchDock(NeonWorkbenchRow[] visible)
    {
        int totalChecked = checkedModFolders.Count;
        bool show = totalChecked > 0 && workbenchMode == "list";
        if (show)
        {
            var visibleSet = visible.Select(r => r.Mod.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int hidden = checkedModFolders.Count(f => !visibleSet.Contains(f));
            batchCountText.Text = $"已选 {totalChecked} 项";
            if (hidden > 0)
            {
                batchHiddenWarnText.Text = $"（含 {hidden} 项当前被筛选隐藏的已选项）";
                batchHiddenWarnText.Visibility = Visibility.Visible;
            }
            else
            {
                batchHiddenWarnText.Visibility = Visibility.Collapsed;
            }
        }
        SetBatchDockVisible(show);
    }

    void SetBatchDockVisible(bool show)
    {
        if (show)
        {
            if (batchDockOpenLogical && !batchDockClosing) return;
            bool retarget = batchDockClosing;
            int myGen = ++batchDockAnimGeneration;
            batchDockOpenLogical = true;
            batchDockClosing = false;
            batchDock.IsHitTestVisible = true;
            batchDock.Visibility = Visibility.Visible;
            NeonMotion.ShowBatchDock(this, batchDock, retarget, () => myGen == batchDockAnimGeneration);
            NeonMotion.OffsetToastRack(this, toastRack, NeonMotion.ToastBatchLiftY);
        }
        else
        {
            if (!batchDockOpenLogical && !batchDockClosing && batchDock.Visibility != Visibility.Visible) return;
            int myGen = ++batchDockAnimGeneration;
            batchDockOpenLogical = false;
            batchDockClosing = true;
            batchDock.IsHitTestVisible = false;
            NeonMotion.OffsetToastRack(this, toastRack, 0.0);
            NeonMotion.HideBatchDock(
                this,
                batchDock,
                () => myGen == batchDockAnimGeneration,
                () =>
                {
                    batchDockClosing = false;
                    batchDock.Visibility = Visibility.Collapsed;
                    batchDock.IsHitTestVisible = true;
                });
        }
    }

    void OnLaunchClicked()
    {
        if (currentSnapshot == null || currentSnapshot.GameRoot == null)
        {
            RequestChooseGameRoot?.Invoke();
            return;
        }
        if (currentSnapshot.GameRunning)
        {
            ShowToast("同一目录的 SlayTheSpire2.exe 已在运行，不会重复启动。", NeonTone.Amber);
            return;
        }
        if (currentSnapshot.HardBlockCount > 0)
        {
            var lines = currentSnapshot.Faults.Select(f => new NeonDiffLine(
                f.Severity == NeonTone.Cinnabar ? "del" : "mod",
                f.Kicker,
                f.Description,
                "建议处理后再开局")).ToArray();
            OpenTransactionModal(new NeonTransactionConfig(
                Title: "当前仍有依赖或版本阻断，确定直接启动游戏？",
                Subtitle: $"将启动 {currentSnapshot.GameRoot}\\SlayTheSpire2.exe",
                GameRunning: false,
                BlockedReason: null,
                AutoSnapshotNote: null,
                BoundaryNote: "上述问题可能造成部分 MOD 无法加载或报错，建议先在工作台处理后再开局。",
                Token: "PREFLIGHT",
                Groups: [new NeonDiffGroup($"检测到 {currentSnapshot.HardBlockCount} 项可能影响加载的问题", lines)],
                ConfirmLabel: "忽略提醒，直接启动",
                CancelLabel: "先回去处理",
                Danger: false,
                FootNote: "直接启动不会修改任何游戏或 MOD 文件。",
                OnConfirmAsync: () => { RequestLaunchGame?.Invoke(); return Task.CompletedTask; },
                PlainSummary: $"当前还有 {currentSnapshot.HardBlockCount} 项未处理的依赖或版本问题，直接启动可能导致对应 MOD 无法生效或报错。",
                ExpandTechnicalDetailsByDefault: true));
            return;
        }
        RequestLaunchGame?.Invoke();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (IsModalOpen)
            {
                CloseTransactionModal();
                e.Handled = true;
            }
            else if (drawerOpenLogical)
            {
                CloseToolsDrawer();
                e.Handled = true;
            }
            else if (selectedModId != null)
            {
                CloseInspector();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None && activeTab == "workbench" && !IsModalOpen)
        {
            if (Keyboard.FocusedElement is not TextBoxBase)
            {
                searchBox.Focus();
                e.Handled = true;
            }
        }
    }

    void UpdateResponsiveInspectorLayout()
    {
        if (selectedModId == null)
        {
            if (inspectorClosing) return;
            inspectorPane.Visibility = Visibility.Collapsed;
            inspectorScrim.Visibility = Visibility.Collapsed;
            splitGapCol.Width = new GridLength(0);
            splitInspectorCol.Width = new GridLength(0);
            return;
        }

        bool narrow = ActualWidth > 0 && ActualWidth < 1120;
        inspectorPane.Visibility = Visibility.Visible;

        if (narrow)
        {
            splitGapCol.Width = new GridLength(0);
            splitInspectorCol.Width = new GridLength(0);
            inspectorScrim.Visibility = Visibility.Visible;
            if (inspectorPane.Parent == splitBodyGrid)
            {
                splitBodyGrid.Children.Remove(inspectorPane);
                inspectorPane.HorizontalAlignment = HorizontalAlignment.Right;
                inspectorPane.VerticalAlignment = VerticalAlignment.Stretch;
                inspectorPane.Margin = new Thickness(0, 62, 10, 40);
                inspectorPane.BorderBrush = NeonPalette.BorderMedium;
                Panel.SetZIndex(inspectorPane, 50);
                Children.Add(inspectorPane);
            }
        }
        else
        {
            inspectorScrim.Visibility = Visibility.Collapsed;
            if (inspectorPane.Parent == this)
            {
                Children.Remove(inspectorPane);
                inspectorPane.HorizontalAlignment = HorizontalAlignment.Stretch;
                inspectorPane.VerticalAlignment = VerticalAlignment.Stretch;
                inspectorPane.Margin = new Thickness(0);
                inspectorPane.Effect = null;
                Grid.SetColumn(inspectorPane, 2);
                splitBodyGrid.Children.Add(inspectorPane);
            }
            splitGapCol.Width = new GridLength(14);
            splitInspectorCol.Width = new GridLength(392);
        }
    }

    Border BuildEmptyState(string title, string sub, string? ctaLabel, Action? onCta, bool actionDisabled = false, bool showProgress = false)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(20, 44, 20, 44)
        };
        var t = NeonWidgets.Text(title, 14, NeonPalette.InkSecondary, FontWeights.SemiBold);
        var s = NeonWidgets.Text(sub, 12, NeonPalette.InkMuted, wrap: true);
        s.HorizontalAlignment = HorizontalAlignment.Center;
        s.Margin = new Thickness(0, 6, 0, 12);
        if (showProgress)
        {
            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var (lampHost, halo, core) = NeonWidgets.CreateStatusLamp(8);
            NeonWidgets.SetStatusLamp(halo, core, NeonTone.Sage, showHalo: true);
            NeonMotion.StartSubtlePulse(this, halo);
            lampHost.Margin = new Thickness(-2, 0, 5, 0);
            lampHost.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(lampHost);
            t.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(t);
            stack.Children.Add(titleRow);
            stack.Children.Add(s);
            stack.Children.Add(NeonWidgets.CreateMineralProgressBar(this, width: 228));
        }
        else
        {
            t.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(t);
            stack.Children.Add(s);
        }
        if (ctaLabel != null && (onCta != null || actionDisabled))
        {
            var btn = new Button
            {
                Content = ctaLabel,
                Height = 27,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Background = NeonPalette.BgElevated,
                Foreground = NeonPalette.InkPrimary,
                BorderBrush = NeonPalette.BorderMedium,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                IsEnabled = !actionDisabled,
                Cursor = actionDisabled ? Cursors.Arrow : Cursors.Hand,
                Template = CreateRoundedButtonTemplate(6, NeonPalette.BgHover, NeonPalette.BorderStrong)
            };
            if (!actionDisabled && onCta != null)
                btn.Click += (_, _) => onCta();
            stack.Children.Add(btn);
        }
        return new Border { Child = stack };
    }

    // ========================================================================
    // SHARED CONTROL TEMPLATES
    // ========================================================================

    static readonly Dictionary<(double Radius, Brush HoverBg, Brush HoverBorder), ControlTemplate> CachedRoundedButtonTemplates = new();
    static readonly ControlTemplate CachedFilterPillTemplate = BuildFilterPillTemplate();
    static readonly ControlTemplate CachedSearchBoxTemplate = BuildSearchBoxTemplate();

    static ControlTemplate CreateRoundedButtonTemplate(double radius, Brush hoverBg, Brush hoverBorder)
    {
        var key = (radius, hoverBg, hoverBorder);
        if (CachedRoundedButtonTemplates.TryGetValue(key, out var cached))
            return cached;

        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        borderFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        borderFactory.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenter);
        template.VisualTree = borderFactory;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        if (hoverBg != Brushes.Transparent)
            hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Bd"));
        if (hoverBorder != Brushes.Transparent)
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, hoverBorder, "Bd"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(NeonMotion.TactileScaleProperty, 0.972, "Bd"));
        template.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "Bd"));
        template.Triggers.Add(disabled);

        template.Seal();
        CachedRoundedButtonTemplates[key] = template;
        return template;
    }

    static ControlTemplate CreateFilterPillTemplate() => CachedFilterPillTemplate;

    static ControlTemplate BuildFilterPillTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12.5));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        borderFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        borderFactory.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenter);
        template.VisualTree = borderFactory;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88, "Bd"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(NeonMotion.TactileScaleProperty, 0.96, "Bd"));
        template.Triggers.Add(pressed);

        template.Seal();
        return template;
    }

    static ControlTemplate CreateSearchBoxTemplate() => CachedSearchBoxTemplate;

    static ControlTemplate BuildSearchBoxTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var gridFactory = new FrameworkElementFactory(typeof(Grid));

        var haloFactory = new FrameworkElementFactory(typeof(Border), "Halo");
        haloFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
        haloFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        haloFactory.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        haloFactory.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        haloFactory.SetValue(UIElement.IsHitTestVisibleProperty, false);
        haloFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        haloFactory.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);
        gridFactory.AppendChild(haloFactory);

        var fieldFactory = new FrameworkElementFactory(typeof(Border), "Field");
        fieldFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        fieldFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        fieldFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        fieldFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        fieldFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        fieldFactory.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);

        var hostFactory = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        hostFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        hostFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        hostFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        fieldFactory.AppendChild(hostFactory);
        gridFactory.AppendChild(fieldFactory);
        template.VisualTree = gridFactory;

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, NeonPalette.Sage, "Field"));
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, NeonPalette.SageFocusRing, "Halo"));
        template.Triggers.Add(focus);

        template.Seal();
        return template;
    }
}
