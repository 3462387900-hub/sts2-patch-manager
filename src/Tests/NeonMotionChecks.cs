using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpirePatch;

public partial class MainWindow
{
    sealed record MotionFrame(byte[] Pixels, int Width, int Height, int Stride, double DurationSec);

    internal async Task CaptureNeonMotionAsync(string parent, string output)
    {
        parent = Path.GetFullPath(parent);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);

        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("动效验证失败：" + label);
            checks.Add("PASS " + label);
            File.WriteAllLines(Path.Combine(output, "动效验收报告.txt"), checks, new UTF8Encoding(false));
        }

        async Task Idle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
        }

        async Task Settle(int ms = 260)
        {
            await Task.Delay(ms);
            await Idle();
        }

        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }

        async Task<(double Min, double Max)> SampleOverAsync(Func<double> probe, int durationMs = 260)
        {
            double min = probe(), max = min;
            void OnFrame(object? sender, EventArgs args)
            {
                double v = probe();
                if (v < min) min = v;
                if (v > max) max = v;
            }
            CompositionTarget.Rendering += OnFrame;
            try { await Settle(durationMs); }
            finally { CompositionTarget.Rendering -= OnFrame; }
            return (min, max);
        }

        // Setup clean isolated fixture at 1280x851 (client 1264x812, even dimensions for crisp H.264)
        Width = 1280;
        Height = 851;
        await SetupNeonIsolatedFixtureAsync(parent);
        await Idle();

        var view = neonWorkbenchView!;
        view.ClearToasts();
        var animPrefKey = SystemParameters.ClientAreaAnimationKey;

        try
        {
            // Force animations ON inside fixture so tests run deterministically regardless of host Windows setting
            view.Resources[animPrefKey] = true;
            await Idle();

            // ----------------------------------------------------------------
            // TEST 1: Inspector First Open (short slide + fade, list stays put!)
            // ----------------------------------------------------------------
            Check(view.DeskScrollControl.ScrollableHeight > 180,
                $"工作台列表具备足够滚动高度（{view.DeskScrollControl.ScrollableHeight:F0}px）");
            view.DeskScrollControl.ScrollToVerticalOffset(140);
            await Idle();
            double scrollBaseline = view.DeskScrollControl.VerticalOffset;

            view.SelectMod("Ray");
            if (!SystemParameters.HighContrast)
            {
                double minOp = view.InspectorPaneControl.Opacity;
                double maxX = view.InspectorSlideTransform.X;
                await SampleOverAsync(() =>
                {
                    if (view.InspectorPaneControl.Opacity < minOp) minOp = view.InspectorPaneControl.Opacity;
                    if (view.InspectorSlideTransform.X > maxX) maxX = view.InspectorSlideTransform.X;
                    return view.InspectorSlideTransform.X;
                }, 260);
                Check(maxX > 8.0 && maxX <= NeonMotion.InspectorOpenOffsetX + 0.1 && minOp < 0.85,
                    $"详情首次打开呈现短距离侧移与淡入（位移峰值 {maxX:F1}px → 0，透明度谷值 {minOp:F2} → 1.00）");
            }
            else
            {
                await Settle(260);
            }
            Check(view.IsInspectorOpen && view.SelectedModId == "Ray" &&
                  Math.Abs(view.InspectorSlideTransform.X) < 0.05 &&
                  Math.Abs(view.InspectorPaneControl.Opacity - 1.0) < 0.01,
                "详情打开结束后完全就位（X=0, Opacity=1.0, Selected=Ray）");
            Check(Math.Abs(view.DeskScrollControl.VerticalOffset - scrollBaseline) < 1.0,
                $"详情首次打开未引起列表跳位（{scrollBaseline:F1}px → {view.DeskScrollControl.VerticalOffset:F1}px）");

            // ----------------------------------------------------------------
            // TEST 2: Switch MOD A -> MOD B -> MOD C while open (panel stays stationary!)
            // ----------------------------------------------------------------
            double switchMaxX = 0, switchMinOp = 1.0;
            void TrackStationary(object? sender, EventArgs args)
            {
                double ax = Math.Abs(view.InspectorSlideTransform.X);
                if (ax > switchMaxX) switchMaxX = ax;
                if (view.InspectorPaneControl.Opacity < switchMinOp) switchMinOp = view.InspectorPaneControl.Opacity;
            }
            CompositionTarget.Rendering += TrackStationary;
            try
            {
                view.SelectMod("Phrolova");
                await Task.Delay(50);
                view.SelectMod("BaseLib");
                await Task.Delay(50);
                view.SelectMod("HextechRunes");
                await Settle(200);
            }
            finally { CompositionTarget.Rendering -= TrackStationary; }
            Check(switchMaxX < 0.05 && switchMinOp > 0.99 && view.SelectedModId == "HextechRunes",
                $"面板已打开时连续换选 A→B→C，面板停留在原处直接换内容（最大位移 {switchMaxX:F2}px，最小透明度 {switchMinOp:F2}）");
            Check(Math.Abs(view.DeskScrollControl.VerticalOffset - scrollBaseline) < 1.0,
                $"连续换选详情期间列表滚动位置保持不动（{view.DeskScrollControl.VerticalOffset:F1}px）");

            // ----------------------------------------------------------------
            // TEST 3: Close Inspector (short slide-out + fade-out, list stays put!)
            // ----------------------------------------------------------------
            view.CloseInspector();
            if (!SystemParameters.HighContrast)
            {
                double closeMaxX = 0, closeMinOp = 1.0;
                int closeFrames = 0;
                await SampleOverAsync(() =>
                {
                    if (view.IsInspectorVisuallyVisible)
                    {
                        closeFrames++;
                        if (view.InspectorSlideTransform.X > closeMaxX) closeMaxX = view.InspectorSlideTransform.X;
                        if (view.InspectorPaneControl.Opacity < closeMinOp) closeMinOp = view.InspectorPaneControl.Opacity;
                    }
                    return view.InspectorSlideTransform.X;
                }, 260);
                Check(closeMaxX > 6.0 && closeMinOp < 0.85,
                    $"详情关闭呈现短距离侧移淡出（采样 {closeFrames} 帧，位移峰值 {closeMaxX:F1}px，透明度降至 {closeMinOp:F2}）");
            }
            else
            {
                await Settle(260);
            }
            Check(!view.IsInspectorOpen && !view.IsInspectorVisuallyVisible && !view.IsInspectorClosing && view.SelectedModId == null,
                "详情关闭完成后彻底收起（Collapsed），无残留状态");
            Check(Math.Abs(view.DeskScrollControl.VerticalOffset - scrollBaseline) < 1.0,
                $"详情关闭后列表未跳位（{scrollBaseline:F1}px → {view.DeskScrollControl.VerticalOffset:F1}px）");

            // ----------------------------------------------------------------
            // TEST 4: Active Tab Switch (Topbar completely static, content subtle fade-in)
            // ----------------------------------------------------------------
            double topbarMinOp = 1.0;
            double tabMinOp = 1.0;
            void TrackTab(object? sender, EventArgs args)
            {
                if (view.TopbarControl.Opacity < topbarMinOp) topbarMinOp = view.TopbarControl.Opacity;
                if (view.SecondaryTabHostControl.Visibility == Visibility.Visible &&
                    view.SecondaryTabHostControl.Opacity < tabMinOp)
                    tabMinOp = view.SecondaryTabHostControl.Opacity;
            }
            CompositionTarget.Rendering += TrackTab;
            try
            {
                view.SwitchTab("patches");
                await Settle(260);
            }
            finally { CompositionTarget.Rendering -= TrackTab; }
            Check(topbarMinOp == 1.0 && view.TopbarControl.RenderTransform == Transform.Identity,
                "主动切页期间顶栏保持 100% 稳定静止");
            if (!SystemParameters.HighContrast)
            {
                Check(tabMinOp < 0.90 && Math.Abs(view.SecondaryTabHostControl.Opacity - 1.0) < 0.01,
                    $"切到「补丁」页时内容区轻微淡入并稳定就位（透明度 {tabMinOp:F2} → {view.SecondaryTabHostControl.Opacity:F2}）");
            }
            view.SwitchTab("workbench");
            await Settle(260);
            Check(view.ActiveTab == "workbench" && Math.Abs(view.WorkbenchStageControl.Opacity - 1.0) < 0.01,
                "切回「工作台」页后内容完整就位");

            // ----------------------------------------------------------------
            // TEST 5: USER'S EXACT STRESS COMBO
            // Open A -> Immediately click B -> Close Inspector -> Immediately switch to Patches!
            // ----------------------------------------------------------------
            view.DeskScrollControl.ScrollToVerticalOffset(140);
            await Idle();
            double comboScrollBefore = view.DeskScrollControl.VerticalOffset;

            view.SelectMod("Ray");
            await Task.Delay(25);
            view.SelectMod("Phrolova");
            await Task.Delay(25);
            view.CloseInspector();
            await Task.Delay(20);
            view.SwitchTab("patches");

            // Last click MUST take effect immediately!
            Check(view.ActiveTab == "patches" &&
                  view.SecondaryTabHostControl.Visibility == Visibility.Visible &&
                  view.WorkbenchStageControl.Visibility == Visibility.Collapsed &&
                  view.SelectedModId == null &&
                  !view.IsInspectorOpen &&
                  !view.IsInspectorVisuallyVisible,
                "连续操作「打开 A → 马上点 B → 关闭详情 → 立刻切到补丁页」后，最后一击立即生效且旧关闭状态即时收口");

            // Wait well past all old 155ms/135ms animation clocks to guarantee NO ghost reopen!
            await Settle(380);
            Check(view.ActiveTab == "patches" &&
                  view.SelectedModId == null &&
                  !view.IsInspectorOpen &&
                  !view.IsInspectorVisuallyVisible &&
                  !view.IsInspectorClosing &&
                  Math.Abs(view.SecondaryTabHostControl.Opacity - 1.0) < 0.01 &&
                  view.TopbarControl.Opacity == 1.0,
                "等待 380ms 超过所有旧动画时钟后，旧动画未把面板重新打开，停留在补丁页且状态干净");

            // Switch back to Workbench and verify list scroll did NOT jump!
            view.SwitchTab("workbench");
            await Settle(260);
            Check(!view.IsInspectorOpen && !view.IsInspectorVisuallyVisible &&
                  Math.Abs(view.DeskScrollControl.VerticalOffset - comboScrollBefore) < 1.0,
                $"切回工作台后详情保持关闭且列表未跳位（{comboScrollBefore:F1}px → {view.DeskScrollControl.VerticalOffset:F1}px）");

            // ----------------------------------------------------------------
            // TEST 6: Background Refresh While Inspector Open (Search Focus, Scroll & Stillness!)
            // ----------------------------------------------------------------
            view.SearchBoxControl.Text = "BaseLib";
            await Idle();
            double refreshTargetScroll = Math.Min(160, Math.Max(90, view.DeskScrollControl.ScrollableHeight - 20));
            view.DeskScrollControl.ScrollToVerticalOffset(refreshTargetScroll);
            await Idle();
            view.SelectMod("Ray");
            await Settle(260);

            var scope = FocusManager.GetFocusScope(view.SearchBoxControl);
            FocusManager.SetFocusedElement(scope, view.SearchBoxControl);
            Keyboard.Focus(view.SearchBoxControl);
            view.SearchBoxControl.CaretIndex = view.SearchBoxControl.Text.Length;
            await Idle();

            double offsetBeforeBg = view.DeskScrollControl.VerticalOffset;
            double inspOffsetBeforeBg = view.InspectorScrollControl.VerticalOffset;
            int caretBeforeBg = view.SearchBoxControl.CaretIndex;

            double bgStageMinOp = 1.0, bgInspMinOp = 1.0, bgInspMaxX = 0.0;
            void TrackStillness(object? sender, EventArgs args)
            {
                if (view.WorkbenchStageControl.Opacity < bgStageMinOp) bgStageMinOp = view.WorkbenchStageControl.Opacity;
                if (view.InspectorPaneControl.Opacity < bgInspMinOp) bgInspMinOp = view.InspectorPaneControl.Opacity;
                double ax = Math.Abs(view.InspectorSlideTransform.X);
                if (ax > bgInspMaxX) bgInspMaxX = ax;
            }
            CompositionTarget.Rendering += TrackStillness;
            try
            {
                await RefreshModsAsync();
                await Settle(220);
            }
            finally { CompositionTarget.Rendering -= TrackStillness; }

            bool searchStillFocused = view.SearchBoxControl.IsKeyboardFocused ||
                ReferenceEquals(FocusManager.GetFocusedElement(scope), view.SearchBoxControl);
            Check(bgStageMinOp == 1.0 && bgInspMinOp == 1.0 && bgInspMaxX < 0.01,
                $"详情打开期间发生后台刷新时，页面和面板全程保持静止（页面 Opacity={bgStageMinOp:F2}，面板 Opacity={bgInspMinOp:F2}，位移={bgInspMaxX:F2}px）");
            Check(view.SearchBoxControl.Text == "BaseLib" && searchStillFocused && view.SearchBoxControl.CaretIndex == caretBeforeBg,
                $"后台刷新后搜索词「BaseLib」、搜索框焦点与光标位置（{view.SearchBoxControl.CaretIndex}）完整保留");
            Check(Math.Abs(view.DeskScrollControl.VerticalOffset - offsetBeforeBg) < 1.0 &&
                  Math.Abs(view.InspectorScrollControl.VerticalOffset - inspOffsetBeforeBg) < 1.0 &&
                  view.IsInspectorOpen && view.SelectedModId == "Ray",
                $"后台刷新后列表滚动位置（{offsetBeforeBg:F1}→{view.DeskScrollControl.VerticalOffset:F1}px）与 Ray 详情完整保留");

            // ----------------------------------------------------------------
            // TEST 8: Shared Sliding Nav Indicator (FeelCurve nav-indicator)
            // ----------------------------------------------------------------
            static double ReadTransformX(FrameworkElement el)
            {
                if (el.RenderTransform is TranslateTransform t) return t.X;
                if (el.RenderTransform is TransformGroup g)
                    return g.Children.OfType<TranslateTransform>().FirstOrDefault()?.X ?? 0.0;
                return 0.0;
            }
            static double ReadTransformY(FrameworkElement el)
            {
                if (el.RenderTransform is TranslateTransform t) return t.Y;
                if (el.RenderTransform is TransformGroup g)
                    return g.Children.OfType<TranslateTransform>().FirstOrDefault()?.Y ?? 0.0;
                return 0.0;
            }
            static double ReadScaleX(FrameworkElement el)
            {
                if (el.RenderTransform is ScaleTransform s) return s.ScaleX;
                if (el.RenderTransform is TransformGroup g)
                    return g.Children.OfType<ScaleTransform>().FirstOrDefault()?.ScaleX ?? 1.0;
                return 1.0;
            }

            view.SearchBoxControl.Text = "";
            view.CloseInspector();
            await Settle(260);

            double navXWorkbench = ReadTransformX(view.NavActiveIndicatorControl);
            var navSample = await SampleOverAsync(() =>
            {
                return ReadTransformX(view.NavActiveIndicatorControl);
            }, 20);
            view.SwitchTab("acquire");
            var (navMinX, navMaxX) = await SampleOverAsync(() => ReadTransformX(view.NavActiveIndicatorControl), 280);
            double navXAcquire = ReadTransformX(view.NavActiveIndicatorControl);
            Check(navXAcquire > navXWorkbench + 60.0 && navMaxX > navXWorkbench + 60.0 && view.TopbarControl.RenderTransform == Transform.Identity,
                $"共享导航下划线平滑滑移至「获取与更新」（X: {navXWorkbench:F1}px → {navXAcquire:F1}px，顶栏本体保持静止）");
            view.SwitchTab("workbench");
            await Settle(280);
            double navXBack = ReadTransformX(view.NavActiveIndicatorControl);
            Check(Math.Abs(navXBack - navXWorkbench) < 1.5,
                $"共享导航下划线平滑滑回「工作台」（X={navXBack:F1}px）");

            // ----------------------------------------------------------------
            // TEST 9: Mode Segmented Pill Glide (清单 ↔ 依赖图谱)
            // ----------------------------------------------------------------
            double segListX = ReadTransformX(view.ModeSegThumbControl);
            var depsBtn = Descendants<Button>(view).Single(b => Equals(b.Content, "依赖图谱"));
            depsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, depsBtn));
            await SampleOverAsync(() => ReadTransformX(view.ModeSegThumbControl), 280);
            double segDepsX = ReadTransformX(view.ModeSegThumbControl);
            Check(view.WorkbenchMode == "deps" && segDepsX > segListX + 20.0,
                $"模式分段滑块平滑滑向「依赖图谱」（X: {segListX:F1}px → {segDepsX:F1}px）");
            var listBtn = Descendants<Button>(view).Single(b => Equals(b.Content, "清单"));
            listBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, listBtn));
            await Settle(280);
            Check(view.WorkbenchMode == "list" && Math.Abs(ReadTransformX(view.ModeSegThumbControl) - segListX) < 1.5,
                $"模式分段滑块平滑滑回「清单」（X={ReadTransformX(view.ModeSegThumbControl):F1}px）");

            // ----------------------------------------------------------------
            // TEST 10: Jelly Toggle Switch Physics (果冻开关 squash-and-stretch!)
            // ----------------------------------------------------------------
            view.SwitchTab("patches");
            await Settle(240);
            bool initialOrphan = OrphanPatchAutoStop.Enabled(NeonDataRoot);
            var orphanSwitch = Descendants<CheckBox>(view.SecondaryTabHostControl)
                .Single(c => (AutomationProperties.GetName(c) ?? "").Contains("自动停用孤立补丁"));
            Border? FindOrphanKnob()
            {
                var cb = Descendants<CheckBox>(view.SecondaryTabHostControl)
                    .FirstOrDefault(c => (AutomationProperties.GetName(c) ?? "").Contains("自动停用孤立补丁"));
                if (cb == null) return null;
                return Descendants<Canvas>(cb).SelectMany(c => c.Children.OfType<Border>()).FirstOrDefault();
            }
            double jellyMaxWidth = NeonMotion.JellyKnobRestWidth;
            double jellyMinX = double.MaxValue, jellyMaxX = double.MinValue;
            int foundFrames = 0;
            orphanSwitch.IsChecked = !initialOrphan;
            await SampleOverAsync(() =>
            {
                var k = FindOrphanKnob();
                if (k == null) return NeonMotion.JellyKnobRestWidth;
                foundFrames++;
                double w = double.IsNaN(k.Width) ? NeonMotion.JellyKnobRestWidth : k.Width;
                double kx = ReadTransformX(k);
                if (w > jellyMaxWidth) jellyMaxWidth = w;
                if (kx < jellyMinX) jellyMinX = kx;
                if (kx > jellyMaxX) jellyMaxX = kx;
                return w;
            }, 430);
            var settledKnob = FindOrphanKnob();
            double finalKnobW = settledKnob?.Width ?? 0.0;
            if (!SystemParameters.HighContrast)
            {
                Check(jellyMaxWidth > 17.5 && Math.Abs(finalKnobW - NeonMotion.JellyKnobRestWidth) < 0.25,
                    $"果冻开关切换呈现中段拉长与回弹就位（采样 {foundFrames} 帧，峰值宽度 {jellyMaxWidth:F1}px → 稳定 {finalKnobW:F1}px，滑移 X:{jellyMinX:F1}..{jellyMaxX:F1}px）");
            }
            // Restore original orphan auto-stop state cleanly
            orphanSwitch = Descendants<CheckBox>(view.SecondaryTabHostControl)
                .Single(c => (AutomationProperties.GetName(c) ?? "").Contains("自动停用孤立补丁"));
            orphanSwitch.IsChecked = initialOrphan;
            await Settle(400);
            view.SwitchTab("workbench");
            await Settle(240);

            // ----------------------------------------------------------------
            // TEST 11: Tools Drawer & Transaction Modal Entrance/Exit + Immediate Logical Close
            // ----------------------------------------------------------------
            view.OpenToolsDrawer();
            double drawerMaxX = ReadTransformX(view.DrawerSheetHostControl);
            await SampleOverAsync(() =>
            {
                double x = ReadTransformX(view.DrawerSheetHostControl);
                if (x > drawerMaxX) drawerMaxX = x;
                return x;
            }, 300);
            if (!SystemParameters.HighContrast)
            {
                Check(drawerMaxX > 10.0 && Math.Abs(ReadTransformX(view.DrawerSheetHostControl)) < 0.1 && view.IsDrawerOpen,
                    $"设置抽屉平滑侧滑展开并就位（位移峰值 {drawerMaxX:F1}px → 0）");
            }
            view.CloseToolsDrawer();
            Check(!view.IsDrawerOpen, "关闭设置抽屉时逻辑状态立即收口");
            await Settle(280);
            Check(!view.IsDrawerVisuallyVisible, "设置抽屉退场结束后彻底收起");

            view.OpenTransactionModal(new NeonTransactionConfig(
                Title: "动效验收预览窗",
                Subtitle: "仅验证弹窗入场缩放与退场收口，不改动任何文件",
                GameRunning: false,
                BlockedReason: null,
                AutoSnapshotNote: null,
                BoundaryNote: null,
                Token: "MOTIONTEST",
                Groups: Array.Empty<NeonDiffGroup>(),
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            double modalMinScale = ReadScaleX(view.ModalSheetHostControl);
            double modalMinOp = view.ModalSheetHostControl.Opacity;
            await SampleOverAsync(() =>
            {
                double s = ReadScaleX(view.ModalSheetHostControl);
                if (s < modalMinScale) modalMinScale = s;
                if (view.ModalSheetHostControl.Opacity < modalMinOp) modalMinOp = view.ModalSheetHostControl.Opacity;
                return s;
            }, 250);
            if (!SystemParameters.HighContrast)
            {
                Check(modalMinScale < 0.99 && modalMinOp < 0.85 &&
                      Math.Abs(ReadScaleX(view.ModalSheetHostControl) - 1.0) < 0.01 &&
                      Math.Abs(view.ModalSheetHostControl.Opacity - 1.0) < 0.01,
                    $"交易弹窗呈现轻缩放与淡入就位（缩放 {modalMinScale:F3} → 1.000，透明度 {modalMinOp:F2} → 1.00）");
            }
            view.CloseTransactionModal();
            Check(!view.IsModalOpen, "关闭交易弹窗时 IsModalOpen 立即变为 false");
            await Settle(280);
            Check(!view.IsModalVisuallyVisible && !view.IsModalClosing, "交易弹窗退场结束后彻底 Collapsed");

            // ----------------------------------------------------------------
            // TEST 12: BatchDock Rise & Toast Anti-Collision Offset
            // ----------------------------------------------------------------
            var firstModCb = Descendants<CheckBox>(view.DeskContentControl).First();
            firstModCb.IsChecked = true;
            firstModCb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, firstModCb));
            view.ShowToast("已验证批量栏与提示浮层避让");
            await Settle(260);
            double liftedToastY = ReadTransformY(view.ToastRackControl);
            Check(view.IsBatchDockOpen && view.BatchDockControl.Visibility == Visibility.Visible && liftedToastY < -40.0,
                $"批量操作栏浮出时 Toast 队列自动上移避让（Toast Y={liftedToastY:F1}px）");
            firstModCb = Descendants<CheckBox>(view.DeskContentControl).First();
            firstModCb.IsChecked = false;
            firstModCb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, firstModCb));
            await Settle(260);
            double restToastY = ReadTransformY(view.ToastRackControl);
            Check(!view.IsBatchDockOpen && view.BatchDockControl.Visibility == Visibility.Collapsed && Math.Abs(restToastY) < 1.0,
                $"取消勾选后批量栏平滑收起且 Toast 队列归位（Toast Y={restToastY:F1}px）");
            view.ClearToasts();

            // ----------------------------------------------------------------
            // TEST 7: System Animations Disabled (ClientAreaAnimationKey = false -> instant switch!)
            // ----------------------------------------------------------------
            view.Resources[animPrefKey] = false;
            await Idle();

            view.SwitchTab("patches");
            Check(view.ActiveTab == "patches" && view.SecondaryTabHostControl.Opacity == 1.0,
                "系统关闭动画时，切到「补丁」页直接切换就位（无淡入）");
            view.SwitchTab("workbench");
            Check(view.ActiveTab == "workbench" && view.WorkbenchStageControl.Opacity == 1.0,
                "系统关闭动画时，切回「工作台」页直接切换就位");
            view.SelectMod("Ray");
            Check(view.IsInspectorOpen && view.InspectorPaneControl.Opacity == 1.0 &&
                  Math.Abs(view.InspectorSlideTransform.X) < 0.001,
                "系统关闭动画时，打开详情面板直接就位（Opacity=1.0, X=0）");
            view.CloseInspector();
            Check(!view.IsInspectorOpen && !view.IsInspectorVisuallyVisible && !view.IsInspectorClosing,
                "系统关闭动画时，关闭详情面板直接收起（无退场延迟）");
            view.OpenToolsDrawer();
            view.CloseToolsDrawer();
            Check(!view.IsDrawerOpen && !view.IsDrawerVisuallyVisible,
                "系统关闭动画时，设置抽屉直接收起（无退场延迟）");
            view.OpenTransactionModal(new NeonTransactionConfig(
                Title: "瞬关测试", Subtitle: "", GameRunning: false, BlockedReason: null,
                AutoSnapshotNote: null, BoundaryNote: null, Token: "INSTANT",
                Groups: Array.Empty<NeonDiffGroup>(), ConfirmLabel: "", CancelLabel: "关闭"));
            view.CloseTransactionModal();
            Check(!view.IsModalOpen && !view.IsModalVisuallyVisible,
                "系统关闭动画时，交易弹窗直接收起（无退场延迟）");

            // ----------------------------------------------------------------
            // PART B: Record Real-Time 60fps Demo Frames (Open A -> Switch B -> Switch C -> Close -> Tab Switch)
            // ----------------------------------------------------------------
            view.Resources[animPrefKey] = true;
            view.ClearToasts();
            view.DeskScrollControl.ScrollToTop();
            await Idle();

            await RecordMotionClipFramesAsync(view, Path.Combine(output, "frames"));
            Check(File.Exists(Path.Combine(output, "frames", "concat.txt")),
                "已按真实时间戳录制打开、换选、收起详情与切页的实时帧序列");
        }
        finally
        {
            view.Resources.Remove(animPrefKey);
            CloseModWatch();
        }
    }

    async Task RecordMotionClipFramesAsync(NeonWorkbenchView view, string framesDir)
    {
        if (Directory.Exists(framesDir)) Directory.Delete(framesDir, true);
        Directory.CreateDirectory(framesDir);

        const double captureScale = 6.0; // Software RenderTargetBitmap takes ~50ms/frame -> 50/6 ≈ 8.3ms real time (>100fps effective!)
        var captured = new List<MotionFrame>();

        (byte[] Pixels, int Width, int Height, int Stride) SnapshotView()
        {
            UpdateLayout();
            int w = Math.Max(1, (int)Math.Round(view.ActualWidth));
            int h = Math.Max(1, (int)Math.Round(view.ActualHeight));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(view);
            int stride = w * 4;
            byte[] pixels = new byte[stride * h];
            rtb.CopyPixels(pixels, stride, 0);
            return (pixels, w, h, stride);
        }

        async Task AppendHoldAsync(double realHoldSec)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var snap = SnapshotView();
            captured.Add(new MotionFrame(snap.Pixels, snap.Width, snap.Height, snap.Stride, realHoldSec));
        }

        async Task CaptureTransitionAsync(Action trigger, int realWindowMs)
        {
            var localFrames = new List<((byte[] Pixels, int Width, int Height, int Stride) Snap, double WallMs)>();
            var localSw = Stopwatch.StartNew();
            trigger();
            localFrames.Add((SnapshotView(), 0.0));

            double wallTargetMs = realWindowMs * captureScale;
            while (localSw.Elapsed.TotalMilliseconds < wallTargetMs)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(12);
                double t = localSw.Elapsed.TotalMilliseconds;
                localFrames.Add((SnapshotView(), t));
            }
            // Allow any final completion callback to finish cleanly
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            localFrames.Add((SnapshotView(), Math.Max(wallTargetMs, localSw.Elapsed.TotalMilliseconds)));

            for (int i = 0; i < localFrames.Count; i++)
            {
                double nextWall = i + 1 < localFrames.Count
                    ? localFrames[i + 1].WallMs
                    : localFrames[i].WallMs + 16.67 * captureScale;
                double realDurSec = Math.Clamp((nextWall - localFrames[i].WallMs) / (1000.0 * captureScale), 0.004, 0.05);
                var s = localFrames[i].Snap;
                captured.Add(new MotionFrame(s.Pixels, s.Width, s.Height, s.Stride, realDurSec));
            }
        }

        NeonMotion.RecordingTimeScale = captureScale;
        try
        {
            // 1. Calm Workbench landing (0.55s)
            await AppendHoldAsync(0.55);

            // 2. First Open: Ray (155ms slide+fade at real 1:1 timing, then hold 0.80s)
            await CaptureTransitionAsync(() => view.SelectMod("Ray"), 190);
            await AppendHoldAsync(0.80);

            // 3. Switch A -> B: Phrolova (panel stays in place, swaps content directly, hold 0.80s)
            view.SelectMod("Phrolova");
            await AppendHoldAsync(0.80);

            // 4. Switch B -> C: HextechRunes (second stationary switch, hold 0.80s)
            view.SelectMod("HextechRunes");
            await AppendHoldAsync(0.80);

            // 5. Close Inspector (135ms slide+fade out at real 1:1 timing, then hold 0.65s)
            await CaptureTransitionAsync(() => view.CloseInspector(), 175);
            await AppendHoldAsync(0.65);

            // 6. Active Tab Switch -> Patches (150ms subtle fade-in, topbar stationary, hold 0.80s)
            await CaptureTransitionAsync(() => view.SwitchTab("patches"), 185);
            await AppendHoldAsync(0.80);

            // 7. Active Tab Switch -> Workbench (150ms subtle fade-in back, hold 0.65s)
            await CaptureTransitionAsync(() => view.SwitchTab("workbench"), 185);
            await AppendHoldAsync(0.65);
        }
        finally
        {
            NeonMotion.RecordingTimeScale = 1.0;
        }

        // Persist captured frames as lossless PNG + ffmpeg concat manifest with exact real-time durations
        var concat = new StringBuilder();
        for (int i = 0; i < captured.Count; i++)
        {
            var f = captured[i];
            string fileName = $"f{i:D4}.png";
            string fullPath = Path.Combine(framesDir, fileName);
            var bs = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null, f.Pixels, f.Stride);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bs));
            using (var fs = File.Create(fullPath)) enc.Save(fs);

            concat.AppendLine($"file '{fileName}'");
            concat.AppendLine($"duration {f.DurationSec.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        // ffmpeg concat demuxer requires repeating the last file without duration
        if (captured.Count > 0)
            concat.AppendLine($"file 'f{captured.Count - 1:D4}.png'");

        File.WriteAllText(Path.Combine(framesDir, "concat.txt"), concat.ToString(), new UTF8Encoding(false));
    }
}
