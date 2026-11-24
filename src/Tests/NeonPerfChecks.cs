using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpirePatch;

public partial class MainWindow
{
    internal async Task RunNeonPerfChecksAsync(string reportPath)
    {
        reportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string parent = Path.Combine(Path.GetTempPath(), "spire-neon-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var lines = new List<string>();
        // Watcher callbacks run off the Dispatcher. Never write the report or
        // touch WPF state from those callbacks. Only the test thread drains them.
        var watcherEvents = new ConcurrentQueue<ModFileChangeEventArgs>();
        void CaptureWatcherEvent(object? sender, ModFileChangeEventArgs e) => watcherEvents.Enqueue(e);
        void DrainWatcherEvents(string label)
        {
            var batch = new List<string>();
            while (watcherEvents.TryDequeue(out var e))
                batch.Add($"INFO watcher_event phase={label} generation={e.Version} kind={e.ChangeType} " +
                    $"mask={e.NotifyMask} root=\"{e.Root}\" watch=\"{e.WatchPath}\" path=\"{e.ChangedPath}\" " +
                    $"old_path=\"{e.OldPath}\" overflow={e.Overflow} tree_changed={e.TreeChanged} error={e.Error}");
            if (batch.Count == 0) return;
            lines.AddRange(batch);
            File.WriteAllLines(reportPath, lines);
        }
        void Log(string line)
        {
            lines.Add(line);
            File.WriteAllLines(reportPath, lines);
        }
        void Check(bool ok, string label)
        {
            Log((ok ? "PASS " : "FAIL ") + label);
            if (!ok) throw new InvalidOperationException(label);
        }
        string ScanState(NeonScanStamp expected) =>
            $"expected_root=\"{expected.Root}\" expected_epoch={expected.RootEpoch} expected_generation={expected.Generation} " +
            $"actual_selected_root=\"{selectedInstallRoot ?? "<null>"}\" actual_watcher_root=\"{modFileWatcher.CurrentRoot ?? "<null>"}\" " +
            $"actual_epoch={Volatile.Read(ref neonRootEpoch)} actual_generation={modFileWatcher.ChangeVersion} " +
            $"dirty={modInventoryDirty} clean_generation={modInventoryCleanVersion} ui_generation={Volatile.Read(ref modChangeVersion)} " +
            $"overflow={modWatchOverflow} tree_changed={modWatchTreeChanged} busy={busy} transaction_writing={transactionWriting} " +
            $"refresh_running={modInventoryRefreshRunning} status=\"{modStatus}\"";

        Action<NeonScanStamp, string> ScanDiagnostic(string label) =>
            (stamp, phase) =>
            {
                DrainWatcherEvents(label + "-" + phase);
                Log($"INFO scan={label} phase={phase} {ScanState(stamp)}");
            };

        // Use the real watcher. Wait for a full quiet interval, not an arbitrary
        // generation value or a reset of the clean watermark. Never stop the watcher.
        async Task AwaitFixtureWatchQuietAsync(string root, long epoch, string label)
        {
            var stamp = NeonCaptureScanStamp(root);
            var stable = Stopwatch.StartNew();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (epoch != Volatile.Read(ref neonRootEpoch) ||
                    !NeonRootMatches(root) ||
                    !string.Equals(modFileWatcher.CurrentRoot, root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{label}: fixture watcher/root session changed. {ScanState(stamp)}");
                int generation = modFileWatcher.ChangeVersion;
                if (generation != stamp.Generation)
                {
                    Log($"INFO {label} watcher advanced while settling {ScanState(stamp)}");
                    stamp = NeonCaptureScanStamp(root);
                    stable.Restart();
                }
                if (stable.Elapsed >= TimeSpan.FromMilliseconds(1100))
                {
                    // Drain already queued watcher notifications before taking the scan stamp.
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (NeonScanStampCurrent(stamp) && epoch == stamp.RootEpoch)
                    {
                        Log($"INFO {label} watcher settled {ScanState(stamp)}");
                        return;
                    }
                    stable.Restart();
                }
                await Task.Delay(50);
            }
            throw new TimeoutException($"{label}: watcher did not settle. {ScanState(stamp)}");
        }

        // Real file notifications must still be accepted, including same-length
        // writes and moves. The separate watcher exercises the production filters,
        // without touching the benchmark root or the application's scan state.
        async Task VerifyContentNotificationsAsync()
        {
            string root = Path.Combine(parent, "watcher-regression");
            string mods = Path.Combine(root, "mods");
            string disabled = Path.Combine(root, ModManager.StorageName);
            Directory.CreateDirectory(mods);
            Directory.CreateDirectory(disabled);
            string file = Path.Combine(mods, "content.dll");
            string moved = Path.Combine(mods, "renamed.dll");
            string disabledFile = Path.Combine(disabled, "record.json");
            File.WriteAllText(file, "AAAA");
            File.WriteAllText(disabledFile, "AAAA");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(disabledFile, DateTime.UtcNow.AddMinutes(-5));
            using var watcher = new ModFileWatcherService();
            var observed = new ConcurrentQueue<ModFileChangeEventArgs>();
            watcher.ChangeDetected += (_, e) => observed.Enqueue(e);
            watcher.Start(root);
            Check(string.Equals(watcher.CurrentRoot, root, StringComparison.OrdinalIgnoreCase), "watcher regression mounted");
            async Task Expect(string name, string path, WatcherChangeTypes kind, Action change)
            {
                int before = watcher.ChangeVersion;
                await Task.Run(change);
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(5) &&
                    !observed.Any(e => e.Version > before && e.ChangeType == kind &&
                        string.Equals(e.ChangedPath, path, StringComparison.OrdinalIgnoreCase)))
                    await Task.Delay(25);
                bool accepted = observed.Any(e => e.Version > before && e.ChangeType == kind &&
                    string.Equals(e.ChangedPath, path, StringComparison.OrdinalIgnoreCase));
                Check(accepted && watcher.ChangeVersion > before, "watcher real " + name);
            }
            static void OverwriteInPlace(string path)
            {
                // No truncation or size change: exercise LastWrite independently.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.Write("BBBB"u8);
                stream.Flush(flushToDisk: true);
            }
            await Expect("same-size active content write", file, WatcherChangeTypes.Changed,
                () => OverwriteInPlace(file));
            await Expect("same-size disabled content write", disabledFile, WatcherChangeTypes.Changed,
                () => OverwriteInPlace(disabledFile));
            await Expect("file rename", moved, WatcherChangeTypes.Renamed, () => File.Move(file, moved));
            await Expect("file delete", moved, WatcherChangeTypes.Deleted, () => File.Delete(moved));
            await Expect("file create", file, WatcherChangeTypes.Created, () => File.WriteAllText(file, "CCCC"));
            // The top-level name watcher must still notice the owned tree disappearing.
            await Expect("owned tree removal", disabled, WatcherChangeTypes.Deleted,
                () => Directory.Delete(disabled, recursive: true));
        }

        async Task LayoutAsync()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
        }
        // Render the real WPF visual into a bitmap, including measure/arrange and draw;
        // off-screen render latency is not a claim about physical monitor presentation.
        void Draw(NeonWorkbenchView view)
        {
            int width = Math.Max(1, (int)Math.Ceiling(view.ActualWidth));
            int height = Math.Max(1, (int)Math.Ceiling(view.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
        }
        double peakGap = 0;
        long lastTick = Stopwatch.GetTimestamp();
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            peakGap = Math.Max(peakGap, Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds);
            lastTick = now;
        };
        void ResetHeartbeat() { peakGap = 0; lastTick = Stopwatch.GetTimestamp(); }
        async Task<double> FinishHeartbeat()
        {
            await Task.Delay(30);
            return Math.Max(0, peakGap - 10);
        }
        void Metric(int n, string phase, double elapsed, double ui, double delay, NeonWorkbenchView view)
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            Log(FormattableString.Invariant($"METRIC count={n} phase={phase} elapsed_ms={elapsed:F3} ui_sync_ms={ui:F3} dispatcher_max_late_ms={delay:F3} working_set_bytes={proc.WorkingSet64} private_bytes={proc.PrivateMemorySize64} managed_bytes={GC.GetTotalMemory(false)} realized_rows={view.NeonRealizedModRows} built_rows_total={view.NeonModRowBuildCount}"));
        }
        int CountVisualRows(NeonWorkbenchView view) => view.DeskContentControl.Children
            .OfType<Border>().Count(b => b.Tag is string id && id.StartsWith("PerfMod", StringComparison.Ordinal));
        void Bound(NeonWorkbenchView view, int n, string phase) =>
            Check(CountVisualRows(view) == view.NeonRealizedModRows &&
                CountVisualRows(view) <= NeonWorkbenchView.NeonModPageSize &&
                CountVisualRows(view) < n, $"{n} {phase} actual WPF card count bounded to 40");

        string MakeRoot(int n)
        {
            string root = Path.Combine(parent, "game-" + n);
            Directory.CreateDirectory(Path.Combine(root, "mods"));
            // Both MOD-owned trees exist before FileSystemWatcher starts, just as in
            // the existing Neon fixture with a pre-disabled mod.
            Directory.CreateDirectory(Path.Combine(root, ModManager.StorageName));
            Directory.CreateDirectory(Path.Combine(root, "data_sts2_windows_x86_64"));
            File.WriteAllBytes(Path.Combine(root, "SlayTheSpire2.exe"), []);
            File.WriteAllText(Path.Combine(root, ".spirepatch-fixture"), "isolated");
            File.WriteAllText(Path.Combine(root, "data_sts2_windows_x86_64", "sts2.dll"), "performance fixture");
            for (int i = 0; i < n; i++)
            {
                string id = $"PerfMod{i:D4}";
                string dir = Path.Combine(root, "mods", id);
                Directory.CreateDirectory(dir);
                object[] deps = i >= 20 && i % 5 == 0
                    ? new[] { new { id = "PerfMod0000", min_version = "1.0.0" } }
                    : Array.Empty<object>();
                File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(new
                {
                    id, name = id, version = "1.0.0", author = "Fixture",
                    description = i < n - 5 ? "PerfGroup fixture" : "Other fixture",
                    has_dll = true, dependencies = deps
                }));
                File.WriteAllText(Path.Combine(dir, id + ".dll"), id + " isolated payload");
            }
            return GameEnvironment.Root(root);
        }

