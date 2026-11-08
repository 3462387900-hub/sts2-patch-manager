using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace SpirePatch;

internal sealed record ModEnvironmentSnapshotFile(
    string Tree, string RelativePath, long Length, string Sha256,
    long LastWriteUtcTicks, int Attributes);

internal sealed record ModEnvironmentSnapshotDirectory(
    string Tree, string RelativePath, long LastWriteUtcTicks, int Attributes);

internal sealed record ModEnvironmentSnapshot(
    int SchemaVersion, string Id, string GameRoot, DateTimeOffset CreatedAt,
    string Purpose, bool ModsPresent, bool DisabledPresent,
    ModEnvironmentSnapshotDirectory[] Directories,
    ModEnvironmentSnapshotFile[] Files, long TotalBytes);

internal sealed record ModEnvironmentSnapshotEntry(
    string Id, DateTimeOffset CreatedAt, string Purpose, int FileCount,
    long TotalBytes, bool IsValid, string? Warning);

internal sealed record ModEnvironmentSnapshotPreview(
    string SnapshotId, DateTimeOffset CreatedAt, string Purpose,
    int CurrentFiles, int TargetFiles, int AddedFiles, int ChangedFiles,
    int RemovedFiles, int AddedDirectories, int RemovedDirectories,
    long TargetBytes, string Token, bool CurrentModsPresent, bool CurrentDisabledPresent);

internal sealed record ModEnvironmentSnapshotRestoreResult(
    string SafetySnapshotId, int RestoredFiles, int RemovedFiles,
    long RestoredBytes, int AddedDirectories, int RemovedDirectories);

