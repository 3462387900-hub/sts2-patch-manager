using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpirePatch;

public partial class MainWindow
{
    readonly List<NeonActivityEntry> neonActivityLog = [];
    WorkshopZipPlan? zipImportPreview;
    string? neonFixtureDataRoot;
    string? neonCoopPeerLabel;
    string neonLastUpdateCheckTimeLabel = "尚未检查";

    string NeonDataRoot => neonFixtureDataRoot ?? installer.DataRoot;

    internal void RecordNeonActivity(string kind, string text)
    {
        neonActivityLog.Insert(0, new NeonActivityEntry(DateTime.Now.ToString("HH:mm:ss"), kind, text));
        if (neonActivityLog.Count > 80) neonActivityLog.RemoveAt(neonActivityLog.Count - 1);
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

    NeonRecoverySnapshotState BuildNeonRecoverySnapshotState()
    {
        ModEnvironmentSnapshotEntry[] entries = [];
        string? warning = null;
        if (selectedInstallRoot != null)
        {
            try { entries = ModEnvironmentSnapshots.List(selectedInstallRoot, NeonDataRoot); }
            catch (Exception ex) { warning = ex.Message; }
        }

        var disabledAll = (modInventory?.Mods ?? [])
            .Where(m => !m.Enabled)
            .OrderBy(m => ModPresentation.IsIndependentPatch(m) ? 1 : 0)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new NeonRecoverySnapshotState(
            Snapshots: entries,
            SnapshotsWarning: warning,
            LatestPatchBatch: NeonWorkbenchBuilder.ReadLatestPatchBackupBatch(selectedInstallRoot),
            DisabledAllMods: disabledAll,
            ActivityLog: neonActivityLog.ToArray());
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
                RecordNeonActivity("patch", enabled ? "开启了目标 MOD 删除后自动安全停用孤立补丁。" : "关闭了孤立补丁自动停用。");
                view.ShowToast(enabled ? "已开启：目标 MOD 全部删除后自动安全停用其孤立补丁" : "已关闭孤立补丁自动停用");
                view.UpdateOrphanAutoStopOnly(enabled, BuildNeonRecoverySnapshotState());
            }
            catch (Exception ex)
            {
                view.ShowToast("保存自动停用设置失败：" + ex.Message, NeonTone.Cinnabar);
            }
        };
        view.RequestAcknowledgeModReview += patch => NeonOpenAcknowledgeModReviewModal(patch);

