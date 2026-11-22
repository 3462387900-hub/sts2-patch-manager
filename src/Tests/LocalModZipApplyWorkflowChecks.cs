using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

/// <summary>
/// Isolated filesystem fixtures only. No real game discovery and no network calls.
/// </summary>
internal static class LocalModZipApplyWorkflowChecks
{
    const string Manifest =
        """{"id":"fixture.apply","name":"Apply Fixture","version":"1.0.0","has_dll":true}""";

    sealed record Entry(string Path, string Content);

    sealed class FailingBackend : ILocalModZipInstallTransactionBackend
    {
        readonly LocalModZipInstallTransactionBackend inner;
        public string TransactionLogDirectory => inner.TransactionLogDirectory;
        public FailingBackend(string dataRoot,
            Func<LocalZipInstallFaultPoint, string, Exception?> fault,
            bool lockAvailable = true)
        {
            inner = new(dataRoot, _ => false, _ => lockAvailable, faultInjector:
                (point, detail) => fault(point, detail));
        }
        public LocalZipInstallTransactionPaths CreatePaths(string root, string target, string id, string workspace) =>
            inner.CreatePaths(root, target, id, workspace);
        public void ValidateTargetParent(string root, string target) => inner.ValidateTargetParent(root, target);
        public Task PrepareAsync(LocalZipPackage package, LocalZipInstallTransactionPaths paths, CancellationToken token) =>
            inner.PrepareAsync(package, paths, token);
        public IReadOnlyDictionary<string, string> SnapshotDirectory(string path) => inner.SnapshotDirectory(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public void WriteJournal(LocalZipInstallTransactionJournal journal) => inner.WriteJournal(journal);
        public void CommitDirectoryMove(LocalZipInstallTransactionJournal journal) => inner.CommitDirectoryMove(journal);
        public void ValidateJournal(LocalZipInstallTransactionJournal journal) => inner.ValidateJournal(journal);
        public void CleanupPreparation(LocalZipInstallTransactionJournal journal) => inner.CleanupPreparation(journal);
        public void DeleteJournal(LocalZipInstallTransactionJournal journal) => inner.DeleteJournal(journal);
        public IReadOnlyList<LocalZipPendingTransaction> ReadPendingTransactions() => inner.ReadPendingTransactions();
        public LocalZipSourcePolicyResult ApplyHistoricalBindingPolicy(string root, string id, LocalZipHistoricalBindingPolicy policy) =>
            inner.ApplyHistoricalBindingPolicy(root, id, policy);
    }

    static string CreateZip(string parent)
    {
        string path = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (Entry entry in new[]
        {
            new Entry("Fixture/mod_manifest.json", Manifest),
            new Entry("Fixture/fixture.dll", "fixture-dll"),
            new Entry("Fixture/data.bin", "fixture-data")
        })
        {
            var item = archive.CreateEntry(entry.Path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(item.Open(), new UTF8Encoding(false));
            writer.Write(entry.Content);
        }
        return path;
    }

    static string Fixture(string parent)
    {
        string root = Path.Combine(parent, "game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        File.WriteAllText(Path.Combine(root, "SlayTheSpire2.exe"), "fixture");
        File.WriteAllText(Path.Combine(root, ".spirepatch-fixture"), "isolated");
        return root;
    }

    static LocalModZipImportWorkflowService PreviewService(string parent, Func<string, bool>? running = null)
    {
        string stage = Path.Combine(parent, "preview-" + Guid.NewGuid().ToString("N"));
        return new(Path.Combine(parent, "data"), stagingRoot: stage, gameRunning: running ?? (_ => false));
    }

    static Dictionary<string, string> Hashes(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);
            result[Path.GetRelativePath(root, file).Replace('\\', '/')] =
                Convert.ToHexString(SHA256.HashData(stream));
        }
        return result;
    }

