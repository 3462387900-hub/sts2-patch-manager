using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

internal enum LocalZipPreviewState { Ready, Blocked, Failed, Cancelled }
internal enum LocalZipConflictKind
{
    SameIdEnabled, SameIdDisabled, SameIdDifferentDirectory, TargetDirectoryOccupied,
    TargetDirectoryDifferentId, TargetFileOccupied, ReservedDirectory, InventoryProblem,
    GameRunning
}
internal enum LocalZipSourceKind { UnverifiedLocalZip }

internal sealed record LocalZipDependency(string Id, string? Minimum, string? Maximum, bool Optional);
internal sealed record LocalZipPayloadFile(string RelativePath, long SizeBytes, string Sha256);
internal sealed record LocalZipConflict(LocalZipConflictKind Kind, string Detail, bool Blocks = true);
internal sealed record LocalZipSourceHints(
    string? GitHubRepository, string? WorkshopId, bool WorkshopBaselineExists,
    bool? GitHubCached = null, bool? WorkshopCached = null,
    IReadOnlyList<string>? ReadWarnings = null)
{
    public bool HasHistoricalBinding => GitHubRepository is not null || WorkshopId is not null;
    public bool HasHistoricalCache => GitHubCached == true || WorkshopCached == true;
    public bool HasHistoricalEvidence => HasHistoricalBinding || HasHistoricalCache || WorkshopBaselineExists;
    public string Warning => HasHistoricalEvidence
        ? "存在此 MOD ID 的历史 GitHub/Workshop 绑定、基线或检查缓存；本地 ZIP 未经远端验证，不得自动继承来源可信身份。"
        : "此 ZIP 是未验证的本地压缩包，不代表 GitHub/Steam 官方来源。";
    public string RuntimeCacheNotice => GitHubCached is null || WorkshopCached is null
        ? "运行时检查缓存未接入或无法确认；不得将未知状态当作无历史缓存。"
        : "已取得运行时 GitHub/Workshop 缓存状态。";
}
internal sealed record LocalZipPackage(
    string ZipPath, string ArchiveSha256, string WorkspaceDirectory,
    string PayloadDirectory, string ModId, string ModName, string Version,
    string Author, string Description, string ManifestPath, string PackageRootPath,
    string DestinationFolderName, string FolderNameReason,
    IReadOnlyList<LocalZipDependency> Dependencies,
    IReadOnlyList<LocalZipPayloadFile> Files, long TotalBytes);
internal sealed record LocalZipReadOnlyContext(
    IReadOnlyList<LocalZipConflict> Conflicts, LocalZipSourceHints Sources);
internal sealed record LocalModZipPreview(
    string GameRoot, LocalZipSourceKind SourceKind, string SourceTrust,
    LocalZipPackage Package, string DestinationDirectory,
    LocalZipSourceHints HistoricalSources, IReadOnlyList<LocalZipConflict> Conflicts,
    string Token, DateTimeOffset CreatedAt)
{
    public bool CanInstallNew => Conflicts.All(item => !item.Blocks);
}
internal sealed record LocalZipPreviewResult(
    LocalZipPreviewState State, LocalModZipPreview? Preview,
    string? Error = null, string? CleanupWarning = null);
internal sealed record LocalZipPreviewVerification(bool IsCurrent, string? Reason);

internal interface ILocalModZipImportBackend
{
    Task<LocalZipPackage> StageAsync(string zipPath, string workspaceDirectory, CancellationToken cancellationToken);
    LocalZipReadOnlyContext InspectReadOnly(string gameRoot, LocalZipPackage package, string dataRoot);
    Task<LocalZipPreviewVerification> VerifyAsync(LocalZipPackage package, CancellationToken cancellationToken);
    void Discard(string workspaceDirectory);
}

/// <summary>
/// Local ZIPs are untrusted inputs. This workflow may create only an isolated
/// manager-owned staging workspace. It never updates game trees or source bindings.
/// UI selection/confirmation and installation deliberately remain outside this batch.
/// </summary>
internal sealed class LocalModZipImportWorkflowService
{
    readonly ILocalModZipImportBackend backend;
    readonly string stagingRoot;
    readonly string dataRoot;
    readonly Func<string, bool> gameRunning;

    internal LocalModZipImportWorkflowService(
        string dataRoot,
        ILocalModZipImportBackend? backend = null,
        string? stagingRoot = null,
        Func<string, bool>? gameRunning = null)
    {
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.stagingRoot = Path.GetFullPath(stagingRoot ??
            Path.Combine(Path.GetTempPath(), "SpirePatch", "LocalZipStaging"));
        this.backend = backend ?? new LocalModZipArchiveBackend();
        this.gameRunning = gameRunning ?? GameEnvironment.Running;
    }

