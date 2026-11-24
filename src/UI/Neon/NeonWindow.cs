using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

public partial class MainWindow
{
    NeonWorkbenchView? neonWorkbenchView;
    LifecycleRegistry? neonOverrideLifecycleEvidence;
    string? neonFixtureRoot;
    string? neonGameRunningRoot;
    bool neonGameRunningCached;
    bool neonGameRunningPollInFlight;
    long neonLastRunningPollMs;
    internal int neonTestRestoreStageDelayMs;
    internal Func<string, ModInventory>? neonTestScanOverride;

    void ShowNeonWorkbench()
    {
        if (neonWorkbenchView == null)
        {
            neonWorkbenchView = new NeonWorkbenchView(ManagerUpdates.CurrentVersion);
            WireNeonWorkbenchEvents(neonWorkbenchView);
        }
        Background = NeonPalette.BgGround;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
        Title = "尖塔修补站 · SpirePatch · " + ManagerUpdates.CurrentReleaseVersion;
        Content = neonWorkbenchView;
        RefreshNeonWorkbench(preserveViewport: true);

        if (selectedInstallRoot != null && (modInventory == null || modInventoryDirty) && !busy && !transactionWriting)
            _ = RefreshNeonFullScanAndHealthAsync(includeLifecycle: false);
    }

    void WireNeonWorkbenchEvents(NeonWorkbenchView view)
    {
        view.RequestChooseGameRoot += ChooseNeonGameFolder;
        view.RequestDiscoverGameRoots += () =>
        {
            if (busy || transactionWriting) return;
            _ = DiscoverNeonGameRootsAsync();
        };
        view.RequestOpenGameFolder += () =>
        {
            if (selectedInstallRoot != null && Directory.Exists(selectedInstallRoot))
                OpenUrl(selectedInstallRoot);
        };
        view.RequestOpenModsFolder += () =>
        {
            if (selectedInstallRoot == null) return;
            string dir = Path.Combine(selectedInstallRoot, "mods");
            if (Directory.Exists(dir)) OpenUrl(dir);
            else view.ShowToast("此游戏目录尚未创建 mods 文件夹。", NeonTone.Amber);
        };
        view.RequestRescanAll += () => _ = RefreshNeonFullScanAndHealthAsync(includeLifecycle: false);
        view.RequestRescanAndVerifyHealth += () => _ = RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
        view.RequestLaunchGame += LaunchGame;
        view.RequestPreviewToggleMod += mod => _ = NeonPreviewToggleModAsync(mod);
        view.RequestDirectToggleMod += (mod, contextModId) => _ = NeonDirectToggleModAsync(mod, contextModId);
        view.RequestPreviewUpdateMod += mod => _ = NeonPreviewUpdateModAsync(mod);
        view.RequestCheckModUpdate += mod => _ = NeonCheckSingleModUpdateAsync(mod);
        view.RequestQuickBindGitHub += (mod, repo) =>
        {
            try
            {
                GitHubUpdates().Bind(mod.Id, repo);
                modSourceCheckWorkflow?.Invalidate(mod);
                RecordNeonActivity("source", $"将「{mod.Name}」关联至 GitHub 仓库：{repo}。");
                view.ShowToast($"已将 {mod.Name} 关联至 GitHub：{repo}");
                RefreshNeonWorkbench(preserveViewport: true);
            }
            catch (Exception ex) { view.ShowToast("绑定失败：" + ex.Message, NeonTone.Cinnabar); }
        };
        view.RequestQuickBindWorkshop += (mod, wsId) =>
        {
            try
            {
                WorkshopSources().Bind(mod.Id, wsId);
                modSourceCheckWorkflow?.Invalidate(mod);
                RecordNeonActivity("source", $"将「{mod.Name}」关联至 Steam 创意工坊 #{wsId}。");
                view.ShowToast($"已将 {mod.Name} 关联至 Workshop #{wsId}");
                RefreshNeonWorkbench(preserveViewport: true);
            }
            catch (Exception ex) { view.ShowToast("绑定失败：" + ex.Message, NeonTone.Cinnabar); }
        };
        view.RequestUnbindModSource += mod =>
        {
            try
            {
                if (GitHubUpdates().Repository(mod.Id) != null) GitHubUpdates().Unbind(mod.Id);
                if (WorkshopSources().Get(mod.Id).HasValue)
                {
                    WorkshopSources().Unbind(mod.Id);
                    WorkshopUpdates().Invalidate(mod.Id);
                }
                modSourceCheckWorkflow?.Invalidate(mod);
                RecordNeonActivity("source", $"解除「{mod.Name}」的更新来源关联（未改动 MOD 文件）。");
                view.ShowToast($"已解除 {mod.Name} 的更新来源关联（不改动 MOD 文件）");
                RefreshNeonWorkbench(preserveViewport: true);
            }
            catch (Exception ex) { view.ShowToast("解除关联失败：" + ex.Message, NeonTone.Cinnabar); }
        };
        view.RequestRestoreModBackup += RestoreModBackup;
        view.RequestOpenModFolder += mod =>
        {
            if (Directory.Exists(mod.Folder)) OpenUrl(mod.Folder);
        };
        view.RequestBatchToggleEnable += folders => _ = NeonPreviewBatchToggleAsync(folders, enable: true);
        view.RequestBatchToggleDisable += folders => _ = NeonPreviewBatchToggleAsync(folders, enable: false);
        view.RequestBatchCheckUpdates += folders => _ = NeonCheckBatchModUpdatesAsync(folders);
        view.RequestPreviewRetirePatch += patch => _ = NeonPreviewRetirePatchAsync(patch);
        view.RequestDiagnoseLog += ChooseLog;
        view.RequestSwitchTheme += ChangeUiTheme;
        view.RequestSimulateExternalChange += SimulateExternalChangeInFixture;
        WireNeonSecondaryPageEvents(view);
    }

