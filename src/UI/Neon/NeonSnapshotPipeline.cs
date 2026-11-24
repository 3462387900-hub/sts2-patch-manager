using System.IO;
using System.Diagnostics;
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
    internal double NeonLastSnapshotApplyMs { get; private set; }
    internal double NeonLastSnapshotCaptureMs { get; private set; }
    internal Action? neonTestSnapshotBuildHook;

    sealed record NeonDependencyCache(ModInventory Identity, ModInventory Captured, long RootEpoch, int Generation,
        ModDependencyGraphReport Graph, IReadOnlyDictionary<string, string?>? BlockReasons);

    static bool SameNeonInventory(ModInventory a, ModInventory b)
    {
        if (a.Mods.Count != b.Mods.Count || !a.Issues.SequenceEqual(b.Issues)) return false;
        // The dependency report contains display names, paths and records as well as
        // dependency edges, so compare every scalar and dependency declaration.
        for (int i = 0; i < a.Mods.Count; i++)
        {
            var x = a.Mods[i]; var y = b.Mods[i];
            if ((x with { Dependencies = y.Dependencies }) != y ||
                !x.Dependencies.SequenceEqual(y.Dependencies)) return false;
        }
        return true;
    }

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
        try
        {
            bool dirty = modInventoryDirty || (selectedInstallRoot != null && modInventory != null &&
                modFileWatcher.ChangeVersion != modInventoryCleanVersion);
            neonWorkbenchView.WithdrawObsoleteNeonHealth(selectedInstallRoot, dirty,
                lifecycleStale || !HasValidLifecycleReport, lifecycleStatus, neonScanning);
        }
        catch (Exception ex)
        {
            neonWorkbenchView.ShowToast("界面状态撤销未完成：" + ex.Message, NeonTone.Amber);
            return Task.FromResult(false);
        }
        neonWorkbenchView.SetRecoveryWarning(NeonRecoveryWarning(selectedInstallRoot));
        NeonSchedulePendingRescan();
        if (neonSnapshotPump == null || neonSnapshotPump.IsCompleted)
            neonSnapshotPump = DrainNeonSnapshotsAsync();
        return neonSnapshotPump;
    }

    async Task<bool> DrainNeonSnapshotsAsync()
    {
        // Coalesce synchronous refresh bursts and ensure the pump field is assigned first.
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        while (UiThemes.IsNeon && neonWorkbenchView != null && !neonWindowClosing && !Dispatcher.HasShutdownStarted)
        {
            long request = neonSnapshotRequest;
            try
            {
            var captureWatch = Stopwatch.StartNew();
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
                new(identity.Mods.Select(mod => mod with { Dependencies = mod.Dependencies.ToArray() }).ToList(), identity.Issues.ToList());
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
            NeonLastSnapshotCaptureMs = captureWatch.Elapsed.TotalMilliseconds;
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
                        if (cache == null || cache.RootEpoch != rootEpoch ||
                            !SameNeonInventory(cache.Captured, inventory))
                        {
                            var diag = new ModDependencyDiagnosticsService().Analyze(inventory);
                            var graph = new ModDependencyGraphReport(
                                diag.Edges.Select(e => new ModDependencyEdge(e.Dependent, e.DependencyId, e.DependencyName,
                                    e.Prerequisite, e.Status, e.Message, e.FromPatchRule, e.IsCycle,
                                    e.DisabledVersionFits, e.MinimumVersion, e.MaximumVersion)).ToArray(),
                                diag.Cycles.Select(c => new ModDependencyCycle(c.ModIds, c.Names)).ToArray(),
                                diag.Summaries.ToDictionary(kv => kv.Key, kv => new ModDependencySummary(
                                    kv.Value.DependencyCount, kv.Value.UnsatisfiedCount, kv.Value.ActiveIssueCount,
                                    kv.Value.CycleEdgeCount), StringComparer.OrdinalIgnoreCase), diag.ActiveIssueCount);
                            cache = new(identity, inventory, rootEpoch, generation, graph, null);
                            rebuiltGraph = true;
                        }
                        else if (!ReferenceEquals(cache.Identity, identity) || cache.Generation != generation)
                        {
                            // Identical manifests may keep the graph; never keep a stale
                            // filesystem occupancy / toggle preflight result across scans.
                            cache = cache with { Identity = identity, Captured = inventory,
                                Generation = generation, BlockReasons = null };
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
                    ReferenceEquals(identity, modInventory) && ReferenceEquals(patchReport, report) &&
                    scanning == neonScanning && healthScanning == lifecycleScanning &&
                    dirty == (modInventoryDirty || (root != null && modInventory != null && generation != modInventoryCleanVersion));

                bool published = false;
                void Publish()
                {
                    if (!Current()) return;
                    bool latestRunning = root != null && string.Equals(neonGameRunningRoot, root, StringComparison.OrdinalIgnoreCase) && neonGameRunningCached;
                    if (latestRunning != running) return;
                    neonDependencyCache = built.cache;
                    neonRecoveryCacheRoot = root;
                    neonCachedSnapshots = built.loaded.Snapshots;
                    neonCachedSnapshotsWarning = built.loaded.Warning;
                    neonCachedLatestPatchBatch = built.loaded.Batch;
                    neonRecoveryCacheDirty = false;
                    neonCachedGitHubNetwork ??= built.network;
                    neonCachedOrphanAutoStop ??= built.orphan;
                    var applyWatch = Stopwatch.StartNew();
                    neonWorkbenchView!.ApplySnapshot(built.snapshot, preserve);
                    NeonLastSnapshotApplyMs = applyWatch.Elapsed.TotalMilliseconds;
                    neonWorkbenchView.SetRecoveryWarning(NeonRecoveryWarning(root));
                    published = true;
                }
                if (root == null) Publish();
                else if (string.Equals(root, modFileWatcher.CurrentRoot, StringComparison.OrdinalIgnoreCase))
                    modFileWatcher.TryRunIfCurrent(root, generation, Publish);
                else if (dirty && modFileWatcher.CurrentRoot == null)
                    modFileWatcher.TryRunIfCurrent(root, generation, Publish);
                if (published && request == neonSnapshotRequest) return true;
                if (!published)
                {
                    NeonSnapshotDiscardCount++;
                    if (UiThemes.IsNeon && !neonWindowClosing && root != null &&
                        string.Equals(root, selectedInstallRoot, StringComparison.OrdinalIgnoreCase) &&
                        generation != modFileWatcher.ChangeVersion)
                        neonWorkbenchView?.WithdrawObsoleteNeonHealth(root, true, true,
                            "文件监测世代已变化，旧快照已丢弃。", neonScanning);
                }
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
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        }
        return false;
    }
}
