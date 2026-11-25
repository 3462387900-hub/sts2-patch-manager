using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SpirePatch;

internal sealed partial class NeonWorkbenchView
{
    void BuildPatchesPage(StackPanel container, NeonWorkbenchSnapshot s)
    {
        var allPatches = s.AllCatalogPatches;
        var looseFixes = allPatches.Where(p => p.IsLooseInModsRoot).ToArray();

        // Compute honest selection closure (matches SelectionPlanner.Resolve rules)
        var (includedIds, autoAddedReasons) = ComputePatchSelectionClosure(allPatches, s);

        // Tier 1: Needs Action (待修复 / 需联动 / 需核对 / 不兼容 / 已安装但健康过期需重新检查)
        var actionTier = allPatches.Where(p =>
            p.StatusLabel is "待修复" or "需联动" or "需核对" or "已确认不兼容" or "疑似上游修复" ||
            (p.InstalledMod is { Enabled: true } && p.StatusLabel == "需重新检查")).ToArray();

        // Tier 2: Verified Safe Retirement — ONLY when lifecycle health is currently valid!
        var retireTier = s.IsLifecycleHealthValid
            ? allPatches.Where(p => p.CanRetire && !actionTier.Contains(p)).ToArray()
            : [];

        // Tier 3: Quiet / Verified / Disabled / Not Applicable
        var quietTier = allPatches
            .Where(p => !actionTier.Contains(p) && !retireTier.Contains(p))
            .OrderByDescending(p => p.IsVerifiedHealthy ? 4 : p.InstalledMod is { Enabled: true } ? 3 : p.InstalledMod != null ? 2 : 1)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        int relevantCount = allPatches.Count(p => p.InstalledMod != null || p.StatusLabel != "不适用");

        // ---- Page Head ----
        var headActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom };
        var refreshBtn = NeonWidgets.CreateStyledButton(
            this,
            s.OnlineRefreshing ? "正在刷新补丁源…" : "刷新在线补丁源",
            "NeonSecondaryButton",
            () => RequestRefreshOnlinePatches?.Invoke());
        refreshBtn.IsEnabled = !s.OnlineRefreshing;
        refreshBtn.Margin = new Thickness(0, 0, 8, 0);
        headActions.Children.Add(refreshBtn);

        if (looseFixes.Length > 0)
        {
            var orgBtn = NeonWidgets.CreateStyledButton(
                this,
                $"整理散落独立补丁 ({looseFixes.Length})",
                "NeonSecondaryButton",
                () => RequestPreviewOrganizePatches?.Invoke());
            orgBtn.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetName(orgBtn, $"整理散落独立补丁 ({looseFixes.Length})");
            headActions.Children.Add(orgBtn);
        }

        var previewBtn = NeonWidgets.CreateStyledButton(
            this,
            includedIds.Count > 0 ? $"预览安装方案 ({includedIds.Count})" : "预览安装方案",
            "NeonPrimaryButton",
            () =>
            {
                if (selectedPatchIds.Count > 0)
                    RequestPreviewPatchInstall?.Invoke(selectedPatchIds.ToArray());
            });
        previewBtn.IsEnabled = selectedPatchIds.Count > 0 && s.GameRoot != null && !s.ModInventoryDirty;
        if (!previewBtn.IsEnabled)
        {
            previewBtn.ToolTip = s.GameRoot == null
                ? "请先选择游戏目录"
                : s.ModInventoryDirty
                    ? "检测到目录变化，请先重新扫描"
                    : "先在下方勾选「待修复」或「需联动」补丁后再预览安装方案";
        }
        AutomationProperties.SetName(previewBtn, previewBtn.Content?.ToString() ?? "预览安装方案");
        headActions.Children.Add(previewBtn);

        container.Children.Add(NeonWidgets.CreatePageHead(
            "修复补丁",
            $"已收录 {s.OnlineCatalogCount} 项本版审核载荷（当前环境关联展示 {relevantCount} 项）· 按“是否需要你处理”分层，不把已正常项目铺满屏幕。",
            headActions));

