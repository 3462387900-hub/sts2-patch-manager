using System.IO;
using System.Text.Json;

namespace SpirePatch;

internal enum NeonGateMode
{
    NoGameRoot,
    Blocked,
    Advisory,
    Ready
}

internal sealed record NeonVersionMismatchInfo(
    string PrereqId,
    string PrereqName,
    string ActualVersion,
    string RequiredVersion,
    string Operator,
    string SourceNote,
    bool FromPatchRule);

internal sealed record NeonCandidateSource(
    string KindLabel,
    string TargetLabel,
    string Note,
    string? GitHubRepository,
    ulong? WorkshopId,
    bool IsVerified,
    bool IsAmbiguous);

internal sealed record NeonRelatedPatch(
    string PatchId,
    string Name,
    string Version,
    string Summary,
    bool IsStandalone,
    string StatusLabel,
    NeonTone StatusTone,
    string VerdictText,
    bool IsVerifiedHealthy,
    bool CanRetire,
    string? RetireBlockReason,
    LifecyclePatchItem? LifecycleItem,
    PatchState? ScanState,
    string? ReviewedRequiredVersion,
    string? ReviewedRequiredModId,
    string? ReviewedRequiredActualVersion = null,
    string[]? RelatedModIds = null,
    string[]? RequiredPatchIds = null,
    ManagedMod? InstalledMod = null,
    string? InstalledRelativeFolder = null,
    bool IsLooseInModsRoot = false,
    ModBaselineChange? PendingModChange = null);

internal sealed record NeonActivityEntry(
    string TimeLabel,
    string Kind,
    string Text);

internal sealed record NeonPatchBackupBatch(
    string FolderName,
    string FullPath,
    string RelativePath,
    DateTime Time,
    string[] AffectedNames,
    bool DocsOnly);

internal sealed record NeonCoopSnapshotState(
    bool HasPeer,
    string? LocalScanTimeLabel,
    string? PeerLabel,
    string? PeerTimeLabel,
    string? PeerManagerVersion,
    bool PeerIsLegacy,
    string? ComparisonSummary,
    CoopDifference[] Rows,
    CoopOutcome? LastOutcome,
    string StatusText);

internal sealed record NeonRecoverySnapshotState(
    ModEnvironmentSnapshotEntry[] Snapshots,
    string? SnapshotsWarning,
    NeonPatchBackupBatch? LatestPatchBatch,
    ManagedMod[] DisabledAllMods,
    NeonActivityEntry[] ActivityLog);

internal sealed record NeonFaultChip(
    string Id,
    NeonTone Severity,
    string Kicker,
    string Description,
    string CtaLabel,
    string? FocusModId,
    string? NavigateTab,
    bool OpenUpdatePreview = false,
    NeonVersionMismatchInfo? VersionDiff = null,
    string? VersionDiffTail = null,
    NeonRelatedPatch? LeadPatch = null);

internal sealed record NeonWorkbenchRow(
    ManagedMod Mod,
    NeonModCategory Category,
    string SourceMuted,
    string? GitHubRepo,
    GitHubModResult? GitHubResult,
    ulong? WorkshopId,
    WorkshopModUpdateResult? WorkshopResult,
    NeonCandidateSource? CandidateSource,
    ModDependencyEdge[] OwnEdges,
    ModDependencyEdge[] ActiveOwnIssues,
    ModDependencyEdge[] InboundEdges,
    ModDependencyEdge[] InboundActiveBroken,
    ModDependencyEdge? OwnDisabledPrereq,
    ModDependencyEdge? OwnMissing,
    ModDependencyEdge? OwnOtherIssue,
    ModDependencyEdge? CausingDisabledBlock,
    NeonVersionMismatchInfo? OwnVersionMismatch,
    NeonVersionMismatchInfo? CausingVersionMismatch,
    NeonRelatedPatch[] RelatedPatches,
    string? ToggleBlockReason,
    bool IsLockedByDependents,
    bool HasUpdate,
    bool IsUnbound,
    bool HasDanger,
    bool HasWarn,
    bool HasAnyAttention,
    int PriorityScore,
    int FileCount,
    long TotalBytes);

internal sealed record NeonManagerUpdateState(
    string CurrentVersion,
    ManagerUpdateChannel Channel,
    bool Busy,
    bool CleanupBusy,
    bool HasNewerRelease,
    bool CanAutoInstall,
    string? LatestVersion,
    string? ReleaseNotes,
    string? ReleaseUrl,
    long LatestSizeBytes,
    string Status,
    string CleanupStatus,
    string? DownloadedArchivePath);

internal sealed record NeonGitHubNetworkState(
    string Mode,
    string ProxyUrl);

internal sealed record NeonWorkbenchSnapshot(
    string? GameRoot,
    bool GameRunning,
    bool HasInventory,
    bool ModInventoryDirty,
    bool LifecycleStale,
    bool LifecycleScanning,
    string LifecycleStatus,
    bool IsPatchScanValid,
    bool IsLifecycleHealthValid,
    DateTime? LastScanTime,
    NeonGateMode GateMode,
    int HardBlockCount,
    NeonFaultChip[] Faults,
    NeonWorkbenchRow[] AllRows,
    ModDependencyGraphReport? DependencyGraph,
    NeonRelatedPatch[] AllCatalogPatches,
    int EnabledModsCount,
    int PendingPatchesCount,
    int VerifiedInstalledPatchesCount,
    int RetirablePatchesCount,
    int AvailableUpdatesCount,
    IReadOnlyDictionary<string, int> FilterCounts,
    bool OrphanAutoStopEnabled,
    bool OnlineRefreshing,
    string OnlineSourceStatus,
    int OnlineCatalogCount,
    bool CheckingModUpdates,
    string LastUpdateCheckTimeLabel,
    NeonCoopSnapshotState Coop,
    NeonRecoverySnapshotState Recovery,
    NeonManagerUpdateState? ManagerUpdate = null,
    NeonGitHubNetworkState? GitHubNetwork = null,
    bool IsScanning = false,
    string? ScanStageText = null);

