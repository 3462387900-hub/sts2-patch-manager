using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpirePatch;

public partial class MainWindow
{
    readonly List<NeonActivityEntry> neonActivityLog = [];
    LocalZipUiWorkflowSession? neonFixtureLocalZipSession;
    string? neonFixtureLocalZipRoot;
    string? neonFixtureDataRoot;
    string? neonCoopPeerLabel;
    string neonLastUpdateCheckTimeLabel = "尚未检查";

    string NeonDataRoot => neonFixtureDataRoot ?? installer.DataRoot;

    internal LocalModZipPreview? NeonCurrentLocalZipPreview =>
        (neonFixtureDataRoot != null ? neonFixtureLocalZipSession : localZipSession)?.CurrentPreview;

    LocalZipUiWorkflowSession NeonLocalZipSession()
    {
        if (neonFixtureDataRoot == null)
            return LocalZipSession();
        if (selectedInstallRoot == null)
            throw new InvalidOperationException("请先选择游戏目录。");
        if (neonFixtureLocalZipSession != null &&
            !string.Equals(neonFixtureLocalZipRoot, selectedInstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            neonFixtureLocalZipSession.ChangeRoot(selectedInstallRoot);
            neonFixtureLocalZipSession = null;
            neonFixtureLocalZipRoot = null;
        }
        if (neonFixtureLocalZipSession == null)
        {
            var cacheProbe = new ModSourceWorkflowLocalZipCacheProbe(ModSourceChecks);
            var archiveBackend = new LocalModZipArchiveBackend(cacheProbe.Inspect);
            var previewWorkflow = new LocalModZipImportWorkflowService(
                NeonDataRoot, archiveBackend, gameRunning: GameEnvironment.Running);
            neonFixtureLocalZipSession = new LocalZipUiWorkflowSession(previewWorkflow, NeonDataRoot);
            neonFixtureLocalZipRoot = selectedInstallRoot;
        }
        return neonFixtureLocalZipSession;
    }

    ModEnvironmentSnapshotWorkflowService NeonSnapshotWorkflow(string root) =>
        new(root, NeonDataRoot, invalidateCaches: ids =>
        {
            try
            {
                var sources = WorkshopSources();
                var updates = WorkshopUpdates();
                foreach (string id in ids)
                {
                    sources.ClearBaseline(id);
                    updates.Invalidate(id);
                }
                modSourceCheckWorkflow?.InvalidateAll();
                return null;
            }
            catch (Exception error) { return error.Message; }
        });

    ModUpdateApplyWorkflowService NeonModUpdateApplyService() =>
        new(
            backupRoot: Path.Combine(NeonDataRoot, "MOD更新事务"),
            isGameRunning: GameEnvironment.Running,
            deferWatcherQuietPeriod: () => modFileWatcher.DeferQuietPeriod());

    internal void RecordNeonActivity(string kind, string text)
    {
        neonActivityLog.Insert(0, new NeonActivityEntry(DateTime.Now.ToString("HH:mm:ss"), kind, text));
        if (neonActivityLog.Count > 80) neonActivityLog.RemoveAt(neonActivityLog.Count - 1);
        if (kind is "snapshot" or "patch" or "restore" or "install" or "coop")
            InvalidateNeonRecoveryCache();
    }

    void InvalidateNeonRecoveryCache()
    {
        neonRecoveryCacheDirty = true;
        neonRecoveryRevision++;
    }

    (ModEnvironmentSnapshotEntry[] Snapshots, string? Warning, NeonPatchBackupBatch? Batch) LoadRecoveryCacheSync(string? root, string dataRoot) =>
        WithNeonRecoveryIo(() => ReadRecoveryCache(root, dataRoot));

    static (ModEnvironmentSnapshotEntry[] Snapshots, string? Warning, NeonPatchBackupBatch? Batch) ReadRecoveryCache(string? root, string dataRoot)
    {
        if (root == null) return ([], null, null);
        ModEnvironmentSnapshotEntry[] entries = [];
        string? warning = null;
        try { entries = new ModEnvironmentSnapshotWorkflowService(root, dataRoot).List(); }
        catch (Exception ex) { warning = ex.Message; }
        var batch = NeonWorkbenchBuilder.ReadLatestPatchBackupBatch(root);
        return (entries, warning, batch);
    }

    NeonCoopSnapshotState BuildNeonCoopSnapshotState()
    {
        string? localTime = null;
        if (coopLocal is { } loc && loc.TryGetProperty("time", out var lt))
        {
            try { localTime = lt.GetDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
        }
        else if (report != null)
        {
            localTime = report.Time.ToString("yyyy-MM-dd HH:mm:ss");
        }

        string? peerTime = null;
        string? peerMgrVer = null;
        bool peerLegacy = false;
        if (coopPeer is { } peer)
        {
            if (peer.TryGetProperty("time", out var pt))
            {
                try { peerTime = pt.GetDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
            }
            if (peer.TryGetProperty("managerVersion", out var mv) && mv.ValueKind == JsonValueKind.String)
                peerMgrVer = "管理器 v" + mv.GetString();
            else if (peer.TryGetProperty("schema", out var sc) && sc.ValueKind == JsonValueKind.Number)
                peerMgrVer = $"清单格式 schema {sc.GetInt32()}" + (CoopPlanner.PresenceReport(peer) ? " · 含存在证据" : " · 旧版格式");
            peerLegacy = !CoopPlanner.PresenceReport(peer);
        }

        return new NeonCoopSnapshotState(
            HasPeer: coopPeer.HasValue,
            LocalScanTimeLabel: localTime,
            PeerLabel: neonCoopPeerLabel,
            PeerTimeLabel: peerTime,
            PeerManagerVersion: peerMgrVer,
            PeerIsLegacy: peerLegacy,
            ComparisonSummary: coopComparison,
            Rows: coopRows?.ToArray() ?? [],
            LastOutcome: coopOutcome,
            StatusText: coopStatus);
    }

    void WireNeonSecondaryPageEvents(NeonWorkbenchView view)
    {
        // Patches page
        view.RequestRefreshOnlinePatches += () => _ = NeonRefreshOnlinePatchesAsync();
        view.RequestPreviewOrganizePatches += () => _ = NeonPreviewOrganizePatchesAsync();
        view.RequestPreviewPatchInstall += ids => _ = NeonPreviewPatchInstallAsync(ids);
        view.RequestToggleOrphanAutoStop += enabled =>
        {
            try
            {
                OrphanPatchAutoStop.SetEnabled(NeonDataRoot, enabled);
                neonCachedOrphanAutoStop = enabled;
                RecordNeonActivity("patch", enabled ? "开启了目标 MOD 删除后自动安全停用孤立补丁。" : "关闭了孤立补丁自动停用。");
                view.ShowToast(enabled ? "已开启：目标 MOD 全部删除后自动安全停用其孤立补丁" : "已关闭孤立补丁自动停用");
                RefreshNeonWorkbench(preserveViewport: true);
            }
            catch (Exception ex)
            {
                view.ShowToast("保存自动停用设置失败：" + ex.Message, NeonTone.Cinnabar);
            }
        };
        view.RequestAcknowledgeModReview += patch => NeonOpenAcknowledgeModReviewModal(patch);

        // Acquire & Updates page
        view.RequestCheckAllModUpdates += () => _ = NeonCheckAllModUpdatesAsync();
        view.RequestCancelUpdateCheck += () =>
        {
            modUpdateCheckCancellation?.Cancel();
            view.ShowToast("已请求取消本次后台更新检查", NeonTone.Amber);
        };
        view.RequestPreviewWorkshopInstall += wsId => _ = NeonPreviewWorkshopInstallAsync(wsId);
        view.RequestChooseLocalZipImport += NeonChooseLocalZipImport;
        view.RequestPreviewLocalZipPath += path => _ = NeonPreviewLocalZipImportAsync(path);
        view.RequestDiscoverGitHubCandidates += () => _ = NeonDiscoverGitHubCandidatesAsync();

        // Co-op page
        view.RequestExportCoopManifest += () => _ = NeonExportCoopManifestAsync();
        view.RequestImportCoopPeerFile += () => _ = NeonImportCoopPeerFileAsync();
        view.RequestRefreshCoopComparison += () => _ = NeonRefreshCoopComparisonAsync();
        view.RequestPreviewCoopApply += ids => _ = NeonPreviewCoopApplyAsync(ids);
        view.RequestOpenPath += path =>
        {
            if (File.Exists(path) || Directory.Exists(path)) OpenUrl(path);
        };

        // Recovery page
        view.RequestCreateEnvironmentSnapshot += purpose => _ = NeonCreateEnvironmentSnapshotAsync(purpose);
        view.RequestPreviewEnvironmentRestore += id => _ = NeonPreviewEnvironmentRestoreAsync(id);
        view.RequestDeleteEnvironmentSnapshot += id => _ = NeonDeleteEnvironmentSnapshotAsync(id);
        view.RequestPreviewPatchBatchRollback += () => _ = NeonPreviewPatchBatchRollbackAsync();
    }

    // ========================================================================
    // PATCHES PAGE PRODUCTION ACTIONS
    // ========================================================================

    async Task NeonRefreshOnlinePatchesAsync()
    {
        if (busy || onlineBusy || neonWorkbenchView == null) return;
        onlineBusy = true;
        sourceCurrent = false;
        onlineStatus = "正在连接公开补丁仓库并刷新索引…";
        RefreshNeonWorkbench(preserveViewport: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        onlineCancellation = cts;
        try
        {
            onlineCatalog = await onlineSource.LoadAsync(cts.Token);
            sourceCurrent = true;
            onlineStatus = $"已连接 · {onlineCatalog.Patches.Count} 项补丁 · 无需登录";
            RecordNeonActivity("patch", $"刷新在线补丁目录成功（共 {onlineCatalog.Patches.Count} 项）。");
            neonWorkbenchView.ShowToast($"已刷新在线补丁源（{onlineCatalog.Patches.Count} 项），正在复核本地指纹…");
        }
        catch (Exception ex)
        {
            onlineStatus = "在线补丁源刷新未完成：" + ex.Message;
            neonWorkbenchView.ShowToast(onlineStatus, NeonTone.Amber);
        }
        finally
        {
            onlineBusy = false;
            if (ReferenceEquals(onlineCancellation, cts)) onlineCancellation = null;
            RefreshNeonWorkbench(preserveViewport: true);
        }

        if (selectedInstallRoot != null && !busy && !transactionWriting)
            await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
    }

    async Task NeonPreviewOrganizePatchesAsync()
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        string root = selectedInstallRoot;
        bool running = await CheckGameRunningOffUiAsync(root);
        bool isFixture = neonFixtureRoot != null &&
            root.Equals(neonFixtureRoot, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(root, ".spirepatch-fixture")) &&
            new FileInfo(Path.Combine(root, "SlayTheSpire2.exe")).Length == 0;

        busy = true;
        PatchOrganizationPlan? plan = null;
        string? error = null;
        try
        {
            plan = await Task.Run(() => isFixture ? PatchOrganizer.PreviewFixture(root) : PatchOrganizer.Preview(root));
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }

        if (error != null || plan == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "无法预览整理散落独立补丁",
                Subtitle: "PatchOrganizer.Preview 预检未通过",
                GameRunning: running,
                BlockedReason: error ?? "未能生成整理方案。",
                AutoSnapshotNote: null,
                BoundaryNote: "仅移动本版已审核的独立补丁目录至 mods/_SpirePatch_Fixes，普通 MOD 与原 MOD 内置修复保持原位。",
                Token: "ORG-BLOCKED",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        var groups = new List<NeonDiffGroup>();
        if (plan.Moves.Length > 0)
        {
            groups.Add(new NeonDiffGroup($"将集中移入 mods/{PatchOrganizer.CollectionName}（{plan.Moves.Length} 项）",
                plan.Moves.Select(m => new NeonDiffLine(
                    "mod",
                    "移动",
                    $"{Path.GetRelativePath(root, m.Source)} → {Path.GetRelativePath(root, m.Destination)}",
                    $"{m.Files.Count} 文件")).ToArray()));
        }
        if (plan.Journals is { Length: > 0 })
        {
            groups.Add(new NeonDiffGroup($"同步迁移历史回滚记录（{plan.Journals.Length} 条）",
                plan.Journals.Select(j => new NeonDiffLine("mod", "迁移", Path.GetRelativePath(root, j.Journal), "先备份原记录再更新路径")).ToArray()));
        }
        if (plan.Skipped.Length > 0)
        {
            groups.Add(new NeonDiffGroup($"按安全规则跳过（{plan.Skipped.Length} 项）",
                plan.Skipped.Select(s => new NeonDiffLine("skip", "跳过", s, "")).ToArray()));
        }

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"整理散落独立修复补丁（{plan.Moves.Length} 项）",
            Subtitle: $"将已审核的独立修复补丁集中到 mods\\{PatchOrganizer.CollectionName}",
            GameRunning: running,
            BlockedReason: plan.Moves.Length == 0 ? "当前没有可安全移动的已审核独立补丁。" : null,
            AutoSnapshotNote: null,
            BoundaryNote: "普通 MOD 与原 MOD 内置修复保持原位；若涉及历史回滚记录，将先备份原始记录再事务迁移路径。",
            Token: plan.Token,
            Groups: groups,
            ConfirmLabel: plan.Moves.Length > 0 ? $"确认整理这 {plan.Moves.Length} 项" : "",
            OnConfirmAsync: plan.Moves.Length > 0 ? () => NeonConfirmOrganizePatchesAsync(plan) : null,
            PlainSummary: $"将把 {plan.Moves.Length} 项散落在 mods 根目录的独立补丁整理进 mods\\{PatchOrganizer.CollectionName}，普通 MOD 与内置修复保持原位不动。"));
    }

    async Task NeonConfirmOrganizePatchesAsync(PatchOrganizationPlan plan)
    {
        if (busy || transactionWriting || !NeonCanWrite(plan.Root)) return;
        busy = true;
        transactionWriting = true;
        try
        {
            var result = await Task.Run(() => PatchOrganizer.Apply(plan, NeonDataRoot));
            installPreview = null; lastInstallResult = null; ClearSelection(); ClearCoop();
            report = null; states = []; demo = false;
            await NeonScanAndPublishAsync(plan.Root);
            InvalidateLifecycle($"已整理 {result.Moved} 项独立补丁至 mods/{PatchOrganizer.CollectionName}，请重新核验补丁健康。");
            RecordNeonActivity("patch", $"整理了 {result.Moved} 项散落独立补丁至 mods/{PatchOrganizer.CollectionName}。");
            neonWorkbenchView?.ShowToast($"整理完成：已移动 {result.Moved} 项独立补丁至专用目录");
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("整理未成功：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonPreviewPatchInstallAsync(string[] requestedIds)
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        if (requestedIds.Length == 0) return;

        string root = selectedInstallRoot;
        bool running = await CheckGameRunningOffUiAsync(root);
        if (running)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "无法预览补丁安装方案",
                Subtitle: "检测到游戏正在运行",
                GameRunning: true,
                BlockedReason: "请先完全退出《杀戮尖塔 2》，再检查与安装修复补丁。",
                AutoSnapshotNote: null,
                BoundaryNote: null,
                Token: "GAME-RUNNING",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "知道了"));
            return;
        }

        busy = true;
        PatchSelection? selection = null;
        InstallResult? preview = null;
        string? error = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        preparationCancellation = cts;
        neonWorkbenchView.ShowToast("正在解析联动补丁并核对本地文件指纹…");
        try
        {
            var currentScan = report != null && report.InventoryComplete &&
                report.Directory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                report.CatalogRevision == Catalog.Revision
                ? report
                : await Task.Run(() => Catalog.Scan(root), cts.Token);
            selection = await Task.Run(() => SelectionPlanner.Resolve(root, requestedIds, currentScan), cts.Token);
            var pkg = await GetPreparedPackageAsync(cts.Token, msg => Dispatcher.BeginInvoke(() => neonWorkbenchView?.ShowToast(msg)));
            preview = await installer.RunAsync(pkg, root, "Preview", selectedIds: selection.Requested);
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            busy = false;
            if (ReferenceEquals(preparationCancellation, cts)) preparationCancellation = null;
        }

        if (error != null || preview == null || selection == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "补丁安装方案预检未通过",
                Subtitle: $"已勾选 {requestedIds.Length} 项补丁",
                GameRunning: false,
                BlockedReason: error ?? "未能生成安装预览。",
                AutoSnapshotNote: null,
                BoundaryNote: "准备和预检阶段均为只读，未修改任何游戏或 MOD 文件。",
                Token: "PATCH-PREFLIGHT-FAIL",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        installPreview = preview;
        var planView = new PlanPresentation(preview);
        var groups = new List<NeonDiffGroup>();
        if (planView.Runtime.Length > 0)
            groups.Add(new NeonDiffGroup($"修复文件改动（{planView.Runtime.Length} 个文件）",
                planView.Runtime.Take(20).Select(f => new NeonDiffLine("add", PlanPresentation.Action(f), f.Relative, "")).ToArray()));
        if (planView.Legacy.Length > 0)
            groups.Add(new NeonDiffGroup($"旧补丁清理（{planView.Legacy.Length} 个文件）",
                planView.Legacy.Take(12).Select(f => new NeonDiffLine("del", "清理", f.Relative, "")).ToArray()));
        if (planView.Documents.Length > 0)
            groups.Add(new NeonDiffGroup($"说明与验证记录（{planView.Documents.Length} 个文件）",
                planView.Documents.Take(12).Select(f => new NeonDiffLine("mod", PlanPresentation.Action(f), f.Relative, "")).ToArray()));
        if (planView.Unchanged.Length > 0)
            groups.Add(new NeonDiffGroup($"已匹配并跳过（{planView.Unchanged.Length} 个文件）",
                [new NeonDiffLine("skip", "跳过", $"共 {planView.Unchanged.Length} 个文件哈希已一致", "无需重写")]));

        string includedNames = string.Join("、", (preview.SelectedIds ?? selection.Included)
            .Select(id => Catalog.All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id));

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: planView.Title,
            Subtitle: $"本次范围：{includedNames}",
            GameRunning: false,
            BlockedReason: preview.Changed == 0 ? "所选补丁的功能与说明文件均已匹配，无需写入。" : null,
            AutoSnapshotNote: "安装器将在写入前自动保存原始文件备份至 mod_fix_backups/，支持按最近批次完整回滚。",
            BoundaryNote: selection.Automatic.Length > 0
                ? $"已自动联动带入 {selection.Automatic.Length} 项必需关联补丁（{string.Join("、", selection.Automatic)}），避免版本错配。"
                : planView.Detail,
            Token: preview.Token,
            Groups: groups,
            ConfirmLabel: preview.Changed > 0 ? planView.Confirm : "",
            OnConfirmAsync: preview.Changed > 0 ? () => NeonConfirmPatchTransactionAsync(preview) : null));
    }

