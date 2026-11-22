using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

internal enum LocalZipHistoricalBindingPolicy
{
    PreserveUnverified,
    RequireManualReview,
    Unbind
}

internal enum LocalZipApplyFailureKind
{
    None,
    InvalidPreview,
    UserDeclined,
    Cancelled,
    PreviewChanged,
    GameRunning,
    TargetChanged,
    PathUnsafe,
    PreparationFailed,
    CommitFailed,
    RecoveryRequired,
    RecoveryFailed
}

internal enum LocalZipInstallRecoveryState
{
    NotRequired,
    Cleaned,
    Completed,
    PendingRecovery
}

internal enum LocalZipInstallFaultPoint
{
    CopyFile,
    BeforeMove,
    AfterMove,
    WriteJournal,
    CleanupPreparation,
    SourceBindingWrite
}

internal sealed record LocalZipApplyDecision(
    bool Confirmed,
    LocalZipHistoricalBindingPolicy HistoricalBindingPolicy);

internal sealed record LocalZipApplyConfirmation(
    LocalModZipPreview Preview,
    LocalZipSourceHints HistoricalSources,
    IReadOnlyList<LocalZipHistoricalBindingPolicy> AvailableBindingPolicies);

internal sealed record LocalZipApplyProgress(
    string Stage,
    string Message,
    bool TransactionWriting = false);

internal sealed record LocalZipInstallTransactionPaths(
    string TransactionId,
    string GameRoot,
    string TargetDirectory,
    string TransactionDirectory,
    string PreparedDirectory,
    string PreviewWorkspaceDirectory);

internal sealed record LocalZipInstallTransactionJournal(
    int SchemaVersion,
    string TransactionId,
    string GameRoot,
    string ModId,
    string DestinationFolderName,
    string TargetDirectory,
    string TransactionDirectory,
    string PreparedDirectory,
    string PreviewWorkspaceDirectory,
    string State,
    Dictionary<string, string> ExpectedFileHashes,
    string HistoricalBindingPolicy,
    bool HadHistoricalSourceEvidence,
    bool SourcePolicyApplied,
    string? SourcePolicyError,
    DateTimeOffset UpdatedAt);

internal sealed record LocalZipPendingTransaction(
    string FileName,
    LocalZipInstallTransactionJournal? Journal,
    string? Error);

internal sealed record LocalZipSourcePolicyResult(
    bool Applied,
    bool PartialSuccess,
    bool SourceReviewRequired,
    string Action,
    string? Error);

internal sealed record LocalZipApplyResult(
    bool Confirmed,
    bool Success,
    bool Cancelled,
    LocalZipApplyFailureKind FailureKind,
    string? FailureReason,
    LocalZipInstallRecoveryState RecoveryState,
    string? TransactionId,
    string? TargetDirectory,
    LocalZipHistoricalBindingPolicy? HistoricalBindingPolicy,
    string HistoricalBindingAction,
    bool SourceReviewRequired,
    bool SourceBindingCleanupFailed,
    bool PartialSuccess,
    bool InventoryInvalidationRequired,
    bool SourceCacheInvalidationRequired,
    bool PatchLifecycleRecheckRequired,
    bool CleanupCompleted,
    string? CleanupFailureReason);

internal sealed record LocalZipInstallRecoveryItem(
    string TransactionId,
    string State,
    bool Completed,
    bool Cleaned,
    bool Pending,
    bool SourceReviewRequired,
    string? Error,
    string? ModId = null,
    string? TargetDirectory = null);

internal sealed record LocalZipInstallRecoveryReport(
    int CheckedCount,
    int CompletedCount,
    int CleanedCount,
    int PendingCount,
    IReadOnlyList<LocalZipInstallRecoveryItem> Items)
{
    public bool HasPendingRecovery => PendingCount > 0;
    public bool InventoryInvalidationRequired => CompletedCount > 0;
    public bool SourceCacheInvalidationRequired => CompletedCount > 0;
    public bool PatchLifecycleRecheckRequired => CompletedCount > 0;
}

internal sealed class LocalZipInstallTransactionException : IOException
{
    public LocalZipApplyFailureKind FailureKind { get; }