internal static class NeonWorkbenchBuilder
{
    internal static NeonModCategory ClassifyCategory(ManagedMod mod)
    {
        string text = mod.Name + " " + mod.Description;
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (mod.Id.EndsWith("Lib", StringComparison.OrdinalIgnoreCase) ||
            mod.Id.EndsWith("Core", StringComparison.OrdinalIgnoreCase) ||
            Has("前置库", "基础库", "核心库", "框架库", "library mod", "shared runtime"))
            return NeonModCategory.Lib;

        if (Has("翻译", "汉化", "统计面板", "预测图谱", "人数上限", "观战视角", "translator", "damage meter", "intent graph", "playerlimit"))
            return NeonModCategory.Util;

        if (Has("原创角色", "狙击角色", "共鸣角色", "特色角色", "角色扩展", "角色模组", "角色 MOD", "角色MOD", "角色 Mod", "战斗模组", "王储", "企鹅物流", "playable character", "character mod", "playable Kakarot"))
            return NeonModCategory.Char;

        if (Has("符文", "大乱斗", "第四幕", "无尽爬塔", "卡牌", "事件", "遗物", "玩法扩展", "relic", "cards", "event", "mechanic", "deck"))
            return NeonModCategory.Content;

        if (Has("美化", "皮肤替换", "外观", "reskin", "skin mod", "turns the defect into", "修复", "兼容", "bugfix", "quick load"))
            return NeonModCategory.Util;

        return NeonModCategory.Content;
    }

