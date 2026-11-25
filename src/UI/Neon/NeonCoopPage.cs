using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SpirePatch;

internal sealed partial class NeonWorkbenchView
{
    void BuildCoopPage(StackPanel container, NeonWorkbenchSnapshot s)
    {
        var coop = s.Coop;
        var rows = coop.Rows
            .OrderByDescending(r => r.Selectable ? 1 : 0)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var selectableRows = rows.Where(r => r.Selectable).ToArray();
        var checkedValid = coopCheckedIds
            .Where(id => selectableRows.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // ---- Page Head ----
        var headActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom };
        var exportBtn = NeonWidgets.CreateStyledButton(this, "导出我的清单 (实时扫描)", "NeonSecondaryButton",
            () => RequestExportCoopManifest?.Invoke());
        exportBtn.IsEnabled = s.GameRoot != null;
        exportBtn.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(exportBtn, "导出我的清单");
        headActions.Children.Add(exportBtn);

        var importBtn = NeonWidgets.CreateStyledButton(
            this,
            "导入队友清单",
            coop.HasPeer ? "NeonSecondaryButton" : "NeonPrimaryButton",
            () => RequestImportCoopPeerFile?.Invoke());
        importBtn.IsEnabled = s.GameRoot != null;
        AutomationProperties.SetName(importBtn, "导入队友清单");
        headActions.Children.Add(importBtn);

        if (coop.HasPeer)
        {
            var recheckBtn = NeonWidgets.CreateStyledButton(this, "重新扫描并比较", "NeonGhostButton",
                () => RequestRefreshCoopComparison?.Invoke());
            recheckBtn.Margin = new Thickness(6, 0, 0, 0);
            headActions.Children.Add(recheckBtn);
        }

        container.Children.Add(NeonWidgets.CreatePageHead(
            "联机补丁核对",
            "导出或导入队友的清单快照，以队友清单为对齐目标，只调整本机且默认不勾选。",
            headActions));

        // ---- Honest Scope Boundary Note ----
        var scopeTb = new TextBlock
        {
            FontFamily = NeonPalette.SansFont,
            FontSize = 12,
            Foreground = NeonPalette.InkSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        };
        scopeTb.Inlines.Add(new Run("核对边界声明") { FontWeight = FontWeights.SemiBold, Foreground = NeonPalette.InkPrimary });
        scopeTb.Inlines.Add(new Run($"：联机核对仅比较本仓库已收录的 {s.OnlineCatalogCount} 项修复补丁与关联前置状态，"));
        scopeTb.Inlines.Add(new Run("不等于") { FontWeight = FontWeights.SemiBold, Foreground = NeonPalette.InkPrimary });
        scopeTb.Inlines.Add(new Run("比对全部普通 MOD 文件、玩法配置或游戏存档。队友清单为其导出时刻的快照；若队友后续有变动请重新导出。"));
        container.Children.Add(NeonWidgets.CreateScopeNote("⚠", scopeTb));

        // ---- 2-Column Machine Comparison Cards ----
        var metaGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        bool narrow = ActualWidth > 0 && ActualWidth < 920;
        if (narrow)
        {
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        else
        {
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Local card
        var localStack = new StackPanel();
        localStack.Children.Add(NeonWidgets.Text("本机状态（只读实时扫描）", 11.5, NeonPalette.InkMuted));
        var localTitle = NeonWidgets.Text(
            s.GameRoot != null ? NeonWorkbenchBuilder.ShortenGamePath(s.GameRoot) : "尚未选择游戏目录",
            13.5, NeonPalette.InkPrimary, FontWeights.SemiBold, mono: s.GameRoot != null, ellipsis: true);
        localTitle.Margin = new Thickness(0, 4, 0, 4);
        localTitle.ToolTip = s.GameRoot;
        localStack.Children.Add(localTitle);
        string scanMeta = coop.LocalScanTimeLabel ?? (s.LastScanTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未扫描");
        localStack.Children.Add(NeonWidgets.Text(
            $"扫描时间：{scanMeta} · {(s.ModInventoryDirty ? "清单待刷新" : "清单完整")} · 已收录补丁 {s.AllCatalogPatches.Length} 项",
            12, NeonPalette.InkSecondary, wrap: true));
        var localCard = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 13, 15, 13),
            Child = localStack
        };
        metaGrid.Children.Add(localCard);

        // Peer card
        var peerStack = new StackPanel();
        peerStack.Children.Add(NeonWidgets.Text("队友对照快照", 11.5, NeonPalette.InkMuted));
        if (coop.HasPeer)
        {
            var peerTitle = NeonWidgets.Text(coop.PeerLabel ?? "已导入队友清单", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold, ellipsis: true);
            peerTitle.Margin = new Thickness(0, 4, 0, 4);
            peerStack.Children.Add(peerTitle);
            peerStack.Children.Add(NeonWidgets.Text(
                $"导出时间：{coop.PeerTimeLabel ?? "未知"} · {coop.PeerManagerVersion ?? "schema 2 快照"}",
                12, NeonPalette.InkSecondary, wrap: true));
        }
        else
        {
            var peerTitle = NeonWidgets.Text("尚未导入队友清单", 13.5, NeonPalette.InkSecondary, FontWeights.SemiBold);
            peerTitle.Margin = new Thickness(0, 4, 0, 4);
            peerStack.Children.Add(peerTitle);
            peerStack.Children.Add(NeonWidgets.Text(
                "点击右上角「导入队友清单」选择队友导出的 JSON 快照文件即可开始比对。",
                12, NeonPalette.InkMuted, wrap: true));
        }
        var peerCard = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 13, 15, 13),
            Child = peerStack
        };
        if (narrow) Grid.SetRow(peerCard, 2);
        else Grid.SetColumn(peerCard, 2);
        metaGrid.Children.Add(peerCard);
        container.Children.Add(metaGrid);