    async Task NeonPreviewBatchToggleAsync(string[] folders, bool enable)
    {
        if (neonWorkbenchView == null) return;
        if (selectedInstallRoot == null || modInventory == null || modInventoryDirty || busy || transactionWriting)
        {
            neonWorkbenchView.ShowToast("MOD 清单尚未稳定或正在处理文件操作，请刷新后重试。", NeonTone.Amber);
            return;
        }
        var folderSet = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = modInventory.Mods
            .Where(m => folderSet.Contains(m.Folder) && !ModPresentation.IsIndependentPatch(m))
            .ToArray();
        if (selected.Length == 0)
        {
            neonWorkbenchView.ShowToast("请先勾选至少 1 个普通 MOD。", NeonTone.Amber);
            return;
        }

        string root = selectedInstallRoot;
        string verb = enable ? "启用" : "停用";
        bool running = GameEnvironment.Running(root);
        if (running)
        {
            neonWorkbenchView.ShowToast($"退出游戏后可批量{verb}", NeonTone.Amber);
            return;
        }

        busy = true;
        ModToggleBatchPreview? preview = null;
        string? error = null;
        try
        {
            preview = await ModToggleWorkflow.PrepareBatchAsync(root, modInventory, selected, enable);
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }

        if (error != null || preview == null || preview.Cancelled)
        {
            neonWorkbenchView.ShowToast($"批量{verb}预检未完成：{error ?? preview?.Status ?? "已取消"}", NeonTone.Amber);
            RefreshNeonWorkbench(preserveViewport: true);
            return;
        }
        if (!root.Equals(selectedInstallRoot, StringComparison.OrdinalIgnoreCase) || modInventoryDirty)
        {
            neonWorkbenchView.ShowToast("模组环境在预检期间发生变化，请刷新后重试。", NeonTone.Amber);
            RefreshNeonWorkbench(preserveViewport: true);
            return;
        }

        int readyCount = preview.Reviews.Count(r => r.State == "可处理");
        int deferredCount = preview.Reviews.Count(r => r.State == "需复核");
        int skippedCount = preview.Reviews.Count(r => r.State == "跳过");
        int actionableCount = preview.ExecutionOrder.Count;

        var groups = new List<NeonDiffGroup>();
        var execLines = preview.Reviews
            .Where(r => r.State != "跳过")
            .Select(r => new NeonDiffLine(
                r.State == "需复核" ? "mod" : "add",
                r.State,
                r.Name,
                r.Explanation))
            .ToArray();
        if (execLines.Length > 0)
            groups.Add(new NeonDiffGroup($"将按依赖顺序依次{verb}（{execLines.Length} 项）", execLines));

        var skipLines = preview.Reviews
            .Where(r => r.State == "跳过")
            .Select(r => new NeonDiffLine("skip", "跳过", r.Name, r.Explanation))
            .ToArray();
        if (skipLines.Length > 0)
            groups.Add(new NeonDiffGroup($"安全保护跳过（{skipLines.Length} 项）", skipLines));

        string plainSummary = actionableCount == 0
            ? $"所选 {selected.Length} 个 MOD 当前均无法直接{verb}（已全部触发安全保护跳过，详见下方明细）。"
            : enable
                ? $"将按依赖顺序恢复启用 {actionableCount} 个 MOD" +
                  (deferredCount > 0 ? $"（其中 {deferredCount} 项在前置启用后自动复核）" : "") +
                  (skippedCount > 0 ? $"，另外 {skippedCount} 项因保护规则跳过" : "") + "。不会修改游戏本体与存档。"
                : $"将按依赖顺序停用 {actionableCount} 个 MOD 并完整移入停用保留区（不删除文件）" +
                  (deferredCount > 0 ? $"，其中 {deferredCount} 项将在下游停用后自动复核" : "") +
                  (skippedCount > 0 ? $"；另外 {skippedCount} 项因仍被依赖或其他规则跳过" : "") + "。";

        var batchPreview = preview;
        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"批量{verb} · 共选 {selected.Length} 项",
            Subtitle: $"可处理 {readyCount} · 依赖顺序复核 {deferredCount} · 跳过 {skippedCount}",
            GameRunning: false,
            BlockedReason: actionableCount == 0 ? $"没有可安全执行批量{verb}的 MOD，请展开下方明细查看跳过原因。" : null,
            AutoSnapshotNote: actionableCount > 0 ? "每一项在真正移动前仍会独立核验文件指纹与依赖状态；停用项完整保留在停用区，随时可恢复。" : null,
            BoundaryNote: "预览未移动任何文件；批量执行中某一项失败不会回滚前面已成功移动的项，执行结束后会给出完整结果。",
            Token: $"BATCH-{(enable ? "ON" : "OFF")}-{actionableCount}",
            Groups: groups,
            ConfirmLabel: actionableCount > 0 ? $"确认批量{verb}（{actionableCount} 项）" : "",
            CancelLabel: actionableCount > 0 ? "取消" : "关闭",
            OnConfirmAsync: actionableCount > 0 ? () => NeonConfirmBatchToggleAsync(batchPreview) : null,
            PlainSummary: plainSummary,
            ExpandTechnicalDetailsByDefault: actionableCount == 0));
    }

    async Task NeonConfirmBatchToggleAsync(ModToggleBatchPreview preview)
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        string root = preview.Root;
        string verb = preview.Enable ? "启用" : "停用";
        bool writing = false;
        busy = true;
        neonWorkbenchView.ShowToast($"正在按依赖顺序批量{verb}（共 {preview.ExecutionOrder.Count} 项）…");
        try
        {
            var result = await ModToggleWorkflow.ConfirmAndApplyBatchAsync(
                preview,
                (_, _) => Task.FromResult(true),
                update =>
                {
                    if (update.TransactionWriting && !writing)
                    {
                        writing = true;
                        transactionWriting = true;
                    }
                    else if (!update.TransactionWriting && writing)
                    {
                        writing = false;
                        transactionWriting = false;
                        modFileWatcher.DeferQuietPeriod();
                    }
                });

            if (result.Confirmed)
            {
                InvalidateLifecycle($"批量{verb}已变更 MOD 启停状态，旧的补丁健康结论已自动失效，请重新核验。");
                ClearSelection();
                ClearCoop();
                report = null;
                states = [];
                installPreview = null;
                lastInstallResult = null;
                modInventoryDirty = true;
                try
                {
                    modInventory = await Task.Run(() => ModManager.Scan(root));
                    modInventoryDirty = false;
                    modInventoryCleanVersion = Math.Max(modInventoryCleanVersion, Volatile.Read(ref modChangeVersion));
                }
                catch (Exception scanEx)
                {
                    modInventoryDirty = true;
                    neonWorkbenchView.ShowToast($"批量{verb}已执行，但后续扫描受阻（{scanEx.Message}），请点击重新扫描。", NeonTone.Amber);
                }
            }

            modStatus = result.Summary;
            RecordNeonActivity("toggle", $"批量{verb}完成：成功 {result.SuccessCount} 项，失败 {result.FailedCount} 项，跳过 {result.SkippedCount} 项。");
            neonWorkbenchView.ClearCheckedMods();

            if (result.FailedCount > 0)
            {
                var lines = result.Items.Select(i => new NeonDiffLine(
                    i.State == "成功" ? "add" : i.State == "失败" ? "del" : "skip",
                    i.State,
                    i.Name,
                    i.Explanation)).ToArray();
                neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                    Title: $"批量{verb}完成（部分项未完成）",
                    Subtitle: $"成功 {result.SuccessCount} · 失败 {result.FailedCount} · 跳过 {result.SkippedCount}",
                    GameRunning: false,
                    BlockedReason: null,
                    AutoSnapshotNote: null,
                    BoundaryNote: "已成功的项保持生效；失败项未强行写入，可按下方原因逐项排查。",
                    Token: "BATCH-RESULT",
                    Groups: [new NeonDiffGroup("逐项执行结果", lines)],
                    ConfirmLabel: "",
                    CancelLabel: "关闭",
                    PlainSummary: result.Summary,
                    ExpandTechnicalDetailsByDefault: true));
            }
            else
            {
                neonWorkbenchView.ShowToast($"批量{verb}完成：成功 {result.SuccessCount} 项" + (result.SkippedCount > 0 ? $"，跳过 {result.SkippedCount} 项" : ""));
            }
        }
        catch (Exception ex)
        {
            modInventoryDirty = true;
            neonWorkbenchView.ShowToast($"批量{verb}中断：{ex.Message}", NeonTone.Cinnabar);
        }
        finally
        {
            if (writing)
            {
                writing = false;
                transactionWriting = false;
                modFileWatcher.DeferQuietPeriod();
            }
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonPreviewUpdateModAsync(ManagedMod mod)
    {
        if (selectedInstallRoot == null || neonWorkbenchView == null) return;
        if (busy || transactionWriting || modInventoryDirty)
        {
            neonWorkbenchView.ShowToast("MOD 清单尚未稳定或正在处理文件操作，请刷新后重试。", NeonTone.Amber);
            return;
        }
        string root = selectedInstallRoot;
        if (GameEnvironment.Running(root))
        {
            neonWorkbenchView.ShowToast("退出游戏后可下载并更新 MOD", NeonTone.Amber);
            return;
        }

        string? repo = null;
        ulong? wsId = null;
        try { repo = GitHubUpdates().Repository(mod.Id); } catch { }
        try { wsId = WorkshopSources().Get(mod.Id); } catch { }

        if (repo != null && GitHubUpdates().Cached(mod.Id)?.Status == "有更新")
        {
            busy = true;
            GitHubModPackage[] packages = [];
            string? pkgErr = null;
            neonWorkbenchView.ShowToast($"正在读取 {mod.Name} 的 GitHub Release 安装包列表…");
            try
            {
                using var installerSource = new GitHubModInstaller();
                packages = await installerSource.PackagesAsync(mod, repo);
            }
            catch (Exception ex) { pkgErr = ex.Message; }
            finally { busy = false; }

            if (pkgErr != null)
            {
                neonWorkbenchView.ShowToast("读取 GitHub 更新包失败：" + pkgErr, NeonTone.Cinnabar);
                return;
            }
            if (packages.Length == 0)
            {
                neonWorkbenchView.ShowToast("该 Release 未提供可校验的单 MOD ZIP 安装包，请从作者发布页手动更新。", NeonTone.Amber);
                return;
            }
            if (packages.Length == 1)
            {
                var p = packages[0];
                await NeonStageAndPreviewModUpdateAsync(
                    root, mod,
                    new ModUpdateStagingRequest(
                        ModUpdateStagingSource.GitHub, p.Repository, p.Tag, p.FileName,
                        p.Size, p.Sha256, p.DownloadUrl, p.ReleaseUrl),
                    packages[0].ReleaseUrl);
                return;
            }

            // Multiple packages in Release -> let player pick explicitly in a native Neon modal
            var pkgPanel = new StackPanel();
            foreach (var candidate in packages)
            {
                var chosenPkg = candidate;
                var cardGrid = new Grid();
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                meta.Children.Add(NeonWidgets.Text(chosenPkg.FileName, 12.5, NeonPalette.InkPrimary, FontWeights.SemiBold, mono: true));
                meta.Children.Add(NeonWidgets.Text($"Tag {chosenPkg.Tag} · {ModEnvironmentSnapshots.FormatBytes(chosenPkg.Size)} · SHA-256 已签名", 11.5, NeonPalette.InkMuted));
                cardGrid.Children.Add(meta);

                var stageBtn = NeonWidgets.CreateStyledButton(neonWorkbenchView, "下载并预览差异", "NeonSecondaryButton", () =>
                {
                    neonWorkbenchView.CloseTransactionModal();
                    _ = NeonStageAndPreviewModUpdateAsync(
                        root, mod,
                        new ModUpdateStagingRequest(
                            ModUpdateStagingSource.GitHub, chosenPkg.Repository, chosenPkg.Tag, chosenPkg.FileName,
                            chosenPkg.Size, chosenPkg.Sha256, chosenPkg.DownloadUrl, chosenPkg.ReleaseUrl),
                        chosenPkg.ReleaseUrl);
                }, "sm");
                stageBtn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(stageBtn, 1);
                cardGrid.Children.Add(stageBtn);

                pkgPanel.Children.Add(new Border
                {
                    Background = NeonPalette.BgElevated,
                    BorderBrush = NeonPalette.BorderSubtle,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(11, 9, 11, 9),
                    Margin = new Thickness(0, 0, 0, 7),
                    Child = cardGrid
                });
            }

            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"选择 GitHub 安装包 · {mod.Name}",
                Subtitle: $"本地 v{NeonVersionDiff.StripPrefix(mod.Version)} → GitHub {packages[0].Tag} · {repo}",
                GameRunning: false,
                BlockedReason: null,
                AutoSnapshotNote: null,
                BoundaryNote: "部分仓库会区分兼容版或标准版；请根据你当前的游戏版本选择对应安装包。",
                Token: "GH-PKG-PICK",
                Groups: [],
                ConfirmLabel: "查看作者发布页",
                CancelLabel: "取消",
                OnConfirmAsync: () => { OpenUrl(packages[0].ReleaseUrl); return Task.CompletedTask; },
                CustomContent: pkgPanel,
                PlainSummary: $"该 Release 提供 {packages.Length} 个已附带 SHA-256 校验的安装包，请选择与你游戏版本匹配的一个下载并预览文件差异。"));
            return;
        }

        if (wsId.HasValue && WorkshopUpdates().Cached(mod.Id)?.Status == "有更新")
        {
            await NeonStageAndPreviewModUpdateAsync(
                root, mod,
                new ModUpdateStagingRequest(ModUpdateStagingSource.Workshop, wsId.Value.ToString()),
                null);
            return;
        }

        neonWorkbenchView.ShowToast("当前没有可预览的更新来源。", NeonTone.Amber);
    }

    async Task NeonStageAndPreviewModUpdateAsync(
        string root,
        ManagedMod mod,
        ModUpdateStagingRequest request,
        string? releaseUrl)
    {
        if (busy || transactionWriting || neonWorkbenchView == null) return;
        busy = true;
        using var backend = new InfrastructureModUpdateStagingBackend();
        var stagingSvc = new ModUpdateStagingWorkflowService(
            backend,
            Path.Combine(NeonDataRoot, "MOD更新暂存"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        preparationCancellation = cts;

        ModUpdateStagingResult? staged = null;
        string? stageError = null;
        try
        {
            staged = await stagingSvc.StageAndPreviewAsync(
                root, mod, request, cts.Token,
                p => Dispatcher.BeginInvoke(() => neonWorkbenchView?.ShowToast(p.Message)));
        }
        catch (Exception ex) { stageError = ex.Message; }
        finally
        {
            if (ReferenceEquals(preparationCancellation, cts))
                preparationCancellation = null;
            busy = false;
        }

        if (stageError != null || staged == null || !staged.ValidationPassed)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"无法预览 MOD 更新 · {mod.Name}",
                Subtitle: request.Source == ModUpdateStagingSource.GitHub ? $"GitHub {request.SourceIdentifier}" : $"Workshop #{request.SourceIdentifier}",
                GameRunning: GameEnvironment.Running(root),
                BlockedReason: stageError ?? staged?.FailureReason ?? "暂存下载或完整性校验未通过。",
                AutoSnapshotNote: null,
                BoundaryNote: "未对当前 mods 目录做任何修改。",
                Token: "UPD-STAGE-FAIL",
                Groups: [],
                ConfirmLabel: releaseUrl != null ? "查看作者发布页" : "",
                CancelLabel: "关闭",
                OnConfirmAsync: releaseUrl != null ? () => { OpenUrl(releaseUrl); return Task.CompletedTask; } : null));
            return;
        }

        var applySvc = NeonModUpdateApplyService();
        var applyPreview = applySvc.CreatePreview(root, mod, staged);
        var changeLines = staged.FileChanges.Take(25).Select(fc =>
        {
            string tag = fc.Kind == ModUpdateFileChangeKind.Added ? "add"
                : fc.Kind == ModUpdateFileChangeKind.Deleted ? "del" : "mod";
            string verb = fc.Kind == ModUpdateFileChangeKind.Added ? "新增"
                : fc.Kind == ModUpdateFileChangeKind.Deleted ? "移除" : "更新";
            return new NeonDiffLine(tag, verb, fc.RelativePath,
                fc.StagedSizeBytes.HasValue ? ModEnvironmentSnapshots.FormatBytes(fc.StagedSizeBytes.Value) : "");
        }).ToList();
        if (staged.FileChanges.Count > 25)
            changeLines.Add(new NeonDiffLine("skip", "更多", $"其余 {staged.FileChanges.Count - 25} 个文件变动已通过校验", ""));

        string stagingDir = staged.StagingDirectory ?? "";
        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"确认更新 MOD · {mod.Name}",
            Subtitle: $"v{NeonVersionDiff.StripPrefix(staged.LocalVersion)} → v{NeonVersionDiff.StripPrefix(staged.StagedVersion ?? "新版")}",
            GameRunning: GameEnvironment.Running(root),
            BlockedReason: null,
            AutoSnapshotNote: "确认前将自动创建完整环境快照与事务备份；若替换途中意外中断，下次启动将自动回滚或安全恢复。",
            BoundaryNote: "下载与校验均已在独立隔离暂存区完成，确认前未接触你的 mods 目录。",
            Token: $"UPD-{mod.Id}",
            Groups: [new NeonDiffGroup($"文件级差异预览（共 {staged.FileChanges.Count} 处变更）", changeLines)],
            ConfirmLabel: "确认备份并更新",
            CancelLabel: "取消",
            OnConfirmAsync: () => NeonConfirmStagedModUpdateAsync(root, mod, applySvc, applyPreview, request),
            PlainSummary: $"已下载并通过完整哈希与清单校验，将把「{mod.Name}」从 v{NeonVersionDiff.StripPrefix(staged.LocalVersion)} 更新至 v{NeonVersionDiff.StripPrefix(staged.StagedVersion ?? "新版")}（新增 {staged.AddedCount}、修改 {staged.ModifiedCount}、移除 {staged.DeletedCount} 个文件）。",
            OnCancel: () =>
            {
                if (!string.IsNullOrWhiteSpace(stagingDir))
                {
                    try { stagingSvc.Discard(stagingDir); } catch { }
                }
            }));
    }

    async Task NeonConfirmStagedModUpdateAsync(
        string root,
        ManagedMod mod,
        ModUpdateApplyWorkflowService applySvc,
        ModUpdateApplyPreview applyPreview,
        ModUpdateStagingRequest request)
    {
        if (busy || transactionWriting || neonWorkbenchView == null) return;
        busy = true;
        transactionWriting = true;
        try
        {
            var safety = await NeonSnapshotWorkflow(root).CreateAsync("更新前自动保护");
            var applyResult = await applySvc.ApplyAsync(applySvc.Confirm(applyPreview, confirmed: true));
            modFileWatcher.DeferQuietPeriod();

            if (!applyResult.Success)
            {
                neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                    Title: $"MOD 更新未完成 · {mod.Name}",
                    Subtitle: $"恢复状态：{applyResult.RecoveryState}",
                    GameRunning: false,
                    BlockedReason: applyResult.FailureReason ?? "更新事务未完成。",
                    AutoSnapshotNote: null,
                    BoundaryNote: $"更新前完整保护快照：{safety.Id}",
                    Token: "UPD-APPLY-FAIL",
                    Groups: [],
                    ConfirmLabel: "",
                    CancelLabel: "关闭",
                    PlainSummary: applyResult.RolledBack
                        ? $"替换未成功，已自动从事务备份完整还原原版「{mod.Name}」（{applyResult.FailureReason}）。"
                        : applyResult.FailureReason ?? "更新未完成。"));
                return;
            }

            if (request.Source == ModUpdateStagingSource.Workshop &&
                ulong.TryParse(request.SourceIdentifier, out ulong wsId) &&
                !string.IsNullOrWhiteSpace(applyPreview.Staging.SourceManifestId))
            {
                try
                {
                    var sources = WorkshopSources();
                    sources.Bind(mod.Id, wsId);
                    sources.RecordBaseline(
                        mod.Id,
                        wsId,
                        applyPreview.Staging.SourceManifestId!,
                        applyResult.AfterVersion ?? mod.Version,
                        applyPreview.Staging.StagedFileHashes);
                    WorkshopUpdates().Invalidate(mod.Id);
                }
                catch { }
            }
            modSourceCheckWorkflow?.InvalidateAll();

            InvalidateLifecycle($"已更新「{mod.Name}」，旧的补丁健康结论已失效，请重新核验。");
            report = null; states = []; ClearCoop(); ClearSelection(); installPreview = null; lastInstallResult = null;
            modInventory = await Task.Run(() => ModManager.Scan(root));
            modInventoryDirty = false;
            modInventoryCleanVersion = Math.Max(modInventoryCleanVersion, Volatile.Read(ref modChangeVersion));

            string newVer = applyResult.AfterVersion ?? applyPreview.Staging.StagedVersion ?? "新版";
            RecordNeonActivity("install", $"将「{mod.Name}」更新至 v{NeonVersionDiff.StripPrefix(newVer)}（已创建保护快照 {safety.Id[..8]}）。");
            neonWorkbenchView.ShowToast($"MOD 更新完成：{mod.Name} → v{NeonVersionDiff.StripPrefix(newVer)}");
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("MOD 更新未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonCheckSingleModUpdateAsync(ManagedMod mod)
    {
        if (selectedInstallRoot == null || modInventory == null || neonWorkbenchView == null) return;
        if (busy || modInventoryDirty)
        {
            neonWorkbenchView.ShowToast("MOD 清单尚未稳定，请刷新后再检查。", NeonTone.Amber);
            return;
        }
        string gameRoot = selectedInstallRoot;
        var inventory = modInventory;
        var sourceChecks = ModSourceChecks();
        var current = sourceChecks.Inspect(mod);
        if (!current.GitHub.IsBound && !current.Workshop.IsBound)
        {
            neonWorkbenchView.ShowToast("该 MOD 尚未关联更新来源。", NeonTone.Amber);
            return;
        }

        busy = true;
        try
        {
            if (current.GitHub.IsBound)
            {
                var res = await sourceChecks.CheckGitHubAsync(
                    mod, force: true, isCurrent: () => IsCurrentModSourceScope(gameRoot, inventory, mod));
                if (!res.IsStale && !res.Cancelled)
                {
                    neonWorkbenchView.ShowToast(
                        $"{mod.Name} · GitHub：{res.GitHub.Status}" + (string.IsNullOrWhiteSpace(res.GitHub.Message) ? "" : $" · {res.GitHub.Message}"),
                        res.GitHub.State == ModSourceCheckState.Failed ? NeonTone.Amber : NeonTone.Sage);
                }
            }
            if (current.Workshop.IsBound)
            {
                var res = await sourceChecks.CheckWorkshopAsync(
                    mod, force: true, isCurrent: () => IsCurrentModSourceScope(gameRoot, inventory, mod));
                if (!res.IsStale && !res.Cancelled)
                {
                    neonWorkbenchView.ShowToast(
                        $"{mod.Name} · 创意工坊：{res.Workshop.Status}" + (string.IsNullOrWhiteSpace(res.Workshop.Message) ? "" : $" · {res.Workshop.Message}"),
                        res.Workshop.State == ModSourceCheckState.Failed ? NeonTone.Amber : NeonTone.Sage);
                }
            }
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("检查更新失败：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonCheckBatchModUpdatesAsync(string[] folders)
    {
        if (selectedInstallRoot == null || modInventory == null || neonWorkbenchView == null) return;
        if (modInventoryDirty || busy || transactionWriting || modUpdateCheckRunning)
        {
            neonWorkbenchView.ShowToast("清单尚未稳定或正在执行更新检查，请稍候。", NeonTone.Amber);
            return;
        }
        var folderSet = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = modInventory.Mods
            .Where(m => folderSet.Contains(m.Folder) && !ModPresentation.IsIndependentPatch(m))
            .ToArray();
        if (selected.Length == 0) return;

        var inventory = modInventory;
        string gameRoot = selectedInstallRoot;
        var sourceChecks = ModSourceChecks();
        using var cancellation = new CancellationTokenSource();
        modUpdateCheckCancellation = cancellation;
        modUpdateCheckRunning = true;
        neonLastUpdateCheckTimeLabel = DateTime.Now.ToString("HH:mm:ss");
        neonWorkbenchView.ShowToast($"正在后台只读检查所选 {selected.Length} 项 MOD 的来源更新…");
        RefreshNeonWorkbench(preserveViewport: true);

        try
        {
            var res = await sourceChecks.CheckAllAsync(
                selected, force: true, isCurrent: () => IsCurrentModSourceInventory(gameRoot, inventory), cancellationToken: cancellation.Token);
            if (!res.Cancelled && !res.IsStale)
            {
                string summary = $"所选检查完成：GitHub {res.GitHubChecked} 项（有更新 {res.GitHubUpdates}），创意工坊 {res.WorkshopChecked} 项（有更新 {res.WorkshopUpdates}）。";
                RecordNeonActivity("update", summary);
                neonWorkbenchView.ShowToast(summary);
            }
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("批量检查更新未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            if (ReferenceEquals(modUpdateCheckCancellation, cancellation))
                modUpdateCheckCancellation = null;
            modUpdateCheckRunning = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    internal void RefreshNeonWorkbench(bool preserveViewport = true)
    {
        if (!UiThemes.IsNeon || neonWorkbenchView == null) return;

        bool running = selectedInstallRoot != null &&
            string.Equals(neonGameRunningRoot, selectedInstallRoot, StringComparison.OrdinalIgnoreCase) &&
            neonGameRunningCached;
        if (selectedInstallRoot != null)
            _ = PollNeonGameRunningAsync(selectedInstallRoot, force: !string.Equals(neonGameRunningRoot, selectedInstallRoot, StringComparison.OrdinalIgnoreCase));
        ModDependencyGraphReport? depGraph = null;
        if (modInventory != null)
        {
            var diag = ModDependencyDiagnostics.Analyze(modInventory);
            depGraph = new ModDependencyGraphReport(
                diag.Edges.Select(e => new ModDependencyEdge(
                    e.Dependent, e.DependencyId, e.DependencyName, e.Prerequisite,
                    e.Status, e.Message, e.FromPatchRule, e.IsCycle,
                    e.DisabledVersionFits, e.MinimumVersion, e.MaximumVersion)).ToArray(),
                diag.Cycles.Select(c => new ModDependencyCycle(c.ModIds, c.Names)).ToArray(),
                diag.Summaries.ToDictionary(
                    kv => kv.Key,
                    kv => new ModDependencySummary(kv.Value.DependencyCount, kv.Value.UnsatisfiedCount, kv.Value.ActiveIssueCount, kv.Value.CycleEdgeCount),
                    StringComparer.OrdinalIgnoreCase),
                diag.ActiveIssueCount);
        }
        GitHubModUpdates? gh = null;
        WorkshopModSources? ws = null;
        WorkshopModUpdates? wsUpd = null;
        if (selectedInstallRoot != null)
        {
            try { gh = GitHubUpdates(); } catch { }
            try { ws = WorkshopSources(); } catch { }
            try { wsUpd = WorkshopUpdates(); } catch { }
        }

        var snapshot = NeonWorkbenchBuilder.Build(
            gameRoot: selectedInstallRoot,
            gameRunning: running,
            inventory: modInventory,
            dependencyGraph: depGraph,
            patchScanReport: report,
            lifecycleReport: HasValidLifecycleReport ? lifecycleReport : null,
            modInventoryDirty: modInventoryDirty,
            lifecycleStale: lifecycleStale,
            lifecycleScanning: lifecycleScanning,
            lifecycleStatus: lifecycleStatus,
            githubUpdates: gh,
            workshopSources: ws,
            workshopUpdates: wsUpd,
            githubDiscovery: githubDiscovery,
            orphanAutoStopEnabled: OrphanPatchAutoStop.Enabled(NeonDataRoot),
            onlineRefreshing: onlineBusy,
            onlineSourceStatus: onlineStatus,
            onlineCatalogCount: onlineCatalog?.Patches.Count ?? Catalog.Bundled.Length,
            checkingModUpdates: modUpdateCheckRunning,
            lastUpdateCheckTimeLabel: neonLastUpdateCheckTimeLabel,
            coopState: BuildNeonCoopSnapshotState(),
            recoveryState: BuildNeonRecoverySnapshotState());

        neonWorkbenchView.ApplySnapshot(snapshot, preserveViewport);
    }

    /// <summary>
    /// Called immediately on the UI thread when InvalidateLifecycle or QueueModChange fires,
    /// withdrawing stale "校验通过" markers on the spot while preserving search, focus, and scroll!
    /// </summary>
    void NotifyNeonHealthInvalidated()
    {
        if (!UiThemes.IsNeon || neonWorkbenchView == null) return;
        RefreshNeonWorkbench(preserveViewport: true);
    }

    void UpdateNeonGlobalControls()
    {
        if (!UiThemes.IsNeon || neonWorkbenchView == null || selectedInstallRoot == null) return;
        _ = PollNeonGameRunningAsync(selectedInstallRoot, force: false);
    }

    async Task PollNeonGameRunningAsync(string root, bool force)
    {
        if (neonGameRunningPollInFlight) return;
        long now = Environment.TickCount64;
        if (!force &&
            string.Equals(neonGameRunningRoot, root, StringComparison.OrdinalIgnoreCase) &&
            now - neonLastRunningPollMs < 1200)
            return;

        neonGameRunningPollInFlight = true;
        try
        {
            bool running = await Task.Run(() => GameEnvironment.Running(root));
            neonGameRunningRoot = root;
            neonGameRunningCached = running;
            neonLastRunningPollMs = Environment.TickCount64;
            if (UiThemes.IsNeon && neonWorkbenchView != null &&
                string.Equals(selectedInstallRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                neonWorkbenchView.UpdateRunningStateOnly(running);
            }
        }
        catch { }
        finally
        {
            neonGameRunningPollInFlight = false;
        }
    }

    async void ChooseNeonGameFolder()
    {
        if (busy || transactionWriting) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择包含 SlayTheSpire2.exe 的游戏根目录"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            UseGameRoot(dialog.FolderName);
            await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("读取目录失败：" + ex.Message, NeonTone.Cinnabar);
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task DiscoverNeonGameRootsAsync()
    {
        if (busy || transactionWriting) return;
        busy = true;
        try
        {
            string? saved = EnvironmentSession.LoadRememberedRoot();
            var found = await EnvironmentSession.DiscoverAsync(saved);
            gameCandidates = found;
            if (found.Count == 0)
            {
                neonWorkbenchView?.ShowToast("未自动找到《杀戮尖塔 2》目录，请手动选择。", NeonTone.Amber);
                return;
            }
            if (found.Count == 1)
            {
                UseGameRoot(found[0].Directory);
                neonWorkbenchView?.ShowToast("已定位游戏目录：" + found[0].Directory);
                busy = false;
                await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
                return;
            }
            ShowNeonGameRootPickerModal(found);
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("自动查找未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally { busy = false; }
    }

    void ShowNeonGameRootPickerModal(IReadOnlyList<GameCandidate> found)
    {
        if (neonWorkbenchView == null || found.Count == 0) return;
        var listPanel = new StackPanel();
        foreach (var cand in found)
        {
            var candidate = cand;
            var cardGrid = new Grid();
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            info.Children.Add(NeonWidgets.Text(candidate.Directory, 12, NeonPalette.InkPrimary, FontWeights.SemiBold, mono: true, wrap: true));
            info.Children.Add(NeonWidgets.Text(candidate.Source, 11.5, NeonPalette.InkMuted));
            cardGrid.Children.Add(info);

            var pickBtn = NeonWidgets.CreateStyledButton(neonWorkbenchView, "使用此目录", "NeonSecondaryButton", () =>
            {
                neonWorkbenchView.CloseTransactionModal();
                try
                {
                    UseGameRoot(candidate.Directory);
                    neonWorkbenchView.ShowToast("已切换游戏目录：" + candidate.Directory);
                    _ = RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
                }
                catch (Exception ex)
                {
                    neonWorkbenchView.ShowToast("切换目录失败：" + ex.Message, NeonTone.Cinnabar);
                }
            }, "sm");
            pickBtn.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(pickBtn, 1);
            cardGrid.Children.Add(pickBtn);

            listPanel.Children.Add(new Border
            {
                Background = NeonPalette.BgElevated,
                BorderBrush = NeonPalette.BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Child = cardGrid
            });
        }

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"发现 {found.Count} 个游戏安装目录",
            Subtitle: "点击下方任一目录立即切换，不会修改游戏文件",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: "切换游戏目录会清除旧目录的扫描和健康结论缓存，不会修改游戏文件。",
            Token: "DISCOVER",
            Groups: [],
            ConfirmLabel: "",
            CancelLabel: "关闭",
            CustomContent: listPanel,
            PlainSummary: $"检测到 {found.Count} 个《杀戮尖塔 2》安装目录，请任选本次要管理的一个。"));
    }

    internal async Task RefreshNeonFullScanAndHealthAsync(bool includeLifecycle)
    {
        if (busy || transactionWriting || selectedInstallRoot == null) return;
        string root = selectedInstallRoot;
        int watchVersion = Volatile.Read(ref modChangeVersion);
        int epoch = lifecycleEpoch;
        busy = true;
        if (includeLifecycle) lifecycleScanning = true;
        RefreshNeonWorkbench(preserveViewport: true);
        try
        {
            var definitions = sourceCurrent && onlineCatalog != null ? onlineCatalog.Patches : Catalog.Bundled;
            string revision = sourceCurrent && onlineCatalog != null ? onlineCatalog.Commit : Catalog.Revision;

            if (!transactionWriting)
            {
                try
                {
                    var zipSession = NeonLocalZipSession();
                    var zipRecovery = await Task.Run(() => zipSession.RecoverPendingTransactions(root, () => modFileWatcher.DeferQuietPeriod()));
                    if (zipRecovery.HasPendingRecovery)
                    {
                        string msg = LocalZipUiResultPresenter.FormatRecoveryReport(zipRecovery, zipSession.TransactionLogDirectory)
                            ?? "发现待人工核对的本地 ZIP 安装事务。";
                        neonWorkbenchView?.ShowToast(msg, NeonTone.Amber);
                    }
                    var updRecovery = await Task.Run(() => NeonModUpdateApplyService().RecoverPendingTransactions(root));
                    if (updRecovery.HasPendingRecovery)
                        neonWorkbenchView?.ShowToast($"发现 {updRecovery.Items.Count(i => i.Pending)} 项待核对的 MOD 更新事务。", NeonTone.Amber);
                    else if (updRecovery.RecoveredCount > 0)
                        neonWorkbenchView?.ShowToast($"已自动恢复 {updRecovery.RecoveredCount} 项中断的 MOD 更新事务。", NeonTone.Amber);
                }
                catch { }
            }

            var scanned = await Task.Run(() =>
            {
                var inv = ModManager.Scan(root);
                var patchRep = Catalog.Scan(root, definitions, revision);
                ModHistoryResult? hist = null;
                if (includeLifecycle && inv.Issues.Count == 0 && patchRep.InventoryComplete)
                    hist = ModHistory.CompareAndStore(root, NeonDataRoot);
                return (inv, patchRep, hist);
            });

            if (!root.Equals(selectedInstallRoot, StringComparison.OrdinalIgnoreCase) || watchVersion != Volatile.Read(ref modChangeVersion))
            {
                modInventoryDirty = true;
                InvalidateLifecycle("扫描期间目录发生变化，请重新扫描核验。");
                return;
            }

            modInventory = scanned.inv;
            modInventoryDirty = false;
            modInventoryCleanVersion = Math.Max(modInventoryCleanVersion, Volatile.Read(ref modChangeVersion));
            neonWorkbenchView?.ClearAllPendingScanStates();
            report = scanned.patchRep;
            scannedCatalog = definitions;
            states = scanned.patchRep.Patches;
            demo = false;

            if (includeLifecycle && scanned.hist != null)
            {
                LifecycleRegistry evidence;
                string evidenceStatus;
                if (neonOverrideLifecycleEvidence != null)
                {
                    evidence = neonOverrideLifecycleEvidence;
                    evidenceStatus = $"已加载 {evidence.Entries.Length} 条已审核上游修复证据记录。";
                }
                else
                {
                    try
                    {
                        evidence = await LifecycleEvidenceSource.FetchAsync();
                        evidenceStatus = $"已检查补丁库：{evidence.Entries.Length} 条有证据的上游记录。";
                    }
                    catch (Exception ex)
                    {
                        evidence = LifecycleEvidenceSource.Empty;
                        evidenceStatus = "在线退役证据暂不可用，仅显示本地指纹状态，禁止自动退役：" + ex.Message;
                    }
                }

                var evaluated = PatchLifecycle.Evaluate(root, scanned.hist, evidence, evidenceStatus, scanned.patchRep, scanned.inv);
                if (epoch != lifecycleEpoch || !root.Equals(selectedInstallRoot, StringComparison.OrdinalIgnoreCase) || watchVersion != Volatile.Read(ref modChangeVersion))
                {
                    InvalidateLifecycle("核验期间环境已变化，请重新扫描。");
                    return;
                }
                lifecycleEvidence = evidence;
                lifecycleReport = evaluated;
                lifecycleStale = false;
                lifecycleStatus = scanned.hist.FirstScan
                    ? "已完成本地指纹与健康基线核验；适配结论仅覆盖已收录条件。"
                    : $"核验完成：发现变化 {scanned.hist.Changes.Length} 项 · 待人工核对 {scanned.hist.PendingReview.Length} 项。";
                RecordNeonActivity("scan", $"完成补丁健康核验（{scanned.inv.Mods.Count(m => !ModPresentation.IsIndependentPatch(m))} 项普通 MOD · 变化 {scanned.hist.Changes.Length} 项）。");
                neonWorkbenchView?.ShowToast("重新扫描与补丁健康核验已完成");
            }
            else
            {
                RecordNeonActivity("scan", $"完成本地只读扫描（{scanned.inv.Mods.Count(m => !ModPresentation.IsIndependentPatch(m))} 项普通 MOD）。");
                neonWorkbenchView?.ShowToast("只读扫描完成（未修改任何游戏文件）");
            }
        }
        catch (Exception ex)
        {
            modInventoryDirty = true;
            InvalidateLifecycle("扫描未完成：" + ex.Message);
            neonWorkbenchView?.ShowToast("扫描未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            lifecycleScanning = false;
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonPreviewToggleModAsync(ManagedMod snapshotMod)
    {
        if (selectedInstallRoot == null || modInventory == null || neonWorkbenchView == null) return;
        if (busy || transactionWriting) return;

        string root = selectedInstallRoot;
        bool running = GameEnvironment.Running(root);
        var current = modInventory.Mods.FirstOrDefault(m =>
            m.Folder.Equals(snapshotMod.Folder, StringComparison.OrdinalIgnoreCase) &&
            m.Manifest.Equals(snapshotMod.Manifest, StringComparison.OrdinalIgnoreCase));

        if (current == null || modInventoryDirty)
        {
            neonWorkbenchView.ShowToast("MOD 清单已变化，请先刷新清单后再预览操作。", NeonTone.Amber);
            return;
        }

        bool enable = !current.Enabled;
        string? blockReason = running
            ? "检测到游戏正在运行（SlayTheSpire2.exe），请先完全退出游戏后再移动 MOD 文件。"
            : ModManager.BlockReason(root, current, modInventory);

        if (blockReason != null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: enable ? $"无法预览启用 · {current.Name}" : $"无法预览停用 · {current.Name}",
                Subtitle: $"{current.Id} · v{NeonVersionDiff.StripPrefix(current.Version)}",
                GameRunning: running,
                BlockedReason: blockReason,
                AutoSnapshotNote: null,
                BoundaryNote: "这是来自 ModManager 的真实安全保护规则，未对任何文件进行修改。",
                Token: "BLOCKED",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "知道了"));
            return;
        }

        busy = true;
        ModTogglePlan? plan = null;
        string? previewError = null;
        try
        {
            plan = await ModToggleWorkflow.PreviewAsync(root, current.Folder, enable);
        }
        catch (Exception ex) { previewError = ex.Message; }
        finally { busy = false; }

        if (previewError != null || plan == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: enable ? $"预检未通过 · {current.Name}" : $"预检未通过 · {current.Name}",
                Subtitle: $"{current.Id} · v{NeonVersionDiff.StripPrefix(current.Version)}",
                GameRunning: running,
                BlockedReason: previewError ?? "无法生成安全操作方案。",
                AutoSnapshotNote: null,
                BoundaryNote: null,
                Token: "PREFLIGHT-FAIL",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        modTogglePreview = plan;
        string srcRel = Path.GetRelativePath(root, current.Folder);
        string dstRel = enable
            ? current.Original ?? Path.Combine("mods", current.Id)
            : $"{ModManager.StorageName}\\<新保留槽>\\内容";

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: enable ? $"预览恢复启用 · {current.Name}" : $"预览停用并保留 · {current.Name}",
            Subtitle: $"{current.Id} · v{NeonVersionDiff.StripPrefix(current.Version)}",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: enable
                ? "此处的「启用」表示将文件原子移回原 mods/ 位置；恢复前会校验停用期间文件未被篡改。"
                : "停用不会删除任何文件，也不会修改游戏存档；进入依赖本模组的存档前需重新启用。",
            Token: plan.Token,
            Groups:
            [
                new NeonDiffGroup("目录原子移动与文件核验",
                [
                    new NeonDiffLine("mod", "源路径", srcRel, $"{plan.FileCount} 个文件 SHA-256 核对一致"),
                    new NeonDiffLine("add", "目标", dstRel, enable ? "恢复回 mods 目录" : "完整保留文件与记录.json")
                ])
            ],
            ConfirmLabel: enable ? "确认恢复启用" : "确认移入停用区",
            OnConfirmAsync: () => NeonApplyTogglePlanAsync(plan, current.Name)));
    }

    async Task NeonApplyTogglePlanAsync(ModTogglePlan plan, string displayName)
    {
        if (busy || transactionWriting) return;
        busy = true;
        transactionWriting = true;
        modTogglePreview = null;
        try
        {
            var exec = await ModToggleWorkflow.ConfirmAndApplyAsync(
                plan,
                (_, _) => Task.FromResult(true));
            if (!exec.Success)
                throw new InvalidOperationException(exec.Error ?? exec.Status);
            modFileWatcher.DeferQuietPeriod();
            installPreview = null;
            lastInstallResult = null;
            ClearSelection();
            ClearCoop();
            report = null;
            states = [];
            demo = false;
            modInventory = await Task.Run(() => ModManager.Scan(plan.Root));
            modInventoryDirty = false;
            modStatus = displayName + (plan.Enable ? " 已恢复启用。" : " 已停用，文件已完整保留。");
            InvalidateLifecycle("MOD 启停状态已变更，旧的补丁健康结论已自动失效，请重新核验。");
            RecordNeonActivity("toggle", $"{(plan.Enable ? "恢复启用" : "停用保留")}了「{displayName}」。");
            neonWorkbenchView?.ShowToast($"{(plan.Enable ? "已恢复启用" : "已安全停用")} {displayName}，相关行与筛选计数已同步更新");
        }
        catch (Exception ex)
        {
            modStatus = "操作未完成：" + ex.Message;
            neonWorkbenchView?.ShowToast(modStatus, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    /// <summary>
    /// Direct inline enable/disable for single normal MODs (Step 1 pilot generalized in Step 2).
    /// Runs full ModManager.Preview -> Apply -> Scan security pipeline without modal interruption,
    /// keeps the Jelly Toggle at its pre-click position until Apply genuinely succeeds,
    /// separates Apply vs Scan failure boundaries, handles running/dependency blocks inline,
    /// and leaves an in-place resolution receipt right where the player was looking.
    /// </summary>
    async Task NeonDirectToggleModAsync(ManagedMod snapshotMod, string? contextDependentModId)
    {
        if (busy || transactionWriting || selectedInstallRoot == null || modInventory == null || neonWorkbenchView == null)
            return;

        string root = selectedInstallRoot;
        var current = modInventory.Mods.FirstOrDefault(m =>
            m.Folder.Equals(snapshotMod.Folder, StringComparison.OrdinalIgnoreCase) &&
            m.Manifest.Equals(snapshotMod.Manifest, StringComparison.OrdinalIgnoreCase));

        bool enable = current != null ? !current.Enabled : !snapshotMod.Enabled;
        string verb = enable ? "启用" : "停用";

        if (current == null || modInventoryDirty)
        {
            const string dirtyMsg = "MOD 清单已变化，请先刷新清单后再操作。";
            neonWorkbenchView.SetInlineToggleStatus(
                snapshotMod.Id,
                new NeonInlineToggleStatus(snapshotMod.Id, snapshotMod.Name, enable, NeonInlineTogglePhase.Failed, "清单已变化", dirtyMsg),
                rerender: true);
            neonWorkbenchView.ShowToast(dirtyMsg, NeonTone.Amber);
            return;
        }

        bool running = GameEnvironment.Running(root);
        if (running)
        {
            const string runningMsg = "退出游戏后可启停（检测到 SlayTheSpire2.exe 正在运行）。";
            neonWorkbenchView.SetInlineToggleStatus(
                current.Id,
                new NeonInlineToggleStatus(current.Id, current.Name, enable, NeonInlineTogglePhase.Failed, "退出游戏后可启停", runningMsg),
                rerender: true);
            neonWorkbenchView.ShowToast("退出游戏后可启停 " + current.Id, NeonTone.Amber);
            return;
        }

        string? blockReason = ModManager.BlockReason(root, current, modInventory);
        if (blockReason != null)
        {
            if (!enable && blockReason.Contains("依赖它"))
            {
                neonWorkbenchView.SelectModAndShowDependents(current.Id);
                return;
            }
            neonWorkbenchView.SetInlineToggleStatus(
                current.Id,
                new NeonInlineToggleStatus(current.Id, current.Name, enable, NeonInlineTogglePhase.Failed, $"当前无法{verb}", blockReason),
                rerender: true);
            neonWorkbenchView.ShowToast(blockReason, NeonTone.Cinnabar);
            return;
        }

        // Capture affected dependents BEFORE disk apply (when enabling a disabled prerequisite)
        var affectedDependentIds = enable
            ? neonWorkbenchView.CurrentSnapshot?.AllRows
                .Where(r => r.OwnDisabledPrereq != null &&
                            r.OwnDisabledPrereq.DependencyId.Equals(current.Id, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Mod.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>()
            : new List<string>();
        if (enable && !string.IsNullOrWhiteSpace(contextDependentModId) &&
            !contextDependentModId.Equals(current.Id, StringComparison.OrdinalIgnoreCase) &&
            !affectedDependentIds.Contains(contextDependentModId, StringComparer.OrdinalIgnoreCase))
        {
            affectedDependentIds.Add(contextDependentModId);
        }

        busy = true;
        transactionWriting = true;
        neonWorkbenchView.ClearInspectorReceipt(current.Id, rerender: false);
        foreach (string depId in affectedDependentIds)
            neonWorkbenchView.ClearInspectorReceipt(depId, rerender: false);

        // Moment 2: toggle stays honestly at pre-click side while showing crisp in-place pending state
        neonWorkbenchView.SetInlineToggleStatus(
            current.Id,
            new NeonInlineToggleStatus(
                current.Id,
                current.Name,
                enable,
                NeonInlineTogglePhase.Applying,
                enable ? "正在核验文件指纹并恢复启用…" : "正在核验并移入停用保留区…"),
            rerender: true);

        // Stage 1: Real Preview & atomic Apply via ModToggleWorkflowService
        ModTogglePlan? plan = null;
        try
        {
            if (neonTestRestoreStageDelayMs > 0)
                await Task.Delay(neonTestRestoreStageDelayMs);
            plan = await ModToggleWorkflow.PreviewAsync(root, current.Folder, enable);
            var exec = await ModToggleWorkflow.ConfirmAndApplyAsync(
                plan,
                (_, _) => Task.FromResult(true));
            if (!exec.Success)
                throw new InvalidOperationException(exec.Error ?? exec.Status);
            modFileWatcher.DeferQuietPeriod();
        }
        catch (Exception ex)
        {
            busy = false;
            transactionWriting = false;
            string failTitle = enable ? "恢复启用未完成" : "停用未完成";
            neonWorkbenchView.SetInlineToggleStatus(
                current.Id,
                new NeonInlineToggleStatus(current.Id, current.Name, enable, NeonInlineTogglePhase.Failed, failTitle, ex.Message),
                rerender: true);
            neonWorkbenchView.ShowToast($"{failTitle}（{current.Id}）：{ex.Message}", NeonTone.Cinnabar);
            return;
        }

        // Apply genuinely succeeded — files are now atomically moved!
        installPreview = null;
        lastInstallResult = null;
        ClearSelection();
        ClearCoop();
        report = null;
        states = [];
        demo = false;
        InvalidateLifecycle("MOD 启停状态已变更，旧的补丁健康结论已自动失效，请重新核验。");

        neonWorkbenchView.SetInlineToggleStatus(
            current.Id,
            new NeonInlineToggleStatus(
                current.Id,
                current.Name,
                enable,
                NeonInlineTogglePhase.AppliedScanPending,
                enable ? "文件已移回，正在核验依赖状态…" : "已移入停用保留区，正在核验依赖状态…"),
            rerender: true);

        // Stage 2: Post-apply rescan in its OWN try/catch so Scan failure NEVER masquerades as Apply failure!
        int postApplyWatchVersion = Volatile.Read(ref modChangeVersion);
        ModInventory freshInventory;
        try
        {
            freshInventory = await Task.Run(() => neonTestScanOverride != null
                ? neonTestScanOverride(plan.Root)
                : ModManager.Scan(plan.Root));
        }
        catch (Exception scanEx)
        {
            modInventoryDirty = true;
            RecordNeonActivity("toggle", enable
                ? $"已恢复启用「{current.Name}」，但后续扫描未完成（依赖状态待核验）。"
                : $"已将「{current.Name}」移入停用保留区，但后续扫描未完成（依赖状态待核验）。");
            busy = false;
            transactionWriting = false;

            string halfLead = enable ? "已恢复启用，依赖状态待核验：" : "已移入停用区，依赖状态待核验：";
            string detail = enable
                ? $"{current.Id} 文件已移回 mods/，但后续清单扫描未完成（{scanEx.Message}）。请重新扫描刷新依赖与补丁状态。"
                : $"{current.Id} 文件已移入停用保留区，但后续清单扫描未完成（{scanEx.Message}）。请重新扫描刷新依赖与补丁状态。";
            neonWorkbenchView.SetInlineToggleStatus(
                current.Id,
                new NeonInlineToggleStatus(current.Id, current.Name, enable, NeonInlineTogglePhase.AppliedScanFailed, halfLead.TrimEnd('：'), detail),
                rerender: false);

            var halfReceipt = new NeonInspectorResolutionReceipt(
                PrereqId: current.Id,
                PrereqName: current.Name,
                Tone: NeonTone.Amber,
                Lead: halfLead,
                Body: detail,
                ShowRescanAction: true);
            neonWorkbenchView.SetInspectorReceipt(current.Id, halfReceipt, rerender: false);
            foreach (string depId in affectedDependentIds)
                neonWorkbenchView.SetInspectorReceipt(depId, halfReceipt, rerender: false);

            RefreshNeonWorkbench(preserveViewport: true);
            neonWorkbenchView.ShowToast($"{current.Id} {halfLead.TrimEnd('：')}，请点击重新扫描", NeonTone.Amber);
            return;
        }

        // Both Apply and Scan succeeded!
        modInventory = freshInventory;
        modInventoryDirty = false;
        modInventoryCleanVersion = Math.Max(modInventoryCleanVersion, Math.Max(postApplyWatchVersion, Volatile.Read(ref modChangeVersion)));
        modStatus = current.Name + (enable ? " 已恢复启用。" : " 已停用，文件已完整保留。");
        RecordNeonActivity("toggle", enable ? $"恢复启用了「{current.Name}」。" : $"停用并保留了「{current.Name}」。");
        neonWorkbenchView.ClearInlineToggleStatus(current.Id, rerender: false);
        busy = false;
        transactionWriting = false;

        // Build fresh dependency graph & trigger the smooth Jelly Toggle glide!
        RefreshNeonWorkbench(preserveViewport: true);

        // Moment 3: attach honest, persistent in-place confirmation receipts right where the player was looking
        var postSnap = neonWorkbenchView.CurrentSnapshot;
        var clearedShortNames = new List<string>();
        if (enable && postSnap != null)
        {
            foreach (string depId in affectedDependentIds)
            {
                var depAfter = postSnap.AllRows.FirstOrDefault(r => r.Mod.Id.Equals(depId, StringComparison.OrdinalIgnoreCase));
                if (depAfter != null && depAfter.OwnDisabledPrereq == null)
                {
                    string shortDep = NeonWidgets.ShortDisplayName(depAfter.Mod.Name);
                    clearedShortNames.Add(shortDep);
                    neonWorkbenchView.SetInspectorReceipt(
                        depAfter.Mod.Id,
                        new NeonInspectorResolutionReceipt(
                            PrereqId: current.Id,
                            PrereqName: current.Name,
                            Tone: NeonTone.Sage,
                            Lead: $"{current.Id} 已启用，{shortDep}的前置阻断已解除。",
                            Body: ""),
                        rerender: false);
                }
            }
        }

        string shortSelf = NeonWidgets.ShortDisplayName(current.Name);
        string selfLead = enable
            ? (clearedShortNames.Count > 0
                ? $"{current.Id} 已启用，{string.Join("、", clearedShortNames)}的前置阻断已解除。"
                : $"{shortSelf} 已恢复启用。")
            : $"{shortSelf} 已停用，文件完整保留在停用区。";
        string selfBody = enable ? "" : "之后需要时可重新启用（启用前会再次核验指纹与依赖）。";

        neonWorkbenchView.SetInspectorReceipt(
            current.Id,
            new NeonInspectorResolutionReceipt(
                PrereqId: current.Id,
                PrereqName: current.Name,
                Tone: NeonTone.Sage,
                Lead: selfLead,
                Body: selfBody),
            rerender: false);

        // Refresh ONLY the right-hand Inspector so the MOD row's live Jelly Toggle flip plays uninterrupted!
        neonWorkbenchView.RefreshInspectorOnlyPreservingScroll();

        neonWorkbenchView.ShowToast(
            enable
                ? (clearedShortNames.Count > 0
                    ? $"{current.Id} 已启用，{string.Join("、", clearedShortNames)}的前置阻断已解除"
                    : $"已恢复启用 {shortSelf}")
                : $"已停用 {shortSelf}，文件已完整保留在停用区");
    }

    async Task NeonPreviewRetirePatchAsync(NeonRelatedPatch patch)
    {
        if (selectedInstallRoot == null || neonWorkbenchView == null || !HasValidLifecycleReport || lifecycleReport == null)
        {
            neonWorkbenchView?.ShowToast("当前补丁健康结论已过期或尚未核验，禁止退役。", NeonTone.Amber);
            return;
        }
        if (busy || transactionWriting) return;

        busy = true;
        ModTogglePlan? plan = null;
        string? error = null;
        try
        {
            plan = await Task.Run(() => PatchLifecycle.PreviewRetire(lifecycleReport, patch.PatchId, lifecycleEvidence));
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }

        if (error != null || plan == null || patch.LifecycleItem == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"无法安全退役 · {patch.Name}",
                Subtitle: patch.PatchId,
                GameRunning: GameEnvironment.Running(selectedInstallRoot),
                BlockedReason: error ?? "当前不符合安全退役条件。",
                AutoSnapshotNote: null,
                BoundaryNote: null,
                Token: "RETIRE-BLOCKED",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        retirePreview = plan;
        retiringItem = patch.LifecycleItem;
        string relSrc = Path.GetRelativePath(selectedInstallRoot, plan.Folder);
        var ev = patch.LifecycleItem.Evidence;

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"安全退役 · {patch.Name}",
            Subtitle: $"上游 {ev?.TargetModId ?? "原模组"} {ev?.Version ?? ""} 已确认内置此修复",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: "仅移出 mods/ 加载目录并保留全部文件与原路径记录，不删除补丁文件，随时可恢复启用。",
            Token: plan.Token,
            Groups:
            [
                new NeonDiffGroup("上游证据与可逆移动范围",
                [
                    new NeonDiffLine("add", "SHA-256", ev?.Sha256 ?? "已审核证据", $"{ev?.TargetModId}/{ev?.RelativePath} ({ev?.Version})"),
                    new NeonDiffLine("mod", "移动目录", $"{relSrc} → {ModManager.StorageName}\\", $"保留 {plan.FileCount} 个文件，可原样恢复")
                ])
            ],
            ConfirmLabel: "确认安全退役（移入保留区）",
            OnConfirmAsync: () => NeonConfirmRetirePatchAsync(plan, patch.LifecycleItem),
            PlainSummary: $"原模组已确认修复此问题，现在可将「{patch.Name}」安全移入停用保留区（不删除文件，之后随时可恢复）。"));
    }

    async Task NeonConfirmRetirePatchAsync(ModTogglePlan plan, LifecyclePatchItem item)
    {
        if (busy || transactionWriting || selectedInstallRoot == null) return;
        retirePreview = null;
        retiringItem = null;
        busy = true;
        transactionWriting = true;
        try
        {
            await Task.Run(() => GameEnvironment.WithLock(plan.Root, () =>
            {
                var proof = item.Evidence ?? throw new InvalidOperationException("缺少可审核的上游修复证据，请重新预览。");
                if (proof.ReviewStatus != "approved" || proof.Decision is not ("fixed_upstream" or "incompatible") ||
                    !PatchLifecycle.MatchesEvidence(plan.Root, ModManager.Scan(plan.Root), proof))
                    throw new InvalidOperationException("原 MOD 的文件指纹在预览后发生变化，请重新扫描并核对。");
                ModManager.Apply(plan);
                return true;
            }));
            string? auditWarning = null;
            try { PatchLifecycle.RecordRetirement(plan.Root, NeonDataRoot, item, plan); }
            catch (Exception e) { auditWarning = "退役审计记录写入失败：" + e.Message; }

            installPreview = null;
            lastInstallResult = null;
            ClearSelection();
            ClearCoop();
            report = null;
            states = [];
            demo = false;
            modInventory = await Task.Run(() => ModManager.Scan(plan.Root));
            modInventoryDirty = false;
            InvalidateLifecycle(item.Patch.Name + " 已安全退役至停用保留区，请重新核验补丁健康。" + (auditWarning == null ? "" : " " + auditWarning));
            RecordNeonActivity("patch", $"安全退役独立补丁「{item.Patch.Name}」（移入停用保留区并写入审计记录）。");
            neonWorkbenchView?.ShowToast($"已安全退役 {item.Patch.Name}（已移入停用保留区并写入审计记录）");
        }
        catch (Exception ex)
        {
            InvalidateLifecycle("退役未完成，请重新扫描核对：" + ex.Message);
            neonWorkbenchView?.ShowToast("退役未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    void SimulateExternalChangeInFixture()
    {
        if (selectedInstallRoot == null || neonFixtureRoot == null ||
            !selectedInstallRoot.Equals(neonFixtureRoot, StringComparison.OrdinalIgnoreCase))
        {
            neonWorkbenchView?.ShowToast("仅在隔离演示目录中支持一键模拟外部文件改动。", NeonTone.Amber);
            return;
        }
        try
        {
            // Write a real file inside the isolated fixture's mods/ folder so the real FileSystemWatcher fires!
            string marker = Path.Combine(selectedInstallRoot, "mods", "external-change-marker.txt");
            File.WriteAllText(marker, "External edit at " + DateTimeOffset.Now.ToString("O"));
            // Immediately mirror invalidation if OS watcher delivery is slightly delayed
            modInventoryDirty = true;
            InvalidateLifecycle("检测到 MOD 文件变化，请重新检查健康状态。");
            neonWorkbenchView?.ShowToast("已写入隔离目录外部文件改动，旧的“校验通过”健康结论已当场撤下", NeonTone.Amber);
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("模拟外部改动失败：" + ex.Message, NeonTone.Cinnabar);
        }
    }
}
