using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpirePatch;

internal static class ApplicationWorkflowChecks
{
    sealed class RecordingWatcher : IModFileWatcherService
    {
        public List<string> Calls { get; } = [];
        public event EventHandler<ModFileChangeEventArgs>? ChangeDetected { add { } remove { } }
        public event EventHandler<ModFileWatchQuietEventArgs>? QuietPeriodElapsed { add { } remove { } }
        public event EventHandler<ModFileWatchFailureEventArgs>? MonitoringFailed { add { } remove { } }
        public void Start(string root) => Calls.Add("start:" + root);
        public void Stop() => Calls.Add("stop");
        public void DeferQuietPeriod() => Calls.Add("defer");
        public ModFileWatchReconfigurationResult CompleteQuietPeriod(int version) => new(false, null);
        public void Dispose() => Calls.Add("dispose");
    }

    sealed class FakeModInventoryRefreshBackend : IModInventoryRefreshBackend
    {
        public Func<string, ModInventory> ScanHandler { get; set; } = _ => new([], []);
        public Func<string, string, ModInventory, bool, OrphanPatchCandidate[]> ObserveHandler { get; set; } =
            (_, _, _, _) => [];
        public Func<string, string, OrphanPatchOutcome> ReconcileHandler { get; set; } =
            (_, _) => OrphanPatchOutcome.Empty(new([], []));
        public bool Enabled { get; set; } = true;
        public string? RecoveryNotice { get; set; }
        int scanCount;
        int observeCount;
        int reconcileCount;
        public int ScanCount => Volatile.Read(ref scanCount);
        public int ObserveCount => Volatile.Read(ref observeCount);
        public int ReconcileCount => Volatile.Read(ref reconcileCount);

        public ModInventory Scan(string root)
        {
            Interlocked.Increment(ref scanCount);
            return ScanHandler(root);
        }

        public string? TakeRecoveryNotice()
        {
            var notice = RecoveryNotice;
            RecoveryNotice = null;
            return notice;
        }

        public bool AutoStopEnabled(string dataRoot) => Enabled;

        public OrphanPatchCandidate[] Observe(
            string root, string dataRoot, ModInventory inventory, bool enabled)
        {
            Interlocked.Increment(ref observeCount);
            return ObserveHandler(root, dataRoot, inventory, enabled);
        }

        public OrphanPatchOutcome Reconcile(string root, string dataRoot)
        {
            Interlocked.Increment(ref reconcileCount);
            return ReconcileHandler(root, dataRoot);
        }
    }

    sealed class FakeModToggleBackend : IModToggleWorkflowBackend
    {
        public ModInventory Inventory { get; private set; }
        public List<string> PreviewAttempts { get; } = [];
        public List<string> AppliedOrder { get; } = [];
        public int ApplyCount { get; private set; }
        public Action<string>? PreviewCompleted { get; set; }
        public Action<ModTogglePlan>? BeforeApply { get; set; }
        public Func<ModTogglePlan, Exception?>? ApplyFailure { get; set; }

        public FakeModToggleBackend(ModInventory inventory)
        {
            Inventory = inventory;
        }

