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
        readonly Func<LocalZipInstallFaultPoint, string, Exception?> fault;
        public string TransactionLogDirectory => inner.TransactionLogDirectory;
        public FailingBackend(string dataRoot,
            Func<LocalZipInstallFaultPoint, string, Exception?> fault)
        {
            this.fault = fault;
            inner = new(dataRoot, _ => false, _ => true, faultInjector:
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

    public static async Task RunAsync(string home, Action<bool, string> check)
    {
        string parent = Path.Combine(home, "local-zip-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string root = Fixture(parent);
        string data = Path.Combine(parent, "manager-data");
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
            !declined.InventoryInvalidationRequired,
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
            Same(Hashes(preview.DestinationDirectory), expected) &&
            !Directory.Exists(preview.Package.WorkspaceDirectory) &&
            !Directory.Exists(Path.GetDirectoryName(successBackend.TransactionLogDirectory) is string
                ? Path.Combine(Path.GetDirectoryName(root)!, ".spirepatch-localzip-transactions", installed.TransactionId!)
                : "") &&
            !Directory.Exists(successBackend.TransactionLogDirectory) &&
            writingTrue && writingFalseAfterTrue && deferred,
            "new install copies and hashes same-volume payload, atomically moves it, cleans artifacts and signals lifecycle invalidation");

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
            !Directory.Exists(tampered.Preview.DestinationDirectory) &&
            !Directory.Exists(tamperService.TransactionLogDirectory),
            "pre-commit verification rejects an altered original ZIP without target mutation");

        // The game becoming active after preview is also rejected before preparation.
        string rootRunning = Fixture(parent);
        var runningPreview = await previewService.PreviewAsync(rootRunning, CreateZip(parent));
        var runningService = new LocalModZipApplyWorkflowService(previewService, data,
            new LocalModZipInstallTransactionBackend(data, _ => true));
        var runningResult = await runningService.ApplyAsync(runningPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(!runningResult.Success && runningResult.FailureKind == LocalZipApplyFailureKind.GameRunning &&
            !Directory.Exists(runningPreview.Preview!.DestinationDirectory),
            "game process state is rechecked before commit and blocks installation");

        // Inventory changes after preview invalidate the immutable preview token.
        string rootInventory = Fixture(parent);
        var inventoryPreview = await previewService.PreviewAsync(rootInventory, CreateZip(parent));
        Directory.CreateDirectory(Path.Combine(rootInventory, "mods", "appeared"));
        File.WriteAllText(Path.Combine(rootInventory, "mods", "appeared", "mod_manifest.json"),
            """{"id":"fixture.apply","version":"9.0.0","has_dll":true}""");
        var inventoryService = new LocalModZipApplyWorkflowService(previewService, data);
        var inventoryResult = await inventoryService.ApplyAsync(inventoryPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(!inventoryResult.Success && !Directory.Exists(inventoryPreview.Preview!.DestinationDirectory),
            "new enabled MOD with same ID after preview is rejected");

        // A target created by another process after preview must never be overwritten.
        string rootAppeared = Fixture(parent);
        var appearedPreview = await previewService.PreviewAsync(rootAppeared, CreateZip(parent));
        Directory.CreateDirectory(appearedPreview.Preview!.DestinationDirectory);
        File.WriteAllText(Path.Combine(appearedPreview.Preview.DestinationDirectory, "external.txt"), "keep");
        var appearedService = new LocalModZipApplyWorkflowService(previewService, data);
        var appearedResult = await appearedService.ApplyAsync(appearedPreview.Preview, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        check(!appearedResult.Success &&
            File.ReadAllText(Path.Combine(appearedPreview.Preview.DestinationDirectory, "external.txt")) == "keep",
            "a destination that appears after preview is never overwritten or deleted");

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
            !Directory.Exists(copyService.TransactionLogDirectory),
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
        var mismatchPreview = await previewService.PreviewAsync(rootMismatch, CreateZip(parent));
        var mismatchBackend = new FailingBackend(data, (point, _) =>
            point == LocalZipInstallFaultPoint.AfterMove ? new IOException("crash after move") : null);
        var mismatchService = new LocalModZipApplyWorkflowService(previewService, data, mismatchBackend);
        var mismatch = await mismatchService.ApplyAsync(mismatchPreview.Preview!, (_, _) =>
            Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
            () => { });
        string target = mismatchPreview.Preview.DestinationDirectory;
        File.WriteAllText(Path.Combine(target, "external-change.txt"), "external");
        var recovery = mismatchService.RecoverPendingTransactions(rootMismatch);
        check(recovery.HasPendingRecovery && Directory.Exists(target) &&
            File.Exists(Path.Combine(target, "external-change.txt")),
            "recovery marks a mismatched target RecoveryRequired and never deletes user-modified content");

        // Explicit unbind is tested against temporary JSON bindings; a failed write is reported.
        string sourceRoot = Fixture(parent);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)).ToUpperInvariant())));
        string github = Path.Combine(data, "GitHub模组更新", key + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(github)!);
        File.WriteAllText(github, """{"fixture.apply":"fixture/repository"}""");
        var unbindBackend = new LocalModZipInstallTransactionBackend(data, _ => false);
        var unbound = unbindBackend.ApplyHistoricalBindingPolicy(sourceRoot, "fixture.apply",
            LocalZipHistoricalBindingPolicy.Unbind);
        check(unbound.Applied && !File.ReadAllText(github).Contains("fixture.apply", StringComparison.Ordinal),
            "explicit unbind removes the prior GitHub binding from isolated manager data");
        var failedUnbindBackend = new FailingBackend(data, (point, _) =>
            point == LocalZipInstallFaultPoint.SourceBindingWrite ? new IOException("injected persistence failure") : null);
        var failedUnbind = failedUnbindBackend.ApplyHistoricalBindingPolicy(sourceRoot, "fixture.apply",
            LocalZipHistoricalBindingPolicy.Unbind);
        check(!failedUnbind.Applied && failedUnbind.PartialSuccess &&
            failedUnbind.Error is not null,
            "source-binding persistence failure is surfaced as partial success rather than swallowed");

        // A symlink/reparse-point parent is rejected before any commit.
        string rootLink = Fixture(parent);
        string linkParent = Path.Combine(rootLink, "mods");
        string realParent = Path.Combine(parent, "real-mods");
        Directory.CreateDirectory(realParent);
        Directory.Delete(linkParent);
        try
        {
            Directory.CreateSymbolicLink(linkParent, realParent);
            var linkPreview = await previewService.PreviewAsync(rootLink, CreateZip(parent));
            check(linkPreview.Preview is not null, "link-parent fixture reaches pre-commit validation");
            var linkService = new LocalModZipApplyWorkflowService(previewService, data);
            var linkResult = await linkService.ApplyAsync(linkPreview.Preview!, (_, _) =>
                Task.FromResult(new LocalZipApplyDecision(true, LocalZipHistoricalBindingPolicy.PreserveUnverified)),
                () => { });
            check(!linkResult.Success && !Directory.Exists(linkResult.TargetDirectory ?? ""),
                "a reparse-point mods parent is rejected without writing through the link");
        }
        catch (UnauthorizedAccessException)
        {
            check(true, "symbolic-link test skipped by platform permission policy");
        }
    }
}
