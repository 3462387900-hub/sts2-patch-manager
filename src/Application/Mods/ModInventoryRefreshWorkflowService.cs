using System.IO;

namespace SpirePatch;

internal sealed record ModInventoryRefreshStamp(
    string? Root,
    int ChangeVersion,
    bool InventoryDirty);

internal sealed record ModInventoryRefreshProgress(
    string Status,
    bool TransactionWriting = false);

internal sealed record ModInventoryRefreshCheckpoint(
    ModInventory Inventory,
    string Status,
    int ChangeVersion,
    bool ClearDiscoveryCache);

internal sealed record ModInventoryRefreshConfirmation(
    string Root,
    int ChangeVersion,
    IReadOnlyList<OrphanPatchCandidate> Candidates);

internal sealed record ModInventoryRefreshResult(
    bool ScanSucceeded,
    bool Cancelled,
    bool Stale,
    ModInventory? Inventory,
    bool InventoryDirty,
    string Status,
    string? OrphanPatchNotice,
    string? RecoveryNotice,
    bool ClearLifecycleState,
    string? LifecycleInvalidationReason);

internal interface IModInventoryRefreshBackend
{
    ModInventory Scan(string root);
    string? TakeRecoveryNotice();
    bool AutoStopEnabled(string dataRoot);
    OrphanPatchCandidate[] Observe(
        string root, string dataRoot, ModInventory inventory, bool enabled);
    OrphanPatchOutcome Reconcile(string root, string dataRoot);
}

internal sealed class DomainModInventoryRefreshBackend : IModInventoryRefreshBackend
{
    public ModInventory Scan(string root) => ModManager.Scan(root);
    public string? TakeRecoveryNotice() => ModEnvironmentSnapshots.TakeRecoveryNotice();
    public bool AutoStopEnabled(string dataRoot) => OrphanPatchAutoStop.Enabled(dataRoot);
    public OrphanPatchCandidate[] Observe(
        string root, string dataRoot, ModInventory inventory, bool enabled) =>
        OrphanPatchAutoStop.Observe(root, dataRoot, inventory, enabled);
    public OrphanPatchOutcome Reconcile(string root, string dataRoot) =>
        OrphanPatchAutoStop.Reconcile(root, dataRoot);
}

/// <summary>
/// Coordinates a stable MOD inventory refresh and the existing orphan-patch
/// auto-stop workflow. Scanning and file operations stay in Domain; UI decisions
/// are supplied as delegates and returned as plain result/progress models.
/// </summary>
internal sealed class ModInventoryRefreshWorkflowService
{
    readonly string dataRoot;
    readonly IModInventoryRefreshBackend backend;
    readonly TimeSpan orphanPatchConfirmationDelay;
    readonly object gate = new();
    CancellationTokenSource? activeRefresh;
    long generation;
    bool transactionWriting;

    public ModInventoryRefreshWorkflowService(
        string dataRoot,
        IModInventoryRefreshBackend? backend = null,
        TimeSpan? orphanPatchConfirmationDelay = null)
    {
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.backend = backend ?? new DomainModInventoryRefreshBackend();
        this.orphanPatchConfirmationDelay =
            orphanPatchConfirmationDelay ?? TimeSpan.FromMilliseconds(3500);
        if (this.orphanPatchConfirmationDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(orphanPatchConfirmationDelay));
    }

    /// <summary>
    /// Cancels a pending scan or confirmation delay. A file transaction that has
    /// already crossed the write boundary is allowed to finish safely.
    /// </summary>
    public void Cancel()
    {
        lock (gate)
        {
            if (!transactionWriting)
                activeRefresh?.Cancel();
        }
    }

