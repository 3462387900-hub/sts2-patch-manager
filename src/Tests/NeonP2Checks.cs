using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;

namespace SpirePatch;

public partial class MainWindow
{
    internal async Task RunNeonP2ChecksAsync(string reportPath)
    {
        reportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string parent = Path.Combine(Path.GetTempPath(), "spire-neon-p2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        List<string> results = [];
        void Check(bool ok, string name)
        {
            results.Add((ok ? "PASS " : "FAIL ") + name);
            File.WriteAllLines(reportPath, results);
            if (!ok) throw new InvalidOperationException(name);
        }
        try
        {
            string theme = Path.Combine(parent, "theme");
            Check(UiThemes.Load(theme) == UiThemes.Workspace, "P2-05 first launch Workspace");
            foreach (string choice in new[] { UiThemes.Classic, UiThemes.Workspace, UiThemes.Neon })
            {
                UiThemes.Save(theme, choice);
                Check(UiThemes.Load(theme) == choice, "P2-05 persisted " + choice);
            }
            File.WriteAllText(UiThemes.SettingsPath(theme), "{");
            Check(UiThemes.Load(theme) == UiThemes.Workspace, "P2-05 corrupt preference fallback");
            string root = await SetupNeonIsolatedFixtureAsync(parent);
            var view = neonWorkbenchView!;
            neonTestHoldRescan = true;
            async Task Settle()
            {
                neonTestScanOverride = null;
                neonRescanPending = false;
                await Task.Delay(1250);
                for (int i = 0; i < 200 && (busy || modInventoryRefreshRunning); i++) await Task.Delay(25);
                if (!await NeonScanAndPublishAsync(root))
                    throw new InvalidOperationException("测试清单尚未稳定。");
            }
            bool ToastContains(DependencyObject node, string message)
            {
                if (node is TextBlock tb && tb.Text.Contains(message, StringComparison.Ordinal)) return true;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    if (ToastContains(VisualTreeHelper.GetChild(node, i), message)) return true;
                return false;
            }
            void CommittedWarning(string name, bool scan)
            {
                var receipt = neonLastWriteReceipt;
                Check(receipt is { State: NeonWriteState.Committed, Tone: NeonTone.Amber } &&
                    (scan ? receipt.ScanFailed : receipt.CacheWarning != null) &&
                    ToastContains(view.ToastRackControl, "写入已成功") &&
                    !receipt.Message.Contains("已自动回滚"), name);
            }
            string? workspace = null;
            const ulong id = 3999999999;
            const string modId = "NeonP2WorkshopFixture";
            string preparedModId = modId;
            neonTestWorkshopPrepare = (game, ws, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                workspace = Path.Combine(parent, "prepared-" + Guid.NewGuid().ToString("N"));
                string assembled = Path.Combine(workspace, "assembled");
                string payload = Path.Combine(assembled, preparedModId);
                Directory.CreateDirectory(payload);
                File.WriteAllText(Path.Combine(payload, preparedModId + ".json"), JsonSerializer.Serialize(new
                { id = preparedModId, name = preparedModId, version = "1.0.0", has_dll = true, has_pck = false }));
                File.WriteAllText(Path.Combine(payload, preparedModId + ".dll"), "isolated DLL");
                File.WriteAllText(Path.Combine(assembled, "assembly-manifest.json"), "{}");
                return Task.FromResult(WorkshopModInstaller.PrepareAssembled(game, ws, preparedModId, "1001", assembled, workspace));
            };
            await Settle();
            await NeonPreviewWorkshopInstallAsync(id);
            Check(workspace != null && Directory.Exists(workspace), "P2-01 real isolated prepared preview exists");
            view.CloseTransactionModal();
            Check(!Directory.Exists(workspace), "P2-01 close removes workspace");
            await NeonPreviewWorkshopInstallAsync(id);
            view.UpdateLayout();
            IEnumerable<Button> Buttons(DependencyObject node)
            {
                if (node is Button button) yield return button;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    foreach (var child in Buttons(VisualTreeHelper.GetChild(node, i))) yield return child;
            }
            var cancel = Buttons(view.ModalBackdropControl).Single(b => AutomationProperties.GetName(b) == "取消");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancel));
            Check(!Directory.Exists(workspace), "P2-01 actual cancel button releases workspace");
            await NeonPreviewWorkshopInstallAsync(id);
            view.OpenTransactionModal(new NeonTransactionConfig(Title: "replacement", Subtitle: "", GameRunning: false,
                BlockedReason: "test", AutoSnapshotNote: null, BoundaryNote: null, Token: "P2", Groups: [], ConfirmLabel: "", CancelLabel: "关闭"));
            Check(!Directory.Exists(workspace), "P2-01 replacement releases workspace");
            view.CloseTransactionModal();

            int cleanups = 0;
            using (var session = new NeonPreviewSession(() => cleanups++))
            {
                session.Dispose(); session.Dispose();
            }
            Check(cleanups == 1, "P2-01 disposal idempotent");
            using (var session = new NeonPreviewSession(() => cleanups++))
            {
                Check(session.TryTransfer() && !session.TryTransfer(), "P2-01 one ownership transfer");
                session.Dispose();
            }
            Check(cleanups == 1, "P2-01 transferred owner not cleaned by preview");

            await NeonPreviewWorkshopInstallAsync(id);
            neonTestScanOverride = _ => throw new IOException("P2 injected scan failure");
            await view.ConfirmTransactionModalAsync();
            Check(Directory.Exists(Path.Combine(root, "mods", modId)) && !Directory.Exists(workspace), "P2-01 new install commits and cleans only its workspace");
            CommittedWarning("P2-03 Workshop Commit succeeded; scan failed", true);
            await Settle();
            await NeonPreviewWorkshopInstallAsync(id);
            Check(workspace != null && !Directory.Exists(workspace), "P2-01 unchanged prepared workspace released immediately");

            var hex = modInventory!.Mods.Single(x => x.Id == "HextechRunes");
            string manifest = WorkshopUpdates().Cached(hex.Id)!.RemoteManifestId;
            neonTestStagingBackend = ModUpdateStagingWorkflowChecks.WorkshopBackend("3748937859", manifest, hex.Id, "1.1.0");
            string historical = Path.Combine(root, "尖塔修补站_MOD备份", hex.Id, "p2-historical");
            foreach (var file in Directory.EnumerateFiles(hex.Folder, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(historical, Path.GetRelativePath(hex.Folder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            await NeonPreviewWorkshopInstallAsync(3748937859);
            neonTestPostWriteCacheFailure = () => throw new IOException("P2 injected cache failure");
            neonTestScanOverride = _ => throw new IOException("P2 injected online scan failure");
            await view.ConfirmTransactionModalAsync();
            Check(ModManager.Scan(root).Mods.Single(x => x.Id == hex.Id).Version == "1.1.0", "P2-03 real Apply committed new version");
            CommittedWarning("P2-03 online Apply succeeded; cache failed", false);
            CommittedWarning("P2-03 online Apply succeeded; scan failed", true);
            neonTestPostWriteCacheFailure = null;
            await Settle();
            neonTestScanOverride = _ => throw new IOException("P2 injected restore scan failure");
            await NeonConfirmRestoreModBackupAsync(root, modInventory!.Mods.Single(x => x.Id == hex.Id), historical);
            Check(ModManager.Scan(root).Mods.Single(x => x.Id == hex.Id).Version == hex.Version, "P2-03 real historical restore committed");
            CommittedWarning("P2-03 restore succeeded; scan failed", true);
            await Settle();

            NeonSetRecoveryFailure(root, NeonUpdateRecoveryKind, NeonUpdateLogDirectory, "P2 injected recovery gate");
            ChangeUiTheme(UiThemes.Workspace);
            Check(UiThemes.IsNeon, "P2-05 P1 recovery gate preserved");
            await NeonRecoverTransactionsAsync(root);
            ChangeUiTheme(UiThemes.Workspace);
            Check(UiThemes.IsWorkspace, "P2-05 Neon to Workspace");
            ChangeUiTheme(UiThemes.Neon);
            Check(UiThemes.IsNeon, "P2-05 Workspace to Neon");
            await Settle();

            // Verify real root-switch and window-closing hooks on an uncommitted new-install preview.
            preparedModId = "NeonP2SecondFixture";
            await NeonPreviewWorkshopInstallAsync(id + 1);
            Check(Directory.Exists(workspace), "P2-01 root-switch preview exists");
            string otherRoot = Path.Combine(parent, "other-game");
            Directory.CreateDirectory(Path.Combine(otherRoot, "mods"));
            Directory.CreateDirectory(Path.Combine(otherRoot, "data_sts2_windows_x86_64"));
            File.WriteAllBytes(Path.Combine(otherRoot, "SlayTheSpire2.exe"), []);
            File.WriteAllText(Path.Combine(otherRoot, ".spirepatch-fixture"), "isolated");
            File.WriteAllText(Path.Combine(otherRoot, "data_sts2_windows_x86_64", "sts2.dll"), "sts2-fixture-0.111.0");
            UseGameRoot(otherRoot);
            Check(!Directory.Exists(workspace), "P2-01 root switch releases prepared preview");
            UseGameRoot(root);
            await NeonRecoverTransactionsAsync(root);
            await Settle();
            await NeonPreviewWorkshopInstallAsync(id + 1);
            Check(Directory.Exists(workspace), "P2-01 closing preview exists");
            // Run the real Closing event without terminating this isolated test process.
            System.ComponentModel.CancelEventHandler keepTestWindow = (_, args) => args.Cancel = true;
            Closing += keepTestWindow;
            try { Close(); }
            finally { Closing -= keepTestWindow; neonWindowClosing = false; }
            Check(!Directory.Exists(workspace), "P2-01 window closing releases prepared preview");

            // Transfer the actual staged update to a fault-injected Apply; closing the
            // failure modal or disposing the preview must not delete its journal or staged content.
            await Settle();
            var current = modInventory!.Mods.Single(x => x.Id == hex.Id);
            var request = NeonWorkshopUpdateRequest(3748937859, manifest);
            var stageService = new ModUpdateStagingWorkflowService(neonTestStagingBackend!,
                Path.Combine(NeonDataRoot, "MOD更新暂存"));
            var staged = await stageService.StageAndPreviewAsync(root, current, request);
            Check(staged.ValidationPassed && staged.StagingDirectory != null && staged.StagedPayloadDirectory != null,
                "P2-01 recovery fixture staged with mock backend");
            var apply = new ModUpdateApplyWorkflowService(NeonUpdateLogDirectory,
                new ModUpdateApplyWorkflowChecks.FaultFiles { FailEveryMoveAfterFirst = true }, _ => false);
            int previewDisposals = 0;
            using (var session = new NeonPreviewSession(() => { previewDisposals++; stageService.Discard(staged.StagingDirectory!); }))
            {
                await NeonConfirmStagedModUpdateAsync(root, current, apply, apply.CreatePreview(root, current, staged), request, session);
                view.CloseTransactionModal();
            }
            Check(previewDisposals == 0 && neonLastWriteReceipt is { State: NeonWriteState.PendingRecovery } &&
                NeonRecoveryBlocked(root) && Directory.Exists(staged.StagingDirectory) &&
                Directory.Exists(staged.StagedPayloadDirectory) &&
                Directory.EnumerateFiles(NeonUpdateLogDirectory, "transaction.json", SearchOption.AllDirectories).Any(),
                "P2-01 actual pending Apply preserves staging and journal after preview disposal");
            await ModUpdateApplyWorkflowChecks.RunAsync(parent, (ok, name) => Check(ok, "P2-01 apply: " + name), workshop: true);
        }
        catch (Exception ex)
        {
            results.Add("ERROR " + ex); File.WriteAllLines(reportPath, results); throw;
        }
        finally
        {
            neonTestWorkshopPrepare = null;
            neonTestPostWriteCacheFailure = null;
            neonTestStagingBackend = null;
            neonTestHoldRescan = false;
            neonTestScanOverride = null;
            neonRescanPending = false;
            NeonCancelOwnedPreview();
            CloseModWatch();
        }
    }
}
