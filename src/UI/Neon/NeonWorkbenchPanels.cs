using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SpirePatch;

internal sealed partial class NeonWorkbenchView
{
    // ========================================================================
    // CONTEXTUAL CAUSAL INSPECTOR
    // ========================================================================

    void RenderInspector(bool preserveScroll, double? explicitSavedOffset = null)
    {
        double targetOffset = explicitSavedOffset ?? (preserveScroll ? inspectorScroll.VerticalOffset : 0);
        var row = selectedModId == null || currentSnapshot == null
            ? null
            : currentSnapshot.AllRows.FirstOrDefault(r => r.Mod.Id.Equals(selectedModId, StringComparison.OrdinalIgnoreCase));

        if (row == null)
        {
            selectedModId = null;
            UpdateResponsiveInspectorLayout();
            return;
        }

        UpdateResponsiveInspectorLayout();
        var s = currentSnapshot!;
        var mod = row.Mod;

        // ---- Inspector Header ----
        inspectorHeadGrid.Children.Clear();
        inspectorHeadGrid.ColumnDefinitions.Clear();
        inspectorHeadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inspectorHeadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headInfo = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        headInfo.Children.Add(NeonWidgets.Text(mod.Name, 15, NeonPalette.InkPrimary, FontWeights.Bold, wrap: true));

        var idRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        idRow.Children.Add(NeonWidgets.Text(mod.Id, 11.5, NeonPalette.InkMuted, mono: true));
        idRow.Children.Add(InspectorDotSep());
        idRow.Children.Add(NeonWidgets.Text("v" + NeonVersionDiff.StripPrefix(mod.Version), 11.5, NeonPalette.InkMuted, mono: true));
        idRow.Children.Add(InspectorDotSep());
        idRow.Children.Add(NeonWidgets.Text(string.IsNullOrWhiteSpace(mod.Author) ? "未知作者" : mod.Author, 11.5, NeonPalette.InkMuted));
        var stateBadge = NeonWidgets.CreateBadge(mod.Enabled ? "已启用" : "已停用", mod.Enabled ? NeonTone.Sage : NeonTone.Neutral);
        stateBadge.Margin = new Thickness(6, 0, 0, 0);
        idRow.Children.Add(stateBadge);
        headInfo.Children.Add(idRow);
        inspectorHeadGrid.Children.Add(headInfo);

        var closeBtn = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Content = "×",
            FontSize = 16,
            Foreground = NeonPalette.InkSecondary,
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "关闭详情 (Esc)",
            VerticalAlignment = VerticalAlignment.Top,
            Template = CreateRoundedButtonTemplate(6, NeonPalette.BgElevated, NeonPalette.BorderMedium)
        };
        AutomationProperties.SetName(closeBtn, "关闭详情");
        closeBtn.Click += (_, _) => CloseInspector();
        Grid.SetColumn(closeBtn, 1);
        inspectorHeadGrid.Children.Add(closeBtn);

        // ---- Inspector Body ----
        inspectorBodyStack.Children.Clear();
        void AddSectionGap() => inspectorBodyStack.Children.Add(new Border { Height = 16 });

        // 0. Contextual Urgent Action Alert right at top
        UIElement? urgentAlert = BuildInspectorUrgentAlert(row, s);
        if (urgentAlert != null)
        {
            inspectorBodyStack.Children.Add(urgentAlert);
            AddSectionGap();
        }

        // 1. 3-Tier Interactive Causal Chain
        inspectorBodyStack.Children.Add(BuildCausalChainSection(row, s));
        AddSectionGap();

        // 2. Associated Curated Patches (with Side-by-Side Status + Type Badges!)
        if (row.RelatedPatches.Length > 0)
        {
            inspectorBodyStack.Children.Add(BuildRelatedPatchesSection(row, s));
            AddSectionGap();
        }

        // 3. Source & Update Verification
        inspectorBodyStack.Children.Add(BuildSourceSection(row));
        AddSectionGap();

        // 4. Files & Single-MOD Backups
        inspectorBodyStack.Children.Add(BuildFilesAndBackupsSection(row, s));

        // ---- Inspector Footer ----
        if (s.GameRunning)
        {
            var blockTb = new TextBlock
            {
                FontFamily = NeonPalette.SansFont,
                FontSize = 12,
                Foreground = NeonPalette.Amber,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17.5
            };
            blockTb.Inlines.Add(new Run("退出游戏后可启停：") { FontWeight = FontWeights.SemiBold });
            blockTb.Inlines.Add(new Run("检测到 SlayTheSpire2.exe 正在运行，退出后即可直接切换此模组。"));
            inspectorFootBorder.Child = blockTb;
        }
        else if (row.IsLockedByDependents && row.ToggleBlockReason != null)
        {
            var blockTb = new TextBlock
            {
                FontFamily = NeonPalette.SansFont,
                FontSize = 12,
                Foreground = NeonPalette.Amber,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17.5
            };
            blockTb.Inlines.Add(new Run(FormatDependentLockShortLabel(row) + "：") { FontWeight = FontWeights.SemiBold });
            blockTb.Inlines.Add(new Run(row.ToggleBlockReason));
            inspectorFootBorder.Child = blockTb;
        }
        else if (row.ToggleBlockReason != null)
        {
            var blockTb = new TextBlock
            {
                FontFamily = NeonPalette.SansFont,
                FontSize = 12,
                Foreground = NeonPalette.Cinnabar,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17.5
            };
            blockTb.Inlines.Add(new Run("当前无法启停：") { FontWeight = FontWeights.SemiBold });
            blockTb.Inlines.Add(new Run(row.ToggleBlockReason));
            inspectorFootBorder.Child = blockTb;
        }
        else
        {
            var footGrid = new Grid();
            footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hint = NeonWidgets.Text(
                mod.Enabled ? "停用仅移至保留区，不删除文件" : "启用前自动核验停用区文件指纹",
                11.5, NeonPalette.InkMuted, wrap: true);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(0, 0, 8, 0);
            footGrid.Children.Add(hint);

            var footStatus = GetInlineToggleStatus(mod.Id);
            bool footPending = footStatus?.Phase is NeonInlineTogglePhase.Applying or NeonInlineTogglePhase.AppliedScanPending;
            string footBtnLabel = footPending
                ? (footStatus!.Phase == NeonInlineTogglePhase.AppliedScanPending
                    ? "正在核验依赖…"
                    : footStatus.TargetEnable ? "正在启用…" : "正在停用…")
                : mod.Enabled ? "停用并保留" : "恢复启用";

            var toggleBtn = NeonWidgets.CreateStyledButton(
                this,
                footBtnLabel,
                mod.Enabled ? "NeonSecondaryButton" : "NeonPrimaryButton",
                () => RequestDirectToggleMod?.Invoke(mod, mod.Id),
                "sm");
            toggleBtn.IsEnabled = !footPending;
            if (footPending) NeonMotion.StartSubtlePulse(this, toggleBtn);
            AutomationProperties.SetName(toggleBtn, footBtnLabel + " " + mod.Name);
            Grid.SetColumn(toggleBtn, 1);
            footGrid.Children.Add(toggleBtn);
            inspectorFootBorder.Child = footGrid;
        }

