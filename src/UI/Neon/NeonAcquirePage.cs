using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpirePatch;

internal sealed partial class NeonWorkbenchView
{
    void BuildAcquirePage(StackPanel container, NeonWorkbenchSnapshot s)
    {
        var updatable = s.AllRows
            .Where(r => r.HasUpdate)
            .OrderByDescending(r => r.PriorityScore)
            .ThenBy(r => r.Mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var reviewNeeded = s.AllRows.Where(r =>
            !r.HasUpdate && (
                r.GitHubResult?.Status is "待核对" or "无法检测" ||
                r.WorkshopResult?.Status is "待首次核验" or "本地文件已变化")).ToArray();
        var unbound = s.AllRows
            .Where(r => r.IsUnbound)
            .OrderByDescending(r => r.CandidateSource != null ? 2 : 1)
            .ThenBy(r => r.Mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        // ---- Page Head ----
        var headActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var lastCheck = NeonWidgets.Text($"上次检查：{s.LastUpdateCheckTimeLabel}", 12, NeonPalette.InkMuted);
        lastCheck.VerticalAlignment = VerticalAlignment.Center;
        lastCheck.Margin = new Thickness(0, 0, 10, 0);
        headActions.Children.Add(lastCheck);

        var checkBtn = s.CheckingModUpdates
            ? NeonWidgets.CreateStyledButton(this, "取消检查…", "NeonCautionButton", () => RequestCancelUpdateCheck?.Invoke())
            : NeonWidgets.CreateStyledButton(this, "检查全部更新", "NeonPrimaryButton", () => RequestCheckAllModUpdates?.Invoke());
        checkBtn.IsEnabled = s.GameRoot != null && !s.ModInventoryDirty;
        AutomationProperties.SetName(checkBtn, checkBtn.Content?.ToString() ?? "检查全部更新");
        headActions.Children.Add(checkBtn);

        container.Children.Add(NeonWidgets.CreatePageHead(
            "获取与更新",
            "进入本页默认展示上次核验缓存，不强制联网阻塞导航；所有外部写入前均自动创建完整环境快照。",
            headActions));

        // ---- Intake Row (Workshop Supply Station + Safe Local ZIP Import) ----
        var intakeGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        bool narrow = ActualWidth > 0 && ActualWidth < 960;
        if (narrow)
        {
            intakeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            intakeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            intakeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        else
        {
            intakeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
            intakeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            intakeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Left Intake Card: Workshop / Supply Station
        var wsCard = BuildWorkshopIntakeCard(s);
        intakeGrid.Children.Add(wsCard);

        // Right Intake Card: Safe Local ZIP Import
        var zipCard = BuildZipIntakeCard(s);
        if (narrow) Grid.SetRow(zipCard, 2);
        else Grid.SetColumn(zipCard, 2);
        intakeGrid.Children.Add(zipCard);

        container.Children.Add(intakeGrid);

        // ---- Update Queue Surface Card ----
        var updHeadLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var updLamp = NeonWidgets.CreateStaticLamp(updatable.Length > 0 ? NeonTone.Amber : NeonTone.Sage);
        updLamp.Margin = new Thickness(0, 0, 8, 0);
        updHeadLeft.Children.Add(updLamp);
        var updTitle = NeonWidgets.Text("检测到可用更新", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        updTitle.Margin = new Thickness(0, 0, 8, 0);
        updHeadLeft.Children.Add(updTitle);
        updHeadLeft.Children.Add(NeonWidgets.CreateBadge(
            $"{updatable.Length} 项",
            updatable.Length > 0 ? NeonTone.Amber : NeonTone.Sage));

        UIElement? updHeadRight = updatable.Length > 0
            ? NeonWidgets.Text("逐项预览校验，写入前自动生成完整环境快照", 12, NeonPalette.InkMuted)
            : null;

        var updBody = new StackPanel();
        if (updatable.Length == 0 && reviewNeeded.Length == 0)
        {
            var calm = NeonWidgets.Text("✓ 所有已启用且已关联来源的 MOD 当前均无待处理更新。", 13, NeonPalette.Sage);
            calm.Margin = new Thickness(16, 20, 16, 20);
            updBody.Children.Add(calm);
        }
        else
        {
            int totalRows = updatable.Length + reviewNeeded.Length;
            int idx = 0;
            foreach (var r in updatable)
                updBody.Children.Add(BuildUpdatableRow(r, isLast: ++idx == totalRows));
            foreach (var r in reviewNeeded)
                updBody.Children.Add(BuildReviewUpdateRow(r, isLast: ++idx == totalRows));
        }
        container.Children.Add(NeonWidgets.CreateSurfaceCard(updHeadLeft, updHeadRight, updBody));

        // ---- Unbound Sources & Discovery Candidates Card ----
        var unbLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var unbTitle = NeonWidgets.Text("来源待确认 / 未绑定来源", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
        unbTitle.Margin = new Thickness(0, 0, 8, 0);
        unbLeft.Children.Add(unbTitle);
        unbLeft.Children.Add(NeonWidgets.CreateBadge($"{unbound.Length} 项", NeonTone.Neutral));

        var unbRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var unbNote = NeonWidgets.Text("所有自动发现与公共索引候选均需人工确认，防止同名搬运误绑", 12, NeonPalette.InkMuted);
        unbNote.VerticalAlignment = VerticalAlignment.Center;
        unbRight.Children.Add(unbNote);
        if (unbound.Length > 0 && s.GameRoot != null)
        {
            var discoverBtn = NeonWidgets.CreateStyledButton(this, "查找候选来源", "NeonGhostButton",
                () => RequestDiscoverGitHubCandidates?.Invoke(), "xs");
            discoverBtn.Margin = new Thickness(8, 0, 0, 0);
            unbRight.Children.Add(discoverBtn);
        }

        var unbBody = new StackPanel();
        if (unbound.Length == 0)
        {
            var calm = NeonWidgets.Text("✓ 所有本地 MOD 均已关联更新来源。", 13, NeonPalette.Sage);
            calm.Margin = new Thickness(16, 18, 16, 18);
            unbBody.Children.Add(calm);
        }
        else
        {
            for (int i = 0; i < unbound.Length; i++)
                unbBody.Children.Add(BuildUnboundSourceRow(unbound[i], isLast: i == unbound.Length - 1));
        }
        container.Children.Add(NeonWidgets.CreateSurfaceCard(unbLeft, unbRight, unbBody));
    }

    Border BuildWorkshopIntakeCard(NeonWorkbenchSnapshot s)
    {
        if (workshopInputHost == null || workshopUrlBox == null)
        {
            var created = NeonWidgets.CreateTextInput("粘贴 Workshop ID、steamcommunity.com 或 workshop.apricityx.top 链接…", height: 34);
            workshopInputHost = created.Host;
            workshopUrlBox = created.Input;
            AutomationProperties.SetName(workshopUrlBox, "输入创意工坊链接或 Workshop ID");
            workshopUrlBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    SubmitWorkshopUrl();
                }
            };
        }

        var stack = new StackPanel();
        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.Children.Add(NeonWidgets.Text("从尖塔补给站 / Steam 创意工坊获取", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold));
        var appTag = NeonWidgets.Text("AppID 2868840 · UGC 解密校验", 11, NeonPalette.InkMuted, mono: true);
        appTag.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(appTag, 1);
        topRow.Children.Add(appTag);
        stack.Children.Add(topRow);

        var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(workshopInputHost);

        var fetchBtn = NeonWidgets.CreateStyledButton(this, "获取预览", "NeonPrimaryButton", SubmitWorkshopUrl);
        fetchBtn.Height = 34;
        fetchBtn.Margin = new Thickness(8, 0, 0, 0);
        fetchBtn.IsEnabled = s.GameRoot != null && !s.ModInventoryDirty;
        AutomationProperties.SetName(fetchBtn, "获取创意工坊预览");
        Grid.SetColumn(fetchBtn, 1);
        inputRow.Children.Add(fetchBtn);
        stack.Children.Add(inputRow);

        if (demoUtilityControlsVisible)
        {
            var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var lbl = NeonWidgets.Text("试一试：", 11.5, NeonPalette.InkMuted);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            lbl.Margin = new Thickness(0, 0, 6, 4);
            presets.Children.Add(lbl);

            void AddChip(string text, string value)
            {
                var chip = NeonWidgets.CreateStyledButton(this, text, "NeonSecondaryButton", () =>
                {
                    if (workshopUrlBox != null) workshopUrlBox.Text = value;
                    workshopInlineFeedbackText = null;
                    if (currentSnapshot != null) RenderSecondaryPage("acquire", currentSnapshot, preserveScroll: true);
                }, "xs");
                chip.Height = 22;
                chip.Margin = new Thickness(0, 0, 6, 4);
                presets.Children.Add(chip);
            }
            AddChip("海克斯大乱斗 (ID: 3748937859)", "3748937859");
            AddChip("补给站链接格式", "https://workshop.apricityx.top/mod/2868840/3748937859");
            AddChip("非法链接测试", "https://example.com/bad-link");
            stack.Children.Add(presets);
        }

        if (!string.IsNullOrWhiteSpace(workshopInlineFeedbackText))
        {
            var fb = NeonWidgets.CreateAlert(
                workshopInlineFeedbackTone,
                NeonWidgets.Text(workshopInlineFeedbackText, 12,
                    workshopInlineFeedbackTone == NeonTone.Cinnabar ? NeonPalette.Cinnabar : NeonPalette.Sage,
                    wrap: true));
            fb.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(fb);
        }

        stack.Children.Add(NeonWidgets.Text(
            "边界说明：已获尖塔补给站授权接入；Manifest 签名验证与本机游戏分支自动匹配尚未实现，安装前请自行核对来源。",
            11.5, NeonPalette.InkMuted, wrap: true));

        return new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 15, 16, 15),
            Child = stack
        };
    }

    void SubmitWorkshopUrl()
    {
        string raw = (workshopUrlBox?.Text ?? "").Trim();
        if (!WorkshopModInstaller.TryParseWorkshopId(raw, out ulong wsId))
        {
            SetWorkshopFeedback("ID 或链接无效；仅接受数字 Workshop ID、steamcommunity.com/sharedfiles/filedetails 或 workshop.apricityx.top/mod/2868840/<ID> 链接。", NeonTone.Cinnabar);
            return;
        }
        SetWorkshopFeedback($"已识别 Workshop ID: {wsId}，正在通过尖塔补给站拉取清单与解密校验预览…", NeonTone.Sage);
        RequestPreviewWorkshopInstall?.Invoke(wsId);
    }

    Border BuildZipIntakeCard(NeonWorkbenchSnapshot s)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.Children.Add(NeonWidgets.Text("安全导入本地 ZIP", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold));
        var ruleTag = NeonWidgets.Text("单 MOD · 拒绝脚本与路径越界", 11, NeonPalette.InkMuted);
        ruleTag.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(ruleTag, 1);
        topRow.Children.Add(ruleTag);
        grid.Children.Add(topRow);

        var desc = NeonWidgets.Text(
            "导入前校验压缩包哈希、路径穿越、保留文件名、可执行脚本与必需 DLL/PCK，且不会覆盖现有目录。",
            12.5, NeonPalette.InkSecondary, wrap: true);
        desc.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(desc, 1);
        grid.Children.Add(desc);

        var dropLabel = NeonWidgets.Text("将单个 MOD 的 .zip 压缩包拖放到此处，或点击下方按钮选择", 12, NeonPalette.InkMuted, wrap: true);
        dropLabel.HorizontalAlignment = HorizontalAlignment.Center;
        dropLabel.TextAlignment = TextAlignment.Center;
        var dropZone = new Border
        {
            AllowDrop = true,
            Background = NeonPalette.BgGround,
            BorderBrush = NeonPalette.BorderMedium,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 12, 12, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = dropLabel
        };
        dropZone.PreviewDragOver += (_, e) =>
        {
            if (IsSingleZipDrop(e))
            {
                dropZone.Background = NeonPalette.SageBg;
                dropZone.BorderBrush = NeonPalette.SageBorder;
                dropLabel.Foreground = NeonPalette.Sage;
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        dropZone.DragLeave += (_, _) =>
        {
            dropZone.Background = NeonPalette.BgGround;
            dropZone.BorderBrush = NeonPalette.BorderMedium;
            dropLabel.Foreground = NeonPalette.InkMuted;
        };
        dropZone.Drop += (_, e) =>
        {
            dropZone.Background = NeonPalette.BgGround;
            dropZone.BorderBrush = NeonPalette.BorderMedium;
            dropLabel.Foreground = NeonPalette.InkMuted;
            HandleZipDropData(e);
        };
        Grid.SetRow(dropZone, 2);
        grid.Children.Add(dropZone);

        var btnRow = new WrapPanel();
        var importBtn = NeonWidgets.CreateStyledButton(this, "选择本地 ZIP 并预览导入", "NeonSecondaryButton",
            () => RequestChooseLocalZipImport?.Invoke());
        importBtn.IsEnabled = s.GameRoot != null && !s.ModInventoryDirty;
        AutomationProperties.SetName(importBtn, "选择本地 ZIP 并预览导入");
        btnRow.Children.Add(importBtn);
        Grid.SetRow(btnRow, 3);
        grid.Children.Add(btnRow);

        return new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 15, 16, 15),
            Child = grid
        };
    }

    Border BuildUpdatableRow(NeonWorkbenchRow r, bool isLast)
    {
        bool isGh = r.GitHubResult?.Status == "有更新";
        string srcLabel = isGh
            ? $"GitHub · {r.GitHubRepo}"
            : $"创意工坊 · WS #{r.WorkshopId}";
        string note = isGh
            ? (!string.IsNullOrWhiteSpace(r.GitHubResult?.Message) ? r.GitHubResult!.Message : "检测到新版正式 Release。")
            : (!string.IsNullOrWhiteSpace(r.WorkshopResult?.Message) ? r.WorkshopResult!.Message : "创意工坊 Manifest 已变化，获取预览后将解密核对实际 MOD 版本。");

        var outerGrid = new Grid();
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outerGrid.Children.Add(new Border { Background = NeonPalette.Amber });

        var rowGrid = new Grid { Margin = new Thickness(14, 13, 16, 13) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var nameLine = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameTb = NeonWidgets.Text(r.Mod.Name, 14, NeonPalette.InkPrimary, FontWeights.SemiBold);
        nameTb.Margin = new Thickness(0, 0, 8, 2);
        nameLine.Children.Add(nameTb);

        if (isGh && !string.IsNullOrWhiteSpace(r.GitHubResult?.LatestVersion))
        {
            var diffPill = NeonWidgets.CreateVersionDiffPill(
                r.Mod.Version,
                r.GitHubResult!.LatestVersion,
                warn: true,
                isUpdate: true);
            diffPill.Margin = new Thickness(0, 0, 0, 2);
            nameLine.Children.Add(diffPill);
        }
        else
        {
            var wsBadge = NeonWidgets.CreateBadge("Manifest 已变化 · 需下载预览确认版本", NeonTone.Amber);
            wsBadge.Margin = new Thickness(0, 0, 0, 2);
            nameLine.Children.Add(wsBadge);
        }
        info.Children.Add(nameLine);

        var desc = NeonWidgets.Text(note, 12.5, NeonPalette.InkSecondary, wrap: true);
        desc.Margin = new Thickness(0, 3, 0, 0);
        info.Children.Add(desc);

        var meta = NeonWidgets.Text(
            $"{srcLabel} · {(isGh ? "校验官方 SHA-256" : "UGC 分块校验")}",
            11.5, NeonPalette.InkMuted, mono: true);
        meta.Margin = new Thickness(0, 5, 0, 0);
        info.Children.Add(meta);
        rowGrid.Children.Add(info);

        string ctaLabel = isGh && !string.IsNullOrWhiteSpace(r.GitHubResult?.LatestVersion)
            ? $"预览更新至 v{NeonVersionDiff.StripPrefix(r.GitHubResult!.LatestVersion)}"
            : "下载并预览更新";
        var updBtn = NeonWidgets.CreateStyledButton(this, ctaLabel, "NeonPrimaryButton",
            () => RequestPreviewUpdateMod?.Invoke(r.Mod), "sm");
        updBtn.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(updBtn, $"{ctaLabel} {r.Mod.Id}");
        Grid.SetColumn(updBtn, 1);
        rowGrid.Children.Add(updBtn);

        Grid.SetColumn(rowGrid, 1);
        outerGrid.Children.Add(rowGrid);

        var border = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
            Child = outerGrid
        };
        border.MouseEnter += (_, _) => border.Background = NeonPalette.BgHover;
        border.MouseLeave += (_, _) => border.Background = NeonPalette.BgSurface;
        return border;
    }

    Border BuildReviewUpdateRow(NeonWorkbenchRow r, bool isLast)
    {
        var rowGrid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var nameLine = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameTb = NeonWidgets.Text(r.Mod.Name, 14, NeonPalette.InkPrimary, FontWeights.SemiBold);
        nameTb.Margin = new Thickness(0, 0, 8, 2);
        nameLine.Children.Add(nameTb);
        string statusTag = r.GitHubResult?.Status is "待核对" or "无法检测"
            ? $"版本格式待核对 · 本地 v{NeonVersionDiff.StripPrefix(r.Mod.Version)}"
            : $"{r.WorkshopResult?.Status ?? "需核对"} · 本地 v{NeonVersionDiff.StripPrefix(r.Mod.Version)}";
        nameLine.Children.Add(NeonWidgets.CreateBadge(statusTag, NeonTone.Neutral));
        info.Children.Add(nameLine);

        string msg = r.GitHubResult?.Message ?? r.WorkshopResult?.Message ?? "版本格式无法自动比较，请前往发布页或工作台核对。";
        var desc = NeonWidgets.Text(msg, 12.5, NeonPalette.InkSecondary, wrap: true);
        desc.Margin = new Thickness(0, 3, 0, 0);
        info.Children.Add(desc);
        rowGrid.Children.Add(info);

        var openBtn = NeonWidgets.CreateStyledButton(this, "在工作台查看", "NeonSecondaryButton",
            () => FocusMod(r.Mod.Id), "sm");
        openBtn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(openBtn, 1);
        rowGrid.Children.Add(openBtn);

        var border = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
            Child = rowGrid
        };
        border.MouseEnter += (_, _) => border.Background = NeonPalette.BgHover;
        border.MouseLeave += (_, _) => border.Background = NeonPalette.BgSurface;
        return border;
    }

