using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

internal enum ModUpdateStagingSource
{
    GitHub,
    Workshop
}

internal enum ModUpdateStagingState
{
    Succeeded,
    Failed,
    Cancelled
}

internal enum ModUpdateStagingFailureKind
{
    None,
    InvalidRequest,
    TargetUnavailable,
    TargetChanged,
    DownloadFailed,
    InvalidArchive,
    UnsafeArchivePath,
    MissingManifest,
    InvalidManifest,
    ManifestMismatch,
    VersionMismatch,
    InvalidPackageStructure,
    HashMismatch,
    IntegrityCheckFailed,
    Cancelled,
    Io,
    Unknown
}

internal enum ModUpdateFileChangeKind
{
    Added,
    Modified,
    Deleted
}

internal sealed record ModUpdateStagingRequest(
    ModUpdateStagingSource Source,
    string SourceIdentifier,
    string? ReleaseTag = null,
    string? FileName = null,
    long? ExpectedArchiveSize = null,
    string? ExpectedArchiveSha256 = null,
    string? DownloadUrl = null,
    string? ReleaseUrl = null,
    string? ExpectedWorkshopManifestId = null);

internal sealed record ModUpdateDownloadedFileEvidence(
    string RelativePath,
    long SizeBytes,
    string Sha1,
    bool Sha1Verified);

internal sealed record ModUpdateDownloadedArtifact(
    string? ArchivePath = null,
    string? PayloadDirectory = null,
    string? SourceManifestId = null,
    ulong? WorkshopId = null,
    bool UpstreamIntegrityVerified = false,
    IReadOnlyList<ModUpdateDownloadedFileEvidence>? Files = null);

internal interface IModUpdateStagingBackend
{
    Task<ModUpdateDownloadedArtifact> DownloadAsync(
        ModUpdateStagingRequest request,
        string workspaceDirectory,
        Action<string>? progress,
        CancellationToken cancellationToken);
}

internal sealed record ModUpdateStagingProgress(
    string Stage,
    string Message,
    int Completed = 0,
    int? Total = null);

internal sealed record ModUpdateFileChange(
    string RelativePath,
    ModUpdateFileChangeKind Kind,
    string? CurrentSha256,
    string? StagedSha256,
    long? CurrentSizeBytes,
    long? StagedSizeBytes);

internal sealed record ModUpdateStagingResult(
    ModUpdateStagingState State,
    ModUpdateStagingSource Source,
    string ModId,
    string SourceIdentifier,
    string TargetFolder,
    string? StagingDirectory,
    string? StagedPayloadDirectory,
    string LocalVersion,
    string? StagedVersion,
    bool VersionChanged,
    string? SourceManifestId,
    bool IntegrityVerified,
    IReadOnlyList<ModUpdateFileChange> FileChanges,
    int AddedCount,
    int ModifiedCount,
    int DeletedCount,
    ModUpdateStagingFailureKind FailureKind,
    string? FailureReason,
    string? CleanupFailureReason,
    DateTimeOffset CompletedAt)
{
    internal bool ValidationPassed => State == ModUpdateStagingState.Succeeded;
}

internal sealed class ModUpdateStagingWorkflowService
{
    const long MaxArchiveBytes = 120L * 1024 * 1024;
    const long MaxExtractedBytes = 300L * 1024 * 1024;
    const int MaxArchiveEntries = 800;
    const int MaxManifestBytes = 128 * 1024;
    const int MaxWorkshopReportBytes = 8 * 1024 * 1024;