        // Acquire & Updates page
        view.RequestCheckAllModUpdates += () =>
        {
            if (selectedInstallRoot == null || modInventory == null || modInventoryDirty)
            {
                view.ShowToast("MOD 清单尚未稳定，请刷新后再检查更新。", NeonTone.Amber);
                return;
            }
            neonLastUpdateCheckTimeLabel = DateTime.Now.ToString("HH:mm:ss");
            RecordNeonActivity("update", "启动了后台只读来源更新检查（GitHub + Steam 创意工坊）。");
            CheckAllModUpdates();
            view.ShowToast("正在后台只读检查 GitHub 与创意工坊更新（不阻塞页面切换）…");
            RefreshNeonWorkbench(preserveViewport: true);
        };
        view.RequestCancelUpdateCheck += () =>
        {
            modUpdateCheckCancellation?.Cancel();
            view.ShowToast("已请求取消本次后台更新检查", NeonTone.Amber);
        };
        view.RequestPreviewWorkshopInstall += wsId => _ = NeonPreviewWorkshopInstallAsync(wsId);
        view.RequestChooseLocalZipImport += NeonChooseLocalZipImport;
        view.RequestDiscoverGitHubCandidates += () =>
        {
            if (selectedInstallRoot == null || modInventory == null) return;
            RecordNeonActivity("source", "启动了未绑定 MOD 的 GitHub 候选来源发现（最多 8 项）。");
            DiscoverGitHubMods();
            view.ShowToast("正在后台查找 GitHub 候选来源（仅生成候选，不会自动绑定）…");
        };

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
        bool running = GameEnvironment.Running(root);
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
        if (busy || transactionWriting) return;
        busy = true;
        transactionWriting = true;
        try
        {
            var result = await Task.Run(() => PatchOrganizer.Apply(plan, NeonDataRoot));
            installPreview = null; lastInstallResult = null; ClearSelection(); ClearCoop();
            report = null; states = []; demo = false;
            modInventory = await Task.Run(() => ModManager.Scan(plan.Root));
            modInventoryDirty = false;
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
        bool running = GameEnvironment.Running(root);
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
        if (busy || transactionWriting || preparedPackage == null) return;
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
            modInventory = await Task.Run(() => ModManager.Scan(preview.Root));
            modInventoryDirty = false;
            var definitions = sourceCurrent && onlineCatalog != null ? onlineCatalog.Patches : Catalog.Bundled;
            report = await Task.Run(() => Catalog.Scan(preview.Root, definitions, sourceCurrent ? onlineCatalog?.Commit : null));
            scannedCatalog = definitions;
            states = report.Patches;
            demo = false;
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

    async Task NeonPreviewWorkshopInstallAsync(ulong workshopId)
    {
        if (busy || transactionWriting || selectedInstallRoot == null || neonWorkbenchView == null) return;
        if (modInventoryDirty)
        {
            neonWorkbenchView.SetWorkshopFeedback("本地 MOD 清单尚未稳定，请刷新后再试。", NeonTone.Cinnabar);
            return;
        }
        string gameRoot = selectedInstallRoot;
        bool running = GameEnvironment.Running(gameRoot);
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

        busy = true;
        WorkshopModPrepared? prepared = null;
        string? error = null;
        try
        {
            using var wsInstaller = new WorkshopModInstaller();
            prepared = await wsInstaller.PrepareAsync(gameRoot, workshopId, progress: msg =>
                Dispatcher.BeginInvoke(() => neonWorkbenchView?.SetWorkshopFeedback(msg, NeonTone.Sage)));
            if (gameRoot != selectedInstallRoot)
                throw new InvalidOperationException("游戏目录已切换，已取消本次安装。");
            string? boundModId = WorkshopSources().ModIdFor(workshopId);
            if (boundModId != null && !boundModId.Equals(prepared.ModId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"该 Workshop ID 已关联到另一个 MOD ID：{boundModId}；为避免来源错配，未继续安装。");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            WorkshopModInstaller.Cleanup(prepared);
            prepared = null;
        }
        finally { busy = false; }

        if (error != null || prepared == null)
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

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
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
            OnConfirmAsync: ready.IsUnchanged ? null : () => NeonConfirmWorkshopInstallAsync(gameRoot, ready)));
    }