    public LocalZipInstallTransactionException(
        LocalZipApplyFailureKind failureKind, string message) : base(message)
    {
        FailureKind = failureKind;
    }

    public LocalZipInstallTransactionException(
        LocalZipApplyFailureKind failureKind, string message, Exception inner) : base(message, inner)
    {
        FailureKind = failureKind;
    }
}

internal interface ILocalModZipInstallTransactionBackend
{
    string TransactionLogDirectory { get; }

    LocalZipInstallTransactionPaths CreatePaths(
        string gameRoot, string targetDirectory, string transactionId, string previewWorkspaceDirectory);

    void ValidateTargetParent(string gameRoot, string targetDirectory);

    Task PrepareAsync(
        LocalZipPackage package,
        LocalZipInstallTransactionPaths paths,
        CancellationToken cancellationToken);

    IReadOnlyDictionary<string, string> SnapshotDirectory(string directory);
    bool DirectoryExists(string path);
    bool FileExists(string path);
    void WriteJournal(LocalZipInstallTransactionJournal journal);
    void CommitDirectoryMove(LocalZipInstallTransactionJournal journal);
    void ValidateJournal(LocalZipInstallTransactionJournal journal);
    void CleanupPreparation(LocalZipInstallTransactionJournal journal);
    void DeleteJournal(LocalZipInstallTransactionJournal journal);
    IReadOnlyList<LocalZipPendingTransaction> ReadPendingTransactions();

    LocalZipSourcePolicyResult ApplyHistoricalBindingPolicy(
        string gameRoot, string modId, LocalZipHistoricalBindingPolicy policy);
}

/// <summary>
/// Executes only first-time local ZIP installs. Preview verification, user consent,
/// preparation, atomic directory move, source-binding policy and recovery are separate
/// boundaries. Existing MOD directories are never replaced.
/// </summary>
internal sealed class LocalModZipApplyWorkflowService
{
    readonly LocalModZipImportWorkflowService previewWorkflow;
    readonly ILocalModZipInstallTransactionBackend backend;
    readonly SemaphoreSlim applyGate = new(1, 1);
    readonly object progressGate = new();
    bool transactionWriting;

    internal LocalModZipApplyWorkflowService(
        LocalModZipImportWorkflowService previewWorkflow,
        string dataRoot,
        ILocalModZipInstallTransactionBackend? backend = null)
    {
        this.previewWorkflow = previewWorkflow ??
            throw new ArgumentNullException(nameof(previewWorkflow));
        this.backend = backend ?? new LocalModZipInstallTransactionBackend(dataRoot);
    }

    internal string TransactionLogDirectory => backend.TransactionLogDirectory;

