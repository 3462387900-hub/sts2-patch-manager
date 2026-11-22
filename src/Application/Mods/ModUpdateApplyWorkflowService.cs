using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

internal enum ModUpdateApplyFailureKind
{
    None,
    InvalidPreview,
    UserDeclined,
    Cancelled,
    TargetChanged,
    GameRunning,
    SourceIdentityMismatch,
    StagedArtifactChanged,
    RecoveryRequired,
    RecoveryFailed,
    BackupFailed,
    CommitFailed,
    RollbackFailed
}

internal enum ModUpdateApplyRecoveryState
{
    NotRequired,
    RolledBack,
    PendingManualRecovery,
    Recovered,
    Cleaned
}

internal sealed record ModUpdateApplyProgress(
    string Stage,
    string Message,
    bool TransactionWriting = false);

internal sealed record ModUpdateApplyPreview(
    string GameRoot,
    ManagedMod Target,
    ModUpdateStagingResult Staging,
    DateTimeOffset CreatedAt);

internal sealed record ModUpdateApplyConfirmation(
    ModUpdateApplyPreview Preview,
    bool Confirmed);

internal sealed record ModUpdateApplyResult(
    bool Confirmed,
    bool Success,
    bool RolledBack,
    bool Cancelled,
    ModUpdateApplyFailureKind FailureKind,
    string? FailureReason,
    ModUpdateApplyRecoveryState RecoveryState,
    string? BackupDirectory,
    string BeforeVersion,
    string? AfterVersion,
    bool InventoryInvalidationRequired,
    bool SourceCacheInvalidationRequired,
    bool PatchLifecycleRecheckRequired,
    bool CleanupCompleted,
    string? CleanupFailureReason);

internal sealed record ModUpdateApplyRecoveryItem(
    string TransactionId,
    string State,
    bool Recovered,
    bool Cleaned,
    bool Pending,
    string? BackupDirectory,
    string? Error);

internal sealed record ModUpdateApplyRecoveryReport(
    int CheckedCount,
    int RecoveredCount,
    int CleanedCount,
    IReadOnlyList<ModUpdateApplyRecoveryItem> Items)
{
    public bool HasPendingRecovery => Items.Any(item => item.Pending);
    public bool ChangedTargetOrCleanedTransaction => RecoveredCount > 0 || CleanedCount > 0;
}

internal sealed record ModUpdateApplyTransactionJournal(
    int SchemaVersion,
    string TransactionId,
    string GameRoot,
    string ModId,
    string TargetFolder,
    string TransactionDirectory,
    string BackupDirectory,
    string StagingDirectory,
    string StagedPayloadDirectory,
    string PreparedDirectory,
    string PreviousDirectory,
    string State,
    string BeforeVersion,
    string AfterVersion,
    Dictionary<string, string> OriginalFileHashes,
    Dictionary<string, string> StagedFileHashes,
    DateTimeOffset UpdatedAt);

internal interface IModUpdateApplyFileOperations
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    void CreateDirectory(string path);
    IReadOnlyList<string> EnumerateFileSystemEntries(string path);
    FileAttributes GetAttributes(string path);
    void CopyFile(string source, string destination, bool overwrite);
    void MoveDirectory(string source, string destination);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteDirectory(string path, bool recursive);
    void DeleteFile(string path);
    void WriteAllText(string path, string content);
    string ReadAllText(string path);
}

internal sealed class PhysicalModUpdateApplyFileOperations : IModUpdateApplyFileOperations
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public IReadOnlyList<string> EnumerateFileSystemEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path).ToArray();
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public void CopyFile(string source, string destination, bool overwrite) =>
        File.Copy(source, destination, overwrite);
    public void MoveDirectory(string source, string destination) =>
        Directory.Move(source, destination);
    public void MoveFile(string source, string destination, bool overwrite) =>
        File.Move(source, destination, overwrite);
    public void DeleteDirectory(string path, bool recursive) =>
        Directory.Delete(path, recursive);
    public void DeleteFile(string path) => File.Delete(path);
    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);
    public string ReadAllText(string path) => File.ReadAllText(path);
}

/// <summary>
/// Applies only a successful, isolated MOD update preview. Confirmation, pre-commit
/// verification, backup, directory replacement, rollback and crash recovery are
/// explicit boundaries. No UI or network dependency is used here.
/// </summary>
internal sealed class ModUpdateApplyWorkflowService
{
    const string JournalFileName = "transaction.json";
    static readonly Regex StageDirectoryName = new(
        @"\Astage-[0-9a-fA-F]{32}\z", RegexOptions.Compiled);
    static readonly Regex TransactionDirectoryName = new(
        @"\A[0-9a-fA-F]{32}\z", RegexOptions.Compiled);
    static readonly Regex GitHubRepositoryName = new(
        @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*\z",
        RegexOptions.Compiled);