    async Task NeonConfirmWorkshopInstallAsync(string gameRoot, WorkshopModPrepared prepared)
    {
        if (busy || transactionWriting) return;
        busy = true;
        transactionWriting = true;
        try
        {
            var safety = await Task.Run(() => ModEnvironmentSnapshots.Create(gameRoot, NeonDataRoot, "更新前自动保护"));
            var result = await Task.Run(() => WorkshopModInstaller.Commit(gameRoot, prepared));
            try
            {
                var sources = WorkshopSources();
                sources.Bind(result.ModId, prepared.WorkshopId);
                sources.RecordBaseline(result.ModId, prepared.WorkshopId, prepared.ManifestId, result.Version, prepared.PayloadSnapshot);
                WorkshopUpdates().Invalidate(result.ModId);
            }
            catch { }

            modInventory = await Task.Run(() => ModManager.Scan(gameRoot));
            modInventoryDirty = false;
            report = null; states = []; ClearCoop(); ClearSelection(); installPreview = null; lastInstallResult = null;
            InvalidateLifecycle("创意工坊 MOD 已安装或更新，旧的补丁健康结论已失效，请重新核验。");
            RecordNeonActivity("install",
                $"通过尖塔补给站{(result.BackupFolder != null ? "更新" : "安装")}了「{result.ModName}」v{result.Version}（已自动创建保护快照 {safety.Id[..8]}）。");
            neonWorkbenchView?.ShowToast($"创意工坊 MOD {(result.BackupFolder != null ? "更新" : "安装")}成功：{result.ModName}");
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("创意工坊安装未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
            WorkshopModInstaller.Cleanup(prepared);
            busy = false;
            transactionWriting = false;
            RefreshNeonWorkbench(preserveViewport: true);
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
        bool running = GameEnvironment.Running(gameRoot);

        busy = true;
        WorkshopZipPlan? plan = null;
        string? error = null;
        try
        {
            plan = await Task.Run(() => WorkshopZipImport.Preview(gameRoot, zipPath));
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }

        if (error != null || plan == null)
        {
            neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
                Title: "安全拦截：无法导入此本地 ZIP",
                Subtitle: Path.GetFileName(zipPath),
                GameRunning: running,
                BlockedReason: error ?? "ZIP 未通过安全与清单校验。",
                AutoSnapshotNote: null,
                BoundaryNote: "安全导入器拒绝路径穿越、保留文件名、脚本/可执行程序及同名目录覆盖；未改动任何磁盘文件。",
                Token: "ZIP-BLOCKED",
                Groups: [],
                ConfirmLabel: "",
                CancelLabel: "关闭"));
            return;
        }

        zipImportPreview = plan;
        string relDest = Path.GetRelativePath(gameRoot, plan.Destination);
        var lines = new List<NeonDiffLine>
        {
            new("add", "新增 MOD", $"{plan.ModName} ({plan.ModId})", $"{plan.FileCount} 文件 · {ModEnvironmentSnapshots.FormatBytes(plan.Bytes)}"),
            new("add", "目标目录", relDest, "不会覆盖现有目录"),
            new("skip", "ZIP SHA-256", plan.ArchiveSha256[..24] + "…", "导入前再次校验压缩包未变化")
        };
        if (plan.Dependencies.Length > 0)
            lines.Add(new("mod", "声明前置", string.Join("、", plan.Dependencies), "请确认对应前置已安装启用"));

        neonWorkbenchView.OpenTransactionModal(new NeonTransactionConfig(
            Title: $"安全预览导入本地 ZIP · {plan.ModName}",
            Subtitle: Path.GetFileName(zipPath),
            GameRunning: false,
            BlockedReason: null,
            AutoSnapshotNote: "确认前将自动创建完整 MOD 环境快照（导入前自动保护），确保可一键回退。",
            BoundaryNote: "本地 ZIP 属于不可信外部输入；已拦截脚本、链接与路径越界，请仅导入你信任的 MOD 压缩包。",
            Token: plan.ArchiveSha256[..10],
            Groups: [new NeonDiffGroup("ZIP 安全校验结果", lines)],
            ConfirmLabel: "确认导入（先自动快照）",
            OnConfirmAsync: () => NeonConfirmLocalZipImportAsync(plan),
            PlainSummary: $"压缩包已通过安全检查（无脚本与路径越界），将把「{plan.ModName}」解压为独立 MOD 目录，不覆盖现有模组。"));
    }