    static bool Same(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out string? hash) &&
            hash.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));

    static bool NoJournals(string directory) =>
        !Directory.Exists(directory) ||
        !Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Any();

    static async Task<(LocalModZipInstallTransactionBackend Backend,
        LocalZipInstallTransactionJournal Journal)> SeedTransactionAsync(
        string dataRoot, LocalModZipPreview preview, string state, bool movePrepared)
    {
        var backend = new LocalModZipInstallTransactionBackend(dataRoot, _ => false);
        string id = Guid.NewGuid().ToString("N");
        LocalZipInstallTransactionPaths paths = backend.CreatePaths(
            preview.GameRoot, preview.DestinationDirectory, id, preview.Package.WorkspaceDirectory);
        await backend.PrepareAsync(preview.Package, paths, CancellationToken.None);
        var expected = preview.Package.Files.ToDictionary(
            file => file.RelativePath, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        var journal = new LocalZipInstallTransactionJournal(
            1, id, Path.GetFullPath(preview.GameRoot), preview.Package.ModId,
            preview.Package.DestinationFolderName, paths.TargetDirectory,
            paths.TransactionDirectory, paths.PreparedDirectory,
            paths.PreviewWorkspaceDirectory, state, expected,
            LocalZipHistoricalBindingPolicy.PreserveUnverified.ToString(),
            preview.HistoricalSources.HasHistoricalEvidence, false, null, DateTimeOffset.UtcNow);
        backend.WriteJournal(journal);
        if (movePrepared)
            Directory.Move(paths.PreparedDirectory, paths.TargetDirectory);
        return (backend, journal);
    }

    public static async Task RunAsync(string home, Action<bool, string> check)
    {
        string parent = Path.Combine(home, "local-zip-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string root = Fixture(parent);
        string data = Path.Combine(parent, "data");
        string zip = CreateZip(parent);
        var previewService = PreviewService(parent);
        var previewResult = await previewService.PreviewAsync(root, zip);
        check(previewResult.State == LocalZipPreviewState.Ready && previewResult.Preview is not null,
            "local ZIP apply consumes a valid read-only batch-8 preview");
        LocalModZipPreview preview = previewResult.Preview!;

        var declinedBackend = new LocalModZipInstallTransactionBackend(data, _ => false);
        var declinedService = new LocalModZipApplyWorkflowService(previewService, data, declinedBackend);
        var declined = await declinedService.ApplyAsync(preview, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(false, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(declined.FailureKind == LocalZipApplyFailureKind.UserDeclined &&
            !Directory.Exists(preview.DestinationDirectory) &&
            !Directory.Exists(Path.Combine(Path.GetDirectoryName(root)!, ".spirepatch-localzip-transactions")) &&
            !Directory.Exists(declinedService.TransactionLogDirectory) &&
            !declined.InventoryInvalidationRequired &&
            !declined.SourceCacheInvalidationRequired &&
            !declined.PatchLifecycleRecheckRequired,
            "declining confirmation creates no preparation/log and emits no invalidation");

        bool writingTrue = false;
        bool writingFalseAfterTrue = false;
        bool deferred = false;
        var successBackend = new LocalModZipInstallTransactionBackend(data, _ => false);
        var successService = new LocalModZipApplyWorkflowService(previewService, data, successBackend);
        var installed = await successService.ApplyAsync(preview, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => deferred = true,
            reportProgress: progress =>
            {
                if (progress.TransactionWriting) writingTrue = true;
                else if (writingTrue) writingFalseAfterTrue = true;
            });
        var expected = preview.Package.Files.ToDictionary(file => file.RelativePath,
            file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        check(installed.Success && installed.InventoryInvalidationRequired &&
            installed.SourceCacheInvalidationRequired && installed.PatchLifecycleRecheckRequired &&
            Same(Hashes(preview.DestinationDirectory), expected),
            "new install atomically moves the verified payload and emits all lifecycle invalidation signals");
        check(!Directory.Exists(preview.Package.WorkspaceDirectory),
            "successful install removes the isolated ZIP preview workspace");
        check(installed.TransactionId is not null &&
            !Directory.Exists(Path.Combine(Path.GetDirectoryName(root)!,
                ".spirepatch-localzip-transactions", installed.TransactionId)),
            "successful install removes only its GUID-owned same-volume preparation directory");
        check(NoJournals(successBackend.TransactionLogDirectory),
            "successful install removes its persisted transaction journal");
        check(writingTrue && writingFalseAfterTrue && deferred,
            "transaction writing remains active through preparation and commit, then defers the watcher quiet period");

        // A changed original ZIP is rejected before any transaction write.
        string rootTamper = Fixture(parent);
        var tampered = await previewService.PreviewAsync(rootTamper, CreateZip(parent));
        File.AppendAllText(tampered.Preview!.Package.ZipPath, "tamper");
        var tamperService = new LocalModZipApplyWorkflowService(previewService, data,
            new LocalModZipInstallTransactionBackend(data, _ => false));
        var tamperResult = await tamperService.ApplyAsync(tampered.Preview, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(!tamperResult.Success && tamperResult.FailureKind == LocalZipApplyFailureKind.PreviewChanged &&
            !Directory.Exists(tampered.Preview!.DestinationDirectory) &&
            NoJournals(tamperService.TransactionLogDirectory),
            "pre-commit verification rejects an altered original ZIP without target mutation");

        // A staged payload modified after the first verification is rejected by the final verifier.
        string rootStagedRace = Fixture(parent);
        var stagedRacePreview = await previewService.PreviewAsync(rootStagedRace, CreateZip(parent));
        var stagedRaceService = new LocalModZipApplyWorkflowService(previewService, data);
        var stagedRaceResult = await stagedRaceService.ApplyAsync(stagedRacePreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { },
            reportProgress: progress =>
            {
                if (progress.Stage == "Prepared")
                {
                    string stagedFile = Path.Combine(stagedRacePreview.Preview!.Package.PayloadDirectory,
                        stagedRacePreview.Preview!.Package.Files[0].RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    File.AppendAllText(stagedFile, "tamper-after-first-verification");
                }
            });
        check(!stagedRaceResult.Success &&
            !Directory.Exists(stagedRacePreview.Preview!.DestinationDirectory),
            "a staged payload changed during preparation is caught by the final pre-commit verifier");

        // The game can start after the first verification while preparation is in progress.
        string rootRunning = Fixture(parent);
        var runningPreview = await previewService.PreviewAsync(rootRunning, CreateZip(parent));
        bool gameRunning = false;
        var runningService = new LocalModZipApplyWorkflowService(previewService, data,
            new LocalModZipInstallTransactionBackend(data, _ => gameRunning));
        var runningResult = await runningService.ApplyAsync(runningPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { },
            reportProgress: progress =>
            {
                if (progress.Stage == "Prepared") gameRunning = true;
            });
        check(!runningResult.Success && runningResult.FailureKind == LocalZipApplyFailureKind.GameRunning &&
            !Directory.Exists(runningPreview.Preview!.DestinationDirectory),
            "a game process starting during preparation is caught by final pre-commit verification");

        // Active and disabled inventory are both rechecked after the initial verification.
        string rootInventory = Fixture(parent);
        var inventoryPreview = await previewService.PreviewAsync(rootInventory, CreateZip(parent));
        var inventoryService = new LocalModZipApplyWorkflowService(previewService, data);
        var inventoryResult = await inventoryService.ApplyAsync(inventoryPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { },
            reportProgress: progress =>
            {
                if (progress.Stage == "Prepared")
                {
                    string appeared = Path.Combine(rootInventory, "mods", "appeared");
                    Directory.CreateDirectory(appeared);
                    File.WriteAllText(Path.Combine(appeared, "mod_manifest.json"),
                        """{"id":"fixture.apply","version":"9.0.0","has_dll":true}""");
                }
            });
        check(!inventoryResult.Success &&
            !Directory.Exists(inventoryPreview.Preview!.DestinationDirectory),
            "an active MOD with the same ID appearing during preparation is rejected");

        string rootDisabledInventory = Fixture(parent);
        var disabledInventoryPreview = await previewService.PreviewAsync(rootDisabledInventory, CreateZip(parent));
        var disabledInventoryService = new LocalModZipApplyWorkflowService(previewService, data);
        var disabledInventoryResult = await disabledInventoryService.ApplyAsync(disabledInventoryPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { },
            reportProgress: progress =>
            {
                if (progress.Stage == "Prepared")
                {
                    string slot = Path.Combine(rootDisabledInventory, "已停用模组_尖塔修补站", "slot-1");
                    Directory.CreateDirectory(slot);
                    File.WriteAllText(Path.Combine(slot, "记录.json"),
                        """{"ModId":"fixture.apply","Status":"disabled","Original":"mods/old"}""");
                }
            });
        check(!disabledInventoryResult.Success &&
            !Directory.Exists(disabledInventoryPreview.Preview!.DestinationDirectory),
            "a disabled MOD with the same ID appearing during preparation is rejected");

        // A target created after the first verification but before final commit is preserved.
        string rootAppeared = Fixture(parent);
        var appearedPreview = await previewService.PreviewAsync(rootAppeared, CreateZip(parent));
        string appearedTarget = appearedPreview.Preview!.DestinationDirectory;
        var appearedService = new LocalModZipApplyWorkflowService(previewService, data);
        var appearedResult = await appearedService.ApplyAsync(appearedPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { },
            reportProgress: progress =>
            {
                if (progress.Stage == "Prepared")
                {
                    Directory.CreateDirectory(appearedTarget);
                    File.WriteAllText(Path.Combine(appearedTarget, "external.txt"), "keep");
                }
            });
        check(!appearedResult.Success &&
            File.ReadAllText(Path.Combine(appearedTarget, "external.txt")) == "keep",
            "a destination appearing between verification and commit is never overwritten or deleted");

        // Preparation failure is limited to this GUID's staging directory.
        string rootCopyFailure = Fixture(parent);
        var copyPreview = await previewService.PreviewAsync(rootCopyFailure, CreateZip(parent));
        var copyBackend = new FailingBackend(data, (point, _) =>
            point == LocalZipInstallFaultPoint.CopyFile ? new IOException("injected copy failure") : null);
        var copyService = new LocalModZipApplyWorkflowService(previewService, data, copyBackend);
        var copyResult = await copyService.ApplyAsync(copyPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(!copyResult.Success && !Directory.Exists(copyPreview.Preview!.DestinationDirectory) &&
            NoJournals(copyService.TransactionLogDirectory),
            "mid-copy failure cleans only the transaction-owned GUID directory and leaves target absent");

        // Move failure before atomic rename preserves a clean target and removes only our preparation.
        string rootMoveFailure = Fixture(parent);
        var movePreview = await previewService.PreviewAsync(rootMoveFailure, CreateZip(parent));
        var moveBackend = new FailingBackend(data, (point, _) =>
            point == LocalZipInstallFaultPoint.BeforeMove ? new IOException("injected move failure") : null);
        var moveService = new LocalModZipApplyWorkflowService(previewService, data, moveBackend);
        var moveResult = await moveService.ApplyAsync(movePreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(!moveResult.Success && !Directory.Exists(movePreview.Preview!.DestinationDirectory),
            "atomic directory move failure leaves the destination absent");

        // A manager-wide file lock held by another writer blocks the atomic move.
        string rootLocked = Fixture(parent);
        var lockedPreview = await previewService.PreviewAsync(rootLocked, CreateZip(parent));
        var lockedBackend = new FailingBackend(data, (_, _) => null, lockAvailable: false);
        var lockedService = new LocalModZipApplyWorkflowService(previewService, data, lockedBackend);
        var lockedResult = await lockedService.ApplyAsync(lockedPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(!lockedResult.Success && lockedResult.FailureKind == LocalZipApplyFailureKind.CommitFailed &&
            !Directory.Exists(lockedPreview.Preview!.DestinationDirectory),
            "an externally held game-directory transaction lock prevents commit and leaves the target absent");

        // Cancellation at ReadyToCommit is honored before the non-cancellable commit boundary.
        string rootCancelled = Fixture(parent);
        var cancelledPreview = await previewService.PreviewAsync(rootCancelled, CreateZip(parent));
        using var cancelSource = new CancellationTokenSource();
        var writingStates = new List<(string Stage, bool Writing)>();
        bool cancelQuietPeriodDeferred = false;
        var cancelledService = new LocalModZipApplyWorkflowService(previewService, data);
        var cancelledResult = await cancelledService.ApplyAsync(cancelledPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => cancelQuietPeriodDeferred = true,
            reportProgress: progress =>
            {
                writingStates.Add((progress.Stage, progress.TransactionWriting));
                if (progress.Stage == "ReadyToCommit") cancelSource.Cancel();
            },
            cancellationToken: cancelSource.Token);
        int firstWritingIndex = writingStates.FindIndex(item => item.Writing);
        int lastWritingIndex = writingStates.FindLastIndex(item => item.Writing);
        check(cancelledResult.Cancelled && !Directory.Exists(cancelledPreview.Preview!.DestinationDirectory) &&
            !cancelledResult.InventoryInvalidationRequired &&
            !cancelledResult.SourceCacheInvalidationRequired &&
            !cancelledResult.PatchLifecycleRecheckRequired &&
            cancelledResult.TransactionId is not null &&
            !Directory.Exists(Path.Combine(Path.GetDirectoryName(rootCancelled)!,
                ".spirepatch-localzip-transactions", cancelledResult.TransactionId)),
            "cancellation before Committing cleans the transaction-owned preparation and leaves target absent");
        check(firstWritingIndex >= 0 && lastWritingIndex >= firstWritingIndex &&
            writingStates.Skip(firstWritingIndex).Take(lastWritingIndex - firstWritingIndex + 1)
                .All(item => item.Writing) &&
            writingStates.Skip(lastWritingIndex + 1).Count(item => !item.Writing) == 1 &&
            cancelQuietPeriodDeferred,
            "TransactionWriting stays continuously true through ReadyToCommit and changes to false once after cleanup");

        // A crash after Prepared but before ReadyToCommit leaves no target; recovery cleans only our GUID.
        string rootCrashPrepared = Fixture(parent);
        var crashPreparedPreview = await previewService.PreviewAsync(rootCrashPrepared, CreateZip(parent));
        var crashPreparedSeed = await SeedTransactionAsync(data, crashPreparedPreview.Preview!, "Prepared", false);
        bool recoveryQuietPeriodDeferred = false;
        var crashPreparedRecovery = new LocalModZipApplyWorkflowService(
            previewService, data, crashPreparedSeed.Backend);
        var preparedRecovery = crashPreparedRecovery.RecoverPendingTransactions(
            rootCrashPrepared, () => recoveryQuietPeriodDeferred = true);
        check(preparedRecovery.CleanedCount == 1 && !preparedRecovery.HasPendingRecovery &&
            !preparedRecovery.InventoryInvalidationRequired &&
            !preparedRecovery.SourceCacheInvalidationRequired &&
            !preparedRecovery.PatchLifecycleRecheckRequired &&
            !Directory.Exists(crashPreparedSeed.Journal.TransactionDirectory) &&
            !Directory.Exists(crashPreparedPreview.Preview!.DestinationDirectory) &&
            !Directory.Exists(crashPreparedPreview.Preview!.Package.WorkspaceDirectory) &&
            recoveryQuietPeriodDeferred,
            "recovery after Prepared with no target cleans the isolated preparation, preview and journal");

        // A durable ReadyToCommit journal without a moved target is also safely cleaned.
        string rootCrashReady = Fixture(parent);
        var crashReadyPreview = await previewService.PreviewAsync(rootCrashReady, CreateZip(parent));
        var crashReadySeed = await SeedTransactionAsync(data, crashReadyPreview.Preview!, "ReadyToCommit", false);
        var crashReadyRecovery = new LocalModZipApplyWorkflowService(
            previewService, data, crashReadySeed.Backend);
        var readyRecovery = crashReadyRecovery.RecoverPendingTransactions(rootCrashReady);
        check(readyRecovery.CleanedCount == 1 && !readyRecovery.HasPendingRecovery &&
            !readyRecovery.InventoryInvalidationRequired &&
            !readyRecovery.SourceCacheInvalidationRequired &&
            !readyRecovery.PatchLifecycleRecheckRequired &&
            !Directory.Exists(crashReadySeed.Journal.TransactionDirectory) &&
            !Directory.Exists(crashReadyPreview.Preview!.DestinationDirectory),
            "recovery after the durable pre-move journal cleans preparation without creating a MOD");

        // A crash after the atomic move but before the Committed journal is repaired from exact hashes.
        string rootCrashMoved = Fixture(parent);
        var crashMovedPreview = await previewService.PreviewAsync(rootCrashMoved, CreateZip(parent));
        var crashMovedSeed = await SeedTransactionAsync(data, crashMovedPreview.Preview!, "Committing", true);
        var crashMovedRecovery = new LocalModZipApplyWorkflowService(
            previewService, data, crashMovedSeed.Backend);
        var movedRecovery = crashMovedRecovery.RecoverPendingTransactions(rootCrashMoved);
        var movedExpected = crashMovedPreview.Preview!.Package.Files.ToDictionary(
            file => file.RelativePath, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        check(movedRecovery.CompletedCount == 1 && !movedRecovery.HasPendingRecovery &&
            movedRecovery.InventoryInvalidationRequired &&
            movedRecovery.SourceCacheInvalidationRequired &&
            movedRecovery.PatchLifecycleRecheckRequired &&
            Same(Hashes(crashMovedPreview.Preview!.DestinationDirectory), movedExpected) &&
            !Directory.Exists(crashMovedSeed.Journal.TransactionDirectory) &&
            !Directory.Exists(crashMovedPreview.Preview!.Package.WorkspaceDirectory),
            "recovery after move and before completion verifies exact hashes, records completion and cleans remnants");

                // Simulate a crash after the move and before the Committed marker.
        string rootCrash = Fixture(parent);
        var crashPreview = await previewService.PreviewAsync(rootCrash, CreateZip(parent));
        var crashBackend = new FailingBackend(data, (point, _) =>
            point == LocalZipInstallFaultPoint.AfterMove ? new IOException("simulated crash after move") : null);
        var crashService = new LocalModZipApplyWorkflowService(previewService, data, crashBackend);
        var crashResult = await crashService.ApplyAsync(crashPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(crashResult.Success && crashResult.InventoryInvalidationRequired,
            "a moved directory with matching hashes is recognized as a committed install after an interruption");

        // External edits after move are preserved for manual recovery, never recursively deleted.
        string rootMismatch = Fixture(parent);
        string mismatchKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootMismatch)).ToUpperInvariant())));
        string mismatchGithub = Path.Combine(data, "GitHub模组更新", mismatchKey + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(mismatchGithub)!);
        File.WriteAllText(mismatchGithub, """{"fixture.apply":"fixture/repository"}""");
        var mismatchPreview = await previewService.PreviewAsync(rootMismatch, CreateZip(parent));
        var mismatchBackend = new FailingBackend(data, (point, _) =>
            point == LocalZipInstallFaultPoint.SourceBindingWrite ? new IOException("source persistence interrupted") : null);
        var mismatchService = new LocalModZipApplyWorkflowService(previewService, data, mismatchBackend);
        var mismatch = await mismatchService.ApplyAsync(mismatchPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.Unbind)),
            () => { });
        string target = mismatchPreview.Preview!.DestinationDirectory;
        File.WriteAllText(Path.Combine(target, "external-change.txt"), "external");
        var recovery = mismatchService.RecoverPendingTransactions(rootMismatch);
        check(mismatch.Success && mismatch.PartialSuccess && mismatch.SourceBindingCleanupFailed,
            "failed explicit source unbind is returned as a partial successful installation");
        check(recovery.HasPendingRecovery &&
            !recovery.InventoryInvalidationRequired &&
            !recovery.SourceCacheInvalidationRequired &&
            !recovery.PatchLifecycleRecheckRequired &&
            Directory.Exists(target) &&
            File.Exists(Path.Combine(target, "external-change.txt")),
            "recovery marks a mismatched target RecoveryRequired and never deletes user-modified content");

        // Historical bindings are surfaced to the caller; Preserve never silently upgrades trust.
        string rootHistorical = Fixture(parent);
        string historicalKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootHistorical)).ToUpperInvariant())));
        string historicalGithub = Path.Combine(data, "GitHub模组更新", historicalKey + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(historicalGithub)!);
        File.WriteAllText(historicalGithub, """{"fixture.apply":"fixture/old-repository"}""");
        var historicalPreview = await previewService.PreviewAsync(rootHistorical, CreateZip(parent));
        bool explicitPoliciesShown = false;
        var historicalService = new LocalModZipApplyWorkflowService(previewService, data);
        var historicalResult = await historicalService.ApplyAsync(historicalPreview.Preview!, (confirmation, _) =>
        {
            explicitPoliciesShown = confirmation.HistoricalSources.HasHistoricalEvidence &&
                confirmation.AvailableBindingPolicies.Contains(LocalZipHistoricalBindingPolicy.PreserveUnverified) &&
                confirmation.AvailableBindingPolicies.Contains(LocalZipHistoricalBindingPolicy.RequireManualReview) &&
                confirmation.AvailableBindingPolicies.Contains(LocalZipHistoricalBindingPolicy.Unbind);
            return Task.FromResult(new LocalZipApplyDecision(
                true, LocalZipHistoricalBindingPolicy.PreserveUnverified));
        }, () => { });
        check(explicitPoliciesShown && historicalResult.Success &&
            historicalResult.SourceReviewRequired &&
            historicalResult.HistoricalBindingAction.Contains("未验证", StringComparison.Ordinal),
            "historical source preservation is an explicit choice and remains marked for manual review");

        string rootReview = Fixture(parent);
        string reviewKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootReview)).ToUpperInvariant())));
        string reviewGithub = Path.Combine(data, "GitHub模组更新", reviewKey + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(reviewGithub)!);
        File.WriteAllText(reviewGithub, """{"fixture.apply":"fixture/review-repository"}""");
        var reviewPreview = await previewService.PreviewAsync(rootReview, CreateZip(parent));
        var reviewService = new LocalModZipApplyWorkflowService(previewService, data);
        var reviewResult = await reviewService.ApplyAsync(reviewPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.RequireManualReview)),
            () => { });
        check(reviewResult.Success && reviewResult.SourceReviewRequired,
            "the explicit review-required policy retains historical metadata without granting trust");

        // Explicit unbind is tested against temporary JSON bindings; a failed write is reported.
        string sourceRoot = Fixture(parent);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)).ToUpperInvariant())));
        string github = Path.Combine(data, "GitHub模组更新", key + ".json");
        string workshopDirectory = Path.Combine(data, "创意工坊来源");
        string workshop = Path.Combine(workshopDirectory, key + ".json");
        string baseline = Path.Combine(workshopDirectory, key + ".versions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(github)!);
        Directory.CreateDirectory(workshopDirectory);
        File.WriteAllText(github, """{"fixture.apply":"fixture/repository"}""");
        File.WriteAllText(workshop, """{"fixture.apply":"123456789"}""");
        File.WriteAllText(baseline,
            """{"fixture.apply":{"WorkshopId":123456789,"ManifestId":"123456789","LocalVersion":"1.0","LocalSnapshotSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","RecordedAt":"2026-11-22T00:00:00+00:00"}}""");
        var unbindBackend = new LocalModZipInstallTransactionBackend(data, _ => false);
        var unbound = unbindBackend.ApplyHistoricalBindingPolicy(sourceRoot, "fixture.apply",
            LocalZipHistoricalBindingPolicy.Unbind);
        check(unbound.Applied &&
            !File.ReadAllText(github).Contains("fixture.apply", StringComparison.Ordinal) &&
            !File.ReadAllText(workshop).Contains("fixture.apply", StringComparison.Ordinal) &&
            !File.ReadAllText(baseline).Contains("fixture.apply", StringComparison.Ordinal),
            "explicit unbind removes GitHub, Workshop and Workshop baseline bindings from isolated manager data");

        string sourceFailureRoot = Fixture(parent);
        string failureKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFailureRoot)).ToUpperInvariant())));
        string failureGithub = Path.Combine(data, "GitHub模组更新", failureKey + ".json");
        string failureWorkshop = Path.Combine(workshopDirectory, failureKey + ".json");
        string failureBaseline = Path.Combine(workshopDirectory, failureKey + ".versions.json");
        File.WriteAllText(failureGithub, """{"fixture.apply":"fixture/repository"}""");
        File.WriteAllText(failureWorkshop, """{"fixture.apply":"123456789"}""");
        File.WriteAllText(failureBaseline,
            """{"fixture.apply":{"WorkshopId":123456789,"ManifestId":"123456789","LocalVersion":"1.0","LocalSnapshotSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","RecordedAt":"2026-11-22T00:00:00+00:00"}}""");
        var failedUnbindBackend = new FailingBackend(data, (point, detail) =>
            point == LocalZipInstallFaultPoint.SourceBindingWrite &&
            detail.Contains("GitHub模组更新", StringComparison.Ordinal)
                ? new IOException("injected GitHub persistence failure") : null);
        var failedUnbind = failedUnbindBackend.ApplyHistoricalBindingPolicy(
            sourceFailureRoot, "fixture.apply", LocalZipHistoricalBindingPolicy.Unbind);
        check(!failedUnbind.Applied && failedUnbind.PartialSuccess &&
            failedUnbind.Error is not null &&
            !File.ReadAllText(failureBaseline).Contains("fixture.apply", StringComparison.Ordinal) &&
            !File.ReadAllText(failureWorkshop).Contains("fixture.apply", StringComparison.Ordinal) &&
            File.ReadAllText(failureGithub).Contains("fixture.apply", StringComparison.Ordinal),
            "a later source-binding persistence failure reports partial success and preserves the failed binding");

        // Replace the target parent with a symlink after initial verification, during preparation.
        string rootLink = Fixture(parent);
        string linkParent = Path.Combine(rootLink, "mods");
        string realParent = Path.Combine(parent, "real-mods");
        Directory.CreateDirectory(realParent);
        var linkPreview = await previewService.PreviewAsync(rootLink, CreateZip(parent));
        var linkService = new LocalModZipApplyWorkflowService(previewService, data);
        bool linkCreated = false;
        try
        {
            var linkResult = await linkService.ApplyAsync(linkPreview.Preview!, (_, _) =>
                Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
                () => { },
                reportProgress: progress =>
                {
                    if (progress.Stage == "Prepared" && !linkCreated)
                    {
                        Directory.Delete(linkParent);
                        Directory.CreateSymbolicLink(linkParent, realParent);
                        linkCreated = true;
                    }
                });
            check(linkCreated && !linkResult.Success &&
                !Directory.Exists(Path.Combine(realParent, linkPreview.Preview!.Package.DestinationFolderName)),
                "a mods parent changed to a reparse point during preparation is rejected without writing through the link");
        }
        catch (UnauthorizedAccessException)
        {
            check(false, "Windows isolated runner must support symbolic-link reparse-point verification");
        }
    }
}