        public string? BlockReason(string root, ManagedMod mod, ModInventory inventory)
        {
            if (inventory.Issues.Count > 0)
                return "先处理清单读取或重复 ID 问题，再启用/停用。";

            var enabled = inventory.Mods.Where(item => item.Enabled).ToArray();
            if (mod.Enabled)
            {
                var dependents = enabled
                    .Where(item => item.Folder != mod.Folder &&
                        item.Dependencies.Any(dependency =>
                            dependency.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (dependents.Length > 0)
                    return "这些启用项依赖它，请先停用依赖项：" +
                        string.Join("、", dependents.Select(item => item.Name));
            }
            else
            {
                foreach (var dependency in mod.Dependencies)
                {
                    var prerequisite = enabled.FirstOrDefault(item =>
                        item.Id.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase));
                    if (prerequisite is null)
                        return "请先启用前置模组：" + dependency.Id;
                }
            }

            return null;
        }

        public ModTogglePlan Preview(string root, string folder, bool enable)
        {
            PreviewAttempts.Add(folder);
            var mod = Inventory.Mods.Single(item =>
                item.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
            string? reason = BlockReason(root, mod, Inventory);
            if (reason is not null)
                throw new InvalidOperationException(reason);
            if (mod.Enabled == enable)
                throw new InvalidOperationException("模组状态已改变。");

            var plan = new ModTogglePlan(root, folder, enable, mod.Name,
                "fixture-token-" + PreviewAttempts.Count, 2);
            PreviewCompleted?.Invoke(folder);
            return plan;
        }

        public void Apply(ModTogglePlan plan)
        {
            ApplyCount++;
            BeforeApply?.Invoke(plan);
            Exception? failure = ApplyFailure?.Invoke(plan);
            if (failure is not null)
                throw failure;

            var current = Inventory.Mods.Single(item =>
                item.Folder.Equals(plan.Folder, StringComparison.OrdinalIgnoreCase));
            if (current.Enabled == plan.Enable)
                throw new InvalidOperationException("模组状态已改变。");

            Inventory = new ModInventory(
                Inventory.Mods.Select(item =>
                    item.Folder.Equals(plan.Folder, StringComparison.OrdinalIgnoreCase)
                        ? item with { Enabled = plan.Enable }
                        : item).ToList(),
                Inventory.Issues);
            AppliedOrder.Add(plan.Name);
        }
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

        // MOD toggle workflows: all paths are temporary and all file operations use a fake backend.
        string toggleRoot = Path.Combine(home, "toggle-game");
        Directory.CreateDirectory(toggleRoot);
        ManagedMod ToggleFixture(
            string id, string name, string folder, bool enabled, params ModDependency[] dependencies) =>
            new(id, name, "1.0.0", "fixture", "fixture description",
                folder, Path.Combine(folder, "manifest.json"), enabled, dependencies,
                enabled ? null : Path.Combine(home, "disabled", id),
                Path.Combine("mods", name));

        var singleMod = ToggleFixture(
            "fixture.toggle.single", "Single", Path.Combine(toggleRoot, "disabled", "single"), false);
        var singleBackend = new FakeModToggleBackend(new([singleMod], []));
        var toggleWorkflow = new ModToggleWorkflowService(singleBackend);
        var singlePlan = await toggleWorkflow.PreviewAsync(toggleRoot, singleMod.Folder, true);
        bool rejectedConfirmation = false;
        var rejectedSingle = await toggleWorkflow.ConfirmAndApplyAsync(
            singlePlan,
            (_, _) =>
            {
                rejectedConfirmation = true;
                return Task.FromResult(false);
            });
        int applyCallsAfterRejection = singleBackend.ApplyCount;
        var singleProgress = new List<bool>();
        var appliedSingle = await toggleWorkflow.ConfirmAndApplyAsync(
            singlePlan,
            (_, _) => Task.FromResult(true),
            progress => singleProgress.Add(progress.TransactionWriting));
        check(rejectedConfirmation && rejectedSingle.Cancelled &&
            applyCallsAfterRejection == 0 && singleBackend.ApplyCount == 1 && appliedSingle.Success &&
            singleBackend.AppliedOrder.SequenceEqual(["Single"]) &&
            singleProgress.SequenceEqual([true, false]),
            "single MOD workflow previews, delegates confirmation, leaves files untouched on rejection and returns the applied result");

        var baseMod = ToggleFixture(
            "fixture.toggle.base", "Base", Path.Combine(toggleRoot, "disabled", "base"), false);
        var dependentMod = ToggleFixture(
            "fixture.toggle.dependent", "Dependent", Path.Combine(toggleRoot, "disabled", "dependent"),
            false, new ModDependency("fixture.toggle.base"));
        var batchBackend = new FakeModToggleBackend(new([baseMod, dependentMod], []));
        var batchWorkflow = new ModToggleWorkflowService(batchBackend);
        var unknownSelected = ToggleFixture(
            "fixture.toggle.unknown", "Unknown", Path.Combine(toggleRoot, "unknown"), false);
        var batchPreview = await batchWorkflow.PrepareBatchAsync(
            toggleRoot,
            batchBackend.Inventory,
            [dependentMod, baseMod, unknownSelected, dependentMod],
            enable: true);
        bool batchPreflightDidNotWrite = batchBackend.ApplyCount == 0;
        var batchWatcher = new RecordingWatcher();
        bool batchWriting = false;
        int writingStartCount = 0;
        var batchResult = await batchWorkflow.ConfirmAndApplyBatchAsync(
            batchPreview,
            (_, _) => Task.FromResult(true),
            progress =>
            {
                if (progress.TransactionWriting && !batchWriting)
                {
                    batchWriting = true;
                    writingStartCount++;
                }
                else if (!progress.TransactionWriting && batchWriting)
                {
                    batchWriting = false;
                    batchWatcher.DeferQuietPeriod();
                }
            });
        check(batchPreview.ExecutionOrder.Select(step => step.Mod.Name)
                .SequenceEqual(["Base", "Dependent"]) &&
            batchPreview.Reviews.Any(review => review.Name == "Dependent" &&
                review.State == "需复核" && review.Explanation.Contains("前置项", StringComparison.Ordinal)) &&
            batchPreview.ExecutionOrder.Single(step => step.Mod.Name == "Dependent")
                .OrderExplanation.Contains("前置项", StringComparison.Ordinal) &&
            batchPreview.Reviews.Count(review => review.State == "跳过") == 2 &&
            batchPreflightDidNotWrite &&
            batchResult.SuccessCount == 2 && batchResult.SkippedCount == 2 &&
            batchBackend.AppliedOrder.SequenceEqual(["Base", "Dependent"]) &&
            writingStartCount == 1 && !batchWriting &&
            batchWatcher.Calls.SequenceEqual(["defer"]),
            "batch selection validation previews all items, enables prerequisites first and defers watcher quiet-period reconciliation after writes");

        var disablePreview = await batchWorkflow.PrepareBatchAsync(
            toggleRoot,
            batchBackend.Inventory,
            [batchBackend.Inventory.Mods.Single(mod => mod.Id == "fixture.toggle.base"),
             batchBackend.Inventory.Mods.Single(mod => mod.Id == "fixture.toggle.dependent")],
            enable: false);
        var disabledBatch = await batchWorkflow.ConfirmAndApplyBatchAsync(
            disablePreview, (_, _) => Task.FromResult(true));
        check(disablePreview.ExecutionOrder.Select(step => step.Mod.Name)
                .SequenceEqual(["Dependent", "Base"]) &&
            disabledBatch.SuccessCount == 2 &&
            batchBackend.AppliedOrder.TakeLast(2).SequenceEqual(["Dependent", "Base"]),
            "batch disable order processes dependents before their prerequisites");

        var previewCancelMods = new[]
        {
            ToggleFixture("fixture.toggle.cancel.a", "Cancel A", Path.Combine(toggleRoot, "cancel-a"), false),
            ToggleFixture("fixture.toggle.cancel.b", "Cancel B", Path.Combine(toggleRoot, "cancel-b"), false)
        };
        var previewCancelBackend = new FakeModToggleBackend(new(previewCancelMods.ToList(), []));
        using (var previewCancellation = new CancellationTokenSource())
        {
            previewCancelBackend.PreviewCompleted = _ => previewCancellation.Cancel();
            var cancelledPreview = await new ModToggleWorkflowService(previewCancelBackend).PrepareBatchAsync(
                toggleRoot, previewCancelBackend.Inventory, previewCancelMods, true,
                cancellationToken: previewCancellation.Token);
            check(cancelledPreview.Cancelled &&
                cancelledPreview.Reviews.All(review => review.State == "已取消") &&
                previewCancelBackend.ApplyCount == 0,
                "batch cancellation during preflight stops before the write phase and returns cancelled review entries");
        }

        var writeCancelMods = new[]
        {
            ToggleFixture("fixture.toggle.write.a", "Write A", Path.Combine(toggleRoot, "write-a"), false),
            ToggleFixture("fixture.toggle.write.b", "Write B", Path.Combine(toggleRoot, "write-b"), false)
        };
        var writeCancelBackend = new FakeModToggleBackend(new(writeCancelMods.ToList(), []));
        var writeCancelWorkflow = new ModToggleWorkflowService(writeCancelBackend);
        var writeCancelPreview = await writeCancelWorkflow.PrepareBatchAsync(
            toggleRoot, writeCancelBackend.Inventory, writeCancelMods, true);
        var applyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var releaseApply = new ManualResetEventSlim(false))
        using (var writeCancellation = new CancellationTokenSource())
        {
            writeCancelBackend.BeforeApply = plan =>
            {
                if (plan.Name == "Write A")
                {
                    applyEntered.TrySetResult(true);
                    releaseApply.Wait(TimeSpan.FromSeconds(5));
                }
            };
            var applying = writeCancelWorkflow.ConfirmAndApplyBatchAsync(
                writeCancelPreview, (_, _) => Task.FromResult(true),
                cancellationToken: writeCancellation.Token);
            await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writeCancellation.Cancel();
            releaseApply.Set();
            var writeCancelled = await applying.WaitAsync(TimeSpan.FromSeconds(5));
            check(writeCancelBackend.ApplyCount == 1 &&
                writeCancelled.SuccessCount == 1 &&
                writeCancelled.CancelledCount == 1 &&
                writeCancelled.Items.Single(item => item.Name == "Write A").State == "成功" &&
                writeCancelled.Items.Single(item => item.Name == "Write B").State == "已取消",
                "batch cancellation during a file move lets the active Domain transaction finish and stops only later items");
        }

        var partialMods = new[]
        {
            ToggleFixture("fixture.toggle.partial.a", "Partial A", Path.Combine(toggleRoot, "partial-a"), false),
            ToggleFixture("fixture.toggle.partial.b", "Partial B", Path.Combine(toggleRoot, "partial-b"), false),
            ToggleFixture("fixture.toggle.partial.c", "Partial C", Path.Combine(toggleRoot, "partial-c"), false)
        };
        var partialBackend = new FakeModToggleBackend(new(partialMods.ToList(), []))
        {
            ApplyFailure = plan => plan.Name == "Partial B"
                ? new IOException("isolated simulated write failure")
                : null
        };
        var partialWorkflow = new ModToggleWorkflowService(partialBackend);
        var partialPreview = await partialWorkflow.PrepareBatchAsync(
            toggleRoot, partialBackend.Inventory, partialMods, true);
        var partialResult = await partialWorkflow.ConfirmAndApplyBatchAsync(
            partialPreview, (_, _) => Task.FromResult(true));
        check(partialResult.SuccessCount == 2 && partialResult.FailedCount == 1 &&
            partialResult.SkippedCount == 0 &&
            partialBackend.AppliedOrder.SequenceEqual(["Partial A", "Partial C"]) &&
            partialResult.Items.Single(item => item.Name == "Partial B").State == "失败",
            "batch workflow continues independent items after one failure and returns per-item partial results");

        // MOD dependency diagnostics: graph inputs are in-memory fixtures and all paths stay under the test temp root.
        var dependencyDiagnostics = new ModDependencyDiagnosticsService();
        var dependencyBase = ToggleFixture(
            "fixture.dependency.base", "Dependency Base", Path.Combine(toggleRoot, "dependency-base"), false);
        var dependencyMiddle = ToggleFixture(
            "fixture.dependency.middle", "Dependency Middle", Path.Combine(toggleRoot, "dependency-middle"),
            false, new ModDependency(dependencyBase.Id));
        var dependencyTop = ToggleFixture(
            "fixture.dependency.top", "Dependency Top", Path.Combine(toggleRoot, "dependency-top"),
            false, new ModDependency(dependencyMiddle.Id));
        var dependencyInventory = new ModInventory(
            [dependencyBase, dependencyMiddle, dependencyTop], []);
        var dependencyReport = dependencyDiagnostics.Analyze(dependencyInventory);
        var dependencyEnableOrder = dependencyDiagnostics.CreateToggleOrder(
            dependencyInventory, [dependencyTop, dependencyMiddle, dependencyBase], true, dependencyReport);
        var dependencyDisableOrder = dependencyDiagnostics.CreateToggleOrder(
            dependencyInventory, [dependencyBase, dependencyMiddle, dependencyTop], false, dependencyReport);
        check(dependencyReport.Cycles.Length == 0 &&
            dependencyEnableOrder.Steps.Select(step => step.Mod.Name)
                .SequenceEqual(["Dependency Base", "Dependency Middle", "Dependency Top"]) &&
            dependencyDisableOrder.Steps.Select(step => step.Mod.Name)
                .SequenceEqual(["Dependency Top", "Dependency Middle", "Dependency Base"]) &&
            dependencyEnableOrder.Steps.Any(step => step.Explanation.Contains("前置项", StringComparison.Ordinal)) &&
            dependencyDisableOrder.Steps.Any(step => step.Explanation.Contains("依赖它的项目", StringComparison.Ordinal)),
            "dependency diagnostics resolve a multi-level graph, return prerequisite-first enable order, dependent-first disable order and explanations");

        var cycleA = ToggleFixture(
            "fixture.dependency.cycle.a", "Cycle A", Path.Combine(toggleRoot, "cycle-a"), false,
            new ModDependency("fixture.dependency.cycle.b"));
        var cycleB = ToggleFixture(
            "fixture.dependency.cycle.b", "Cycle B", Path.Combine(toggleRoot, "cycle-b"), false,
            new ModDependency("fixture.dependency.cycle.a"));
        var cycleInventory = new ModInventory([cycleA, cycleB], []);
        var cycleReport = dependencyDiagnostics.Analyze(cycleInventory);
        var cycleOrder = dependencyDiagnostics.CreateToggleOrder(
            cycleInventory, [cycleA, cycleB], enable: true, diagnostics: cycleReport);
        check(cycleReport.Cycles.Length == 1 &&
            cycleReport.Cycles[0].ModIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals([cycleA.Id, cycleB.Id]) &&
            cycleReport.Edges.All(edge => edge.IsCycle) &&
            cycleOrder.Steps.Count == 0 &&
            cycleOrder.CyclicFolders.SetEquals([cycleA.Folder, cycleB.Folder]),
            "dependency diagnostics report strongly connected MOD cycles and exclude them from toggle order");

        var missingDependent = ToggleFixture(
            "fixture.dependency.missing", "Missing Dependent", Path.Combine(toggleRoot, "missing-dependent"),
            false, new ModDependency("fixture.dependency.not-installed"));
        var disabledPrerequisite = ToggleFixture(
            "fixture.dependency.disabled-base", "Disabled Base", Path.Combine(toggleRoot, "disabled-base"), false);
        var disabledDependent = ToggleFixture(
            "fixture.dependency.disabled-child", "Disabled Dependent", Path.Combine(toggleRoot, "disabled-dependent"),
            false, new ModDependency(disabledPrerequisite.Id));
        var missingInventory = new ModInventory(
            [missingDependent, disabledPrerequisite, disabledDependent], []);
        var missingReport = dependencyDiagnostics.Analyze(missingInventory);
        check(missingReport.Edges.Any(edge =>
                edge.Dependent.Id == missingDependent.Id && edge.Status == ModDependencyStatus.Missing) &&
            missingReport.Edges.Any(edge =>
                edge.Dependent.Id == disabledDependent.Id &&
                edge.Status == ModDependencyStatus.DisabledPrerequisite &&
                edge.Prerequisite?.Id == disabledPrerequisite.Id),
            "dependency diagnostics distinguish an uninstalled prerequisite from a prerequisite that is installed but disabled");

        var versionPrerequisite = ToggleFixture(
            "fixture.dependency.version-base", "Version Base", Path.Combine(toggleRoot, "version-base"), true) with
            { Version = "1.5.0" };
        var versionMismatch = ToggleFixture(
            "fixture.dependency.version-low", "Version Mismatch", Path.Combine(toggleRoot, "version-mismatch"),
            false, new ModDependency(versionPrerequisite.Id, "2.0.0", "3.0.0"));
        var versionSatisfied = ToggleFixture(
            "fixture.dependency.version-ok", "Version Satisfied", Path.Combine(toggleRoot, "version-satisfied"),
            false, new ModDependency(versionPrerequisite.Id, "1.0.0", "2.0.0"));
        var versionInventory = new ModInventory(
            [versionPrerequisite, versionMismatch, versionSatisfied], []);
        var versionReport = dependencyDiagnostics.Analyze(versionInventory);
        var mismatchReadiness = dependencyDiagnostics.DiagnoseToggle(toggleRoot, versionMismatch, versionInventory);
        var satisfiedReadiness = dependencyDiagnostics.DiagnoseToggle(toggleRoot, versionSatisfied, versionInventory);
        check(versionReport.Edges.Any(edge =>
                edge.Dependent.Id == versionMismatch.Id && edge.Status == ModDependencyStatus.VersionMismatch) &&
            versionReport.Edges.Any(edge =>
                edge.Dependent.Id == versionSatisfied.Id && edge.Status == ModDependencyStatus.Satisfied) &&
            !mismatchReadiness.CanProceed &&
            mismatchReadiness.Kind == ModToggleBlockKind.Dependency &&
            satisfiedReadiness.CanProceed && satisfiedReadiness.Message is null,
            "dependency diagnostics share Domain version parsing for mismatched and compatible prerequisite ranges");

        // MOD inventory refresh: all backends below are fake and all paths are temporary.
        string refreshRoot = Path.Combine(home, "refresh-game");
        string refreshRoot2 = Path.Combine(home, "refresh-game-switched");
        string refreshData = Path.Combine(home, "refresh-data");
        Directory.CreateDirectory(refreshRoot);
        Directory.CreateDirectory(refreshRoot2);
        var emptyInventory = new ModInventory([], []);
        var refreshBackend = new FakeModInventoryRefreshBackend
        {
            RecoveryNotice = "隔离测试恢复提示"
        };
        var refreshService = new ModInventoryRefreshWorkflowService(
            refreshData, refreshBackend, TimeSpan.FromMilliseconds(1));
        var refreshStamp = new ModInventoryRefreshStamp(refreshRoot, 7, true);
        ModInventoryRefreshCheckpoint? refreshCheckpoint = null;
        bool refreshDeferred = false;
        var refreshed = await refreshService.RefreshAsync(
            refreshRoot, refreshStamp, () => refreshStamp,
            (_, _) => Task.FromResult(false),
            () => refreshDeferred = true,
            checkpoint => refreshCheckpoint = checkpoint);
        check(refreshed.ScanSucceeded && !refreshed.Cancelled && !refreshed.Stale &&
            !refreshed.InventoryDirty && refreshed.Inventory is not null &&
            refreshCheckpoint is { ClearDiscoveryCache: true } &&
            refreshCheckpoint.Inventory.Mods.Count == 0 &&
            refreshed.RecoveryNotice == "隔离测试恢复提示" &&
            refreshBackend.ObserveCount == 1 && !refreshDeferred,
            "MOD refresh scans in the background, clears dirty state, requests cache invalidation and returns recovery notices");

        // A file event arriving while Scan is running must discard the old result.
        var raceStamp = new ModInventoryRefreshStamp(refreshRoot, 10, true);
        var changedStamp = raceStamp;
        var raceBackend = new FakeModInventoryRefreshBackend
        {
            ScanHandler = _ =>
            {
                changedStamp = changedStamp with { ChangeVersion = 11, InventoryDirty = true };
                return emptyInventory;
            }
        };
        var raceService = new ModInventoryRefreshWorkflowService(
            refreshData, raceBackend, TimeSpan.FromMilliseconds(1));
        bool raceDeferred = false;
        var staleScan = await raceService.RefreshAsync(
            refreshRoot, raceStamp, () => changedStamp,
            (_, _) => Task.FromResult(false),
            () => raceDeferred = true);
        check(staleScan.Stale && staleScan.InventoryDirty && staleScan.Inventory is null &&
            raceDeferred && raceBackend.ObserveCount == 0,
            "MOD refresh rejects scan results when watcher version changes and defers the next quiet-period check");

        // The dirty flag is part of the scan stamp even if a monitor failure
        // cannot advance the ordinary filesystem event version.
        var dirtyStamp = new ModInventoryRefreshStamp(refreshRoot, 12, false);
        var dirtyCurrent = dirtyStamp;
        var dirtyBackend = new FakeModInventoryRefreshBackend
        {
            ScanHandler = _ =>
            {
                dirtyCurrent = dirtyCurrent with { InventoryDirty = true };
                return emptyInventory;
            }
        };
        var dirtyService = new ModInventoryRefreshWorkflowService(
            refreshData, dirtyBackend, TimeSpan.FromMilliseconds(1));
        bool dirtyDeferred = false;
        var dirtyResult = await dirtyService.RefreshAsync(
            refreshRoot, dirtyStamp, () => dirtyCurrent,
            (_, _) => Task.FromResult(false),
            () => dirtyDeferred = true);
        check(dirtyResult.Stale && dirtyResult.InventoryDirty &&
            dirtyResult.Inventory is null && dirtyDeferred,
            "MOD refresh rejects a result when the dirty marker changes without a watcher version increment");

        // Publishing a scan result is atomic with the watcher's version lock.
        using (var commitWatcher = new ModFileWatcherService(TimeSpan.FromMilliseconds(20)))
        {
            commitWatcher.Start(refreshRoot);
            bool firstCommitRan = false;
            bool staleCommitRan = false;
            bool currentCommitRan = false;
            bool firstCommit = commitWatcher.TryRunIfCurrent(
                refreshRoot, 0, () => firstCommitRan = true);
            commitWatcher.SimulateBufferOverflowForTests(refreshRoot);
            bool staleCommit = commitWatcher.TryRunIfCurrent(
                refreshRoot, 0, () => staleCommitRan = true);
            bool currentCommit = commitWatcher.TryRunIfCurrent(
                refreshRoot, 1, () => currentCommitRan = true);
            check(firstCommit && firstCommitRan && !staleCommit && !staleCommitRan &&
                currentCommit && currentCommitRan,
                "inventory publication is atomic with watcher versions and rejects a stale commit");
            commitWatcher.Stop();
        }

        // A directory switch cancels a pending scan; the blocked fake scan cannot publish its result.
        var scanEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var releaseScan = new ManualResetEventSlim(false))
        {
            var cancelBackend = new FakeModInventoryRefreshBackend
            {
                ScanHandler = _ =>
                {
                    scanEntered.TrySetResult(true);
                    releaseScan.Wait(TimeSpan.FromSeconds(5));
                    return emptyInventory;
                }
            };
            var cancelService = new ModInventoryRefreshWorkflowService(
                refreshData, cancelBackend, TimeSpan.FromMilliseconds(1));
            var oldStamp = new ModInventoryRefreshStamp(refreshRoot, 2, true);
            var newStamp = new ModInventoryRefreshStamp(refreshRoot2, 0, true);
            var currentStamp = oldStamp;
            var pending = cancelService.RefreshAsync(
                refreshRoot, oldStamp, () => currentStamp,
                (_, _) => Task.FromResult(false), () => { });
            await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            currentStamp = newStamp;
            cancelService.Cancel();
            releaseScan.Set();
            var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            check(cancelled.Cancelled && cancelled.InventoryDirty && cancelled.Inventory is null,
                "switching game directories cancels a pending refresh and prevents old-root results from being published");
        }