    public async Task<LocalZipPreviewResult> PreviewAsync(
        string gameRoot, string zipPath, CancellationToken cancellationToken = default)
    {
        string? workspace = null;
        bool retainWorkspace = false;
        LocalZipPreviewResult outcome;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = GameEnvironment.Root(gameRoot);
            string fullZip = Path.GetFullPath(zipPath);
            EnsureIsolated(root, fullZip);
            workspace = Path.Combine(stagingRoot, "localzip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            GameEnvironment.NoLinks(workspace);

            LocalZipPackage package = await backend.StageAsync(fullZip, workspace, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            LocalZipReadOnlyContext context = backend.InspectReadOnly(root, package, dataRoot);
            var conflicts = context.Conflicts.ToList();
            if (gameRunning(root))
                conflicts.Add(new(LocalZipConflictKind.GameRunning, "游戏正在运行，不能提交安装。"));
            foreach (string warning in context.Sources.ReadWarnings ?? Array.Empty<string>())
                conflicts.Add(new(LocalZipConflictKind.InventoryProblem, warning));

            var frozen = new LocalModZipPreview(
                root, LocalZipSourceKind.UnverifiedLocalZip, "Unverified",
                package, Path.Combine(root, "mods", package.DestinationFolderName),
                context.Sources, Array.AsReadOnly(conflicts.ToArray()),
                ComputeToken(root, package, context.Sources, conflicts),
                DateTimeOffset.UtcNow);
            retainWorkspace = true;
            outcome = new(frozen.CanInstallNew ? LocalZipPreviewState.Ready : LocalZipPreviewState.Blocked, frozen);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new(LocalZipPreviewState.Cancelled, null, "本地 ZIP 预览已取消。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or ArgumentException or
            System.Text.Json.JsonException)
        {
            outcome = new(LocalZipPreviewState.Failed, null, error.Message);
        }

        if (!retainWorkspace && workspace is not null)
        {
            try { Discard(workspace); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException)
            {
                outcome = outcome with { CleanupWarning = "暂存清理未完成，请保留现场核对：" + error.Message };
            }
        }
        return outcome;
    }

    /// <summary>
    /// Read-only validity check for a future apply workflow. Never executes a commit.
    /// Rehashes the original ZIP and every staged file and verifies the frozen token.
    /// </summary>
    public async Task<LocalZipPreviewVerification> VerifyAsync(
        LocalModZipPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureIsolated(preview.GameRoot, preview.Package.ZipPath);
            if (!preview.DestinationDirectory.Equals(
                Path.Combine(preview.GameRoot, "mods", preview.Package.DestinationFolderName),
                StringComparison.OrdinalIgnoreCase))
                return new(false, "预览目标目录与包信息不一致。");
            LocalZipReadOnlyContext current = backend.InspectReadOnly(
                preview.GameRoot, preview.Package, dataRoot);
            var conflicts = current.Conflicts.ToList();
            if (gameRunning(preview.GameRoot))
                conflicts.Add(new(LocalZipConflictKind.GameRunning, "游戏正在运行，不能提交安装。"));
            foreach (string warning in current.Sources.ReadWarnings ?? Array.Empty<string>())
                conflicts.Add(new(LocalZipConflictKind.InventoryProblem, warning));
            string token = ComputeToken(preview.GameRoot, preview.Package, current.Sources, conflicts);
            if (!string.Equals(token, preview.Token, StringComparison.Ordinal))
                return new(false, "游戏目录、冲突、历史绑定或预览内容已变化。");
            return await backend.VerifyAsync(preview.Package, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, "预览复核已取消。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, error.Message);
        }
    }

    public void Discard(LocalModZipPreview? preview)
    {
        if (preview is not null)
            Discard(preview.Package.WorkspaceDirectory);
    }

    public void Discard(string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        string full = Path.GetFullPath(workspaceDirectory);
        if (!string.Equals(Path.GetDirectoryName(full), stagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full),
                @"\Alocalzip-[0-9a-fA-F]{32}\z"))
            throw new InvalidOperationException("拒绝清理不属于本地 ZIP 预览的路径。");
        GameEnvironment.NoLinks(stagingRoot);
        backend.Discard(full);
    }

    void EnsureIsolated(string gameRoot, string zipPath)
    {
        string root = Path.GetFullPath(gameRoot);
        string stage = Path.GetFullPath(stagingRoot);
        if (Within(stage, root) || Within(root, stage) ||
            Within(stage, dataRoot) && Within(dataRoot, root))
            throw new InvalidOperationException("本地 ZIP 隔离暂存目录不能与游戏目录重合或相互包含。");
        if (Within(stage, zipPath))
            throw new InvalidOperationException("ZIP 原文件不能位于即将使用的暂存目录中。");
        GameEnvironment.NoLinks(stage);
        GameEnvironment.NoLinks(zipPath);
    }

    static bool Within(string path, string root)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static string ComputeToken(string root, LocalZipPackage p,
        LocalZipSourceHints hints, IReadOnlyList<LocalZipConflict> conflicts)
    {
        var builder = new StringBuilder();
        void Add(string? value) => builder.Append(value?.Length ?? -1).Append(':').Append(value).Append('\n');
        Add(root); Add(p.ZipPath); Add(p.ArchiveSha256); Add(p.WorkspaceDirectory);
        Add(p.PayloadDirectory); Add(p.ModId); Add(p.ModName); Add(p.Version);
        Add(p.Author); Add(p.Description); Add(p.ManifestPath); Add(p.PackageRootPath);
        Add(p.DestinationFolderName); Add(p.FolderNameReason); Add(p.TotalBytes.ToString());
        foreach (var d in p.Dependencies) { Add(d.Id); Add(d.Minimum); Add(d.Maximum); Add(d.Optional.ToString()); }
        foreach (var f in p.Files) { Add(f.RelativePath); Add(f.SizeBytes.ToString()); Add(f.Sha256); }
        Add(hints.GitHubRepository); Add(hints.WorkshopId);
        Add(hints.WorkshopBaselineExists.ToString()); Add(hints.GitHubCached?.ToString());
        Add(hints.WorkshopCached?.ToString());
        foreach (var c in conflicts) { Add(c.Kind.ToString()); Add(c.Detail); Add(c.Blocks.ToString()); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
