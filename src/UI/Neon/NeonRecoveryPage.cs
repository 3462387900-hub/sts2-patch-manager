using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SpirePatch;

internal sealed partial class NeonWorkbenchView
{
    void BuildRecoveryPage(StackPanel container, NeonWorkbenchSnapshot s)
    {
        var rec = s.Recovery;
        long totalSnapBytes = rec.Snapshots.Sum(x => Math.Max(0, x.TotalBytes));

        // ---- Page Head ----
        var createBtn = NeonWidgets.CreateStyledButton(this, "创建手动环境快照", "NeonPrimaryButton",
            () => OpenCreateSnapshotModal(s));
        createBtn.IsEnabled = s.GameRoot != null && !s.ModInventoryDirty && !s.GameRunning;
        AutomationProperties.SetName(createBtn, "创建手动环境快照");

        container.Children.Add(NeonWidgets.CreatePageHead(
            "快照与恢复",
            "弄清“刚才动了什么”并安全回到之前的完整 MOD 状态；任何环境恢复前都会先自动保护当前现场。",
            createBtn));

        // ---- Scope Note ----
        var scopeTb = new TextBlock
        {
            FontFamily = NeonPalette.SansFont,
            FontSize = 12,
            Foreground = NeonPalette.InkSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        };
        scopeTb.Inlines.Add(new Run("快照包含范围") { FontWeight = FontWeights.SemiBold, Foreground = NeonPalette.InkPrimary });
        scopeTb.Inlines.Add(new Run("：完整备份当前游戏目录的 "));
        scopeTb.Inlines.Add(new Run("mods/") { FontFamily = NeonPalette.MonoFont });
        scopeTb.Inlines.Add(new Run(" 与 "));
        scopeTb.Inlines.Add(new Run(ModManager.StorageName + "/") { FontFamily = NeonPalette.MonoFont });
        scopeTb.Inlines.Add(new Run("（保留目录、时间戳与 SHA-256 校验，单份上限 50,000 文件 / 30 GB）。"));
        scopeTb.Inlines.Add(new Run("不包含") { FontWeight = FontWeights.SemiBold, Foreground = NeonPalette.InkPrimary });
        scopeTb.Inlines.Add(new Run("游戏本体、存档、个人配置、来源绑定及单 MOD 独立历史备份。"));
        container.Children.Add(NeonWidgets.CreateScopeNote("ℹ", scopeTb));

        // ---- 2-Column Recovery Grid (Left 1.35* : Right 1*) ----
        var grid = new Grid();
        bool narrow = ActualWidth > 0 && ActualWidth < 960;
        if (narrow)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Left Column: Snapshots Vault + Patch Batch Rollback
        var leftCol = new StackPanel();
        leftCol.Children.Add(BuildSnapshotsCard(s, rec, totalSnapBytes));
        leftCol.Children.Add(BuildPatchBatchRollbackCard(s, rec.LatestPatchBatch));
        grid.Children.Add(leftCol);

        // Right Column: Live Activity Ledger + Disabled Mod Vault
        var rightCol = new StackPanel();
        rightCol.Children.Add(BuildActivityLedgerCard(rec.ActivityLog));
        rightCol.Children.Add(BuildDisabledVaultCard(rec.DisabledAllMods));
        if (narrow) Grid.SetRow(rightCol, 2);
        else Grid.SetColumn(rightCol, 2);
        grid.Children.Add(rightCol);

        container.Children.Add(grid);
    }

    Border BuildSnapshotsCard(NeonWorkbenchSnapshot s, NeonRecoverySnapshotState rec, long totalBytes)
    {
        var headLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var title = NeonWidgets.Text("完整 MOD 环境快照", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 8, 0);
        headLeft.Children.Add(title);
        headLeft.Children.Add(NeonWidgets.CreateBadge(
            $"{rec.Snapshots.Length} 份 · 共 {ModEnvironmentSnapshots.FormatBytes(totalBytes)}",
            NeonTone.Neutral));

        var headRight = NeonWidgets.Text("写入前自动生成，亦可手动留档", 11.5, NeonPalette.InkMuted);

        var body = new StackPanel();
        if (!string.IsNullOrWhiteSpace(rec.SnapshotsWarning))
        {
            var warn = NeonWidgets.CreateAlert(NeonTone.Amber, NeonWidgets.Text("无法读取快照目录：" + rec.SnapshotsWarning, 12, NeonPalette.Amber, wrap: true));
            warn.Margin = new Thickness(14);
            body.Children.Add(warn);
        }
        else if (rec.Snapshots.Length == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(18, 22, 18, 22) };
            empty.Children.Add(NeonWidgets.Text("还没有环境快照", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold));
            var sub = NeonWidgets.Text("点击右上角「创建手动环境快照」，或在安装/更新 MOD 前由管理器自动生成可恢复保护快照。", 12, NeonPalette.InkMuted, wrap: true);
            sub.Margin = new Thickness(0, 4, 0, 0);
            empty.Children.Add(sub);
            body.Children.Add(empty);
        }
        else
        {
            for (int i = 0; i < rec.Snapshots.Length; i++)
            {
                var snap = rec.Snapshots[i];
                bool isAuto = snap.Purpose.Contains("自动", StringComparison.Ordinal);

                var itemGrid = new Grid { Margin = new Thickness(16, 13, 16, 13) };
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(21) });
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                itemGrid.Children.Add(NeonWidgets.CreateTimelineDot(isAuto));

