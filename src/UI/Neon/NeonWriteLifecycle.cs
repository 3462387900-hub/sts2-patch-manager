using System.IO;

namespace SpirePatch;

// Exactly one owner may dispose a preview. Transfer relinquishes preview ownership
// before invoking a writer; journal-owned staging is never deleted by the preview.
internal sealed class NeonPreviewSession(Action cleanup, Action<Exception>? cleanupFailed = null) : IDisposable
{
    Action? release = cleanup;
    internal bool TryTransfer() => Interlocked.Exchange(ref release, null) != null;
    public void Dispose()
    {
        var action = Interlocked.Exchange(ref release, null);
        if (action == null) return;
        try { action(); }
        catch (Exception error) when (cleanupFailed != null) { cleanupFailed(error); }
    }
}

internal enum NeonWriteState { NotCommitted, Committed, RolledBack, PendingRecovery }

internal sealed record NeonWriteReceipt(NeonWriteState State, string Summary,
    bool ScanFailed = false, string? CacheWarning = null, string? RefreshWarning = null)
{
    internal string Message => State == NeonWriteState.Committed
        ? Summary + "；写入已成功" +
          (ScanFailed ? "，但后续扫描失败，请手动刷新" : "") +
          (CacheWarning != null ? "；缓存或版本基线更新失败：" + CacheWarning + "，请重新核验来源" : "") +
          (RefreshWarning != null ? "；后续刷新或清理未完成：" + RefreshWarning + "，请手动刷新" : "")
        : State == NeonWriteState.RolledBack ? "写入未提交，已自动回滚：" + Summary
        : State == NeonWriteState.PendingRecovery ? "事务待恢复，请勿重复写入：" + Summary
        : Summary;
    internal NeonTone Tone => State == NeonWriteState.Committed
        ? ScanFailed || CacheWarning != null || RefreshWarning != null ? NeonTone.Amber : NeonTone.Sage
        : State == NeonWriteState.RolledBack ? NeonTone.Amber : NeonTone.Cinnabar;
}

public partial class MainWindow
{
    NeonPreviewSession? neonOwnedPreview;
    CancellationTokenSource? neonPreviewPreparationCancellation;
    bool neonWindowClosing;
    internal Func<string, ulong, CancellationToken, Task<WorkshopModPrepared>>? neonTestWorkshopPrepare;
    internal Action? neonTestPostWriteCacheFailure;
    internal NeonWriteReceipt? neonLastWriteReceipt;

    bool NeonPreviewRootCurrent(string root, long epoch) =>
        !neonWindowClosing && UiThemes.IsNeon && NeonRootMatches(root) &&
        epoch == Volatile.Read(ref neonRootEpoch);

    NeonPreviewSession NeonCreatePreviewSession(Action cleanup) => new(cleanup, error =>
        neonWorkbenchView?.ShowToast("预览已取消，但暂存清理失败，请检查目录权限：" + error.Message, NeonTone.Amber));

    void NeonCleanupWorkshopPreview(WorkshopModPrepared prepared)
    {
        WorkshopModInstaller.Cleanup(prepared);
        if (Directory.Exists(prepared.WorkspaceDirectory))
            throw new IOException("暂存目录仍存在：" + prepared.WorkspaceDirectory);
    }

    void NeonOpenOwnedPreview(NeonPreviewSession session, NeonTransactionConfig config)
    {
        neonWorkbenchView!.OpenTransactionModal(config);
        neonOwnedPreview = session;
    }

    void NeonDetachPreview(NeonPreviewSession session)
    {
        if (ReferenceEquals(neonOwnedPreview, session)) neonOwnedPreview = null;
    }

    void NeonReleasePreview(NeonPreviewSession session)
    {
        NeonDetachPreview(session);
        session.Dispose();
    }

    void NeonCancelOwnedPreview()
    {
        if (neonOwnedPreview is not { } session) return;
        try { neonWorkbenchView?.CloseTransactionModal(); }
        finally { NeonReleasePreview(session); }
    }

    void NeonDiscardUpdateStaging(ModUpdateApplyPreview preview)
    {
        if (preview.Staging.StagingDirectory is not { } dir || !Directory.Exists(dir)) return;
        using var backend = new InfrastructureModUpdateStagingBackend();
        new ModUpdateStagingWorkflowService(neonTestStagingBackend ?? backend,
            Path.Combine(NeonDataRoot, "MOD更新暂存")).Discard(dir);
    }

    void NeonPublishWriteReceipt(NeonWriteReceipt receipt)
    {
        neonLastWriteReceipt = receipt;
        modStatus = receipt.Message;
        try { neonWorkbenchView?.ShowToast(receipt.Message, receipt.Tone); }
        catch { /* The authoritative write receipt must survive a UI rendering error. */ }
    }

    async Task NeonCompleteCommittedWriteAsync(string root, string summary, Action updateCache,
        string healthReason, string activity, string? cleanupWarning = null)
    {
        // Write already completed. No exception from this point can change that fact.
        modInventoryDirty = true;
        string? cacheWarning = null;
        string? refreshWarning = cleanupWarning;
        bool scanFailed = true;
        try
        {
            if (!NeonRootMatches(root)) throw new InvalidOperationException("游戏目录已切换");
            neonTestPostWriteCacheFailure?.Invoke();
            updateCache();
        }
        catch (Exception ex) { cacheWarning = ex.Message; }

        try
        {
            report = null; states = []; ClearCoop(); ClearSelection();
            installPreview = null; lastInstallResult = null; modTogglePreview = null; demo = false;
            suppressNeonHealthInvalidationRefresh = true;
            try { InvalidateLifecycle(healthReason); }
            finally { suppressNeonHealthInvalidationRefresh = false; }
            // P1 publication is still the only way to mark the inventory clean.
            scanFailed = !await NeonScanAndPublishAsync(root);
        }
        catch (Exception ex) { refreshWarning = JoinWarning(refreshWarning, ex.Message); }
        try { RecordNeonActivity("install", activity + (scanFailed ? " 后续扫描未完成。" : "") +
            (cacheWarning != null ? " 缓存或版本基线更新失败：" + cacheWarning : "")); }
        catch (Exception ex) { refreshWarning = JoinWarning(refreshWarning, ex.Message); }
        NeonPublishWriteReceipt(new(NeonWriteState.Committed, summary, scanFailed, cacheWarning, refreshWarning));
    }

    static void NeonRunCacheSteps(params Action[] steps)
    {
        string? warning = null;
        foreach (var step in steps)
        {
            try { step(); }
            catch (Exception ex) { warning = JoinWarning(warning, ex.Message); }
        }
        if (warning != null) throw new IOException(warning);
    }

    static string JoinWarning(string? first, string second) => first == null ? second : first + "；" + second;

    async Task NeonFinishWriteAsync()
    {
        busy = false;
        transactionWriting = false;
        try
        {
            if (!await RefreshNeonWorkbenchAsync(preserveViewport: true))
                throw new IOException("最新界面快照未能提交");
        }
        catch (Exception ex)
        {
            if (neonLastWriteReceipt is { State: NeonWriteState.Committed } receipt)
                NeonPublishWriteReceipt(receipt with { RefreshWarning = JoinWarning(receipt.RefreshWarning, ex.Message) });
            else neonWorkbenchView?.ShowToast("界面刷新未完成：" + ex.Message, NeonTone.Amber);
        }
    }
}