        // ---- Status / Legacy / Last Outcome Banners ----
        if (!string.IsNullOrWhiteSpace(coop.StatusText))
        {
            var statusBanner = NeonWidgets.CreateAlert(NeonTone.Neutral, NeonWidgets.Text(coop.StatusText, 12.5, NeonPalette.InkPrimary, wrap: true));
            statusBanner.Margin = new Thickness(0, 0, 0, 12);
            container.Children.Add(statusBanner);
        }

        if (coop.HasPeer && coop.PeerIsLegacy)
        {
            var legacyBanner = NeonWidgets.CreateAlert(
                NeonTone.Amber,
                NeonWidgets.Text("这是旧版清单，仅支持查看。请双方更新至 0.9.0 或更新版本并重新导出，才能使用一键处理。", 12.5, NeonPalette.Amber, wrap: true));
            legacyBanner.Margin = new Thickness(0, 0, 0, 12);
            container.Children.Add(legacyBanner);
        }

        if (coop.LastOutcome is { } outcome)
        {
            var outStack = new StackPanel();
            outStack.Children.Add(NeonWidgets.Text(
                outcome.Success
                    ? "最近一次联机对齐已完成并复检通过"
                    : "联机对齐已停止后续步骤：" + (outcome.Error ?? ""),
                12.5, outcome.Success ? NeonPalette.Sage : NeonPalette.Cinnabar, FontWeights.SemiBold, wrap: true));
            if (outcome.Completed.Length > 0)
            {
                var stepsTb = NeonWidgets.Text(string.Join("  ·  ", outcome.Completed.Select(x => "✓ " + x)), 12, NeonPalette.InkSecondary, wrap: true);
                stepsTb.Margin = new Thickness(0, 4, 0, 0);
                outStack.Children.Add(stepsTb);
            }
            if (!string.IsNullOrWhiteSpace(outcome.Journal))
            {
                var jRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                var openJ = NeonWidgets.CreateStyledButton(this, "查看联机对齐日志", "NeonGhostButton",
                    () => RequestOpenPath?.Invoke(outcome.Journal), "xs");
                jRow.Children.Add(openJ);
                outStack.Children.Add(jRow);
            }
            var outAlert = NeonWidgets.CreateAlert(outcome.Success ? NeonTone.Sage : NeonTone.Cinnabar, outStack);
            outAlert.Margin = new Thickness(0, 0, 0, 12);
            container.Children.Add(outAlert);
        }

