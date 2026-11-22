using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

/// <summary>
/// Read-only adapter from the existing source-check workflow to the local ZIP
/// preview cache probe. InspectRuntimeCache never starts a network operation.
/// </summary>
internal sealed class ModSourceWorkflowLocalZipCacheProbe
{
    readonly ModSourceCheckWorkflowService sourceChecks;

    public ModSourceWorkflowLocalZipCacheProbe(ModSourceCheckWorkflowService sourceChecks) =>
        this.sourceChecks = sourceChecks ?? throw new ArgumentNullException(nameof(sourceChecks));

    public (bool? GitHubCached, bool? WorkshopCached) Inspect(string modId) =>
        sourceChecks.InspectRuntimeCache(modId);
}

internal sealed record LocalZipConfirmationPresentation(
    string ModName,
    string ModId,
    string Version,
    string Author,
    string Description,
    int FileCount,
    long TotalBytes,
    string ZipPath,
    string ArchiveSha256,
    string TargetDirectory,
    string FolderNameReason,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> ConflictDetails,
    bool CanConfirm,
    bool HasHistoricalEvidence,
    IReadOnlyList<string> HistoricalEvidence,
    string RuntimeCacheNotice,
    IReadOnlyList<LocalZipHistoricalBindingPolicy> AvailablePolicies,
    string SourceWarning);

internal static class LocalZipConfirmationPresenter
{
    public static LocalZipConfirmationPresentation Create(LocalModZipPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        LocalZipPackage package = preview.Package;
        LocalZipSourceHints sources = preview.HistoricalSources;

        var dependencies = package.Dependencies.Select(item =>
        {
            string range = item.Minimum is null && item.Maximum is null
                ? ""
                : " [" + (item.Minimum ?? "任意") + " - " + (item.Maximum ?? "任意") + "]";
            return item.Id + range + (item.Optional ? "（可选）" : "");
        }).ToArray();

        var history = new List<string>
        {
            "GitHub 绑定：" + (sources.GitHubRepository ?? "无"),
            "Workshop ID：" + (sources.WorkshopId ?? "无"),
            "Workshop 版本基线：" + (sources.WorkshopBaselineExists ? "存在" : "不存在"),
            "GitHub 运行时检查缓存：" + CacheLabel(sources.GitHubCached),
            "Workshop 运行时检查缓存：" + CacheLabel(sources.WorkshopCached)
        };
        if (sources.ReadWarnings is { Count: > 0 })
            history.AddRange(sources.ReadWarnings.Select(item => "来源读取警告：" + item));

        return new(
            package.ModName,
            package.ModId,
            package.Version,
            package.Author,
            package.Description,
            package.Files.Count,
            package.TotalBytes,
            package.ZipPath,
            package.ArchiveSha256,
            preview.DestinationDirectory,
            package.FolderNameReason,
            Array.AsReadOnly(dependencies),
            Array.AsReadOnly(preview.Conflicts.Select(item =>
                (item.Blocks ? "阻断：" : "提示：") + item.Detail).ToArray()),
            preview.CanInstallNew,
            sources.HasHistoricalEvidence,
            Array.AsReadOnly(history.ToArray()),
            sources.RuntimeCacheNotice,
            Array.AsReadOnly(Enum.GetValues<LocalZipHistoricalBindingPolicy>()),
            "未验证本地 ZIP 来源：文件内容经过本地结构与哈希检查，但没有因此获得 GitHub、Steam 或作者身份认证。");
    }

    static string CacheLabel(bool? value) =>
        value switch { true => "存在", false => "不存在", _ => "未知（不得视为不存在）" };
}

internal sealed record LocalZipUiInvalidationPlan(
    bool RefreshInventory,
    bool InvalidateSourceCache,
    bool InvalidatePatchLifecycle)
{
    public static LocalZipUiInvalidationPlan ForApply(LocalZipApplyResult result) =>
        new(result.InventoryInvalidationRequired,
            result.SourceCacheInvalidationRequired,
            result.PatchLifecycleRecheckRequired);

    public static LocalZipUiInvalidationPlan ForRecovery(LocalZipInstallRecoveryReport report) =>
        new(report.InventoryInvalidationRequired,
            report.SourceCacheInvalidationRequired,
            report.PatchLifecycleRecheckRequired);
}

internal static class LocalZipUiResultPresenter
{
    public static string FormatApplyResult(LocalZipApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string target = string.IsNullOrWhiteSpace(result.TargetDirectory)
            ? ""
            : "目标目录：" + result.TargetDirectory + "。";

        if (result.Success)
        {
            if (result.PartialSuccess)
            {
                string issue = result.SourceBindingCleanupFailed
                    ? "MOD 已完成安装，但历史来源解绑持久化未能完整完成。"
                    : "MOD 已完成安装，但事务清理或来源处理仍需恢复。";
                return issue + target +
                    (string.IsNullOrWhiteSpace(result.CleanupFailureReason)
                        ? "" : "待处理原因：" + result.CleanupFailureReason + "。");
            }

            return "本地 ZIP MOD 全新安装成功。" + target +
                (result.SourceReviewRequired ? "历史来源仍标记为未验证，需人工复核。" : "");
        }

        if (result.Cancelled)
            return "本地 ZIP 安装已取消。" + target +
                (result.FailureReason ?? "目标目录未执行安装写入。");

        if (result.FailureKind == LocalZipApplyFailureKind.UserDeclined)
            return "已取消本次导入，未修改游戏目录。";

        if (result.FailureKind == LocalZipApplyFailureKind.RecoveryRequired)
            return "需要人工核对本地 ZIP 事务。" + target +
                (result.FailureReason ?? "事务现场已保留。") +
                "管理器不会在 UI 层自动删除或覆盖目标目录。";

        return "本地 ZIP 导入未完成（" + result.FailureKind + "）。" + target +
            (result.FailureReason ?? "未取得成功安装结果。") +
            (string.IsNullOrWhiteSpace(result.CleanupFailureReason)
                ? "" : "清理问题：" + result.CleanupFailureReason);
    }