        try
        {
            Log("INFO WPF off-screen render; dispatcher lateness = largest 10ms Input timer gap minus 10ms; ui_sync_ms = measured synchronous UI phase, not total background time. Memory is absolute process/managed heap, not per-row allocation.");
            Log("INFO no HTTP is needed; isolated game and manager data only; P1 recovery and guarded inventory publication are exercised.");
            neonFixtureDataRoot = Path.Combine(parent, "manager-data");
            Directory.CreateDirectory(neonFixtureDataRoot);
            neonTestHoldRescan = true;
            await VerifyContentNotificationsAsync();
            modFileWatcher.ChangeDetected += CaptureWatcherEvent;
            // Build every fixture before starting the watcher for its root.
            var roots = await Task.Run(() => new[] { 50, 200, 500 }.ToDictionary(n => n, MakeRoot));
            heartbeat.Start();
            foreach (int n in new[] { 50, 200, 500 })
            {
                string root = roots[n];
                neonFixtureRoot = root;
                // Mount, recover and scan are one isolated initialization operation.
                // The real quiet-period handler respects busy, so it cannot launch a
                // competing RefreshMods during the initial scan.
                busy = true;
                try
                {
                    UseGameRoot(root);
                    var mounted = NeonCaptureScanStamp(root);
                    Log($"INFO {n} mounted {ScanState(mounted)}");
                    Check(NeonScanStampCurrent(mounted), $"{n} initial watcher mounted");
                    // On subsequent sizes, root selection can start a Neon snapshot
                    // worker that reads the recovery list under the game's mutex.
                    // Finish that worker before starting recovery and the scan.
                    if (neonSnapshotPump is { } pending) await pending;
                    await AwaitFixtureWatchQuietAsync(root, mounted.RootEpoch, $"{n} initial");
                    // No live discovery, update checks or lifecycle HTTP.
                    await NeonRecoverTransactionsAsync(root);
                    Check(NeonRecoveryChecked(root) && !NeonRecoveryBlocked(root), $"{n} isolated P1 recovery gate");
                    await AwaitFixtureWatchQuietAsync(root, mounted.RootEpoch, $"{n} post-recovery");
                    if (neonSnapshotPump is { } afterRecovery) await afterRecovery;
                    Check(!modInventoryRefreshRunning && NeonRootMatches(root) &&
                        string.Equals(modFileWatcher.CurrentRoot, root, StringComparison.OrdinalIgnoreCase) &&
                        Volatile.Read(ref neonRootEpoch) == mounted.RootEpoch,
                        $"{n} no competing refresh and root session unchanged");
                    // Independent single-mask probes on the first isolated scan:
                    // do not feed production generation, do not open watched files.
                    // Record even when publication fails, so the next log identifies
                    // whether the filesystem emitted CreationTime, LastWrite or Size.
                    var probeEvents = new ConcurrentQueue<(NotifyFilters Mask, string Path)>();
                    var probes = new List<FileSystemWatcher>();
                    var initial = NeonCaptureScanStamp(root);
                    bool accepted;
                    try
                    {
                        if (n == 50)
                        {
                            foreach (var mask in new[] { NotifyFilters.CreationTime, NotifyFilters.LastWrite, NotifyFilters.Size })
                            {
                                var probe = new FileSystemWatcher(Path.Combine(root, "mods"))
                                { IncludeSubdirectories = true, NotifyFilter = mask };
                                probes.Add(probe);
                                probe.Changed += (_, e) => probeEvents.Enqueue((mask, e.FullPath));
                                probe.EnableRaisingEvents = true;
                            }
                        }
                        accepted = await NeonScanAndPublishAsync(root, ScanDiagnostic($"{n} initial"));
                        // Catch notifications delivered shortly after the scan, not
                        // just those that happened before guarded publication.
                        await Task.Delay(1100);
                    }
                    finally
                    {
                        foreach (var probe in probes) probe.Dispose();
                        DrainWatcherEvents($"{n}-initial-after-quiet");
                        if (n == 50)
                        {
                            foreach (var mask in new[] { NotifyFilters.CreationTime, NotifyFilters.LastWrite, NotifyFilters.Size })
                                Log($"INFO native_probe mask={mask} count={probeEvents.Count(e => e.Mask == mask)}");
                            foreach (var e in probeEvents) Log($"INFO native_probe mask={e.Mask} path=\"{e.Path}\"");
                        }
                    }
                    Check(accepted, $"{n} initial guarded inventory publication");
                    Check(NeonScanStampCurrent(initial) && !modInventoryDirty,
                        $"{n} read-only scan leaves watcher generation unchanged");
                }
                finally { busy = false; }
                Check(modInventory?.Mods.Count == n, $"{n} exact fixture inventory");
                ResetHeartbeat();
                var timer = Stopwatch.StartNew();
                if (!UiThemes.IsNeon) ChangeUiTheme(UiThemes.Neon);
                var view = neonWorkbenchView!;
                view.SearchBoxControl.Text = "";
                view.SetNeonPerfNameSort();
                bool firstAccepted = await RefreshNeonWorkbenchAsync(preserveViewport: false);
                await LayoutAsync();
                var drawTimer = Stopwatch.StartNew();
                Draw(view);
                drawTimer.Stop();
                timer.Stop();
                double delay = await FinishHeartbeat();
                Metric(n, "first_screen", timer.Elapsed.TotalMilliseconds,
                    NeonLastSnapshotCaptureMs + NeonLastSnapshotApplyMs + drawTimer.Elapsed.TotalMilliseconds, delay, view);
                Check(firstAccepted, $"{n} first snapshot accepted");
                Check(view.CurrentSnapshot?.AllRows.Length == n, $"{n} snapshot has all rows");
                Bound(view, n, "first screen");
                var original = view.DeskContentControl.Children.OfType<Border>().First(b => b.Tag is string);
                long builds = view.NeonModRowBuildCount;
                long graphs = NeonDependencyBuildCount;
                Check(await RefreshNeonWorkbenchAsync(), $"{n} unchanged snapshot accepted");
                await LayoutAsync();
                Check(view.NeonModRowBuildCount == builds &&
                    ReferenceEquals(original, view.DeskContentControl.Children.OfType<Border>().First(b => b.Tag is string)),
                    $"{n} unchanged refresh retains actual card instances");
                Check(NeonDependencyBuildCount == graphs, $"{n} dependency graph cache hit");
                Check(await NeonScanAndPublishAsync(root, ScanDiagnostic($"{n} unchanged rescan")), $"{n} unchanged real rescan accepted");
                Check(await RefreshNeonWorkbenchAsync(), $"{n} identical rescan projected");
                Check(NeonDependencyBuildCount == graphs, $"{n} equivalent inventory reuses dependency graph");
                await LayoutAsync();

                const string selected = "PerfMod0010";
                view.SelectMod(selected);
                await LayoutAsync();
                await Task.Delay(250);
                var sv = view.DeskScrollControl;
                Check(sv.ScrollableHeight > 150, $"{n} scrollable first page");
                ResetHeartbeat();
                timer.Restart();
                var ui = Stopwatch.StartNew();
                sv.ScrollToVerticalOffset(120);
                UpdateLayout();
                ui.Stop();
                await LayoutAsync();
                drawTimer.Restart();
                Draw(view);
                drawTimer.Stop();
                timer.Stop();
                delay = await FinishHeartbeat();
                Metric(n, "scroll", timer.Elapsed.TotalMilliseconds, ui.Elapsed.TotalMilliseconds + drawTimer.Elapsed.TotalMilliseconds, delay, view);
                double savedOffset = sv.VerticalOffset;
                Check(savedOffset > 100, $"{n} actual nonzero scroll offset");

                ResetHeartbeat();
                timer.Restart();
                ui.Restart();
                view.SearchBoxControl.Text = "PerfGroup";
                UpdateLayout();
                ui.Stop();
                await LayoutAsync();
                drawTimer.Restart();
                Draw(view);
                drawTimer.Stop();
                timer.Stop();
                delay = await FinishHeartbeat();
                Metric(n, "search_filter", timer.Elapsed.TotalMilliseconds, ui.Elapsed.TotalMilliseconds + drawTimer.Elapsed.TotalMilliseconds, delay, view);
                Check(NeonWorkbenchBuilder.FilterAndSort(view.CurrentSnapshot!.AllRows, "all", "PerfGroup", "name").Length == n - 5,
                    $"{n} search matches expected count");
                Check(view.SelectedModId == selected && view.SearchBoxControl.Text == "PerfGroup" &&
                    Math.Abs(sv.VerticalOffset - savedOffset) < 2, $"{n} search retains selection, query and scroll");
                Bound(view, n, "filter");

                // Real file movement and fresh guarded scan via existing production entrypoint.
                ResetHeartbeat();
                timer.Restart();
                ui.Restart();
                var toggle = NeonDirectToggleModAsync(view.CurrentSnapshot.AllRows.Single(x => x.Mod.Id == selected).Mod, selected);
                ui.Stop();
                await toggle;
                await LayoutAsync();
                drawTimer.Restart();
                Draw(view);
                drawTimer.Stop();
                timer.Stop();
                delay = await FinishHeartbeat();
                Metric(n, "single_disable_refresh", timer.Elapsed.TotalMilliseconds,
                    ui.Elapsed.TotalMilliseconds + NeonLastSnapshotCaptureMs + NeonLastSnapshotApplyMs + drawTimer.Elapsed.TotalMilliseconds, delay, view);
                Check(view.CurrentSnapshot?.AllRows.Single(x => x.Mod.Id == selected).Mod.Enabled == false &&
                    !view.CurrentSnapshot.ModInventoryDirty && !Directory.Exists(Path.Combine(root, "mods", selected)),
                    $"{n} real disable and clean refreshed inventory");
                Check(view.SelectedModId == selected && view.SearchBoxControl.Text == "PerfGroup" &&
                    Math.Abs(sv.VerticalOffset - savedOffset) < 2, $"{n} disable retains selection, query and scroll");
                Bound(view, n, "disable");

                ResetHeartbeat();
                timer.Restart();
                ui.Restart();
                var enableSnapshot = view.CurrentSnapshot ?? throw new InvalidOperationException("Missing snapshot before enable.");
                var enable = NeonDirectToggleModAsync(enableSnapshot.AllRows.Single(x => x.Mod.Id == selected).Mod, selected);
                ui.Stop();
                await enable;
                await LayoutAsync();
                drawTimer.Restart();
                Draw(view);
                drawTimer.Stop();
                timer.Stop();
                delay = await FinishHeartbeat();
                Metric(n, "single_enable_refresh", timer.Elapsed.TotalMilliseconds,
                    ui.Elapsed.TotalMilliseconds + NeonLastSnapshotCaptureMs + NeonLastSnapshotApplyMs + drawTimer.Elapsed.TotalMilliseconds, delay, view);
                Check(view.CurrentSnapshot?.AllRows.Single(x => x.Mod.Id == selected).Mod.Enabled == true &&
                    !view.CurrentSnapshot.ModInventoryDirty && Directory.Exists(Path.Combine(root, "mods", selected)),
                    $"{n} real enable and clean refreshed inventory");
                Check(view.SelectedModId == selected && view.SearchBoxControl.Text == "PerfGroup" &&
                    Math.Abs(sv.VerticalOffset - savedOffset) < 2, $"{n} enable retains selection, query and scroll");
                view.ChangeNeonModPage(2);
                await LayoutAsync();
                Check(view.NeonCurrentModPage == 2 && view.SelectedModId == selected, $"{n} page navigation retains selected MOD identity");
                Bound(view, n, "page two");
                view.FocusMod(selected);
                await LayoutAsync();
                Check(view.NeonCurrentModPage == 1 && view.SelectedModId == selected, $"{n} locate jumps to correct page");
                view.CloseInspector();
                view.SearchBoxControl.Text = "";
                await LayoutAsync();

                // Make a worker predictably obsolete while it is building.
                using var release = new ManualResetEventSlim(false);
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                long discarded = NeonSnapshotDiscardCount;
                int first = 0;
                neonTestSnapshotBuildHook = () =>
                {
                    if (Interlocked.Exchange(ref first, 1) != 0) return;
                    started.TrySetResult(true);
                    if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("fixture worker gate");
                };
                Task<bool> old = RefreshNeonWorkbenchAsync();
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    RecordNeonActivity("snapshot", $"perf-new-generation-{n}");
                    _ = RefreshNeonWorkbenchAsync();
                }
                finally { neonTestSnapshotBuildHook = null; release.Set(); }
                Check(await old, $"{n} latest request committed after obsolete worker");
                Check(NeonSnapshotDiscardCount > discarded &&
                    view.CurrentSnapshot!.Recovery.ActivityLog.Any(x => x.Text == $"perf-new-generation-{n}"),
                    $"{n} stale projection discarded and latest recovery revision published");
                await Task.Delay(1000);
            }