        if (preserveScroll)
        {
            inspectorScroll.ScrollToVerticalOffset(targetOffset);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                inspectorScroll.ScrollToVerticalOffset(Math.Min(targetOffset, inspectorScroll.ScrollableHeight));
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            inspectorScroll.ScrollToTop();
        }
    }

    UIElement? BuildInspectorUrgentAlert(NeonWorkbenchRow row, NeonWorkbenchSnapshot s)
    {
        if (row.OwnDisabledPrereq != null)
        {
            var edge = row.OwnDisabledPrereq;
            var preRow = edge.Prerequisite != null
                ? s.AllRows.FirstOrDefault(r => r.Mod.Id.Equals(edge.Prerequisite.Id, StringComparison.OrdinalIgnoreCase))
                : null;
            var preStatus = preRow != null ? GetInlineToggleStatus(preRow.Mod.Id) : null;

            if (preStatus is { Phase: NeonInlineTogglePhase.AppliedScanFailed })
                return BuildInlineReceiptCard(row.Mod.Id, new NeonInspectorResolutionReceipt(
                    PrereqId: preStatus.ModId,
                    PrereqName: preStatus.ModName,
                    Tone: NeonTone.Amber,
                    Lead: "已恢复启用，依赖状态待核验：",
                    Body: preStatus.Detail ?? $"{preStatus.ModId} 已移回 mods/，请重新扫描刷新依赖与补丁状态。",
                    ShowRescanAction: true));

            string shortSelf = NeonWidgets.ShortDisplayName(row.Mod.Name);
            var stack = new StackPanel();
            var tb = new TextBlock { FontFamily = NeonPalette.SansFont, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            tb.Inlines.Add(new Run("依赖阻断：") { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run("必需前置 "));
            tb.Inlines.Add(new Run(edge.DependencyId) { FontFamily = NeonPalette.MonoFont, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run(" 当前处于停用保留区。恢复 "));
            tb.Inlines.Add(new Run(edge.DependencyId) { FontFamily = NeonPalette.MonoFont, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run($" 后，预计解除{shortSelf}的前置阻断。"));
            stack.Children.Add(tb);

            if (preRow != null)
            {
                if (edge.DisabledVersionFits && preRow.ToggleBlockReason == null)
                {
                    bool pending = preStatus?.Phase is NeonInlineTogglePhase.Applying or NeonInlineTogglePhase.AppliedScanPending;
                    string ctaText = pending
                        ? (preStatus!.Phase == NeonInlineTogglePhase.AppliedScanPending
                            ? $"正在核验 {preRow.Mod.Id} 依赖…"
                            : $"正在启用 {preRow.Mod.Id}…")
                        : $"恢复启用 {preRow.Mod.Id}";
                    var cta = NeonWidgets.CreateStyledButton(
                        this,
                        ctaText,
                        "NeonDangerButton",
                        () => RequestDirectToggleMod?.Invoke(preRow.Mod, row.Mod.Id),
                        "sm");
                    cta.IsEnabled = !pending;
                    cta.Margin = new Thickness(0, 8, 0, 0);
                    cta.HorizontalAlignment = HorizontalAlignment.Left;
                    AutomationProperties.SetName(cta, ctaText);
                    if (pending) NeonMotion.StartSubtlePulse(this, cta);
                    stack.Children.Add(cta);

                    if (preStatus is { Phase: NeonInlineTogglePhase.Failed })
                    {
                        var failNote = NeonWidgets.Text(
                            "恢复启用未完成：" + (preStatus.Detail ?? preStatus.Message),
                            11.5, NeonPalette.AlertDangerInk, FontWeights.SemiBold, wrap: true);
                        failNote.Margin = new Thickness(0, 7, 0, 0);
                        stack.Children.Add(failNote);
                    }
                }
                else
                {
                    string why = !edge.DisabledVersionFits
                        ? "前置版本同样不满足最低要求，无法直接通过启用解决：" + edge.Message
                        : preRow.ToggleBlockReason ?? "请先刷新清单后再操作。";
                    var note = NeonWidgets.Text(why, 11.5, NeonPalette.AlertDangerInk, wrap: true);
                    note.Margin = new Thickness(0, 6, 0, 0);
                    stack.Children.Add(note);
                }
            }
            return NeonWidgets.CreateAlert(NeonTone.Cinnabar, stack);
        }

        if (row.OwnVersionMismatch != null)
        {
            var mm = row.OwnVersionMismatch;
            var preRow = s.AllRows.FirstOrDefault(r => r.Mod.Id.Equals(mm.PrereqId, StringComparison.OrdinalIgnoreCase));
            var stack = new StackPanel();
            var tb = new TextBlock { FontFamily = NeonPalette.SansFont, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            tb.Inlines.Add(new Run("版本差阻断：") { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run($"依据{mm.SourceNote}，要求 "));
            tb.Inlines.Add(new Run($"{mm.PrereqId} {mm.Operator} v{NeonVersionDiff.StripPrefix(mm.RequiredVersion)}") { FontFamily = NeonPalette.MonoFont, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run("，当前本地为 "));
            tb.Inlines.Add(new Run("v" + NeonVersionDiff.StripPrefix(mm.ActualVersion)) { FontFamily = NeonPalette.MonoFont, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run("。"));
            stack.Children.Add(tb);

            var diffPill = NeonWidgets.CreateVersionDiffPill(mm.ActualVersion, mm.RequiredVersion, mm.Operator);
            diffPill.HorizontalAlignment = HorizontalAlignment.Left;
            diffPill.Margin = new Thickness(0, 7, 0, 0);
            stack.Children.Add(diffPill);

            if (preRow is { HasUpdate: true })
            {
                string ver = preRow.GitHubResult?.LatestVersion is { Length: > 0 } lv
                    ? " 至 v" + NeonVersionDiff.StripPrefix(lv)
                    : "";
                var cta = NeonWidgets.CreateStyledButton(
                    this,
                    $"预览更新 {preRow.Mod.Id}{ver}",
                    "NeonDangerButton",
                    () => RequestPreviewUpdateMod?.Invoke(preRow.Mod),
                    "sm");
                cta.Margin = new Thickness(0, 8, 0, 0);
                cta.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(cta);
            }
            return NeonWidgets.CreateAlert(NeonTone.Cinnabar, stack);
        }

        if (row.CausingDisabledBlock != null)
        {
            var selfStatus = GetInlineToggleStatus(row.Mod.Id);
            if (selfStatus is { Phase: NeonInlineTogglePhase.AppliedScanFailed })
                return BuildInlineReceiptCard(row.Mod.Id, new NeonInspectorResolutionReceipt(
                    PrereqId: selfStatus.ModId,
                    PrereqName: selfStatus.ModName,
                    Tone: NeonTone.Amber,
                    Lead: "已恢复启用，依赖状态待核验：",
                    Body: selfStatus.Detail ?? $"{selfStatus.ModId} 已移回 mods/，请重新扫描刷新依赖与补丁状态。",
                    ShowRescanAction: true));

            var stack = new StackPanel();
            string victims = string.Join("、", row.InboundActiveBroken.Take(4).Select(e => NeonWidgets.ShortDisplayName(e.Dependent.Name)));
            var tb = new TextBlock { FontFamily = NeonPalette.SansFont, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            tb.Inlines.Add(new Run($"正在阻断 {row.InboundActiveBroken.Length} 个下游项目：") { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run($"因为本前置库目前停用，{victims} 无法正常满足依赖。恢复启用后，预计解除受影响模组的前置阻断。"));
            stack.Children.Add(tb);

            if (row.ToggleBlockReason == null)
            {
                bool pending = selfStatus?.Phase is NeonInlineTogglePhase.Applying or NeonInlineTogglePhase.AppliedScanPending;
                string ctaText = pending
                    ? (selfStatus!.Phase == NeonInlineTogglePhase.AppliedScanPending ? "正在核验依赖…" : "正在启用…")
                    : "恢复启用本前置库";
                var cta = NeonWidgets.CreateStyledButton(
                    this,
                    ctaText,
                    "NeonDangerButton",
                    () => RequestDirectToggleMod?.Invoke(row.Mod, row.Mod.Id),
                    "sm");
                cta.IsEnabled = !pending;
                cta.Margin = new Thickness(0, 8, 0, 0);
                cta.HorizontalAlignment = HorizontalAlignment.Left;
                AutomationProperties.SetName(cta, ctaText);
                if (pending) NeonMotion.StartSubtlePulse(this, cta);
                stack.Children.Add(cta);

                if (selfStatus is { Phase: NeonInlineTogglePhase.Failed })
                {
                    var failNote = NeonWidgets.Text(
                        "恢复启用未完成：" + (selfStatus.Detail ?? selfStatus.Message),
                        11.5, NeonPalette.AlertDangerInk, FontWeights.SemiBold, wrap: true);
                    failNote.Margin = new Thickness(0, 7, 0, 0);
                    stack.Children.Add(failNote);
                }
            }
            return NeonWidgets.CreateAlert(NeonTone.Cinnabar, stack);
        }

        if (row.CausingVersionMismatch != null)
        {
            var mm = row.CausingVersionMismatch;
            var stack = new StackPanel();
            var tb = new TextBlock { FontFamily = NeonPalette.SansFont, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            tb.Inlines.Add(new Run("细微版本差正在影响下游：") { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run($"当前 "));
            tb.Inlines.Add(new Run("v" + NeonVersionDiff.StripPrefix(mm.ActualVersion)) { FontFamily = NeonPalette.MonoFont, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run($"，{mm.SourceNote}要求 "));
            tb.Inlines.Add(new Run($"{mm.Operator} v{NeonVersionDiff.StripPrefix(mm.RequiredVersion)}") { FontFamily = NeonPalette.MonoFont, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run("。"));
            stack.Children.Add(tb);

            var diffPill = NeonWidgets.CreateVersionDiffPill(mm.ActualVersion, mm.RequiredVersion, mm.Operator);
            diffPill.HorizontalAlignment = HorizontalAlignment.Left;
            diffPill.Margin = new Thickness(0, 7, 0, 0);
            stack.Children.Add(diffPill);

            if (row.HasUpdate)
            {
                string ver = row.GitHubResult?.LatestVersion is { Length: > 0 } lv
                    ? $"至 v{NeonVersionDiff.StripPrefix(lv)}（自动快照）"
                    : "（自动快照）";
                var cta = NeonWidgets.CreateStyledButton(
                    this,
                    $"预览更新{ver}",
                    "NeonPrimaryButton",
                    () => RequestPreviewUpdateMod?.Invoke(row.Mod),
                    "sm");
                cta.Margin = new Thickness(0, 8, 0, 0);
                cta.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(cta);
            }
            return NeonWidgets.CreateAlert(NeonTone.Amber, stack);
        }

        var directSelfStatus = GetInlineToggleStatus(row.Mod.Id);
        if (directSelfStatus is { Phase: NeonInlineTogglePhase.AppliedScanFailed })
        {
            string halfLead = directSelfStatus.TargetEnable ? "已恢复启用，依赖状态待核验：" : "已移入停用区，依赖状态待核验：";
            return BuildInlineReceiptCard(row.Mod.Id, new NeonInspectorResolutionReceipt(
                PrereqId: directSelfStatus.ModId,
                PrereqName: directSelfStatus.ModName,
                Tone: NeonTone.Amber,
                Lead: halfLead,
                Body: directSelfStatus.Detail ?? directSelfStatus.Message,
                ShowRescanAction: true));
        }
        if (directSelfStatus is { Phase: NeonInlineTogglePhase.Failed })
        {
            bool runningNotice = directSelfStatus.Message.Contains("退出游戏");
            return BuildInlineReceiptCard(row.Mod.Id, new NeonInspectorResolutionReceipt(
                PrereqId: directSelfStatus.ModId,
                PrereqName: directSelfStatus.ModName,
                Tone: runningNotice ? NeonTone.Amber : NeonTone.Cinnabar,
                Lead: runningNotice ? "退出游戏后可启停：" : (directSelfStatus.TargetEnable ? "恢复启用未完成：" : "停用未完成："),
                Body: directSelfStatus.Detail ?? directSelfStatus.Message,
                ShowRescanAction: false));
        }

        if (inspectorReceipts.TryGetValue(row.Mod.Id, out var receipt))
            return BuildInlineReceiptCard(row.Mod.Id, receipt);

        return null;
    }

    Border BuildInlineReceiptCard(string modId, NeonInspectorResolutionReceipt receipt)
    {
        var headGrid = new Grid();
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var content = new StackPanel();
        var tb = new TextBlock { FontFamily = NeonPalette.SansFont, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        if (receipt.Tone == NeonTone.Sage)
            tb.Inlines.Add(new Run("✓ ") { FontWeight = FontWeights.Bold });
        tb.Inlines.Add(new Run(receipt.Lead) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(receipt.Body))
            tb.Inlines.Add(new Run(receipt.Body));
        content.Children.Add(tb);

        if (receipt.ShowRescanAction)
        {
            var rescanBtn = NeonWidgets.CreateStyledButton(
                this,
                "重新扫描",
                "NeonSecondaryButton",
                () => RequestRescanAll?.Invoke(),
                "xs");
            rescanBtn.Margin = new Thickness(0, 8, 0, 0);
            rescanBtn.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(rescanBtn, "重新扫描核验依赖");
            content.Children.Add(rescanBtn);
        }

        headGrid.Children.Add(content);

        if (!receipt.ShowRescanAction)
        {
            var dismissBtn = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Content = "×",
                FontSize = 13,
                Foreground = receipt.Tone == NeonTone.Sage ? NeonPalette.SageAlertInk : NeonPalette.InkSecondary,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, -2, -2, 0),
                ToolTip = "收起此条结果提示",
                Template = CreateRoundedButtonTemplate(4, NeonPalette.BgWell, Brushes.Transparent)
            };
            AutomationProperties.SetName(dismissBtn, "收起结果提示");
            dismissBtn.Click += (_, _) => ClearInspectorReceipt(modId, rerender: true);
            Grid.SetColumn(dismissBtn, 1);
            headGrid.Children.Add(dismissBtn);
        }

        var card = NeonWidgets.CreateAlert(receipt.Tone, headGrid);
        if (animatedReceiptIds.Add(modId))
            NeonMotion.EnterInlineCard(this, card);
        return card;
    }

    UIElement BuildCausalChainSection(NeonWorkbenchRow row, NeonWorkbenchSnapshot s)
    {
        var section = new StackPanel();
        section.Children.Add(InspectorSectionTitle("因果依赖链（点击节点跳转）", "前置 ──▶ 当前 ──▶ 影响"));

        var chainBox = new Border
        {
            Background = NeonPalette.BgGround,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 6)
        };
        var chainStack = new StackPanel();

        // Tier 1: Upstream prerequisites
        chainStack.Children.Add(NeonWidgets.Text($"↑ 它依赖的前置（{row.OwnEdges.Length}）", 11, NeonPalette.InkMuted));
        if (row.OwnEdges.Length == 0)
        {
            var none = NeonWidgets.Text("未声明必需前置（基础独立项）", 12, NeonPalette.InkMuted);
            none.Margin = new Thickness(4, 5, 0, 2);
            chainStack.Children.Add(none);
        }
        else
        {
            foreach (var edge in row.OwnEdges)
            {
                FrameworkElement badge;
                if (edge.Status == ModDependencyStatus.VersionMismatch && edge.Prerequisite != null)
                {
                    bool isMax = string.IsNullOrWhiteSpace(edge.MinimumVersion) && !string.IsNullOrWhiteSpace(edge.MaximumVersion);
                    badge = NeonWidgets.CreateVersionDiffPill(
                        edge.Prerequisite.Version,
                        isMax ? edge.MaximumVersion : edge.MinimumVersion,
                        isMax ? "≤" : "≥");
                }
                else if (edge.Status == ModDependencyStatus.DisabledPrerequisite)
                {
                    badge = NeonWidgets.CreateBadge("已停用", NeonTone.Cinnabar);
                }
                else if (edge.Status == ModDependencyStatus.Missing)
                {
                    badge = NeonWidgets.CreateBadge("未安装", NeonTone.Cinnabar);
                }
                else if (edge.IsIssue)
                {
                    badge = NeonWidgets.CreateBadge(ModDependencyGraphPresentation.Status(edge.Status), NeonTone.Cinnabar);
                }
                else
                {
                    badge = NeonWidgets.CreateBadge("v" + NeonVersionDiff.StripPrefix(edge.Prerequisite?.Version ?? "?") + " ✓", NeonTone.Sage);
                }

                string sub = edge.DependencyId + (edge.MinimumVersion.Length > 0 ? $" · 要求 ≥ {edge.MinimumVersion}" : "");
                var node = BuildChainNode(
                    title: edge.DependencyName,
                    sub: sub,
                    badge: badge,
                    broken: edge.IsIssue,
                    current: false,
                    onClick: edge.Prerequisite != null ? () => SelectMod(edge.Prerequisite.Id) : null);
                node.Margin = new Thickness(0, 5, 0, 0);
                chainStack.Children.Add(node);
            }
        }

        // Connector
        chainStack.Children.Add(ChainConnector());

        // Tier 2: Current MOD
        var currNode = BuildChainNode(
            title: row.Mod.Name,
            sub: $"当前检视 · v{NeonVersionDiff.StripPrefix(row.Mod.Version)}",
            badge: NeonWidgets.CreateBadge(row.Mod.Enabled ? "启用" : "停用", row.Mod.Enabled ? NeonTone.Sage : NeonTone.Neutral),
            broken: false,
            current: true,
            onClick: null);
        chainStack.Children.Add(currNode);

        // Connector
        chainStack.Children.Add(ChainConnector());

        // Tier 3: Downstream dependents
        chainStack.Children.Add(NeonWidgets.Text($"↓ 依赖它的模组与补丁（{row.InboundEdges.Length}）", 11, NeonPalette.InkMuted));
        if (row.InboundEdges.Length == 0)
        {
            var none = NeonWidgets.Text("暂无其他启用 MOD 或补丁声明依赖本项", 12, NeonPalette.InkMuted);
            none.Margin = new Thickness(4, 5, 0, 0);
            chainStack.Children.Add(none);
        }
        else
        {
            foreach (var edge in row.InboundEdges)
            {
                bool isPatch = ModPresentation.IsIndependentPatch(edge.Dependent);
                string title = isPatch ? "[补丁] " + edge.Dependent.Name : edge.Dependent.Name;
                string sub = edge.IsActiveIssue
                    ? edge.Message
                    : isPatch ? "已收录兼容补丁" : edge.Dependent.Enabled ? "启用中 · 依赖满足" : "已停用";
                FrameworkElement badge = NeonWidgets.CreateBadge(
                    edge.IsActiveIssue ? "受阻" : isPatch ? "补丁" : "依赖",
                    edge.IsActiveIssue ? NeonTone.Cinnabar : isPatch ? NeonTone.Indigo : NeonTone.Neutral);

                var node = BuildChainNode(
                    title: title,
                    sub: sub,
                    badge: badge,
                    broken: edge.IsActiveIssue,
                    current: false,
                    onClick: isPatch ? () => SwitchTab("patches") : () => SelectMod(edge.Dependent.Id));
                node.Margin = new Thickness(0, 5, 0, 0);
                chainStack.Children.Add(node);
            }
        }

        chainBox.Child = chainStack;
        section.Children.Add(chainBox);
        section.Children.Add(NeonWidgets.Text("只读分析范围：MOD 清单声明及已收录补丁规则；未声明的运行时依赖无法自动推断。", 11, NeonPalette.InkMuted, wrap: true));
        return section;
    }

    /// <summary>
    /// Displays associated curated patches with exact Side-by-Side [Patch Status] + [Patch Type] badges,
    /// immediate withdrawal of stale "校验通过" marks, and real PatchLifecycle retirement preflight!
    /// </summary>
    UIElement BuildRelatedPatchesSection(NeonWorkbenchRow row, NeonWorkbenchSnapshot s)
    {
        var section = new StackPanel();
        string healthMeta = s.IsLifecycleHealthValid ? "健康核验有效"
            : s.LifecycleStale || s.ModInventoryDirty ? "健康结论已过期"
            : "待补丁健康核验";
        section.Children.Add(InspectorSectionTitle($"关联修复补丁（{row.RelatedPatches.Length}）", healthMeta));

        foreach (var patch in row.RelatedPatches)
        {
            var card = new Border
            {
                Background = NeonPalette.BgElevated,
                BorderBrush = patch.CanRetire ? NeonPalette.SageBorder
                    : patch.StatusTone == NeonTone.Cinnabar ? NeonPalette.CinnabarBorder
                    : patch.StatusTone == NeonTone.Amber ? NeonPalette.AmberBorder
                    : NeonPalette.BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 8, 0, 0)
            };

            var stack = new StackPanel();
            var titleRow = new DockPanel();
            var verTb = NeonWidgets.Text("v" + NeonVersionDiff.StripPrefix(patch.Version), 11, NeonPalette.InkMuted, mono: true);
            verTb.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(verTb, Dock.Right);
            titleRow.Children.Add(verTb);
            titleRow.Children.Add(NeonWidgets.Text(patch.Name, 12.5, NeonPalette.InkPrimary, FontWeights.SemiBold, wrap: true));
            stack.Children.Add(titleRow);

            // Side-by-side Patch Status + Patch Type strip!
            var statusAndType = NeonWidgets.CreatePatchStatusAndTypeStrip(
                patch.StatusLabel,
                patch.StatusTone,
                patch.IsStandalone,
                statusToolTip: patch.VerdictText);
            statusAndType.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(statusAndType);

            // Reviewed rule version diff if applicable
            if (patch.ReviewedRequiredVersion != null &&
                patch.ReviewedRequiredModId != null &&
                patch.ReviewedRequiredModId.Equals(row.Mod.Id, StringComparison.OrdinalIgnoreCase))
            {
                var diffPill = NeonWidgets.CreateVersionDiffPill(
                    row.Mod.Version,
                    patch.ReviewedRequiredVersion,
                    "≥",
                    prefix: "审核规则 " + row.Mod.Id,
                    toolTip: $"依据 patch-catalog.json 已审核适配规则（{patch.Name}）");
                diffPill.HorizontalAlignment = HorizontalAlignment.Left;
                diffPill.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(diffPill);
            }

            var verdictTb = NeonWidgets.Text(patch.VerdictText, 11.5, NeonPalette.InkSecondary, wrap: true);
            verdictTb.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(verdictTb);

            var actionRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var evBtn = NeonWidgets.CreateStyledButton(this, "查看判断依据", "NeonSecondaryButton",
                () => OpenPatchEvidenceModal(patch, s), "xs");
            evBtn.Margin = new Thickness(0, 0, 6, 0);
            actionRow.Children.Add(evBtn);

            if (patch.CanRetire)
            {
                var retireBtn = NeonWidgets.CreateStyledButton(this, "预览安全退役", "NeonPrimaryButton",
                    () => RequestPreviewRetirePatch?.Invoke(patch), "xs",
                    "仅当本地原 MOD 文件精确匹配已审核上游修复 SHA-256 时可用；移入保留区可恢复");
                AutomationProperties.SetName(retireBtn, "预览安全退役 " + patch.PatchId);
                actionRow.Children.Add(retireBtn);
            }
            else if (s.LifecycleStale || s.ModInventoryDirty)
            {
                var recheckBtn = NeonWidgets.CreateStyledButton(this, "重新核验健康", "NeonGhostButton",
                    () => RequestRescanAndVerifyHealth?.Invoke(), "xs");
                actionRow.Children.Add(recheckBtn);
            }
            stack.Children.Add(actionRow);

            card.Child = stack;
            section.Children.Add(card);
        }

        return section;
    }

    UIElement BuildSourceSection(NeonWorkbenchRow row)
    {
        var section = new StackPanel();
        section.Children.Add(InspectorSectionTitle("更新来源与核验", null));

        var card = new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 8, 0, 0)
        };
        var stack = new StackPanel();

        if (row.GitHubRepo != null)
        {
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            left.Children.Add(NeonWidgets.Text("GitHub 公开仓库", 12, NeonPalette.InkPrimary, FontWeights.SemiBold));
            left.Children.Add(NeonWidgets.Text(row.GitHubRepo, 11.5, NeonPalette.InkSecondary, mono: true, wrap: true));
            top.Children.Add(left);

            string st = row.GitHubResult?.Status ?? "尚未检查";
            NeonTone tone = st == "有更新" ? NeonTone.Amber : st == "已是最新" ? NeonTone.Sage : NeonTone.Neutral;
            var badge = NeonWidgets.CreateBadge(st, tone);
            badge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, 1);
            top.Children.Add(badge);
            stack.Children.Add(top);

            if (!string.IsNullOrWhiteSpace(row.GitHubResult?.Message))
            {
                var msg = NeonWidgets.Text(row.GitHubResult.Message, 11.5, NeonPalette.InkSecondary, wrap: true);
                msg.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(msg);
            }

            var btns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (row.GitHubResult?.Status == "有更新")
            {
                string ver = row.GitHubResult.LatestVersion.Length > 0 ? $" (v{NeonVersionDiff.StripPrefix(row.GitHubResult.LatestVersion)})" : "";
                var upd = NeonWidgets.CreateStyledButton(this, "预览下载并更新" + ver, "NeonPrimaryButton",
                    () => RequestPreviewUpdateMod?.Invoke(row.Mod), "sm");
                upd.Margin = new Thickness(0, 0, 7, 4);
                btns.Children.Add(upd);
            }
            else
            {
                var chk = NeonWidgets.CreateStyledButton(this, "检查更新", "NeonSecondaryButton",
                    () => RequestCheckModUpdate?.Invoke(row.Mod), "xs");
                chk.Margin = new Thickness(0, 0, 7, 4);
                btns.Children.Add(chk);
            }
            var unbind = NeonWidgets.CreateStyledButton(this, "解除仓库关联", "NeonGhostButton",
                () => RequestUnbindModSource?.Invoke(row.Mod), "xs");
            btns.Children.Add(unbind);
            stack.Children.Add(btns);
        }
        else if (row.WorkshopId.HasValue)
        {
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            left.Children.Add(NeonWidgets.Text("Steam 创意工坊 / 尖塔补给站", 12, NeonPalette.InkPrimary, FontWeights.SemiBold));
            left.Children.Add(NeonWidgets.Text($"WS #{row.WorkshopId.Value}", 11.5, NeonPalette.InkSecondary, mono: true));
            top.Children.Add(left);

            string st = row.WorkshopResult?.Status ?? "尚未检查";
            NeonTone tone = st == "有更新" ? NeonTone.Amber : st == "已是最新" ? NeonTone.Sage : NeonTone.Neutral;
            var badge = NeonWidgets.CreateBadge(st, tone);
            badge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, 1);
            top.Children.Add(badge);
            stack.Children.Add(top);

            if (!string.IsNullOrWhiteSpace(row.WorkshopResult?.Message))
            {
                var msg = NeonWidgets.Text(row.WorkshopResult.Message, 11.5, NeonPalette.InkSecondary, wrap: true);
                msg.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(msg);
            }

            var btns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (row.WorkshopResult?.Status == "有更新")
            {
                var upd = NeonWidgets.CreateStyledButton(this, "下载并预览更新", "NeonPrimaryButton",
                    () => RequestPreviewUpdateMod?.Invoke(row.Mod), "sm");
                upd.Margin = new Thickness(0, 0, 7, 4);
                btns.Children.Add(upd);
            }
            else
            {
                var chk = NeonWidgets.CreateStyledButton(this, "检查更新", "NeonSecondaryButton",
                    () => RequestCheckModUpdate?.Invoke(row.Mod), "xs");
                chk.Margin = new Thickness(0, 0, 7, 4);
                btns.Children.Add(chk);
            }
            btns.Children.Add(NeonWidgets.CreateStyledButton(this, "解除工坊关联", "NeonGhostButton",
                () => RequestUnbindModSource?.Invoke(row.Mod), "xs"));
            stack.Children.Add(btns);
        }
        else if (row.CandidateSource != null)
        {
            var cand = row.CandidateSource;
            var noteTb = new TextBlock { FontFamily = NeonPalette.SansFont, FontSize = 12, Foreground = NeonPalette.InkPrimary, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            noteTb.Inlines.Add(new Run("发现候选来源：") { FontWeight = FontWeights.SemiBold });
            noteTb.Inlines.Add(new Run(cand.Note));
            stack.Children.Add(noteTb);

            var targetTb = NeonWidgets.Text(cand.TargetLabel, 11.5, NeonPalette.InkSecondary, mono: true);
            targetTb.Margin = new Thickness(0, 5, 0, 8);
            stack.Children.Add(targetTb);

            var confirmBtn = NeonWidgets.CreateStyledButton(this, "核对无误 · 确认关联", "NeonPrimaryButton", () =>
            {
                OpenTransactionModal(new NeonTransactionConfig(
                    Title: $"确认关联更新来源 · {row.Mod.Name}",
                    Subtitle: "来源关联按游戏目录保存在管理器数据区，不改写 MOD 文件",
                    GameRunning: false,
                    BlockedReason: null,
                    AutoSnapshotNote: null,
                    BoundaryNote: cand.IsAmbiguous
                        ? "此条目在公共来源索引中标记为同名/泛化候选，请确认作者一致后再绑定。"
                        : "已核对候选来源标识，绑定仅用于只读更新检测，不修改 MOD 文件。",
                    Token: "BIND-SOURCE",
                    Groups: [new NeonDiffGroup("关联内容", [new NeonDiffLine("add", "绑定", cand.TargetLabel, "仅影响更新检测")])],
                    ConfirmLabel: "确认关联此来源",
                    OnConfirmAsync: () =>
                    {
                        if (cand.GitHubRepository != null) RequestQuickBindGitHub?.Invoke(row.Mod, cand.GitHubRepository);
                        else if (cand.WorkshopId.HasValue) RequestQuickBindWorkshop?.Invoke(row.Mod, cand.WorkshopId.Value);
                        return Task.CompletedTask;
                    }));
            }, "sm");
            confirmBtn.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(confirmBtn);
        }
        else
        {
            stack.Children.Add(NeonWidgets.Text("当前尚未关联 GitHub 或创意工坊来源，可手动输入关联：", 12, NeonPalette.InkSecondary, wrap: true));
            var inputGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var input = new TextBox
            {
                Height = 30,
                FontFamily = NeonPalette.MonoFont,
                FontSize = 12,
                Foreground = NeonPalette.InkPrimary,
                Background = NeonPalette.BgElevated,
                BorderBrush = NeonPalette.BorderMedium,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "输入 GitHub 仓库（作者/仓库）或数字 Workshop ID",
                Template = CreateSearchBoxTemplate()
            };
            inputGrid.Children.Add(input);

            var bindBtn = NeonWidgets.CreateStyledButton(this, "绑定", "NeonSecondaryButton", () =>
            {
                string raw = input.Text.Trim();
                if (Regex.IsMatch(raw, @"^\d{6,14}$") && ulong.TryParse(raw, out ulong ws))
                    RequestQuickBindWorkshop?.Invoke(row.Mod, ws);
                else if (GitHubModUpdates.TryNormalize(raw, out string repo))
                    RequestQuickBindGitHub?.Invoke(row.Mod, repo);
                else
                    ShowToast("格式无效：请输入 作者/仓库、GitHub 链接或 6–14 位数字 Workshop ID", NeonTone.Cinnabar);
            }, "sm");
            bindBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(bindBtn, 1);
            inputGrid.Children.Add(bindBtn);
            stack.Children.Add(inputGrid);
        }

        card.Child = stack;
        section.Children.Add(card);
        return section;
    }

    UIElement BuildFilesAndBackupsSection(NeonWorkbenchRow row, NeonWorkbenchSnapshot s)
    {
        var section = new StackPanel();
        section.Children.Add(InspectorSectionTitle("文件与独立历史备份", null));

        var card = new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 8, 0, 0)
        };
        var stack = new StackPanel();

        string desc = string.IsNullOrWhiteSpace(row.Mod.Description) ? "作者未在清单中填写简介。" : row.Mod.Description;
        stack.Children.Add(NeonWidgets.Text(desc, 12, NeonPalette.InkSecondary, wrap: true));

        var kv = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        string relPath = s.GameRoot != null
            ? Path.GetRelativePath(s.GameRoot, row.Mod.Folder)
            : row.Mod.Folder;

        kv.Children.Add(NeonWidgets.Text("所在目录", 11.5, NeonPalette.InkMuted));
        var pathV = NeonWidgets.Text(relPath, 11.5, NeonPalette.InkPrimary, mono: true, wrap: true);
        Grid.SetColumn(pathV, 1);
        kv.Children.Add(pathV);

        var sizeK = NeonWidgets.Text("文件体积", 11.5, NeonPalette.InkMuted);
        sizeK.Margin = new Thickness(0, 5, 0, 0);
        Grid.SetRow(sizeK, 1);
        kv.Children.Add(sizeK);
        var sizeV = NeonWidgets.Text($"{row.FileCount} 文件 · {NeonWorkbenchBuilder.FormatBytes(row.TotalBytes)}", 11.5, NeonPalette.InkPrimary, mono: true);
        sizeV.Margin = new Thickness(0, 5, 0, 0);
        Grid.SetRow(sizeV, 1);
        Grid.SetColumn(sizeV, 1);
        kv.Children.Add(sizeV);
        stack.Children.Add(kv);

        var quickLinks = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var openFolder = NeonWidgets.CreateStyledButton(this, "打开模组目录", "NeonGhostButton",
            () => RequestOpenModFolder?.Invoke(row.Mod), "xs");
        openFolder.Margin = new Thickness(0, 0, 6, 0);
        quickLinks.Children.Add(openFolder);
        stack.Children.Add(quickLinks);

        // Real single-MOD backups from GitHubModInstaller.Backups
        if (row.Mod.Enabled && s.GameRoot != null)
        {
            string[] backups = [];
            try { backups = GitHubModInstaller.Backups(s.GameRoot, row.Mod); } catch { }
            if (backups.Length > 0)
            {
                var bkBlock = new Border
                {
                    BorderBrush = NeonPalette.BorderSubtle,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 9, 0, 0),
                    Padding = new Thickness(0, 8, 0, 0)
                };
                var bkStack = new StackPanel();
                bkStack.Children.Add(NeonWidgets.Text($"单 MOD 历史版本备份（{backups.Length} 份）", 12, NeonPalette.InkPrimary, FontWeights.SemiBold));
                for (int i = 0; i < Math.Min(3, backups.Length); i++)
                {
                    string name = Path.GetFileName(backups[i]) + (i == 0 ? " (最近一份·始终保留)" : "");
                    var line = NeonWidgets.Text(name, 11.5, NeonPalette.InkSecondary, mono: true, ellipsis: true);
                    line.Margin = new Thickness(0, 4, 0, 0);
                    bkStack.Children.Add(line);
                }
                var restoreBtn = NeonWidgets.CreateStyledButton(this, "恢复上次备份", "NeonSecondaryButton",
                    () => RequestRestoreModBackup?.Invoke(row.Mod), "xs");
                restoreBtn.Margin = new Thickness(0, 8, 0, 0);
                restoreBtn.HorizontalAlignment = HorizontalAlignment.Left;
                bkStack.Children.Add(restoreBtn);
                bkBlock.Child = bkStack;
                stack.Children.Add(bkBlock);
            }
        }

        card.Child = stack;
        section.Children.Add(card);
        return section;
    }

    static Grid InspectorSectionTitle(string left, string? right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(NeonWidgets.Text(left, 11.5, NeonPalette.InkMuted, FontWeights.Bold));
        if (!string.IsNullOrEmpty(right))
        {
            var r = NeonWidgets.Text(right, 11, NeonPalette.InkMuted);
            Grid.SetColumn(r, 1);
            g.Children.Add(r);
        }
        return g;
    }

    static TextBlock InspectorDotSep()
    {
        var tb = NeonWidgets.Text("·", 11.5, NeonPalette.InkMuted);
        tb.Margin = new Thickness(5, 0, 5, 0);
        return tb;
    }

    static TextBlock ChainConnector()
    {
        var tb = NeonWidgets.Text("▼", 10, NeonPalette.InkMuted);
        tb.HorizontalAlignment = HorizontalAlignment.Center;
        tb.Margin = new Thickness(0, 6, 0, 6);
        return tb;
    }

    Button BuildChainNode(string title, string sub, FrameworkElement badge, bool broken, bool current, Action? onClick)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        left.Children.Add(NeonWidgets.Text(title, 12.5, NeonPalette.InkPrimary, FontWeights.SemiBold, ellipsis: true));
        left.Children.Add(NeonWidgets.Text(sub, 11, NeonPalette.InkSecondary, mono: current, ellipsis: true));
        g.Children.Add(left);

        badge.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(badge, 1);
        g.Children.Add(badge);

        Brush borderBrush = current ? NeonPalette.Sage : broken ? NeonPalette.CinnabarBorder : NeonPalette.BorderSubtle;
        Brush bgBrush = current ? NeonPalette.SageBg : broken ? NeonPalette.CinnabarBg : NeonPalette.BgElevated;

        var box = new Border
        {
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 6, 9, 6),
            Child = g
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
            btn.MouseLeave += (_, _) => box.BorderBrush = borderBrush;
            btn.Click += (_, _) => onClick();
        }
        return btn;
    }

    void OpenPatchEvidenceModal(NeonRelatedPatch patch, NeonWorkbenchSnapshot s)
    {
        var lines = new List<NeonDiffLine>
        {
            new("skip", "核验状态", s.IsLifecycleHealthValid ? "当前有效" : "已过期或未完成健康核验",
                s.LastScanTime?.ToString("HH:mm:ss") ?? "尚未扫描"),
            new("mod", "补丁类型", patch.IsStandalone ? "独立补丁（独立目录，可逆停用）" : "原 MOD 内置修复（修改原文件）", patch.StatusLabel)
        };
        if (patch.LifecycleItem?.Evidence is { } ev)
        {
            lines.Add(new("add", "SHA-256", ev.Sha256, $"{ev.TargetModId}/{ev.RelativePath} ({ev.Version})"));
            lines.Add(new("add", "审核决策", $"{ev.Decision} · {ev.ReviewStatus}", "已审核上游证据"));
        }
        else if (patch.ScanState?.Evidence is { Length: > 0 } scanEv)
        {
            foreach (string line in scanEv.Take(3))
                lines.Add(new("skip", "扫描依据", line, ""));
        }

        OpenTransactionModal(new NeonTransactionConfig(
            Title: $"补丁健康与判断依据 · {patch.Name}",
            Subtitle: $"{patch.PatchId} · v{NeonVersionDiff.StripPrefix(patch.Version)} · {(patch.IsStandalone ? "独立补丁" : "原 MOD 内置修复")}",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: patch.VerdictText,
            Token: s.IsLifecycleHealthValid ? "VERIFIED" : "STALE",
            Groups: [new NeonDiffGroup("最近一次扫描与证据记录", lines)],
            ConfirmLabel: "",
            CancelLabel: "关闭",
            FootNote: "文件指纹扫描只核对已收录条件，不替代真实游戏运行验证。",
            PlainSummary: patch.VerdictText,
            ExpandTechnicalDetailsByDefault: true));
    }

    // ========================================================================
    // UNIFIED TRANSACTION PREVIEW MODAL OVERLAY
    // ========================================================================

    (Border Backdrop, Border Sheet) BuildTransactionModalOverlay()
    {
        var backdrop = new Border
        {
            Background = NeonPalette.ModalBackdrop,
            Padding = new Thickness(20),
            Visibility = Visibility.Collapsed
        };

        var headGrid = new Grid();
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(modalTitleText);
        modalSubText.Margin = new Thickness(0, 3, 0, 0);
        titles.Children.Add(modalSubText);
        headGrid.Children.Add(titles);

        var closeBtn = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Content = "×",
            FontSize = 16,
            Foreground = NeonPalette.InkSecondary,
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "关闭弹窗 (Esc)",
            Template = CreateRoundedButtonTemplate(6, NeonPalette.BgElevated, NeonPalette.BorderMedium)
        };
        closeBtn.Click += (_, _) => CloseTransactionModal();
        Grid.SetColumn(closeBtn, 1);
        headGrid.Children.Add(closeBtn);

        var headBorder = new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 15, 18, 13),
            Child = headGrid
        };

        var bodyScroll = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(18, 15, 18, 15), Child = modalBodyStack },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 460
        };

        var footGrid = new Grid();
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modalFootNoteText.VerticalAlignment = VerticalAlignment.Center;
        modalFootNoteText.Margin = new Thickness(0, 0, 12, 0);
        footGrid.Children.Add(modalFootNoteText);
        Grid.SetColumn(modalFootButtons, 1);
        footGrid.Children.Add(modalFootButtons);

        var footBorder = new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 12, 18, 12),
            Child = footGrid
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(headBorder);
        Grid.SetRow(bodyScroll, 1);
        layout.Children.Add(bodyScroll);
        Grid.SetRow(footBorder, 2);
        layout.Children.Add(footBorder);

        var sheet = new Border
        {
            Width = 580,
            MaxWidth = 580,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderMedium,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Child = layout
        };
        var sheetShadow = new Border
        {
            Width = 580,
            MaxWidth = 580,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = NeonPalette.BgSurface,
            CornerRadius = new CornerRadius(14),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#141816"),
                BlurRadius = 48,
                ShadowDepth = 12,
                Opacity = 0.24
            }
        };
        var sheetHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        sheetHost.Children.Add(sheetShadow);
        sheetHost.Children.Add(sheet);

        backdrop.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, backdrop)) CloseTransactionModal();
        };
        backdrop.Child = sheetHost;
        return (backdrop, sheet);
    }

    public void OpenTransactionModal(NeonTransactionConfig config)
    {
        modalTitleText.Text = config.Title;
        modalSubText.Text = config.Subtitle;
        modalBodyStack.Children.Clear();
        modalFootButtons.Children.Clear();
        activeModalConfirmAsync = config.OnConfirmAsync;
        modalExecuting = false;

        bool blocked = !string.IsNullOrWhiteSpace(config.BlockedReason);

        void AddAlertBlock(NeonTone tone, string lead, string text, Panel? target = null)
        {
            var tb = new TextBlock { FontFamily = NeonPalette.SansFont, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            if (!string.IsNullOrWhiteSpace(lead))
                tb.Inlines.Add(new Run(lead) { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run(text));
            var alert = NeonWidgets.CreateAlert(tone, tb);
            alert.Margin = new Thickness(0, 0, 0, 10);
            (target ?? modalBodyStack).Children.Add(alert);
        }

        // 1. Hard block / game-running warning upfront
        if (blocked)
            AddAlertBlock(NeonTone.Cinnabar, "无法执行此操作：", config.BlockedReason!);
        else if (config.GameRunning)
            AddAlertBlock(NeonTone.Cinnabar, "游戏正在运行：", "请先完全退出《杀戮尖塔 2》，再执行文件改动。");

        // 2. Human-first impact summary (plain Chinese, zero jargon upfront!)
        string? primarySummary = !string.IsNullOrWhiteSpace(config.PlainSummary)
            ? config.PlainSummary
            : config.BoundaryNote;
        if (!string.IsNullOrWhiteSpace(primarySummary))
        {
            var summaryTb = NeonWidgets.Text(primarySummary!, 13, NeonPalette.InkPrimary, wrap: true);
            summaryTb.LineHeight = 20;
            var summaryBox = new Border
            {
                Background = NeonPalette.BgElevated,
                BorderBrush = config.Danger ? NeonPalette.CinnabarBorder : NeonPalette.BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = summaryTb
            };
            modalBodyStack.Children.Add(summaryBox);
        }

        // 3. Concise Sage safety guarantee (auto snapshot before write)
        if (!string.IsNullOrWhiteSpace(config.AutoSnapshotNote))
            AddAlertBlock(NeonTone.Sage, "✓ 自动保护：", config.AutoSnapshotNote!);

        // 4. Interactive custom content (e.g. snapshot purpose input or exported JSON) — always visible!
        if (config.CustomContent != null)
        {
            if (config.CustomContent is FrameworkElement customFe)
                customFe.Margin = new Thickness(0, 0, 0, 10);
            modalBodyStack.Children.Add(config.CustomContent);
        }

        // 5. Technical Details (Preflight chips, Token, Raw File/Path/SHA-256 Diff Groups)
        // Collapsed by default on retained write modals; expanded when diagnostic/list-centric.
        bool hasSecondaryBoundary = !string.IsNullOrWhiteSpace(config.PlainSummary) &&
                                    !string.IsNullOrWhiteSpace(config.BoundaryNote) &&
                                    !string.Equals(config.PlainSummary.Trim(), config.BoundaryNote.Trim(), StringComparison.Ordinal);
        bool hasTechnicalDetails = config.Groups.Count > 0 || !string.IsNullOrWhiteSpace(config.Token) || hasSecondaryBoundary;
        if (hasTechnicalDetails)
        {
            var techBody = new StackPanel();

            var pfBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var gameChip = NeonWidgets.CreateBadge(
                config.GameRunning ? "✕ 游戏正在运行" : "✓ 游戏已退出",
                config.GameRunning ? NeonTone.Cinnabar : NeonTone.Sage);
            gameChip.Margin = new Thickness(0, 0, 6, 4);
            pfBar.Children.Add(gameChip);

            var checkChip = NeonWidgets.CreateBadge(
                blocked ? "✕ 预检未通过" : "✓ 清单与文件指纹复核",
                blocked ? NeonTone.Cinnabar : NeonTone.Sage);
            checkChip.Margin = new Thickness(0, 0, 6, 4);
            pfBar.Children.Add(checkChip);

            if (!string.IsNullOrWhiteSpace(config.Token))
            {
                string shortToken = config.Token.Length > 10 ? config.Token[..10] : config.Token;
                var tokenChip = new Border
                {
                    Background = NeonPalette.BgWell,
                    BorderBrush = NeonPalette.BorderSubtle,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(0, 0, 0, 4),
                    Child = NeonWidgets.Text("令牌 " + shortToken, 11, NeonPalette.InkSecondary, mono: true)
                };
                pfBar.Children.Add(tokenChip);
            }
            techBody.Children.Add(pfBar);

            if (hasSecondaryBoundary)
            {
                var noteTb = NeonWidgets.Text("边界说明：" + config.BoundaryNote!, 11.5, NeonPalette.InkSecondary, wrap: true);
                noteTb.Margin = new Thickness(0, 0, 0, 8);
                techBody.Children.Add(noteTb);
            }

            for (int gi = 0; gi < config.Groups.Count; gi++)
            {
                var box = NeonWidgets.CreateDiffBox(config.Groups[gi]);
                box.Margin = new Thickness(0, 0, 0, gi < config.Groups.Count - 1 ? 8 : 0);
                techBody.Children.Add(box);
            }

            if (config.ExpandTechnicalDetailsByDefault)
            {
                techBody.Margin = new Thickness(0, 2, 0, 6);
                modalBodyStack.Children.Add(techBody);
            }
            else
            {
                int totalDiffLines = config.Groups.Sum(g => g.Lines.Count);
                string leftHint = blocked
                    ? "预检与校验记录"
                    : totalDiffLines > 0
                        ? $"✓ 已通过安全预检 · 涉及 {totalDiffLines} 项路径/文件记录"
                        : "✓ 已通过安全预检与指纹复核";

                bool expanded = false;
                var chevronText = NeonWidgets.Text("查看路径与校验细节 ▾", 11.5, NeonPalette.Sage, FontWeights.Medium);
                chevronText.VerticalAlignment = VerticalAlignment.Center;

                var headerGrid = new Grid();
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var leftTb = NeonWidgets.Text(leftHint, 11.5, NeonPalette.InkSecondary, ellipsis: true);
                leftTb.VerticalAlignment = VerticalAlignment.Center;
                headerGrid.Children.Add(leftTb);
                Grid.SetColumn(chevronText, 1);
                chevronText.Margin = new Thickness(10, 0, 0, 0);
                headerGrid.Children.Add(chevronText);

                var techHost = new Border
                {
                    BorderBrush = NeonPalette.BorderSubtle,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Background = NeonPalette.BgGround,
                    Padding = new Thickness(11, 10, 11, 10),
                    Visibility = Visibility.Collapsed,
                    Child = techBody
                };

                var toggleBtn = new Button
                {
                    Content = headerGrid,
                    Background = NeonPalette.BgWell,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(11, 8, 11, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Cursor = Cursors.Hand,
                    Template = CreateRoundedButtonTemplate(0, NeonPalette.BgHover, Brushes.Transparent)
                };
                AutomationProperties.SetName(toggleBtn, "展开或收起技术细节");
                toggleBtn.Click += (_, _) =>
                {
                    expanded = !expanded;
                    techHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                    chevronText.Text = expanded ? "收起技术细节 ▴" : "查看路径与校验细节 ▾";
                    if (expanded) NeonMotion.EnterInlineCard(this, techHost);
                };

                var drawerStack = new StackPanel();
                drawerStack.Children.Add(toggleBtn);
                drawerStack.Children.Add(techHost);

                var drawerBox = new Border
                {
                    Background = NeonPalette.BgElevated,
                    BorderBrush = NeonPalette.BorderSubtle,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    ClipToBounds = true,
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = drawerStack
                };
                modalBodyStack.Children.Add(drawerBox);
            }
        }

        modalFootNoteText.Text = config.FootNote;

        var cancelBtn = NeonWidgets.CreateStyledButton(this, config.CancelLabel, "NeonSecondaryButton", CloseTransactionModal);
        AutomationProperties.SetName(cancelBtn, config.CancelLabel);
        modalFootButtons.Children.Add(cancelBtn);

        if (config.OnConfirmAsync != null && !string.IsNullOrWhiteSpace(config.ConfirmLabel))
        {
            var confirmBtn = NeonWidgets.CreateStyledButton(
                this,
                config.ConfirmLabel,
                config.Danger ? "NeonDangerButton" : "NeonPrimaryButton",
                async () =>
                {
                    if (modalExecuting || activeModalConfirmAsync == null) return;
                    var handler = activeModalConfirmAsync;
                    modalExecuting = true;
                    CloseTransactionModal();
                    try { await handler(); }
                    finally { modalExecuting = false; }
                });
            confirmBtn.Margin = new Thickness(8, 0, 0, 0);
            confirmBtn.IsEnabled = !blocked && !config.GameRunning;
            AutomationProperties.SetName(confirmBtn, config.ConfirmLabel);
            modalFootButtons.Children.Add(confirmBtn);
        }

        bool retargetFromClose = modalClosing;
        if (!modalOpenLogical && !modalClosing)
            modalReturnFocus = Keyboard.FocusedElement;

        int myGen = ++modalAnimGeneration;
        modalOpenLogical = true;
        modalClosing = false;
        modalBackdrop.IsHitTestVisible = true;
        modalBackdrop.Visibility = Visibility.Visible;

        var sheetHost = (FrameworkElement)modalBackdrop.Child;
        NeonMotion.OpenModal(
            this,
            modalBackdrop,
            sheetHost,
            retargetFromClose,
            () => myGen == modalAnimGeneration);
    }

    public void CloseTransactionModal()
    {
        if (!modalOpenLogical && !modalClosing && modalBackdrop.Visibility != Visibility.Visible)
        {
            activeModalConfirmAsync = null;
            return;
        }

        var returnTarget = modalReturnFocus;
        modalReturnFocus = null;
        activeModalConfirmAsync = null;
        modalOpenLogical = false;
        modalClosing = true;
        modalBackdrop.IsHitTestVisible = false;
        int myGen = ++modalAnimGeneration;

        var sheetHost = (FrameworkElement)modalBackdrop.Child;
        NeonMotion.CloseModal(
            this,
            modalBackdrop,
            sheetHost,
            () => myGen == modalAnimGeneration,
            () =>
            {
                modalClosing = false;
                modalBackdrop.Visibility = Visibility.Collapsed;
                modalBackdrop.IsHitTestVisible = true;
            });

        TryRestoreFocus(returnTarget);
    }

    // ========================================================================
    // ENVIRONMENT / DIAGNOSTICS / SETTINGS SLIDE-OVER DRAWER
    // ========================================================================

    Border BuildToolsDrawerOverlay()
    {
        var backdrop = new Border
        {
            Background = NeonPalette.DrawerBackdrop,
            Visibility = Visibility.Collapsed
        };

        var headGrid = new Grid();
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(NeonWidgets.Text("环境、诊断与设置", 15.5, NeonPalette.InkPrimary, FontWeights.Bold));
        var sub = NeonWidgets.Text("游戏目录、运行锁状态、日志诊断与界面风格", 12, NeonPalette.InkSecondary);
        sub.Margin = new Thickness(0, 3, 0, 0);
        titles.Children.Add(sub);
        headGrid.Children.Add(titles);

        var closeBtn = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Content = "×",
            FontSize = 16,
            Foreground = NeonPalette.InkSecondary,
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Template = CreateRoundedButtonTemplate(6, NeonPalette.BgElevated, NeonPalette.BorderMedium)
        };
        closeBtn.Click += (_, _) => CloseToolsDrawer();
        Grid.SetColumn(closeBtn, 1);
        headGrid.Children.Add(closeBtn);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 15, 18, 13),
            Child = headGrid
        });

        var scroll = new ScrollViewer
        {
            Content = drawerBodyStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        var sheet = new Border
        {
            Width = 440,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderMedium,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = layout
        };
        var sheetShadow = new Border
        {
            Width = 440,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = NeonPalette.BgSurface,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#141816"),
                BlurRadius = 40,
                ShadowDepth = -4,
                Opacity = 0.20
            }
        };
        var sheetHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        sheetHost.Children.Add(sheetShadow);
        sheetHost.Children.Add(sheet);

        backdrop.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, backdrop)) CloseToolsDrawer();
        };
        backdrop.Child = sheetHost;
        return backdrop;
    }

    public void OpenToolsDrawer()
    {
        if (currentSnapshot != null) RenderToolsDrawerContent(currentSnapshot);

        bool retargetFromClose = drawerClosing;
        if (!drawerOpenLogical && !drawerClosing)
            drawerReturnFocus = Keyboard.FocusedElement;

        int myGen = ++drawerAnimGeneration;
        drawerOpenLogical = true;
        drawerClosing = false;
        drawerBackdrop.IsHitTestVisible = true;
        drawerBackdrop.Visibility = Visibility.Visible;

        var sheetHost = (FrameworkElement)drawerBackdrop.Child;
        NeonMotion.OpenDrawer(
            this,
            drawerBackdrop,
            sheetHost,
            retargetFromClose,
            () => myGen == drawerAnimGeneration);
    }

    public void CloseToolsDrawer()
    {
        if (!drawerOpenLogical && !drawerClosing && drawerBackdrop.Visibility != Visibility.Visible)
            return;

        var returnTarget = drawerReturnFocus;
        drawerReturnFocus = null;
        drawerOpenLogical = false;
        drawerClosing = true;
        drawerBackdrop.IsHitTestVisible = false;
        int myGen = ++drawerAnimGeneration;

        var sheetHost = (FrameworkElement)drawerBackdrop.Child;
        NeonMotion.CloseDrawer(
            this,
            drawerBackdrop,
            sheetHost,
            () => myGen == drawerAnimGeneration,
            () =>
            {
                drawerClosing = false;
                drawerBackdrop.Visibility = Visibility.Collapsed;
                drawerBackdrop.IsHitTestVisible = true;
            });

        TryRestoreFocus(returnTarget);
    }

    void RenderToolsDrawerContent(NeonWorkbenchSnapshot s)
    {
        drawerBodyStack.Children.Clear();
        void AddSection(string title, UIElement cardContent)
        {
            drawerBodyStack.Children.Add(NeonWidgets.Text(title, 11.5, NeonPalette.InkMuted, FontWeights.Bold));
            drawerBodyStack.Children.Add(new Border
            {
                Background = NeonPalette.BgElevated,
                BorderBrush = NeonPalette.BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 7, 0, 16),
                Child = cardContent
            });
        }

        // 1. Game root & running lock
        var rootStack = new StackPanel();
        rootStack.Children.Add(NeonWidgets.Text(s.GameRoot ?? "尚未选择游戏目录", 12, NeonPalette.InkPrimary, FontWeights.SemiBold, mono: true, wrap: true));
        var stateLine = NeonWidgets.Text(
            s.GameRoot == null ? "请定位包含 SlayTheSpire2.exe 的目录" : s.GameRunning ? "状态：游戏正在运行（写入操作已锁定保护）" : "状态：游戏已退出（可安全执行目录事务）",
            11.5, s.GameRunning ? NeonPalette.Amber : NeonPalette.InkSecondary, wrap: true);
        stateLine.Margin = new Thickness(0, 4, 0, 9);
        rootStack.Children.Add(stateLine);
        var rootBtns = new WrapPanel();
        var choose = NeonWidgets.CreateStyledButton(this, "选择游戏目录", "NeonPrimaryButton", () => { CloseToolsDrawer(); RequestChooseGameRoot?.Invoke(); }, "sm");
        choose.Margin = new Thickness(0, 0, 7, 4);
        var autoFind = NeonWidgets.CreateStyledButton(this, "自动查找", "NeonSecondaryButton", () => { CloseToolsDrawer(); RequestDiscoverGameRoots?.Invoke(); }, "sm");
        autoFind.Margin = new Thickness(0, 0, 7, 4);
        rootBtns.Children.Add(choose);
        rootBtns.Children.Add(autoFind);
        if (s.GameRoot != null)
            rootBtns.Children.Add(NeonWidgets.CreateStyledButton(this, "打开目录", "NeonGhostButton", () => RequestOpenGameFolder?.Invoke(), "sm"));
        rootStack.Children.Add(rootBtns);
        AddSection("游戏目录与进程锁", rootStack);

        // 2. Patch health & log diagnosis
        var diagStack = new StackPanel();
        diagStack.Children.Add(NeonWidgets.Text(
            s.IsLifecycleHealthValid
                ? $"补丁健康核验有效 · 已验证 {s.VerifiedInstalledPatchesCount} 项修复（{s.RetirablePatchesCount} 项可安全退役）。"
                : "补丁健康核验尚未运行或已过期；点击下方按钮重新核对本地文件指纹。",
            12, NeonPalette.InkSecondary, wrap: true));
        var diagBtns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var scanHealth = NeonWidgets.CreateStyledButton(this, "重新扫描并核验补丁健康", "NeonSecondaryButton", () => { CloseToolsDrawer(); RequestRescanAndVerifyHealth?.Invoke(); }, "sm");
        scanHealth.Margin = new Thickness(0, 0, 7, 4);
        var logDiag = NeonWidgets.CreateStyledButton(this, "选择 godot.log 诊断", "NeonGhostButton", () => { CloseToolsDrawer(); RequestDiagnoseLog?.Invoke(); }, "sm");
        diagBtns.Children.Add(scanHealth);
        diagBtns.Children.Add(logDiag);
        diagStack.Children.Add(diagBtns);
        AddSection("健康核验与日志特征诊断", diagStack);

        // 3. Theme switcher (keeps legacy Workspace / Classic accessible as fallback)
        var themeStack = new StackPanel();
        themeStack.Children.Add(NeonWidgets.Text("切换界面风格立即生效，不会修改任何游戏或 MOD 文件。", 11.5, NeonPalette.InkSecondary, wrap: true));
        var themeBtns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (id, label) in new[]
        {
            (UiThemes.Neon, "Neon 工作台（当前）"),
            (UiThemes.Workspace, "新版 Beta"),
            (UiThemes.Classic, "经典 Beta")
        })
        {
            string target = id;
            var btn = NeonWidgets.CreateStyledButton(
                this,
                label,
                target == UiThemes.Neon ? "NeonPrimaryButton" : "NeonSecondaryButton",
                () => { CloseToolsDrawer(); RequestSwitchTheme?.Invoke(target); },
                "xs");
            btn.Margin = new Thickness(0, 0, 6, 6);
            themeBtns.Children.Add(btn);
        }
        themeStack.Children.Add(themeBtns);
        AddSection("界面风格", themeStack);
    }
}
