using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpirePatch;

internal static class ApplicationWorkflowChecks
{
    sealed class RecordingWatcher : IModFileWatcherService
    {
        public List<string> Calls { get; } = [];
        public event EventHandler<ModFileChangeEventArgs>? ChangeDetected;
        public event EventHandler<ModFileWatchQuietEventArgs>? QuietPeriodElapsed;
        public event EventHandler<ModFileWatchFailureEventArgs>? MonitoringFailed;
        public void Start(string root) => Calls.Add("start:" + root);
        public void Stop() => Calls.Add("stop");
        public void DeferQuietPeriod() => Calls.Add("defer");
        public ModFileWatchReconfigurationResult CompleteQuietPeriod(int version) => new(false, null);
        public void Dispose() => Calls.Add("dispose");
    }

    sealed class FailingApplyBackend : ICoopAlignmentWorkflowBackend
    {
        readonly DomainCoopAlignmentWorkflowBackend inner = new();
        public List<string> Steps { get; } = [];
        public int ScanCount { get; private set; }

        public (ScanReport Report, ModInventory Inventory, JsonElement Snapshot) Scan(
            string root, IReadOnlyList<OnlinePatch>? definitions, string? revision)
        {
            ScanCount++;
            return inner.Scan(root, definitions, revision);
        }

        public JsonElement CreateDemo(ScanReport? report, IReadOnlyList<PatchState> states) =>
            inner.CreateDemo(report, states);
        public void WriteReport(string path, JsonElement snapshot) => inner.WriteReport(path, snapshot);
        public JsonElement ReadReport(string path) => inner.ReadReport(path);
        public string Compare(JsonElement local, JsonElement peer) => inner.Compare(local, peer);
        public List<CoopDifference> Analyze(JsonElement local, JsonElement peer, ScanReport report, ModInventory inventory) =>
            inner.Analyze(local, peer, report, inventory);
        public Task<CoopPlan> PrepareAsync(
            BundleInstaller installer, PreparedPackage? package, string root,
            JsonElement peer, IEnumerable<string> selected) =>
            inner.PrepareAsync(installer, package, root, peer, selected);

        public Task<CoopOutcome> ApplyAsync(BundleInstaller installer, PreparedPackage? package, CoopPlan plan)
        {
            // Simulate the Domain transaction contract: once a step fails, later steps are not run.
            Steps.Add("first-step-completed");
            Steps.Add("second-step-failed");
            try
            {
                throw new IOException("isolated simulated middle-step failure");
            }
            catch (Exception error)
            {
                return Task.FromResult(new CoopOutcome(
                    false, ["first-step-completed"], error.Message,
                    Path.Combine(installer.DataRoot, "fixture-journal.json")));
            }
        }
    }