    async Task NeonConfirmPatchTransactionAsync(InstallResult preview)
    {
        if (busy || transactionWriting || preparedPackage == null || !NeonCanWrite(preview.Root)) return;
        bool undo = preview.Mode == "RollbackPreview";
        busy = true;
        transactionWriting = true;
        installPreview = null;
        try
        {
            var result = await installer.RunAsync(
                preparedPackage,
                preview.Root,
                undo ? "Rollback" : "Install",
                preview.Token,
                !undo && preview.SelectedIds?.Length > 0 ? preview.SelectedIds : null);
            lastInstallResult = result;
            ClearCoop();
            ClearSelection();
            var stamp = NeonCaptureScanStamp(preview.Root);
            var definitions = sourceCurrent && onlineCatalog != null ? onlineCatalog.Patches : Catalog.Bundled;
            string? revision = sourceCurrent ? onlineCatalog?.Commit : null;
            var scanned = await Task.Run(() => (Inventory: ModManager.Scan(preview.Root),
                Report: Catalog.Scan(preview.Root, definitions, revision)));
            NeonTryPublishInventory(stamp, scanned.Inventory, () =>
            {
                report = scanned.Report;
                scannedCatalog = definitions;
                states = scanned.Report.Patches;
                demo = false;
            });
            InvalidateLifecycle(undo ? "已完成补丁批次回滚，请重新核验补丁健康状态。" : "补丁安装完成，旧健康基线已刷新，请重新核验。");
            RecordNeonActivity(undo ? "restore" : "patch",
                undo ? $"完成了最近一次补丁批次回滚（共还原 {result.Changed} 个文件）。"
                     : $"安装并校验修复补丁完成（变更 {result.Changed} 个文件，备份：{result.Backup}）。");
            neonWorkbenchView?.ShowToast(undo ? "已完成最近一次补丁批次回滚" : $"补丁安装并校验完成（变更 {result.Changed} 个文件）");
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast((undo ? "回滚未完成：" : "安装未完成：") + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    void NeonOpenAcknowledgeModReviewModal(NeonRelatedPatch patch)
    {
        if (selectedInstallRoot == null || neonWorkbenchView == null || patch.PendingModChange == null) return;
        var ch = patch.PendingModChange;
        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"标记已核对 · {patch.Name}",
            Subtitle: "仅清除管理器内部的文件变更提醒",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: "此操作只清除内部待核对记录，不改动任何游戏文件，也不作为“上游已修复 Bug”或安全退役的证明。",
            Token: "ACK-" + ch.ModId,
            Groups:
            [
                new NeonDiffGroup("本地核对信息",
                [
                    new NeonDiffLine("mod", "关联 MOD", $"{ch.ModId} ({ch.OldVersion} → {ch.NewVersion})", $"变更 {ch.ChangedFiles} 个文件 · 将核验与上次扫描一致")
                ])
            ],
            ConfirmLabel: "确认标记已核对",
            OnConfirmAsync: () => NeonConfirmAcknowledgeModReviewAsync(ch.ModId, patch.Name)));
    }

    async Task NeonConfirmAcknowledgeModReviewAsync(string modId, string patchName)
    {
        if (selectedInstallRoot == null || busy || transactionWriting) return;
        string root = selectedInstallRoot;
        busy = true;
        try
        {
            await Task.Run(() => ModHistory.AcknowledgeReview(root, NeonDataRoot, modId));
            RecordNeonActivity("patch", $"人工标记「{patchName}」的关联 MOD（{modId}）更新为已核对（未改动游戏文件）。");
            neonWorkbenchView?.ShowToast($"已清除「{patchName}」的待核对提醒");
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("未能标记已核对：" + ex.Message, NeonTone.Cinnabar);
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
            return;
        }
        busy = false;
        await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
    }

    // ========================================================================
    // ACQUIRE & UPDATES PRODUCTION ACTIONS (Workshop + Local ZIP)
    // ========================================================================

    async Task NeonCheckAllModUpdatesAsync()
    {
        if (selectedInstallRoot == null || modInventory == null || modInventoryDirty)
        {
            neonWorkbenchView?.ShowToast("MOD 清单尚未稳定，请刷新后再检查更新。", NeonTone.Amber);
            return;
        }
        if (busy || transactionWriting || modUpdateCheckRunning) return;

        var inventory = modInventory;
        string gameRoot = selectedInstallRoot;
        var sourceChecks = ModSourceChecks();
        using var cancellation = new CancellationTokenSource();
        modUpdateCheckCancellation = cancellation;
        modUpdateCheckRunning = true;
        neonLastUpdateCheckTimeLabel = DateTime.Now.ToString("HH:mm:ss");
        RecordNeonActivity("update", "启动了后台只读来源更新检查（GitHub + Steam 创意工坊）。");
        neonWorkbenchView?.ShowToast("正在后台只读检查 GitHub 与创意工坊更新（不阻塞页面切换）…");
        RefreshNeonWorkbench(preserveViewport: true);

        try
        {
            var result = await sourceChecks.CheckAllAsync(
                inventory.Mods,
                force: false,
                isCurrent: () => IsCurrentModSourceInventory(gameRoot, inventory),
                cancellationToken: cancellation.Token);
            if (cancellation.IsCancellationRequested || result.Cancelled)
            {
                modStatus = "已取消本次更新检查。";
                neonWorkbenchView?.ShowToast(modStatus, NeonTone.Amber);
                return;
            }
            if (result.IsStale || gameRoot != selectedInstallRoot || !ReferenceEquals(inventory, modInventory))
                return;

            modStatus =
                $"检查完成：GitHub {result.GitHubChecked} 项（有更新 {result.GitHubUpdates}）；" +
                $"创意工坊 {result.WorkshopChecked} 项（有更新 {result.WorkshopUpdates}，待核验 {result.WorkshopNeedsReview}）。" +
                "仅检测来源内容，不会自动下载或修改游戏文件。";
            RecordNeonActivity("update", modStatus);
            neonWorkbenchView?.ShowToast(modStatus);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (gameRoot == selectedInstallRoot)
            {
                modStatus = "已取消本次更新检查。";
                neonWorkbenchView?.ShowToast(modStatus, NeonTone.Amber);
            }
        }
        catch (Exception ex)
        {
            if (gameRoot == selectedInstallRoot)
            {
                modStatus = "MOD 更新检查未完成：" + ex.Message;
                neonWorkbenchView?.ShowToast(modStatus, NeonTone.Cinnabar);
            }
        }
        finally
        {
            if (ReferenceEquals(modUpdateCheckCancellation, cancellation))
                modUpdateCheckCancellation = null;
            modUpdateCheckRunning = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonDiscoverGitHubCandidatesAsync()
    {
        if (busy || transactionWriting || modInventory == null || selectedInstallRoot == null) return;
        if (modInventoryDirty)
        {
            neonWorkbenchView?.ShowToast("MOD 清单尚未稳定，请刷新后再查找来源。", NeonTone.Amber);
            return;
        }
        var inventory = modInventory;
        string gameRoot = selectedInstallRoot;
        var sourceChecks = ModSourceChecks();
        var mods = inventory.Mods
            .Where(mod => !ModPresentation.IsIndependentPatch(mod))
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(mod =>
            {
                var state = sourceChecks.Inspect(mod).GitHub;
                return !state.IsBound && state.CacheState != ModSourceCacheState.Fresh;
            })
            .Take(8)
            .ToArray();

        if (mods.Length == 0)
        {
            neonWorkbenchView?.ShowToast("暂无待查找的普通 MOD；已关联来源或近期缓存结果均已保留。");
            return;
        }

        RecordNeonActivity("source", $"启动了未绑定 MOD 的 GitHub 候选来源发现（最多 {mods.Length} 项）。");
        neonWorkbenchView?.ShowToast($"正在后台查找 {mods.Length} 项未关联 MOD 的 GitHub 候选来源…");
        busy = true;
        int found = 0, verified = 0, examined = 0;
        try
        {
            foreach (var mod in mods)
            {
                var result = await sourceChecks.DiscoverGitHubAsync(
                    mod,
                    force: false,
                    isCurrent: () => IsCurrentModSourceScope(gameRoot, inventory, mod));
                if (result.Cancelled || result.IsStale) break;
                if (result.GitHub.Candidates.Count > 0) found++;
                if (result.GitHub.Candidates.Any(c =>
                    c.Trust is ModSourceTrustLevel.ManifestDeclared or ModSourceTrustLevel.RemoteManifestVerified))
                    verified++;
                examined++;
                if (result.GitHub.State == ModSourceCheckState.Failed)
                {
                    modStatus = $"已检查 {examined} 项，GitHub 搜索暂不可用：{result.GitHub.Message}（已查到候选可在详情确认）。";
                    neonWorkbenchView?.ShowToast(modStatus, NeonTone.Amber);
                    return;
                }
            }
            if (gameRoot != selectedInstallRoot || !ReferenceEquals(inventory, modInventory)) return;
            modStatus = $"来源查找完成：检查 {examined} 项，发现 {found} 项候选（{verified} 项有清单证据），可在详情中人工核对绑定。";
            RecordNeonActivity("source", modStatus);
            neonWorkbenchView?.ShowToast(modStatus);
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("来源查找未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonPreviewWorkshopInstallAsync(ulong workshopId)
    {
        if (busy || transactionWriting || neonPreviewPreparationCancellation != null || selectedInstallRoot == null || neonWorkbenchView == null) return;
        if (modInventoryDirty)
        {
            neonWorkbenchView.SetWorkshopFeedback("本地 MOD 清单尚未稳定，请刷新后再试。", NeonTone.Cinnabar);
            return;
        }
        string gameRoot = selectedInstallRoot;
        long epoch = Volatile.Read(ref neonRootEpoch);
        if (!NeonCanWrite(gameRoot)) return;
        bool running = await CheckGameRunningOffUiAsync(gameRoot);
        if (!NeonPreviewRootCurrent(gameRoot, epoch)) return;
        if (running)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"无法从创意工坊获取 · WS #{workshopId}",
                Subtitle: "检测到游戏正在运行",
                GameRunning: true,
                BlockedReason: "请先退出《杀戮尖塔 2》，再执行创意工坊下载与安装预览。",
                AutoSnapshotNote: null,
                BoundaryNote: null,
                Token: "GAME-RUNNING",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "知道了"));
            return;
        }

        if (busy || transactionWriting || modInventoryDirty || !NeonPreviewRootCurrent(gameRoot, epoch) || !NeonCanWrite(gameRoot)) return;
        string? boundId = WorkshopSources().ModIdFor(workshopId);
        if (boundId != null)
        {
            var matches = modInventory?.Mods.Where(m => m.Id.Equals(boundId, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (matches.Length > 1)
            {
                neonWorkbenchView.ShowToast("本地存在重复 MOD ID，无法确定更新目标。", NeonTone.Amber);
                return;
            }
            if (matches.Length == 1 && WorkshopUpdates().Cached(boundId) is { } cached &&
                cached.Status == "有更新" && cached.WorkshopId == workshopId && ulong.TryParse(cached.RemoteManifestId, out var manifest) && manifest != 0)
            {
                await NeonStageAndPreviewModUpdateAsync(gameRoot, matches[0],
                    NeonWorkshopUpdateRequest(workshopId, cached.RemoteManifestId), null);
                return;
            }
        }
        if (neonFixtureDataRoot != null && neonTestWorkshopPrepare == null)
        {
            neonWorkbenchView.ShowToast("隔离测试禁止使用真实 Workshop 下载。", NeonTone.Amber);
            return;
        }
        busy = true;
        WorkshopModPrepared? prepared = null;
        NeonPreviewSession? session = null;
        string? error = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        neonPreviewPreparationCancellation = cts;
        try
        {
            if (neonTestWorkshopPrepare != null && neonFixtureDataRoot != null)
                prepared = await neonTestWorkshopPrepare(gameRoot, workshopId, cts.Token);
            else
            {
                using var wsInstaller = new WorkshopModInstaller();
                prepared = await wsInstaller.PrepareAsync(gameRoot, workshopId, progress: msg =>
                    Dispatcher.BeginInvoke(() => neonWorkbenchView?.SetWorkshopFeedback(msg, NeonTone.Sage)), cancellationToken: cts.Token);
            }
            var owned = prepared;
            session = NeonCreatePreviewSession(() => NeonCleanupWorkshopPreview(owned));
            cts.Token.ThrowIfCancellationRequested();
            if (!NeonPreviewRootCurrent(gameRoot, epoch))
                throw new InvalidOperationException("游戏目录已切换，已取消本次安装。");
            string? boundModId = WorkshopSources().ModIdFor(workshopId);
            if (boundModId != null && !boundModId.Equals(prepared.ModId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"该 Workshop ID 已关联到另一个 MOD ID：{boundModId}；为避免来源错配，未继续安装。");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            session?.Dispose();
            prepared = null;
        }
        finally
        {
            if (ReferenceEquals(neonPreviewPreparationCancellation, cts)) neonPreviewPreparationCancellation = null;
            busy = false;
        }

        if (!NeonPreviewRootCurrent(gameRoot, epoch) || cts.IsCancellationRequested)
        {
            session?.Dispose();
            return;
        }

        if (error != null || prepared == null || session == null)
        {
            neonWorkbenchView.SetWorkshopFeedback("获取预览失败：" + (error ?? "未知错误"), NeonTone.Cinnabar);
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"无法完成创意工坊获取 · WS #{workshopId}",
                Subtitle: "尖塔补给站（Apricityx）UGC 预检未通过",
                GameRunning: false,
                BlockedReason: error ?? "无法读取或校验该创意工坊条目。",
                AutoSnapshotNote: null,
                BoundaryNote: "预检失败未对 mods 目录进行任何写入。",
                Token: "WS-FAIL",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        bool presented = false;
        try
        {
            // Preparation can discover an installed target without a verified cached manifest.
            // The old installer may identify it, but cannot perform the overwrite.
            if (prepared.IsUpdate)
            {
                var matches = modInventory?.Mods.Where(m => m.Id.Equals(prepared.ModId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Folder, prepared.ExistingFolder, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
                var request = NeonWorkshopUpdateRequest(workshopId, prepared.ManifestId);
                var priorBinding = WorkshopSources().Get(prepared.ModId);
                session.Dispose();
                if (matches.Length != 1 || modInventoryDirty || !NeonRootMatches(gameRoot) ||
                    (priorBinding.HasValue && priorBinding.Value != workshopId))
                {
                    neonWorkbenchView.ShowToast("目标 MOD 或已绑定来源发生变化，请先重新核验。", NeonTone.Amber);
                    return;
                }
                if (prepared.IsUnchanged)
                {
                    neonWorkbenchView.SetWorkshopFeedback("本地文件已与创意工坊一致，无需覆盖。", NeonTone.Sage);
                    return;
                }
                await NeonStageAndPreviewModUpdateAsync(gameRoot, matches[0], request, null);
                return;
            }
            if (!NeonPreviewRootCurrent(gameRoot, epoch) || modInventoryDirty) return;
            if (prepared.IsUnchanged)
            {
                neonWorkbenchView.SetWorkshopFeedback("本地文件已与创意工坊一致，无需覆盖。", NeonTone.Sage);
                return;
            }
            neonWorkbenchView.SetWorkshopFeedback($"已通过 Chunk 与文件 SHA-1 校验：{prepared.ModName} ({prepared.Version})", NeonTone.Sage);
            var ready = prepared;
            string relDest = Path.GetRelativePath(gameRoot, ready.DestinationDirectory);
            var lines = new List<NeonDiffLine>
            {
                new(ready.IsUnchanged ? "skip" : ready.IsUpdate ? "mod" : "add",
                    ready.IsUnchanged ? "已一致" : ready.IsUpdate ? "更新" : "新增",
                    $"{ready.ModName} ({ready.ModId})",
                    ready.IsUpdate ? $"{ready.ExistingVersion ?? "未知"} → {ready.Version}" : $"v{ready.Version}"),
                new("add", "目标目录", relDest, $"{ready.FileCount} 文件 · {ModEnvironmentSnapshots.FormatBytes(ready.TotalBytes)}"),
                new("skip", "Manifest", ready.ManifestId, $"Workshop #{ready.WorkshopId}")
            };

            NeonOpenOwnedPreview(session, new NeonTransactionConfig(
                Title: ready.IsUnchanged ? $"创意工坊 MOD 已是最新 · {ready.ModName}"
                     : ready.IsUpdate ? $"预览更新创意工坊 MOD · {ready.ModName}"
                     : $"预览安装创意工坊 MOD · {ready.ModName}",
                Subtitle: $"来源：尖塔补给站 · Workshop #{ready.WorkshopId} · Chunk/SHA-1 已校验",
                GameRunning: false,
                BlockedReason: ready.IsUnchanged ? "本地文件与创意工坊内容完全一致，无需重复替换。" : null,
                AutoSnapshotNote: ready.IsUnchanged ? null : "确认后将先自动创建一份完整环境快照（更新前自动保护），再执行替换或新增。",
                BoundaryNote: "Manifest 签名验证与本机游戏分支匹配尚未执行，请仅安装信任的创意工坊内容。",
                Token: ready.InitialInventoryToken,
                Groups: [new NeonDiffGroup("校验结果与写入范围", lines)],
                ConfirmLabel: ready.IsUnchanged ? "" : ready.IsUpdate ? "确认更新（先自动快照）" : "确认安装（先自动快照）",
                OnConfirmAsync: () => NeonConfirmWorkshopInstallAsync(gameRoot, ready, session),
                OnCancel: () => NeonReleasePreview(session)));
            presented = true;
        }
        finally { if (!presented) session.Dispose(); }
    }

    async Task NeonConfirmWorkshopInstallAsync(string gameRoot, WorkshopModPrepared prepared, NeonPreviewSession session)
    {
        using (session)
        {
            NeonDetachPreview(session);
            if (busy || transactionWriting || modInventoryDirty || !NeonCanWrite(gameRoot) || prepared.IsUpdate || prepared.IsUnchanged)
            {
                neonWorkbenchView?.ShowToast("只允许全新安装进入此入口；覆盖更新请重新预览。", NeonTone.Amber);
                return;
            }
            neonLastWriteReceipt = null;
            busy = true;
            transactionWriting = true;
            bool transferred = false;
            WorkshopModInstallResult? committed = null;
            try
            {
                var safety = await Task.Run(() => ModEnvironmentSnapshots.Create(gameRoot, NeonDataRoot, "更新前自动保护"));
                if (!NeonCanWrite(gameRoot)) throw new InvalidOperationException("游戏目录已变化，请重新预览。");
                // The legacy installer is permitted only for a new directory move.
                if (!session.TryTransfer()) throw new InvalidOperationException("预览已取消，请重新准备。");
                transferred = true;
                committed = await Task.Run(() => WorkshopModInstaller.Commit(gameRoot, prepared));
                var result = committed;
                await NeonCompleteCommittedWriteAsync(gameRoot, $"创意工坊 MOD 安装成功：{result.ModName}",
                    () => NeonRunCacheSteps(
                        () =>
                        {
                            var sources = WorkshopSources();
                            sources.Bind(result.ModId, prepared.WorkshopId);
                            sources.RecordBaseline(result.ModId, prepared.WorkshopId, prepared.ManifestId, result.Version, prepared.PayloadSnapshot);
                        },
                        () => WorkshopUpdates().Invalidate(result.ModId),
                        () => modSourceCheckWorkflow?.InvalidateAll()),
                    "创意工坊 MOD 已安装，旧的补丁健康结论已失效，请重新核验。",
                    $"通过尖塔补给站安装了「{result.ModName}」v{result.Version}（已自动创建保护快照 {safety.Id[..8]}）。");
            }
            catch (Exception ex)
            {
                if (committed != null) NeonPublishWriteReceipt(new(NeonWriteState.Committed, "创意工坊 MOD 安装成功", true, null, ex.Message));
                else NeonPublishWriteReceipt(new(NeonWriteState.NotCommitted, "创意工坊安装未完成：" + ex.Message));
            }
            finally
            {
                // A new install has no overwrite recovery journal. Only its transaction owner cleans.
                if (transferred)
                {
                    try { NeonCleanupWorkshopPreview(prepared); }
                    catch (Exception error)
                    {
                        if (neonLastWriteReceipt is { State: NeonWriteState.Committed } receipt)
                            NeonPublishWriteReceipt(receipt with { RefreshWarning = JoinWarning(receipt.RefreshWarning, error.Message) });
                        else neonWorkbenchView?.ShowToast("暂存清理未完成：" + error.Message, NeonTone.Amber);
                    }
                }
                await NeonFinishWriteAsync();
            }
        }
    }

    void NeonChooseLocalZipImport()
    {
        if (busy || transactionWriting || selectedInstallRoot == null) return;
        var open = new OpenFileDialog
        {
            Title = "选择单个 MOD 的本地 ZIP 压缩包",
            Filter = "ZIP 压缩包|*.zip"
        };
        if (open.ShowDialog(this) != true) return;
        _ = NeonPreviewLocalZipImportAsync(open.FileName);
    }

    internal async Task NeonPreviewLocalZipImportAsync(string zipPath)
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        string gameRoot = selectedInstallRoot;
        if (!NeonCanWrite(gameRoot)) return;
        bool running = await CheckGameRunningOffUiAsync(gameRoot);
        var session = NeonLocalZipSession();

        busy = true;
        LocalZipPreviewResult? previewResult = null;
        string? error = null;
        localZipOperationCancellation?.Cancel();
        using var cts = new CancellationTokenSource();
        localZipOperationCancellation = cts;
        try
        {
            previewResult = await session.PreviewAsync(gameRoot, zipPath, cts.Token);
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            if (ReferenceEquals(localZipOperationCancellation, cts))
                localZipOperationCancellation = null;
            busy = false;
        }

        if (error != null || previewResult == null ||
            previewResult.State is LocalZipPreviewState.Failed or LocalZipPreviewState.Cancelled ||
            previewResult.Preview == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "安全拦截：无法导入此本地 ZIP",
                Subtitle: Path.GetFileName(zipPath),
                GameRunning: running,
                BlockedReason: error ?? previewResult?.Error ?? "ZIP 未通过安全与清单校验。",
                AutoSnapshotNote: null,
                BoundaryNote: "安全导入器拒绝路径穿越、保留文件名、脚本/可执行程序及同名目录覆盖；未改动任何磁盘文件。",
                Token: "ZIP-BLOCKED",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭",
                PlainSummary: $"未能导入「{Path.GetFileName(zipPath)}」：{error ?? previewResult?.Error ?? "安全校验未通过"}。"));
            return;
        }

        var preview = previewResult.Preview;
        var pres = LocalZipConfirmationPresenter.Create(preview);
        if (!pres.CanConfirm)
        {
            try { session.DiscardPreview(); } catch { }
            var conflictLines = pres.ConflictDetails
                .Select(d => new NeonDiffLine("del", "冲突", d, ""))
                .ToArray();
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: $"无法导入此本地 ZIP · {pres.ModName}",
                Subtitle: Path.GetFileName(zipPath),
                GameRunning: running,
                BlockedReason: string.Join("\n", pres.ConflictDetails.DefaultIfEmpty("存在阻断冲突，不能安全导入。")),
                AutoSnapshotNote: null,
                BoundaryNote: pres.SourceWarning,
                Token: preview.Token,
                Groups: conflictLines.Length > 0 ? [new NeonDiffGroup("冲突与安全检查详情", conflictLines)] : [],
                ConfirmLabel: "",
                CancelLabel: "关闭",
                ExpandTechnicalDetailsByDefault: true));
            return;
        }

        string relDest = Path.GetRelativePath(gameRoot, pres.TargetDirectory);
        var lines = new List<NeonDiffLine>
        {
            new("add", "新增 MOD", $"{pres.ModName} ({pres.ModId})", $"{pres.FileCount} 文件 · {ModEnvironmentSnapshots.FormatBytes(pres.TotalBytes)}"),
            new("add", "目标目录", relDest, pres.FolderNameReason),
            new("skip", "ZIP SHA-256", pres.ArchiveSha256[..Math.Min(24, pres.ArchiveSha256.Length)] + "…", "导入前再次校验压缩包未变化")
        };
        if (pres.Dependencies.Count > 0)
            lines.Add(new("mod", "声明前置", string.Join("、", pres.Dependencies), "请确认对应前置已安装启用"));
        if (pres.HasHistoricalEvidence)
        {
            foreach (string h in pres.HistoricalEvidence)
                lines.Add(new("skip", "历史来源", h, pres.RuntimeCacheNotice));
        }

        LocalZipHistoricalBindingPolicy selectedPolicy = LocalZipHistoricalBindingPolicy.PreserveUnverified;
        UIElement? policyContent = null;
        if (pres.HasHistoricalEvidence)
        {
            var policyPanel = new StackPanel();
            policyPanel.Children.Add(NeonWidgets.Text(
                $"检测到「{pres.ModId}」已有历史更新来源绑定；本地 ZIP 无法验证作者身份，请选择导入后对历史来源的处理方式：",
                12, NeonPalette.InkSecondary, wrap: true));

            var labels = new Dictionary<LocalZipHistoricalBindingPolicy, string>
            {
                [LocalZipHistoricalBindingPolicy.PreserveUnverified] = "保留历史来源关联（标记未验证）",
                [LocalZipHistoricalBindingPolicy.RequireManualReview] = "暂停自动检查并要求人工核对",
                [LocalZipHistoricalBindingPolicy.Unbind] = "解除历史来源绑定"
            };
            var picker = new ComboBox
            {
                Margin = new Thickness(0, 7, 0, 0),
                Height = 32,
                SelectedIndex = 0
            };
            if (neonWorkbenchView.TryFindResource("NeonComboBox") is Style cbStyle)
                picker.Style = cbStyle;
            foreach (var policy in pres.AvailablePolicies)
            {
                picker.Items.Add(new ComboBoxItem
                {
                    Content = labels.TryGetValue(policy, out var lbl) ? lbl : policy.ToString(),
                    Tag = policy
                });
            }
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is ComboBoxItem { Tag: LocalZipHistoricalBindingPolicy chosen })
                    selectedPolicy = chosen;
            };
            policyPanel.Children.Add(picker);
            policyContent = policyPanel;
        }

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"安全预览导入本地 ZIP · {pres.ModName}",
            Subtitle: $"{Path.GetFileName(zipPath)} · {pres.Version}",
            GameRunning: running,
            BlockedReason: null,
            AutoSnapshotNote: "确认前将自动创建完整 MOD 环境快照（导入前自动保护），并启用独立事务日志与崩溃恢复保护。",
            BoundaryNote: pres.SourceWarning,
            Token: preview.Token,
            Groups: [new NeonDiffGroup("ZIP 安全校验与目录范围", lines)],
            ConfirmLabel: "确认导入（先自动快照）",
            OnConfirmAsync: () => NeonConfirmLocalZipImportAsync(gameRoot, session, preview, () => selectedPolicy),
            CustomContent: policyContent,
            PlainSummary: $"压缩包已通过安全检查（无脚本与路径越界），将把「{pres.ModName}」全新安装到 mods 目录，不覆盖现有模组。",
            OnCancel: () => { try { session.DiscardPreview(); } catch { } }));
    }