    static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = true
    };

    readonly string backupRoot;
    readonly IModUpdateApplyFileOperations files;
    readonly Func<string, bool> isGameRunning;
    readonly Action<ModUpdateApplyProgress>? reportProgress;
    readonly Action? deferWatcherQuietPeriod;

    public ModUpdateApplyWorkflowService(
        string? backupRoot = null,
        IModUpdateApplyFileOperations? fileOperations = null,
        Func<string, bool>? isGameRunning = null,
        Action<ModUpdateApplyProgress>? reportProgress = null,
        Action? deferWatcherQuietPeriod = null)
    {
        this.backupRoot = Path.GetFullPath(backupRoot ??
            Path.Combine(Path.GetTempPath(), "SpirePatch", "ModUpdateBackups"));
        files = fileOperations ?? new PhysicalModUpdateApplyFileOperations();
        this.isGameRunning = isGameRunning ?? GameEnvironment.Running;
        this.reportProgress = reportProgress;
        this.deferWatcherQuietPeriod = deferWatcherQuietPeriod;
    }

    /// <summary>
    /// Captures the already-computed read-only preview as an immutable apply boundary.
    /// It does not write to the game directory or create a backup.
    /// </summary>
    public ModUpdateApplyPreview CreatePreview(
        string gameRoot,
        ManagedMod target,
        ModUpdateStagingResult staging)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(staging);

        if (!staging.ValidationPassed || !staging.IntegrityVerified ||
            staging.StagingDirectory is null || staging.StagedPayloadDirectory is null ||
            staging.CurrentFileHashes.Count == 0 || staging.StagedFileHashes.Count == 0)
        {
            throw new InvalidOperationException("只有完整校验成功且包含前后文件快照的暂存结果才能进入写入预览。");
        }

        var frozenStaging = staging with
        {
            CurrentFileHashes = staging.CurrentFileHashes.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            StagedFileHashes = staging.StagedFileHashes.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };

        return new ModUpdateApplyPreview(
            Path.GetFullPath(gameRoot),
            target with { },
            frozenStaging,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Records the user's explicit decision. A declined confirmation never reaches
    /// the backup or transaction boundary.
    /// </summary>
    public ModUpdateApplyConfirmation Confirm(
        ModUpdateApplyPreview preview,
        bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new ModUpdateApplyConfirmation(preview, confirmed);
    }

    public Task<ModUpdateApplyResult> ApplyAsync(
        ModUpdateApplyConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        if (!confirmation.Confirmed)
            return Task.FromResult(Result(
                false,
                false,
                false,
                true,
                ModUpdateApplyFailureKind.UserDeclined,
                "用户拒绝了更新确认，没有创建备份，也没有修改目标 MOD。",
                ModUpdateApplyRecoveryState.NotRequired,
                null,
                confirmation.Preview.Staging.LocalVersion,
                null,
                cleanupCompleted: true));

        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Cancelled(
                confirmation.Preview.Staging.LocalVersion));

        // Do not pass the token to Task.Run: the workflow must always return a
        // structured result, including when cancellation arrives during a commit.
        return Task.Run(
            () => ApplyCore(confirmation.Preview, cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// Scans this manager's isolated transaction root and restores any interrupted
    /// update before another apply is allowed to proceed.
    /// </summary>
    public ModUpdateApplyRecoveryReport RecoverPendingTransactions(string gameRoot)
    {
        string normalizedRoot = GameEnvironment.Root(gameRoot);
        return GameEnvironment.WithLock(
            normalizedRoot,
            () => RecoverPendingTransactionsLocked(normalizedRoot));
    }

    ModUpdateApplyResult ApplyCore(
        ModUpdateApplyPreview preview,
        CancellationToken cancellationToken)
    {
        string gameRoot;
        try
        {
            gameRoot = GameEnvironment.Root(preview.GameRoot);
            return GameEnvironment.WithLock(
                gameRoot,
                () => ApplyLocked(preview, gameRoot, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(preview.Staging.LocalVersion);
        }
        catch (Exception error)
        {
            return Result(
                true, false, false, false,
                ModUpdateApplyFailureKind.InvalidPreview,
                "无法安全打开游戏目录或取得文件事务锁：" + error.Message,
                ModUpdateApplyRecoveryState.NotRequired,
                null,
                preview.Staging.LocalVersion,
                null,
                cleanupCompleted: true);
        }
    }

    ModUpdateApplyResult ApplyLocked(
        ModUpdateApplyPreview preview,
        string gameRoot,
        CancellationToken cancellationToken)
    {
        ModUpdateStagingResult staging = preview.Staging;

        ModUpdateApplyRecoveryReport recovery;
        try
        {
            recovery = RecoverPendingTransactionsLocked(gameRoot);
        }
        catch (Exception error)
        {
            return Result(
                true, false, false, false,
                ModUpdateApplyFailureKind.RecoveryFailed,
                "检查上次中断的更新事务失败：" + error.Message,
                ModUpdateApplyRecoveryState.PendingManualRecovery,
                null,
                staging.LocalVersion,
                null,
                cleanupCompleted: false,
                cleanupFailureReason: error.Message);
        }

        if (recovery.Items.Count > 0)
        {
            bool pending = recovery.HasPendingRecovery;
            return Result(
                true, false, false, false,
                pending ? ModUpdateApplyFailureKind.RecoveryFailed :
                    ModUpdateApplyFailureKind.RecoveryRequired,
                pending
                    ? "发现未完成事务且自动恢复未能完成。已保留备份，请先完成人工或后续恢复。"
                    : "已处理上次遗留事务。为避免使用过期预览，本次写入已中止，请重新扫描并预览。",
                pending ? ModUpdateApplyRecoveryState.PendingManualRecovery :
                    recovery.RecoveredCount > 0 ? ModUpdateApplyRecoveryState.Recovered :
                    ModUpdateApplyRecoveryState.Cleaned,
                recovery.Items.FirstOrDefault(item => item.Pending)?.BackupDirectory,
                staging.LocalVersion,
                null,
                cleanupCompleted: !pending);
        }

        try
        {
            // 1. Recheck the exact target tree captured by the read-only preview.
            VerifyTargetSnapshot(preview);

            // 2. Never replace a MOD while the game process is running.
            if (isGameRunning(gameRoot))
                return Rejected(
                    ModUpdateApplyFailureKind.GameRunning,
                    "检测到游戏仍在运行，已拒绝覆盖写入。",
                    staging.LocalVersion);

            // 3. Recheck MOD identity, active location and canonical path boundaries.
            VerifySourceAndTargetIdentity(preview, gameRoot);

            // 4. Recheck every staged payload file and reject missing/extra/tampered data.
            VerifyStagedArtifact(preview, gameRoot);

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(staging.LocalVersion);
        }
        catch (PreCommitVerificationException error)
        {
            return Rejected(error.Kind, error.Message, staging.LocalVersion);
        }
        catch (Exception error)
        {
            return Rejected(
                ModUpdateApplyFailureKind.InvalidPreview,
                "写入前复核失败，目标目录未修改：" + error.Message,
                staging.LocalVersion);
        }

        string targetFolder = Path.GetFullPath(preview.Target.Folder);
        string modsRoot = Path.GetDirectoryName(targetFolder)!;
        string transactionId = Guid.NewGuid().ToString("N");
        string transactionDirectory = Path.Combine(backupRoot, transactionId);
        string backupDirectory = Path.Combine(transactionDirectory, "backup");
        string preparedDirectory = Path.Combine(
            modsRoot, ".spirepatch-update-" + transactionId + ".prepared");
        string previousDirectory = Path.Combine(
            modsRoot, ".spirepatch-update-" + transactionId + ".previous");

        var journal = new ModUpdateApplyTransactionJournal(
            1,
            transactionId,
            gameRoot,
            staging.ModId,
            targetFolder,
            transactionDirectory,
            backupDirectory,
            staging.StagingDirectory!,
            staging.StagedPayloadDirectory!,
            preparedDirectory,
            previousDirectory,
            "BackupReady",
            staging.LocalVersion,
            staging.StagedVersion!,
            staging.CurrentFileHashes.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            staging.StagedFileHashes.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);

        bool backupCreated = false;
        bool transactionWriting = false;
        try
        {
            EnsureBackupRootIsolated(gameRoot);
            GameEnvironment.NoLinks(backupRoot);
            files.CreateDirectory(backupRoot);
            files.CreateDirectory(transactionDirectory);

            WriteJournal(journal with { State = "BackupCreating", UpdatedAt = DateTimeOffset.UtcNow });
            CopySnapshotFiles(targetFolder, backupDirectory, journal.OriginalFileHashes,
                cancellationToken);
            if (!MatchesSnapshot(SnapshotDirectory(backupDirectory), journal.OriginalFileHashes))
                throw new IOException("备份完成后 SHA-256 复核不一致。");
            backupCreated = true;
            journal = journal with { State = "BackupReady", UpdatedAt = DateTimeOffset.UtcNow };
            WriteJournal(journal);

            Report("BackupCreated", "原 MOD 已完整备份，正在准备新目录。");
            cancellationToken.ThrowIfCancellationRequested();
            transactionWriting = true;
            Report("PreparingReplacement", "正在隔离准备新 MOD 目录。", true);

            CopySnapshotFiles(
                journal.StagedPayloadDirectory,
                preparedDirectory,
                journal.StagedFileHashes,
                cancellationToken);
            if (!MatchesSnapshot(SnapshotDirectory(preparedDirectory), journal.StagedFileHashes))
                throw new IOException("待提交目录的 SHA-256 复核不一致。");

            journal = journal with { State = "Prepared", UpdatedAt = DateTimeOffset.UtcNow };
            WriteJournal(journal);
            Report("ReadyToCommit", "备份与新目录均已校验，等待进入关键提交阶段。", true);

            // Last cancellable boundary. After this check, no cancellation token is
            // observed until the complete commit or complete rollback has finished.
            cancellationToken.ThrowIfCancellationRequested();

            journal = journal with { State = "Committing", UpdatedAt = DateTimeOffset.UtcNow };
            WriteJournal(journal);
            transactionWriting = true;
            Report("Committing", "正在执行目录替换；此阶段将完整提交或完整回滚。", true);

            try
            {
                files.MoveDirectory(targetFolder, previousDirectory);
                files.MoveDirectory(preparedDirectory, targetFolder);

                if (!MatchesSnapshot(SnapshotDirectory(targetFolder), journal.StagedFileHashes))
                    throw new IOException("提交后的目标目录与已校验暂存产物不一致。");

                journal = journal with { State = "Committed", UpdatedAt = DateTimeOffset.UtcNow };
                WriteJournal(journal);

                string? cleanupError = CleanupAfterSafeOutcome(journal, includeStaging: true);
                Report("Completed", "MOD 更新已提交。");
                return Result(
                    true, true, false, false,
                    ModUpdateApplyFailureKind.None,
                    null,
                    ModUpdateApplyRecoveryState.NotRequired,
                    files.DirectoryExists(backupDirectory) ? backupDirectory : null,
                    staging.LocalVersion,
                    staging.StagedVersion,
                    cleanupCompleted: cleanupError is null,
                    cleanupFailureReason: cleanupError,
                    invalidateCaches: true);
            }
            catch (Exception commitError)
            {
                Report("RollingBack", "提交失败，正在从隔离备份恢复原 MOD。", true);
                try
                {
                    RestoreOriginal(journal);
                    journal = journal with { State = "RolledBack", UpdatedAt = DateTimeOffset.UtcNow };
                    WriteJournal(journal);
                    string? cleanupError = CleanupAfterSafeOutcome(journal, includeStaging: true);
                    Report("RollbackCompleted", "原 MOD 已完整恢复。");
                    return Result(
                        true, false, true, false,
                        ModUpdateApplyFailureKind.CommitFailed,
                        "更新提交失败，已自动恢复原 MOD：" + commitError.Message,
                        ModUpdateApplyRecoveryState.RolledBack,
                        files.DirectoryExists(backupDirectory) ? backupDirectory : null,
                        staging.LocalVersion,
                        staging.LocalVersion,
                        cleanupCompleted: cleanupError is null,
                        cleanupFailureReason: cleanupError,
                        invalidateCaches: true);
                }
                catch (Exception rollbackError)
                {
                    try
                    {
                        journal = journal with
                        {
                            State = "RollbackPending",
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        WriteJournal(journal);
                    }
                    catch
                    {
                        // The earlier Committing marker remains the recovery signal.
                    }

                    return Result(
                        true, false, false, false,
                        ModUpdateApplyFailureKind.RollbackFailed,
                        "更新提交与自动回滚均失败。已保留备份和事务标记，必须先恢复原 MOD。提交错误：" +
                        commitError.Message + "；回滚错误：" + rollbackError.Message,
                        ModUpdateApplyRecoveryState.PendingManualRecovery,
                        backupDirectory,
                        staging.LocalVersion,
                        null,
                        cleanupCompleted: false,
                        cleanupFailureReason: rollbackError.Message,
                        invalidateCaches: true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            string? cleanupError = null;
            try
            {
                if (files.DirectoryExists(targetFolder) &&
                    MatchesSnapshot(SnapshotDirectory(targetFolder), journal.OriginalFileHashes))
                {
                    if (files.DirectoryExists(preparedDirectory))
                        files.DeleteDirectory(preparedDirectory, true);
                    if (files.DirectoryExists(transactionDirectory))
                        files.DeleteDirectory(transactionDirectory, true);
                }
                else if (backupCreated)
                {
                    journal = journal with { State = "RollbackPending", UpdatedAt = DateTimeOffset.UtcNow };
                    WriteJournal(journal);
                    cleanupError = "取消时目标目录状态无法确认，已保留备份等待恢复。";
                }
            }
            catch (Exception error)
            {
                cleanupError = error.Message;
            }

            return Result(
                true, false, false, true,
                cleanupError is null ? ModUpdateApplyFailureKind.Cancelled :
                    ModUpdateApplyFailureKind.RollbackFailed,
                cleanupError is null ? "关键提交阶段开始前收到取消请求，目标 MOD 未修改。" :
                    "取消发生在提交前，但现场清理/核对未完成：" + cleanupError,
                cleanupError is null ? ModUpdateApplyRecoveryState.NotRequired :
                    ModUpdateApplyRecoveryState.PendingManualRecovery,
                backupCreated && files.DirectoryExists(backupDirectory) ? backupDirectory : null,
                staging.LocalVersion,
                null,
                cleanupCompleted: cleanupError is null,
                cleanupFailureReason: cleanupError);
        }
        catch (Exception error)
        {
            string? cleanupError = null;
            try
            {
                if (files.DirectoryExists(targetFolder) &&
                    MatchesSnapshot(SnapshotDirectory(targetFolder), journal.OriginalFileHashes))
                {
                    if (files.DirectoryExists(preparedDirectory))
                        files.DeleteDirectory(preparedDirectory, true);
                    if (files.DirectoryExists(transactionDirectory))
                        files.DeleteDirectory(transactionDirectory, true);
                }
                else if (backupCreated)
                {
                    journal = journal with { State = "RollbackPending", UpdatedAt = DateTimeOffset.UtcNow };
                    WriteJournal(journal);
                    cleanupError = "目标目录状态无法确认，已保留备份等待恢复。";
                }
                else if (files.DirectoryExists(transactionDirectory))
                {
                    files.DeleteDirectory(transactionDirectory, true);
                }
            }
            catch (Exception cleanupFailure)
            {
                cleanupError = cleanupFailure.Message;
            }

            return Result(
                true, false, false, false,
                cleanupError is null ? ModUpdateApplyFailureKind.BackupFailed :
                    ModUpdateApplyFailureKind.RollbackFailed,
                "备份或提交准备阶段失败，目标 MOD 未进入提交阶段：" + error.Message,
                cleanupError is null ? ModUpdateApplyRecoveryState.NotRequired :
                    ModUpdateApplyRecoveryState.PendingManualRecovery,
                backupCreated && files.DirectoryExists(backupDirectory) ? backupDirectory : null,
                staging.LocalVersion,
                null,
                cleanupCompleted: cleanupError is null,
                cleanupFailureReason: cleanupError);
        }
        finally
        {
            if (transactionWriting)
                Report("TransactionFinished", "文件事务已结束。", false);
        }
    }

    ModUpdateApplyRecoveryReport RecoverPendingTransactionsLocked(string gameRoot)
    {
        if (!files.DirectoryExists(backupRoot))
            return new(0, 0, 0, Array.Empty<ModUpdateApplyRecoveryItem>());

        EnsureBackupRootIsolated(gameRoot);
        GameEnvironment.NoLinks(backupRoot);

        var items = new List<ModUpdateApplyRecoveryItem>();
        int recoveredCount = 0;
        int cleanedCount = 0;

        foreach (string transactionDirectory in files.EnumerateFileSystemEntries(backupRoot))
        {
            if (!files.DirectoryExists(transactionDirectory))
                continue;

            string transactionId = Path.GetFileName(transactionDirectory);
            string marker = Path.Combine(transactionDirectory, JournalFileName);
            if (!TransactionDirectoryName.IsMatch(transactionId) || !files.FileExists(marker))
            {
                items.Add(new(
                    transactionId,
                    "Unknown",
                    false,
                    false,
                    true,
                    Path.Combine(transactionDirectory, "backup"),
                    "事务目录缺少有效标记，已保留现场，不自动删除。"));
                continue;
            }

            ModUpdateApplyTransactionJournal? journal;
            try
            {
                journal = JsonSerializer.Deserialize<ModUpdateApplyTransactionJournal>(
                    files.ReadAllText(marker));
            }
            catch (Exception error)
            {
                items.Add(new(
                    transactionId, "InvalidJournal", false, false, true,
                    Path.Combine(transactionDirectory, "backup"),
                    "事务标记无法读取，已保留备份：" + error.Message));
                continue;
            }

            if (journal is null)
            {
                items.Add(new(
                    transactionId, "InvalidJournal", false, false, true,
                    Path.Combine(transactionDirectory, "backup"),
                    "事务标记内容为空，已保留现场。"));
                continue;
            }

            if (!journal.GameRoot.Equals(gameRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                ValidateJournalPaths(journal, gameRoot, transactionDirectory);
                string target = journal.TargetFolder;
                bool targetIsOriginal = files.DirectoryExists(target) &&
                    MatchesSnapshot(SnapshotDirectory(target), journal.OriginalFileHashes);
                bool targetIsCommitted = journal.State == "Committed" &&
                    files.DirectoryExists(target) &&
                    MatchesSnapshot(SnapshotDirectory(target), journal.StagedFileHashes);

                bool recovered = false;
                if (!targetIsOriginal && !targetIsCommitted)
                {
                    RestoreOriginal(journal);
                    recovered = true;
                    recoveredCount++;
                }

                journal = journal with
                {
                    State = recovered ? "RolledBack" : journal.State,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                WriteJournal(journal);

                string? cleanupError = CleanupAfterSafeOutcome(journal, includeStaging: true);
                if (cleanupError is null)
                    cleanedCount++;

                items.Add(new(
                    transactionId,
                    journal.State,
                    recovered,
                    cleanupError is null,
                    cleanupError is not null,
                    files.DirectoryExists(journal.BackupDirectory) ? journal.BackupDirectory : null,
                    cleanupError));
            }
            catch (Exception error)
            {
                items.Add(new(
                    transactionId,
                    journal.State,
                    false,
                    false,
                    true,
                    journal.BackupDirectory,
                    "恢复未完成，备份与事务标记均予以保留：" + error.Message));
            }
        }

        return new(items.Count, recoveredCount, cleanedCount, items);
    }

    void VerifyTargetSnapshot(ModUpdateApplyPreview preview)
    {
        string targetFolder = Path.GetFullPath(preview.Target.Folder);
        GameEnvironment.NoLinks(targetFolder);
        if (!files.DirectoryExists(targetFolder))
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.TargetChanged,
                "预览后目标 MOD 目录已被删除。");

        Dictionary<string, string> current = SnapshotDirectory(targetFolder);
        if (!MatchesSnapshot(current, preview.Staging.CurrentFileHashes))
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.TargetChanged,
                "预览后目标 MOD 文件发生外部修改，必须重新预览后才能覆盖。");
    }

    void VerifySourceAndTargetIdentity(ModUpdateApplyPreview preview, string gameRoot)
    {
        ManagedMod target = preview.Target;
        ModUpdateStagingResult staging = preview.Staging;
        string normalizedTarget = Path.GetFullPath(target.Folder);
        string modsRoot = Path.GetFullPath(Path.Combine(gameRoot, "mods"));

        if (!target.Enabled || target.Storage is not null ||
            !target.Id.Equals(staging.ModId, StringComparison.OrdinalIgnoreCase) ||
            !normalizedTarget.Equals(Path.GetFullPath(staging.TargetFolder), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(normalizedTarget)!.Equals(modsRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedTarget.Equals(modsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.SourceIdentityMismatch,
                "MOD ID、活动目录或预览目标路径不一致，已拒绝写入。");
        }

        GameEnvironment.NoLinks(normalizedTarget);
        string manifest = Path.GetFullPath(target.Manifest);
        if (!IsSameOrWithin(manifest, normalizedTarget) || manifest.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
            !files.FileExists(manifest))
        {
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.SourceIdentityMismatch,
                "目标 MOD 清单路径无效或越过目标目录。");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
        JsonElement root = document.RootElement;
        string manifestId = root.TryGetProperty("id", out JsonElement idValue) &&
            idValue.ValueKind == JsonValueKind.String ? idValue.GetString() ?? string.Empty : string.Empty;
        string manifestVersion = root.TryGetProperty("version", out JsonElement versionValue) &&
            versionValue.ValueKind == JsonValueKind.String ? versionValue.GetString() ?? string.Empty : string.Empty;

        if (!manifestId.Equals(staging.ModId, StringComparison.OrdinalIgnoreCase) ||
            !manifestVersion.Equals(staging.LocalVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.SourceIdentityMismatch,
                "目标 MOD 清单中的 ID 或版本与暂存预览不一致。");
        }

        bool sourceValid = staging.Source switch
        {
            ModUpdateStagingSource.GitHub => GitHubRepositoryName.IsMatch(staging.SourceIdentifier),
            ModUpdateStagingSource.Workshop =>
                ulong.TryParse(staging.SourceIdentifier, out ulong workshopId) && workshopId > 0,
            _ => false
        };

        if (!sourceValid)
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.SourceIdentityMismatch,
                "更新来源身份无效，已拒绝覆盖写入。");
    }

    void VerifyStagedArtifact(ModUpdateApplyPreview preview, string gameRoot)
    {
        ModUpdateStagingResult staging = preview.Staging;
        string stagingDirectory = Path.GetFullPath(staging.StagingDirectory!);
        string payloadDirectory = Path.GetFullPath(staging.StagedPayloadDirectory!);

        if (!StageDirectoryName.IsMatch(Path.GetFileName(stagingDirectory)) ||
            !IsSameOrWithin(payloadDirectory, stagingDirectory) ||
            payloadDirectory.Equals(stagingDirectory, StringComparison.OrdinalIgnoreCase) ||
            IsSameOrWithin(stagingDirectory, gameRoot) ||
            IsSameOrWithin(gameRoot, stagingDirectory))
        {
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.StagedArtifactChanged,
                "暂存工作区路径不合法、已移动或与游戏目录重叠。");
        }

        GameEnvironment.NoLinks(stagingDirectory);
        GameEnvironment.NoLinks(payloadDirectory);
        if (!files.DirectoryExists(stagingDirectory) || !files.DirectoryExists(payloadDirectory))
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.StagedArtifactChanged,
                "等待确认期间暂存目录或暂存产物已被删除。");

        Dictionary<string, string> actual = SnapshotDirectory(
            payloadDirectory,
            staging.Source == ModUpdateStagingSource.Workshop);
        if (!MatchesSnapshot(actual, staging.StagedFileHashes))
            throw new PreCommitVerificationException(
                ModUpdateApplyFailureKind.StagedArtifactChanged,
                "等待确认期间暂存产物被修改、缺失或增加了未校验文件。");
    }

    void EnsureBackupRootIsolated(string gameRoot)
    {
        if (IsSameOrWithin(backupRoot, gameRoot) || IsSameOrWithin(gameRoot, backupRoot))
            throw new InvalidOperationException("隔离备份目录必须与游戏目录完全分离。");
    }

    void ValidateJournalPaths(
        ModUpdateApplyTransactionJournal journal,
        string gameRoot,
        string transactionDirectory)
    {
        if (journal.SchemaVersion != 1 ||
            !TransactionDirectoryName.IsMatch(journal.TransactionId) ||
            !Path.GetFullPath(journal.TransactionDirectory).Equals(
                Path.GetFullPath(transactionDirectory), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(Path.GetFullPath(transactionDirectory))!.Equals(
                backupRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(journal.GameRoot).Equals(gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("事务标记中的根目录或事务 ID 不匹配。");
        }

        string target = Path.GetFullPath(journal.TargetFolder);
        string modsRoot = Path.GetFullPath(Path.Combine(gameRoot, "mods"));
        if (!Path.GetDirectoryName(target)!.Equals(modsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("事务标记中的目标目录越过活动 mods 目录。");

        string expectedBackup = Path.Combine(transactionDirectory, "backup");
        string expectedPrepared = Path.Combine(
            modsRoot, ".spirepatch-update-" + journal.TransactionId + ".prepared");
        string expectedPrevious = Path.Combine(
            modsRoot, ".spirepatch-update-" + journal.TransactionId + ".previous");
        if (!Path.GetFullPath(journal.BackupDirectory).Equals(expectedBackup, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(journal.PreparedDirectory).Equals(expectedPrepared, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(journal.PreviousDirectory).Equals(expectedPrevious, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("事务标记中的备份或替换路径越界。");

        string stagingDirectory = Path.GetFullPath(journal.StagingDirectory);
        string payloadDirectory = Path.GetFullPath(journal.StagedPayloadDirectory);
        if (!StageDirectoryName.IsMatch(Path.GetFileName(stagingDirectory)) ||
            !IsSameOrWithin(payloadDirectory, stagingDirectory) ||
            IsSameOrWithin(stagingDirectory, gameRoot) ||
            IsSameOrWithin(gameRoot, stagingDirectory) ||
            IsSameOrWithin(stagingDirectory, backupRoot) ||
            IsSameOrWithin(backupRoot, stagingDirectory))
            throw new InvalidDataException("事务标记中的暂存路径无效。");

        GameEnvironment.NoLinks(target);
        GameEnvironment.NoLinks(transactionDirectory);
        GameEnvironment.NoLinks(stagingDirectory);
    }

    void RestoreOriginal(ModUpdateApplyTransactionJournal journal)
    {
        string target = journal.TargetFolder;
        if (files.DirectoryExists(target) &&
            MatchesSnapshot(SnapshotDirectory(target), journal.OriginalFileHashes))
            return;

        Exception? previousFailure = null;
        if (files.DirectoryExists(journal.PreviousDirectory))
        {
            try
            {
                Dictionary<string, string> previousSnapshot = SnapshotDirectory(journal.PreviousDirectory);
                if (MatchesSnapshot(previousSnapshot, journal.OriginalFileHashes))
                {
                    RestoreByMovingPrevious(journal);
                    return;
                }
            }
            catch (Exception error)
            {
                previousFailure = error;
            }
        }

        try
        {
            RestoreByCopyingBackup(journal);
        }
        catch (Exception backupFailure)
        {
            if (previousFailure is not null)
                throw new AggregateException(
                    "原目录副本与隔离备份均未能完成恢复。",
                    previousFailure,
                    backupFailure);
            throw;
        }
    }

    void RestoreByMovingPrevious(ModUpdateApplyTransactionJournal journal)
    {
        string target = journal.TargetFolder;
        string quarantine = target + ".partial-" + journal.TransactionId;
        if (files.DirectoryExists(quarantine))
            throw new IOException("恢复隔离目录已存在，拒绝覆盖：" + quarantine);

        bool quarantined = false;
        try
        {
            if (files.DirectoryExists(target))
            {
                files.MoveDirectory(target, quarantine);
                quarantined = true;
            }

            files.MoveDirectory(journal.PreviousDirectory, target);
            if (!MatchesSnapshot(SnapshotDirectory(target), journal.OriginalFileHashes))
                throw new IOException("从替换前目录恢复后 SHA-256 不一致。");

            if (quarantined && files.DirectoryExists(quarantine))
                files.DeleteDirectory(quarantine, true);
        }
        catch
        {
            if (quarantined && !files.DirectoryExists(target) && files.DirectoryExists(quarantine))
            {
                try { files.MoveDirectory(quarantine, target); }
                catch { /* Preserve the quarantine and transaction backup for recovery. */ }
            }
            throw;
        }
    }

    void RestoreByCopyingBackup(ModUpdateApplyTransactionJournal journal)
    {
        string backup = journal.BackupDirectory;
        if (!files.DirectoryExists(backup) ||
            !MatchesSnapshot(SnapshotDirectory(backup), journal.OriginalFileHashes))
            throw new IOException("隔离备份不存在或 SHA-256 校验失败，禁止使用不完整备份恢复。");

        string target = journal.TargetFolder;
        string restoreDirectory = target + ".restore-" + journal.TransactionId;
        string quarantine = target + ".partial-" + journal.TransactionId;
        if (files.DirectoryExists(restoreDirectory) || files.DirectoryExists(quarantine))
            throw new IOException("恢复临时目录或隔离目录已存在，拒绝覆盖。");

        CopySnapshotFiles(backup, restoreDirectory, journal.OriginalFileHashes, CancellationToken.None);
        if (!MatchesSnapshot(SnapshotDirectory(restoreDirectory), journal.OriginalFileHashes))
            throw new IOException("从隔离备份复制出的恢复目录 SHA-256 不一致。");

        bool quarantined = false;
        try
        {
            if (files.DirectoryExists(target))
            {
                files.MoveDirectory(target, quarantine);
                quarantined = true;
            }

            files.MoveDirectory(restoreDirectory, target);
            if (!MatchesSnapshot(SnapshotDirectory(target), journal.OriginalFileHashes))
                throw new IOException("恢复后的目标 MOD SHA-256 不一致。");

            if (quarantined && files.DirectoryExists(quarantine))
                files.DeleteDirectory(quarantine, true);
        }
        catch
        {
            if (quarantined && !files.DirectoryExists(target) && files.DirectoryExists(quarantine))
            {
                try { files.MoveDirectory(quarantine, target); }
                catch { /* Keep the original backup and both recovery paths. */ }
            }
            throw;
        }
    }

    string? CleanupAfterSafeOutcome(
        ModUpdateApplyTransactionJournal journal,
        bool includeStaging)
    {
        try
        {
            if (files.DirectoryExists(journal.PreparedDirectory))
                files.DeleteDirectory(journal.PreparedDirectory, true);
            if (files.DirectoryExists(journal.PreviousDirectory))
                files.DeleteDirectory(journal.PreviousDirectory, true);

            if (includeStaging)
                DeleteStagingWorkspace(journal.StagingDirectory);

            if (files.DirectoryExists(journal.TransactionDirectory))
                files.DeleteDirectory(journal.TransactionDirectory, true);

            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    void DeleteStagingWorkspace(string stagingDirectory)
    {
        string full = Path.GetFullPath(stagingDirectory);
        if (!StageDirectoryName.IsMatch(Path.GetFileName(full)))
            throw new InvalidOperationException("拒绝清理非标准暂存工作区。");
        GameEnvironment.NoLinks(full);
        if (files.DirectoryExists(full))
            files.DeleteDirectory(full, true);
    }

    void CopySnapshotFiles(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyDictionary<string, string> expected,
        CancellationToken cancellationToken)
    {
        GameEnvironment.NoLinks(sourceRoot);
        if (!files.DirectoryExists(sourceRoot))
            throw new DirectoryNotFoundException("源目录不存在：" + sourceRoot);

        files.CreateDirectory(destinationRoot);
        foreach (var pair in expected.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = SafeCombine(sourceRoot, pair.Key);
            string destination = SafeCombine(destinationRoot, pair.Key);
            GameEnvironment.NoLinks(source);

            if (!files.FileExists(source))
                throw new FileNotFoundException("源文件缺失：" + pair.Key, source);

            FileAttributes attributes = files.GetAttributes(source);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("拒绝复制链接或非普通文件：" + pair.Key);

            string? parent = Path.GetDirectoryName(destination);
            if (parent is not null)
                files.CreateDirectory(parent);
            files.CopyFile(source, destination, overwrite: true);
        }
    }

    Dictionary<string, string> SnapshotDirectory(
        string root,
        bool excludeWorkshopReport = false)
    {
        GameEnvironment.NoLinks(root);
        if (!files.DirectoryExists(root))
            throw new DirectoryNotFoundException("目录不存在：" + root);

        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in files.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = files.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("目录中包含链接或重解析点，拒绝继续：" + entry);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (excludeWorkshopReport &&
                    relative.Equals("assembly-manifest.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                ValidateRelativePath(relative);
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                if (!snapshot.TryAdd(relative, hash))
                    throw new InvalidDataException("目录中存在大小写冲突或重复文件路径：" + relative);
            }
        }

        return snapshot;
    }

    static bool MatchesSnapshot(
        IReadOnlyDictionary<string, string> actual,
        IReadOnlyDictionary<string, string> expected) =>
        actual.Count == expected.Count &&
        actual.All(pair => expected.TryGetValue(pair.Key, out string? hash) &&
            pair.Value.Equals(hash, StringComparison.OrdinalIgnoreCase));

    static string SafeCombine(string root, string relative)
    {
        ValidateRelativePath(relative);
        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(
            fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrWithin(candidate, fullRoot) ||
            candidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件相对路径越过目录边界：" + relative);
        return candidate;
    }

    static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathRooted(relative) ||
            relative.Contains('\\') ||
            relative.Contains(':') ||
            relative.Contains('\0'))
            throw new InvalidDataException("文件相对路径无效：" + relative);

        string[] segments = relative.Split('/');
        if (segments.Any(segment =>
            segment.Length == 0 || segment is "." or ".." ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("文件相对路径包含非法片段：" + relative);
    }

    static bool IsSameOrWithin(string candidate, string root)
    {
        string fullCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    void WriteJournal(ModUpdateApplyTransactionJournal journal)
    {
        files.CreateDirectory(journal.TransactionDirectory);
        string marker = Path.Combine(journal.TransactionDirectory, JournalFileName);
        string temporary = marker + ".tmp";
        files.WriteAllText(temporary, JsonSerializer.Serialize(journal, JournalJsonOptions));
        files.MoveFile(temporary, marker, overwrite: true);
    }

    void Report(string stage, string message, bool transactionWriting = false)
    {
        if (transactionWriting || stage is "Completed" or "RollbackCompleted" or "TransactionFinished")
        {
            try { deferWatcherQuietPeriod?.Invoke(); }
            catch { /* Watcher debounce must not interrupt a file transaction. */ }
        }

        try { reportProgress?.Invoke(new(stage, message, transactionWriting)); }
        catch { /* UI/progress callbacks must not interrupt a file transaction. */ }
    }

    static ModUpdateApplyResult Rejected(
        ModUpdateApplyFailureKind kind,
        string reason,
        string beforeVersion) =>
        Result(
            true, false, false, false, kind, reason,
            ModUpdateApplyRecoveryState.NotRequired,
            null, beforeVersion, null, cleanupCompleted: true);

    static ModUpdateApplyResult Cancelled(string beforeVersion) =>
        Result(
            false, false, false, true,
            ModUpdateApplyFailureKind.Cancelled,
            "操作已取消，目标 MOD 未进入写入阶段。",
            ModUpdateApplyRecoveryState.NotRequired,
            null, beforeVersion, null, cleanupCompleted: true);

    static ModUpdateApplyResult Result(
        bool confirmed,
        bool success,
        bool rolledBack,
        bool cancelled,
        ModUpdateApplyFailureKind failureKind,
        string? failureReason,
        ModUpdateApplyRecoveryState recoveryState,
        string? backupDirectory,
        string beforeVersion,
        string? afterVersion,
        bool cleanupCompleted,
        string? cleanupFailureReason = null,
        bool invalidateCaches = false) =>
        new(
            confirmed,
            success,
            rolledBack,
            cancelled,
            failureKind,
            failureReason,
            recoveryState,
            backupDirectory,
            beforeVersion,
            afterVersion,
            invalidateCaches,
            invalidateCaches,
            invalidateCaches,
            cleanupCompleted,
            cleanupFailureReason);

    sealed class PreCommitVerificationException(
        ModUpdateApplyFailureKind kind,
        string message) : Exception(message)
    {
        public ModUpdateApplyFailureKind Kind { get; } = kind;
    }
}