        // A newer refresh supersedes an older in-flight request.
        var firstScanEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var releaseFirstScan = new ManualResetEventSlim(false))
        {
            int scanNumber = 0;
            var duplicateBackend = new FakeModInventoryRefreshBackend
            {
                ScanHandler = _ =>
                {
                    int number = Interlocked.Increment(ref scanNumber);
                    if (number == 1)
                    {
                        firstScanEntered.TrySetResult(true);
                        releaseFirstScan.Wait(TimeSpan.FromSeconds(5));
                    }
                    return emptyInventory;
                }
            };
            var duplicateService = new ModInventoryRefreshWorkflowService(
                refreshData, duplicateBackend, TimeSpan.FromMilliseconds(1));
            var sameStamp = new ModInventoryRefreshStamp(refreshRoot, 4, false);
            var firstRequest = duplicateService.RefreshAsync(
                refreshRoot, sameStamp, () => sameStamp,
                (_, _) => Task.FromResult(false), () => { });
            await firstScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondRequest = duplicateService.RefreshAsync(
                refreshRoot, sameStamp, () => sameStamp,
                (_, _) => Task.FromResult(false), () => { });
            var secondResult = await secondRequest.WaitAsync(TimeSpan.FromSeconds(5));
            releaseFirstScan.Set();
            var firstResult = await firstRequest.WaitAsync(TimeSpan.FromSeconds(5));
            check(firstResult.Cancelled && secondResult.ScanSucceeded &&
                duplicateBackend.ScanCount == 2,
                "a repeated refresh cancels the previous request and only the latest request can publish");
        }