    async Task NeonConfirmLocalZipImportAsync(
        string gameRoot,
        LocalZipUiWorkflowSession session,
        LocalModZipPreview preview,
        Func<LocalZipHistoricalBindingPolicy> readPolicy)
    {
        if (busy || transactionWriting || !NeonCanWrite(gameRoot))
        {
            session.DiscardPreview();
            return;
        }
        busy = true;
        transactionWriting = true;
        localZipOperationCancellation?.Cancel();
        using var cts = new CancellationTokenSource();
        localZipOperationCancellation = cts;
        try
        {
            var safety = await NeonSnapshotWorkflow(gameRoot).CreateAsync("导入前自动保护", cts.Token);
            var chosenPolicy = readPolicy();
            var result = await session.ApplyAsync(
                (confirmation, _) => Task.FromResult(new LocalZipApplyDecision(
                    gameRoot.Equals(selectedInstallRoot, StringComparison.OrdinalIgnoreCase) &&
                    !modInventoryDirty &&
                    confirmation.Preview.Token.Equals(preview.Token, StringComparison.Ordinal) &&
                    confirmation.Preview.CanInstallNew,
                    chosenPolicy)),
                () => modFileWatcher.DeferQuietPeriod(),
                progress => Dispatcher.BeginInvoke(() =>
                {
                    if (!gameRoot.Equals(selectedInstallRoot, StringComparison.OrdinalIgnoreCase)) return;
                    transactionWriting = progress.TransactionWriting;
                    modStatus = progress.Message;
                }),
                cts.Token);

            var plan = LocalZipUiInvalidationPlan.ForApply(result);
            string summary = LocalZipUiResultPresenter.FormatApplyResult(result);
            if (result.RecoveryState == LocalZipInstallRecoveryState.PendingRecovery)
            {
                NeonSetRecoveryFailure(gameRoot, NeonZipRecoveryKind, session.TransactionLogDirectory, summary);
                var recovery = await Task.Run(() => session.RecoverPendingTransactions(
                    gameRoot, () => modFileWatcher.DeferQuietPeriod()));
                var recPlan = HandleLocalZipRecoveryReport(gameRoot, recovery, session.TransactionLogDirectory);
                NeonRecordRecoveryStatus(gameRoot, new(NeonZipRecoveryKind, session.TransactionLogDirectory,
                    recovery.HasPendingRecovery || localZipRecoveryBlocked
                        ? localZipRecoveryNotice ?? "存在待人工核对的 ZIP 事务。" : null));
                plan = new LocalZipUiInvalidationPlan(
                    plan.RefreshInventory || recPlan.RefreshInventory,
                    plan.InvalidateSourceCache || recPlan.InvalidateSourceCache,
                    plan.InvalidatePatchLifecycle || recPlan.InvalidatePatchLifecycle);
                if (recovery.CompletedCount > 0 && session.CurrentPreview != null)
                    session.DiscardPreview();
                if (localZipRecoveryNotice != null)
                    summary += "\n" + localZipRecoveryNotice;
            }

            if (plan.InvalidateSourceCache)
                InvalidateLocalZipCaches(gameRoot);
            if (plan.InvalidatePatchLifecycle || result.Success)
                InvalidateLifecycle($"已安全导入本地 MOD「{preview.Package.ModName}」，旧健康结论已失效，请重新核验。");

            if (plan.RefreshInventory || result.Success)
            {
                report = null; states = []; ClearCoop(); ClearSelection(); installPreview = null; lastInstallResult = null;
                await NeonScanAndPublishAsync(gameRoot);
                modFileWatcher.DeferQuietPeriod();
            }

            RecordNeonActivity("install", result.Success
                ? $"通过事务安全导入本地 ZIP 模组「{preview.Package.ModName}」（已创建保护快照 {safety.Id[..8]}）。"
                : $"本地 ZIP 导入未完成：{summary}");
            neonWorkbenchView?.ShowToast(
                result.Success ? $"已安全导入本地 MOD：{preview.Package.ModName}" : summary,
                result.Success && !result.PartialSuccess ? NeonTone.Sage : result.PartialSuccess ? NeonTone.Amber : NeonTone.Cinnabar);
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("导入未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            if (ReferenceEquals(localZipOperationCancellation, cts))
                localZipOperationCancellation = null;
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    // ========================================================================
    // CO-OP ALIGNMENT PRODUCTION ACTIONS
    // ========================================================================

    async Task NeonExportCoopManifestAsync()
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        busy = true;
        JsonElement? snapshot = null;
        string? error = null;
        try
        {
            snapshot = await FreshReportAsync();
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }

        if (error != null || snapshot == null)
        {
            neonWorkbenchView.ShowToast("导出联机清单失败：" + (error ?? "未知错误"), NeonTone.Cinnabar);
            RefreshNeonWorkbench(preserveViewport: true);
            return;
        }

        string jsonText = JsonSerializer.Serialize(snapshot.Value, new JsonSerializerOptions { WriteIndented = true });
        RecordNeonActivity("coop", "执行只读实时扫描并生成了本机 schema 2 联机核对清单。");
        RefreshNeonWorkbench(preserveViewport: true);

        var previewBox = new TextBox
        {
            Text = jsonText,
            IsReadOnly = true,
            FontFamily = NeonPalette.MonoFont,
            FontSize = 11,
            Foreground = NeonPalette.InkPrimary,
            Background = NeonPalette.BgGround,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap
        };

        var snapVal = snapshot.Value.Clone();
        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: "已实时扫描并生成本机联机清单",
            Subtitle: "仅包含已收录修复补丁状态与存在证据，不含游戏存档或个人隐私",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: null,
            BoundaryNote: "把导出的 JSON 发给队友即可在「联机」页导入比对；若之后修改了 MOD 或补丁，请重新导出。",
            Token: "COOP-EXPORT",
            Groups: [],
            ConfirmLabel: "保存清单 JSON 文件…",
            CancelLabel: "关闭",
            FootNote: "点击右侧按钮可保存为 .json 文件，也可以直接复制上方内容。",
            OnConfirmAsync: () =>
            {
                var save = new SaveFileDialog
                {
                    Title = "保存我的联机补丁清单",
                    Filter = "联机补丁清单 (*.json)|*.json",
                    FileName = $"尖塔修补站-联机清单-{DateTime.Now:yyyyMMdd-HHmm}.json"
                };
                if (save.ShowDialog(this) == true)
                {
                    CoopReports.Write(save.FileName, snapVal);
                    RecordNeonActivity("coop", "保存本机联机清单至：" + Path.GetFileName(save.FileName));
                    neonWorkbenchView?.ShowToast("已保存联机清单：" + Path.GetFileName(save.FileName));
                    RefreshNeonWorkbench(preserveViewport: true);
                }
                return Task.CompletedTask;
            },
            CustomContent: previewBox,
            PlainSummary: "已通过实时扫描生成本机联机核对清单（仅含已收录补丁状态与存在证据，不含存档与隐私）；可直接保存为 JSON 发给队友比对。"));
    }