    public async Task<LocalZipApplyResult> ApplyAsync(
        LocalModZipPreview preview,
        Func<LocalZipApplyConfirmation, CancellationToken, Task<LocalZipApplyDecision>> confirm,
        Action deferQuietPeriod,
        Action<LocalZipApplyProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(deferQuietPeriod);

        try
        {
            await applyGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(false, true, LocalZipApplyFailureKind.Cancelled,
                "本地 ZIP 安装在开始前已取消。");
        }

        bool writing = false;
        LocalZipInstallTransactionJournal? journal = null;
        LocalZipInstallTransactionPaths? paths = null;
        bool commitStarted = false;
        LocalZipHistoricalBindingPolicy? selectedPolicy = null;

        void Report(string stage, string message)
        {
            bool current;
            lock (progressGate) current = transactionWriting;
            try { reportProgress?.Invoke(new(stage, message, current)); }
            catch { /* Progress observers must never interrupt a filesystem transaction. */ }
        }

        void SetWriting(bool value, string stage, string message)
        {
            lock (progressGate)
            {
                if (transactionWriting == value)
                    return;
                transactionWriting = value;
                writing = value;
            }
            try { reportProgress?.Invoke(new(stage, message, value)); }
            catch { /* Progress observers must never interrupt a filesystem transaction. */ }
        }

        try
        {
            if (preview.SourceKind != LocalZipSourceKind.UnverifiedLocalZip ||
                !preview.SourceTrust.Equals("Unverified", StringComparison.Ordinal) ||
                !preview.CanInstallNew)
                return Failure(false, false, LocalZipApplyFailureKind.InvalidPreview,
                    "预览不是可安装的全新本地 ZIP，或预览中仍存在阻断冲突。");

            cancellationToken.ThrowIfCancellationRequested();
            Report("AwaitingConfirmation", "等待调用方确认本地 ZIP 全新安装及历史来源处理策略。");
            LocalZipApplyDecision decision = await confirm(new(
                preview,
                preview.HistoricalSources,
                Array.AsReadOnly(Enum.GetValues<LocalZipHistoricalBindingPolicy>())),
                cancellationToken);

            if (!decision.Confirmed)
                return new(false, false, false, LocalZipApplyFailureKind.UserDeclined,
                    "用户拒绝安装；未创建准备目录或事务日志，也未修改游戏目录。",
                    LocalZipInstallRecoveryState.NotRequired, null,
                    preview.DestinationDirectory, decision.HistoricalBindingPolicy,
                    "未处理历史来源绑定", false, false, false,
                    false, false, false, true, null);

            selectedPolicy = decision.HistoricalBindingPolicy;
            if (!Enum.IsDefined(typeof(LocalZipHistoricalBindingPolicy), selectedPolicy.Value))
                return Failure(true, false, LocalZipApplyFailureKind.InvalidPreview,
                    "历史来源处理策略无效。", preview.DestinationDirectory, selectedPolicy);

            cancellationToken.ThrowIfCancellationRequested();

            // First mandatory read-only pre-commit verification. No transaction file,
            // preparation directory or game-directory write exists before this succeeds.
            LocalZipPreviewVerification firstVerification =
                await previewWorkflow.VerifyAsync(preview, cancellationToken);
            if (!firstVerification.IsCurrent)
            {
                LocalZipApplyFailureKind kind =
                    firstVerification.Reason?.Contains("游戏正在运行", StringComparison.Ordinal) == true
                        ? LocalZipApplyFailureKind.GameRunning
                        : LocalZipApplyFailureKind.PreviewChanged;
                return Failure(true, false, kind,
                    firstVerification.Reason ?? "本地 ZIP 预览已失效。",
                    preview.DestinationDirectory, selectedPolicy);
            }

            backend.ValidateTargetParent(preview.GameRoot, preview.DestinationDirectory);

            IReadOnlyList<LocalZipPendingTransaction> existing =
                backend.ReadPendingTransactions();
            if (existing.Any(item => item.Journal is null ||
                item.Journal.GameRoot.Equals(preview.GameRoot, StringComparison.OrdinalIgnoreCase)))
                return Failure(true, false, LocalZipApplyFailureKind.RecoveryRequired,
                    "发现尚未处理的本地 ZIP 安装事务，请先执行 RecoverPendingTransactions()。",
                    preview.DestinationDirectory, selectedPolicy,
                    recoveryState: LocalZipInstallRecoveryState.PendingRecovery);

            cancellationToken.ThrowIfCancellationRequested();

            string transactionId = Guid.NewGuid().ToString("N");
            paths = backend.CreatePaths(
                preview.GameRoot, preview.DestinationDirectory, transactionId, preview.Package.WorkspaceDirectory);
            var expected = preview.Package.Files.ToDictionary(
                item => item.RelativePath, item => item.Sha256,
                StringComparer.OrdinalIgnoreCase);

            journal = new LocalZipInstallTransactionJournal(
                1,
                transactionId,
                Path.GetFullPath(preview.GameRoot),
                preview.Package.ModId,
                preview.Package.DestinationFolderName,
                paths.TargetDirectory,
                paths.TransactionDirectory,
                paths.PreparedDirectory,
                paths.PreviewWorkspaceDirectory,
                "Preparing",
                expected,
                selectedPolicy.Value.ToString(),
                preview.HistoricalSources.HasHistoricalEvidence,
                false,
                null,
                DateTimeOffset.UtcNow);

            // Keep one uninterrupted true interval from the first preparation/log
            // write through Prepared, ReadyToCommit, Committing and final cleanup.
            SetWriting(true, "Preparing", "正在同卷隔离准备目录中复制并校验本地 MOD 文件。");
            backend.WriteJournal(journal);
            await backend.PrepareAsync(preview.Package, paths, cancellationToken);

            IReadOnlyDictionary<string, string> preparedHashes =
                backend.SnapshotDirectory(paths.PreparedDirectory);
            if (!MatchesSnapshot(preparedHashes, expected))
                throw new IOException("同卷准备目录的逐文件 SHA-256 与预览清单不一致。");

            journal = journal with { State = "Prepared", UpdatedAt = DateTimeOffset.UtcNow };
            backend.WriteJournal(journal);
            Report("Prepared", "同卷准备目录已完整复制，逐文件 SHA-256 校验通过。");

            // Re-run the exact batch-8 verifier after preparation to catch changes
            // that happened while files were being copied.
            LocalZipPreviewVerification finalVerification =
                await previewWorkflow.VerifyAsync(preview, cancellationToken);
            if (!finalVerification.IsCurrent)
            {
                LocalZipApplyFailureKind kind =
                    finalVerification.Reason?.Contains("游戏正在运行", StringComparison.Ordinal) == true
                        ? LocalZipApplyFailureKind.GameRunning
                        : LocalZipApplyFailureKind.PreviewChanged;
                throw new LocalZipInstallTransactionException(kind,
                    finalVerification.Reason ?? "提交前复核失败。");
            }

            backend.ValidateTargetParent(preview.GameRoot, preview.DestinationDirectory);
            journal = journal with { State = "ReadyToCommit", UpdatedAt = DateTimeOffset.UtcNow };
            backend.WriteJournal(journal);
            Report("ReadyToCommit", "预览、目标目录、MOD 清单及准备文件均已复核，等待最终提交。");

            // Last cancellable boundary. From the durable Committing marker onward,
            // the workflow ignores cancellation and must finish or leave a recoverable log.
            cancellationToken.ThrowIfCancellationRequested();
            journal = journal with { State = "Committing", UpdatedAt = DateTimeOffset.UtcNow };
            backend.WriteJournal(journal);
            commitStarted = true;
            Report("Committing", "正在执行首次安装的同卷原子目录移动；此阶段不再响应取消。");

            backend.CommitDirectoryMove(journal);

            IReadOnlyDictionary<string, string> finalHashes =
                backend.SnapshotDirectory(journal.TargetDirectory);
            if (!MatchesSnapshot(finalHashes, journal.ExpectedFileHashes))
            {
                journal = journal with
                {
                    State = "RecoveryRequired",
                    SourcePolicyError = "最终目标目录逐文件 SHA-256 与事务日志不一致。",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                TryWriteJournal(journal);
                return Failure(true, false, LocalZipApplyFailureKind.RecoveryRequired,
                    "目录移动后最终 SHA-256 复核不一致。目标目录与事务现场均已保留，严禁自动删除，请人工核对。",
                    journal.TargetDirectory, selectedPolicy,
                    recoveryState: LocalZipInstallRecoveryState.PendingRecovery,
                    transactionId: transactionId,
                    cleanupCompleted: false,
                    cleanupFailureReason: "目标目录哈希不一致，已保留事务日志和现场。");
            }

            return CompleteVerifiedInstall(
                journal, selectedPolicy.Value, deferQuietPeriod, Report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !commitStarted)
        {
            string? cleanupError = CleanupUncommitted(journal);
            return Failure(true, true, LocalZipApplyFailureKind.Cancelled,
                cleanupError is null
                    ? "本地 ZIP 安装已在关键提交阶段之前取消；目标目录未修改。"
                    : "本地 ZIP 安装已取消，但事务清理未完成：" + cleanupError,
                journal?.TargetDirectory, selectedPolicy,
                transactionId: journal?.TransactionId,
                cleanupCompleted: cleanupError is null,
                cleanupFailureReason: cleanupError);
        }
        catch (LocalZipInstallTransactionException error)
        {
            if (commitStarted && journal is not null)
                return ResolveCommitFailure(journal, selectedPolicy, error, Report);

            string? cleanupError = CleanupUncommitted(journal);
            return Failure(selectedPolicy.HasValue, false, error.FailureKind,
                error.Message + (cleanupError is null ? "" : "；事务清理未完成：" + cleanupError),
                journal?.TargetDirectory ?? preview.DestinationDirectory, selectedPolicy,
                transactionId: journal?.TransactionId,
                cleanupCompleted: cleanupError is null,
                cleanupFailureReason: cleanupError);
        }
        catch (Exception error)
        {
            if (commitStarted && journal is not null)
                return ResolveCommitFailure(journal, selectedPolicy, error, Report);

            string? cleanupError = CleanupUncommitted(journal);
            return Failure(selectedPolicy.HasValue, false,
                journal is null ? LocalZipApplyFailureKind.PreparationFailed :
                    LocalZipApplyFailureKind.PreparationFailed,
                "本地 ZIP 安装准备失败：" + error.Message +
                (cleanupError is null ? "" : "；事务清理未完成：" + cleanupError),
                journal?.TargetDirectory ?? preview.DestinationDirectory, selectedPolicy,
                transactionId: journal?.TransactionId,
                cleanupCompleted: cleanupError is null,
                cleanupFailureReason: cleanupError);
        }
        finally
        {
            if (writing)
            {
                SetWriting(false, "TransactionFinished", "本地 ZIP 文件事务已完成或已安全收尾。");
                try { deferQuietPeriod(); }
                catch { /* A watcher callback cannot undo a completed filesystem outcome. */ }
            }
            applyGate.Release();
        }
    }

    LocalZipApplyResult CompleteVerifiedInstall(
        LocalZipInstallTransactionJournal journal,
        LocalZipHistoricalBindingPolicy policy,
        Action deferQuietPeriod,
        Action<string, string> report)
    {
        LocalZipSourcePolicyResult sourceResult;
        try
        {
            journal = journal with { State = "Committed", UpdatedAt = DateTimeOffset.UtcNow };
            backend.WriteJournal(journal);
            sourceResult = backend.ApplyHistoricalBindingPolicy(
                journal.GameRoot, journal.ModId, policy);
            journal = journal with
            {
                SourcePolicyApplied = sourceResult.Applied,
                SourcePolicyError = sourceResult.Error,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            backend.WriteJournal(journal);
        }
        catch (Exception error)
        {
            sourceResult = new(false, true,
                policy == LocalZipHistoricalBindingPolicy.RequireManualReview,
                policy == LocalZipHistoricalBindingPolicy.Unbind
                    ? "历史来源解绑待恢复" : "历史来源策略已记录但事务日志待恢复",
                error.Message);
            journal = journal with
            {
                State = "Committed",
                SourcePolicyApplied = false,
                SourcePolicyError = error.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            TryWriteJournal(journal);
        }

        string? cleanupError = null;
        if (sourceResult.Applied)
        {
            cleanupError = CleanupCommitted(journal);
        }
        else
        {
            // The install itself is verified and complete. Keep the committed journal
            // so recovery can retry an idempotent source unbind or finish cleanup.
            try { previewWorkflow.Discard(journal.PreviewWorkspaceDirectory); }
            catch (Exception error) { cleanupError = error.Message; }
            cleanupError = JoinErrors(cleanupError, sourceResult.Error);
        }

        bool partial = !sourceResult.Applied || cleanupError is not null;
        report("Completed", partial
            ? "MOD 已完成全新安装，但来源解绑或临时事务清理仍需恢复。"
            : "MOD 全新安装已完成，目标目录哈希复核通过。");

        return new(
            true, true, false, LocalZipApplyFailureKind.None,
            partial ? "安装已成功，但存在需要后续处理的来源或清理事项。" : null,
            partial ? LocalZipInstallRecoveryState.PendingRecovery :
                LocalZipInstallRecoveryState.Completed,
            journal.TransactionId,
            journal.TargetDirectory,
            policy,
            sourceResult.Action,
            sourceResult.SourceReviewRequired ||
                (policy == LocalZipHistoricalBindingPolicy.PreserveUnverified && journal.HadHistoricalSourceEvidence),
            policy == LocalZipHistoricalBindingPolicy.Unbind && !sourceResult.Applied,
            partial,
            true, true, true,
            cleanupError is null,
            cleanupError);
    }

    LocalZipApplyResult ResolveCommitFailure(
        LocalZipInstallTransactionJournal journal,
        LocalZipHistoricalBindingPolicy? policy,
        Exception error,
        Action<string, string> report)
    {
        bool preparedExists = backend.DirectoryExists(journal.PreparedDirectory);
        bool targetExists = backend.DirectoryExists(journal.TargetDirectory);

        if (targetExists && !preparedExists)
        {
            try
            {
                IReadOnlyDictionary<string, string> hashes =
                    backend.SnapshotDirectory(journal.TargetDirectory);
                if (MatchesSnapshot(hashes, journal.ExpectedFileHashes))
                    return CompleteVerifiedInstall(
                        journal,
                        ParsePolicy(journal.HistoricalBindingPolicy),
                        () => { },
                        report);

                journal = journal with
                {
                    State = "RecoveryRequired",
                    SourcePolicyError = "提交异常后发现目标目录哈希与预期不符。",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                TryWriteJournal(journal);
                return Failure(true, false, LocalZipApplyFailureKind.RecoveryRequired,
                    "提交阶段发生异常，目标目录已出现但哈希不符。已保留目标目录和事务日志，绝不递归删除目标目录。",
                    journal.TargetDirectory, policy,
                    recoveryState: LocalZipInstallRecoveryState.PendingRecovery,
                    transactionId: journal.TransactionId,
                    cleanupCompleted: false,
                    cleanupFailureReason: error.Message);
            }
            catch (Exception verifyError)
            {
                journal = journal with
                {
                    State = "RecoveryRequired",
                    SourcePolicyError = "提交结果无法安全核验：" + verifyError.Message,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                TryWriteJournal(journal);
                return Failure(true, false, LocalZipApplyFailureKind.RecoveryRequired,
                    "提交阶段发生异常，无法确认最终目录内容。已保留目标目录和事务日志，等待人工核对。",
                    journal.TargetDirectory, policy,
                    recoveryState: LocalZipInstallRecoveryState.PendingRecovery,
                    transactionId: journal.TransactionId,
                    cleanupCompleted: false,
                    cleanupFailureReason: verifyError.Message);
            }
        }

        // If the prepared directory still exists, the atomic move did not complete.
        // A target that appeared externally is never touched; only our GUID directory
        // and its journal are eligible for cleanup.
        string? cleanupError = CleanupUncommitted(journal);
        LocalZipApplyFailureKind failureKind =
            error is LocalZipInstallTransactionException transactionError
                ? transactionError.FailureKind
                : LocalZipApplyFailureKind.CommitFailed;
        return Failure(true, false, failureKind,
            "本地 ZIP 安装未完成，目标目录未被覆盖：" + error.Message +
            (cleanupError is null ? "" : "；事务清理未完成：" + cleanupError),
            journal.TargetDirectory, policy,
            transactionId: journal.TransactionId,
            cleanupCompleted: cleanupError is null,
            cleanupFailureReason: cleanupError);
    }

    LocalZipInstallRecoveryReport RecoverPendingTransactionsCore(
        string? requestedGameRoot,
        Action deferQuietPeriod,
        Action<LocalZipApplyProgress>? reportProgress)
    {
        IReadOnlyList<LocalZipPendingTransaction> pending;
        try { pending = backend.ReadPendingTransactions(); }
        catch (Exception error)
        {
            var failed = new LocalZipInstallRecoveryItem(
                "unknown", "RecoveryRequired", false, false, true, false, error.Message);
            return new(1, 0, 0, 1, [failed]);
        }

        string? filterRoot = requestedGameRoot is null
            ? null : Path.GetFullPath(requestedGameRoot);
        var items = new List<LocalZipInstallRecoveryItem>();
        int completed = 0;
        int cleaned = 0;
        bool writingStarted = false;

        void StartWriting()
        {
            if (writingStarted) return;
            writingStarted = true;
            try { reportProgress?.Invoke(new(
                "Recovering", "正在检查并安全收尾本地 ZIP 安装事务。", true)); }
            catch { }
            lock (progressGate) transactionWriting = true;
        }

        foreach (LocalZipPendingTransaction entry in pending)
        {
            if (entry.Journal is null)
            {
                items.Add(new(entry.FileName, "RecoveryRequired",
                    false, false, true, false, entry.Error ?? "事务日志无效，已保留。"));
                continue;
            }

            LocalZipInstallTransactionJournal journal = entry.Journal;
            if (filterRoot is not null &&
                !journal.GameRoot.Equals(filterRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            LocalZipHistoricalBindingPolicy recordedPolicy = ParsePolicy(journal.HistoricalBindingPolicy);
            bool reviewRequired = journal.HadHistoricalSourceEvidence &&
                (recordedPolicy == LocalZipHistoricalBindingPolicy.PreserveUnverified ||
                 recordedPolicy == LocalZipHistoricalBindingPolicy.RequireManualReview);
            try
            {
                backend.ValidateJournal(journal);
                StartWriting();
                LocalZipInstallRecoveryItem item = GameEnvironment.WithLock(
                    journal.GameRoot,
                    () => RecoverOne(journal, reviewRequired));
                if (item.Completed) completed++;
                if (item.Cleaned) cleaned++;
                items.Add(item);
            }
            catch (Exception error)
            {
                try
                {
                    journal = journal with
                    {
                        State = "RecoveryRequired",
                        SourcePolicyError = error.Message,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    backend.WriteJournal(journal);
                }
                catch { }
                items.Add(new(journal.TransactionId, "RecoveryRequired",
                    false, false, true, reviewRequired, error.Message,
                    journal.ModId, journal.TargetDirectory));
                StartWriting();
            }
        }

        if (writingStarted)
        {
            try { reportProgress?.Invoke(new(
                "RecoveryFinished", "本地 ZIP 安装事务恢复检查已结束。", false)); }
            catch { }
            lock (progressGate) transactionWriting = false;
            try { deferQuietPeriod(); }
            catch { }
        }

        int pendingCount = items.Count(item => item.Pending);
        return new(items.Count, completed, cleaned, pendingCount,
            Array.AsReadOnly(items.ToArray()));
    }

    LocalZipInstallRecoveryItem RecoverOne(
        LocalZipInstallTransactionJournal journal, bool reviewRequired)
    {
        bool targetExists = backend.DirectoryExists(journal.TargetDirectory);
        if (!targetExists)
        {
            backend.CleanupPreparation(journal);
            previewWorkflow.Discard(journal.PreviewWorkspaceDirectory);
            backend.DeleteJournal(journal);
            return new(journal.TransactionId, "Cleaned", false, true,
                false, reviewRequired, null, journal.ModId, journal.TargetDirectory);
        }

        IReadOnlyDictionary<string, string> targetHashes =
            backend.SnapshotDirectory(journal.TargetDirectory);
        if (!MatchesSnapshot(targetHashes, journal.ExpectedFileHashes))
        {
            journal = journal with
            {
                State = "RecoveryRequired",
                SourcePolicyError = "目标目录已存在，但逐文件 SHA-256 与日志预期不符；目标目录已保留。",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            backend.WriteJournal(journal);
            return new(journal.TransactionId, "RecoveryRequired",
                false, false, true, reviewRequired,
                "目标目录内容与事务日志不一致。绝不删除目标目录，请人工核对。",
                journal.ModId, journal.TargetDirectory);
        }

        if (!journal.State.Equals("Committed", StringComparison.Ordinal))
        {
            journal = journal with { State = "Committed", UpdatedAt = DateTimeOffset.UtcNow };
            backend.WriteJournal(journal);
        }

        if (!journal.SourcePolicyApplied)
        {
            LocalZipHistoricalBindingPolicy policy =
                ParsePolicy(journal.HistoricalBindingPolicy);
            LocalZipSourcePolicyResult sourceResult =
                backend.ApplyHistoricalBindingPolicy(journal.GameRoot, journal.ModId, policy);
            journal = journal with
            {
                SourcePolicyApplied = sourceResult.Applied,
                SourcePolicyError = sourceResult.Error,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            backend.WriteJournal(journal);
            if (!sourceResult.Applied)
                return new(journal.TransactionId, "Committed",
                    true, false, true, reviewRequired || sourceResult.SourceReviewRequired,
                    sourceResult.Error ?? "历史来源处理尚未完成。",
                    journal.ModId, journal.TargetDirectory);
            reviewRequired = reviewRequired || sourceResult.SourceReviewRequired;
        }

        backend.CleanupPreparation(journal);
        previewWorkflow.Discard(journal.PreviewWorkspaceDirectory);
        backend.DeleteJournal(journal);
        return new(journal.TransactionId, "Completed",
            true, false, false, reviewRequired, null,
            journal.ModId, journal.TargetDirectory);
    }

    string? CleanupUncommitted(LocalZipInstallTransactionJournal? journal)
    {
        if (journal is null) return null;
        try
        {
            backend.CleanupPreparation(journal);
            previewWorkflow.Discard(journal.PreviewWorkspaceDirectory);
            backend.DeleteJournal(journal);
            return null;
        }
        catch (Exception error)
        {
            var pending = journal with
            {
                State = "RecoveryRequired",
                SourcePolicyError = "未提交事务清理失败：" + error.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            TryWriteJournal(pending);
            return error.Message;
        }
    }

    string? CleanupCommitted(LocalZipInstallTransactionJournal journal)
    {
        var errors = new List<string>();
        try { previewWorkflow.Discard(journal.PreviewWorkspaceDirectory); }
        catch (Exception error) { errors.Add("预览暂存清理失败：" + error.Message); }

        try { backend.CleanupPreparation(journal); }
        catch (Exception error) { errors.Add("同卷事务目录清理失败：" + error.Message); }

        if (errors.Count == 0)
        {
            try { backend.DeleteJournal(journal); }
            catch (Exception error) { errors.Add("事务日志清理失败：" + error.Message); }
        }

        return errors.Count == 0 ? null : string.Join("；", errors);
    }

    void TryWriteJournal(LocalZipInstallTransactionJournal journal)
    {
        try { backend.WriteJournal(journal); }
        catch { /* Preserve the last durable journal; recovery will inspect the target. */ }
    }

    static bool MatchesSnapshot(
        IReadOnlyDictionary<string, string> actual,
        IReadOnlyDictionary<string, string> expected) =>
        actual.Count == expected.Count && expected.All(pair =>
            actual.TryGetValue(pair.Key, out string? hash) &&
            hash.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));

    static LocalZipHistoricalBindingPolicy ParsePolicy(string value) =>
        Enum.TryParse(value, out LocalZipHistoricalBindingPolicy policy) &&
        Enum.IsDefined(typeof(LocalZipHistoricalBindingPolicy), policy)
            ? policy : throw new InvalidDataException("事务日志中的历史来源策略无效。");

    static string? JoinErrors(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return first + "；" + second;
    }

    static LocalZipApplyResult Failure(
        bool confirmed,
        bool cancelled,
        LocalZipApplyFailureKind kind,
        string reason,
        string? target = null,
        LocalZipHistoricalBindingPolicy? policy = null,
        LocalZipInstallRecoveryState recoveryState = LocalZipInstallRecoveryState.NotRequired,
        string? transactionId = null,
        bool cleanupCompleted = true,
        string? cleanupFailureReason = null) =>
        new(confirmed, false, cancelled, kind, reason, recoveryState,
            transactionId, target, policy, "未处理历史来源绑定",
            policy == LocalZipHistoricalBindingPolicy.RequireManualReview,
            false, false, false, false, false,
            cleanupCompleted, cleanupFailureReason);

    public LocalZipInstallRecoveryReport RecoverPendingTransactions(
        string? gameRoot = null,
        Action? deferQuietPeriod = null,
        Action<LocalZipApplyProgress>? reportProgress = null)
    {
        applyGate.Wait();
        try
        {
            return RecoverPendingTransactionsCore(
                gameRoot, deferQuietPeriod ?? (() => { }), reportProgress);
        }
        finally
        {
            applyGate.Release();
        }
    }
}