    internal static string ShortenGamePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return "尚未选择游戏目录";
        string trimmed = fullPath.TrimEnd('\\', '/');
        if (trimmed.Length <= 26) return trimmed;
        string? root = Path.GetPathRoot(trimmed);
        string leaf = Path.GetFileName(trimmed);
        if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(leaf))
            return root + "…\\" + leaf;
        return leaf.Length > 0 ? leaf : trimmed;
    }

    internal static NeonWorkbenchSnapshot Build(
        string? gameRoot,
        bool gameRunning,
        ModInventory? inventory,
        ModDependencyGraphReport? dependencyGraph,
        ScanReport? patchScanReport,
        PatchLifecycleReport? lifecycleReport,
        bool modInventoryDirty,
        bool lifecycleStale,
        bool lifecycleScanning,
        string lifecycleStatus,
        GitHubModUpdates? githubUpdates,
        WorkshopModSources? workshopSources,
        WorkshopModUpdates? workshopUpdates,
        GitHubModDiscovery? githubDiscovery,
        bool orphanAutoStopEnabled = true,
        bool onlineRefreshing = false,
        string onlineSourceStatus = "",
        int onlineCatalogCount = 0,
        bool checkingModUpdates = false,
        string lastUpdateCheckTimeLabel = "尚未检查",
        NeonCoopSnapshotState? coopState = null,
        NeonRecoverySnapshotState? recoveryState = null,
        NeonManagerUpdateState? managerUpdateState = null,
        NeonGitHubNetworkState? githubNetworkState = null,
        bool isScanning = false,
        string? scanStageText = null)
    {
        bool hasRoot = !string.IsNullOrWhiteSpace(gameRoot);
        bool hasInventory = hasRoot && inventory != null;
        bool isPatchScanValid = hasRoot && !modInventoryDirty && patchScanReport is { InventoryComplete: true } &&
            patchScanReport.Directory.Equals(gameRoot, StringComparison.OrdinalIgnoreCase);
        bool isLifecycleHealthValid = isPatchScanValid && !lifecycleStale && !lifecycleScanning &&
            lifecycleReport != null &&
            lifecycleReport.Root.Equals(gameRoot, StringComparison.OrdinalIgnoreCase);

        if (hasInventory && dependencyGraph == null)
            dependencyGraph = ModDependencyGraph.Analyze(inventory!);

        var modsById = (inventory?.Mods ?? [])
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        var duplicateIds = modsById.Where(kv => kv.Value.Length > 1)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Project every catalogued patch with honest health/scan validity
        var catalogPatches = new List<NeonRelatedPatch>();
        var reviewedVersionRequirements = new List<(string PatchId, string PatchName, string TargetModId, string RequiredVer)>();

        foreach (var onlinePatch in Catalog.Bundled)
        {
            var info = Catalog.Info(onlinePatch);
            bool standalone = Catalog.IsStandalonePatch(onlinePatch.ComponentId);
            PatchState? scanState = isPatchScanValid
                ? patchScanReport!.Patches.FirstOrDefault(p => p.Patch.Id.Equals(onlinePatch.ComponentId, StringComparison.OrdinalIgnoreCase))
                : null;
            LifecyclePatchItem? lifeItem = isLifecycleHealthValid
                ? lifecycleReport!.Patches.FirstOrDefault(p => p.Patch.Id.Equals(onlinePatch.ComponentId, StringComparison.OrdinalIgnoreCase))
                : null;
            ManagedMod? installedPatchMod = modsById.TryGetValue(onlinePatch.ComponentId, out var patchMatches) && patchMatches.Length == 1
                ? patchMatches[0]
                : null;

            string statusLabel;
            NeonTone statusTone;
            bool isVerifiedHealthy = false;
            string verdict;

            if (lifecycleScanning)
            {
                statusLabel = "检查中";
                statusTone = NeonTone.Amber;
                verdict = "正在核对本地 MOD 与补丁文件指纹…";
            }
            else if (installedPatchMod is { Enabled: false })
            {
                statusLabel = "已停用";
                statusTone = NeonTone.Neutral;
                verdict = "独立补丁保留在停用区，文件未删除，可随时恢复启用。";
            }
            else if (!isPatchScanValid || lifecycleStale)
            {
                if (lifecycleStale || modInventoryDirty)
                {
                    statusLabel = "需重新检查";
                    statusTone = NeonTone.Amber;
                    verdict = string.IsNullOrWhiteSpace(lifecycleStatus)
                        ? "检测到文件变化，旧的补丁健康结论已自动失效，请重新扫描核验。"
                        : lifecycleStatus;
                }
                else
                {
                    statusLabel = "待扫描核验";
                    statusTone = NeonTone.Neutral;
                    verdict = "尚未完成本目录的补丁指纹扫描，不显示校验通过结论。";
                }
            }
            else if (lifeItem?.CanRetire == true)
            {
                statusLabel = "可安全退役";
                statusTone = NeonTone.Sage;
                verdict = lifeItem.Evidence?.Note ?? lifeItem.Message;
            }
            else if (lifeItem?.State == LifecycleState.VerifiedUpstreamFix)
            {
                statusLabel = "上游修复已验证";
                statusTone = NeonTone.Sage;
                verdict = lifeItem.Message;
            }
            else if (lifeItem?.State == LifecycleState.Incompatible)
            {
                statusLabel = "已确认不兼容";
                statusTone = NeonTone.Cinnabar;
                verdict = lifeItem.Message;
            }
            else if (lifeItem?.State == LifecycleState.SuspectedUpstreamFix)
            {
                statusLabel = "疑似上游修复";
                statusTone = NeonTone.Amber;
                verdict = lifeItem.Message;
            }
            else if (lifeItem?.State == LifecycleState.NeedsReview)
            {
                statusLabel = "需核对";
                statusTone = NeonTone.Cinnabar;
                verdict = lifeItem.Message;
            }
            else if (scanState?.Status == "已安装")
            {
                if (isLifecycleHealthValid)
                {
                    statusLabel = "校验通过";
                    statusTone = NeonTone.Sage;
                    isVerifiedHealthy = true;
                    verdict = lifeItem?.Message ?? scanState.Reason;
                }
                else
                {
                    statusLabel = "文件匹配 · 待核健康";
                    statusTone = NeonTone.Neutral;
                    verdict = "当前补丁文件指纹一致，尚未完成补丁健康基线核验。";
                }
            }
            else if (scanState?.Status == "待修复")
            {
                statusLabel = "待修复";
                statusTone = NeonTone.Amber;
                verdict = scanState.Reason;
            }
            else if (scanState?.Status == "需联动")
            {
                statusLabel = "需联动";
                statusTone = NeonTone.Indigo;
                verdict = scanState.Reason;
            }
            else if (scanState?.Status == "需核对")
            {
                statusLabel = "需核对";
                statusTone = NeonTone.Cinnabar;
                verdict = scanState.Reason;
            }
            else if (scanState?.Status == "已停用")
            {
                statusLabel = "已停用";
                statusTone = NeonTone.Neutral;
                verdict = scanState.Reason;
            }
            else
            {
                statusLabel = "不适用";
                statusTone = NeonTone.Neutral;
                verdict = scanState?.Reason ?? "当前组合未命中该修复补丁适用条件。";
            }

            // Collect reviewed version requirements from Catalog.Bundled when patch is active/installed
            string? reviewedVer = null;
            string? reviewedModId = null;
            string? reviewedActualVer = null;
            string[] relatedModIds = Catalog.PatchRelatedModIds(onlinePatch);
            if (installedPatchMod is { Enabled: true } || scanState?.Status is "已安装" or "需联动" or "待修复" or "需核对")
            {
                foreach (string relId in relatedModIds)
                {
                    if (!modsById.TryGetValue(relId, out var targetArr) || targetArr.Length != 1) continue;
                    var targetMod = targetArr[0];
                    if (!targetMod.Enabled) continue;
                    string? reqVer = Catalog.ReviewedRequiredVersion(onlinePatch.ComponentId, relId);
                    if (reqVer == null) continue;
                    int? cmp = NeonVersionDiff.CompareNumeric(targetMod.Version, reqVer);
                    if (cmp.HasValue && cmp.Value < 0)
                    {
                        reviewedVer = reqVer;
                        reviewedModId = relId;
                        reviewedActualVer = targetMod.Version;
                        reviewedVersionRequirements.Add((onlinePatch.ComponentId, info.Name, relId, reqVer));
                    }
                }
            }

            string? installedRelFolder = null;
            bool isLooseInModsRoot = false;
            if (installedPatchMod != null && hasRoot)
            {
                installedRelFolder = Path.GetRelativePath(gameRoot!, installedPatchMod.Folder);
                if (standalone && installedPatchMod.Enabled)
                {
                    string modsRoot = Path.Combine(gameRoot!, "mods").TrimEnd('\\', '/');
                    string collectionDir = Path.Combine(modsRoot, PatchOrganizer.CollectionName);
                    string? parentDir = Path.GetDirectoryName(installedPatchMod.Folder)?.TrimEnd('\\', '/');
                    bool inCollection = installedPatchMod.Folder.StartsWith(collectionDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    if (!inCollection && string.Equals(parentDir, modsRoot, StringComparison.OrdinalIgnoreCase))
                        isLooseInModsRoot = true;
                }
            }

            ModBaselineChange? pendingModChange = null;
            if (isLifecycleHealthValid && lifecycleReport?.History.PendingReview is { Length: > 0 } pendingReviews)
            {
                pendingModChange = pendingReviews.FirstOrDefault(ch =>
                    relatedModIds.Contains(ch.ModId, StringComparer.OrdinalIgnoreCase));
            }

            catalogPatches.Add(new NeonRelatedPatch(
                onlinePatch.ComponentId,
                info.Name,
                info.Version,
                info.Summary,
                standalone,
                statusLabel,
                statusTone,
                verdict,
                isVerifiedHealthy,
                CanRetire: isLifecycleHealthValid && lifeItem?.CanRetire == true,
                RetireBlockReason: lifeItem?.BlockReason,
                LifecycleItem: lifeItem,
                ScanState: scanState,
                ReviewedRequiredVersion: reviewedVer,
                ReviewedRequiredModId: reviewedModId,
                ReviewedRequiredActualVersion: reviewedActualVer,
                RelatedModIds: relatedModIds,
                RequiredPatchIds: scanState?.RelatedPatchIds ?? [],
                InstalledMod: installedPatchMod,
                InstalledRelativeFolder: installedRelFolder,
                IsLooseInModsRoot: isLooseInModsRoot,
                PendingModChange: pendingModChange));
        }

        // Map modId -> related catalog patches (only patches relevant to installed environment)
        var relatedPatchMap = new Dictionary<string, List<NeonRelatedPatch>>(StringComparer.OrdinalIgnoreCase);
        foreach (var onlinePatch in Catalog.Bundled)
        {
            var projected = catalogPatches.First(p => p.PatchId.Equals(onlinePatch.ComponentId, StringComparison.OrdinalIgnoreCase));
            bool patchInstalled = modsById.ContainsKey(onlinePatch.ComponentId);
            bool patchRelevant = patchInstalled || projected.ScanState?.Status is "待修复" or "需联动" or "需核对" or "已安装" or "已停用"
                || projected.CanRetire;
            if (!patchRelevant) continue;

            var relatedIds = Catalog.PatchRelatedModIds(onlinePatch)
                .Append(onlinePatch.ComponentId)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string modId in relatedIds)
            {
                if (!relatedPatchMap.TryGetValue(modId, out var list))
                    relatedPatchMap[modId] = list = [];
                if (!list.Any(x => x.PatchId.Equals(projected.PatchId, StringComparison.OrdinalIgnoreCase)))
                    list.Add(projected);
            }
        }

        // 2. Build normal MOD rows
        var normalMods = (inventory?.Mods ?? [])
            .Where(m => !ModPresentation.IsIndependentPatch(m))
            .ToArray();

        var allEdges = dependencyGraph?.Edges ?? [];
        var rows = new List<NeonWorkbenchRow>(normalMods.Length);

        foreach (var mod in normalMods)
        {
            var ownEdges = allEdges
                .Where(e => e.Dependent.Manifest.Equals(mod.Manifest, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var activeOwnIssues = ownEdges.Where(e => e.IsActiveIssue).ToArray();
            var inboundEdges = allEdges
                .Where(e => e.DependencyId.Equals(mod.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var inboundActiveBroken = inboundEdges.Where(e => e.IsActiveIssue).ToArray();

            var ownDisabledPrereq = activeOwnIssues.FirstOrDefault(e => e.Status == ModDependencyStatus.DisabledPrerequisite);
            var ownMissing = activeOwnIssues.FirstOrDefault(e => e.Status == ModDependencyStatus.Missing);
            var ownVerEdge = activeOwnIssues.FirstOrDefault(e => e.Status == ModDependencyStatus.VersionMismatch);
            var ownOtherIssue = activeOwnIssues.FirstOrDefault(e =>
                e.Status is ModDependencyStatus.Duplicate or ModDependencyStatus.VersionUnknown or ModDependencyStatus.InvalidConstraint || e.IsCycle);

            var causingDisabledBlock = inboundActiveBroken.FirstOrDefault(e => e.Status == ModDependencyStatus.DisabledPrerequisite);

            NeonVersionMismatchInfo? ownVersionMismatch = null;
            if (ownVerEdge is { Prerequisite: not null })
            {
                bool isMax = string.IsNullOrWhiteSpace(ownVerEdge.MinimumVersion) && !string.IsNullOrWhiteSpace(ownVerEdge.MaximumVersion);
                string req = isMax ? ownVerEdge.MaximumVersion : ownVerEdge.MinimumVersion;
                string op = isMax ? "≤" : "≥";
                ownVersionMismatch = new NeonVersionMismatchInfo(
                    ownVerEdge.DependencyId,
                    ownVerEdge.DependencyName,
                    ownVerEdge.Prerequisite.Version,
                    req,
                    op,
                    ownVerEdge.FromPatchRule ? "已审核补丁规则" : "MOD 清单声明",
                    ownVerEdge.FromPatchRule);
            }

            // Determine highest required version among broken inbound edges (manifest declarations first, then reviewed patch rules)
            NeonVersionMismatchInfo? causingVersionMismatch = null;
            var inboundVerEdges = inboundActiveBroken.Where(e => e.Status == ModDependencyStatus.VersionMismatch).ToArray();
            if (inboundVerEdges.Length > 0)
            {
                ModDependencyEdge? strongest = null;
                foreach (var edge in inboundVerEdges)
                {
                    if (string.IsNullOrWhiteSpace(edge.MinimumVersion)) continue;
                    if (strongest == null ||
                        (NeonVersionDiff.CompareNumeric(strongest.MinimumVersion, edge.MinimumVersion) ?? 0) < 0)
                        strongest = edge;
                }
                strongest ??= inboundVerEdges[0];
                bool isMax = string.IsNullOrWhiteSpace(strongest.MinimumVersion) && !string.IsNullOrWhiteSpace(strongest.MaximumVersion);
                causingVersionMismatch = new NeonVersionMismatchInfo(
                    mod.Id,
                    mod.Name,
                    mod.Version,
                    isMax ? strongest.MaximumVersion : strongest.MinimumVersion,
                    isMax ? "≤" : "≥",
                    strongest.FromPatchRule ? "已审核补丁规则" : "下游 MOD 清单声明",
                    strongest.FromPatchRule);
            }
            else
            {
                var reviewedForMod = reviewedVersionRequirements
                    .Where(r => r.TargetModId.Equals(mod.Id, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (reviewedForMod.Length > 0)
                {
                    var highest = reviewedForMod[0];
                    foreach (var item in reviewedForMod.Skip(1))
                    {
                        if ((NeonVersionDiff.CompareNumeric(highest.RequiredVer, item.RequiredVer) ?? 0) < 0)
                            highest = item;
                    }
                    causingVersionMismatch = new NeonVersionMismatchInfo(
                        mod.Id,
                        mod.Name,
                        mod.Version,
                        highest.RequiredVer,
                        "≥",
                        $"已审核补丁规则（{highest.PatchName}）",
                        true);
                }
            }

            // Sources & cached update results (exact same validation rules as AtlasNativeWindow)
            string? repo = null;
            ulong? wsId = null;
            GitHubModResult? ghRes = null;
            WorkshopModUpdateResult? wsRes = null;
            try { repo = githubUpdates?.Repository(mod.Id); } catch { }
            try { wsId = workshopSources?.Get(mod.Id); } catch { }
            try { ghRes = githubUpdates?.Cached(mod.Id); } catch { }
            try { wsRes = workshopUpdates?.Cached(mod.Id); } catch { }

            if (ghRes != null && (ghRes.LocalVersion != mod.Version ||
                !ghRes.Repository.Equals(repo, StringComparison.OrdinalIgnoreCase)))
                ghRes = null;
            if (wsRes != null && (wsRes.LocalVersion != mod.Version || wsRes.WorkshopId != wsId))
                wsRes = null;

            bool isUnbound = repo == null && !wsId.HasValue;
            NeonCandidateSource? candidate = null;
            if (isUnbound)
            {
                var discovered = githubDiscovery?.Cached(mod);
                var topGh = discovered?.Candidates.FirstOrDefault();
                if (topGh != null)
                {
                    candidate = new NeonCandidateSource(
                        topGh.Verified ? "GitHub 远端清单 ID 已核验" : "GitHub 候选来源",
                        "GitHub: " + topGh.Repository,
                        topGh.Reason,
                        topGh.Repository,
                        null,
                        topGh.Verified,
                        !topGh.Verified);
                }
                else
                {
                    var coverIdx = WorkshopCoverIndex.Match(mod, workshopSources, duplicateIds);
                    if (coverIdx != null)
                    {
                        candidate = new NeonCandidateSource(
                            coverIdx.NeedsReview ? "公共索引候选 · 同名需人工核对" : "公共封面索引候选",
                            $"Workshop #{coverIdx.WorkshopId}",
                            coverIdx.NeedsReview
                                ? "公共来源索引命中泛化或同名条目，请核对作者一致后再手动确认绑定。"
                                : "公共来源索引匹配同名同作者条目，需手动确认后绑定为更新来源。",
                            null,
                            coverIdx.WorkshopId,
                            !coverIdx.NeedsReview,
                            coverIdx.NeedsReview);
                    }
                }
            }

            string sourceMuted = repo != null && wsId.HasValue ? $"GitHub · WS #{wsId}"
                : repo != null ? "GitHub"
                : wsId.HasValue ? $"WS #{wsId}"
                : "未关联来源";

            bool hasUpdate = mod.Enabled && (ghRes?.Status == "有更新" || wsRes?.Status == "有更新");
            bool hasDanger = ownDisabledPrereq != null || ownMissing != null || ownVersionMismatch != null
                || ownOtherIssue != null || causingDisabledBlock != null;
            bool hasWarn = causingVersionMismatch != null || hasUpdate;

            int priority = 0;
            if (causingDisabledBlock != null) priority += 120;
            if (ownDisabledPrereq != null || ownMissing != null || ownOtherIssue != null) priority += 110;
            if (causingVersionMismatch != null) priority += 100;
            if (ownVersionMismatch != null) priority += 95;
            if (hasUpdate) priority += 60;
            if (isUnbound && candidate != null) priority += 20;
            if (!mod.Enabled && causingDisabledBlock == null) priority -= 30;

            string? blockReason = null;
            if (gameRunning)
                blockReason = "检测到游戏正在运行（SlayTheSpire2.exe），请先完全退出游戏后再移动 MOD 文件。";
            else if (modInventoryDirty)
                blockReason = "MOD 文件发生变化，请刷新后再操作。";
            else if (hasRoot && inventory != null)
            {
                try { blockReason = ModManager.BlockReason(gameRoot!, mod, inventory); }
                catch (Exception ex) { blockReason = ex.Message; }
            }
            bool isLockedByDependents = mod.Enabled && !gameRunning && !modInventoryDirty && blockReason != null;

            // Deferred to Inspector background lookup so building the 50+ MOD snapshot never walks disk on UI thread.
            const int fileCount = 0;
            const long totalBytes = 0;

            var relatedPatches = relatedPatchMap.TryGetValue(mod.Id, out var rlist)
                ? rlist
                    .OrderByDescending(p => p.CanRetire ? 5
                        : p.IsVerifiedHealthy ? 4
                        : modsById.ContainsKey(p.PatchId) ? 3
                        : p.StatusLabel is "待修复" or "需联动" ? 2
                        : 1)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()
                : [];

            rows.Add(new NeonWorkbenchRow(
                Mod: mod,
                Category: ClassifyCategory(mod),
                SourceMuted: sourceMuted,
                GitHubRepo: repo,
                GitHubResult: ghRes,
                WorkshopId: wsId,
                WorkshopResult: wsRes,
                CandidateSource: candidate,
                OwnEdges: ownEdges,
                ActiveOwnIssues: activeOwnIssues,
                InboundEdges: inboundEdges,
                InboundActiveBroken: inboundActiveBroken,
                OwnDisabledPrereq: ownDisabledPrereq,
                OwnMissing: ownMissing,
                OwnOtherIssue: ownOtherIssue,
                CausingDisabledBlock: causingDisabledBlock,
                OwnVersionMismatch: ownVersionMismatch,
                CausingVersionMismatch: causingVersionMismatch,
                RelatedPatches: relatedPatches,
                ToggleBlockReason: blockReason,
                IsLockedByDependents: isLockedByDependents,
                HasUpdate: hasUpdate,
                IsUnbound: isUnbound,
                HasDanger: hasDanger,
                HasWarn: hasWarn,
                HasAnyAttention: hasDanger || hasWarn,
                PriorityScore: priority,
                FileCount: fileCount,
                TotalBytes: totalBytes));
        }

        // 3. Build Launch Gate Faults (Root-cause grouped, never noisy spam!)
        var faults = new List<NeonFaultChip>();
        int hardBlockCount = 0;

        if (inventory is { Issues.Count: > 0 })
        {
            hardBlockCount += inventory.Issues.Count;
            faults.Add(new NeonFaultChip(
                "fault-inventory-issues",
                NeonTone.Cinnabar,
                "清单读取异常",
                inventory.Issues[0],
                "核对清单",
                null,
                null));
        }

        var activeIssueEdges = allEdges.Where(e => e.IsActiveIssue).ToArray();

        // 3a. Disabled prerequisites
        foreach (var group in activeIssueEdges
            .Where(e => e.Status == ModDependencyStatus.DisabledPrerequisite)
            .GroupBy(e => e.DependencyId, StringComparer.OrdinalIgnoreCase))
        {
            hardBlockCount++;
            var modVictims = group.Where(e => !ModPresentation.IsIndependentPatch(e.Dependent)).ToArray();
            var patchVictims = group.Where(e => ModPresentation.IsIndependentPatch(e.Dependent)).ToArray();
            string focusId = modVictims.FirstOrDefault()?.Dependent.Id ?? group.Key;
            var parts = new List<string>();
            if (modVictims.Length > 0)
                parts.Add(string.Join("、", modVictims.Take(2).Select(e => ShortModName(e.Dependent.Name))));
            if (patchVictims.Length > 0)
                parts.Add($"{patchVictims.Length} 项补丁");
            string victims = parts.Count > 0 ? string.Join(" 与 ", parts) : group.First().Dependent.Name;

            faults.Add(new NeonFaultChip(
                $"fault-disabled-{group.Key}",
                NeonTone.Cinnabar,
                "前置已停用",
                $"{group.Key} 当前停用 → 阻断 {victims}",
                "定位修复",
                focusId,
                null));
        }

        // 3b. Version mismatches (manifest declarations + reviewed patch rules)
        var verGroups = activeIssueEdges
            .Where(e => e.Status == ModDependencyStatus.VersionMismatch && e.Prerequisite != null)
            .GroupBy(e => e.DependencyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var handledVerPrereqs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (prereqId, edges) in verGroups)
        {
            handledVerPrereqs.Add(prereqId);
            hardBlockCount++;
            var prereqRow = rows.FirstOrDefault(r => r.Mod.Id.Equals(prereqId, StringComparison.OrdinalIgnoreCase));
            var mismatch = prereqRow?.CausingVersionMismatch ?? new NeonVersionMismatchInfo(
                prereqId,
                edges[0].DependencyName,
                edges[0].Prerequisite!.Version,
                edges[0].MinimumVersion.Length > 0 ? edges[0].MinimumVersion : edges[0].MaximumVersion,
                edges[0].MinimumVersion.Length > 0 ? "≥" : "≤",
                edges[0].FromPatchRule ? "已审核补丁规则" : "MOD 清单声明",
                edges[0].FromPatchRule);

            var modVictims = edges.Where(e => !ModPresentation.IsIndependentPatch(e.Dependent)).ToArray();
            var patchVictims = edges.Where(e => ModPresentation.IsIndependentPatch(e.Dependent)).ToArray();
            var reviewedPatchCount = reviewedVersionRequirements
                .Count(r => r.TargetModId.Equals(prereqId, StringComparison.OrdinalIgnoreCase));
            int totalPatches = Math.Max(patchVictims.Length, reviewedPatchCount);

            var parts = new List<string>();
            if (modVictims.Length > 0)
                parts.Add(string.Join("、", modVictims.Take(2).Select(e => ShortModName(e.Dependent.Name))));
            if (totalPatches > 0)
                parts.Add($"{totalPatches} 项补丁");
            string tail = parts.Count > 0 ? " · 影响 " + string.Join(" 与 ", parts) : "";

            bool canPreviewUpdate = prereqRow?.HasUpdate == true;
            faults.Add(new NeonFaultChip(
                $"fault-ver-{prereqId}",
                NeonTone.Amber,
                "版本差影响适配",
                $"{prereqId} v{NeonVersionDiff.StripPrefix(mismatch.ActualVersion)} → 需 {mismatch.Operator} {NeonVersionDiff.StripPrefix(mismatch.RequiredVersion)}{tail}",
                canPreviewUpdate ? "预览更新" : "查看详情",
                prereqId,
                null,
                OpenUpdatePreview: canPreviewUpdate,
                VersionDiff: mismatch,
                VersionDiffTail: tail));
        }

        // Also include reviewed patch version drifts whose prerequisite didn't already have a manifest mismatch
        foreach (var group in reviewedVersionRequirements
            .Where(r => !handledVerPrereqs.Contains(r.TargetModId))
            .GroupBy(r => r.TargetModId, StringComparer.OrdinalIgnoreCase))
        {
            hardBlockCount++;
            var prereqRow = rows.FirstOrDefault(r => r.Mod.Id.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            var mismatch = prereqRow?.CausingVersionMismatch;
            if (mismatch == null) continue;
            string tail = $" · 影响 {group.Count()} 项已审核补丁";
            bool canPreviewUpdate = prereqRow?.HasUpdate == true;
            faults.Add(new NeonFaultChip(
                $"fault-ver-patch-{group.Key}",
                NeonTone.Amber,
                "版本差影响适配",
                $"{group.Key} v{NeonVersionDiff.StripPrefix(mismatch.ActualVersion)} → 需 {mismatch.Operator} {NeonVersionDiff.StripPrefix(mismatch.RequiredVersion)}{tail}",
                canPreviewUpdate ? "预览更新" : "查看详情",
                group.Key,
                null,
                OpenUpdatePreview: canPreviewUpdate,
                VersionDiff: mismatch,
                VersionDiffTail: tail));
        }

        // 3c. Missing / Cycle / Duplicate / Invalid constraints
        foreach (var group in activeIssueEdges
            .Where(e => e.Status is ModDependencyStatus.Missing or ModDependencyStatus.Duplicate
                or ModDependencyStatus.VersionUnknown or ModDependencyStatus.InvalidConstraint || e.IsCycle)
            .GroupBy(e => e.DependencyId, StringComparer.OrdinalIgnoreCase))
        {
            hardBlockCount++;
            var first = group.First();
            string kicker = first.IsCycle ? "循环依赖"
                : first.Status == ModDependencyStatus.Missing ? "缺少必需前置"
                : ModDependencyGraphPresentation.Status(first.Status);
            string desc = first.Status == ModDependencyStatus.Missing
                ? $"未安装 {group.Key} → 影响 {string.Join("、", group.Take(2).Select(e => ShortModName(e.Dependent.Name)))}"
                : $"{first.Dependent.Name} · {first.Message}";
            faults.Add(new NeonFaultChip(
                $"fault-other-{group.Key}",
                NeonTone.Cinnabar,
                kicker,
                desc,
                "查看详情",
                first.Dependent.Id,
                null));
        }

        // 3d. Pending patches (only from valid scan / lifecycle report; verified-retirable patches live in Tier 2!)
        var pendingPatches = isPatchScanValid
            ? catalogPatches.Where(p =>
                !p.CanRetire && (
                    p.ScanState?.Status is "待修复" or "需联动" or "需核对" ||
                    (isLifecycleHealthValid && p.LifecycleItem?.State is LifecycleState.NeedsReview or LifecycleState.Incompatible))).ToArray()
            : [];

        if (pendingPatches.Length > 0)
        {
            var lead = pendingPatches[0];
            string extra = pendingPatches.Length > 1 ? $" 等 {pendingPatches.Length} 项" : "";
            string? leadTargetMod = lead.LifecycleItem?.RelatedMods.FirstOrDefault()
                ?? Catalog.PatchRelatedModIds(Catalog.Bundled.First(x => x.ComponentId.Equals(lead.PatchId, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(id => rows.Any(r => r.Mod.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
            faults.Add(new NeonFaultChip(
                "fault-patches",
                NeonTone.Indigo,
                lead.StatusLabel == "需核对" ? "补丁待核对" : "待处理修复补丁",
                $"{lead.Name}{extra}",
                "前往处理",
                null,
                "patches",
                LeadPatch: lead));
        }

        int verifiedInstalledCount = isLifecycleHealthValid
            ? catalogPatches.Count(p => p.IsVerifiedHealthy)
            : 0;
        int retirableCount = isLifecycleHealthValid
            ? catalogPatches.Count(p => p.CanRetire)
            : 0;
        int availableUpdatesCount = rows.Count(r => r.HasUpdate);

        var filterCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["all"] = rows.Count,
            ["issues"] = rows.Count(r => r.HasAnyAttention),
            ["char-content"] = rows.Count(r => r.Category is NeonModCategory.Char or NeonModCategory.Content),
            ["lib"] = rows.Count(r => r.Category == NeonModCategory.Lib),
            ["util"] = rows.Count(r => r.Category == NeonModCategory.Util),
            ["disabled"] = rows.Count(r => !r.Mod.Enabled),
            ["unbound"] = rows.Count(r => r.IsUnbound)
        };

        NeonGateMode mode = !hasRoot ? NeonGateMode.NoGameRoot
            : hardBlockCount > 0 ? NeonGateMode.Blocked
            : faults.Count > 0 ? NeonGateMode.Advisory
            : NeonGateMode.Ready;

        var defaultCoop = coopState ?? new NeonCoopSnapshotState(
            HasPeer: false,
            LocalScanTimeLabel: patchScanReport?.Time.ToString("yyyy-MM-dd HH:mm:ss"),
            PeerLabel: null,
            PeerTimeLabel: null,
            PeerManagerVersion: null,
            PeerIsLegacy: false,
            ComparisonSummary: null,
            Rows: [],
            LastOutcome: null,
            StatusText: "");

        var defaultRecovery = recoveryState ?? new NeonRecoverySnapshotState(
            Snapshots: [],
            SnapshotsWarning: null,
            LatestPatchBatch: ReadLatestPatchBackupBatch(gameRoot),
            DisabledAllMods: (inventory?.Mods ?? []).Where(m => !m.Enabled).ToArray(),
            ActivityLog: []);

        return new NeonWorkbenchSnapshot(
            GameRoot: gameRoot,
            GameRunning: gameRunning,
            HasInventory: hasInventory,
            ModInventoryDirty: modInventoryDirty,
            LifecycleStale: lifecycleStale,
            LifecycleScanning: lifecycleScanning,
            LifecycleStatus: lifecycleStatus,
            IsPatchScanValid: isPatchScanValid,
            IsLifecycleHealthValid: isLifecycleHealthValid,
            LastScanTime: patchScanReport?.Time,
            GateMode: mode,
            HardBlockCount: hardBlockCount,
            Faults: faults.ToArray(),
            AllRows: rows.ToArray(),
            DependencyGraph: dependencyGraph,
            AllCatalogPatches: catalogPatches.ToArray(),
            EnabledModsCount: rows.Count(r => r.Mod.Enabled),
            PendingPatchesCount: pendingPatches.Length,
            VerifiedInstalledPatchesCount: verifiedInstalledCount,
            RetirablePatchesCount: retirableCount,
            AvailableUpdatesCount: availableUpdatesCount,
            FilterCounts: filterCounts,
            OrphanAutoStopEnabled: orphanAutoStopEnabled,
            OnlineRefreshing: onlineRefreshing,
            OnlineSourceStatus: onlineSourceStatus,
            OnlineCatalogCount: onlineCatalogCount > 0 ? onlineCatalogCount : Catalog.Bundled.Length,
            CheckingModUpdates: checkingModUpdates,
            LastUpdateCheckTimeLabel: lastUpdateCheckTimeLabel,
            Coop: defaultCoop,
            Recovery: defaultRecovery,
            ManagerUpdate: managerUpdateState,
            GitHubNetwork: githubNetworkState,
            IsScanning: isScanning,
            ScanStageText: scanStageText);
    }

    internal static NeonPatchBackupBatch? ReadLatestPatchBackupBatch(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return null;
        try
        {
            string parent = Path.Combine(gameRoot, "mod_fix_backups");
            if (!Directory.Exists(parent)) return null;
            var dirs = Directory.EnumerateDirectories(parent, "STS2-AllFixes-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .ToArray();
            foreach (string dir in dirs)
            {
                string restorePath = Path.Combine(dir, "restore.json");
                if (!File.Exists(restorePath)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(restorePath));
                var root = doc.RootElement;
                string status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString() ?? ""
                    : "";
                if (!status.Equals("installed", StringComparison.OrdinalIgnoreCase)) continue;

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool hasRuntime = false;
                if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in filesEl.EnumerateArray())
                    {
                        string rel = f.TryGetProperty("relative", out var r) && r.ValueKind == JsonValueKind.String
                            ? r.GetString() ?? ""
                            : "";
                        if (rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                            rel.EndsWith(".pck", StringComparison.OrdinalIgnoreCase) ||
                            rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            hasRuntime = true;
                        string folder = Path.GetDirectoryName(rel.Replace('/', '\\')) ?? rel;
                        string leaf = Path.GetFileName(folder.TrimEnd('\\'));
                        if (!string.IsNullOrWhiteSpace(leaf))
                        {
                            var match = Catalog.All.FirstOrDefault(p => p.Id.Equals(leaf, StringComparison.OrdinalIgnoreCase));
                            names.Add(match?.Name ?? leaf);
                        }
                    }
                }
                if (names.Count == 0) names.Add(Path.GetFileName(dir));
                return new NeonPatchBackupBatch(
                    FolderName: Path.GetFileName(dir),
                    FullPath: dir,
                    RelativePath: Path.GetRelativePath(gameRoot, dir).Replace('\\', '/'),
                    Time: Directory.GetLastWriteTime(dir),
                    AffectedNames: names.ToArray(),
                    DocsOnly: !hasRuntime);
            }
        }
        catch { }
        return null;
    }

    internal static NeonWorkbenchRow[] FilterAndSort(
        NeonWorkbenchRow[] rows,
        string activeFilter,
        string searchQuery,
        string sortBy)
    {
        string q = (searchQuery ?? "").Trim();
        IEnumerable<NeonWorkbenchRow> filtered = rows;

        filtered = activeFilter switch
        {
            "issues" => filtered.Where(r => r.HasAnyAttention),
            "char-content" => filtered.Where(r => r.Category is NeonModCategory.Char or NeonModCategory.Content),
            "lib" => filtered.Where(r => r.Category == NeonModCategory.Lib),
            "util" => filtered.Where(r => r.Category == NeonModCategory.Util),
            "disabled" => filtered.Where(r => !r.Mod.Enabled),
            "unbound" => filtered.Where(r => r.IsUnbound),
            _ => filtered
        };

        if (q.Length > 0)
        {
            filtered = filtered.Where(r =>
            {
                string deps = string.Join(" ", r.Mod.Dependencies.Select(d => d.Id));
                string hay = $"{r.Mod.Name} {r.Mod.Id} {r.Mod.Author} {r.Mod.Description} {deps}";
                return hay.Contains(q, StringComparison.OrdinalIgnoreCase);
            });
        }

        return sortBy switch
        {
            "name" => filtered.OrderBy(r => r.Mod.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            "enabled" => filtered.OrderByDescending(r => r.Mod.Enabled).ThenBy(r => r.Mod.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            _ => filtered.OrderByDescending(r => r.PriorityScore).ThenBy(r => r.Mod.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
        };
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
        if (bytes >= 1024L) return Math.Round(bytes / 1024.0).ToString("F0") + " KB";
        return bytes + " B";
    }

    static string ShortModName(string full)
    {
        int sep = full.IndexOf(" · ", StringComparison.Ordinal);
        return sep > 0 ? full[..sep] : full;
    }
}
