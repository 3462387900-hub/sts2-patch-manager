using System.IO;
using System.Net.Http;
using System.Windows.Threading;

namespace SpirePatch;

public partial class MainWindow
{
    long neonSnapshotRequest;
    long neonRecoveryRevision;
    Task<bool>? neonSnapshotPump;
    bool neonSnapshotPreserveViewport = true;
    NeonDependencyCache? neonDependencyCache;
    internal long NeonDependencyBuildCount { get; private set; }
    internal long NeonSnapshotDiscardCount { get; private set; }
    internal Action? neonTestSnapshotBuildHook;

    sealed record NeonDependencyCache(ModInventory Identity, long RootEpoch, int Generation,
        ModDependencyGraphReport Graph, IReadOnlyDictionary<string, string?>? BlockReasons);

    sealed class SnapshotNoHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("Snapshot projection may not send HTTP requests."));
    }

    internal Task<bool> RefreshNeonWorkbenchAsync(bool preserveViewport = true)
    {
        Dispatcher.VerifyAccess();
        if (!UiThemes.IsNeon || neonWorkbenchView == null || neonWindowClosing || Dispatcher.HasShutdownStarted)
            return Task.FromResult(false);

        neonSnapshotRequest++;
        neonSnapshotPreserveViewport = preserveViewport;
        // Safety is synchronous even though the expensive projection is not.
        neonWorkbenchView.WithdrawObsoleteNeonHealth(selectedInstallRoot, modInventoryDirty,
            lifecycleStale || !HasValidLifecycleReport, lifecycleStatus, neonScanning);
        neonWorkbenchView.SetRecoveryWarning(NeonRecoveryWarning(selectedInstallRoot));
        NeonSchedulePendingRescan();
        if (neonSnapshotPump == null || neonSnapshotPump.IsCompleted)
            neonSnapshotPump = DrainNeonSnapshotsAsync();
        return neonSnapshotPump;
    }

    async Task<bool> DrainNeonSnapshotsAsync()
    {
        // Coalesce synchronous refresh bursts and ensure the pump field is assigned first.
        await Dispatcher.Yield(DispatcherPriority.Background);
        while (UiThemes.IsNeon && neonWorkbenchView != null && !neonWindowClosing && !Dispatcher.HasShutdownStarted)
        {
            long request = neonSnapshotRequest;
            bool preserve = neonSnapshotPreserveViewport;
            string? root = selectedInstallRoot;
            long rootEpoch = Volatile.Read(ref neonRootEpoch);
            int generation = modFileWatcher.ChangeVersion;
            int healthEpoch = lifecycleEpoch;
            long recoveryRevision = neonRecoveryRevision;
            var identity = modInventory;
            bool dirty = modInventoryDirty || (root != null && identity != null && generation != modInventoryCleanVersion);
            // Scan publishes new inventory objects; copy mutable containers before releasing the UI thread.
            ModInventory? inventory = identity == null ? null :
                new(identity.Mods.ToList(), identity.Issues.ToList());
            var patchReport = report;
            var healthReport = HasValidLifecycleReport && !dirty ? lifecycleReport : null;
            bool healthStale = lifecycleStale || dirty;
            bool healthScanning = lifecycleScanning;
            string healthStatus = lifecycleStatus;
            bool scanning = neonScanning;
            string? scanStage = neonScanStageText;
            bool running = root != null && string.Equals(neonGameRunningRoot, root, StringComparison.OrdinalIgnoreCase) && neonGameRunningCached;
            if (root != null)
                _ = PollNeonGameRunningAsync(root, force: !string.Equals(neonGameRunningRoot, root, StringComparison.OrdinalIgnoreCase));

            string dataRoot = NeonDataRoot;
            // Live cache objects are Dispatcher-owned. Copy their record values only;
            // the worker creates its own local-file readers if a store has not been loaded.
            bool hasGh = root != null && string.Equals(githubRoot, root, StringComparison.OrdinalIgnoreCase) && githubModUpdates != null;
            bool hasWs = root != null && string.Equals(workshopSourceRoot, root, StringComparison.OrdinalIgnoreCase) && workshopModSources != null;
            bool hasWsChecks = root != null && string.Equals(workshopUpdatesRoot, root, StringComparison.OrdinalIgnoreCase) && workshopModUpdates != null;
            var sourceRows = new Dictionary<string, NeonSourceProjection>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in inventory?.Mods ?? [])
            {
                var discovered = githubDiscovery?.Cached(mod);
                if (discovered != null) discovered = discovered with { Candidates = discovered.Candidates.ToArray() };
                sourceRows[mod.Manifest] = new(
                    hasGh ? githubModUpdates!.Repository(mod.Id) : null,
                    hasWs ? workshopModSources!.Get(mod.Id) : null,
                    hasGh ? githubModUpdates!.Cached(mod.Id) : null,
                    hasWsChecks ? workshopModUpdates!.Cached(mod.Id) : null, discovered);
            }
            var bound = hasWs ? workshopModSources!.SnapshotBoundWorkshopIds() : Array.Empty<ulong>();
            bool recoveryDirty = neonRecoveryCacheDirty || !string.Equals(root, neonRecoveryCacheRoot, StringComparison.OrdinalIgnoreCase);
            var recovery = (Snapshots: neonCachedSnapshots, Warning: neonCachedSnapshotsWarning, Batch: neonCachedLatestPatchBatch);
            var activity = neonActivityLog.ToArray();
            var coop = BuildNeonCoopSnapshotState();
            var manager = BuildNeonManagerUpdateSnapshotState();
            var network = neonCachedGitHubNetwork;
            bool? orphan = neonCachedOrphanAutoStop;
            bool online = onlineBusy, checking = modUpdateCheckRunning;
            string onlineLabel = onlineStatus, lastCheck = neonLastUpdateCheckTimeLabel;
            int onlineCount = onlineCatalog?.Patches.Count ?? Catalog.Bundled.Length;
            var depCache = neonDependencyCache;
            var testHook = neonFixtureDataRoot != null ? neonTestSnapshotBuildHook : null;
            try
            {
                var built = await Task.Run(() =>
                {
                    testHook?.Invoke();
                    var sources = sourceRows;
                    if (root != null && (!hasGh || !hasWs))
                    {
                        using var ghReader = !hasGh ? new GitHubModUpdates(dataRoot, root, new SnapshotNoHttpHandler()) : null;
                        var wsReader = !hasWs ? new WorkshopModSources(dataRoot, root) : null;
                        if (wsReader != null) bound = wsReader.SnapshotBoundWorkshopIds();
                        foreach (var mod in inventory?.Mods ?? [])
                        {
                            var old = sources[mod.Manifest];
                            sources[mod.Manifest] = old with
                            {
                                Repository = hasGh ? old.Repository : ghReader?.Repository(mod.Id),
                                WorkshopId = hasWs ? old.WorkshopId : wsReader?.Get(mod.Id)
                            };
                        }
                    }
                    bool rebuiltGraph = false;
                    NeonDependencyCache? cache = null;
                    if (identity != null && inventory != null)
                    {
                        cache = depCache;
                        if (cache == null || !ReferenceEquals(cache.Identity, identity) ||
                            cache.RootEpoch != rootEpoch || cache.Generation != generation)
                        {
                            var diag = ModDependencyDiagnostics.Analyze(inventory);
                            var graph = new ModDependencyGraphReport(
                                diag.Edges.Select(e => new ModDependencyEdge(e.Dependent, e.DependencyId, e.DependencyName,
                                    e.Prerequisite, e.Status, e.Message, e.FromPatchRule, e.IsCycle,
                                    e.DisabledVersionFits, e.MinimumVersion, e.MaximumVersion)).ToArray(),
                                diag.Cycles.Select(c => new ModDependencyCycle(c.ModIds, c.Names)).ToArray(),
                                diag.Summaries.ToDictionary(kv => kv.Key, kv => new ModDependencySummary(
                                    kv.Value.DependencyCount, kv.Value.UnsatisfiedCount, kv.Value.ActiveIssueCount,
                                    kv.Value.CycleEdgeCount), StringComparer.OrdinalIgnoreCase), diag.ActiveIssueCount);
                            cache = new(identity, rootEpoch, generation, graph, null);
                            rebuiltGraph = true;
                        }
                        if (!dirty && !running && root != null && cache.BlockReasons == null)
                        {
                            var reasons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                            foreach (var mod in inventory.Mods)
                            {
                                try { reasons[mod.Manifest] = ModManager.BlockReason(root, mod, inventory); }
                                catch (Exception ex) { reasons[mod.Manifest] = ex.Message; }
                            }
                            cache = cache with { BlockReasons = reasons };
                        }
                    }
                    var loaded = recoveryDirty ? LoadRecoveryCacheSync(root, dataRoot) : recovery;
                    var recoveryState = new NeonRecoverySnapshotState(loaded.Snapshots, loaded.Warning, loaded.Batch,
                        (inventory?.Mods ?? []).Where(x => !x.Enabled)
                            .OrderBy(x => ModPresentation.IsIndependentPatch(x) ? 1 : 0)
                            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), activity);
                    if (network == null)
                    {
                        var cfg = GitHubNetwork.Load(dataRoot);
                        network = new(cfg.Mode, cfg.ProxyUrl);
                    }
                    orphan ??= OrphanPatchAutoStop.Enabled(dataRoot);
                    var snapshot = NeonWorkbenchBuilder.Build(root, running, inventory, cache?.Graph,
                        patchReport, healthReport, dirty, healthStale, healthScanning, healthStatus,
                        null, null, null, null, orphan.Value, online, onlineLabel, onlineCount,
                        checking, lastCheck, coop, recoveryState, manager, network, scanning, scanStage,
                        sources, bound.ToHashSet(), cache?.BlockReasons);
                    return (snapshot, cache, loaded, network, orphan, rebuiltGraph);
                });

                if (built.rebuiltGraph) NeonDependencyBuildCount++;
                bool Current() => UiThemes.IsNeon && !neonWindowClosing && !Dispatcher.HasShutdownStarted &&
                    request == neonSnapshotRequest && rootEpoch == Volatile.Read(ref neonRootEpoch) &&
                    generation == modFileWatcher.ChangeVersion && healthEpoch == lifecycleEpoch &&
                    recoveryRevision == neonRecoveryRevision &&
                    string.Equals(root, selectedInstallRoot, StringComparison.OrdinalIgnoreCase) &&
                    ReferenceEquals(identity, modInventory) &&
                    dirty == (modInventoryDirty || (root != null && modInventory != null && generation != modInventoryCleanVersion));

                bool published = false;
                void Publish()
                {
                    if (!Current()) return;
                    neonDependencyCache = built.cache;
                    neonRecoveryCacheRoot = root;
                    neonCachedSnapshots = built.loaded.Snapshots;
                    neonCachedSnapshotsWarning = built.loaded.Warning;
                    neonCachedLatestPatchBatch = built.loaded.Batch;
                    neonRecoveryCacheDirty = false;
                    neonCachedGitHubNetwork ??= built.network;
                    neonCachedOrphanAutoStop ??= built.orphan;
                    bool latestRunning = root != null && string.Equals(neonGameRunningRoot, root, StringComparison.OrdinalIgnoreCase) && neonGameRunningCached;
                    // A process-state change while building requires rebuilding row lock reasons.
                    if (latestRunning != running) return;
                    neonWorkbenchView!.ApplySnapshot(built.snapshot, preserve);
                    neonWorkbenchView.SetRecoveryWarning(NeonRecoveryWarning(root));
                    published = true;
                }
                if (root == null) Publish();
                else modFileWatcher.TryRunIfCurrent(root, generation, Publish);
                if (published && request == neonSnapshotRequest) return true;
                if (!published) NeonSnapshotDiscardCount++;
                if (request == neonSnapshotRequest && (!UiThemes.IsNeon || neonWindowClosing)) return false;
                // A stopped/mismatched watcher cannot certify a clean view; let the next
                // root/scan request restart the pump rather than spinning.
                if (root != null && !string.Equals(root, modFileWatcher.CurrentRoot, StringComparison.OrdinalIgnoreCase) &&
                    request == neonSnapshotRequest) return false;
            }
            catch (Exception ex)
            {
                if (request != neonSnapshotRequest) continue;
                neonWorkbenchView?.ShowToast("界面快照刷新未完成：" + ex.Message + "，请手动刷新。", NeonTone.Amber);
                return false;
            }
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        return false;
    }
}