                var content = new StackPanel();
                var topLine = new Grid();
                topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titleWrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                var purposeTb = NeonWidgets.Text(snap.Purpose, 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
                purposeTb.Margin = new Thickness(0, 0, 7, 2);
                titleWrap.Children.Add(purposeTb);
                titleWrap.Children.Add(NeonWidgets.CreateBadge(isAuto ? "自动保护" : "手动快照", isAuto ? NeonTone.Indigo : NeonTone.Sage));
                topLine.Children.Add(titleWrap);

                var timeTb = NeonWidgets.Text(snap.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 11.5, NeonPalette.InkMuted, mono: true);
                timeTb.VerticalAlignment = VerticalAlignment.Center;
                timeTb.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(timeTb, 1);
                topLine.Children.Add(timeTb);
                content.Children.Add(topLine);

                string shortId = snap.Id.Length > 12 ? snap.Id[..12] : snap.Id;
                var metaTb = NeonWidgets.Text(
                    $"{snap.FileCount:N0} 个文件 · {ModEnvironmentSnapshots.FormatBytes(snap.TotalBytes)} · 编号 {shortId}",
                    12, NeonPalette.InkSecondary, mono: true);
                metaTb.Margin = new Thickness(0, 4, 0, 0);
                content.Children.Add(metaTb);

                if (!snap.IsValid)
                {
                    var warnTb = NeonWidgets.Text("快照索引异常：" + (snap.Warning ?? "无法读取"), 11.5, NeonPalette.Cinnabar, wrap: true);
                    warnTb.Margin = new Thickness(0, 4, 0, 0);
                    content.Children.Add(warnTb);
                }

                var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
                var restoreBtn = NeonWidgets.CreateStyledButton(this, "预览差异并恢复", "NeonSecondaryButton",
                    () => RequestPreviewEnvironmentRestore?.Invoke(snap.Id), "sm");
                restoreBtn.IsEnabled = snap.IsValid && !s.ModInventoryDirty;
                restoreBtn.Margin = new Thickness(0, 0, 7, 0);
                AutomationProperties.SetName(restoreBtn, "预览差异并恢复 " + snap.Purpose);

                var delBtn = NeonWidgets.CreateStyledButton(this, "删除此快照", "NeonGhostButton",
                    () => OpenDeleteSnapshotConfirmModal(snap), "sm");
                AutomationProperties.SetName(delBtn, "删除此快照 " + snap.Purpose);

                btnRow.Children.Add(restoreBtn);
                btnRow.Children.Add(delBtn);
                content.Children.Add(btnRow);

                Grid.SetColumn(content, 1);
                itemGrid.Children.Add(content);

                body.Children.Add(new Border
                {
                    Background = NeonPalette.BgSurface,
                    BorderBrush = NeonPalette.BorderSubtle,
                    BorderThickness = i == rec.Snapshots.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                    Child = itemGrid
                });
            }
        }

        return NeonWidgets.CreateSurfaceCard(headLeft, headRight, body);
    }

    Border BuildPatchBatchRollbackCard(NeonWorkbenchSnapshot s, NeonPatchBackupBatch? batch)
    {
        var headLeft = NeonWidgets.Text("补丁批次回滚（mod_fix_backups）", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        var headRight = NeonWidgets.Text("按最近安装批次完整撤销，保留更早批次", 11.5, NeonPalette.InkMuted);

        var body = new StackPanel();
        if (batch == null)
        {
            var empty = NeonWidgets.Text("当前没有待回滚的补丁安装批次。", 12.5, NeonPalette.InkMuted);
            empty.Margin = new Thickness(16, 18, 16, 18);
            body.Children.Add(empty);
        }
        else
        {
            var g = new Grid { Margin = new Thickness(16, 13, 16, 13) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            info.Children.Add(NeonWidgets.Text(
                "最近批次：" + string.Join("、", batch.AffectedNames),
                13.5, NeonPalette.InkPrimary, FontWeights.SemiBold, wrap: true));
            var meta = NeonWidgets.Text(
                $"安装于 {batch.Time:yyyy-MM-dd HH:mm} · 备份位置 {batch.RelativePath} · 类型：{(batch.DocsOnly ? "仅撤销说明改动" : "实际修复回滚")}",
                12, NeonPalette.InkSecondary, wrap: true);
            meta.Margin = new Thickness(0, 4, 0, 0);
            info.Children.Add(meta);
            g.Children.Add(info);

            var checkBtn = NeonWidgets.CreateStyledButton(this, "检查最近一次回滚", "NeonSecondaryButton",
                () => RequestPreviewPatchBatchRollback?.Invoke(), "sm");
            checkBtn.VerticalAlignment = VerticalAlignment.Center;
            checkBtn.IsEnabled = s.GameRoot != null;
            AutomationProperties.SetName(checkBtn, "检查最近一次回滚");
            Grid.SetColumn(checkBtn, 1);
            g.Children.Add(checkBtn);
            body.Children.Add(g);
        }

        return NeonWidgets.CreateSurfaceCard(headLeft, headRight, body);
    }

    Border BuildActivityLedgerCard(NeonActivityEntry[] log)
    {
        var headLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var title = NeonWidgets.Text("本次改动与操作轨迹", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 8, 0);
        headLeft.Children.Add(title);
        headLeft.Children.Add(NeonWidgets.CreateBadge($"{log.Length} 条", NeonTone.Neutral));

        var headRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var scopeLbl = NeonWidgets.Text("仅记录本次启动", 11.5, NeonPalette.InkMuted);
        scopeLbl.VerticalAlignment = VerticalAlignment.Center;
        headRight.Children.Add(scopeLbl);
        if (log.Length > 0)
        {
            var copyBtn = NeonWidgets.CreateStyledButton(this, "复制轨迹", "NeonGhostButton", () =>
            {
                try
                {
                    Clipboard.SetText(string.Join(Environment.NewLine, log.Select(e => $"[{e.TimeLabel}] {e.Text}")));
                    ShowToast("已复制本次操作轨迹到剪贴板");
                }
                catch { }
            }, "xs");
            copyBtn.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetName(copyBtn, "复制本次操作轨迹");
            headRight.Children.Add(copyBtn);
        }

        var list = new StackPanel();
        if (log.Length == 0)
        {
            var empty = NeonWidgets.Text("本次会话尚未记录文件操作轨迹。", 12.5, NeonPalette.InkMuted);
            empty.Margin = new Thickness(14, 16, 14, 16);
            list.Children.Add(empty);
        }
        else
        {
            for (int i = 0; i < log.Length; i++)
            {
                var entry = log[i];
                var g = new Grid { Margin = new Thickness(14, 10, 14, 10) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txt = NeonWidgets.Text(entry.Text, 12.5, NeonPalette.InkPrimary, wrap: true);
                txt.Margin = new Thickness(0, 0, 10, 0);
                g.Children.Add(txt);

                var time = NeonWidgets.Text(entry.TimeLabel, 11, NeonPalette.InkMuted, mono: true);
                time.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(time, 1);
                g.Children.Add(time);

                list.Children.Add(new Border
                {
                    BorderBrush = NeonPalette.BorderSubtle,
                    BorderThickness = i == log.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                    Child = g
                });
            }
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 310,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list
        };
        NeonMotion.AttachSmoothWheelScroll(scroll, this);

        return NeonWidgets.CreateSurfaceCard(headLeft, headRight, scroll);
    }

    Border BuildDisabledVaultCard(ManagedMod[] disabledMods)
    {
        var headLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var title = NeonWidgets.Text($"停用保留区（{ModManager.StorageName}）", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 8, 0);
        headLeft.Children.Add(title);
        headLeft.Children.Add(NeonWidgets.CreateBadge($"{disabledMods.Length} 项", NeonTone.Neutral));

        var body = new StackPanel();
        if (disabledMods.Length == 0)
        {
            var empty = NeonWidgets.Text("停用保留区当前为空。停用 MOD 或独立补丁不会删除文件。", 12.5, NeonPalette.InkMuted, wrap: true);
            empty.Margin = new Thickness(16, 16, 16, 16);
            body.Children.Add(empty);
        }
        else
        {
            for (int i = 0; i < disabledMods.Length; i++)
            {
                var m = disabledMods[i];
                bool isPatch = ModPresentation.IsIndependentPatch(m);
                var g = new Grid { Margin = new Thickness(14, 10, 14, 10) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                var titleRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                var nameTb = NeonWidgets.Text(m.Name, 12.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
                nameTb.Margin = new Thickness(0, 0, 6, 0);
                titleRow.Children.Add(nameTb);
                if (isPatch) titleRow.Children.Add(NeonWidgets.CreateBadge("独立补丁", NeonTone.Neutral));
                left.Children.Add(titleRow);

                var orig = NeonWidgets.Text("原目录：" + (m.Original ?? ("mods/" + m.Id)), 11, NeonPalette.InkMuted, mono: true, ellipsis: true);
                orig.Margin = new Thickness(0, 2, 0, 0);
                left.Children.Add(orig);
                g.Children.Add(left);

                var btn = NeonWidgets.CreateStyledButton(
                    this,
                    isPatch ? "预览恢复" : "查看/恢复",
                    "NeonSecondaryButton",
                    () =>
                    {
                        if (isPatch) RequestPreviewToggleMod?.Invoke(m);
                        else FocusMod(m.Id);
                    },
                    "xs");
                btn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(btn, 1);
                g.Children.Add(btn);

                body.Children.Add(new Border
                {
                    BorderBrush = NeonPalette.BorderSubtle,
                    BorderThickness = i == disabledMods.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                    Child = g
                });
            }
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body
        };
        NeonMotion.AttachSmoothWheelScroll(scroll, this);

        return NeonWidgets.CreateSurfaceCard(headLeft, null, scroll);
    }

    void OpenCreateSnapshotModal(NeonWorkbenchSnapshot s)
    {
        var stack = new StackPanel();
        var lbl = NeonWidgets.Text("快照备注（最多 80 字）", 12, NeonPalette.InkPrimary, FontWeights.SemiBold);
        lbl.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(lbl);

        var (inputHost, inputBox, _) = NeonWidgets.CreateTextInput("例如：周末联机前手动留档", height: 34);
        inputBox.MaxLength = 80;
        inputBox.Text = $"手动留档 · {DateTime.Now:HH:mm}";
        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                _ = ConfirmTransactionModalAsync();
            }
        };
        AutomationProperties.SetName(inputBox, "快照备注");
        stack.Children.Add(inputHost);

        OpenTransactionModal(new NeonTransactionConfig(
            Title: "创建手动 MOD 环境快照",
            Subtitle: "完整备份当前 mods/ 与已停用模组保留区并生成 SHA-256 索引",
            GameRunning: s.GameRunning,
            BlockedReason: s.ModInventoryDirty ? "检测到 MOD 文件变化，请先刷新清单稳定后再创建快照。" : null,
            AutoSnapshotNote: null,
            BoundaryNote: "快照保存在管理器数据目录，不会修改游戏文件；不包含游戏本体与存档。",
            Token: "SNAP-CREATE",
            Groups: [],
            ConfirmLabel: "创建快照",
            FootNote: "确认后将复制并逐文件校验 SHA-256，请勿中途关闭管理器。",
            OnConfirmAsync: () =>
            {
                string purpose = string.IsNullOrWhiteSpace(inputBox.Text) ? "手动快照" : inputBox.Text.Trim();
                RequestCreateEnvironmentSnapshot?.Invoke(purpose);
                return Task.CompletedTask;
            },
            CustomContent: stack));
    }

    void OpenDeleteSnapshotConfirmModal(ModEnvironmentSnapshotEntry snap)
    {
        OpenTransactionModal(new NeonTransactionConfig(
            Title: $"删除环境快照 · {snap.Purpose}",
            Subtitle: $"释放 {ModEnvironmentSnapshots.FormatBytes(snap.TotalBytes)} 磁盘空间；不会影响当前游戏 MOD",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: "删除后将无法再恢复到此历史快照；当前游戏 mods 目录不受任何影响。",
            Token: snap.Id,
            Groups:
            [
                new NeonDiffGroup("待删除快照信息",
                [
                    new NeonDiffLine("del", "删除", $"{snap.Purpose} ({snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm})", $"{snap.FileCount:N0} 文件 · {ModEnvironmentSnapshots.FormatBytes(snap.TotalBytes)}")
                ])
            ],
            ConfirmLabel: "确认删除此快照",
            Danger: true,
            OnConfirmAsync: () =>
            {
                RequestDeleteEnvironmentSnapshot?.Invoke(snap.Id);
                return Task.CompletedTask;
            },
            PlainSummary: $"删除历史快照「{snap.Purpose}」可释放 {ModEnvironmentSnapshots.FormatBytes(snap.TotalBytes)} 磁盘空间；不会改动当前已安装或已停用的任何 MOD。"));
    }
}