    async Task NeonImportCoopPeerFileAsync()
    {
        if (busy || transactionWriting || selectedInstallRoot == null) return;
        var open = new OpenFileDialog
        {
            Title = "选择队友导出的联机补丁清单 JSON",
            Filter = "联机补丁清单 (*.json)|*.json|所有文件|*.*"
        };
        if (open.ShowDialog(this) != true) return;
        await NeonLoadCoopPeerPathAsync(open.FileName, Path.GetFileNameWithoutExtension(open.FileName));
    }

    internal async Task NeonLoadCoopPeerPathAsync(string path, string? displayLabel = null)
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        busy = true;
        try
        {
            ClearCoop();
            var peer = await Task.Run(() => CoopReports.Read(path));
            await SetCoopComparisonAsync(peer);
            neonCoopPeerLabel = displayLabel ?? Path.GetFileName(path);
            RecordNeonActivity("coop", $"导入队友清单「{neonCoopPeerLabel}」并与本机最新扫描完成比对（发现 {coopRows?.Count ?? 0} 项差异）。");
            neonWorkbenchView.ShowToast($"已与队友清单「{neonCoopPeerLabel}」完成比对");
        }
        catch (Exception ex)
        {
            coopStatus = "导入或比对未完成：" + ex.Message;
            neonWorkbenchView.ShowToast(coopStatus, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonRefreshCoopComparisonAsync()
    {
        if (busy || transactionWriting || coopPeer is not { } peer || neonWorkbenchView == null) return;
        busy = true;
        try
        {
            string? savedLabel = neonCoopPeerLabel;
            await SetCoopComparisonAsync(peer.Clone());
            neonCoopPeerLabel = savedLabel;
            neonWorkbenchView.ShowToast("已重新扫描本机并刷新联机差异比对");
        }
        catch (Exception ex)
        {
            coopStatus = "重新比较失败：" + ex.Message;
            neonWorkbenchView.ShowToast(coopStatus, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonPreviewCoopApplyAsync(string[] selectedIds)
    {
        if (busy || transactionWriting || coopPeer is not { } peer || selectedInstallRoot == null || neonWorkbenchView == null) return;
        if (selectedIds.Length == 0) return;

        string root = selectedInstallRoot;
        bool running = await CheckGameRunningOffUiAsync(root);
        var currentRows = coopRows?.ToArray() ?? [];

        busy = true;
        CoopWorkflowPreparedPlan? prepared = null;
        string? error = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        preparationCancellation = cts;
        try
        {
            prepared = await CoopWorkflow.PrepareAsync(
                root,
                peer,
                selectedIds,
                currentRows,
                (ct, status) => GetPreparedPackageAsync(ct, status),
                cts.Token,
                status => Dispatcher.BeginInvoke(() => neonWorkbenchView?.ShowToast(status)));
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            busy = false;
            if (ReferenceEquals(preparationCancellation, cts)) preparationCancellation = null;
        }

        if (error != null || prepared == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "联机对齐预检未通过",
                Subtitle: $"已勾选 {selectedIds.Length} 项差异",
                GameRunning: running,
                BlockedReason: error ?? "无法生成联机对齐方案。",
                AutoSnapshotNote: null,
                BoundaryNote: "未修改任何游戏文件；请核对差异原因或重新扫描后再试。",
                Token: "COOP-BLOCKED",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        var plan = prepared.Plan;
        coopPlan = plan;
        coopPlanPackage = prepared.Package;
        var groups = new List<NeonDiffGroup>
        {
            new("将按步骤在本机执行的对齐项",
                plan.Rows.Select(r => new NeonDiffLine(
                    r.Action == CoopAction.Disable ? "mod" : "add",
                    r.ActionLabel,
                    $"{r.Name} ({r.Id})",
                    $"本机 {r.Mine} → 队友 {r.Peer}")).ToArray())
        };
        if (plan.Install is { } inst)
        {
            groups.Add(new NeonDiffGroup($"将从已审核修复包安装（变更 {inst.Changed} 文件）",
                (inst.SelectedIds ?? []).Select(id => new NeonDiffLine("add", "安装",
                    Catalog.All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id, "安装前自动备份")).ToArray()));
        }
        if (plan.Moves.Length > 0)
        {
            groups.Add(new NeonDiffGroup($"独立补丁目录移动（{plan.Moves.Length} 项）",
                plan.Moves.Select(m => new NeonDiffLine(
                    m.Plan.Enable ? "add" : "mod",
                    m.Plan.Enable ? "恢复启用" : "移出保留",
                    m.Plan.Name,
                    $"{m.Plan.FileCount} 文件 SHA-256 核对")).ToArray()));
        }

        var readyPlan = prepared;
        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"确认联机对齐方案（{plan.Rows.Length} 项）",
            Subtitle: $"以队友「{neonCoopPeerLabel ?? "对照快照"}」为目标，仅调整本机",
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: "逐步执行并写入完整审计日志；任一步骤失败将立即停止并保留已完成记录。",
            BoundaryNote: "不会修改队友电脑；移出的独立补丁完整保留在停用区，随时可恢复。",
            Token: plan.Token,
            Groups: groups,
            ConfirmLabel: "确认一键处理",
            OnConfirmAsync: () => NeonConfirmCoopApplyAsync(readyPlan),
            PlainSummary: $"将以队友「{neonCoopPeerLabel ?? "对照快照"}」为参考，只在本机调整所选的 {plan.Rows.Length} 项补丁差异，不会修改队友电脑与存档。"));
    }

    async Task NeonConfirmCoopApplyAsync(CoopWorkflowPreparedPlan prepared)
    {
        if (busy || transactionWriting || !NeonCanWrite(selectedInstallRoot)) return;
        busy = true;
        transactionWriting = true;
        coopPlan = null;
        coopPlanPackage = null;
        string? savedLabel = neonCoopPeerLabel;
        try
        {
            var comparisonStamp = selectedInstallRoot is { } coopRoot ? NeonCaptureScanStamp(coopRoot) : (NeonScanStamp?)null;
            var applied = await CoopWorkflow.ApplyAsync(prepared, CurrentCoopContext());
            modFileWatcher.DeferQuietPeriod();
            var outcome = applied.Outcome;
            bool comparisonCurrent = comparisonStamp.HasValue && NeonScanStampCurrent(comparisonStamp.Value);
            if (!comparisonCurrent) NeonRejectStaleScan();
            if (applied.Comparison != null && comparisonCurrent)
            {
                ApplyCoopComparison(applied.Comparison,comparisonStamp);
                neonCoopPeerLabel = savedLabel;
            }
            else
            {
                coopRows = null;
                coopComparison = null;
            }
            coopOutcome = outcome;
            coopStatus = applied.Comparison == null || !comparisonCurrent
                ? "处理已记录，但重新扫描比较失败或已过期：" + applied.RefreshError
                : outcome.Success
                    ? "所选联机对齐步骤已完成并重新扫描复检通过。"
                    : "处理已停止：" + outcome.Error;
            InvalidateLifecycle("联机对齐修改了本地补丁状态，请重新核验补丁健康。");
            RecordNeonActivity("coop", outcome.Success
                ? $"完成联机对齐（{outcome.Completed.Length} 步），复检通过。"
                : $"联机对齐中止：{outcome.Error}");
            neonWorkbenchView?.ShowToast(
                outcome.Success && applied.Comparison != null && comparisonCurrent
                    ? "联机对齐完成并已复检通过"
                    : coopStatus,
                outcome.Success && applied.Comparison != null && comparisonCurrent ? NeonTone.Sage : NeonTone.Amber);
        }
        catch (Exception ex)
        {
            coopStatus = "联机对齐未完成：" + ex.Message;
            neonWorkbenchView?.ShowToast(coopStatus, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    // ========================================================================
    // RECOVERY & SNAPSHOTS PRODUCTION ACTIONS
    // ========================================================================

    async Task NeonCreateEnvironmentSnapshotAsync(string purpose)
    {
        if (busy || transactionWriting || modInventoryDirty || selectedInstallRoot == null || neonWorkbenchView == null) return;
        string gameRoot = selectedInstallRoot;
        busy = true;
        transactionWriting = true;
        try
        {
            var snapshot = await NeonSnapshotWorkflow(gameRoot).CreateAsync(purpose);
            RecordNeonActivity("snapshot",
                $"创建完整环境快照「{purpose}」（{snapshot.Files.Length:N0} 文件 · {ModEnvironmentSnapshots.FormatBytes(snapshot.TotalBytes)}）。");
            neonWorkbenchView.ShowToast($"已创建环境快照「{purpose}」（{snapshot.Files.Length:N0} 文件）");
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("创建快照失败：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonPreviewEnvironmentRestoreAsync(string snapshotId)
    {
        if (busy || transactionWriting || modInventoryDirty || selectedInstallRoot == null || neonWorkbenchView == null) return;
        string gameRoot = selectedInstallRoot;
        bool running = await CheckGameRunningOffUiAsync(gameRoot);
        busy = true;
        ModEnvironmentSnapshotPreview? preview = null;
        string? error = null;
        try
        {
            preview = await NeonSnapshotWorkflow(gameRoot).PreviewRestoreAsync(snapshotId);
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }

        if (error != null || preview == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "无法预览环境快照恢复",
                Subtitle: "快照完整性或文件校验未通过",
                GameRunning: running,
                BlockedReason: error ?? "无法读取目标快照。",
                AutoSnapshotNote: null,
                BoundaryNote: "未对当前 mods 目录进行任何修改。",
                Token: "RESTORE-FAIL",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        var lines = new List<NeonDiffLine>
        {
            new("skip", "当前环境", $"{preview.CurrentFiles:N0} 个文件", "恢复前将自动备份"),
            new("add", "恢复新增", $"+{preview.AddedFiles:N0} 个文件", $"目录 +{preview.AddedDirectories:N0}"),
            new("mod", "内容替换", $"{preview.ChangedFiles:N0} 个文件", "逐文件 SHA-256 校验"),
            new("del", "当前移除", $"-{preview.RemovedFiles:N0} 个文件", $"目录 -{preview.RemovedDirectories:N0}")
        };

        var p = preview;
        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"预览恢复环境快照 · {p.Purpose}",
            Subtitle: $"{p.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 目标 {p.TargetFiles:N0} 文件（{ModEnvironmentSnapshots.FormatBytes(p.TargetBytes)}）",
            GameRunning: running,
            BlockedReason: running ? "请先退出《杀戮尖塔 2》，再执行环境恢复。" : null,
            AutoSnapshotNote: "开始替换前将自动创建一份「恢复前自动保护」快照；即使恢复后发现问题，也可随时再回到当前状态。",
            BoundaryNote: "将整体替换 mods/ 与 已停用模组_尖塔修补站/；不会修改游戏本体与存档。",
            Token: p.Token,
            Groups: [new NeonDiffGroup("文件与目录变化预览", lines)],
            ConfirmLabel: "确认并恢复此环境",
            OnConfirmAsync: () => NeonConfirmEnvironmentRestoreAsync(gameRoot, p),
            PlainSummary: $"将把 MOD 环境还原到「{p.Purpose}」时刻（预计恢复新增 {p.AddedFiles:N0} 个、替换 {p.ChangedFiles:N0} 个、当前移除 {p.RemovedFiles:N0} 个文件）；不修改游戏本体与存档。"));
    }

    async Task NeonConfirmEnvironmentRestoreAsync(string gameRoot, ModEnvironmentSnapshotPreview preview)
    {
        if (busy || transactionWriting || !NeonCanWrite(gameRoot)) return;
        string[] previousIds = modInventory?.Mods.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        busy = true;
        transactionWriting = true;
        try
        {
            var workflowResult = await NeonSnapshotWorkflow(gameRoot).ConfirmRestoreAsync(preview, previousIds);
            modFileWatcher.DeferQuietPeriod();
            var result = workflowResult.Restore;
            InvalidateLifecycle("MOD 环境已恢复至历史快照，旧的补丁健康结论已自动失效，请重新核验。");
            report = null; states = []; ClearCoop(); ClearSelection(); installPreview = null; lastInstallResult = null; modTogglePreview = null;

            // The restore service's internal scan has no watcher stamp. Scan again under our boundary.
            if (workflowResult.RefreshedInventory != null && await NeonScanAndPublishAsync(gameRoot))
            {
                RecordNeonActivity("restore",
                    $"已恢复完整环境快照「{preview.Purpose}」（自动生成恢复前保护快照 {result.SafetySnapshotId[..8]}）。");
                string cacheNote = workflowResult.CacheWarning == null ? "" : $"（部分版本基线清理未完成：{workflowResult.CacheWarning}）";
                neonWorkbenchView?.ShowToast($"已恢复快照「{preview.Purpose}」，当前现场已另存为自动保护快照{cacheNote}");
            }
            else
            {
                modInventoryDirty = true;
                RecordNeonActivity("restore",
                    $"已恢复完整环境快照「{preview.Purpose}」，但后续清单重扫受阻：{workflowResult.RefreshError}。");
                neonWorkbenchView?.ShowToast(
                    $"已恢复快照「{preview.Purpose}」，但重新扫描失败：{workflowResult.RefreshError}，请点击重新扫描。",
                    NeonTone.Amber);
            }
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("环境恢复未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonDeleteEnvironmentSnapshotAsync(string snapshotId)
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        string gameRoot = selectedInstallRoot;
        busy = true;
        try
        {
            await NeonSnapshotWorkflow(gameRoot).DeleteAsync(snapshotId);
            RecordNeonActivity("snapshot", $"删除了历史环境快照（编号 {snapshotId[..Math.Min(12, snapshotId.Length)]}）。");
            neonWorkbenchView.ShowToast("已删除该历史快照");
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("删除快照失败：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            busy = false;
            RefreshNeonWorkbench(preserveViewport: true);
        }
    }

    async Task NeonPreviewPatchBatchRollbackAsync()
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        string root = selectedInstallRoot;
        bool running = await CheckGameRunningOffUiAsync(root);
        if (running)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "无法检查补丁批次回滚",
                Subtitle: "检测到游戏正在运行",
                GameRunning: true,
                BlockedReason: "请先退出《杀戮尖塔 2》，再检查与执行补丁批次回滚。",
                AutoSnapshotNote: null,
                BoundaryNote: null,
                Token: "GAME-RUNNING",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "知道了"));
            return;
        }

        busy = true;
        InstallResult? preview = null;
        string? error = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        preparationCancellation = cts;
        try
        {
            var pkg = await GetPreparedPackageAsync(cts.Token, msg => Dispatcher.BeginInvoke(() => neonWorkbenchView?.ShowToast(msg)));
            preview = await installer.RunAsync(pkg, root, "RollbackPreview");
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            busy = false;
            if (ReferenceEquals(preparationCancellation, cts)) preparationCancellation = null;
        }

        if (error != null || preview == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "补丁批次回滚预检未通过",
                Subtitle: "mod_fix_backups 备份与当前文件核对未通过",
                GameRunning: false,
                BlockedReason: error ?? "没有找到可回滚的补丁安装批次。",
                AutoSnapshotNote: null,
                BoundaryNote: "未修改任何游戏文件。",
                Token: "ROLLBACK-FAIL",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        installPreview = preview;
        var planView = new PlanPresentation(preview);
        var groups = new List<NeonDiffGroup>();
        if (planView.Runtime.Length > 0)
            groups.Add(new NeonDiffGroup($"将撤销的修复文件（{planView.Runtime.Length} 个文件）",
                planView.Runtime.Take(20).Select(f => new NeonDiffLine("del", PlanPresentation.Action(f), f.Relative, "")).ToArray()));
        if (planView.Documents.Length > 0)
            groups.Add(new NeonDiffGroup($"将撤销的说明文件（{planView.Documents.Length} 个文件）",
                planView.Documents.Take(12).Select(f => new NeonDiffLine("mod", PlanPresentation.Action(f), f.Relative, "")).ToArray()));

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: planView.Title,
            Subtitle: $"使用备份：{preview.Backup}",
            GameRunning: false,
            BlockedReason: preview.Changed == 0 ? "没有需要回滚的文件改动。" : null,
            AutoSnapshotNote: null,
            BoundaryNote: "回滚恢复该批次安装前的真实文件状态（包含当时可能存在的旧修复），保留更早独立安装的补丁。",
            Token: preview.Token,
            Groups: groups,
            ConfirmLabel: preview.Changed > 0 ? planView.Confirm : "",
            Danger: true,
            OnConfirmAsync: preview.Changed > 0 ? () => NeonConfirmPatchTransactionAsync(preview) : null,
            PlainSummary: $"将把最近一批安装的修复补丁回退到安装前的真实文件状态（共涉及 {preview.Changed} 个文件），更早独立安装的补丁保持不受影响。"));
    }
}