    public async Task<ModInventoryRefreshResult> RefreshAsync(
        string root,
        ModInventoryRefreshStamp initialStamp,
        Func<ModInventoryRefreshStamp> readCurrentStamp,
        Func<ModInventoryRefreshConfirmation, CancellationToken, Task<bool>> confirmBeforeStop,
        Action deferQuietPeriod,
        Action<ModInventoryRefreshCheckpoint>? inventoryReady = null,
        Action<ModInventoryRefreshProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        root = NormalizeRoot(root);
        CancellationTokenSource linked;
        long requestGeneration;

        lock (gate)
        {
            if (transactionWriting)
            {
                return new(false, false, false, null, true,
                    "正在安全停用孤儿补丁，请等待文件操作结束。", null, null, false, null);
            }

            activeRefresh?.Cancel();
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeRefresh = linked;
            requestGeneration = ++generation;
        }

        bool IsOwner()
        {
            lock (gate)
                return generation == requestGeneration &&
                    ReferenceEquals(activeRefresh, linked);
        }

        bool IsCurrent()
        {
            if (!IsOwner() || linked.IsCancellationRequested)
                return false;

            var current = readCurrentStamp();
            return SameRoot(current.Root, root) &&
                current.ChangeVersion == initialStamp.ChangeVersion;
        }

        void DeferIfStillWatchingThisRoot()
        {
            try
            {
                if (SameRoot(readCurrentStamp().Root, root))
                    deferQuietPeriod();
            }
            catch
            {
                // A failed UI state read must not turn a stale scan into a write.
            }
        }

        void Report(string status, bool writing = false)
        {
            try { reportProgress?.Invoke(new(status, writing)); }
            catch { /* UI progress must not interrupt a filesystem workflow. */ }
        }

        ModInventory? inventory = null;
        string status = "正在读取模组清单…";
        string? orphanNotice = null;
        string? recoveryNotice = null;
        bool clearLifecycleState = false;
        string? lifecycleInvalidationReason = null;
        bool checkpointPublished = false;

        try
        {
            if (!SameRoot(initialStamp.Root, root) || !IsCurrent())
            {
                DeferIfStillWatchingThisRoot();
                return new(false, false, true, null, true,
                    "扫描期间目录或文件状态发生变化，正在准备重新刷新。",
                    null, null, false, null);
            }

            Report(status);
            inventory = await Task.Run(() => backend.Scan(root), linked.Token);
            linked.Token.ThrowIfCancellationRequested();

            if (!IsCurrent())
            {
                DeferIfStillWatchingThisRoot();
                return new(false, false, true, null, true,
                    "扫描期间目录发生变化，正在准备重新刷新。",
                    null, null, false, null);
            }

            recoveryNotice = backend.TakeRecoveryNotice();
            status = InventoryStatus(inventory);
            inventoryReady?.Invoke(new(
                inventory, status, initialStamp.ChangeVersion,
                initialStamp.InventoryDirty));
            checkpointPublished = true;

            // Only a complete, stable inventory is eligible for orphan-patch automation.
            if (inventory.Issues.Count == 0)
            {
                try
                {
                    bool enabled = backend.AutoStopEnabled(dataRoot);
                    var candidates = await Task.Run(
                        () => backend.Observe(root, dataRoot, inventory, enabled),
                        linked.Token);
                    linked.Token.ThrowIfCancellationRequested();

                    if (!IsCurrent())
                    {
                        DeferIfStillWatchingThisRoot();
                        return new(true, false, true, inventory, true,
                            "扫描期间 MOD 环境再次发生变化，已取消自动停用并安排复查。",
                            orphanNotice, recoveryNotice, false, null);
                    }

                    if (enabled && candidates.Length > 0)
                    {
                        status = "发现可能失去对应 MOD 的独立修复补丁，等待文件操作结束并再次校验…";
                        Report(status);
                        await Task.Delay(orphanPatchConfirmationDelay, linked.Token);
                        linked.Token.ThrowIfCancellationRequested();

                        if (!IsCurrent())
                        {
                            DeferIfStillWatchingThisRoot();
                            return new(true, false, true, inventory, true,
                                "扫描期间 MOD 环境再次发生变化，已取消自动停用并安排复查。",
                                orphanNotice, recoveryNotice, false, null);
                        }

                        // This is the UI boundary for the original silent second check:
                        // the UI revalidates the opt-in setting, selected root and watcher version.
                        bool confirmed = await confirmBeforeStop(
                            new(root, initialStamp.ChangeVersion, candidates), linked.Token);
                        linked.Token.ThrowIfCancellationRequested();

                        if (!IsCurrent())
                        {
                            DeferIfStillWatchingThisRoot();
                            return new(true, false, true, inventory, true,
                                "扫描期间 MOD 环境再次发生变化，已取消自动停用并安排复查。",
                                orphanNotice, recoveryNotice, false, null);
                        }

                        if (confirmed)
                        {
                            lock (gate)
                            {
                                if (generation != requestGeneration ||
                                    !ReferenceEquals(activeRefresh, linked) ||
                                    linked.IsCancellationRequested)
                                    throw new OperationCanceledException(linked.Token);
                                transactionWriting = true;
                            }

                            Report("正在安全停用失去目标 MOD 的独立修复补丁…", writing: true);
                            OrphanPatchOutcome outcome;
                            try
                            {
                                // Once the write boundary is crossed, do not cancel the
                                // Domain transaction halfway through moving patch files.
                                outcome = await Task.Run(() => backend.Reconcile(root, dataRoot));
                            }
                            finally
                            {
                                lock (gate)
                                {
                                    if (generation == requestGeneration &&
                                        ReferenceEquals(activeRefresh, linked))
                                        transactionWriting = false;
                                }
                                Report(status, writing: false);
                            }

                            inventory = outcome.Inventory;
                            status = InventoryStatus(inventory);

                            if (outcome.Disabled.Length > 0)
                            {
                                orphanNotice = "已自动停用 " + outcome.Disabled.Length +
                                    " 个失去目标 MOD 的修复补丁：" +
                                    string.Join("、", outcome.Disabled.Take(3)) +
                                    (outcome.Disabled.Length > 3 ? " 等" : "") +
                                    "。补丁文件完整保留，可在修复补丁中重新启用。";
                                lifecycleInvalidationReason =
                                    "目标 MOD 已移除，相关修复补丁已安全停用，请重新扫描。";
                                clearLifecycleState = true;
                            }

                            if (outcome.Skipped.Length > 0)
                            {
                                orphanNotice = "部分修复补丁未能自动停用：" +
                                    string.Join("；", outcome.Skipped.Take(2)) +
                                    "。未改变这些补丁的文件，请退出游戏或检查 MOD 目录后再刷新。";
                            }

                            if (!IsCurrent())
                            {
                                DeferIfStillWatchingThisRoot();
                                return new(true, false, true, inventory, true,
                                    "自动停用已结束，但 MOD 环境随后再次发生变化，已安排重新扫描。",
                                    orphanNotice, recoveryNotice, clearLifecycleState,
                                    lifecycleInvalidationReason);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    orphanNotice = "孤立补丁自动检查未完成：" + error.Message +
                        "。不会未经核对移动补丁文件。";
                }
            }

            if (!IsCurrent())
            {
                DeferIfStillWatchingThisRoot();
                return new(true, false, true, inventory, true,
                    "扫描期间 MOD 环境发生变化，已安排重新刷新。",
                    orphanNotice, recoveryNotice, clearLifecycleState,
                    lifecycleInvalidationReason);
            }

            return new(true, false, false, inventory, false, status,
                orphanNotice, recoveryNotice, clearLifecycleState,
                lifecycleInvalidationReason);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested || !IsOwner())
        {
            return new(false, true, false, checkpointPublished ? inventory : null, true,
                status, orphanNotice, recoveryNotice, clearLifecycleState,
                lifecycleInvalidationReason);
        }
        catch (Exception error)
        {
            return new(false, false, false, checkpointPublished ? inventory : null, true,
                "无法读取模组：" + error.Message, orphanNotice, recoveryNotice,
                clearLifecycleState, lifecycleInvalidationReason);
        }
        finally
        {
            lock (gate)
            {
                if (generation == requestGeneration &&
                    ReferenceEquals(activeRefresh, linked))
                {
                    transactionWriting = false;
                    activeRefresh = null;
                }
            }
            linked.Dispose();
        }
    }

    static string InventoryStatus(ModInventory inventory) =>
        $"共 {inventory.Mods.Count} 项 · {inventory.Mods.Count(mod => mod.Enabled)} 项启用 · {inventory.Mods.Count(mod => !mod.Enabled)} 项停用";

    static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    static bool SameRoot(string? left, string right) =>
        left is not null && NormalizeRoot(left).Equals(
            NormalizeRoot(right), StringComparison.OrdinalIgnoreCase);
}
