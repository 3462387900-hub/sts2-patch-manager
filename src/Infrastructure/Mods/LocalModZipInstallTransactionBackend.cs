using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

/// <summary>
/// Physical transaction adapter for first-time local ZIP installs. Preparation lives
/// beside the game root (same volume, never inside mods); journals live under manager
/// data. No network, Workshop download or existing-MOD replacement is performed.
/// </summary>
internal sealed class LocalModZipInstallTransactionBackend : ILocalModZipInstallTransactionBackend
{
    const string PreparationRootName = ".spirepatch-localzip-transactions";
    const string JournalDirectoryName = "LocalModZipInstallTransactions";

    readonly string dataRoot;
    readonly Func<string, bool> gameRunning;
    readonly Func<string, bool> lockAvailable;
    readonly Func<string, bool> isReparsePoint;
    readonly Func<LocalZipInstallFaultPoint, string, Exception?>? faultInjector;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal LocalModZipInstallTransactionBackend(
        string dataRoot,
        Func<string, bool>? gameRunning = null,
        Func<string, bool>? lockAvailable = null,
        Func<string, bool>? isReparsePoint = null,
        Func<LocalZipInstallFaultPoint, string, Exception?>? faultInjector = null)
    {
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.gameRunning = gameRunning ?? GameEnvironment.Running;
        this.lockAvailable = lockAvailable ?? (_ => true);
        this.isReparsePoint = isReparsePoint ?? IsReparsePoint;
        this.faultInjector = faultInjector;
        TransactionLogDirectory = Path.Combine(this.dataRoot, JournalDirectoryName);
    }

    public string TransactionLogDirectory { get; }

    public LocalZipInstallTransactionPaths CreatePaths(
        string gameRoot,
        string targetDirectory,
        string transactionId,
        string previewWorkspaceDirectory)
    {
        string root = NormalizeRoot(gameRoot);
        string target = Path.GetFullPath(targetDirectory);
        string workspace = Path.GetFullPath(previewWorkspaceDirectory);
        if (!Guid.TryParseExact(transactionId, "N", out _))
            throw new ArgumentException("事务 ID 必须是 32 位 GUID。", nameof(transactionId));

        string expectedParent = Path.Combine(root, "mods");
        if (!Path.GetDirectoryName(target)!.Equals(expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe, "安装目标必须是 mods 目录下的单层新目录。");

        string gameParent = Path.GetDirectoryName(root) ??
            throw new InvalidOperationException("无法取得游戏目录所在父目录。");
        string transactionDirectory = Path.Combine(
            gameParent, PreparationRootName, transactionId);
        string preparedDirectory = Path.Combine(transactionDirectory, "prepared");

        if (IsWithin(workspace, root) || IsWithin(root, workspace) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileName(workspace), @"\Alocalzip-[0-9a-fA-F]{32}\z"))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe, "预览暂存路径不属于隔离的本地 ZIP 工作区。");