    Border BuildUnboundSourceRow(NeonWorkbenchRow r, bool isLast)
    {
        var cand = r.CandidateSource;
        var rowGrid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var nameLine = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameTb = NeonWidgets.Text(r.Mod.Name, 14, NeonPalette.InkPrimary, FontWeights.SemiBold);
        nameTb.Margin = new Thickness(0, 0, 7, 2);
        nameLine.Children.Add(nameTb);
        var idTb = NeonWidgets.Text($"{r.Mod.Id} · v{NeonVersionDiff.StripPrefix(r.Mod.Version)}", 11.5, NeonPalette.InkMuted, mono: true);
        idTb.Margin = new Thickness(0, 0, 8, 2);
        idTb.VerticalAlignment = VerticalAlignment.Center;
        nameLine.Children.Add(idTb);

        if (cand != null)
        {
            nameLine.Children.Add(NeonWidgets.CreateBadge(
                cand.KindLabel,
                cand.IsVerified ? NeonTone.Sage : cand.IsAmbiguous ? NeonTone.Amber : NeonTone.Neutral));
        }
        else
        {
            nameLine.Children.Add(NeonWidgets.CreateBadge("尚未发现公开候选", NeonTone.Neutral));
        }
        info.Children.Add(nameLine);

        string descText = cand != null
            ? $"{cand.Note}（候选：{cand.TargetLabel}）"
            : "未在本地清单或 README 中读取到公开地址，可手动填入 GitHub 仓库或 Workshop ID。";
        var desc = NeonWidgets.Text(descText, 12.5, NeonPalette.InkSecondary, wrap: true);
        desc.Margin = new Thickness(0, 3, 0, 0);
        info.Children.Add(desc);
        rowGrid.Children.Add(info);

        Button cta;
        if (cand != null)
        {
            cta = NeonWidgets.CreateStyledButton(this, "核对并确认关联", "NeonPrimaryButton",
                () => OpenBindCandidateConfirmModal(r, cand), "sm");
            AutomationProperties.SetName(cta, "核对并确认关联 " + r.Mod.Id);
        }
        else
        {
            cta = NeonWidgets.CreateStyledButton(this, "去详情手动绑定", "NeonSecondaryButton",
                () => FocusMod(r.Mod.Id), "sm");
        }
        cta.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(cta, 1);
        rowGrid.Children.Add(cta);

        var border = new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
            Child = rowGrid
        };
        border.MouseEnter += (_, _) => border.Background = NeonPalette.BgHover;
        border.MouseLeave += (_, _) => border.Background = NeonPalette.BgSurface;
        return border;
    }

    void OpenBindCandidateConfirmModal(NeonWorkbenchRow row, NeonCandidateSource cand)
    {
        OpenTransactionModal(new NeonTransactionConfig(
            Title: $"确认关联更新来源 · {row.Mod.Name}",
            Subtitle: "来源关联按游戏目录保存在管理器数据区，不改写 MOD 文件",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: cand.IsAmbiguous
                ? "此条目在公共索引中标记为同名/多候选条目，请再次核对作者名一致后再绑定。"
                : "已读取到候选来源；确认后仅用于只读更新检测，不会自动下载或改动游戏文件。",
            Token: "SOURCE-BIND",
            Groups:
            [
                new NeonDiffGroup("关联内容",
                [
                    new NeonDiffLine("add", "绑定", cand.TargetLabel, "仅影响更新检测")
                ])
            ],
            ConfirmLabel: "确认关联此来源",
            OnConfirmAsync: () =>
            {
                if (!string.IsNullOrWhiteSpace(cand.GitHubRepository))
                    RequestQuickBindGitHub?.Invoke(row.Mod, cand.GitHubRepository!);
                else if (cand.WorkshopId.HasValue)
                    RequestQuickBindWorkshop?.Invoke(row.Mod, cand.WorkshopId.Value);
                return Task.CompletedTask;
            }));
    }

    static bool IsSingleZipDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1) return false;
        if (!string.Equals(Path.GetExtension(files[0]), ".zip", StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(files[0]);
    }

    void HandleZipDropData(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        e.Handled = true;
        if (files.Length != 1 || !string.Equals(Path.GetExtension(files[0]), ".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(files[0]))
        {
            ShowToast("请拖入单个 .zip 格式的 MOD 压缩包。", NeonTone.Amber);
            return;
        }
        if (currentSnapshot?.GameRoot == null || currentSnapshot.ModInventoryDirty)
        {
            ShowToast("请先选择游戏目录并刷新清单后再导入 ZIP。", NeonTone.Amber);
            return;
        }
        RequestPreviewLocalZipPath?.Invoke(files[0]);
    }
}