        // ---- Scope Note + Orphan Auto-Stop Toggle ----
        var scopeTb = new TextBlock
        {
            FontFamily = NeonPalette.SansFont,
            FontSize = 12,
            Foreground = NeonPalette.InkSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        };
        scopeTb.Inlines.Add(new Run("推荐是精确文件适配结论") { FontWeight = FontWeights.SemiBold, Foreground = NeonPalette.InkPrimary });
        scopeTb.Inlines.Add(new Run("：基于 "));
        scopeTb.Inlines.Add(new Run("patch.json") { FontFamily = NeonPalette.MonoFont });
        scopeTb.Inlines.Add(new Run(" 与 DLL/PCK 的 SHA-256 指纹核验，不凭版本号猜测上游已修复 Bug。独立补丁停用与退役均移至保留区，可原样恢复。"));

        var orphanRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var orphanCb = NeonWidgets.CreateJellyToggleCheckBox(
            isChecked: s.OrphanAutoStopEnabled,
            automationName: "目标 MOD 删除后自动停用孤立补丁",
            onToggled: nextVal => RequestToggleOrphanAutoStop?.Invoke(nextVal),
            previousChecked: lastOrphanAutoStopState,
            animHost: this);
        lastOrphanAutoStopState = s.OrphanAutoStopEnabled;
        orphanCb.Margin = new Thickness(0, 0, 8, 0);
        var orphanLbl = NeonWidgets.Text("目标 MOD 删除后自动停用孤立补丁", 12, NeonPalette.InkPrimary, FontWeights.Medium);
        orphanLbl.VerticalAlignment = VerticalAlignment.Center;
        orphanLbl.Cursor = Cursors.Hand;
        orphanLbl.MouseLeftButtonDown += (_, _) => orphanCb.IsChecked = !(orphanCb.IsChecked == true);
        orphanRow.Children.Add(orphanCb);
        orphanRow.Children.Add(orphanLbl);

        container.Children.Add(NeonWidgets.CreateScopeNote("ℹ", scopeTb, orphanRow));

        if (s.GameRoot == null)
        {
            container.Children.Add(BuildEmptyState(
                "尚未关联游戏目录",
                "请先选择包含 SlayTheSpire2.exe 的目录，再扫描本地 MOD 与补丁指纹。",
                "选择游戏目录",
                () => RequestChooseGameRoot?.Invoke()));
            return;
        }