            // A real watcher event must invalidate a blocked projection even if no
            // new inventory object has yet been published. Hold busy only to keep the
            // existing watcher quiet-period auto-scan from racing the assertion.
            using (var watcherRelease = new ManualResetEventSlim(false))
            {
                var watcherStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                long watcherDiscards = NeonSnapshotDiscardCount;
                int before = modFileWatcher.ChangeVersion;
                int gateOnce = 0;
                neonTestSnapshotBuildHook = () =>
                {
                    if (Interlocked.Exchange(ref gateOnce, 1) != 0) return;
                    watcherStarted.TrySetResult(true);
                    if (!watcherRelease.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("watcher fixture worker gate");
                };
                busy = true;
                var blocked = RefreshNeonWorkbenchAsync();
                try
                {
                    await watcherStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    string marker = Path.Combine(roots[500], "mods", "PerfMod0010", "watcher-fixture.txt");
                    await Task.Run(() => File.WriteAllText(marker, "external fixture edit"));
                    for (int i = 0; i < 100 && modFileWatcher.ChangeVersion == before; i++) await Task.Delay(25);
                    Check(modFileWatcher.ChangeVersion != before, "real isolated watcher generation advanced");
                }
                finally { neonTestSnapshotBuildHook = null; watcherRelease.Set(); }
                try
                {
                    Check(await blocked && NeonSnapshotDiscardCount > watcherDiscards &&
                        neonWorkbenchView!.CurrentSnapshot?.ModInventoryDirty == true &&
                        !neonWorkbenchView.CurrentSnapshot.IsLifecycleHealthValid,
                        "changed watcher generation rejects old clean projection");
                    await Task.Delay(1000);
                    Check(await NeonScanAndPublishAsync(roots[500], ScanDiagnostic("watcher changed rescan")), "new scan covers changed watcher generation");
                    Check(await RefreshNeonWorkbenchAsync(), "clean projection after watcher change");
                }
                finally { busy = false; }
            }

            // Root A -> B -> A with a blocked worker, same watcher generation may be reused.
            string a = roots[500], b = roots[50];
            using var rootRelease = new ManualResetEventSlim(false);
            var rootStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long rootDiscards = NeonSnapshotDiscardCount;
            neonTestSnapshotBuildHook = () =>
            {
                rootStarted.TrySetResult(true);
                if (!rootRelease.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("root fixture worker gate");
            };
            var obsolete = RefreshNeonWorkbenchAsync();
            try
            {
                await rootStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                UseGameRoot(b);
                Check(neonWorkbenchView!.CurrentSnapshot?.GameRoot == b &&
                    neonWorkbenchView.CurrentSnapshot.AllRows.Length == 0, "root B immediately withdraws root A rows");
                UseGameRoot(a);
                Check(neonWorkbenchView.CurrentSnapshot?.GameRoot == a &&
                    neonWorkbenchView.CurrentSnapshot.AllRows.Length == 0, "A-B-A cannot revive old root A projection");
                await NeonRecoverTransactionsAsync(a);
                Check(await NeonScanAndPublishAsync(a, ScanDiagnostic("A-B-A rescan")), "A-B-A new guarded inventory");
                _ = RefreshNeonWorkbenchAsync();
            }
            finally { neonTestSnapshotBuildHook = null; rootRelease.Set(); }
            Check(await obsolete && NeonSnapshotDiscardCount > rootDiscards &&
                neonWorkbenchView!.CurrentSnapshot?.AllRows.Length == 500,
                "A-B-A obsolete worker discarded; new 500-row projection committed");
            Log("PASS neon-perf-self-test completed");
        }
        catch (Exception ex)
        {
            DrainWatcherEvents("failure");
            Log("FAIL " + ex);
            throw;
        }
        finally
        {
            heartbeat.Stop();
            modFileWatcher.ChangeDetected -= CaptureWatcherEvent;
            busy = false;
            neonTestSnapshotBuildHook = null;
            neonTestHoldRescan = false;
            CloseModWatch();
            try { Directory.Delete(parent, true); } catch { }
        }
    }
}