        // Candidate confirmation and cancellation both pass through the UI delegate.
        var orphanCandidate = new OrphanPatchCandidate(
            "fixture.patch", "Fixture Patch", ["fixture.target"]);
        var postStopInventory = new ModInventory([], []);
        var confirmedBackend = new FakeModInventoryRefreshBackend
        {
            ObserveHandler = (_, _, _, enabled) => enabled ? [orphanCandidate] : [],
            ReconcileHandler = (_, _) => new(["Fixture Patch"], [], postStopInventory)
        };
        var confirmedService = new ModInventoryRefreshWorkflowService(
            refreshData, confirmedBackend, TimeSpan.FromMilliseconds(1));
        bool confirmationCalled = false;
        bool writingStarted = false;
        bool writingFinished = false;
        var confirmedResult = await confirmedService.RefreshAsync(
            refreshRoot, new(refreshRoot, 5, false), () => new(refreshRoot, 5, false),
            (request, _) =>
            {
                confirmationCalled = request.Candidates.Single().PatchId == "fixture.patch";
                return Task.FromResult(true);
            },
            () => { },
            reportProgress: progress =>
            {
                if (progress.TransactionWriting) writingStarted = true;
                else if (writingStarted) writingFinished = true;
            });
        check(confirmationCalled && confirmedBackend.ReconcileCount == 1 &&
            confirmedResult.Inventory == postStopInventory &&
            confirmedResult.ClearLifecycleState &&
            confirmedResult.LifecycleInvalidationReason?.Contains("安全停用", StringComparison.Ordinal) == true &&
            confirmedResult.OrphanPatchNotice?.Contains("已自动停用 1 个", StringComparison.Ordinal) == true &&
            writingStarted && writingFinished,
            "orphan-patch confirmation executes the Domain reconcile and reports inventory/lifecycle state changes");