        if (!Path.GetPathRoot(preparedDirectory)!.Equals(
            Path.GetPathRoot(target)!, StringComparison.OrdinalIgnoreCase))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe, "同卷准备目录与最终 MOD 目录不在同一卷。");

        EnsureLogDirectoryIndependent(root);
        return new(transactionId, root, target, transactionDirectory,
            preparedDirectory, workspace);
    }

    public void ValidateTargetParent(string gameRoot, string targetDirectory)
    {
        string root = NormalizeRoot(gameRoot);
        string target = Path.GetFullPath(targetDirectory);
        string mods = Path.Combine(root, "mods");

        if (!Path.GetDirectoryName(target)!.Equals(mods, StringComparison.OrdinalIgnoreCase))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe, "安装目标已越过游戏 mods 目录。");

        try
        {
            GameEnvironment.NoLinks(root);
            GameEnvironment.NoLinks(mods);
            if (isReparsePoint(root) || isReparsePoint(mods))
                throw new InvalidOperationException("游戏根目录或 mods 父目录已变成链接/重解析点。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe,
                "目标父目录无法安全访问或包含链接/重解析点：" + error.Message, error);
        }

        if (!Directory.Exists(mods))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe, "游戏 mods 目录不存在，拒绝自动创建安装父目录。");

        if (Directory.Exists(target) || File.Exists(target))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.TargetChanged,
                "全新安装目标已经出现，严禁覆盖：" + target);

        EnsureLogDirectoryIndependent(root);
    }

    public async Task PrepareAsync(
        LocalZipPackage package,
        LocalZipInstallTransactionPaths paths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePaths(paths);
        if (Directory.Exists(paths.TransactionDirectory) ||
            File.Exists(paths.TransactionDirectory))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PreparationFailed, "本次 GUID 准备目录已经存在，拒绝复用。");

        string preparationRoot = Path.GetDirectoryName(paths.TransactionDirectory)!;
        GameEnvironment.NoLinks(preparationRoot);
        Directory.CreateDirectory(paths.TransactionDirectory);
        GameEnvironment.NoLinks(paths.TransactionDirectory);
        Directory.CreateDirectory(paths.PreparedDirectory);

        foreach (LocalZipPayloadFile file in package.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowFault(LocalZipInstallFaultPoint.CopyFile, file.RelativePath);
            cancellationToken.ThrowIfCancellationRequested();

            string source = SafeCombine(package.PayloadDirectory, file.RelativePath);
            string destination = SafeCombine(paths.PreparedDirectory, file.RelativePath);
            GameEnvironment.NoLinks(source);
            if (!File.Exists(source))
                throw new InvalidDataException("预览暂存文件缺失：" + file.RelativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                copied += count;
                if (copied > file.SizeBytes)
                    throw new InvalidDataException("准备文件长度超过预览声明：" + file.RelativePath);
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            if (copied != file.SizeBytes)
                throw new InvalidDataException("准备文件长度与预览声明不一致：" + file.RelativePath);

            string actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("准备文件 SHA-256 与预览声明不一致：" + file.RelativePath);
        }

        IReadOnlyDictionary<string, string> snapshot = SnapshotDirectory(paths.PreparedDirectory);
        var expected = package.Files.ToDictionary(
            item => item.RelativePath, item => item.Sha256,
            StringComparer.OrdinalIgnoreCase);
        if (!MatchesSnapshot(snapshot, expected))
            throw new InvalidDataException("同卷准备目录包含缺失、新增或哈希不一致的文件。");
    }

    public IReadOnlyDictionary<string, string> SnapshotDirectory(string directory)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("待核验目录不存在：" + root);
        GameEnvironment.NoLinks(root);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("事务目录包含链接或重解析点：" + child);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                    continue;
                }

                string relative = Path.GetRelativePath(root, child).Replace('\\', '/');
                using var stream = File.OpenRead(child);
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                if (!result.TryAdd(relative, hash))
                    throw new InvalidDataException("事务目录包含大小写冲突的文件路径：" + relative);
            }
        }
        return result;
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);

    public void WriteJournal(LocalZipInstallTransactionJournal journal)
    {
        ValidateJournal(journal);
        EnsureLogDirectoryIndependent(journal.GameRoot);
        GameEnvironment.NoLinks(TransactionLogDirectory);
        Directory.CreateDirectory(TransactionLogDirectory);
        GameEnvironment.NoLinks(TransactionLogDirectory);

        ThrowFault(LocalZipInstallFaultPoint.WriteJournal, journal.State);
        string path = JournalPath(journal.TransactionId);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(journal, JsonOptions));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public void CommitDirectoryMove(LocalZipInstallTransactionJournal journal)
    {
        ValidateJournal(journal);
        string prepared = journal.PreparedDirectory;
        string target = journal.TargetDirectory;

        if (!Path.GetPathRoot(prepared)!.Equals(
            Path.GetPathRoot(target)!, StringComparison.OrdinalIgnoreCase))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe, "拒绝跨卷移动本地 MOD 安装目录。");

        if (!Directory.Exists(prepared))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PreparationFailed, "同卷准备目录已不存在，无法提交。");

        if (!lockAvailable(journal.GameRoot))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.CommitFailed, "游戏目录正被另一个管理器事务锁占用。");

        try
        {
            GameEnvironment.WithLock(journal.GameRoot, () =>
            {
                ValidateTargetParent(journal.GameRoot, target);
                if (gameRunning(journal.GameRoot))
                    throw new LocalZipInstallTransactionException(
                        LocalZipApplyFailureKind.GameRunning, "检测到游戏已启动，拒绝执行最终目录移动。");

                ThrowFault(LocalZipInstallFaultPoint.BeforeMove, target);
                Directory.Move(prepared, target);
                ThrowFault(LocalZipInstallFaultPoint.AfterMove, target);
                return true;
            });
        }
        catch (LocalZipInstallTransactionException)
        {
            throw;
        }
        catch (InvalidOperationException error)
        {
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.CommitFailed,
                "无法取得游戏目录事务锁或执行安全提交：" + error.Message, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.CommitFailed,
                "同卷目录移动失败：" + error.Message, error);
        }
    }

    public void ValidateJournal(LocalZipInstallTransactionJournal journal)
    {
        if (journal.SchemaVersion != 1 ||
            !Guid.TryParseExact(journal.TransactionId, "N", out _))
            throw new InvalidDataException("本地 ZIP 事务日志版本或 GUID 无效。");

        string root = NormalizeRoot(journal.GameRoot);
        string gameParent = Path.GetDirectoryName(root) ??
            throw new InvalidDataException("事务日志中的游戏根目录无效。");
        string expectedTransaction = Path.Combine(
            gameParent, PreparationRootName, journal.TransactionId);
        string expectedPrepared = Path.Combine(expectedTransaction, "prepared");
        string expectedTarget = Path.Combine(root, "mods", ValidateFolderName(journal.DestinationFolderName));

        if (!Path.GetFullPath(journal.TransactionDirectory).Equals(
                expectedTransaction, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(journal.PreparedDirectory).Equals(
                expectedPrepared, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(journal.TargetDirectory).Equals(
                expectedTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("事务日志中的目标或准备目录路径与 GUID 不匹配。");

        string workspace = Path.GetFullPath(journal.PreviewWorkspaceDirectory);
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileName(workspace), @"\Alocalzip-[0-9a-fA-F]{32}\z") ||
            IsWithin(workspace, root) || IsWithin(root, workspace))
            throw new InvalidDataException("事务日志中的预览暂存路径不安全。");

        if (string.IsNullOrWhiteSpace(journal.ModId) || journal.ModId.Length > 256 ||
            journal.ExpectedFileHashes is null ||
            journal.ExpectedFileHashes.Count is 0 or > 2000 ||
            journal.ExpectedFileHashes.Any(pair =>
                !IsSafeRelativePath(pair.Key) || !IsSha256(pair.Value)))
            throw new InvalidDataException("事务日志中的 MOD ID 或逐文件 SHA-256 清单无效。");

        if (journal.State is not ("Preparing" or "Prepared" or "ReadyToCommit" or
            "Committing" or "Committed" or "RecoveryRequired"))
            throw new InvalidDataException("事务日志状态未知：" + journal.State);

        if (!Enum.TryParse(journal.HistoricalBindingPolicy, out LocalZipHistoricalBindingPolicy policy) ||
            !Enum.IsDefined(typeof(LocalZipHistoricalBindingPolicy), policy))
            throw new InvalidDataException("事务日志中的历史来源策略无效。");

        EnsureLogDirectoryIndependent(root);
        GameEnvironment.NoLinks(root);
        GameEnvironment.NoLinks(TransactionLogDirectory);
    }

    public void CleanupPreparation(LocalZipInstallTransactionJournal journal)
    {
        ValidateJournal(journal);
        ThrowFault(LocalZipInstallFaultPoint.CleanupPreparation, journal.TransactionId);

        string parent = Path.GetDirectoryName(journal.TransactionDirectory)!;
        GameEnvironment.NoLinks(parent);
        if (Directory.Exists(journal.TransactionDirectory) ||
            File.Exists(journal.TransactionDirectory))
            DeleteWithoutFollowingLinks(journal.TransactionDirectory);
    }

    public void DeleteJournal(LocalZipInstallTransactionJournal journal)
    {
        ValidateJournal(journal);
        GameEnvironment.NoLinks(TransactionLogDirectory);
        string path = JournalPath(journal.TransactionId);
        if (File.Exists(path))
        {
            GameEnvironment.NoLinks(path);
            File.Delete(path);
        }
    }

    public IReadOnlyList<LocalZipPendingTransaction> ReadPendingTransactions()
    {
        if (!Directory.Exists(TransactionLogDirectory))
            return Array.Empty<LocalZipPendingTransaction>();

        GameEnvironment.NoLinks(TransactionLogDirectory);
        var result = new List<LocalZipPendingTransaction>();
        foreach (string path in Directory.EnumerateFiles(TransactionLogDirectory, "*.json",
            SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(fileName, "N", out _))
            {
                result.Add(new(fileName, null, "事务日志文件名不是有效 GUID。"));
                continue;
            }

            try
            {
                GameEnvironment.NoLinks(path);
                LocalZipInstallTransactionJournal? journal =
                    JsonSerializer.Deserialize<LocalZipInstallTransactionJournal>(File.ReadAllText(path));
                if (journal is null || !journal.TransactionId.Equals(
                    fileName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new(fileName, null, "事务日志为空或 GUID 与文件名不一致。"));
                    continue;
                }
                result.Add(new(fileName, journal, null));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                JsonException or InvalidDataException or ArgumentException)
            {
                result.Add(new(fileName, null, "事务日志无法读取：" + error.Message));
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    public LocalZipSourcePolicyResult ApplyHistoricalBindingPolicy(
        string gameRoot, string modId, LocalZipHistoricalBindingPolicy policy)
    {
        if (!Enum.IsDefined(typeof(LocalZipHistoricalBindingPolicy), policy))
            throw new ArgumentOutOfRangeException(nameof(policy));

        if (policy == LocalZipHistoricalBindingPolicy.PreserveUnverified)
            return new(true, false, false,
                "保留历史 GitHub/Workshop 信息，但本次本地 ZIP 仍标记为未验证来源。",
                null);

        if (policy == LocalZipHistoricalBindingPolicy.RequireManualReview)
            return new(true, false, true,
                "保留历史绑定作为线索；必须人工复核后才能重新信任来源。",
                null);

        string root = NormalizeRoot(gameRoot);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(root).ToUpperInvariant())));
        string workshopDirectory = Path.Combine(dataRoot, "创意工坊来源");
        string workshopBaseline = Path.Combine(workshopDirectory, key + ".versions.json");
        string workshopBindings = Path.Combine(workshopDirectory, key + ".json");
        string githubBindings = Path.Combine(dataRoot, "GitHub模组更新", key + ".json");

        var errors = new List<string>();
        bool removedAny = false;

        bool baselineOk = TryRemoveJsonEntry(workshopBaseline, modId, out bool baselineRemoved, out string? baselineError);
        removedAny |= baselineRemoved;
        if (!baselineOk && baselineError is not null)
            errors.Add("Workshop 版本基线：" + baselineError);

        // Remove the Workshop binding only after its baseline was safely removed.
        if (baselineOk)
        {
            bool workshopOk = TryRemoveJsonEntry(
                workshopBindings, modId, out bool workshopRemoved, out string? workshopError);
            removedAny |= workshopRemoved;
            if (!workshopOk && workshopError is not null)
                errors.Add("Workshop 来源绑定：" + workshopError);
        }

        bool githubOk = TryRemoveJsonEntry(
            githubBindings, modId, out bool githubRemoved, out string? githubError);
        removedAny |= githubRemoved;
        if (!githubOk && githubError is not null)
            errors.Add("GitHub 来源绑定：" + githubError);

        bool applied = errors.Count == 0;
        string? errorText = errors.Count == 0 ? null : string.Join("；", errors);
        string action = errors.Count > 0
            ? removedAny ? "部分历史来源绑定已解绑，仍有持久化失败项。" :
                "历史来源解绑未完成，原绑定已尽可能保留。"
            : removedAny ? "已显式解绑历史 GitHub/Workshop 绑定及可用版本基线。"
                : "未发现需要解绑的历史来源绑定。";

        return new(applied, errors.Count > 0 && removedAny, false, action, errorText);
    }

    bool TryRemoveJsonEntry(
        string path, string modId, out bool removed, out string? error)
    {
        removed = false;
        error = null;
        if (!File.Exists(path))
            return true;

        try
        {
            GameEnvironment.NoLinks(path);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("来源文件根节点不是 JSON 对象。");

            var entries = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                entries[property.Name] = property.Value.Clone();

            if (!entries.Remove(modId))
                return true;

            WriteJsonAtomically(path, entries);
            removed = true;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    void WriteJsonAtomically(string path, Dictionary<string, JsonElement> entries)
    {
        ThrowFault(LocalZipInstallFaultPoint.SourceBindingWrite, path);
        string directory = Path.GetDirectoryName(path)!;
        GameEnvironment.NoLinks(directory);
        Directory.CreateDirectory(directory);
        GameEnvironment.NoLinks(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries, JsonOptions));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    string JournalPath(string transactionId) =>
        Path.Combine(TransactionLogDirectory, transactionId + ".json");

    void ValidatePaths(LocalZipInstallTransactionPaths paths)
    {
        string root = NormalizeRoot(paths.GameRoot);
        string expectedTarget = Path.Combine(root, "mods",
            Path.GetFileName(paths.TargetDirectory));
        string parent = Path.GetDirectoryName(root)!;
        string expectedTransaction = Path.Combine(parent, PreparationRootName, paths.TransactionId);
        string expectedPrepared = Path.Combine(expectedTransaction, "prepared");

        if (!Path.GetFullPath(paths.TargetDirectory).Equals(
                expectedTarget, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(paths.TransactionDirectory).Equals(
                expectedTransaction, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(paths.PreparedDirectory).Equals(
                expectedPrepared, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(paths.TransactionId, "N", out _))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe, "事务准备路径与本次 GUID 不匹配。");

        if (!Path.GetPathRoot(paths.PreparedDirectory)!.Equals(
            Path.GetPathRoot(paths.TargetDirectory)!, StringComparison.OrdinalIgnoreCase))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe, "准备目录与最终目标不在同一卷。");
    }

    void EnsureLogDirectoryIndependent(string gameRoot)
    {
        string root = NormalizeRoot(gameRoot);
        string log = Path.GetFullPath(TransactionLogDirectory);
        if (IsWithin(log, root) || IsWithin(root, log))
            throw new LocalZipInstallTransactionException(
                LocalZipApplyFailureKind.PathUnsafe,
                "本地 ZIP 事务日志必须位于独立于游戏目录的管理器数据目录。");
    }

    static string NormalizeRoot(string gameRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));

    static string ValidateFolderName(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 120 ||
            folder is "." or ".." || folder.EndsWith(".", StringComparison.Ordinal) ||
            folder.EndsWith(" ", StringComparison.Ordinal) ||
            folder.Any(ch => char.IsControl(ch) || "<>:\"|?*\\/".Contains(ch)))
            throw new InvalidDataException("事务日志中的目标文件夹名无效。");
        return folder;
    }

    static string SafeCombine(string parent, string relative)
    {
        if (!IsSafeRelativePath(relative))
            throw new InvalidDataException("事务文件相对路径不安全：" + relative);
        string full = Path.GetFullPath(Path.Combine(
            parent, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(full, parent) ||
            full.Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("事务文件路径越过隔离目录。");
        GameEnvironment.NoLinks(full);
        return full;
    }

    static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 600 ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("\\", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains(':') || Path.IsPathRooted(path))
            return false;
        string[] segments = path.Split('/');
        return segments.Length is > 0 and <= 16 &&
            segments.All(segment => segment.Length is > 0 and <= 120 &&
                segment is not ("." or "..") &&
                !segment.EndsWith(".", StringComparison.Ordinal) &&
                !segment.EndsWith(" ", StringComparison.Ordinal) &&
                !segment.Any(ch => char.IsControl(ch) || "<>:\"|?*\\/".Contains(ch)));
    }

    static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    static bool MatchesSnapshot(
        IReadOnlyDictionary<string, string> actual,
        IReadOnlyDictionary<string, string> expected) =>
        actual.Count == expected.Count && expected.All(pair =>
            actual.TryGetValue(pair.Key, out string? hash) &&
            hash.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));

    static bool IsWithin(string path, string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    void ThrowFault(LocalZipInstallFaultPoint point, string detail)
    {
        Exception? error = faultInjector?.Invoke(point, detail);
        if (error is not null)
            throw error;
    }

    static void DeleteWithoutFollowingLinks(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
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
                DeleteWithoutFollowingLinks(child);
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }
}
