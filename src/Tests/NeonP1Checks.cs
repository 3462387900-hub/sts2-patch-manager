using System.IO;
using System.Text.Json;

namespace SpirePatch;

public partial class MainWindow
{
    sealed class NeonP1RecordingBackend(IModUpdateStagingBackend inner) : IModUpdateStagingBackend
    {
        public List<ModUpdateStagingRequest> Requests { get; } = [];
        public Task<ModUpdateDownloadedArtifact> DownloadAsync(ModUpdateStagingRequest request,
            string workspaceDirectory, Action<string>? progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return inner.DownloadAsync(request, workspaceDirectory, progress, cancellationToken);
        }
    }

    internal async Task RunNeonP1ChecksAsync(string reportPath)
    {
        reportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string parent = Path.Combine(Path.GetTempPath(), "spire-neon-p1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        List<string> results = [];
        void Check(bool ok, string name)
        {
            results.Add((ok ? "PASS " : "FAIL ") + name);
            File.WriteAllLines(reportPath, results);
            if (!ok) throw new InvalidOperationException(name);
        }
        static string Fingerprint(string folder) => string.Join("\n",
            ModManager.FileSnapshot(folder).OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value));
        static void CopyTree(string source, string destination)
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        try
        {
            await ModUpdateStagingWorkflowChecks.RunAsync(parent, (ok, name) => Check(ok, "staging: " + name));
            await ModUpdateApplyWorkflowChecks.RunAsync(parent, (ok, name) => Check(ok, "Workshop apply: " + name), workshop: true);
            string root = await SetupNeonIsolatedFixtureAsync(parent);
            var view = neonWorkbenchView!;
            var hex = modInventory!.Mods.Single(x => x.Id == "HextechRunes");
            var manifest = WorkshopUpdates().Cached(hex.Id)!.RemoteManifestId;
            var recording = new NeonP1RecordingBackend(ModUpdateStagingWorkflowChecks.WorkshopBackend(
                "3748937859", manifest, hex.Id, "1.1.0"));
            neonTestStagingBackend = recording;
            string before = Fingerprint(hex.Folder);
            string staging = Path.Combine(NeonDataRoot, "MOD更新暂存");
            bool Staged() => Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any();
            await NeonPreviewUpdateModAsync(hex);
            Check(recording.Requests.Count == 1 && Staged(), "P1-01 workbench uses verified Workshop staging");
            view.CloseTransactionModal();
            Check(!Staged() && Fingerprint(hex.Folder) == before, "P1-01 workbench cancel leaves target unchanged");
            await NeonPreviewWorkshopInstallAsync(3748937859);
            Check(recording.Requests.Count == 2 && recording.Requests[0] == recording.Requests[1] && Staged(),
                "P1-01 acquisition uses identical manifest-pinned staging request");
            view.CloseTransactionModal();
            Check(!Staged() && Fingerprint(hex.Folder) == before, "P1-01 acquisition cancel leaves target unchanged");

            // Exercise the shared post-operation publication path with a deterministic delay.
            // Do not allow the auto-rescan to replace state before the rejection assertion.
            async Task RejectDelayed(string name, Func<Task> interfere)
            {
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                neonTestScanOverride = game =>
                {
                    var inventory = ModManager.Scan(game);
                    entered.TrySetResult(true);
                    release.Task.GetAwaiter().GetResult();
                    return inventory;
                };
                busy = true;
                var task = NeonScanAndPublishAsync(root);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await interfere();
                    release.TrySetResult(true);
                    Check(!await task && modInventoryDirty, name);
                }
                finally
                {
                    release.TrySetResult(true);
                    await task;
                    neonTestScanOverride = null;
                    busy = false;
                    neonRescanPending = false;
                }
            }
            await RejectDelayed("P1-02 delayed shared post-write scan rejects external file generation", async () =>
            {
                int generation = modFileWatcher.ChangeVersion;
                File.WriteAllText(Path.Combine(root, "mods", "p1-external-change.txt"), "outside");
                for (int i = 0; i < 200 && modFileWatcher.ChangeVersion == generation; i++) await Task.Delay(25);
                Check(modFileWatcher.ChangeVersion != generation, "P1-02 external write observed by watcher");
            });
            await RejectDelayed("P1-02 delayed shared post-write scan rejects watcher overflow", () =>
            {
                modFileWatcher.SimulateBufferOverflowForTests(root);
                return Task.CompletedTask;
            });
            // Exercise root-session ABA without touching a real game directory.
            await RejectDelayed("P1-02 delayed shared post-write scan rejects root epoch change", () =>
            {
                Interlocked.Increment(ref neonRootEpoch);
                return Task.CompletedTask;
            });

            // Both recovery families have real on-disk journals at once.
            Directory.CreateDirectory(NeonZipLogDirectory);
            string corrupt = Path.Combine(NeonZipLogDirectory, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(corrupt, "{");
            string tx = Guid.NewGuid().ToString("N");
            string txDir = Path.Combine(NeonUpdateLogDirectory, tx);
            string backup = Path.Combine(txDir, "backup");
            string stage = Path.Combine(staging, "stage-" + Guid.NewGuid().ToString("N"));
            string payload = Path.Combine(stage, "payload");
            string previous = Path.Combine(root, "mods", ".spirepatch-update-" + tx + ".previous");
            string prepared = Path.Combine(root, "mods", ".spirepatch-update-" + tx + ".prepared");
            CopyTree(hex.Folder, backup);
            CopyTree(hex.Folder, payload);
            var journal = new ModUpdateApplyTransactionJournal(1, tx, root, hex.Id, hex.Folder,
                txDir, backup, stage, payload, prepared, previous, "Committing", hex.Version, hex.Version,
                ModManager.FileSnapshot(hex.Folder), ModManager.FileSnapshot(payload), DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(txDir, "transaction.json"), JsonSerializer.Serialize(journal));
            Directory.Move(hex.Folder, previous);
            Directory.CreateDirectory(hex.Folder);
            File.WriteAllText(Path.Combine(hex.Folder, "partial.txt"), "interrupted");
            busy = true;
            try
            {
                await NeonRecoverTransactionsAsync(root);
                Check(NeonRecoveryBlocked(root) && !neonRecoveryStates[root][NeonUpdateRecoveryKind].Blocked &&
                    Fingerprint(hex.Folder) == before && !Directory.Exists(txDir),
                    "P1-03 corrupt ZIP journal does not prevent online transaction rollback");
                Check(view.RecoveryWarningVisible && NeonRecoveryWarning(root)!.Contains(NeonZipLogDirectory) &&
                    !NeonCanWrite(root), "P1-03 persistent warning and overlapping write gate");
                File.Delete(corrupt);
                await NeonRecoverTransactionsAsync(root);
                Check(!NeonRecoveryBlocked(root) && !view.RecoveryWarningVisible && NeonCanWrite(root),
                    "P1-03 successful retry clears warning and gate");
                var denied = await NeonRunRecoveryStepAsync("permission", NeonZipLogDirectory,
                    () => Task.FromException<string?>(new UnauthorizedAccessException("injected")));
                Check(denied.Blocked && denied.FailureReason!.Contains("UnauthorizedAccessException"),
                    "P1-03 permission failure produces structured failure");
            }
            finally { busy = false; }
        }
        catch (Exception ex)
        {
            results.Add("ERROR " + ex);
            File.WriteAllLines(reportPath, results);
            throw;
        }
        finally
        {
            neonTestStagingBackend = null;
            neonTestScanOverride = null;
            neonRescanPending = false;
            CloseModWatch();
        }
    }
}