    async Task NeonConfirmLocalZipImportAsync(WorkshopZipPlan plan)
    {
        if (busy || transactionWriting) return;
        busy = true;
        transactionWriting = true;
        zipImportPreview = null;
        try
        {
            var safety = await Task.Run(() => ModEnvironmentSnapshots.Create(plan.Root, NeonDataRoot, "导入前自动保护"));
            await Task.Run(() => WorkshopZipImport.Apply(plan));
            modInventory = await Task.Run(() => ModManager.Scan(plan.Root));
            modInventoryDirty = false;
            report = null; states = []; ClearCoop(); ClearSelection(); installPreview = null; lastInstallResult = null;
            InvalidateLifecycle($"已安全导入本地 MOD「{plan.ModName}」，旧健康结论已失效，请重新核验。");
            RecordNeonActivity("install", $"安全导入本地 ZIP 模组「{plan.ModName}」（{plan.FileCount} 文件，已创建保护快照 {safety.Id[..8]}）。");
            neonWorkbenchView?.ShowToast($"已安全导入本地 MOD：{plan.ModName}");
        }
        catch (Exception ex)
        {
            neonWorkbenchView?.ShowToast("导入未完成：" + ex.Message, NeonTone.Cinnabar);
        }
        finally
        {
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
            CustomContent: previewBox));
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
        bool running = GameEnvironment.Running(root);
        bool needsPackage = coopRows?.Any(r => selectedIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase) && r.Action == CoopAction.Install) == true;

        busy = true;
        CoopPlan? plan = null;
        string? error = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        preparationCancellation = cts;
        try
        {
            PreparedPackage? pkg = null;
            if (needsPackage)
                pkg = await GetPreparedPackageAsync(cts.Token, msg => Dispatcher.BeginInvoke(() => neonWorkbenchView?.ShowToast(msg)));
            plan = await CoopAlignment.PrepareAsync(installer, pkg, root, peer, selectedIds);
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            busy = false;
            if (ReferenceEquals(preparationCancellation, cts)) preparationCancellation = null;
        }

        if (error != null || plan == null)
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

        coopPlan = plan;
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
            OnConfirmAsync: () => NeonConfirmCoopApplyAsync(plan)));
    }

    async Task NeonConfirmCoopApplyAsync(CoopPlan plan)
    {
        if (busy || transactionWriting) return;
        busy = true;
        transactionWriting = true;
        coopPlan = null;
        string? savedLabel = neonCoopPeerLabel;
        try
        {
            var outcome = await CoopAlignment.ApplyAsync(installer, preparedPackage, plan);
            await SetCoopComparisonAsync(plan.Peer);
            neonCoopPeerLabel = savedLabel;
            coopOutcome = outcome;
            coopStatus = outcome.Success
                ? "所选联机对齐步骤已完成并重新扫描复检通过。"
                : "处理已停止：" + outcome.Error;
            InvalidateLifecycle("联机对齐修改了本地补丁状态，请重新核验补丁健康。");
            RecordNeonActivity("coop", outcome.Success
                ? $"完成联机对齐（{outcome.Completed.Length} 步），复检通过。"
                : $"联机对齐中止：{outcome.Error}");
            neonWorkbenchView?.ShowToast(
                outcome.Success ? "联机对齐完成并已复检通过" : "联机对齐已停止：" + outcome.Error,
                outcome.Success ? NeonTone.Sage : NeonTone.Cinnabar);
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
            var snapshot = await Task.Run(() => ModEnvironmentSnapshots.Create(gameRoot, NeonDataRoot, purpose));
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
        bool running = GameEnvironment.Running(gameRoot);
        busy = true;
        ModEnvironmentSnapshotPreview? preview = null;
        string? error = null;
        try
        {
            preview = await Task.Run(() => ModEnvironmentSnapshots.PreviewRestore(gameRoot, NeonDataRoot, snapshotId));
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
        if (busy || transactionWriting) return;
        string[] previousIds = modInventory?.Mods.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        busy = true;
        transactionWriting = true;
        try
        {
            var result = await Task.Run(() => ModEnvironmentSnapshots.Restore(gameRoot, NeonDataRoot, preview.SnapshotId, preview.Token));
            InvalidateLifecycle("MOD 环境已恢复至历史快照，旧的补丁健康结论已自动失效，请重新核验。");
            report = null; states = []; ClearCoop(); ClearSelection(); installPreview = null; lastInstallResult = null; modTogglePreview = null;

            var refreshed = await Task.Run(() => ModManager.Scan(gameRoot));
            modInventory = refreshed;
            modInventoryDirty = false;
            try
            {
                var sources = WorkshopSources();
                var updates = WorkshopUpdates();
                foreach (string id in previousIds.Concat(refreshed.Mods.Select(m => m.Id)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    sources.ClearBaseline(id);
                    updates.Invalidate(id);
                }
            }
            catch { }

            RecordNeonActivity("restore",
                $"已恢复完整环境快照「{preview.Purpose}」（自动生成恢复前保护快照 {result.SafetySnapshotId[..8]}）。");
            neonWorkbenchView?.ShowToast($"已恢复快照「{preview.Purpose}」，当前现场已另存为自动保护快照");
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
            await Task.Run(() => ModEnvironmentSnapshots.Delete(gameRoot, NeonDataRoot, snapshotId));
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
        bool running = GameEnvironment.Running(root);
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