        // ---- Diff Comparison Table Card ----
        var headLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var headTitle = NeonWidgets.Text("差异对照清单", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        headTitle.Margin = new Thickness(0, 0, 8, 0);
        headLeft.Children.Add(headTitle);

        if (coop.HasPeer)
        {
            bool allAligned = rows.Length == 0 || rows.All(r => r.Diagnosis == "停用记录不同");
            string badgeLabel = rows.Length == 0
                ? "已收录补丁完全一致"
                : $"{rows.Length} 项差异（{selectableRows.Length} 项可自动处理）";
            headLeft.Children.Add(NeonWidgets.CreateBadge(badgeLabel, allAligned ? NeonTone.Sage : NeonTone.Amber));
        }

        UIElement? headRight = null;
        if (coop.HasPeer && selectableRows.Length > 0)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var selAll = NeonWidgets.CreateStyledButton(this, $"全选可处理项 ({selectableRows.Length})", "NeonGhostButton", () =>
            {
                foreach (var r in selectableRows) coopCheckedIds.Add(r.Id);
                if (currentSnapshot != null) RenderSecondaryPage("coop", currentSnapshot, preserveScroll: true);
            }, "sm");
            selAll.Margin = new Thickness(0, 0, 6, 0);

            var clearSel = NeonWidgets.CreateStyledButton(this, "清空", "NeonGhostButton", () =>
            {
                coopCheckedIds.Clear();
                if (currentSnapshot != null) RenderSecondaryPage("coop", currentSnapshot, preserveScroll: true);
            }, "sm");
            clearSel.Margin = new Thickness(0, 0, 6, 0);

            var applyBtn = NeonWidgets.CreateStyledButton(
                this,
                checkedValid.Length > 0 ? $"预览一键处理 ({checkedValid.Length})" : "预览一键处理",
                "NeonPrimaryButton",
                () =>
                {
                    if (checkedValid.Length > 0)
                        RequestPreviewCoopApply?.Invoke(checkedValid);
                },
                "sm");
            applyBtn.IsEnabled = checkedValid.Length > 0 && !s.ModInventoryDirty;
            AutomationProperties.SetName(applyBtn, applyBtn.Content?.ToString() ?? "预览一键处理");

            bar.Children.Add(selAll);
            bar.Children.Add(clearSel);
            bar.Children.Add(applyBtn);
            headRight = bar;
        }