    public static async Task RunAsync(string parent, Action<bool, string> check)
    {
        string home = Path.Combine(parent, "Application 工作流测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        // File watcher: real FileSystemWatcher events are confined to a temporary fixture.
        string watchRoot = Path.Combine(home, "watch-game");
        string watchMods = Path.Combine(watchRoot, "mods");
        Directory.CreateDirectory(watchMods);
        Directory.CreateDirectory(Path.Combine(watchRoot, ModManager.StorageName));
        File.WriteAllText(Path.Combine(watchRoot, "SlayTheSpire2.exe"), "fixture-only");

        using (var watcher = new ModFileWatcherService(TimeSpan.FromMilliseconds(120)))
        {
            var quiet = new TaskCompletionSource<(ModFileWatchQuietEventArgs Args, TimeSpan Delay)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            long lastChange = 0;
            watcher.ChangeDetected += (_, _) => Interlocked.Exchange(ref lastChange, Stopwatch.GetTimestamp());
            watcher.QuietPeriodElapsed += (_, args) =>
            {
                long changedAt = Interlocked.Read(ref lastChange);
                quiet.TrySetResult((args, Stopwatch.GetElapsedTime(changedAt)));
            };
            watcher.Start(watchRoot);
            File.WriteAllText(Path.Combine(watchMods, "first-change.txt"), "one");
            await Task.Delay(35);
            File.WriteAllText(Path.Combine(watchMods, "first-change.txt"), "two");
            var settled = await quiet.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(settled.Args.Version > 0 && settled.Delay >= TimeSpan.FromMilliseconds(90),
                "MOD watcher waits for the configured quiet/debounce interval before notifying reconciliation");
            _ = watcher.CompleteQuietPeriod(settled.Args.Version);

            var overflow = new TaskCompletionSource<ModFileWatchQuietEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.QuietPeriodElapsed += (_, args) =>
            {
                if (args.Overflow) overflow.TrySetResult(args);
            };
            watcher.SimulateBufferOverflowForTests(watchRoot);
            var overflowEvent = await overflow.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rebuilt = watcher.CompleteQuietPeriod(overflowEvent.Version);
            check(overflowEvent.Overflow && rebuilt.Recreated && rebuilt.Error is null,
                "MOD watcher reports buffer overflow and rebuilds its filesystem watchers");
        }

        // Environment session: ordering and persistence are verified with a fake watcher.
        string gameA = Path.Combine(home, "game-a");
        string gameB = Path.Combine(home, "game-b");
        Directory.CreateDirectory(gameA);
        Directory.CreateDirectory(gameB);
        File.WriteAllText(Path.Combine(gameA, "SlayTheSpire2.exe"), "fixture");
        File.WriteAllText(Path.Combine(gameB, "SlayTheSpire2.exe"), "fixture");
        string settings = Path.Combine(home, "settings", "偏好.json");
        var fakeWatcher = new RecordingWatcher();
        var session = new GameEnvironmentSessionService(settings, fakeWatcher, gameA);
        var order = new List<string>();
        session.DirectoryChanging += (_, args) => order.Add("cancel-old:" + args.PreviousRoot);
        session.DirectoryChanged += (_, args) => order.Add("reset-state:" + args.CurrentRoot);
        var selected = session.SelectRoot(gameB, remember: true);
        check(selected.Changed && session.CurrentRoot == Path.GetFullPath(gameB) &&
            order.Count == 2 && order[0].StartsWith("cancel-old:", StringComparison.Ordinal) &&
            order[1].StartsWith("reset-state:", StringComparison.Ordinal) &&
            fakeWatcher.Calls.SequenceEqual(["start:" + Path.GetFullPath(gameB)]) &&
            session.LoadRememberedRoot() == Path.GetFullPath(gameB),
            "game-directory switch cancels old work, resets session state, restarts monitoring and saves preference");
        fakeWatcher.Dispose();

        // Snapshot workflow: isolated game tree, full preview/restore and cache invalidation.
        string snapshotRoot = Path.Combine(home, "snapshot-game");
        string modFolder = Path.Combine(snapshotRoot, "mods", "FixtureMod");
        string snapshotData = Path.Combine(home, "snapshot-data");
        Directory.CreateDirectory(modFolder);
        File.WriteAllText(Path.Combine(snapshotRoot, "SlayTheSpire2.exe"), "fixture");
        File.WriteAllText(Path.Combine(modFolder, "manifest.json"),
            JsonSerializer.Serialize(new { id = "FixtureMod", name = "FixtureMod", version = "1.0.0", has_dll = true, dependencies = Array.Empty<object>() }));
        string modDll = Path.Combine(modFolder, "FixtureMod.dll");
        File.WriteAllText(modDll, "original fixture bytes");
        var invalidated = new List<string>();
        var snapshots = new ModEnvironmentSnapshotWorkflowService(
            snapshotRoot, snapshotData,
            invalidateCaches: ids => { invalidated.AddRange(ids); return null; });
        var created = await snapshots.CreateAsync("Application workflow fixture");
        File.WriteAllText(modDll, "changed after snapshot");
        var preview = await snapshots.PreviewRestoreAsync(created.Id);
        var restored = await snapshots.ConfirmRestoreAsync(preview, ["PreviousFixture"]);
        check(preview.ChangedFiles > 0 && restored.RefreshedInventory is not null &&
            File.ReadAllText(modDll) == "original fixture bytes" &&
            restored.RefreshedInventory.Mods.Any(mod => mod.Id == "FixtureMod") &&
            invalidated.Contains("PreviousFixture", StringComparer.OrdinalIgnoreCase) &&
            invalidated.Contains("FixtureMod", StringComparer.OrdinalIgnoreCase),
            "snapshot workflow creates, previews, restores, rescans and invalidates old/new MOD baselines");
        await snapshots.DeleteAsync(created.Id);
        await snapshots.DeleteAsync(restored.Restore.SafetySnapshotId);
        check(snapshots.List().Length == 0,
            "snapshot workflow deletes selected snapshots without touching the game executable");

        // Coop workflow: report/compare uses the existing Domain scanner and report rules.
        string coopRoot = Path.Combine(home, "coop-game");
        Directory.CreateDirectory(Path.Combine(coopRoot, "mods"));
        File.WriteAllText(Path.Combine(coopRoot, "SlayTheSpire2.exe"), "fixture");
        string coopData = Path.Combine(home, "coop-data");
        var installer = new BundleInstaller(coopData);
        var context = new CoopAlignmentWorkflowContext(
            coopRoot, false, null, Array.Empty<PatchState>());
        var workflow = new CoopAlignmentWorkflowService(installer);
        var fresh = await workflow.CreateFreshReportAsync(context);
        var same = await workflow.CompareAsync(fresh.Snapshot, context);
        string reportPath = Path.Combine(home, "peer-report.json");
        _ = await workflow.ExportFreshReportAsync(reportPath, context);
        var imported = await workflow.CompareReportFileAsync(reportPath, context);
        check(same.Summary.Contains("本机扫描：", StringComparison.Ordinal) &&
            imported.Summary.Contains("队友清单扫描：", StringComparison.Ordinal) &&
            imported.Rows is not null,
            "coop workflow rescans, exports and compares local fixture reports through Domain rules");

        var failingBackend = new FailingApplyBackend();
        var failingWorkflow = new CoopAlignmentWorkflowService(installer, failingBackend);
        var fakePlan = new CoopPlan(
            coopRoot, fresh.Snapshot.Clone(), ["fixture-action"], [], null, [], "fixture-token");
        int scansBeforeApply = failingBackend.ScanCount;
        var failed = await failingWorkflow.ApplyAsync(
            new CoopWorkflowPreparedPlan(fakePlan, null), context);
        check(!failed.Outcome.Success &&
            failingBackend.Steps.SequenceEqual(["first-step-completed", "second-step-failed"]) &&
            !failingBackend.Steps.Contains("later-step", StringComparer.Ordinal) &&
            failed.Comparison is not null && failingBackend.ScanCount > scansBeforeApply,
            "coop workflow preserves a middle-step failure, does not continue later steps and still performs a fresh comparison");
    }
}