        // ---- Compact Search & Status Filter Strip ----
        EnsurePatchesSearchControl();
        var filterBar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var patchPills = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (fid, flabel) in new[]
        {
            ("all", $"全部 ({allPatches.Length})"),
            ("action", $"需处理 ({actionTier.Length})"),
            ("retire", $"可退役 ({retireTier.Length})"),
            ("ready", $"已就绪与不适用 ({quietTier.Length})")
        })
        {
            bool active = patchesStatusFilter == fid;
            var pill = new Button
            {
                Content = flabel,
                Height = 25,
                Padding = new Thickness(9, 0, 9, 0),
                Margin = new Thickness(0, 0, 6, 4),
                FontFamily = NeonPalette.SansFont,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Background = active ? NeonPalette.InkPrimary : Brushes.Transparent,
                Foreground = active ? NeonPalette.BgSurface : NeonPalette.InkSecondary,
                BorderBrush = active ? NeonPalette.InkPrimary : NeonPalette.BorderSubtle,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(12, active ? NeonPalette.InkPrimary : NeonPalette.BgWell, active ? NeonPalette.InkPrimary : NeonPalette.BorderMedium)
            };
            string targetFilter = fid;
            pill.Click += (_, _) =>
            {
                if (patchesStatusFilter == targetFilter) return;
                patchesStatusFilter = targetFilter;
                if (targetFilter == "ready") patchesShowReady = true;
                if (currentSnapshot != null) RenderSecondaryPage("patches", currentSnapshot, preserveScroll: false);
            };
            patchPills.Children.Add(pill);
        }
        filterBar.Children.Add(patchPills);

        patchesSearchHost!.Width = 260;
        patchesSearchHost.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(patchesSearchHost, 1);
        filterBar.Children.Add(patchesSearchHost);
        container.Children.Add(filterBar);

        static bool MatchesPatchQuery(NeonRelatedPatch p, string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return true;
            if (p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.PatchId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Summary.Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;
            return p.RelatedModIds?.Any(id => id.Contains(q, StringComparison.OrdinalIgnoreCase)) == true;
        }
        string pq = patchesFilterQuery.Trim();
        var filteredAction = actionTier.Where(p => MatchesPatchQuery(p, pq)).ToArray();
        var filteredRetire = retireTier.Where(p => MatchesPatchQuery(p, pq)).ToArray();
        var filteredQuiet = quietTier.Where(p => MatchesPatchQuery(p, pq)).ToArray();
        bool showAction = patchesStatusFilter is "all" or "action";
        bool showRetire = patchesStatusFilter is "all" or "retire";
        bool showQuiet = patchesStatusFilter is "all" or "ready";
        bool effectiveShowReady = patchesShowReady || patchesStatusFilter == "ready" || pq.Length > 0;

        // ---- Tier 1: Needs Action ----
        if (showAction && (patchesStatusFilter == "action" || pq.Length == 0 || filteredAction.Length > 0))
        {
            var tier1Left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var tier1Lamp = NeonWidgets.CreateStaticLamp(filteredAction.Length > 0 ? NeonTone.Amber : NeonTone.Sage);
            tier1Lamp.Margin = new Thickness(0, 0, 8, 0);
            tier1Left.Children.Add(tier1Lamp);
            var tier1Title = NeonWidgets.Text("需要你处理", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
            tier1Title.Margin = new Thickness(0, 0, 8, 0);
            tier1Left.Children.Add(tier1Title);
            tier1Left.Children.Add(NeonWidgets.CreateBadge(
                $"{filteredAction.Length} 项",
                filteredAction.Length > 0 ? NeonTone.Amber : NeonTone.Sage));

            int checkableCount = filteredAction.Count(p => p.ScanState?.Status is "待修复" or "需联动");
            UIElement tier1Right = autoAddedReasons.Count > 0
                ? NeonWidgets.CreateBadge($"已自动联动带入 {autoAddedReasons.Count} 项必需关联补丁", NeonTone.Indigo)
                : checkableCount == 0 && filteredAction.Length > 0
                    ? NeonWidgets.Text("当前均为需核对项，请先逐项查看判断依据", 12, NeonPalette.InkMuted)
                    : NeonWidgets.Text("勾选待装修复后，必要关联补丁将自动绑定", 12, NeonPalette.InkMuted);

            var tier1Body = new StackPanel();
            if (filteredAction.Length == 0)
            {
                var calm = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 18, 16, 18) };
                var okMark = NeonWidgets.Text("✓", 13, NeonPalette.Sage, FontWeights.Bold);
                okMark.Margin = new Thickness(0, 0, 8, 0);
                calm.Children.Add(okMark);
                calm.Children.Add(NeonWidgets.Text(
                    pq.Length > 0 ? "没有匹配当前搜索词的待处理补丁。" : "当前没有待安装、需联动或未核对的修复补丁。",
                    13, NeonPalette.Sage));
                tier1Body.Children.Add(calm);
            }
            else
            {
                for (int i = 0; i < filteredAction.Length; i++)
                {
                    tier1Body.Children.Add(BuildPatchActionRow(
                        filteredAction[i], s, autoAddedReasons, isLast: i == filteredAction.Length - 1));
                }
            }
            container.Children.Add(NeonWidgets.CreateSurfaceCard(tier1Left, tier1Right, tier1Body));
        }

        // ---- Tier 2: Safe Retirement (ONLY when health is valid and SHA-256 matched!) ----
        if (showRetire && filteredRetire.Length > 0)
        {
            var tier2Left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var tier2Lamp = NeonWidgets.CreateStaticLamp(NeonTone.Sage);
            tier2Lamp.Margin = new Thickness(0, 0, 8, 0);
            tier2Left.Children.Add(tier2Lamp);
            var tier2Title = NeonWidgets.Text("可安全退役（上游已验证修复）", 13.5, NeonPalette.InkPrimary, FontWeights.SemiBold);
            tier2Title.Margin = new Thickness(0, 0, 8, 0);
            tier2Left.Children.Add(tier2Title);
            tier2Left.Children.Add(NeonWidgets.CreateBadge($"{filteredRetire.Length} 项", NeonTone.Sage));

            var tier2Right = NeonWidgets.Text("仅当本地原 MOD 文件精确匹配已审核 SHA-256 时才出现", 12, NeonPalette.InkMuted);

            var tier2Body = new StackPanel();
            for (int i = 0; i < filteredRetire.Length; i++)
            {
                tier2Body.Children.Add(BuildPatchRetireRow(filteredRetire[i], s, isLast: i == filteredRetire.Length - 1));
            }
            container.Children.Add(NeonWidgets.CreateSurfaceCard(tier2Left, tier2Right, tier2Body));
        }
        else if (patchesStatusFilter == "retire" && filteredRetire.Length == 0)
        {
            container.Children.Add(BuildEmptyState(
                s.IsLifecycleHealthValid ? "当前没有可安全退役的补丁" : "尚未完成补丁健康核验",
                s.IsLifecycleHealthValid ? "没有检测到精确匹配上游 SHA-256 的已修复原 MOD。" : "请先刷新扫描并核验本地指纹与上游证据库。",
                "查看全部补丁",
                () => { patchesStatusFilter = "all"; if (currentSnapshot != null) RenderSecondaryPage("patches", currentSnapshot, preserveScroll: false); }));
        }

        // ---- Tier 3: Quiet / Installed / Disabled / Not Applicable (Collapsible) ----
        if (showQuiet && (patchesStatusFilter == "ready" || pq.Length == 0 || filteredQuiet.Length > 0))
        {
            var tier3Card = new Border
            {
                Background = NeonPalette.BgSurface,
                BorderBrush = NeonPalette.BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 0, 14)
            };
            var tier3Stack = new StackPanel();

            var trigGrid = new Grid();
            trigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            trigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string looseHint = looseFixes.Length > 0 ? $" · 含 {looseFixes.Length} 项散落根目录待整理" : "";
            var trigLeft = NeonWidgets.Text($"已就绪、已停用与不适用（{filteredQuiet.Length} 项）{looseHint}", 13, NeonPalette.InkSecondary, FontWeights.SemiBold);
            trigLeft.VerticalAlignment = VerticalAlignment.Center;
            trigGrid.Children.Add(trigLeft);
            var trigRight = NeonWidgets.Text(effectiveShowReady ? "收起 ▴" : "展开查看 ▾", 12, NeonPalette.InkSecondary, FontWeights.Medium);
            trigRight.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trigRight, 1);
            trigGrid.Children.Add(trigRight);

            var trigBorder = new Border
            {
                Background = NeonPalette.BgElevated,
                BorderBrush = NeonPalette.BorderSubtle,
                BorderThickness = effectiveShowReady ? new Thickness(0, 0, 0, 1) : new Thickness(0),
                Padding = new Thickness(16, 11, 16, 11),
                Child = trigGrid
            };
            var trigBtn = new Button
            {
                Content = trigBorder,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Template = NeonWidgets.CreateBareButtonTemplate()
            };
            AutomationProperties.SetName(trigBtn, effectiveShowReady ? "收起已就绪与不适用补丁" : "展开查看已就绪与不适用补丁");
            trigBtn.MouseEnter += (_, _) => { trigLeft.Foreground = NeonPalette.InkPrimary; trigRight.Foreground = NeonPalette.InkPrimary; };
            trigBtn.MouseLeave += (_, _) => { trigLeft.Foreground = NeonPalette.InkSecondary; trigRight.Foreground = NeonPalette.InkSecondary; };
            trigBtn.Click += (_, _) =>
            {
                patchesShowReady = !effectiveShowReady;
                if (currentSnapshot != null) RenderSecondaryPage("patches", currentSnapshot, preserveScroll: true);
            };
            tier3Stack.Children.Add(trigBtn);

            if (effectiveShowReady)
            {
                if (filteredQuiet.Length == 0)
                {
                    var calm = NeonWidgets.Text("没有匹配当前筛选条件的已就绪或不适用补丁。", 12.5, NeonPalette.InkMuted);
                    calm.Margin = new Thickness(16, 16, 16, 16);
                    tier3Stack.Children.Add(calm);
                }
                else
                {
                    for (int i = 0; i < filteredQuiet.Length; i++)
                        tier3Stack.Children.Add(BuildPatchQuietRow(filteredQuiet[i], s, isLast: i == filteredQuiet.Length - 1));
                }
            }

            tier3Card.Child = tier3Stack;
            container.Children.Add(tier3Card);
        }

        if (pq.Length > 0 && filteredAction.Length == 0 && filteredRetire.Length == 0 && filteredQuiet.Length == 0)
        {
            container.Children.Add(BuildEmptyState(
                "没有匹配的修复补丁",
                $"未找到包含「{pq}」的补丁，可以清空关键词查看全部 {allPatches.Length} 项。",
                "清空搜索",
                () =>
                {
                    patchesFilterQuery = "";
                    if (patchesSearchBox != null) patchesSearchBox.Text = "";
                    patchesStatusFilter = "all";
                    if (currentSnapshot != null) RenderSecondaryPage("patches", currentSnapshot, preserveScroll: false);
                }));
        }
    }

    void EnsurePatchesSearchControl()
    {
        if (patchesSearchHost != null && patchesSearchBox != null) return;
        var created = NeonWidgets.CreateTextInput("搜索补丁名、ID 或关联 MOD…", height: 30);
        patchesSearchHost = created.Host;
        patchesSearchBox = created.Input;
        patchesSearchBox.Text = patchesFilterQuery;
        AutomationProperties.SetName(patchesSearchBox, "搜索修复补丁");
        patchesSearchBox.TextChanged += (_, _) =>
        {
            string next = patchesSearchBox.Text;
            if (string.Equals(patchesFilterQuery, next, StringComparison.Ordinal)) return;
            patchesFilterQuery = next;
            if (activeTab == "patches" && currentSnapshot != null)
                RenderSecondaryPage("patches", currentSnapshot, preserveScroll: false);
        };
    }

    (HashSet<string> Included, Dictionary<string, string> AutoAddedReasons) ComputePatchSelectionClosure(
        NeonRelatedPatch[] allPatches,
        NeonWorkbenchSnapshot s)
    {
        var included = new HashSet<string>(selectedPatchIds, StringComparer.OrdinalIgnoreCase);
        var autoReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (included.Count == 0) return (included, autoReasons);

        var byId = allPatches.ToDictionary(p => p.PatchId, StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (string id in included.ToArray())
            {
                if (!byId.TryGetValue(id, out var patch)) continue;
                foreach (string rel in patch.RequiredPatchIds ?? [])
                {
                    if (included.Add(rel))
                    {
                        autoReasons[rel] = $"由 {patch.Name} 联动带入";
                        changed = true;
                    }
                }
            }
        } while (changed);

        return (included, autoReasons);
    }

    Border BuildPatchActionRow(
        NeonRelatedPatch p,
        NeonWorkbenchSnapshot s,
        Dictionary<string, string> autoAddedReasons,
        bool isLast)
    {
        bool isDanger = p.StatusLabel is "需核对" or "已确认不兼容";
        bool canCheck = p.ScanState?.Status is "待修复" or "需联动";
        bool isManual = selectedPatchIds.Contains(p.PatchId);
        bool isAuto = autoAddedReasons.TryGetValue(p.PatchId, out string? autoReason);
        bool isChecked = isManual || isAuto;

        var outerGrid = new Grid();
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        outerGrid.Children.Add(new Border
        {
            Background = isDanger ? NeonPalette.Cinnabar : NeonPalette.Amber
        });

        var rowGrid = new Grid { Margin = new Thickness(14, 13, 16, 13) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left check / ! indicator
        if (canCheck)
        {
            var cb = new CheckBox
            {
                Style = (Style)FindResource("NeonCheckBox"),
                IsChecked = isChecked,
                IsEnabled = !isAuto,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 0, 0),
                ToolTip = isAuto ? autoReason : "选择此项纳入安装方案"
            };
            AutomationProperties.SetName(cb, "选择补丁 " + p.Name);
            cb.Checked += (_, _) =>
            {
                selectedPatchIds.Add(p.PatchId);
                if (currentSnapshot != null) RenderSecondaryPage("patches", currentSnapshot, preserveScroll: true);
            };
            cb.Unchecked += (_, _) =>
            {
                selectedPatchIds.Remove(p.PatchId);
                if (currentSnapshot != null) RenderSecondaryPage("patches", currentSnapshot, preserveScroll: true);
            };
            rowGrid.Children.Add(cb);
        }
        else
        {
            var bang = NeonWidgets.Text("!", 14, isDanger ? NeonPalette.Cinnabar : NeonPalette.Amber, FontWeights.Bold);
            bang.HorizontalAlignment = HorizontalAlignment.Center;
            bang.VerticalAlignment = VerticalAlignment.Top;
            bang.Margin = new Thickness(0, 1, 6, 0);
            rowGrid.Children.Add(bang);
        }

        // Middle info stack
        var info = new StackPanel { Margin = new Thickness(4, 0, 14, 0) };
        var nameLine = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameTb = NeonWidgets.Text(p.Name, 14, NeonPalette.InkPrimary, FontWeights.SemiBold);
        nameTb.Margin = new Thickness(0, 0, 7, 2);
        nameTb.VerticalAlignment = VerticalAlignment.Center;
        nameLine.Children.Add(nameTb);

        var idTb = NeonWidgets.Text($"{p.PatchId} · v{NeonVersionDiff.StripPrefix(p.Version)}", 11.5, NeonPalette.InkMuted, mono: true);
        idTb.Margin = new Thickness(0, 0, 8, 2);
        idTb.VerticalAlignment = VerticalAlignment.Center;
        nameLine.Children.Add(idTb);

        // Side-by-side Status + Type strip (+ optional auto-linked badge)
        var statusAndType = NeonWidgets.CreatePatchStatusAndTypeStrip(
            p.StatusLabel,
            p.StatusTone,
            p.IsStandalone,
            extraTag: isAuto ? $"自动联动 · {autoReason}" : p.IsLooseInModsRoot ? "散落 mods/ 根目录" : null,
            extraTone: isAuto ? NeonTone.Indigo : NeonTone.Amber);
        statusAndType.Margin = new Thickness(0, 0, 0, 2);
        nameLine.Children.Add(statusAndType);
        info.Children.Add(nameLine);

        var desc = NeonWidgets.Text(p.Summary, 12.5, NeonPalette.InkSecondary, wrap: true);
        desc.Margin = new Thickness(0, 3, 0, 0);
        info.Children.Add(desc);

        // Precision character-level Version Diff pill when reviewed rule requires newer prerequisite
        if (!string.IsNullOrEmpty(p.ReviewedRequiredVersion) &&
            !string.IsNullOrEmpty(p.ReviewedRequiredModId) &&
            !string.IsNullOrEmpty(p.ReviewedRequiredActualVersion))
        {
            string reqModId = p.ReviewedRequiredModId!;
            var diffWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var diffPill = NeonWidgets.CreateVersionDiffPill(
                p.ReviewedRequiredActualVersion,
                p.ReviewedRequiredVersion,
                "≥",
                prefix: $"依赖提示：{reqModId}",
                warn: false,
                suffix: $"· 建议先在工作台更新 {reqModId} →");
            var diffBtn = new Button
            {
                Content = diffPill,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = $"点击定位到 {reqModId} 并查看更新",
                Template = NeonWidgets.CreateInteractiveBareButtonTemplate(pressScale: 0.985, hoverOpacity: 0.92)
            };
            AutomationProperties.SetName(diffBtn, $"前往工作台更新 {reqModId}");
            diffBtn.Click += (_, _) => FocusMod(reqModId, openUpdatePreview: true);
            diffWrap.Children.Add(diffBtn);
            info.Children.Add(diffWrap);
        }

        // Pending MOD file change warning (from real ModHistory.PendingReview)
        if (p.PendingModChange != null)
        {
            var ch = p.PendingModChange;
            var alertTb = new TextBlock { FontFamily = NeonPalette.SansFont, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            alertTb.Inlines.Add(new Run("检测到关联 MOD "));
            alertTb.Inlines.Add(new Run(ch.ModId) { FontFamily = NeonPalette.MonoFont, FontWeight = FontWeights.SemiBold });
            string verPart = ch.OldVersion == ch.NewVersion
                ? $" 文件发生同版本重打包（变更 {ch.ChangedFiles} 个文件）"
                : $" 从 {ch.OldVersion} 变更为 {ch.NewVersion}（变更 {ch.ChangedFiles} 个文件）";
            alertTb.Inlines.Add(new Run(verPart + "，但上游修复证据库暂无匹配记录，不推测 Bug 已解决。"));
            var alert = NeonWidgets.CreateAlert(NeonTone.Amber, alertTb);
            alert.Margin = new Thickness(0, 7, 0, 0);
            info.Children.Add(alert);
        }
        else if (p.StatusLabel == "需重新检查")
        {
            var staleAlert = NeonWidgets.CreateAlert(NeonTone.Amber, NeonWidgets.Text(p.VerdictText, 12, NeonPalette.Amber, wrap: true));
            staleAlert.Margin = new Thickness(0, 7, 0, 0);
            info.Children.Add(staleAlert);
        }

        // Meta row
        var metaParts = new List<string>();
        if (p.RelatedModIds is { Length: > 0 })
            metaParts.Add("关联：" + string.Join("、", p.RelatedModIds));
        if (p.RequiredPatchIds is { Length: > 0 })
            metaParts.Add("必须联动：" + string.Join("、", p.RequiredPatchIds));
        if (s.LastScanTime.HasValue)
            metaParts.Add("上次核验 " + s.LastScanTime.Value.ToString("HH:mm:ss"));
        if (metaParts.Count > 0)
        {
            var metaTb = NeonWidgets.Text(string.Join(" · ", metaParts), 11.5, NeonPalette.InkMuted, wrap: true);
            metaTb.Margin = new Thickness(0, 6, 0, 0);
            info.Children.Add(metaTb);
        }

        Grid.SetColumn(info, 1);
        rowGrid.Children.Add(info);

        // Right action buttons
        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var evBtn = NeonWidgets.CreateStyledButton(this, "查看判断依据", "NeonSecondaryButton",
            () => OpenPatchEvidenceModal(p, s), "sm");
        rightBtns.Children.Add(evBtn);

        if (p.PendingModChange != null)
        {
            var ackBtn = NeonWidgets.CreateStyledButton(this, "标记已核对", "NeonCautionButton",
                () => RequestAcknowledgeModReview?.Invoke(p), "sm");
            ackBtn.Margin = new Thickness(7, 0, 0, 0);
            AutomationProperties.SetName(ackBtn, "标记已核对 " + p.Name);
            rightBtns.Children.Add(ackBtn);
        }
        else if (p.StatusLabel == "需重新检查")
        {
            var rescanBtn = NeonWidgets.CreateStyledButton(this, "重新核验", "NeonCautionButton",
                () => RequestRescanAndVerifyHealth?.Invoke(), "sm");
            rescanBtn.Margin = new Thickness(7, 0, 0, 0);
            rightBtns.Children.Add(rescanBtn);
        }

        Grid.SetColumn(rightBtns, 2);
        rowGrid.Children.Add(rightBtns);
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

    Border BuildPatchRetireRow(NeonRelatedPatch p, NeonWorkbenchSnapshot s, bool isLast)
    {
        var outerGrid = new Grid();
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outerGrid.Children.Add(new Border { Background = NeonPalette.Sage });

        var rowGrid = new Grid { Margin = new Thickness(14, 13, 16, 13) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var nameLine = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameTb = NeonWidgets.Text(p.Name, 14, NeonPalette.InkPrimary, FontWeights.SemiBold);
        nameTb.Margin = new Thickness(0, 0, 7, 2);
        nameLine.Children.Add(nameTb);
        var idTb = NeonWidgets.Text(p.PatchId, 11.5, NeonPalette.InkMuted, mono: true);
        idTb.Margin = new Thickness(0, 0, 8, 2);
        idTb.VerticalAlignment = VerticalAlignment.Center;
        nameLine.Children.Add(idTb);

        var strip = NeonWidgets.CreatePatchStatusAndTypeStrip("SHA-256 精确匹配上游修复", NeonTone.Sage, p.IsStandalone);
        strip.Margin = new Thickness(0, 0, 0, 2);
        nameLine.Children.Add(strip);
        info.Children.Add(nameLine);

        var desc = NeonWidgets.Text(p.VerdictText, 12.5, NeonPalette.InkSecondary, wrap: true);
        desc.Margin = new Thickness(0, 3, 0, 0);
        info.Children.Add(desc);
        rowGrid.Children.Add(info);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var evBtn = NeonWidgets.CreateStyledButton(this, "查看依据", "NeonSecondaryButton",
            () => OpenPatchEvidenceModal(p, s), "sm");
        evBtn.Margin = new Thickness(0, 0, 7, 0);
        var retireBtn = NeonWidgets.CreateStyledButton(this, "预览安全退役", "NeonPrimaryButton",
            () => RequestPreviewRetirePatch?.Invoke(p), "sm");
        AutomationProperties.SetName(retireBtn, "预览安全退役 " + p.PatchId);
        rightBtns.Children.Add(evBtn);
        rightBtns.Children.Add(retireBtn);
        Grid.SetColumn(rightBtns, 1);
        rowGrid.Children.Add(rightBtns);

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

    Border BuildPatchQuietRow(NeonRelatedPatch p, NeonWorkbenchSnapshot s, bool isLast)
    {
        var rowGrid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var nameLine = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameTb = NeonWidgets.Text(p.Name, 14, NeonPalette.InkPrimary, FontWeights.SemiBold);
        nameTb.Margin = new Thickness(0, 0, 7, 2);
        nameLine.Children.Add(nameTb);
        var idTb = NeonWidgets.Text($"{p.PatchId} · v{NeonVersionDiff.StripPrefix(p.Version)}", 11.5, NeonPalette.InkMuted, mono: true);
        idTb.Margin = new Thickness(0, 0, 8, 2);
        idTb.VerticalAlignment = VerticalAlignment.Center;
        nameLine.Children.Add(idTb);

        var strip = NeonWidgets.CreatePatchStatusAndTypeStrip(
            p.StatusLabel,
            p.StatusTone,
            p.IsStandalone,
            extraTag: p.IsLooseInModsRoot ? "散落 mods/ 根目录" : null,
            extraTone: NeonTone.Amber);
        strip.Margin = new Thickness(0, 0, 0, 2);
        nameLine.Children.Add(strip);
        info.Children.Add(nameLine);

        var desc = NeonWidgets.Text(p.Summary, 12.5, NeonPalette.InkSecondary, wrap: true);
        desc.Margin = new Thickness(0, 3, 0, 0);
        info.Children.Add(desc);

        var meta = NeonWidgets.Text("路径：" + (p.InstalledRelativeFolder ?? "未安装"), 11.5, NeonPalette.InkMuted, mono: p.InstalledRelativeFolder != null);
        meta.Margin = new Thickness(0, 5, 0, 0);
        info.Children.Add(meta);
        rowGrid.Children.Add(info);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rightBtns.Children.Add(NeonWidgets.CreateStyledButton(this, "查看依据", "NeonGhostButton",
            () => OpenPatchEvidenceModal(p, s), "sm"));

        if (p.IsStandalone && p.InstalledMod != null)
        {
            var mod = p.InstalledMod;
            var toggleBtn = NeonWidgets.CreateStyledButton(
                this,
                mod.Enabled ? "预览停用" : "预览恢复启用",
                mod.Enabled ? "NeonGhostButton" : "NeonSecondaryButton",
                () => RequestPreviewToggleMod?.Invoke(mod),
                "sm");
            toggleBtn.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetName(toggleBtn, (mod.Enabled ? "预览停用 " : "预览恢复启用 ") + p.PatchId);
            rightBtns.Children.Add(toggleBtn);
        }

        Grid.SetColumn(rightBtns, 1);
        rowGrid.Children.Add(rightBtns);

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
}