        var body = new StackPanel();
        if (!coop.HasPeer)
        {
            body.Children.Add(BuildEmptyState(
                "尚未开始联机核对",
                "你可以先「导出我的清单」发给队友，或「导入队友清单」与本机实时扫描结果逐项比对。",
                "导入队友清单",
                () => RequestImportCoopPeerFile?.Invoke()));
        }
        else if (rows.Length == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(20, 36, 20, 36), HorizontalAlignment = HorizontalAlignment.Center };
            var okTitle = NeonWidgets.Text($"✓ 已收录修复补丁与队友「{coop.PeerLabel ?? "对照快照"}」保持一致", 14, NeonPalette.Sage, FontWeights.SemiBold);
            okTitle.HorizontalAlignment = HorizontalAlignment.Center;
            var okSub = NeonWidgets.Text("提示：请仍确认双方普通 MOD 组合及游戏版本一致后再建房开局。", 12, NeonPalette.InkMuted);
            okSub.HorizontalAlignment = HorizontalAlignment.Center;
            okSub.Margin = new Thickness(0, 6, 0, 0);
            empty.Children.Add(okTitle);
            empty.Children.Add(okSub);
            body.Children.Add(empty);
        }
        else
        {
            int manualCount = rows.Length - selectableRows.Length;
            var filterStrip = new Border
            {
                Background = NeonPalette.BgGround,
                BorderBrush = NeonPalette.BorderSubtle,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 8, 12, 8)
            };
            var filterPills = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var (fid, flabel) in new[]
            {
                ("all", $"全部差异 ({rows.Length})"),
                ("auto", $"可自动处理 ({selectableRows.Length})"),
                ("manual", $"需人工核对 ({manualCount})")
            })
            {
                bool active = coopDiffFilter == fid;
                var pill = new Button
                {
                    Content = flabel,
                    Height = 24,
                    Padding = new Thickness(9, 0, 9, 0),
                    Margin = new Thickness(0, 0, 6, 0),
                    FontFamily = NeonPalette.SansFont,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    Background = active ? NeonPalette.InkPrimary : Brushes.Transparent,
                    Foreground = active ? NeonPalette.BgSurface : NeonPalette.InkSecondary,
                    BorderBrush = active ? NeonPalette.InkPrimary : NeonPalette.BorderSubtle,
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Template = CreateRoundedButtonTemplate(12, active ? NeonPalette.InkPrimary : NeonPalette.BgWell, active ? NeonPalette.InkPrimary : NeonPalette.BorderMedium)
                };
                string target = fid;
                pill.Click += (_, _) =>
                {
                    if (coopDiffFilter == target) return;
                    coopDiffFilter = target;
                    if (currentSnapshot != null) RenderSecondaryPage("coop", currentSnapshot, preserveScroll: false);
                };
                filterPills.Children.Add(pill);
            }
            filterStrip.Child = filterPills;
            body.Children.Add(filterStrip);

            var displayRows = coopDiffFilter switch
            {
                "auto" => rows.Where(r => r.Selectable).ToArray(),
                "manual" => rows.Where(r => !r.Selectable).ToArray(),
                _ => rows
            };

            if (displayRows.Length == 0)
            {
                var emptyFiltered = NeonWidgets.Text("当前筛选条件下没有差异条目。", 12.5, NeonPalette.InkMuted);
                emptyFiltered.Margin = new Thickness(16, 20, 16, 20);
                body.Children.Add(emptyFiltered);
            }
            else
            {
                // Table Header
                body.Children.Add(BuildCoopTableHeader());
                for (int i = 0; i < displayRows.Length; i++)
                    body.Children.Add(BuildCoopTableRow(displayRows[i], checkedValid.Contains(displayRows[i].Id, StringComparer.OrdinalIgnoreCase), isLast: i == displayRows.Length - 1));
            }
        }

        container.Children.Add(NeonWidgets.CreateSurfaceCard(headLeft, headRight, body));
    }

    static Grid CreateCoopColumnsGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(142) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.9, GridUnitType.Star) });
        return g;
    }

    static Border BuildCoopTableHeader()
    {
        var g = CreateCoopColumnsGrid();
        void AddHeader(int col, string text)
        {
            var tb = NeonWidgets.Text(text, 11.5, NeonPalette.InkMuted, FontWeights.SemiBold);
            tb.Margin = new Thickness(6, 0, 6, 0);
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }
        AddHeader(1, "补丁名称");
        AddHeader(2, "本机");
        AddHeader(3, "队友");
        AddHeader(4, "诊断与建议动作");
        AddHeader(5, "原因与边界说明");

        return new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 9, 8, 9),
            Child = g
        };
    }

    Border BuildCoopTableRow(CoopDifference row, bool isChecked, bool isLast)
    {
        var g = CreateCoopColumnsGrid();

        // Col 0: Checkbox or dash
        if (row.Selectable)
        {
            var cb = new CheckBox
            {
                Style = (Style)FindResource("NeonCheckBox"),
                IsChecked = isChecked,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0)
            };
            AutomationProperties.SetName(cb, "勾选联机处理 " + row.Name);
            cb.Checked += (_, _) =>
            {
                coopCheckedIds.Add(row.Id);
                if (currentSnapshot != null) RenderSecondaryPage("coop", currentSnapshot, preserveScroll: true);
            };
            cb.Unchecked += (_, _) =>
            {
                coopCheckedIds.Remove(row.Id);
                if (currentSnapshot != null) RenderSecondaryPage("coop", currentSnapshot, preserveScroll: true);
            };
            g.Children.Add(cb);
        }
        else
        {
            var dash = NeonWidgets.Text("—", 12, NeonPalette.InkFaint);
            dash.HorizontalAlignment = HorizontalAlignment.Center;
            dash.VerticalAlignment = VerticalAlignment.Top;
            dash.ToolTip = "不可自动勾选处理";
            g.Children.Add(dash);
        }

        // Col 1: Name + Mono ID
        var nameStack = new StackPanel { Margin = new Thickness(6, 0, 8, 0) };
        nameStack.Children.Add(NeonWidgets.Text(row.Name, 12.5, NeonPalette.InkPrimary, FontWeights.SemiBold, wrap: true));
        var idTb = NeonWidgets.Text(row.Id, 11, NeonPalette.InkMuted, mono: true);
        idTb.Margin = new Thickness(0, 2, 0, 0);
        nameStack.Children.Add(idTb);
        Grid.SetColumn(nameStack, 1);
        g.Children.Add(nameStack);

        // Col 2: Local status
        var mineBadge = NeonWidgets.CreateBadge(row.Mine, NeonTone.Neutral);
        mineBadge.HorizontalAlignment = HorizontalAlignment.Left;
        mineBadge.VerticalAlignment = VerticalAlignment.Top;
        mineBadge.Margin = new Thickness(6, 0, 4, 0);
        Grid.SetColumn(mineBadge, 2);
        g.Children.Add(mineBadge);

        // Col 3: Peer status
        var peerTone = row.Peer == "已安装" ? NeonTone.Sage : NeonTone.Neutral;
        var peerBadge = NeonWidgets.CreateBadge(row.Peer, peerTone);
        peerBadge.HorizontalAlignment = HorizontalAlignment.Left;
        peerBadge.VerticalAlignment = VerticalAlignment.Top;
        peerBadge.Margin = new Thickness(6, 0, 4, 0);
        Grid.SetColumn(peerBadge, 3);
        g.Children.Add(peerBadge);

        // Col 4: Diagnosis badge + action label
        NeonTone diagTone = row.Action is CoopAction.Install or CoopAction.Enable ? NeonTone.Indigo
            : row.Action == CoopAction.Disable ? NeonTone.Amber
            : row.Diagnosis == "停用记录不同" ? NeonTone.Neutral
            : NeonTone.Cinnabar;
        var diagStack = new StackPanel { Margin = new Thickness(6, 0, 8, 0) };
        var diagBadge = NeonWidgets.CreateBadge(row.Diagnosis, diagTone);
        diagBadge.HorizontalAlignment = HorizontalAlignment.Left;
        diagStack.Children.Add(diagBadge);
        var actLbl = NeonWidgets.Text(row.ActionLabel, 11.5, NeonPalette.InkSecondary, FontWeights.Medium);
        actLbl.Margin = new Thickness(0, 4, 0, 0);
        diagStack.Children.Add(actLbl);
        Grid.SetColumn(diagStack, 4);
        g.Children.Add(diagStack);

        // Col 5: Reason & boundary detail + optional evidence modal link
        var detailStack = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        detailStack.Children.Add(NeonWidgets.Text(row.Detail, 12, NeonPalette.InkSecondary, wrap: true));
        if (!row.Selectable && currentSnapshot != null)
        {
            var matchedPatch = currentSnapshot.AllCatalogPatches
                .FirstOrDefault(p => p.PatchId.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
            if (matchedPatch != null)
            {
                var evBtn = NeonWidgets.CreateStyledButton(this, "查看判断依据", "NeonGhostButton",
                    () => OpenPatchEvidenceModal(matchedPatch, currentSnapshot), "xs");
                evBtn.HorizontalAlignment = HorizontalAlignment.Left;
                evBtn.Margin = new Thickness(-8, 4, 0, 0);
                detailStack.Children.Add(evBtn);
            }
        }
        Grid.SetColumn(detailStack, 5);
        g.Children.Add(detailStack);

        Brush rowBg = row.Selectable ? NeonPalette.BgSurface : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F6F4EF"));
        return new Border
        {
            Background = rowBg,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 11, 8, 11),
            Child = g
        };
    }
}