/// <summary>
/// Full, local-only snapshots of the two MOD-owned trees: active mods and the
/// manager's disabled-MOD storage. Game files, saves and manager settings are excluded.
/// </summary>
internal static class ModEnvironmentSnapshots
{
    const int Schema = 1;
    const int MaxFiles = 50000;
    const int MaxDirectories = 30000;
    const long MaxBytes = 30L * 1024 * 1024 * 1024;
    const long MaxMetadataBytes = 32L * 1024 * 1024;
    const string SnapshotFolder = "MOD环境快照";
    const string RestoreStageFolder = "尖塔修补站_环境恢复暂存";
    static readonly Regex IdPattern = new(@"\A[0-9a-f]{32}\z", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex HashPattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static string? recoveryNotice;
    internal static string? TakeRecoveryNotice() => Interlocked.Exchange(ref recoveryNotice, null);

    sealed record TreeScan(
        string Name, bool Present,
        ModEnvironmentSnapshotDirectory[] Directories,
        ModEnvironmentSnapshotFile[] Files);

    internal sealed record ModEnvironmentRestoreJournal(
        int SchemaVersion, string GameRoot, string StageId,
        string SnapshotId, string SafetySnapshotId,
        bool OldModsPresent, bool OldDisabledPresent,
        bool TargetModsPresent, bool TargetDisabledPresent,
        string Status, DateTimeOffset StartedAt);

    static string NormalizeRoot(string root) => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    static string RootKey(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRoot(root).ToUpperInvariant())));
    static string StoreRoot(string root, string dataRoot)
    {
        string data = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(data);
        GameEnvironment.NoLinks(data);
        string store = BundleInstaller.Child(data, SnapshotFolder + "/" + RootKey(root));
        string mods = Path.Combine(root, "mods");
        string disabled = Path.Combine(root, ModManager.StorageName);
        if (IsSameOrChild(store, mods) || IsSameOrChild(store, disabled))
            throw new InvalidOperationException("管理器数据目录位于 MOD 目录内部，无法安全保存环境快照。请将管理器移到普通目录后重试。");
        return store;
    }

    static bool IsSameOrChild(string candidate, string parent)
    {
        string fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return fullCandidate.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
            fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static string TreePath(string root, string tree) => tree switch
    {
        "mods" => GameEnvironment.Child(root, "mods"),
        "disabled" => GameEnvironment.Child(root, ModManager.StorageName),
        _ => throw new InvalidDataException("快照包含未知目录类型。")
    };

    static string PayloadPath(string folder, string tree) =>
        BundleInstaller.Child(folder, tree);

    static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    static TreeScan ScanTree(string root, string tree) =>
        ScanDirectory(tree, TreePath(root, tree));

    static TreeScan ScanPayloadTree(string folder, string tree) =>
        ScanDirectory(tree, PayloadPath(folder, tree));

    static TreeScan ScanDirectory(string tree, string source)
    {
        if (!Exists(source))
            return new(tree, false, [], []);
        if ((File.GetAttributes(source) & FileAttributes.Directory) == 0)
            throw new InvalidDataException("MOD 目录位置被同名文件占用：" + source);

        var directories = new List<ModEnvironmentSnapshotDirectory>();
        var files = new List<ModEnvironmentSnapshotFile>();
        GameEnvironment.NoLinks(source);

        void Walk(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                GameEnvironment.NoLinks(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("MOD 环境中存在链接路径，快照操作已停止：" + entry);

                string relative = Path.GetRelativePath(source, entry).Replace('\\', '/');
                if (relative.Length == 0 || relative.Length > 800)
                    throw new InvalidDataException("MOD 环境中存在超出路径限制的项目：" + entry);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var info = new DirectoryInfo(entry);
                    directories.Add(new(tree, relative, info.LastWriteTimeUtc.Ticks, (int)attributes));
                    if (directories.Count > MaxDirectories)
                        throw new InvalidDataException("MOD 环境目录数量超过快照上限。");
                    Walk(entry);
                }
                else
                {
                    var info = new FileInfo(entry);
                    long length = info.Length;
                    if (length < 0)
                        throw new InvalidDataException("无法读取 MOD 文件大小：" + entry);
                    files.Add(new(tree, relative, length, Catalog.Hash(entry),
                        info.LastWriteTimeUtc.Ticks, (int)attributes));
                    if (files.Count > MaxFiles)
                        throw new InvalidDataException("MOD 环境文件数量超过快照上限。");
                }
            }
        }

        Walk(source);
        return new(tree, true,
            directories.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    static TreeScan[] ScanTrees(string root)
    {
        var trees = new[] { ScanTree(root, "mods"), ScanTree(root, "disabled") };
        long total = trees.SelectMany(tree => tree.Files).Sum(file => file.Length);
        if (total > MaxBytes)
            throw new InvalidDataException("MOD 环境超过单个快照 30 GB 的安全上限。");
        return trees;
    }

    static void CheckFreeSpace(string destination, long bytes)
    {
        string? driveRoot = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrWhiteSpace(driveRoot)) return;
        try
        {
            var drive = new DriveInfo(driveRoot);
            if (drive.IsReady && drive.AvailableFreeSpace < bytes + 32L * 1024 * 1024)
                throw new IOException("快照磁盘空间不足。需要约 " + FormatBytes(bytes) + "，请先清理磁盘空间。");
        }
        catch (ArgumentException) { }
        catch (DriveNotFoundException) { }
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    static void CopyFileVerified(string source, string destination, ModEnvironmentSnapshotFile expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > expected.Length)
                throw new InvalidDataException("复制期间文件大小发生变化：" + source);
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        output.Flush(true);
        string actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (copied != expected.Length || !actualHash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("复制期间文件内容发生变化或校验失败：" + source);

        File.SetLastWriteTimeUtc(destination, new DateTime(expected.LastWriteUtcTicks, DateTimeKind.Utc));
        const FileAttributes preserve = FileAttributes.ReadOnly | FileAttributes.Hidden |
            FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed |
            FileAttributes.Temporary;
        var safeAttributes = (FileAttributes)expected.Attributes & preserve;
        File.SetAttributes(destination, safeAttributes == 0 ? FileAttributes.Normal : safeAttributes);
    }

    static void CopyTreeFromSource(string root, string destination, TreeScan scan)
    {
        if (!scan.Present) return;
        Directory.CreateDirectory(destination);
        foreach (var directory in scan.Directories)
        {
            string target = BundleInstaller.Child(destination, directory.RelativePath);
            Directory.CreateDirectory(target);
        }
        foreach (var file in scan.Files)
        {
            string source = BundleInstaller.Child(TreePath(root, scan.Name), file.RelativePath);
            string target = BundleInstaller.Child(destination, file.RelativePath);
            CopyFileVerified(source, target, file);
        }
        foreach (var directory in scan.Directories.OrderByDescending(item => item.RelativePath.Length))
        {
            string target = BundleInstaller.Child(destination, directory.RelativePath);
            try { Directory.SetLastWriteTimeUtc(target, new DateTime(directory.LastWriteUtcTicks, DateTimeKind.Utc)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void CopyTreeFromSnapshot(string snapshotFolder, string destination, ModEnvironmentSnapshot snapshot, string tree)
    {
        bool present = tree == "mods" ? snapshot.ModsPresent : snapshot.DisabledPresent;
        if (!present) return;
        Directory.CreateDirectory(destination);
        foreach (var directory in snapshot.Directories.Where(item => item.Tree == tree))
        {
            string target = BundleInstaller.Child(destination, directory.RelativePath);
            Directory.CreateDirectory(target);
        }
        foreach (var file in snapshot.Files.Where(item => item.Tree == tree))
        {
            string source = BundleInstaller.Child(PayloadPath(snapshotFolder, tree), file.RelativePath);
            string target = BundleInstaller.Child(destination, file.RelativePath);
            CopyFileVerified(source, target, file);
        }
        foreach (var directory in snapshot.Directories.Where(item => item.Tree == tree)
            .OrderByDescending(item => item.RelativePath.Length))
        {
            string target = BundleInstaller.Child(destination, directory.RelativePath);
            try { Directory.SetLastWriteTimeUtc(target, new DateTime(directory.LastWriteUtcTicks, DateTimeKind.Utc)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void SaveMetadata(string folder, ModEnvironmentSnapshot snapshot)
    {
        string path = BundleInstaller.Child(folder, "snapshot.json");
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static ModEnvironmentSnapshot CreateLocked(string root, string dataRoot, string purpose)
    {
        var scans = ScanTrees(root);
        long bytes = scans.SelectMany(tree => tree.Files).Sum(file => file.Length);
        string store = StoreRoot(root, dataRoot);
        Directory.CreateDirectory(store);
        GameEnvironment.NoLinks(store);
        CheckFreeSpace(store, bytes);

        string id = Guid.NewGuid().ToString("N");
        string final = BundleInstaller.Child(store, id);
        string temporary = BundleInstaller.Child(store, "staging-" + id);
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var scan in scans)
                CopyTreeFromSource(root, BundleInstaller.Child(temporary, scan.Name), scan);

            // Verify the copied payload before publishing the snapshot metadata.
            foreach (var scan in scans)
            {
                var copied = ScanPayloadTree(temporary, scan.Name);
                if (!SameTree(scan, copied))
                    throw new InvalidDataException("快照复制后的目录结构或文件校验不一致：" + scan.Name);
                var sourceAfterCopy = ScanTree(root, scan.Name);
                if (!SameTree(scan, sourceAfterCopy))
                    throw new InvalidDataException("创建快照期间源 MOD 目录发生变化，已取消并保留原文件：" + scan.Name);
            }

            var snapshot = new ModEnvironmentSnapshot(
                Schema, id, NormalizeRoot(root), DateTimeOffset.UtcNow, purpose,
                scans.Single(tree => tree.Name == "mods").Present,
                scans.Single(tree => tree.Name == "disabled").Present,
                scans.SelectMany(tree => tree.Directories).ToArray(),
                scans.SelectMany(tree => tree.Files).ToArray(),
                bytes);
            SaveMetadata(temporary, snapshot);
            Directory.Move(temporary, final);
            return snapshot;
        }
        catch
        {
            TryDeleteTree(temporary);
            throw;
        }
    }

    internal static ModEnvironmentSnapshot Create(string gameRoot, string dataRoot, string purpose = "手动快照")
    {
        string root = GameEnvironment.Root(gameRoot);
        return GameEnvironment.WithLock(root, () =>
        {
            if (GameEnvironment.Running(root))
                throw new InvalidOperationException("请先退出游戏，再创建 MOD 环境快照。");
            if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 80)
                throw new ArgumentException("快照说明长度无效。");
            return CreateLocked(root, dataRoot, purpose);
        });
    }

    static string SnapshotStore(string root, string dataRoot) => StoreRoot(root, dataRoot);

    static string SnapshotPath(string root, string dataRoot, string id)
    {
        if (!IdPattern.IsMatch(id))
            throw new InvalidDataException("快照编号无效。");
        return BundleInstaller.Child(SnapshotStore(root, dataRoot), id);
    }

    static ModEnvironmentSnapshot ReadSnapshot(string root, string dataRoot, string id, out string folder)
    {
        folder = SnapshotPath(root, dataRoot, id);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("找不到指定的 MOD 环境快照。");
        GameEnvironment.NoLinks(folder);
        string metadataPath = BundleInstaller.Child(folder, "snapshot.json");
        if (!File.Exists(metadataPath))
            throw new InvalidDataException("快照缺少索引文件，无法安全恢复。");
        if (new FileInfo(metadataPath).Length > MaxMetadataBytes)
            throw new InvalidDataException("快照索引文件超过安全大小限制。");
        var snapshot = JsonSerializer.Deserialize<ModEnvironmentSnapshot>(File.ReadAllText(metadataPath))
            ?? throw new InvalidDataException("快照索引无法读取。");
        ValidateMetadata(root, folder, snapshot);
        return snapshot;
    }

    static void ValidateMetadata(string root, string folder, ModEnvironmentSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != Schema ||
            string.IsNullOrWhiteSpace(snapshot.Id) || !IdPattern.IsMatch(snapshot.Id) ||
            !snapshot.Id.Equals(Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(snapshot.GameRoot) ||
            !snapshot.GameRoot.Equals(NormalizeRoot(root), StringComparison.OrdinalIgnoreCase) ||
            snapshot.CreatedAt == default ||
            snapshot.Files is null || snapshot.Directories is null ||
            snapshot.Files.Length > MaxFiles || snapshot.Directories.Length > MaxDirectories ||
            snapshot.TotalBytes < 0 || snapshot.TotalBytes > MaxBytes ||
            string.IsNullOrWhiteSpace(snapshot.Purpose) || snapshot.Purpose.Length > 80)
            throw new InvalidDataException("快照索引版本、目录或数量信息无效。");

        var fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in snapshot.Files)
        {
            if (file is null) throw new InvalidDataException("快照文件清单包含空项目。");
            ValidateTreeAndRelativePath(folder, file.Tree, file.RelativePath);
            if (file.Length < 0 || file.LastWriteUtcTicks < DateTime.MinValue.Ticks ||
                file.LastWriteUtcTicks > DateTime.MaxValue.Ticks ||
                string.IsNullOrWhiteSpace(file.Sha256) || !HashPattern.IsMatch(file.Sha256) ||
                !fileKeys.Add(file.Tree + "\0" + file.RelativePath))
                throw new InvalidDataException("快照文件清单存在重复路径或无效摘要。");
            total = checked(total + file.Length);
            if (total > MaxBytes) throw new InvalidDataException("快照文件总大小超过安全上限。");
            bool treePresent = file.Tree == "mods" ? snapshot.ModsPresent : snapshot.DisabledPresent;
            if (!treePresent) throw new InvalidDataException("快照文件属于未记录的目录。");
        }
        foreach (var directory in snapshot.Directories)
        {
            if (directory is null) throw new InvalidDataException("快照目录清单包含空项目。");
            ValidateTreeAndRelativePath(folder, directory.Tree, directory.RelativePath);
            if (directory.LastWriteUtcTicks < DateTime.MinValue.Ticks ||
                directory.LastWriteUtcTicks > DateTime.MaxValue.Ticks ||
                !directoryKeys.Add(directory.Tree + "\0" + directory.RelativePath))
                throw new InvalidDataException("快照目录清单存在重复路径。");
            bool treePresent = directory.Tree == "mods" ? snapshot.ModsPresent : snapshot.DisabledPresent;
            if (!treePresent) throw new InvalidDataException("快照目录属于未记录的目录。");
        }
        if (total != snapshot.TotalBytes)
            throw new InvalidDataException("快照索引中的文件总大小不一致。");
        foreach (string key in fileKeys)
            if (directoryKeys.Contains(key) || directoryKeys.Any(dir =>
                dir.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("快照索引包含互相冲突的文件与目录路径。");
    }

    static void ValidateTreeAndRelativePath(string folder, string tree, string relative)
    {
        if (tree is not ("mods" or "disabled") || string.IsNullOrWhiteSpace(relative) ||
            relative.Length > 800 || relative.StartsWith('/') || relative.Contains('\\') ||
            relative.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("快照包含不安全的相对路径。");
        _ = BundleInstaller.Child(PayloadPath(folder, tree), relative);
    }

    static bool SameTree(TreeScan expected, TreeScan actual)
    {
        if (expected.Name != actual.Name || expected.Present != actual.Present ||
            expected.Directories.Length != actual.Directories.Length ||
            expected.Files.Length != actual.Files.Length) return false;
        var dirs = actual.Directories.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (expected.Directories.Any(item => !dirs.ContainsKey(item.RelativePath))) return false;
        var files = actual.Files.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        return expected.Files.All(item => files.TryGetValue(item.RelativePath, out var found) &&
            item.Length == found.Length && item.Sha256.Equals(found.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    static TreeScan[] SnapshotTrees(ModEnvironmentSnapshot snapshot) =>
    [
        new("mods", snapshot.ModsPresent,
            snapshot.Directories.Where(item => item.Tree == "mods").ToArray(),
            snapshot.Files.Where(item => item.Tree == "mods").ToArray()),
        new("disabled", snapshot.DisabledPresent,
            snapshot.Directories.Where(item => item.Tree == "disabled").ToArray(),
            snapshot.Files.Where(item => item.Tree == "disabled").ToArray())
    ];

    static void VerifySnapshotPayload(string folder, ModEnvironmentSnapshot snapshot)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "snapshot.json" };
        if (snapshot.ModsPresent) allowed.Add("mods");
        if (snapshot.DisabledPresent) allowed.Add("disabled");
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
        {
            string name = Path.GetFileName(entry);
            if (!allowed.Contains(name))
                throw new InvalidDataException("快照目录包含索引之外的文件或目录：" + name);
            GameEnvironment.NoLinks(entry);
        }

        foreach (var expected in SnapshotTrees(snapshot))
        {
            string payload = PayloadPath(folder, expected.Name);
            if (!expected.Present)
            {
                if (Exists(payload))
                    throw new InvalidDataException("快照包含索引未声明的目录：" + expected.Name);
                continue;
            }
            if (!Directory.Exists(payload))
                throw new InvalidDataException("快照内容目录缺失：" + expected.Name);
            var actual = ScanPayloadTree(folder, expected.Name);
            if (!SameTree(expected, actual))
                throw new InvalidDataException("快照内容与索引不一致或已损坏：" + expected.Name);
        }
    }

    sealed record CurrentState(TreeScan[] Trees, ModEnvironmentSnapshotFile[] Files,
        ModEnvironmentSnapshotDirectory[] Directories);

    static CurrentState CaptureCurrent(string root)
    {
        var trees = ScanTrees(root);
        return new(trees, trees.SelectMany(tree => tree.Files).ToArray(),
            trees.SelectMany(tree => tree.Directories).ToArray());
    }

    static string FileKey(ModEnvironmentSnapshotFile file) => file.Tree + "\0" + file.RelativePath;
    static string DirectoryKey(ModEnvironmentSnapshotDirectory directory) => directory.Tree + "\0" + directory.RelativePath;

    static string Token(string root, string id, string metadataHash, CurrentState current)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Root = NormalizeRoot(root),
            SnapshotId = id,
            MetadataHash = metadataHash,
            Trees = current.Trees.Select(tree => new { tree.Name, tree.Present }).ToArray(),
            Files = current.Files.OrderBy(file => file.Tree, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            Directories = current.Directories.OrderBy(directory => directory.Tree, StringComparer.OrdinalIgnoreCase)
                .ThenBy(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    static ModEnvironmentSnapshotPreview PreviewLocked(string root, string dataRoot, string id)
    {
        if (GameEnvironment.Running(root))
            throw new InvalidOperationException("请先退出游戏，再预览环境恢复。");
        var snapshot = ReadSnapshot(root, dataRoot, id, out var folder);
        VerifySnapshotPayload(folder, snapshot);
        var current = CaptureCurrent(root);
        var targetFiles = snapshot.Files.ToDictionary(FileKey, StringComparer.OrdinalIgnoreCase);
        var currentFiles = current.Files.ToDictionary(FileKey, StringComparer.OrdinalIgnoreCase);
        int added = targetFiles.Count(pair => !currentFiles.ContainsKey(pair.Key));
        int removed = currentFiles.Count(pair => !targetFiles.ContainsKey(pair.Key));
        int changed = targetFiles.Count(pair => currentFiles.TryGetValue(pair.Key, out var file) &&
            (file.Length != pair.Value.Length || !file.Sha256.Equals(pair.Value.Sha256, StringComparison.OrdinalIgnoreCase)));

        var targetDirectories = snapshot.Directories.ToDictionary(DirectoryKey, StringComparer.OrdinalIgnoreCase);
        var currentDirectories = current.Directories.ToDictionary(DirectoryKey, StringComparer.OrdinalIgnoreCase);
        int addedDirectories = targetDirectories.Keys.Count(key => !currentDirectories.ContainsKey(key));
        int removedDirectories = currentDirectories.Keys.Count(key => !targetDirectories.ContainsKey(key));
        string metadataPath = BundleInstaller.Child(folder, "snapshot.json");
        string metadataHash = Catalog.Hash(metadataPath);

        return new(id, snapshot.CreatedAt, snapshot.Purpose,
            current.Files.Length, snapshot.Files.Length, added, changed, removed,
            addedDirectories, removedDirectories, snapshot.TotalBytes,
            Token(root, id, metadataHash, current),
            current.Trees.Single(tree => tree.Name == "mods").Present,
            current.Trees.Single(tree => tree.Name == "disabled").Present);
    }

    internal static ModEnvironmentSnapshotPreview PreviewRestore(string gameRoot, string dataRoot, string id)
    {
        string root = GameEnvironment.Root(gameRoot);
        return GameEnvironment.WithLock(root, () => PreviewLocked(root, dataRoot, id));
    }

    internal static ModEnvironmentSnapshotEntry[] List(string gameRoot, string dataRoot)
    {
        string root = GameEnvironment.Root(gameRoot);
        return GameEnvironment.WithLock(root, () =>
        {
            string store = SnapshotStore(root, dataRoot);
            if (!Directory.Exists(store)) return [];
            GameEnvironment.NoLinks(store);
            var entries = new List<ModEnvironmentSnapshotEntry>();
            foreach (string folder in Directory.EnumerateDirectories(store)
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                string id = Path.GetFileName(folder);
                if (!IdPattern.IsMatch(id)) continue;
                try
                {
                    GameEnvironment.NoLinks(folder);
                    string metadataPath = BundleInstaller.Child(folder, "snapshot.json");
                    if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > MaxMetadataBytes)
                        throw new InvalidDataException("快照索引缺失或超过安全大小。");
                    var snapshot = JsonSerializer.Deserialize<ModEnvironmentSnapshot>(File.ReadAllText(metadataPath))
                        ?? throw new InvalidDataException("快照索引无法读取。");
                    ValidateMetadata(root, folder, snapshot);
                    entries.Add(new(id, snapshot.CreatedAt, snapshot.Purpose, snapshot.Files.Length,
                        snapshot.TotalBytes, true, null));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                               InvalidOperationException or ArgumentException or OverflowException)
                {
                    entries.Add(new(id, new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder), TimeSpan.Zero),
                        "无法读取", 0, 0, false, error.Message));
                }
            }
            return entries.OrderByDescending(entry => entry.CreatedAt).ToArray();
        });
    }

    internal static void WriteRestoreJournal(string stageRoot, ModEnvironmentRestoreJournal journal)
    {
        string path = BundleInstaller.Child(stageRoot, "restore.json");
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(journal, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static ModEnvironmentRestoreJournal ReadRestoreJournal(string root, string stageRoot)
    {
        string path = BundleInstaller.Child(stageRoot, "restore.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
            throw new InvalidDataException("恢复事务日志缺失或超过安全大小。");
        var journal = JsonSerializer.Deserialize<ModEnvironmentRestoreJournal>(File.ReadAllText(path))
            ?? throw new InvalidDataException("恢复事务日志无法读取。");
        if (journal.SchemaVersion != Schema ||
            string.IsNullOrWhiteSpace(journal.GameRoot) ||
            !journal.GameRoot.Equals(NormalizeRoot(root), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(journal.StageId) ||
            !journal.StageId.Equals(Path.GetFileName(stageRoot), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(journal.SnapshotId) || !IdPattern.IsMatch(journal.SnapshotId) ||
            string.IsNullOrWhiteSpace(journal.SafetySnapshotId) || !IdPattern.IsMatch(journal.SafetySnapshotId) ||
            journal.StartedAt == default ||
            journal.Status is not ("prepared" or "old-moved-mods" or "old-moved-disabled" or
                "new-moved-mods" or "new-moved-disabled" or "completed" or "rolled-back" or "rollback-failed"))
            throw new InvalidDataException("恢复事务日志中的游戏目录、编号或状态无效。");
        return journal;
    }

    static void MoveLiveToRecovery(string stageRoot, string tree, string live)
    {
        if (File.Exists(live))
            throw new IOException("恢复事务期间 MOD 目录位置出现同名文件，无法自动回滚：" + live);
        if (!Directory.Exists(live)) return;
        string failedRoot = BundleInstaller.Child(stageRoot, "failed-new");
        Directory.CreateDirectory(failedRoot);
        string destination = BundleInstaller.Child(failedRoot, tree + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(live, destination);
    }

    static void VerifySafetySnapshot(string root, string dataRoot, string safetySnapshotId)
    {
        var safety = ReadSnapshot(root, dataRoot, safetySnapshotId, out var folder);
        VerifySnapshotPayload(folder, safety);
        foreach (var expected in SnapshotTrees(safety))
        {
            var actual = ScanTree(root, expected.Name);
            if (!SameTree(expected, actual))
                throw new InvalidDataException("自动保护快照与回滚后的目录不一致：" + expected.Name);
        }
    }

    static void RecoverStage(string root, string dataRoot, string stageRoot, ModEnvironmentRestoreJournal journal)
    {
        if (GameEnvironment.Running(root))
            throw new InvalidOperationException("检测到上次 MOD 环境恢复未完成。请先退出游戏，再重新打开管理器执行自动回滚。");

        string previousRoot = BundleInstaller.Child(stageRoot, "previous");
        string nextRoot = BundleInstaller.Child(stageRoot, "next");
        string[] trees = ["mods", "disabled"];

        foreach (string tree in trees.Reverse())
        {
            bool oldPresent = tree == "mods" ? journal.OldModsPresent : journal.OldDisabledPresent;
            bool targetPresent = tree == "mods" ? journal.TargetModsPresent : journal.TargetDisabledPresent;
            string live = TreePath(root, tree);
            string previous = BundleInstaller.Child(previousRoot, tree);
            string staged = BundleInstaller.Child(nextRoot, tree);
            bool previousExists = Directory.Exists(previous);
            bool stagedExists = Directory.Exists(staged);
            bool liveExists = Directory.Exists(live);

            if (File.Exists(live))
                throw new IOException("检测到 MOD 目录位置被同名文件占用，自动回滚已停止：" + live);

            if (oldPresent)
            {
                if (previousExists)
                {
                    if (liveExists) MoveLiveToRecovery(stageRoot, tree, live);
                    Directory.Move(previous, live);
                }
                else if (!liveExists)
                {
                    throw new IOException("原始 MOD 目录和恢复备份均不存在：" + tree);
                }
            }
            else if (!targetPresent)
            {
                if (liveExists)
                    throw new IOException("恢复前不存在的目录出现了意外内容，自动回滚不会删除它：" + live);
            }
            else if (stagedExists)
            {
                if (liveExists)
                    throw new IOException("恢复暂存目录和游戏目录同时存在，无法判断安全的回滚目标：" + tree);
            }
            else if (liveExists)
            {
                MoveLiveToRecovery(stageRoot, tree, live);
            }
        }

        VerifySafetySnapshot(root, dataRoot, journal.SafetySnapshotId);
        WriteRestoreJournal(stageRoot, journal with { Status = "rolled-back" });
        TryDeleteTree(stageRoot);
        if (Directory.Exists(stageRoot))
            throw new IOException("原始 MOD 环境已回滚，但恢复暂存目录无法清理：" + stageRoot);
    }

    internal static void RecoverInterrupted(string gameRoot, string dataRoot)
    {
        string root = GameEnvironment.Root(gameRoot);
        GameEnvironment.WithLock(root, () =>
        {
            string parent = GameEnvironment.Child(root, RestoreStageFolder);
            if (!Exists(parent)) return true;
            if (!Directory.Exists(parent))
                throw new InvalidDataException("MOD 环境恢复暂存位置被同名文件占用。");
            GameEnvironment.NoLinks(parent);

            foreach (string stageRoot in Directory.EnumerateDirectories(parent))
            {
                string stageId = Path.GetFileName(stageRoot);
                if (!IdPattern.IsMatch(stageId)) continue;
                GameEnvironment.NoLinks(stageRoot);
                string journalPath = BundleInstaller.Child(stageRoot, "restore.json");
                if (!File.Exists(journalPath))
                {
                    // Live directories are never moved until the journal is safely written.
                    TryDeleteTree(stageRoot);
                    if (Directory.Exists(stageRoot))
                        throw new IOException("无法清理未开始提交的恢复暂存目录：" + stageRoot);
                    continue;
                }

                var journal = ReadRestoreJournal(root, stageRoot);
                if (journal.Status is "completed" or "rolled-back")
                {
                    TryDeleteTree(stageRoot);
                    if (Directory.Exists(stageRoot))
                        throw new IOException("无法清理已完成的恢复暂存目录：" + stageRoot);
                    continue;
                }

                try
                {
                    RecoverStage(root, dataRoot, stageRoot, journal);
                    Interlocked.Exchange(ref recoveryNotice,
                        "检测到上次 MOD 环境恢复中断，已自动回滚到恢复前环境。");
                }
                catch (Exception error)
                {
                    throw new IOException("检测到上次 MOD 环境恢复未完成，自动回滚未能完成。请保留此目录，不要手动删除：" +
                        stageRoot + "。原因：" + error.Message, error);
                }
            }
            return true;
        });
    }

    internal static ModEnvironmentSnapshotRestoreResult Restore(
        string gameRoot, string dataRoot, string snapshotId, string expectedToken)
    {
        string root = GameEnvironment.Root(gameRoot);
        return GameEnvironment.WithLock(root, () =>
        {
            var preview = PreviewLocked(root, dataRoot, snapshotId);
            if (!preview.Token.Equals(expectedToken, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前 MOD 文件在预览后发生变化，请重新预览恢复方案。");

            var snapshot = ReadSnapshot(root, dataRoot, snapshotId, out var snapshotFolder);
            string stageId = Guid.NewGuid().ToString("N");
            string stageRoot = GameEnvironment.Child(root, RestoreStageFolder + "/" + stageId);
            string nextRoot = BundleInstaller.Child(stageRoot, "next");
            string previousRoot = BundleInstaller.Child(stageRoot, "previous");
            Directory.CreateDirectory(nextRoot);
            Directory.CreateDirectory(previousRoot);

            bool success = false;
            bool preserveStage = false;
            try
            {
                CheckFreeSpace(root, snapshot.TotalBytes);
                CopyTreeFromSnapshot(snapshotFolder, BundleInstaller.Child(nextRoot, "mods"), snapshot, "mods");
                CopyTreeFromSnapshot(snapshotFolder, BundleInstaller.Child(nextRoot, "disabled"), snapshot, "disabled");
                foreach (var expected in SnapshotTrees(snapshot))
                {
                    var staged = ScanDirectory(expected.Name, BundleInstaller.Child(nextRoot, expected.Name));
                    if (!SameTree(expected, staged))
                        throw new InvalidDataException("恢复暂存目录校验失败：" + expected.Name);
                }

                var rechecked = PreviewLocked(root, dataRoot, snapshotId);
                if (!rechecked.Token.Equals(expectedToken, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("准备恢复期间当前 MOD 环境发生变化，请重新预览。");

                // A complete safety snapshot is committed before the first live directory move.
                var safety = CreateLocked(root, dataRoot, "恢复前自动保护");
                var afterSafety = PreviewLocked(root, dataRoot, snapshotId);
                if (!afterSafety.Token.Equals(expectedToken, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("创建恢复前保护快照期间，当前 MOD 环境发生变化。已保留保护快照，请重新预览。");

                string[] trees = ["mods", "disabled"];
                var journal = new ModEnvironmentRestoreJournal(
                    Schema, NormalizeRoot(root), stageId, snapshotId, safety.Id,
                    afterSafety.CurrentModsPresent, afterSafety.CurrentDisabledPresent,
                    snapshot.ModsPresent, snapshot.DisabledPresent,
                    "prepared", DateTimeOffset.UtcNow);
                WriteRestoreJournal(stageRoot, journal);
                var movedOld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var movedNew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string tree in trees)
                    {
                        string live = TreePath(root, tree);
                        if (File.Exists(live))
                            throw new InvalidOperationException("MOD 目录位置被同名文件占用，已停止恢复：" + live);
                        if (Directory.Exists(live))
                        {
                            string backup = BundleInstaller.Child(previousRoot, tree);
                            Directory.Move(live, backup);
                            movedOld.Add(tree);
                            journal = journal with { Status = tree == "mods" ? "old-moved-mods" : "old-moved-disabled" };
                            WriteRestoreJournal(stageRoot, journal);
                        }
                    }

                    foreach (string tree in trees)
                    {
                        bool present = tree == "mods" ? snapshot.ModsPresent : snapshot.DisabledPresent;
                        if (!present) continue;
                        string staged = BundleInstaller.Child(nextRoot, tree);
                        string live = TreePath(root, tree);
                        Directory.Move(staged, live);
                        movedNew.Add(tree);
                        journal = journal with { Status = tree == "mods" ? "new-moved-mods" : "new-moved-disabled" };
                        WriteRestoreJournal(stageRoot, journal);
                    }

                    foreach (var expected in SnapshotTrees(snapshot))
                    {
                        var actual = ScanTree(root, expected.Name);
                        if (!SameTree(expected, actual))
                            throw new InvalidDataException("恢复后的目录校验失败：" + expected.Name);
                    }
                    journal = journal with { Status = "completed" };
                    WriteRestoreJournal(stageRoot, journal);
                }
                catch (Exception restoreError)
                {
                    Exception? rollbackError = null;
                    try
                    {
                        foreach (string tree in trees.Reverse())
                        {
                            if (!movedNew.Contains(tree)) continue;
                            string live = TreePath(root, tree);
                            MoveLiveToRecovery(stageRoot, tree, live);
                        }
                        foreach (string tree in trees.Reverse())
                        {
                            if (!movedOld.Contains(tree)) continue;
                            string backup = BundleInstaller.Child(previousRoot, tree);
                            string live = TreePath(root, tree);
                            if (Directory.Exists(backup) && !Exists(live))
                                Directory.Move(backup, live);
                        }
                        VerifySafetySnapshot(root, dataRoot, safety.Id);
                    }
                    catch (Exception error) { rollbackError = error; }

                    if (rollbackError is not null)
                    {
                        preserveStage = true;
                        try { WriteRestoreJournal(stageRoot, journal with { Status = "rollback-failed" }); }
                        catch (Exception) { }
                        throw new IOException("环境恢复失败，自动回滚也未能完成。请保留此目录并勿手动删除：" +
                            stageRoot + "。恢复错误：" + restoreError.Message + "；回滚错误：" + rollbackError.Message,
                            new AggregateException(restoreError, rollbackError));
                    }
                    try { WriteRestoreJournal(stageRoot, journal with { Status = "rolled-back" }); }
                    catch (Exception) { }
                    throw;
                }

                success = true;
                return new ModEnvironmentSnapshotRestoreResult(
                    safety.Id, snapshot.Files.Length, preview.RemovedFiles,
                    snapshot.TotalBytes, preview.AddedDirectories, preview.RemovedDirectories);
            }
            finally
            {
                if (success || !preserveStage) TryDeleteTree(stageRoot);
            }
        });
    }

    internal static void Delete(string gameRoot, string dataRoot, string id)
    {
        string root = GameEnvironment.Root(gameRoot);
        GameEnvironment.WithLock(root, () =>
        {
            string folder = SnapshotPath(root, dataRoot, id);
            if (!Directory.Exists(folder))
            {
                if (File.Exists(folder)) throw new InvalidDataException("快照编号位置被同名文件占用。");
                throw new DirectoryNotFoundException("找不到指定快照。");
            }
            GameEnvironment.NoLinks(folder);
            TryDeleteTree(folder);
            if (Directory.Exists(folder))
                throw new IOException("快照未能完整删除，请检查文件权限：" + folder);
            return true;
        });
    }

    static void TryDeleteTree(string path)
    {
        try { DeleteTree(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException) { }
    }

    static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        GameEnvironment.NoLinks(path);
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            GameEnvironment.NoLinks(entry);
            if (Directory.Exists(entry)) DeleteTree(entry);
            else
            {
                try { File.SetAttributes(entry, FileAttributes.Normal); } catch (IOException) { }
                File.Delete(entry);
            }
        }
        try { File.SetAttributes(path, FileAttributes.Normal); } catch (IOException) { }
        Directory.Delete(path, false);
    }
}
