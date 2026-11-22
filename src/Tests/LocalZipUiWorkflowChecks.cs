using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

/// <summary>
/// Exercises the local ZIP UI boundary with isolated fixture roots and in-memory
/// source-cache probes. No real game, WPF window, or network client is used.
/// </summary>
internal static class LocalZipUiWorkflowChecks
{
    const string Manifest =
        """{"id":"fixture.ui","name":"UI Fixture","version":"1.0.0","author":"Tester","description":"UI workflow fixture","has_dll":true,"dependencies":[{"id":"fixture.base","min_version":"1.0","optional":false}]}""";

    sealed class RuntimeCacheBackend : IModSourceCheckBackend, IModSourceRuntimeCacheProbe
    {
        readonly bool? github;
        readonly bool? workshop;

        public RuntimeCacheBackend(bool? github, bool? workshop)
        {
            this.github = github;
            this.workshop = workshop;
        }

        public (bool? GitHubCached, bool? WorkshopCached) ReadRuntimeCachePresence(string modId) =>
            (github, workshop);

        public ModSourceBackendSnapshot Read(ManagedMod mod) =>
            new(null, null, null, null, null, WorkshopBaselineState.Unbound);

        public Task<ModSourceBackendDiscoveryResult> DiscoverGitHubAsync(
            ManagedMod mod, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("UI cache probe must not perform discovery.");

        public Task<ModSourceBackendGitHubCheck> CheckGitHubAsync(
            ManagedMod mod, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("UI cache probe must not perform an online check.");

        public Task<ModSourceBackendWorkshopCheck> CheckWorkshopAsync(
            ManagedMod mod, ulong workshopId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("UI cache probe must not perform an online check.");
    }

    static string Fixture(string parent, string name)
    {
        string root = Path.Combine(parent, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        File.WriteAllText(Path.Combine(root, "SlayTheSpire2.exe"), "fixture-only-not-a-game");
        return root;
    }

    static string CreateZip(string parent, string name = "Sample")
    {
        string zip = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".zip");
        using var output = File.Create(zip);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry(name + "/mod_manifest.json", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
            writer.Write(Manifest);
        var dll = archive.CreateEntry(name + "/fixture.dll", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(dll.Open(), new UTF8Encoding(false)))
            writer.Write("fixture-payload");
        return zip;
    }

    static string RootKey(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())));

    static (string GitHub, string Workshop, string Baseline) AddHistoricalSources(
        string dataRoot, string gameRoot)
    {
        string key = RootKey(gameRoot);
        string githubDirectory = Path.Combine(dataRoot, "GitHub模组更新");
        string workshopDirectory = Path.Combine(dataRoot, "创意工坊来源");
        Directory.CreateDirectory(githubDirectory);
        Directory.CreateDirectory(workshopDirectory);
        string github = Path.Combine(githubDirectory, key + ".json");
        string workshop = Path.Combine(workshopDirectory, key + ".json");
        string baseline = Path.Combine(workshopDirectory, key + ".versions.json");
        File.WriteAllText(github, """{"fixture.ui":"fixture/old-repository"}""");
        File.WriteAllText(workshop, """{"fixture.ui":"123456789"}""");
        File.WriteAllText(baseline,
            """{"fixture.ui":{"WorkshopId":123456789,"ManifestId":"123456789","LocalVersion":"1.0.0","LocalSnapshotSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","RecordedAt":"2026-11-23T00:00:00+00:00"}}""");
        return (github, workshop, baseline);
    }

    static void AddExistingMod(string root)
    {
        string folder = Path.Combine(root, "mods", "existing");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod_manifest.json"), Manifest);
        File.WriteAllText(Path.Combine(folder, "fixture.dll"), "existing-payload");
    }

    static LocalZipUiWorkflowSession CreateSession(
        string parent,
        string dataRoot,
        Func<string, (bool? GitHubCached, bool? WorkshopCached)> cacheProbe,
        ILocalModZipInstallTransactionBackend? transactionBackend = null)
    {
        string staging = Path.Combine(parent, "staging-" + Guid.NewGuid().ToString("N"));
        var archiveBackend = new LocalModZipArchiveBackend(cacheProbe);
        var preview = new LocalModZipImportWorkflowService(
            dataRoot, archiveBackend, staging, _ => false);
        return new LocalZipUiWorkflowSession(preview, dataRoot,
            transactionBackend ?? new LocalModZipInstallTransactionBackend(dataRoot, _ => false));
    }

    static bool HasNoInvalidation(LocalZipUiInvalidationPlan plan) =>
        !plan.RefreshInventory && !plan.InvalidateSourceCache && !plan.InvalidatePatchLifecycle;

    public static async Task RunAsync(string home, Action<bool, string> check)
    {
        string parent = Path.Combine(home, "local-zip-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);

        // The same read-only source-check probe used by the UI is passed into the
        // archive backend, then its result is frozen into the preview token.
        string cacheRoot = Fixture(parent, "cache-probe-game");
        string cacheData = Path.Combine(parent, "cache-probe-data");
        var sourceChecks = new ModSourceCheckWorkflowService(
            cacheRoot, new RuntimeCacheBackend(true, false));
        int probeCalls = 0;
        var cacheProbe = new ModSourceWorkflowLocalZipCacheProbe(sourceChecks);
        var cacheBackend = new LocalModZipArchiveBackend(modId =>
        {
            probeCalls++;
            return cacheProbe.Inspect(modId);
        });
        var cacheWorkflow = new LocalModZipImportWorkflowService(
            cacheData, cacheBackend,
            Path.Combine(parent, "cache-probe-staging"), _ => false);
        var cacheSession = new LocalZipUiWorkflowSession(cacheWorkflow, cacheData);
        var cachePreviewResult = await cacheSession.PreviewAsync(cacheRoot, CreateZip(parent));
        var cachePreview = cachePreviewResult.Preview ??
            throw new InvalidOperationException("Runtime cache fixture did not preview.");
        check(probeCalls >= 1 &&
            cachePreview.HistoricalSources.GitHubCached == true &&
            cachePreview.HistoricalSources.WorkshopCached == false &&
            cachePreview.HistoricalSources.RuntimeCacheNotice ==
                "已取得运行时 GitHub/Workshop 缓存状态。",
            "old GitHub/Workshop runtime-cache presence is injected through ModSourceCheckWorkflowService without network access");
        cacheSession.DiscardPreview();

        // A blocked preview still presents all source evidence and all three explicit
        // policies, but the presentation model never enables the install action.
        string conflictRoot = Fixture(parent, "conflict-game");
        string conflictData = Path.Combine(parent, "conflict-data");
        AddExistingMod(conflictRoot);
        AddHistoricalSources(conflictData, conflictRoot);
        var conflictSession = CreateSession(parent, conflictData, _ => (false, false));
        var conflictResult = await conflictSession.PreviewAsync(conflictRoot, CreateZip(parent));
        var conflictPreview = conflictResult.Preview ??
            throw new InvalidOperationException("Conflict fixture did not produce a read-only preview.");
        var presentation = LocalZipConfirmationPresenter.Create(conflictPreview);
        check(conflictResult.State == LocalZipPreviewState.Blocked &&
            !presentation.CanConfirm &&
            presentation.ConflictDetails.Any(line => line.Contains("相同 MOD ID 已启用", StringComparison.Ordinal)) &&
            presentation.HasHistoricalEvidence &&
            presentation.HistoricalEvidence.Any(line => line.Contains("fixture/old-repository", StringComparison.Ordinal)) &&
            presentation.HistoricalEvidence.Any(line => line.Contains("123456789", StringComparison.Ordinal)) &&
            presentation.HistoricalEvidence.Any(line => line.Contains("Workshop 版本基线：存在", StringComparison.Ordinal)) &&
            presentation.AvailablePolicies.Contains(LocalZipHistoricalBindingPolicy.PreserveUnverified) &&
            presentation.AvailablePolicies.Contains(LocalZipHistoricalBindingPolicy.RequireManualReview) &&
            presentation.AvailablePolicies.Contains(LocalZipHistoricalBindingPolicy.Unbind),
            "confirmation presentation shows concrete conflict blockers, historical GitHub/Workshop evidence and all three policies");
        check(presentation.ModName == "UI Fixture" && presentation.ModId == "fixture.ui" &&
            presentation.Version == "1.0.0" && presentation.Author == "Tester" &&
            presentation.Description == "UI workflow fixture" && presentation.FileCount == 2 &&
            presentation.TotalBytes > 0 && presentation.ZipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            presentation.ArchiveSha256.Length == 64 &&
            presentation.TargetDirectory.Contains("Sample", StringComparison.Ordinal) &&
            presentation.FolderNameReason.Length > 0 && presentation.Dependencies.Count == 1,
            "confirmation presentation includes package identity, author, version, file count, ZIP hash, target reasoning and dependencies");
        string conflictWorkspace = conflictPreview.Package.WorkspaceDirectory;
        conflictSession.DiscardPreview();
        check(!Directory.Exists(conflictWorkspace),
            "cancelling a blocked confirmation discards its isolated staging workspace");

        // Directory switching and selecting a replacement ZIP both discard the prior
        // preview workspace; neither action emits installation invalidation signals.
        string lifecycleRoot = Fixture(parent, "lifecycle-game");
        string nextRoot = Fixture(parent, "next-game");
        string lifecycleData = Path.Combine(parent, "lifecycle-data");
        var lifecycleSession = CreateSession(parent, lifecycleData, _ => (false, false));
        var firstPreviewResult = await lifecycleSession.PreviewAsync(lifecycleRoot, CreateZip(parent, "First"));
        string firstWorkspace = firstPreviewResult.Preview!.Package.WorkspaceDirectory;
        lifecycleSession.ChangeRoot(nextRoot);
        check(!Directory.Exists(firstWorkspace) && lifecycleSession.CurrentPreview is null,
            "switching the selected game directory discards the old preview workspace");

        var secondPreviewResult = await lifecycleSession.PreviewAsync(lifecycleRoot, CreateZip(parent, "Second"));
        string secondWorkspace = secondPreviewResult.Preview!.Package.WorkspaceDirectory;
        var thirdPreviewResult = await lifecycleSession.PreviewAsync(lifecycleRoot, CreateZip(parent, "Third"));
        check(!Directory.Exists(secondWorkspace) && thirdPreviewResult.Preview is not null &&
            Directory.Exists(thirdPreviewResult.Preview.Package.WorkspaceDirectory),
            "choosing a replacement ZIP discards the previous workspace before staging the new archive");

        // A user decline is a no-write result, removes the preview workspace, and
        // produces no inventory/source/lifecycle invalidation.
        var declined = await lifecycleSession.ApplyAsync(
            (_, _) => Task.FromResult(new LocalZipApplyDecision(
                false, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(!declined.Success && declined.FailureKind == LocalZipApplyFailureKind.UserDeclined &&
            !Directory.Exists(thirdPreviewResult.Preview!.Package.WorkspaceDirectory) &&
            !Directory.Exists(thirdPreviewResult.Preview.DestinationDirectory) &&
            LocalZipUiResultPresenter.FormatApplyResult(declined).Contains("已取消", StringComparison.Ordinal) &&
            HasNoInvalidation(LocalZipUiInvalidationPlan.ForApply(declined)),
            "declining confirmation displays cancellation, discards staging and emits no success invalidation");

        // A real successful install passes the selected policy, exposes continuous
        // writing state, calls DeferQuietPeriod once and requests all three refreshes.
        string applyRoot = Fixture(parent, "apply-game");
        string applyData = Path.Combine(parent, "apply-data");
        var applySession = CreateSession(parent, applyData, _ => (false, false));
        await applySession.PreviewAsync(applyRoot, CreateZip(parent));
        var writingStates = new List<bool>();
        int deferCount = 0;
        var applied = await applySession.ApplyAsync(
            (confirmation, _) => Task.FromResult(new LocalZipApplyDecision(
                confirmation.Preview.CanInstallNew,
                LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => deferCount++,
            progress => writingStates.Add(progress.TransactionWriting));
        var applyPlan = LocalZipUiInvalidationPlan.ForApply(applied);
        check(applied.Success && !applied.PartialSuccess &&
            File.Exists(Path.Combine(applied.TargetDirectory!, "fixture.dll")) &&
            writingStates.Contains(true) && writingStates.Last() == false &&
            deferCount == 1,
            "confirmed UI apply delegates to the transaction service, brackets file writing and defers the watcher quiet period");
        check(applyPlan.RefreshInventory && applyPlan.InvalidateSourceCache &&
            applyPlan.InvalidatePatchLifecycle,
            "only a verified successful installation requests inventory, source-cache and patch-lifecycle invalidation");

        // A historical binding policy selected by the confirmation callback is passed
        // unchanged to batch 9. PreserveUnverified never upgrades source trust.
        string historyRoot = Fixture(parent, "history-game");
        string historyData = Path.Combine(parent, "history-data");
        var historyFiles = AddHistoricalSources(historyData, historyRoot);
        var historySession = CreateSession(parent, historyData, _ => (false, false));
        await historySession.PreviewAsync(historyRoot, CreateZip(parent));
        var preserved = await historySession.ApplyAsync(
            (confirmation, _) => Task.FromResult(new LocalZipApplyDecision(
                confirmation.HistoricalSources.HasHistoricalEvidence,
                LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(preserved.Success && preserved.SourceReviewRequired &&
            File.ReadAllText(historyFiles.GitHub).Contains("fixture/old-repository", StringComparison.Ordinal),
            "PreserveUnverified is forwarded explicitly and retains the old binding without granting trust");

        // RequireManualReview is forwarded as a distinct explicit policy and leaves
        // historical source records intact while surfacing the review requirement.
        string reviewRoot = Fixture(parent, "review-game");
        string reviewData = Path.Combine(parent, "review-data");
        var reviewFiles = AddHistoricalSources(reviewData, reviewRoot);
        var reviewSession = CreateSession(parent, reviewData, _ => (false, false));
        await reviewSession.PreviewAsync(reviewRoot, CreateZip(parent));
        var reviewed = await reviewSession.ApplyAsync(
            (confirmation, _) => Task.FromResult(new LocalZipApplyDecision(
                confirmation.HistoricalSources.HasHistoricalEvidence,
                LocalZipHistoricalBindingPolicy.RequireManualReview)),
            () => { });
        check(reviewed.Success && reviewed.SourceReviewRequired &&
            File.ReadAllText(reviewFiles.GitHub).Contains("fixture/old-repository", StringComparison.Ordinal) &&
            File.Exists(reviewFiles.Baseline),
            "RequireManualReview preserves historical records but keeps the source explicitly untrusted pending manual review");

        // Partial source-unbind persistence failure remains a successful install with
        // a visible partial-success message and positive invalidation signals.
        string partialRoot = Fixture(parent, "partial-game");
        string partialData = Path.Combine(parent, "partial-data");
        var partialFiles = AddHistoricalSources(partialData, partialRoot);
        var partialBackend = new LocalModZipInstallTransactionBackend(
            partialData, _ => false,
            faultInjector: (point, detail) =>
                point == LocalZipInstallFaultPoint.SourceBindingWrite &&
                detail.Contains("GitHub模组更新", StringComparison.Ordinal)
                    ? new IOException("fixture GitHub binding persistence failure")
                    : null);
        var partialSession = CreateSession(parent, partialData, _ => (false, false), partialBackend);
        await partialSession.PreviewAsync(partialRoot, CreateZip(parent));
        var partial = await partialSession.ApplyAsync(
            (confirmation, _) => Task.FromResult(new LocalZipApplyDecision(
                confirmation.HistoricalSources.HasHistoricalEvidence,
                LocalZipHistoricalBindingPolicy.Unbind)),
            () => { });
        var partialPlan = LocalZipUiInvalidationPlan.ForApply(partial);
        string partialMessage = LocalZipUiResultPresenter.FormatApplyResult(partial);
        check(partial.Success && partial.PartialSuccess && partial.SourceBindingCleanupFailed &&
            partialPlan.RefreshInventory && partialPlan.InvalidateSourceCache &&
            partialPlan.InvalidatePatchLifecycle &&
            partialMessage.Contains("历史来源解绑", StringComparison.Ordinal) &&
            partial.TargetDirectory is not null && Directory.Exists(partial.TargetDirectory) &&
            File.ReadAllText(partialFiles.GitHub).Contains("fixture.ui", StringComparison.Ordinal),
            "failed historical unbind is presented as partial success while retaining the installed MOD and unresolved binding");

        // A matching target from an interrupted Committing transaction is a safe
        // recovery and therefore requests the same post-install invalidation as ApplyAsync.
        string safeRoot = Fixture(parent, "safe-recovery-game");
        string safeData = Path.Combine(parent, "safe-recovery-data");
        var safeBackend = new LocalModZipInstallTransactionBackend(safeData, _ => false);
        var safeSession = CreateSession(parent, safeData, _ => (false, false), safeBackend);
        var safePreviewResult = await safeSession.PreviewAsync(safeRoot, CreateZip(parent));
        LocalModZipPreview safePreview = safePreviewResult.Preview!;
        string safeTransactionId = Guid.NewGuid().ToString("N");
        LocalZipInstallTransactionPaths safePaths = safeBackend.CreatePaths(
            safeRoot, safePreview.DestinationDirectory, safeTransactionId,
            safePreview.Package.WorkspaceDirectory);
        await safeBackend.PrepareAsync(safePreview.Package, safePaths, CancellationToken.None);
        var safeHashes = safePreview.Package.Files.ToDictionary(
            item => item.RelativePath, item => item.Sha256, StringComparer.OrdinalIgnoreCase);
        safeBackend.WriteJournal(new LocalZipInstallTransactionJournal(
            1, safeTransactionId, Path.GetFullPath(safeRoot), safePreview.Package.ModId,
            safePreview.Package.DestinationFolderName, safePaths.TargetDirectory,
            safePaths.TransactionDirectory, safePaths.PreparedDirectory,
            safePaths.PreviewWorkspaceDirectory, "Committing", safeHashes,
            LocalZipHistoricalBindingPolicy.PreserveUnverified.ToString(),
            false, false, null, DateTimeOffset.UtcNow));
        Directory.Move(safePaths.PreparedDirectory, safePaths.TargetDirectory);
        var safeRecovery = safeSession.RecoverPendingTransactions(safeRoot);
        var safeRecoveryPlan = LocalZipUiInvalidationPlan.ForRecovery(safeRecovery);
        check(safeRecovery.CompletedCount == 1 && !safeRecovery.HasPendingRecovery &&
            safeRecoveryPlan.RefreshInventory && safeRecoveryPlan.InvalidateSourceCache &&
            safeRecoveryPlan.InvalidatePatchLifecycle &&
            Directory.Exists(safePaths.TargetDirectory),
            "a hash-matching interrupted install is safely completed and requests inventory, source-cache and lifecycle refresh");
        safeSession.DiscardPreview();

        // Seed a real Committing journal, move the prepared tree, then alter its
        // target. Recovery must report the exact path and preserve the external edit.
        string recoveryRoot = Fixture(parent, "recovery-game");
        string recoveryData = Path.Combine(parent, "recovery-data");
        var recoveryBackend = new LocalModZipInstallTransactionBackend(recoveryData, _ => false);
        var recoverySession = CreateSession(parent, recoveryData, _ => (false, false), recoveryBackend);
        var recoveryPreviewResult = await recoverySession.PreviewAsync(recoveryRoot, CreateZip(parent));
        LocalModZipPreview recoveryPreview = recoveryPreviewResult.Preview!;
        string transactionId = Guid.NewGuid().ToString("N");
        LocalZipInstallTransactionPaths paths = recoveryBackend.CreatePaths(
            recoveryRoot, recoveryPreview.DestinationDirectory, transactionId,
            recoveryPreview.Package.WorkspaceDirectory);
        await recoveryBackend.PrepareAsync(recoveryPreview.Package, paths, CancellationToken.None);
        var expectedHashes = recoveryPreview.Package.Files.ToDictionary(
            item => item.RelativePath, item => item.Sha256, StringComparer.OrdinalIgnoreCase);
        var journal = new LocalZipInstallTransactionJournal(
            1, transactionId, Path.GetFullPath(recoveryRoot), recoveryPreview.Package.ModId,
            recoveryPreview.Package.DestinationFolderName, paths.TargetDirectory,
            paths.TransactionDirectory, paths.PreparedDirectory, paths.PreviewWorkspaceDirectory,
            "Committing", expectedHashes,
            LocalZipHistoricalBindingPolicy.PreserveUnverified.ToString(),
            false, false, null, DateTimeOffset.UtcNow);
        recoveryBackend.WriteJournal(journal);
        Directory.Move(paths.PreparedDirectory, paths.TargetDirectory);
        string externalFile = Path.Combine(paths.TargetDirectory, "external-change.txt");
        File.WriteAllText(externalFile, "preserve-this-external-change");

        var recovery = recoverySession.RecoverPendingTransactions(recoveryRoot);
        var recoveryPlan = LocalZipUiInvalidationPlan.ForRecovery(recovery);
        string recoveryMessage = LocalZipUiResultPresenter.FormatRecoveryReport(
            recovery, recoverySession.TransactionLogDirectory) ?? "";
        check(recovery.HasPendingRecovery &&
            recovery.Items.Single(item=>item.Pending).State == "RecoveryRequired" &&
            recovery.Items.Single(item=>item.Pending).ModId == recoveryPreview.Package.ModId &&
            recovery.Items.Single(item=>item.Pending).TargetDirectory == paths.TargetDirectory &&
            recoveryMessage.Contains(paths.TargetDirectory, StringComparison.Ordinal) &&
            recoveryMessage.Contains(recoverySession.TransactionLogDirectory, StringComparison.Ordinal) &&
            HasNoInvalidation(recoveryPlan) &&
            File.ReadAllText(externalFile) == "preserve-this-external-change" &&
            Directory.Exists(paths.TargetDirectory),
            "RecoveryRequired displays the exact target and journal paths, emits no success invalidation, and preserves the mismatched target untouched");

        // The recovery notice formatter distinguishes a completed recovery from a
        // pending manual-review state; UI presentation itself performs no deletion.
        check(LocalZipUiResultPresenter.FormatApplyResult(new LocalZipApplyResult(
            true, false, false, LocalZipApplyFailureKind.RecoveryRequired,
            "目标目录哈希不一致。", LocalZipInstallRecoveryState.PendingRecovery,
            transactionId, paths.TargetDirectory, LocalZipHistoricalBindingPolicy.PreserveUnverified,
            "未处理历史来源绑定", false, false, false, false, false, false,
            false, "目标目录已保留。")).Contains("不会在 UI 层自动删除或覆盖", StringComparison.Ordinal),
            "RecoveryRequired apply feedback explicitly prohibits UI-side target deletion or overwrite");

        // Keep the fixture cleanup explicit and scoped to manager-owned staging only.
        cacheSession.DiscardPreview();
        conflictSession.DiscardPreview();
        historySession.DiscardPreview();
        reviewSession.DiscardPreview();
        partialSession.DiscardPreview();
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }
}