    public static string? FormatRecoveryReport(
        LocalZipInstallRecoveryReport report, string transactionLogDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.HasPendingRecovery)
        {
            if (report.CompletedCount > 0 || report.CleanedCount > 0)
                return $"事务恢复检查完成：安全完成 {report.CompletedCount} 项，清理未提交事务 {report.CleanedCount} 项。";
            return null;
        }

        var lines = new List<string>
        {
            $"发现 {report.PendingCount} 项待人工核对或待重试的本地 ZIP 事务："
        };
        foreach (LocalZipInstallRecoveryItem item in report.Items.Where(item => item.Pending))
        {
            string logName = Path.HasExtension(item.TransactionId)
                ? item.TransactionId
                : item.TransactionId + ".json";
            string logPath = Path.Combine(transactionLogDirectory, logName);
            lines.Add(
                "事务：" + item.TransactionId +
                "；状态：" + item.State +
                (string.IsNullOrWhiteSpace(item.ModId) ? "" : "；MOD ID：" + item.ModId) +
                "；目标目录：" + (item.TargetDirectory ?? "日志中无法确认") +
                "；事务日志：" + logPath +
                (string.IsNullOrWhiteSpace(item.Error) ? "" : "；原因：" + item.Error));
        }

        lines.Add("目标目录与事务日志均已保留。请人工核对；管理器不会自动删除或覆盖目标目录。");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Owns the UI lifetime of one local ZIP preview. It is intentionally WPF-free so
/// discard, apply and recovery boundaries can be exercised with isolated fixtures.
/// </summary>
internal sealed class LocalZipUiWorkflowSession
{
    readonly LocalModZipImportWorkflowService previewWorkflow;
    readonly LocalModZipApplyWorkflowService applyWorkflow;
    LocalModZipPreview? currentPreview;

    public LocalZipUiWorkflowSession(
        LocalModZipImportWorkflowService previewWorkflow,
        string dataRoot,
        ILocalModZipInstallTransactionBackend? transactionBackend = null)
    {
        this.previewWorkflow = previewWorkflow ??
            throw new ArgumentNullException(nameof(previewWorkflow));
        applyWorkflow = new LocalModZipApplyWorkflowService(
            previewWorkflow, dataRoot, transactionBackend);
    }

    public LocalModZipPreview? CurrentPreview => currentPreview;
    public string TransactionLogDirectory => applyWorkflow.TransactionLogDirectory;

    public async Task<LocalZipPreviewResult> PreviewAsync(
        string gameRoot, string zipPath, CancellationToken cancellationToken = default)
    {
        DiscardPreview();
        LocalZipPreviewResult result = await previewWorkflow.PreviewAsync(
            gameRoot, zipPath, cancellationToken);
        currentPreview = result.Preview;
        return result;
    }

    public void ChangeRoot(string gameRoot)
    {
        if (currentPreview is null) return;
        string normalized = Path.GetFullPath(gameRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!normalized.Equals(currentPreview.GameRoot, StringComparison.OrdinalIgnoreCase))
            DiscardPreview();
    }

    public void DiscardPreview()
    {
        if (currentPreview is null) return;
        previewWorkflow.Discard(currentPreview);
        currentPreview = null;
    }

    public async Task<LocalZipApplyResult> ApplyAsync(
        Func<LocalZipApplyConfirmation, CancellationToken, Task<LocalZipApplyDecision>> confirm,
        Action deferQuietPeriod,
        Action<LocalZipApplyProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        LocalModZipPreview preview = currentPreview ??
            throw new InvalidOperationException("当前没有可提交的本地 ZIP 预览。");
        LocalZipApplyResult result = await applyWorkflow.ApplyAsync(
            preview, confirm, deferQuietPeriod, reportProgress, cancellationToken);

        // A transaction ID means the transaction service owns any retained recovery
        // evidence. Do not let the UI discard its workspace after a commit/recovery fault.
        if (result.Success || result.TransactionId is not null)
        {
            currentPreview = null;
        }
        else if (result.RecoveryState != LocalZipInstallRecoveryState.PendingRecovery)
        {
            DiscardPreview();
        }

        return result;
    }

    public LocalZipInstallRecoveryReport RecoverPendingTransactions(
        string? gameRoot = null,
        Action? deferQuietPeriod = null,
        Action<LocalZipApplyProgress>? reportProgress = null) =>
        applyWorkflow.RecoverPendingTransactions(gameRoot, deferQuietPeriod, reportProgress);
}
