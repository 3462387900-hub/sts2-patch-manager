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
            return root;
        }

        try
        {
            Log("INFO WPF off-screen render; dispatcher lateness = largest 10ms Input timer gap minus 10ms; ui_sync_ms = measured synchronous UI phase, not total background time. Memory is absolute process/managed heap, not per-row allocation.");
            Log("INFO no HTTP is needed; isolated game and manager data only; P1 recovery and guarded inventory publication are exercised.");
            neonFixtureDataRoot = Path.Combine(parent, "manager-data");
            Directory.CreateDirectory(neonFixtureDataRoot);
            neonTestHoldRescan = true;
            // Build every fixture before starting the watcher for its root.
            var roots = await Task.Run(() => new[] { 50, 200, 500 }.ToDictionary(n => n, MakeRoot));
            heartbeat.Start();
            foreach (int n in new[] { 50, 200, 500 })
            {
                string root = roots[n];
                neonFixtureRoot = root;
                UseGameRoot(root);
                // No live discovery, update checks or lifecycle HTTP.
                await NeonRecoverTransactionsAsync(root);
                Check(NeonRecoveryChecked(root) && !NeonRecoveryBlocked(root), $"{n} isolated P1 recovery gate");
                Check(await NeonScanAndPublishAsync(root), $"{n} initial guarded inventory publication");
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
                Check(await NeonScanAndPublishAsync(root), $"{n} unchanged real rescan accepted");
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
                    Check(await NeonScanAndPublishAsync(roots[500]), "new scan covers changed watcher generation");
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
                Check(await NeonScanAndPublishAsync(a), "A-B-A new guarded inventory");
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
            Log("FAIL " + ex);
            throw;
        }
        finally
        {
            heartbeat.Stop();
            busy = false;
            neonTestSnapshotBuildHook = null;
            neonTestHoldRescan = false;
            CloseModWatch();
            try { Directory.Delete(parent, true); } catch { }
        }
    }
}
