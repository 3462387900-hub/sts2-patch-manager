using System.IO;
using System.Windows;
using System.Windows.Automation;
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
    internal int neonTestScanDelayMs;
    bool neonScanning;
    string? neonScanStageText;
    bool suppressNeonHealthInvalidationRefresh;
    string? neonRecoveryCacheRoot;
    ModEnvironmentSnapshotEntry[] neonCachedSnapshots = [];
    string? neonCachedSnapshotsWarning;
    NeonPatchBackupBatch? neonCachedLatestPatchBatch;
    bool neonRecoveryCacheDirty = true;
    bool? neonCachedOrphanAutoStop;
    NeonGitHubNetworkState? neonCachedGitHubNetwork;
    internal Func<string, ModInventory>? neonTestScanOverride;

    // A watcher generation can restart at zero after a directory switch; the session
    // epoch prevents an old A -> B -> A scan from ever being accepted.
    long neonRootEpoch;
    bool neonRescanPending;
    bool neonRescanQueued;
    internal bool neonTestHoldRescan;
    internal readonly record struct NeonScanStamp(string Root, int Generation, long RootEpoch);

    NeonScanStamp NeonCaptureScanStamp(string root) =>
        new(root, modFileWatcher.ChangeVersion, Volatile.Read(ref neonRootEpoch));

    bool NeonScanStampCurrent(NeonScanStamp stamp) =>
        NeonRootMatches(stamp.Root) &&
        string.Equals(modFileWatcher.CurrentRoot, stamp.Root, StringComparison.OrdinalIgnoreCase) &&
        stamp.Generation == modFileWatcher.ChangeVersion &&
        stamp.RootEpoch == Volatile.Read(ref neonRootEpoch);

    bool NeonTryPublishInventory(NeonScanStamp stamp, ModInventory inventory, Action? publishOtherScanState = null)
    {
        bool published = false;
        if (NeonScanStampCurrent(stamp))
        {
            modFileWatcher.TryRunIfCurrent(stamp.Root, stamp.Generation, () =>
            {
                if (!NeonRootMatches(stamp.Root) || stamp.RootEpoch != Volatile.Read(ref neonRootEpoch) ||
                    !string.Equals(modFileWatcher.CurrentRoot, stamp.Root, StringComparison.OrdinalIgnoreCase)) return;
                modInventory = inventory;
                modInventoryDirty = false;
                // Never acknowledge a generation that the actual scan did not cover.
                modInventoryCleanVersion = stamp.Generation;
                publishOtherScanState?.Invoke();
                published = true;
            });
        }
        if (!published) NeonRejectStaleScan();
        return published;
    }

    void NeonRejectStaleScan()
    {
        modInventoryDirty = true;
        report = null; states = []; ClearCoop(); ClearSelection(); installPreview = null; modTogglePreview = null;
        if (selectedInstallRoot != null && string.Equals(modFileWatcher.CurrentRoot, selectedInstallRoot, StringComparison.OrdinalIgnoreCase))
            neonRescanPending = true;
        modFileWatcher.DeferQuietPeriod();
        InvalidateLifecycle("扫描期间目录或文件监测世代已变化，旧结果已丢弃，请重新核验。");
    }

    void NeonSchedulePendingRescan()
    {
        if (neonFixtureDataRoot != null && neonTestHoldRescan) return;
        if (!neonRescanPending || neonRescanQueued || busy || transactionWriting ||
            !UiThemes.IsNeon || selectedInstallRoot == null || Dispatcher.HasShutdownStarted) return;
        neonRescanQueued = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            neonRescanQueued = false;
            if (!neonRescanPending || busy || transactionWriting || !UiThemes.IsNeon) return;
            neonRescanPending = false;
            if (selectedInstallRoot != null) RefreshMods();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    async Task<bool> NeonScanAndPublishAsync(string root)
    {
        var stamp = NeonCaptureScanStamp(root);
        modInventoryDirty = true;
        try
        {
            var inventory = await Task.Run(() => neonTestScanOverride != null ? neonTestScanOverride(root) : ModManager.Scan(root));
            return NeonTryPublishInventory(stamp, inventory);
        }
        catch (Exception error)
        {
            modInventoryDirty = true;
            InvalidateLifecycle("文件操作已结束，但清单扫描未完成：" + error.Message);
            neonWorkbenchView?.ShowToast("文件操作已结束，清单仍待重新扫描：" + error.Message, NeonTone.Amber);
            return false;
        }
    }

    const string NeonZipRecoveryKind = "本地 ZIP 安装";
    const string NeonUpdateRecoveryKind = "在线 MOD 更新";
    internal sealed record NeonRecoveryStatus(string Operation, string LogDirectory, string? FailureReason)
    {
        public bool Blocked => FailureReason != null;
    }
    readonly Dictionary<string, Dictionary<string, NeonRecoveryStatus>> neonRecoveryStates = new(StringComparer.OrdinalIgnoreCase);
    string NeonZipLogDirectory => Path.Combine(NeonDataRoot, "LocalModZipInstallTransactions");
    string NeonUpdateLogDirectory => Path.Combine(NeonDataRoot, "MOD更新事务");

    internal static async Task<NeonRecoveryStatus> NeonRunRecoveryStepAsync(
        string operation, string logDirectory, Func<Task<string?>> recover)
    {
        try
        {
            // Directory.Exists hides access errors; only a genuinely missing directory is benign.
            await Task.Run(() =>
            {
                try
                {
                    if ((File.GetAttributes(logDirectory) & FileAttributes.Directory) == 0)
                        throw new IOException("事务日志路径不是目录。");
                    _ = Directory.EnumerateFileSystemEntries(logDirectory).Take(1).ToArray();
                }
                catch (DirectoryNotFoundException) { }
                catch (FileNotFoundException) { }
            });
            return new(operation, logDirectory, await recover());
        }
        catch (Exception error) { return new(operation, logDirectory, error.GetType().Name + "：" + error.Message); }
    }

    void NeonRecordRecoveryStatus(string root, NeonRecoveryStatus status)
    {
        if (!neonRecoveryStates.TryGetValue(root, out var statesForRoot))
            neonRecoveryStates[root] = statesForRoot = new(StringComparer.Ordinal);
        statesForRoot[status.Operation] = status;
        if (NeonRootMatches(root))
            neonWorkbenchView?.SetRecoveryWarning(NeonRecoveryWarning(root));
    }

    void NeonSetRecoveryFailure(string root, string operation, string logDirectory, string reason) =>
        NeonRecordRecoveryStatus(root, new(operation, logDirectory, reason));

    bool NeonRecoveryChecked(string root) =>
        neonRecoveryStates.TryGetValue(root, out var state) &&
        state.ContainsKey(NeonZipRecoveryKind) && state.ContainsKey(NeonUpdateRecoveryKind);

    bool NeonRecoveryBlocked(string root) =>
        neonRecoveryStates.TryGetValue(root, out var state) && state.Values.Any(s => s.Blocked);

    string? NeonRecoveryWarning(string? root)
    {
        if (root == null || !neonRecoveryStates.TryGetValue(root, out var state)) return null;
        var failures = state.Values.Where(s => s.Blocked).Select(s =>
            $"{s.Operation}事务恢复未完成：{s.FailureReason}\n日志目录：{s.LogDirectory}").ToArray();
        return failures.Length == 0 ? null :
            "事务恢复待处理 · 已阻断可能改动 MOD / 补丁文件的操作。修复日志或访问权限后点击刷新扫描重试。\n" +
            string.Join("\n", failures);
    }

    bool NeonCanWrite(string? root)
    {
        if (root == null || !NeonRootMatches(root)) return false;
        if (!NeonRecoveryChecked(root) || NeonRecoveryBlocked(root))
        {
            neonWorkbenchView?.ShowToast(NeonRecoveryBlocked(root)
                ? "存在未解决的事务恢复异常，已阻断文件改动，请查看持续警告。"
                : "尚未完成当前目录的事务恢复检查，请先刷新扫描。", NeonTone.Amber);
            return false;
        }
        return true;
    }

    async Task NeonRecoverTransactionsAsync(string root)
    {
        bool wasWriting = transactionWriting;
        transactionWriting = true;
        try
        {
        // Each family owns its own exception boundary. One unreadable journal must
        // never prevent the other family from being inspected or recovered.
        var zipStatus = await NeonRunRecoveryStepAsync(NeonZipRecoveryKind, NeonZipLogDirectory, async () =>
        {
            var session = NeonLocalZipSession();
            var recovery = await Task.Run(() => session.RecoverPendingTransactions(root, () => modFileWatcher.DeferQuietPeriod()));
            if (NeonRootMatches(root))
            {
                var plan = HandleLocalZipRecoveryReport(root, recovery, session.TransactionLogDirectory);
                ApplyLocalZipInvalidationPlan(root, plan);
                if (recovery.CompletedCount > 0) neonRecoveryCacheDirty = true;
            }
            return recovery.HasPendingRecovery || (NeonRootMatches(root) && localZipRecoveryBlocked)
                ? LocalZipUiResultPresenter.FormatRecoveryReport(recovery, session.TransactionLogDirectory)
                    ?? "发现待人工核对的本地 ZIP 安装事务。"
                : null;
        });
        NeonRecordRecoveryStatus(root, zipStatus);
        var updateStatus = await NeonRunRecoveryStepAsync(NeonUpdateRecoveryKind, NeonUpdateLogDirectory, async () =>
        {
            var recovery = await Task.Run(() => NeonModUpdateApplyService().RecoverPendingTransactions(root));
            if (recovery.ChangedTargetOrCleanedTransaction && NeonRootMatches(root))
            {
                modSourceCheckWorkflow?.InvalidateAll();
                neonRecoveryCacheDirty = true;
                modInventoryDirty = true;
                report = null; states = []; ClearCoop(); ClearSelection(); installPreview = null; modTogglePreview = null;
                InvalidateLifecycle("在线 MOD 事务已恢复，旧的健康及来源结论已失效。");
            }
            return recovery.HasPendingRecovery
                ? string.Join("\n", recovery.Items.Where(i => i.Pending).Select(i =>
                    $"{i.TransactionId} · {i.State}：{i.Error ?? "需要人工核对"}"))
                : null;
        });
        NeonRecordRecoveryStatus(root, updateStatus);
        if (NeonRootMatches(root) && NeonRecoveryBlocked(root))
            InvalidateLifecycle("存在未解决的事务恢复异常，暂停文件写入与健康确认。");
        }
        finally { transactionWriting = wasWriting; modFileWatcher.DeferQuietPeriod(); }
    }

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

        if (selectedInstallRoot != null && (modInventory == null || modInventoryDirty || !NeonRecoveryChecked(selectedInstallRoot)) && !busy && !transactionWriting)
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
        view.RequestRescanAll += () => _ = RefreshNeonFullScanAndHealthAsync(includeLifecycle: false, userInitiated: true);
        view.RequestRescanAndVerifyHealth += () => _ = RefreshNeonFullScanAndHealthAsync(includeLifecycle: true, userInitiated: true);
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
        view.RequestRestoreModBackup += NeonPreviewRestoreModBackup;
        view.RequestOpenModFolder += mod =>
        {
            if (Directory.Exists(mod.Folder)) OpenUrl(mod.Folder);
        };
        view.RequestBatchToggleEnable += folders => _ = NeonPreviewBatchToggleAsync(folders, enable: true);
        view.RequestBatchToggleDisable += folders => _ = NeonPreviewBatchToggleAsync(folders, enable: false);
        view.RequestBatchCheckUpdates += folders => _ = NeonCheckBatchModUpdatesAsync(folders);
        view.RequestPreviewRetirePatch += patch => _ = NeonPreviewRetirePatchAsync(patch);
        view.RequestDiagnoseLog += () => _ = NeonChooseAndDiagnoseLogAsync();
        view.RequestSelectManagerChannel += SelectManagerUpdateChannel;
        view.RequestCheckManagerUpdate += () => _ = CheckManagerUpdatesAsync(showPage: true);
        view.RequestOpenManagerUpdateModal += NeonOpenManagerUpdateModal;
        view.RequestCleanupOldManagerFiles += CleanupOldManagerFiles;
        view.RequestOpenManagerReleasePage += () => OpenUrl(managerRelease?.ReleaseUrl ?? ManagerUpdates.RepositoryUrl);
        view.RequestOpenGitHubNetworkModal += NeonOpenGitHubNetworkModal;
        view.RequestDiagnoseGitHubNetwork += () => _ = NeonDiagnoseGitHubNetworkAsync();
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
        bool running = await CheckGameRunningOffUiAsync(root);
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
        if (!NeonCanWrite(root)) return;
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
                    await NeonScanAndPublishAsync(root);
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

        if (wsId.HasValue && WorkshopUpdates().Cached(mod.Id) is { Status: "有更新" } workshopCheck)
        {
            await NeonStageAndPreviewModUpdateAsync(
                root, mod,
                NeonWorkshopUpdateRequest(wsId.Value, workshopCheck.RemoteManifestId),
                null);
            return;
        }

        neonWorkbenchView.ShowToast("当前没有可预览的更新来源。", NeonTone.Amber);
    }

    // Both Workshop entry points construct the identical, manifest-pinned request.
    internal static ModUpdateStagingRequest NeonWorkshopUpdateRequest(ulong workshopId, string manifestId) =>
        new(ModUpdateStagingSource.Workshop, workshopId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ExpectedWorkshopManifestId: manifestId);

    internal IModUpdateStagingBackend? neonTestStagingBackend;

    bool NeonRootMatches(string root) => string.Equals(root, selectedInstallRoot, StringComparison.OrdinalIgnoreCase);

    async Task NeonStageAndPreviewModUpdateAsync(
        string root,
        ManagedMod mod,
        ModUpdateStagingRequest request,
        string? releaseUrl)
    {
        if (busy || transactionWriting || neonWorkbenchView == null || modInventoryDirty || !NeonCanWrite(root)) return;
        if (await CheckGameRunningOffUiAsync(root))
        {
            neonWorkbenchView.ShowToast("请退出游戏后再更新。", NeonTone.Amber);
            return;
        }
        if (busy || transactionWriting || modInventoryDirty || !NeonCanWrite(root)) return;
        if (neonFixtureDataRoot != null && neonTestStagingBackend == null)
        {
            neonWorkbenchView.ShowToast("隔离测试禁止使用真实下载后端。", NeonTone.Amber);
            return;
        }
        busy = true;
        using var backend = new InfrastructureModUpdateStagingBackend();
        var stagingSvc = new ModUpdateStagingWorkflowService(
            neonTestStagingBackend ?? backend,
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
            bool stageFailedRunning = await CheckGameRunningOffUiAsync(root);
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"无法预览 MOD 更新 · {mod.Name}",
                Subtitle: request.Source == ModUpdateStagingSource.GitHub ? $"GitHub {request.SourceIdentifier}" : $"Workshop #{request.SourceIdentifier}",
                GameRunning: stageFailedRunning,
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
        ModUpdateApplyPreview applyPreview;
        try
        {
            if (!NeonRootMatches(root) || modInventoryDirty)
                throw new InvalidOperationException("暂存期间游戏目录或清单已变化，请重新预览。");
            applyPreview = applySvc.CreatePreview(root, mod, staged);
        }
        catch (Exception ex)
        {
            if (staged.StagingDirectory is { } discarded) stagingSvc.Discard(discarded);
            neonWorkbenchView.ShowToast("更新预览未完成：" + ex.Message, NeonTone.Amber);
            return;
        }
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

        bool stageReadyRunning = await CheckGameRunningOffUiAsync(root);
        string stagingDir = staged.StagingDirectory ?? "";
        if (busy || transactionWriting || modInventoryDirty || !NeonRootMatches(root))
        {
            if (stagingDir.Length > 0) stagingSvc.Discard(stagingDir);
            return;
        }
        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"确认更新 MOD · {mod.Name}",
            Subtitle: $"v{NeonVersionDiff.StripPrefix(staged.LocalVersion)} → v{NeonVersionDiff.StripPrefix(staged.StagedVersion ?? "新版")}",
            GameRunning: stageReadyRunning,
            BlockedReason: null,
            AutoSnapshotNote: "确认前将自动创建完整环境快照与事务备份；若替换途中意外中断，下次启动将自动回滚或安全恢复。",
            BoundaryNote: "下载与校验均已在独立隔离暂存区完成，确认前未接触你的 mods 目录。",
            Token: $"UPD-{mod.Id}",
            Groups: [new NeonDiffGroup($"文件级差异预览（共 {staged.FileChanges.Count} 处变更）", changeLines)],
            ConfirmLabel: "确认备份并更新",
            CancelLabel: "取消",
            OnConfirmAsync: async () =>
            {
                // A rejected stale confirmation never transfers staging ownership to the apply service.
                if (busy || transactionWriting || modInventoryDirty || !NeonCanWrite(root))
                {
                    if (stagingDir.Length > 0) stagingSvc.Discard(stagingDir);
                    neonWorkbenchView?.ShowToast("目录或清单已变化，请重新预览。", NeonTone.Amber);
                    return;
                }
                await NeonConfirmStagedModUpdateAsync(root, mod, applySvc, applyPreview, request);
            },
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
        if (busy || transactionWriting || neonWorkbenchView == null || modInventoryDirty || !NeonCanWrite(root)) return;
        busy = true;
        transactionWriting = true;
        try
        {
            if (await CheckGameRunningOffUiAsync(root))
            {
                if (applyPreview.Staging.StagingDirectory is { } dir)
                {
                    using var cleanupBackend = new InfrastructureModUpdateStagingBackend();
                    new ModUpdateStagingWorkflowService(neonTestStagingBackend ?? cleanupBackend,
                        Path.Combine(NeonDataRoot, "MOD更新暂存")).Discard(dir);
                }
                neonWorkbenchView.ShowToast("游戏运行中，已取消更新。", NeonTone.Amber);
                return;
            }
            var safety = await NeonSnapshotWorkflow(root).CreateAsync("更新前自动保护");
            var applyResult = await applySvc.ApplyAsync(applySvc.Confirm(applyPreview, confirmed: true));
            modFileWatcher.DeferQuietPeriod();

            if (!applyResult.Success)
            {
                if (applyResult.RecoveryState == ModUpdateApplyRecoveryState.PendingManualRecovery ||
                    applyResult.FailureKind is ModUpdateApplyFailureKind.RecoveryRequired or ModUpdateApplyFailureKind.RecoveryFailed)
                    NeonSetRecoveryFailure(root, NeonUpdateRecoveryKind, NeonUpdateLogDirectory,
                        applyResult.FailureReason ?? "存在尚未恢复的在线更新事务。");
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
            await NeonScanAndPublishAsync(root);

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
            orphanAutoStopEnabled: neonCachedOrphanAutoStop ??= OrphanPatchAutoStop.Enabled(NeonDataRoot),
            onlineRefreshing: onlineBusy,
            onlineSourceStatus: onlineStatus,
            onlineCatalogCount: onlineCatalog?.Patches.Count ?? Catalog.Bundled.Length,
            checkingModUpdates: modUpdateCheckRunning,
            lastUpdateCheckTimeLabel: neonLastUpdateCheckTimeLabel,
            coopState: BuildNeonCoopSnapshotState(),
            recoveryState: BuildNeonRecoverySnapshotState(),
            managerUpdateState: BuildNeonManagerUpdateSnapshotState(),
            githubNetworkState: BuildNeonGitHubNetworkSnapshotState(),
            isScanning: neonScanning,
            scanStageText: neonScanStageText);

        neonWorkbenchView.ApplySnapshot(snapshot, preserveViewport);
        neonWorkbenchView.SetRecoveryWarning(NeonRecoveryWarning(selectedInstallRoot));
        NeonSchedulePendingRescan();
    }

    /// <summary>
    /// Called immediately on the UI thread when InvalidateLifecycle or QueueModChange fires,
    /// withdrawing stale "校验通过" markers on the spot while preserving search, focus, and scroll!
    /// </summary>
    void NotifyNeonHealthInvalidated()
    {
        if (suppressNeonHealthInvalidationRefresh || !UiThemes.IsNeon || neonWorkbenchView == null) return;
        RefreshNeonWorkbench(preserveViewport: true);
    }

    async Task<bool> CheckGameRunningOffUiAsync(string root)
    {
        bool running = await Task.Run(() => GameEnvironment.Running(root));
        neonGameRunningRoot = root;
        neonGameRunningCached = running;
        neonLastRunningPollMs = Environment.TickCount64;
        return running;
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

    internal async Task RefreshNeonFullScanAndHealthAsync(bool includeLifecycle, bool userInitiated = false)
    {
        if (selectedInstallRoot == null)
        {
            if (userInitiated)
            {
                neonWorkbenchView?.ShowToast("尚未选择游戏目录，请先选择或自动查找《杀戮尖塔 2》目录。", NeonTone.Amber);
                _ = DiscoverNeonGameRootsAsync();
            }
            return;
        }
        if (busy || transactionWriting)
        {
            if (userInitiated)
            {
                string busyMsg = neonScanning
                    ? "正在扫描当前目录，请稍候片刻…"
                    : transactionWriting
                        ? "当前有文件改动正在执行，完成后可重新扫描。"
                        : "当前有其他检查任务正在进行，请稍候…";
                neonWorkbenchView?.ShowToast(busyMsg, transactionWriting ? NeonTone.Amber : NeonTone.Neutral);
            }
            return;
        }

        string root = selectedInstallRoot;
        int epoch = lifecycleEpoch;
        busy = true;
        neonScanning = true;
        neonScanStageText = "正在扫描本地 MOD 目录与依赖清单…";
        if (includeLifecycle) lifecycleScanning = true;
        RefreshNeonWorkbench(preserveViewport: true);
        if (userInitiated)
            neonWorkbenchView?.ShowToast("正在扫描本地 MOD 清单…", NeonTone.Neutral);
        try
        {
            if (neonTestScanDelayMs > 0)
                await Task.Delay(neonTestScanDelayMs);

            var definitions = sourceCurrent && onlineCatalog != null ? onlineCatalog.Patches : Catalog.Bundled;
            string revision = sourceCurrent && onlineCatalog != null ? onlineCatalog.Commit : Catalog.Revision;

            await NeonRecoverTransactionsAsync(root);

            // Recovery may itself write files. Capture the scan generation only after it has finished.
            if (!NeonRootMatches(root)) { NeonRejectStaleScan(); return; }
            epoch = lifecycleEpoch;
            var scanStamp = NeonCaptureScanStamp(root);
            // Phase 1: Fast local MOD inventory + patch scan + recovery cache preload so UI stays non-blocking.
            var scanned = await Task.Run(() =>
            {
                var inv = ModManager.Scan(root);
                var patchRep = Catalog.Scan(root, definitions, revision);
                var recCache = LoadRecoveryCacheSync(root);
                return (inv, patchRep, recCache);
            });

            if (!NeonTryPublishInventory(scanStamp, scanned.inv, () =>
            {
                neonRecoveryCacheRoot = root;
                neonCachedSnapshots = scanned.recCache.Snapshots;
                neonCachedSnapshotsWarning = scanned.recCache.Warning;
                neonCachedLatestPatchBatch = scanned.recCache.Batch;
                neonRecoveryCacheDirty = false;
                report = scanned.patchRep;
                scannedCatalog = definitions;
                states = scanned.patchRep.Patches;
                demo = false;
            })) return;
            neonWorkbenchView?.ClearAllPendingScanStates();

            // Phase 2: History hashing + upstream lifecycle evidence check (list already visible!).
            if (includeLifecycle && !NeonRecoveryBlocked(root) && scanned.inv.Issues.Count == 0 && scanned.patchRep.InventoryComplete)
            {
                neonScanStageText = "本地清单已读取，正在核对文件指纹与补丁健康…";
                RefreshNeonWorkbench(preserveViewport: true);

                var hist = await Task.Run(() => ModHistory.CompareAndStore(root, NeonDataRoot));
                if (epoch != lifecycleEpoch || !NeonScanStampCurrent(scanStamp))
                {
                    NeonRejectStaleScan();
                    return;
                }

                LifecycleRegistry evidence;
                string evidenceStatus;
                if (neonOverrideLifecycleEvidence != null)
                {
                    evidence = neonOverrideLifecycleEvidence;
                    evidenceStatus = $"已加载 {evidence.Entries.Length} 条已审核上游修复证据记录。";
                }
                else
                {
                    neonScanStageText = "本地指纹已核对，正在联网查询上游修复证据…";
                    RefreshNeonWorkbench(preserveViewport: true);
                    try
                    {
                        using var evidenceCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        evidence = await LifecycleEvidenceSource.FetchAsync(evidenceCts.Token);
                        evidenceStatus = $"已检查补丁库：{evidence.Entries.Length} 条有证据的上游记录。";
                    }
                    catch (Exception ex)
                    {
                        evidence = LifecycleEvidenceSource.Empty;
                        evidenceStatus = "在线退役证据暂不可用，仅显示本地指纹状态，禁止自动退役：" + ex.Message;
                    }
                }

                var evaluated = PatchLifecycle.Evaluate(root, hist, evidence, evidenceStatus, scanned.patchRep, scanned.inv);
                if (epoch != lifecycleEpoch || !NeonScanStampCurrent(scanStamp))
                {
                    NeonRejectStaleScan();
                    return;
                }
                bool healthPublished = false;
                modFileWatcher.TryRunIfCurrent(scanStamp.Root, scanStamp.Generation, () =>
                {
                    if (!NeonRootMatches(scanStamp.Root) || scanStamp.RootEpoch != Volatile.Read(ref neonRootEpoch) ||
                        epoch != lifecycleEpoch) return;
                    lifecycleEvidence = evidence;
                    lifecycleReport = evaluated;
                    lifecycleStale = false;
                    lifecycleStatus = hist.FirstScan
                        ? "已完成本地指纹与健康基线核验；适配结论仅覆盖已收录条件。"
                        : $"核验完成：发现变化 {hist.Changes.Length} 项 · 待人工核对 {hist.PendingReview.Length} 项。";
                    healthPublished = true;
                });
                if (!healthPublished) { NeonRejectStaleScan(); return; }
                RecordNeonActivity("scan", $"完成补丁健康核验（{scanned.inv.Mods.Count(m => !ModPresentation.IsIndependentPatch(m))} 项普通 MOD · 变化 {hist.Changes.Length} 项）。");
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
            neonScanning = false;
            neonScanStageText = null;
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
        bool running = await CheckGameRunningOffUiAsync(root);
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
        if (busy || transactionWriting || !NeonCanWrite(plan.Root)) return;
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
            bool fresh = await NeonScanAndPublishAsync(plan.Root);
            modStatus = displayName + (plan.Enable ? " 已恢复启用。" : " 已停用，文件已完整保留。");
            InvalidateLifecycle("MOD 启停状态已变更，旧的补丁健康结论已自动失效，请重新核验。");
            RecordNeonActivity("toggle", $"{(plan.Enable ? "恢复启用" : "停用保留")}了「{displayName}」。");
            neonWorkbenchView?.ShowToast($"{(plan.Enable ? "已恢复启用" : "已安全停用")} {displayName}" + (fresh ? "，相关行与筛选计数已同步更新" : "，清单仍待重新扫描。"), fresh ? NeonTone.Sage : NeonTone.Amber);
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
        if (!NeonCanWrite(root)) return;
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

        if (string.Equals(neonGameRunningRoot, root, StringComparison.OrdinalIgnoreCase) && neonGameRunningCached)
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

        // Stage 1: Authoritative running check (off UI thread) -> Preview -> atomic Apply via ModToggleWorkflowService
        ModTogglePlan? plan = null;
        try
        {
            if (neonTestRestoreStageDelayMs > 0)
                await Task.Delay(neonTestRestoreStageDelayMs);
            if (await CheckGameRunningOffUiAsync(root))
            {
                busy = false;
                transactionWriting = false;
                const string runningMsg = "退出游戏后可启停（检测到 SlayTheSpire2.exe 正在运行）。";
                neonWorkbenchView.SetInlineToggleStatus(
                    current.Id,
                    new NeonInlineToggleStatus(current.Id, current.Name, enable, NeonInlineTogglePhase.Failed, "退出游戏后可启停", runningMsg),
                    rerender: true);
                neonWorkbenchView.ShowToast("退出游戏后可启停 " + current.Id, NeonTone.Amber);
                return;
            }
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

        // Apply genuinely succeeded — files are now atomically moved! Coalesce health invalidation with the upcoming single refresh.
        installPreview = null;
        lastInstallResult = null;
        ClearSelection();
        ClearCoop();
        report = null;
        states = [];
        demo = false;
        suppressNeonHealthInvalidationRefresh = true;
        try { InvalidateLifecycle("MOD 启停状态已变更，旧的补丁健康结论已自动失效，请重新核验。"); }
        finally { suppressNeonHealthInvalidationRefresh = false; }

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
        var postApplyStamp = NeonCaptureScanStamp(plan.Root);
        ModInventory freshInventory;
        try
        {
            freshInventory = await Task.Run(() => neonTestScanOverride != null
                ? neonTestScanOverride(plan.Root)
                : ModManager.Scan(plan.Root));
            if (!NeonTryPublishInventory(postApplyStamp, freshInventory))
                throw new InvalidOperationException("扫描期间目录或文件监测世代变化，已安排重新扫描。");
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
        // Guarded publication above is the only clean-state commit.
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
            bool running = await CheckGameRunningOffUiAsync(selectedInstallRoot);
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"无法安全退役 · {patch.Name}",
                Subtitle: patch.PatchId,
                GameRunning: running,
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
        if (busy || transactionWriting || !NeonCanWrite(plan.Root)) return;
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
            await NeonScanAndPublishAsync(plan.Root);
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

    NeonManagerUpdateState BuildNeonManagerUpdateSnapshotState()
    {
        bool hasNewer = managerRelease != null && ManagerUpdates.IsNewer(managerRelease);
        return new NeonManagerUpdateState(
            CurrentVersion: ManagerUpdates.CurrentReleaseVersion,
            Channel: managerUpdateChannel,
            Busy: managerUpdateBusy,
            CleanupBusy: managerCleanupBusy,
            HasNewerRelease: hasNewer,
            CanAutoInstall: managerRelease != null && CanInstallManagerRelease(managerRelease),
            LatestVersion: managerRelease?.Version,
            ReleaseNotes: managerRelease?.Notes,
            ReleaseUrl: managerRelease?.ReleaseUrl,
            LatestSizeBytes: managerRelease?.Size ?? 0,
            Status: managerUpdateStatus,
            CleanupStatus: managerCleanupStatus,
            DownloadedArchivePath: managerDownload);
    }

    NeonGitHubNetworkState BuildNeonGitHubNetworkSnapshotState()
    {
        if (neonCachedGitHubNetwork != null)
            return neonCachedGitHubNetwork;
        try
        {
            var cfg = GitHubNetwork.Load(installer.DataRoot);
            neonCachedGitHubNetwork = new NeonGitHubNetworkState(cfg.Mode, cfg.ProxyUrl);
        }
        catch
        {
            neonCachedGitHubNetwork = new NeonGitHubNetworkState(GitHubNetwork.Default.Mode, GitHubNetwork.Default.ProxyUrl);
        }
        return neonCachedGitHubNetwork;
    }

    async Task NeonChooseAndDiagnoseLogAsync()
    {
        string defaultLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2", "logs");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择本次游戏日志",
            Filter = "日志文件|*.log;*.txt|所有文件|*.*",
            InitialDirectory = Directory.Exists(defaultLogs) ? defaultLogs : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true) return;
        await NeonDiagnoseLogFileAsync(dialog.FileName);
    }

    internal async Task NeonDiagnoseLogFileAsync(string logPath)
    {
        if (neonWorkbenchView == null) return;
        try
        {
            string text = await Task.Run(() =>
            {
                using var f = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var r = new StreamReader(f);
                var lines = new Queue<string>();
                while (r.ReadLine() is { } line)
                {
                    lines.Enqueue(line);
                    if (lines.Count > 12000) lines.Dequeue();
                }
                return string.Join('\n', lines);
            });

            var matchedLines = new List<NeonDiffLine>();
            var plainBullets = new List<string>();
            if (text.Contains("Version mismatch: Act4FinalAscent"))
            {
                plainBullets.Add("明确匹配：第四幕文件校验不一致，对应「联机兼容校验更新」。仍需核对本机文件版本后再安装。");
                matchedLines.Add(new NeonDiffLine("mod", "已知特征", "Version mismatch: Act4FinalAscent", "对应补丁：联机兼容校验更新"));
            }
            if (text.Contains("OverflowException") && text.Contains("NHealNumVfx"))
            {
                plainBullets.Add("匹配：超大回血飘字溢出，对应「超大回血数字溢出」补丁。");
                matchedLines.Add(new NeonDiffLine("mod", "已知特征", "OverflowException + NHealNumVfx", "对应补丁：超大回血数字溢出"));
            }
            if (text.Contains("MutableModelException"))
            {
                plainBullets.Add("检测到可变模型异常。目前没有针对该异常的已验证修复，不能自动归因到某个模组。");
                matchedLines.Add(new NeonDiffLine("del", "未收录异常", "MutableModelException", "暂无已验证修复方案"));
            }

            string summary = plainBullets.Count > 0
                ? string.Join("\n\n", plainBullets)
                : "最近 12,000 行未匹配到已收录的故障特征。这不代表没有故障；未响应或闪退仍需结合完整日志与其他线索排查。";
            if (matchedLines.Count == 0)
                matchedLines.Add(new NeonDiffLine("mod", "匹配结果", "最近 12,000 行无已收录特征", Path.GetFileName(logPath)));

            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "游戏日志故障特征诊断",
                Subtitle: $"{Path.GetFileName(logPath)} · 已扫描末尾至多 12,000 行（只读，不上传）",
                GameRunning: false,
                BlockedReason: null,
                AutoSnapshotNote: null,
                BoundaryNote: "仅在本机匹配已知崩溃/报错特征字符串；不会读取存档或上传日志。",
                Token: "LOG-DIAGNOSIS",
                Groups:
                [
                    new NeonDiffGroup("匹配到的日志特征", matchedLines)
                ],
                ConfirmLabel: "",
                CancelLabel: "关闭",
                FootNote: "诊断结果仅作参考；涉及游戏改动的操作仍在对应工坊页或主工作台确认。",
                PlainSummary: summary,
                ExpandTechnicalDetailsByDefault: true));
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("无法读取日志文件：" + ex.Message, NeonTone.Cinnabar);
        }
    }

    void NeonOpenManagerUpdateModal()
    {
        if (neonWorkbenchView == null) return;
        if (managerRelease == null)
        {
            neonWorkbenchView.ShowToast("尚未检查到可用的管理器更新信息，请先点击检查更新。", NeonTone.Amber);
            return;
        }
        var rel = managerRelease;
        bool isNewer = ManagerUpdates.IsNewer(rel);
        bool canInstall = CanUpdateManager && CanInstallManagerRelease(rel);
        string sizeText = rel.Size > 0 ? $"{rel.Size / 1048576.0:0.0} MB" : "未知大小";

        var altActions = new StackPanel();
        var altLabel = NeonWidgets.Text("其他获取方式（仅在主通道下载缓慢或受限时使用）", 12, NeonPalette.InkSecondary, FontWeights.SemiBold);
        altLabel.Margin = new Thickness(0, 0, 0, 6);
        altActions.Children.Add(altLabel);
        var btnRow = new WrapPanel();

        if (canInstall)
        {
            var altBtn = NeonWidgets.CreateStyledButton(neonWorkbenchView, "备用官方通道更新", "NeonSecondaryButton", () =>
            {
                neonWorkbenchView.CloseTransactionModal();
                _ = DownloadAndApplyManagerUpdateCoreAsync(preferAssetApi: true, skipConfirm: true);
            }, "sm");
            altBtn.Margin = new Thickness(0, 0, 6, 4);
            btnRow.Children.Add(altBtn);
        }
        if (CanUpdateManager)
        {
            var zipBtn = NeonWidgets.CreateStyledButton(neonWorkbenchView, "仅下载更新 ZIP", "NeonGhostButton", () =>
            {
                neonWorkbenchView.CloseTransactionModal();
                DownloadManagerUpdate();
            }, "sm");
            zipBtn.Margin = new Thickness(0, 0, 6, 4);
            btnRow.Children.Add(zipBtn);
        }
        var relBtn = NeonWidgets.CreateStyledButton(neonWorkbenchView, "打开发布页", "NeonGhostButton", () => OpenUrl(rel.ReleaseUrl), "sm");
        relBtn.Margin = new Thickness(0, 0, 0, 4);
        btnRow.Children.Add(relBtn);
        altActions.Children.Add(btnRow);

        string notesText = string.IsNullOrWhiteSpace(rel.Notes)
            ? "未附带详细发布说明。"
            : rel.Notes.Trim();

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: isNewer ? $"管理器可更新至 v{rel.Version}" : $"当前发布版本 · v{rel.Version}",
            Subtitle: $"当前运行 v{ManagerUpdates.CurrentReleaseVersion} · 频道：{(managerUpdateChannel == ManagerUpdateChannel.Beta ? "测试版 Beta" : "稳定版")} · {sizeText}",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: "自动更新前会校验压缩包 SHA-256 摘要，并保留旧版备用直到新程序启动成功。",
            BoundaryNote: "仅更新尖塔修补站管理器自身，不会修改任何《杀戮尖塔 2》游戏或 MOD 文件。",
            Token: $"MGR-UPDATE-v{rel.Version}",
            Groups:
            [
                new NeonDiffGroup("发行包与校验信息",
                [
                    new NeonDiffLine("add", "目标文件", rel.FileName, sizeText),
                    new NeonDiffLine("mod", "SHA-256", string.IsNullOrWhiteSpace(rel.Sha256) ? "以清单为准" : rel.Sha256, rel.ReleaseUrl)
                ])
            ],
            ConfirmLabel: canInstall && isNewer ? $"自动下载并更新至 v{rel.Version}" : "",
            CancelLabel: "关闭",
            FootNote: canInstall ? "确认后将下载并启动平滑替换；无需手动覆盖文件夹。" : "当前运行环境不支持原地替换，请改用仅下载 ZIP 或发布页。",
            OnConfirmAsync: canInstall && isNewer ? () => DownloadAndApplyManagerUpdateCoreAsync(preferAssetApi: false, skipConfirm: true) : null,
            CustomContent: altActions,
            PlainSummary: notesText));
    }

    void NeonOpenGitHubNetworkModal() => NeonOpenGitHubNetworkModalInternal(null, null, null);

    void NeonOpenGitHubNetworkModalInternal(string? errorText, string? draftMode, string? draftProxy)
    {
        if (neonWorkbenchView == null) return;
        GitHubNetworkSettings existing;
        try { existing = GitHubNetwork.Load(installer.DataRoot); }
        catch { existing = GitHubNetwork.Default; }

        string selectedMode = draftMode ?? (existing.Mode == "local" ? "local" : "system");
        string initialProxy = draftProxy ?? (existing.Mode == "local" && !string.IsNullOrWhiteSpace(existing.ProxyUrl)
            ? existing.ProxyUrl
            : "http://127.0.0.1:7890");

        var stack = new StackPanel();
        if (!string.IsNullOrWhiteSpace(errorText))
        {
            var errAlert = NeonWidgets.CreateAlert(NeonTone.Cinnabar, NeonWidgets.Text("保存失败：" + errorText, 12, NeonPalette.InkPrimary, wrap: true));
            errAlert.Margin = new Thickness(0, 0, 0, 10);
            stack.Children.Add(errAlert);
        }

        var modeTitle = NeonWidgets.Text("网络模式", 12, NeonPalette.InkPrimary, FontWeights.SemiBold);
        modeTitle.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(modeTitle);

        var (proxyHost, proxyBox, _) = NeonWidgets.CreateTextInput("http://127.0.0.1:7890", height: 34);
        proxyBox.Text = initialProxy;
        AutomationProperties.SetName(proxyBox, "本机 HTTP 代理地址");

        var modeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        Button? sysBtn = null;
        Button? localBtn = null;

        void ApplyModeVisual()
        {
            bool isLocal = selectedMode == "local";
            proxyBox.IsEnabled = isLocal;
            proxyHost.Opacity = isLocal ? 1.0 : 0.48;
            if (sysBtn != null)
                sysBtn.Style = (Style)neonWorkbenchView.FindResource(!isLocal ? "NeonPrimaryButton" : "NeonSecondaryButton");
            if (localBtn != null)
                localBtn.Style = (Style)neonWorkbenchView.FindResource(isLocal ? "NeonPrimaryButton" : "NeonSecondaryButton");
        }

        sysBtn = NeonWidgets.CreateStyledButton(neonWorkbenchView, "跟随 Windows 系统代理（默认）", "NeonPrimaryButton", () =>
        {
            selectedMode = "system";
            ApplyModeVisual();
        }, "sm");
        sysBtn.Margin = new Thickness(0, 0, 8, 4);

        localBtn = NeonWidgets.CreateStyledButton(neonWorkbenchView, "使用本机 HTTP 代理（例如 Clash / sing-box）", "NeonSecondaryButton", () =>
        {
            selectedMode = "local";
            ApplyModeVisual();
        }, "sm");
        localBtn.Margin = new Thickness(0, 0, 0, 4);

        modeRow.Children.Add(sysBtn);
        modeRow.Children.Add(localBtn);
        stack.Children.Add(modeRow);

        var proxyLabel = NeonWidgets.Text("本机代理地址（只在选择本机 HTTP 代理时生效，仅支持 localhost / 127.0.0.1 HTTP 代理）", 11.5, NeonPalette.InkSecondary, wrap: true);
        proxyLabel.Margin = new Thickness(0, 0, 0, 5);
        stack.Children.Add(proxyLabel);
        stack.Children.Add(proxyHost);
        ApplyModeVisual();

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: "GitHub 网络与代理设置",
            Subtitle: "配置本管理器访问 GitHub API、Release 与源站清单的网络通道",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: "配置仅保存在管理器数据目录，不会修改 Windows 系统代理设置，也不会读取或保存任何 GitHub Token。",
            Token: "GITHUB-NET",
            Groups: [],
            ConfirmLabel: "保存网络设置",
            CancelLabel: "取消",
            FootNote: "保存后建议重启管理器，确保所有后台下载与检查客户端切换到新设置。",
            OnConfirmAsync: () =>
            {
                try
                {
                    GitHubNetwork.Save(
                        new GitHubNetworkSettings(1, selectedMode, selectedMode == "local" ? proxyBox.Text : ""),
                        installer.DataRoot);
                    neonCachedGitHubNetwork = new NeonGitHubNetworkState(selectedMode, selectedMode == "local" ? proxyBox.Text.Trim() : "");
                    RefreshNeonWorkbench(preserveViewport: true);
                    RecordNeonActivity("source", selectedMode == "local"
                        ? $"保存 GitHub 网络设置：本机 HTTP 代理（{proxyBox.Text.Trim()}）。"
                        : "保存 GitHub 网络设置：跟随 Windows 系统代理。");
                    neonWorkbenchView.ShowToast("GitHub 网络设置已保存。请重启管理器使所有连接使用新设置。");
                }
                catch (Exception ex)
                {
                    NeonOpenGitHubNetworkModalInternal(ex.Message, selectedMode, proxyBox.Text);
                }
                return Task.CompletedTask;
            },
            CustomContent: stack,
            PlainSummary: "如果你通过本机代理软件访问 GitHub，可切换到本机 HTTP 代理模式并填写端口（常见如 http://127.0.0.1:7890）；否则建议保持跟随 Windows 系统代理。"));
    }

    async Task NeonDiagnoseGitHubNetworkAsync()
    {
        if (neonWorkbenchView == null || busy || managerUpdateBusy || transactionWriting) return;
        busy = true;
        RefreshNeonWorkbench(preserveViewport: true);
        neonWorkbenchView.ShowToast("正在诊断 GitHub API、Raw 文件与 Release 页面连接…", NeonTone.Neutral);
        try
        {
            var entries = await GitHubNetwork.DiagnoseAsync(root: installer.DataRoot);
            var cfg = GitHubNetwork.Load(installer.DataRoot);
            string modeName = cfg.Mode == "local" ? $"本机 HTTP 代理（{cfg.ProxyUrl}）" : "跟随 Windows 系统网络";
            bool allOk = entries.All(e => e.Success);

            var lines = entries.Select(e => new NeonDiffLine(
                e.Success ? "add" : "del",
                e.Success ? "连通" : "异常",
                $"{e.Name} ({e.Host})",
                $"{e.Status} · {e.ElapsedMilliseconds} ms")).ToArray();

            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: allOk ? "GitHub 连接诊断正常" : "GitHub 部分通道连接受阻",
                Subtitle: $"当前网络模式：{modeName}",
                GameRunning: false,
                BlockedReason: allOk ? null : "检测到至少一个 GitHub 端点未能顺利连通；可尝试配置本机 HTTP 代理或在更新时改用备用通道。",
                AutoSnapshotNote: null,
                BoundaryNote: "此探测仅测试 GitHub API、Raw 与发布页基础握手延迟，不代表大体积发布附件的实际下载带宽。",
                Token: "GITHUB-DIAG",
                Groups:
                [
                    new NeonDiffGroup("各端点探测明细", lines)
                ],
                ConfirmLabel: "",
                CancelLabel: "关闭",
                FootNote: "可在工具箱 → GitHub 网络与代理中切换代理模式后重新诊断。",
                PlainSummary: allOk
                    ? $"已验证 {entries.Length} 个 GitHub 关键端点全部可达（当前模式：{modeName}）。"
                    : $"共有 {entries.Count(e => !e.Success)} / {entries.Length} 个端点存在超时或连接失败；建议检查本机代理端口配置。",
                ExpandTechnicalDetailsByDefault: true));
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("GitHub 网络诊断未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async void NeonPreviewRestoreModBackup(ManagedMod mod)
    {
        if (neonWorkbenchView == null || busy || transactionWriting || modInventoryDirty || selectedInstallRoot == null) return;
        string root = selectedInstallRoot;
        bool running = await CheckGameRunningOffUiAsync(root);
        try
        {
            var backups = await Task.Run(() => GitHubModInstaller.Backups(root, mod));
            if (backups.Length == 0)
            {
                neonWorkbenchView.ShowToast($"没有找到「{mod.Name}」的可用历史备份。", NeonTone.Amber);
                return;
            }
            string latest = backups[0];
            string relMod = Path.GetRelativePath(root, mod.Folder);

            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"恢复 MOD 上一次备份 · {mod.Name}",
                Subtitle: $"{mod.Id} · 共发现 {backups.Length} 份本地备份",
                GameRunning: running,
                BlockedReason: running ? "检测到《杀戮尖塔 2》正在运行，请先退出游戏再恢复 MOD 备份。" : null,
                AutoSnapshotNote: "恢复前会自动把当前版本另存为一份新备份，原有文件不会直接删除。",
                BoundaryNote: "恢复后会清理该 MOD 的自动版本基线与来源缓存，下次检查更新会重新核对本地文件指纹。",
                Token: $"MOD-RESTORE-{mod.Id}",
                Groups:
                [
                    new NeonDiffGroup("恢复来源与受影响目录",
                    [
                        new NeonDiffLine("add", "恢复备份", Path.GetFileName(latest), latest),
                        new NeonDiffLine("mod", "目标目录", relMod, "当前目录内容将先备份再替换")
                    ])
                ],
                ConfirmLabel: running ? "" : "确认恢复上一备份",
                CancelLabel: "取消",
                FootNote: "确认前不会改动任何 MOD 文件。",
                OnConfirmAsync: () => NeonConfirmRestoreModBackupAsync(root, mod, latest),
                PlainSummary: $"将把「{mod.Name}」还原为最近一次更新前保留的备份；当前版本也会自动另存备份，可随时再切回。"));
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("读取 MOD 备份列表失败：" + ex.Message, NeonTone.Cinnabar);
        }
    }

    async Task NeonConfirmRestoreModBackupAsync(string root, ManagedMod mod, string backupPath)
    {
        if (neonWorkbenchView == null || busy || transactionWriting || !NeonCanWrite(root)) return;
        busy = true;
        transactionWriting = true;
        RefreshNeonWorkbench(preserveViewport: true);
        try
        {
            string previous = await Task.Run(() => GitHubModInstaller.Restore(root, mod, backupPath));
            string baselineNote = "";
            try
            {
                WorkshopSources().ClearBaseline(mod.Id);
                WorkshopUpdates().Invalidate(mod.Id);
                modSourceCheckWorkflow?.Invalidate(mod);
            }
            catch (Exception baselineError)
            {
                baselineNote = $"（版本基线清理未完成：{baselineError.Message}；下次检查会重新核验本地文件）";
            }

            installPreview = null;
            lastInstallResult = null;
            ClearSelection();
            ClearCoop();
            report = null;
            states = [];
            demo = false;
            await NeonScanAndPublishAsync(root);
            InvalidateLifecycle($"已恢复「{mod.Name}」的上一备份，旧的补丁健康结论已失效，请重新扫描核验。");
            RecordNeonActivity("install", $"恢复「{mod.Name}」至备份「{Path.GetFileName(backupPath)}」（旧版本已保留在：{Path.GetFileName(previous)}）。");
            neonWorkbenchView.ShowToast($"已恢复「{mod.Name}」备份{baselineNote}");
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("恢复 MOD 备份未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }
}