    static readonly Regex RepositoryPattern = new(
        @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*\z",
        RegexOptions.Compiled);
    static readonly Regex ArchiveNamePattern = new(
        @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,180}\.zip\z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Sha256Pattern = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.Compiled);
    static readonly Regex ReservedNamePattern = new(
        @"\A(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly IModUpdateStagingBackend backend;
    readonly string stagingRoot;
    readonly Func<DateTimeOffset> utcNow;

    sealed record ManifestInfo(string Id, string Version, bool HasDll, bool HasPck);

    sealed record FileSnapshot(string Sha256, long SizeBytes);

    sealed record ValidatedPayload(string Directory, ManifestInfo Manifest);

    sealed class StagingFailureException(
        ModUpdateStagingFailureKind kind,
        string message,
        Exception? inner = null) : Exception(message, inner)
    {
        public ModUpdateStagingFailureKind Kind { get; } = kind;
    }

    public ModUpdateStagingWorkflowService(
        IModUpdateStagingBackend backend,
        string? stagingRoot = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.stagingRoot = Path.GetFullPath(stagingRoot ??
            Path.Combine(Path.GetTempPath(), "SpirePatch", "ModUpdateStaging"));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ModUpdateStagingResult> StageAndPreviewAsync(
        string gameRoot,
        ManagedMod target,
        ModUpdateStagingRequest request,
        CancellationToken cancellationToken = default,
        Action<ModUpdateStagingProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        string targetFolder = target.Folder;
        string? workspace = null;
        bool keepWorkspace = false;
        ModUpdateStagingResult? outcome = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRequest(request);

            string normalizedGameRoot = Path.GetFullPath(gameRoot);
            string normalizedTarget = Path.GetFullPath(target.Folder);
            string modsRoot = Path.GetFullPath(Path.Combine(normalizedGameRoot, "mods"));
            targetFolder = normalizedTarget;

            ValidateTarget(normalizedGameRoot, modsRoot, normalizedTarget, target);
            if (IsSameOrWithin(stagingRoot, normalizedGameRoot) ||
                IsSameOrWithin(normalizedGameRoot, stagingRoot))
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.InvalidRequest,
                    "隔离暂存目录必须与游戏目录完全分离。");
            }

            Report(progress, "Validate", "正在只读核对当前 MOD 清单与文件…");
            ManifestInfo currentManifest = ReadManifest(target.Manifest, isTarget: true);
            if (!currentManifest.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase) ||
                !currentManifest.Version.Equals(target.Version, StringComparison.Ordinal))
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.TargetChanged,
                    "当前 MOD 清单的 ID 或版本已变化，请刷新列表后重新预览。");
            }

            Dictionary<string, FileSnapshot> currentFiles =
                SnapshotDirectory(normalizedTarget, excludeWorkshopReport: false, cancellationToken);

            EnsureNoReparseAncestors(stagingRoot, ModUpdateStagingFailureKind.InvalidRequest);
            Directory.CreateDirectory(stagingRoot);
            workspace = Path.Combine(stagingRoot, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            EnsureNoReparsePoint(workspace, ModUpdateStagingFailureKind.InvalidRequest);

            Report(progress, "Download", "正在将更新内容下载到独立暂存目录…");
            ModUpdateDownloadedArtifact artifact = await backend.DownloadAsync(
                request,
                workspace,
                message => Report(progress, "Download", message),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            string payloadRoot;
            string? sourceManifestId = artifact.SourceManifestId;
            if (request.Source == ModUpdateStagingSource.GitHub)
            {
                string archiveHash = await VerifyGitHubArchiveAsync(
                    request, artifact, workspace, cancellationToken);
                Report(progress, "Verify", "下载包 SHA-256 已通过，正在安全检查 ZIP 结构…");
                string extractionRoot = Path.Combine(workspace, "extracted");
                Directory.CreateDirectory(extractionRoot);
                await ExtractGitHubArchiveAsync(
                    artifact.ArchivePath!,
                    extractionRoot,
                    cancellationToken,
                    progress);
                payloadRoot = extractionRoot;
                _ = archiveHash;
            }
            else
            {
                payloadRoot = VerifyWorkshopArtifact(request, artifact, workspace, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, "Validate", "正在核对 MOD 清单、版本和文件结构…");
            ValidatedPayload validated = ValidatePayload(
                payloadRoot, request, target.Id, cancellationToken);
            Dictionary<string, FileSnapshot> stagedFiles =
                SnapshotDirectory(validated.Directory, request.Source == ModUpdateStagingSource.Workshop,
                    cancellationToken);
            IReadOnlyList<ModUpdateFileChange> changes = Compare(currentFiles, stagedFiles);

            int added = changes.Count(item => item.Kind == ModUpdateFileChangeKind.Added);
            int modified = changes.Count(item => item.Kind == ModUpdateFileChangeKind.Modified);
            int deleted = changes.Count(item => item.Kind == ModUpdateFileChangeKind.Deleted);

            keepWorkspace = true;
            Report(progress, "Preview", "暂存、完整性校验和只读变更预览已完成。");
            outcome = new ModUpdateStagingResult(
                ModUpdateStagingState.Succeeded,
                request.Source,
                target.Id,
                request.SourceIdentifier,
                normalizedTarget,
                workspace,
                validated.Directory,
                currentManifest.Version,
                validated.Manifest.Version,
                !currentManifest.Version.Equals(validated.Manifest.Version, StringComparison.OrdinalIgnoreCase),
                sourceManifestId,
                true,
                changes,
                added,
                modified,
                deleted,
                ModUpdateStagingFailureKind.None,
                null,
                null,
                utcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = Failure(
                target, request, targetFolder,
                ModUpdateStagingState.Cancelled,
                ModUpdateStagingFailureKind.Cancelled,
                "暂存操作已取消。");
        }
        catch (StagingFailureException error)
        {
            outcome = Failure(
                target, request, targetFolder,
                ModUpdateStagingState.Failed, error.Kind, error.Message);
        }
        catch (HttpRequestException error)
        {
            outcome = Failure(
                target, request, targetFolder,
                ModUpdateStagingState.Failed,
                ModUpdateStagingFailureKind.DownloadFailed,
                error.Message);
        }
        catch (TimeoutException error)
        {
            outcome = Failure(
                target, request, targetFolder,
                ModUpdateStagingState.Failed,
                ModUpdateStagingFailureKind.DownloadFailed,
                error.Message);
        }
        catch (JsonException error)
        {
            outcome = Failure(
                target, request, targetFolder,
                ModUpdateStagingState.Failed,
                ModUpdateStagingFailureKind.InvalidManifest,
                "清单 JSON 无法解析：" + error.Message);
        }
        catch (InvalidDataException error)
        {
            outcome = Failure(
                target, request, targetFolder,
                ModUpdateStagingState.Failed,
                ModUpdateStagingFailureKind.InvalidPackageStructure,
                error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            outcome = Failure(
                target, request, targetFolder,
                ModUpdateStagingState.Failed,
                ModUpdateStagingFailureKind.Io,
                error.Message);
        }
        catch (Exception error)
        {
            outcome = Failure(
                target, request, targetFolder,
                ModUpdateStagingState.Failed,
                ModUpdateStagingFailureKind.Unknown,
                error.Message);
        }
        finally
        {
            if (!keepWorkspace && workspace is not null)
            {
                try
                {
                    DeleteTreeWithoutFollowingLinks(workspace);
                }
                catch (Exception cleanupError)
                {
                    if (outcome is not null)
                    {
                        outcome = outcome with
                        {
                            StagingDirectory = Directory.Exists(workspace) ? workspace : null,
                            CleanupFailureReason = cleanupError.Message
                        };
                    }
                }
            }
        }

        return outcome!;
    }

    public void Discard(string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        string full = Path.GetFullPath(stagingDirectory);
        if (!string.Equals(Path.GetDirectoryName(full), stagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), @"\Astage-[0-9a-fA-F]{32}\z"))
        {
            throw new InvalidOperationException("只能清理本服务创建的隔离暂存目录。");
        }

        EnsureNoReparseAncestors(stagingRoot, ModUpdateStagingFailureKind.InvalidRequest);
        if (Directory.Exists(full) || File.Exists(full))
            DeleteTreeWithoutFollowingLinks(full);
    }

    static ModUpdateStagingResult Failure(
        ManagedMod target,
        ModUpdateStagingRequest request,
        string targetFolder,
        ModUpdateStagingState state,
        ModUpdateStagingFailureKind kind,
        string reason) =>
        new(
            state,
            request.Source,
            target.Id,
            request.SourceIdentifier,
            targetFolder,
            null,
            null,
            target.Version,
            null,
            false,
            null,
            false,
            Array.Empty<ModUpdateFileChange>(),
            0,
            0,
            0,
            kind,
            reason,
            null,
            DateTimeOffset.UtcNow);

    static void ValidateRequest(ModUpdateStagingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceIdentifier))
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.InvalidRequest, "更新来源标识不能为空。");

        if (request.Source == ModUpdateStagingSource.GitHub)
        {
            if (!RepositoryPattern.IsMatch(request.SourceIdentifier) ||
                request.ReleaseTag is null ||
                !Version.TryParse(request.ReleaseTag.TrimStart('v', 'V'), out _) ||
                request.FileName is null ||
                !ArchiveNamePattern.IsMatch(request.FileName) ||
                request.ExpectedArchiveSize is not long size ||
                size <= 0 || size > MaxArchiveBytes ||
                request.ExpectedArchiveSha256 is null ||
                !Sha256Pattern.IsMatch(request.ExpectedArchiveSha256) ||
                request.DownloadUrl is null ||
                request.ReleaseUrl is null)
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.InvalidRequest,
                    "GitHub 更新包缺少有效的仓库、版本、文件名、大小、SHA-256 或发布链接信息。");
            }

            string officialDownload =
                "https://github.com/" + request.SourceIdentifier + "/releases/download/" +
                Uri.EscapeDataString(request.ReleaseTag) + "/" + Uri.EscapeDataString(request.FileName);
            string officialRelease =
                "https://github.com/" + request.SourceIdentifier + "/releases/tag/" + request.ReleaseTag;
            if (!request.DownloadUrl.Equals(officialDownload, StringComparison.Ordinal) ||
                !request.ReleaseUrl.Equals(officialRelease, StringComparison.Ordinal))
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.InvalidRequest,
                    "GitHub 下载链接与发布来源标识不一致。");
            }

            return;
        }

        if (request.Source == ModUpdateStagingSource.Workshop)
        {
            if (!ulong.TryParse(request.SourceIdentifier, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong workshopId) ||
                workshopId == 0 ||
                string.IsNullOrWhiteSpace(request.ExpectedWorkshopManifestId) ||
                !ulong.TryParse(request.ExpectedWorkshopManifestId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong manifestId) ||
                manifestId == 0)
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.InvalidRequest,
                    "Workshop 更新请求缺少有效的 Workshop ID 或预期 Manifest ID。");
            }

            return;
        }

        throw new StagingFailureException(
            ModUpdateStagingFailureKind.InvalidRequest, "不支持的更新来源类型。");
    }

    static void ValidateTarget(
        string gameRoot,
        string modsRoot,
        string targetFolder,
        ManagedMod target)
    {
        if (!target.Enabled || target.Storage is not null)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.TargetUnavailable,
                "只允许预览已启用且位于活动 mods 目录中的普通 MOD。");

        if (!Directory.Exists(gameRoot) || !Directory.Exists(modsRoot) ||
            !Directory.Exists(targetFolder))
        {
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.TargetUnavailable,
                "游戏目录、mods 目录或目标 MOD 目录不存在。");
        }

        if (!Path.GetDirectoryName(targetFolder)!.Equals(modsRoot, StringComparison.OrdinalIgnoreCase))
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.TargetUnavailable,
                "目标 MOD 必须是游戏 mods 目录下的独立子目录。");

        string manifest = Path.GetFullPath(target.Manifest);
        if (!Path.GetDirectoryName(manifest)!.Equals(targetFolder, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(manifest))
        {
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.TargetUnavailable,
                "目标 MOD 清单不在其独立目录内，无法安全预览。");
        }

        EnsureNoReparseAncestors(gameRoot, ModUpdateStagingFailureKind.TargetUnavailable);
        EnsureNoReparseAncestors(targetFolder, ModUpdateStagingFailureKind.TargetUnavailable);
        EnsureNoReparsePoint(manifest, ModUpdateStagingFailureKind.TargetUnavailable);
    }

    static ManifestInfo ReadManifest(string path, bool isTarget)
    {
        ModUpdateStagingFailureKind failureKind = isTarget
            ? ModUpdateStagingFailureKind.TargetUnavailable
            : ModUpdateStagingFailureKind.InvalidManifest;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxManifestBytes)
                throw new StagingFailureException(failureKind, "MOD 清单为空、缺失或超过大小限制。");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new StagingFailureException(failureKind, "MOD 清单必须是 JSON 对象。");

            var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                    throw new StagingFailureException(failureKind, "MOD 清单包含重复字段：" + property.Name);
            }

            string id = ReadRequiredString(properties, "id", failureKind);
            string version = ReadRequiredString(properties, "version", failureKind);
            bool hasDll = ReadOptionalBoolean(properties, "has_dll", failureKind);
            bool hasPck = ReadOptionalBoolean(properties, "has_pck", failureKind);
            if (!properties.ContainsKey("has_dll") && !properties.ContainsKey("has_pck"))
                throw new StagingFailureException(failureKind, "MOD 清单没有声明 has_dll 或 has_pck。");
            if (!hasDll && !hasPck)
                throw new StagingFailureException(failureKind, "MOD 清单没有声明任何可加载内容。");

            if (properties.TryGetValue("dependencies", out JsonElement dependencies))
            {
                if (dependencies.ValueKind != JsonValueKind.Array)
                    throw new StagingFailureException(failureKind, "MOD 依赖清单必须是数组。");

                foreach (JsonElement dependency in dependencies.EnumerateArray())
                {
                    if (dependency.ValueKind == JsonValueKind.String)
                    {
                        if (string.IsNullOrWhiteSpace(dependency.GetString()))
                            throw new StagingFailureException(failureKind, "MOD 依赖 ID 不能为空。");
                        continue;
                    }

                    if (dependency.ValueKind != JsonValueKind.Object)
                        throw new StagingFailureException(failureKind, "MOD 依赖项格式无效。");

                    var dependencyProperties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty property in dependency.EnumerateObject())
                    {
                        if (!dependencyProperties.TryAdd(property.Name, property.Value))
                            throw new StagingFailureException(failureKind, "MOD 依赖项包含重复字段。");
                    }

                    _ = ReadRequiredString(dependencyProperties, "id", failureKind);
                    if (dependencyProperties.TryGetValue("optional", out JsonElement optional) &&
                        optional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new StagingFailureException(failureKind, "MOD 依赖项 optional 字段必须是布尔值。");
                }
            }

            return new ManifestInfo(id, version, hasDll, hasPck);
        }
        catch (StagingFailureException)
        {
            throw;
        }
        catch (JsonException error)
        {
            throw new StagingFailureException(failureKind, "MOD 清单 JSON 无法解析。", error);
        }
        catch (IOException error)
        {
            throw new StagingFailureException(failureKind, "无法读取 MOD 清单：" + error.Message, error);
        }
    }

    static string ReadRequiredString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        ModUpdateStagingFailureKind failureKind)
    {
        if (!properties.TryGetValue(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            throw new StagingFailureException(failureKind, "MOD 清单缺少有效的 " + name + " 字段。");

        string text = value.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 180 ||
            text.Any(char.IsControl) || !text.Equals(text.Trim(), StringComparison.Ordinal))
            throw new StagingFailureException(failureKind, "MOD 清单中的 " + name + " 字段为空或格式不安全。");
        return text;
    }

    static bool ReadOptionalBoolean(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        ModUpdateStagingFailureKind failureKind)
    {
        if (!properties.TryGetValue(name, out JsonElement value))
            return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new StagingFailureException(failureKind, "MOD 清单中的 " + name + " 字段必须是布尔值。");
        return value.GetBoolean();
    }

    async Task<string> VerifyGitHubArchiveAsync(
        ModUpdateStagingRequest request,
        ModUpdateDownloadedArtifact artifact,
        string workspace,
        CancellationToken cancellationToken)
    {
        if (artifact.ArchivePath is null || artifact.PayloadDirectory is not null)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.InvalidPackageStructure,
                "GitHub 下载后端没有返回唯一的 ZIP 文件。");

        string archive = RequirePathInsideWorkspace(
            artifact.ArchivePath, workspace, mustBeDirectory: false);
        var info = new FileInfo(archive);
        if (!info.Exists || info.Length != request.ExpectedArchiveSize)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.HashMismatch,
                "下载包大小与 GitHub Release 元数据不一致。");

        string hash = await HashFileAsync(archive, HashAlgorithmName.SHA256, cancellationToken);
        if (!hash.Equals(request.ExpectedArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.HashMismatch,
                "下载包 SHA-256 与 GitHub Release 元数据不一致。");

        return hash;
    }

    static async Task<string> HashFileAsync(
        string path,
        HashAlgorithmName algorithm,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    static async Task ExtractGitHubArchiveAsync(
        string archivePath,
        string extractionRoot,
        CancellationToken cancellationToken,
        Action<ModUpdateStagingProgress>? progress)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        int extractedFiles = 0;

        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxArchiveEntries)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.InvalidArchive,
                "ZIP 文件数量为空或超过安全限制。");

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            string[] segments = ValidateArchivePath(entry.FullName, isDirectory);
            string relative = string.Join("/", segments);

            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & 0x400) != 0 ||
                (isDirectory && unixType == 0x8000) ||
                (!isDirectory && unixType == 0x4000))
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.UnsafeArchivePath,
                    "ZIP 包含符号链接、重解析点或特殊文件，已拒绝解压。");
            }

            string outputPath = SafeCombine(extractionRoot, relative);
            if (isDirectory)
            {
                if (files.Contains(relative))
                    throw new StagingFailureException(
                        ModUpdateStagingFailureKind.InvalidPackageStructure,
                        "ZIP 将同一路径同时声明为文件和目录。");
                directories.Add(relative);
                Directory.CreateDirectory(outputPath);
                continue;
            }

            if (!files.Add(relative) || directories.Contains(relative))
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.InvalidPackageStructure,
                    "ZIP 包含重复文件名或文件/目录路径冲突。");

            string[] parents = segments.Take(segments.Length - 1).ToArray();
            for (int index = 1; index <= parents.Length; index++)
            {
                string parent = string.Join("/", parents.Take(index));
                if (files.Contains(parent))
                    throw new StagingFailureException(
                        ModUpdateStagingFailureKind.InvalidPackageStructure,
                        "ZIP 文件路径存在父子冲突。");
                directories.Add(parent);
            }

            if (entry.Length < 0 || entry.Length > MaxExtractedBytes ||
                totalBytes > MaxExtractedBytes - entry.Length ||
                (entry.Length > 0 && entry.CompressedLength == 0) ||
                (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 1000))
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.InvalidArchive,
                    "ZIP 解压大小或压缩比例超过安全限制。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using Stream input = entry.Open();
            await using var output = new FileStream(
                outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[64 * 1024];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += count;
                totalBytes += count;
                if (written > entry.Length || totalBytes > MaxExtractedBytes)
                    throw new StagingFailureException(
                        ModUpdateStagingFailureKind.InvalidArchive,
                        "ZIP 实际解压内容超过安全限制。");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            if (written != entry.Length)
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.InvalidArchive,
                    "ZIP 文件实际长度与压缩包记录不一致。");

            extractedFiles++;
            Report(progress, "Extract", "已安全解压：" + relative, extractedFiles, archive.Entries.Count);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (extractedFiles == 0)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.InvalidPackageStructure,
                "ZIP 不包含任何普通文件。");
    }

    static string[] ValidateArchivePath(string name, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 600 ||
            name.StartsWith("/", StringComparison.Ordinal) ||
            name.StartsWith("\\", StringComparison.Ordinal) ||
            name.Contains('\\') || name.Contains(':') || name.Contains('\0') ||
            Path.IsPathRooted(name))
        {
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.UnsafeArchivePath,
                "ZIP 包含绝对路径、盘符路径或非法路径分隔符。");
        }

        string clean = isDirectory ? name[..^1] : name;
        string[] segments = clean.Split('/');
        if (segments.Length is 0 or > 16 || segments.Any(segment =>
                segment.Length is 0 or > 120 ||
                segment is "." or ".." ||
                segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.EndsWith(" ", StringComparison.Ordinal) ||
                segment.Any(character =>
                    char.IsControl(character) || "<>\"|?*".Contains(character)) ||
                ReservedNamePattern.IsMatch(segment.Split('.')[0])))
        {
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.UnsafeArchivePath,
                "ZIP 包含路径穿越、保留设备名或不安全的 Windows 文件名。");
        }

        return segments;
    }

    string VerifyWorkshopArtifact(
        ModUpdateStagingRequest request,
        ModUpdateDownloadedArtifact artifact,
        string workspace,
        CancellationToken cancellationToken)
    {
        if (artifact.PayloadDirectory is null || artifact.ArchivePath is not null ||
            artifact.WorkshopId is null ||
            !ulong.TryParse(request.SourceIdentifier, NumberStyles.None,
                CultureInfo.InvariantCulture, out ulong expectedWorkshopId) ||
            artifact.WorkshopId.Value != expectedWorkshopId ||
            !string.Equals(artifact.SourceManifestId, request.ExpectedWorkshopManifestId, StringComparison.Ordinal) ||
            !artifact.UpstreamIntegrityVerified ||
            artifact.Files is null || artifact.Files.Count == 0)
        {
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.IntegrityCheckFailed,
                "Workshop 下载结果缺少匹配的 Workshop/Manifest 身份或上游完整性校验记录。");
        }

        string payload = RequirePathInsideWorkspace(
            artifact.PayloadDirectory, workspace, mustBeDirectory: true);
        VerifyWorkshopAssemblyReport(payload, request, artifact);
        VerifyWorkshopFileEvidence(payload, artifact.Files, cancellationToken);
        return payload;
    }

    static void VerifyWorkshopAssemblyReport(
        string payload,
        ModUpdateStagingRequest request,
        ModUpdateDownloadedArtifact artifact)
    {
        string reportPath = Path.Combine(payload, "assembly-manifest.json");
        if (!File.Exists(reportPath))
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.IntegrityCheckFailed,
                "Workshop 组装目录缺少 assembly-manifest.json。");

        EnsureNoReparsePoint(reportPath, ModUpdateStagingFailureKind.IntegrityCheckFailed);
        var info = new FileInfo(reportPath);
        if (info.Length <= 0 || info.Length > MaxWorkshopReportBytes)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.IntegrityCheckFailed,
                "Workshop 组装报告为空或超过大小限制。");

        using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetInt32(root, "appId", out int appId) || appId != 2868840 ||
            !TryGetString(root, "publishedFileId", out string workshopId) ||
            !workshopId.Equals(request.SourceIdentifier, StringComparison.Ordinal) ||
            !TryGetString(root, "manifestId", out string manifestId) ||
            !manifestId.Equals(artifact.SourceManifestId, StringComparison.Ordinal) ||
            !TryGetString(root, "source", out string source) ||
            !source.Equals("spire_supply_station", StringComparison.Ordinal) ||
            !TryGetInt32(root, "fileCount", out int fileCount) ||
            fileCount != artifact.Files!.Count ||
            !TryGetBoolean(root, "chunkChecksumsVerified", out bool chunksVerified) || !chunksVerified ||
            !TryGetBoolean(root, "fileSha1Verified", out bool filesVerified) || !filesVerified ||
            !TryGetBoolean(root, "modFilesAssembled", out bool assembled) || !assembled ||
            !root.TryGetProperty("files", out JsonElement files) ||
            files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() != artifact.Files.Count)
        {
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.IntegrityCheckFailed,
                "Workshop 组装报告中的条目身份、文件数量或完整性标记不匹配。");
        }

        var evidence = artifact.Files.ToDictionary(
            item => NormalizeEvidencePath(item.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (!TryGetString(file, "path", out string path) ||
                !TryGetInt64(file, "sizeBytes", out long size) ||
                !TryGetString(file, "sha1", out string sha1) ||
                !TryGetBoolean(file, "sha1Verified", out bool verified) ||
                !verified ||
                !evidence.TryGetValue(NormalizeEvidencePath(path), out var expected) ||
                expected.SizeBytes != size ||
                !expected.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.IntegrityCheckFailed,
                    "Workshop 组装报告中的文件证据与下载后端返回的记录不一致。");
            }
        }
    }

    static void VerifyWorkshopFileEvidence(
        string payload,
        IReadOnlyList<ModUpdateDownloadedFileEvidence> evidence,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> actualFiles = EnumerateFiles(payload)
            .Where(item => !item.Key.Equals("assembly-manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

        var expected = new Dictionary<string, ModUpdateDownloadedFileEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (ModUpdateDownloadedFileEvidence item in evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeEvidencePath(item.RelativePath);
            if (!item.Sha1Verified || item.SizeBytes < 0 ||
                !Regex.IsMatch(item.Sha1, @"\A[0-9a-fA-F]{40}\z") ||
                !expected.TryAdd(relative, item))
            {
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.IntegrityCheckFailed,
                    "Workshop 文件证据包含无效 SHA-1、重复路径或未通过校验的文件。");
            }
        }

        if (actualFiles.Count != expected.Count)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.IntegrityCheckFailed,
                "Workshop 组装目录的实际文件数量与 Steam Manifest 证据不一致。");

        foreach (var pair in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actualFiles.TryGetValue(pair.Key, out string? path))
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.IntegrityCheckFailed,
                    "Workshop 组装目录缺少 Manifest 文件：" + pair.Key);

            var info = new FileInfo(path);
            if (info.Length != pair.Value.SizeBytes)
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.IntegrityCheckFailed,
                    "Workshop 文件长度与 Manifest 不一致：" + pair.Key);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] actualHash = SHA1.HashData(stream);
            byte[] expectedHash = Convert.FromHexString(pair.Value.Sha1);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.IntegrityCheckFailed,
                    "Workshop 文件 SHA-1 校验失败：" + pair.Key);
        }
    }

    static string NormalizeEvidencePath(string path)
    {
        string[] segments = ValidateArchivePath(path, isDirectory: false);
        return string.Join("/", segments);
    }

    static ValidatedPayload ValidatePayload(
        string payloadRoot,
        ModUpdateStagingRequest request,
        string expectedModId,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> files = EnumerateFiles(payloadRoot);
        string[] manifests = files
            .Where(item =>
            {
                string name = Path.GetFileName(item.Key);
                return name.Equals("mod_manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase);
            })
            .Select(item => item.Value)
            .ToArray();

        if (manifests.Length == 0)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.MissingManifest,
                "更新包缺少 mod_manifest.json 或 manifest.json。");
        if (manifests.Length != 1)
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.InvalidPackageStructure,
                "更新包必须且只能包含一个 MOD 清单。");

        string manifestPath = manifests[0];
        string modDirectory = Path.GetDirectoryName(manifestPath) ?? payloadRoot;
        if (modDirectory.Length == 0)
            modDirectory = payloadRoot;
        modDirectory = Path.GetFullPath(modDirectory);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Source == ModUpdateStagingSource.Workshop &&
                file.Key.Equals("assembly-manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsSameOrWithin(file.Value, modDirectory))
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.InvalidPackageStructure,
                    "更新包包含 MOD 主目录之外的额外文件：" + file.Key);
        }

        ManifestInfo manifest = ReadManifest(manifestPath, isTarget: false);
        if (!manifest.Id.Equals(expectedModId, StringComparison.OrdinalIgnoreCase))
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.ManifestMismatch,
                "更新包 MOD ID 与当前目标不一致；目标为 " + expectedModId + "，包内为 " + manifest.Id + "。");

        if (request.Source == ModUpdateStagingSource.GitHub &&
            !SameVersion(manifest.Version, request.ReleaseTag!))
        {
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.VersionMismatch,
                "更新包清单版本与所选 GitHub Release 标签不一致。");
        }

        Dictionary<string, string> payloadFiles = EnumerateFiles(modDirectory);
        bool hasDll = payloadFiles.Keys.Any(path =>
            path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        bool hasPck = payloadFiles.Keys.Any(path =>
            path.EndsWith(".pck", StringComparison.OrdinalIgnoreCase));
        if ((manifest.HasDll && !hasDll) || (manifest.HasPck && !hasPck) || (!hasDll && !hasPck))
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.InvalidPackageStructure,
                "更新包缺少清单要求的 DLL/PCK 文件，或没有任何可加载内容。");

        return new ValidatedPayload(modDirectory, manifest);
    }

    static bool SameVersion(string first, string second)
    {
        string left = first.Trim().TrimStart('v', 'V');
        string right = second.Trim().TrimStart('v', 'V');
        if (Version.TryParse(left, out Version? a) && Version.TryParse(right, out Version? b))
            return a == b;
        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    static Dictionary<string, FileSnapshot> SnapshotDirectory(
        string root,
        bool excludeWorkshopReport,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> files = EnumerateFiles(root);
        var snapshot = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludeWorkshopReport &&
                pair.Key.Equals("assembly-manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var infoBefore = new FileInfo(pair.Value);
            long size = infoBefore.Length;
            DateTime lastWrite = infoBefore.LastWriteTimeUtc;
            using var stream = new FileStream(pair.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            var infoAfter = new FileInfo(pair.Value);
            if (infoAfter.Length != size || infoAfter.LastWriteTimeUtc != lastWrite)
                throw new StagingFailureException(
                    ModUpdateStagingFailureKind.TargetChanged,
                    "读取文件时文件内容发生变化，请重新预览：" + pair.Key);

            snapshot.Add(pair.Key, new FileSnapshot(hash, size));
        }
        return snapshot;
    }

    static Dictionary<string, string> EnumerateFiles(string root)
    {
        EnsureNoReparsePoint(root, ModUpdateStagingFailureKind.InvalidPackageStructure);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new StagingFailureException(
                        ModUpdateStagingFailureKind.InvalidPackageStructure,
                        "目录中包含符号链接或重解析点，拒绝读取：" + entry);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                string[] segments = ValidateArchivePath(relative, isDirectory: false);
                string normalized = string.Join("/", segments);
                if (!files.TryAdd(normalized, entry))
                    throw new StagingFailureException(
                        ModUpdateStagingFailureKind.InvalidPackageStructure,
                        "文件树包含大小写冲突或重复路径：" + normalized);
            }
        }

        return files;
    }

    static IReadOnlyList<ModUpdateFileChange> Compare(
        IReadOnlyDictionary<string, FileSnapshot> current,
        IReadOnlyDictionary<string, FileSnapshot> staged)
    {
        var changes = new List<ModUpdateFileChange>();
        foreach (string path in current.Keys.Union(staged.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            bool hasCurrent = current.TryGetValue(path, out FileSnapshot? oldFile);
            bool hasStaged = staged.TryGetValue(path, out FileSnapshot? newFile);
            if (!hasCurrent)
            {
                changes.Add(new ModUpdateFileChange(
                    path, ModUpdateFileChangeKind.Added, null,
                    newFile!.Sha256, null, newFile.SizeBytes));
            }
            else if (!hasStaged)
            {
                changes.Add(new ModUpdateFileChange(
                    path, ModUpdateFileChangeKind.Deleted,
                    oldFile!.Sha256, null, oldFile.SizeBytes, null));
            }
            else if (!oldFile!.Sha256.Equals(newFile!.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new ModUpdateFileChange(
                    path, ModUpdateFileChangeKind.Modified,
                    oldFile.Sha256, newFile.Sha256, oldFile.SizeBytes, newFile.SizeBytes));
            }
        }
        return changes;
    }

    static string RequirePathInsideWorkspace(string path, string workspace, bool mustBeDirectory)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(workspace);
        if (!IsSameOrWithin(full, root) || full.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.InvalidPackageStructure,
                "下载后端返回的文件路径越过隔离暂存目录。");

        EnsureNoReparseAncestors(full, ModUpdateStagingFailureKind.InvalidPackageStructure);
        if (mustBeDirectory ? !Directory.Exists(full) : !File.Exists(full))
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.InvalidPackageStructure,
                "下载后端返回的暂存文件或目录不存在。");
        return full;
    }

    static string SafeCombine(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(
            fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrWithin(candidate, fullRoot) ||
            candidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new StagingFailureException(
                ModUpdateStagingFailureKind.UnsafeArchivePath,
                "ZIP 解压目标越过了隔离暂存根目录。");
        }
        return candidate;
    }

    static void EnsureNoReparsePoint(
        string path,
        ModUpdateStagingFailureKind failureKind)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new StagingFailureException(
                failureKind, "路径包含符号链接或 Windows 重解析点：" + path);
    }

    static void EnsureNoReparseAncestors(
        string path,
        ModUpdateStagingFailureKind failureKind)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            EnsureNoReparsePoint(current, failureKind);
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || parent.Equals(current, StringComparison.Ordinal))
                break;
            current = parent;
        }
    }

    static bool IsSameOrWithin(string path, string root)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static void DeleteTreeWithoutFollowingLinks(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0)
                Directory.Delete(path, recursive: false);
            else
                File.Delete(path);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
                DeleteTreeWithoutFollowingLinks(child);
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return true;
    }

    static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement property) &&
            property.TryGetInt32(out value);
    }

    static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement property) &&
            property.TryGetInt64(out value);
    }

    static void Report(
        Action<ModUpdateStagingProgress>? progress,
        string stage,
        string message,
        int completed = 0,
        int? total = null) =>
        progress?.Invoke(new ModUpdateStagingProgress(stage, message, completed, total));
}