        var cancelledBackend = new FakeModInventoryRefreshBackend
        {
            ObserveHandler = (_, _, _, enabled) => enabled ? [orphanCandidate] : []
        };
        var cancelledService = new ModInventoryRefreshWorkflowService(
            refreshData, cancelledBackend, TimeSpan.FromMilliseconds(1));
        bool cancelConfirmationCalled = false;
        var cancelledCandidate = await cancelledService.RefreshAsync(
            refreshRoot, new(refreshRoot, 6, false), () => new(refreshRoot, 6, false),
            (_, _) =>
            {
                cancelConfirmationCalled = true;
                return Task.FromResult(false);
            },
            () => { });
        check(cancelConfirmationCalled && cancelledBackend.ReconcileCount == 0 &&
            !cancelledCandidate.ClearLifecycleState &&
            cancelledCandidate.Inventory?.Mods.Count == 0 &&
            cancelledCandidate.OrphanPatchNotice is null,
            "declining the orphan-patch confirmation leaves patch files untouched");

        // A watcher change during the grace period prevents confirmation and requests another quiet period.
        var delayBackend = new FakeModInventoryRefreshBackend
        {
            ObserveHandler = (_, _, _, enabled) => enabled ? [orphanCandidate] : []
        };
        var delayService = new ModInventoryRefreshWorkflowService(
            refreshData, delayBackend, TimeSpan.FromMilliseconds(80));
        var delayStamp = new ModInventoryRefreshStamp(refreshRoot, 8, false);
        var currentDelayStamp = delayStamp;
        bool confirmationAfterChange = false;
        bool deferredAfterChange = false;
        var delayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayRequest = delayService.RefreshAsync(
            refreshRoot, delayStamp, () => currentDelayStamp,
            (_, _) =>
            {
                confirmationAfterChange = true;
                return Task.FromResult(true);
            },
            () => deferredAfterChange = true,
            reportProgress: progress =>
            {
                if (progress.Status.StartsWith("发现可能", StringComparison.Ordinal))
                    delayStarted.TrySetResult(true);
            });
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        currentDelayStamp = currentDelayStamp with { ChangeVersion = 9, InventoryDirty = true };
        var delayResult = await delayRequest.WaitAsync(TimeSpan.FromSeconds(5));
        check(delayResult.Stale && deferredAfterChange && !confirmationAfterChange &&
            delayBackend.ReconcileCount == 0,
            "a watcher change during orphan-patch grace period defers confirmation and prevents stale auto-stop");

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
