using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

/// <summary>
/// Physical adapter for untrusted local archives. It has no network client, installer,
/// workshop integration or write access to the game tree. Inventory and binding reads
/// deliberately avoid ModManager.Scan (which can perform interrupted recovery).
/// </summary>
internal sealed class LocalModZipArchiveBackend : ILocalModZipImportBackend
{
    internal const long MaxArchiveBytes = 256L * 1024 * 1024;
    internal const long MaxExtractedBytes = 512L * 1024 * 1024;
    internal const long MaxFileBytes = 256L * 1024 * 1024;
    internal const int MaxEntries = 2000;
    internal const int MaxManifestBytes = 128 * 1024;
    internal const int MaxDepth = 16;
    internal const int MaxPathCharacters = 600;
    internal const double MaxCompressionRatio = 1000d;

    static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse",
          ".hta", ".scr", ".msi", ".msp", ".lnk", ".url" };

    sealed record ArchiveFile(ZipArchiveEntry Entry, string Name);
    sealed record ManifestDetails(string Id, string Name, string Version, string Author,
        string Description, IReadOnlyList<LocalZipDependency> Dependencies, bool HasDll, bool HasPck);
    sealed record InventoryItem(string Id, string Folder, bool Enabled);
    sealed record InventorySnapshot(List<InventoryItem> Items, List<string> Problems);

    internal static bool AcceptCompression(long uncompressed, long compressed) =>
        uncompressed >= 0 && compressed >= 0 &&
        (uncompressed == 0 || (compressed > 0 &&
         uncompressed / (double)compressed <= MaxCompressionRatio));

    internal static bool WithinLimits(long archiveBytes, long totalBytes,
        long singleBytes, int entries, long manifestBytes) =>
        archiveBytes is > 0 and <= MaxArchiveBytes &&
        totalBytes is >= 0 and <= MaxExtractedBytes &&
        singleBytes is >= 0 and <= MaxFileBytes &&
        entries is > 0 and <= MaxEntries &&
        manifestBytes is >= 0 and <= MaxManifestBytes;

    public async Task<LocalZipPackage> StageAsync(
        string zipPath, string workspaceDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string zip = Path.GetFullPath(zipPath);
        string workspace = Path.GetFullPath(workspaceDirectory);
        GameEnvironment.NoLinks(zip);
        GameEnvironment.NoLinks(workspace);
        if (!File.Exists(zip)) throw new InvalidDataException("本地 ZIP 文件不存在。");
        var zipInfo = new FileInfo(zip);
        if (zipInfo.Length <= 0 || zipInfo.Length > MaxArchiveBytes)
            throw new InvalidDataException("ZIP 压缩包超过 256 MiB 安全上限或为空。");
        if (!Directory.Exists(workspace) || Directory.EnumerateFileSystemEntries(workspace).Any())
            throw new InvalidOperationException("本地 ZIP 工作区不存在或已经包含文件。");

        string archiveSha = await HashFileAsync(zip, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string extracted = Path.Combine(workspace, "extracted");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ArchiveFile>();
        long total = 0;

        using (var input = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false))
        {
            if (archive.Entries.Count is 0 or > MaxEntries)
                throw new InvalidDataException("ZIP 条目数量为空或超过 2000。");

            // Validate the entire central directory before creating any extracted file.
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                string name = CheckedArchivePath(entry.FullName, directory);
                int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType is not (0 or 0x8000 or 0x4000) ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                    (directory && unixType == 0x8000) ||
                    (!directory && unixType == 0x4000))
                    throw new InvalidDataException("ZIP 包含链接、重解析点、特殊文件或文件类型与路径不一致。");
                if (!seen.Add(name))
                    throw new InvalidDataException("ZIP 包含大小写冲突或重复路径：" + name);
                if (directory) { directoryNames.Add(name); continue; }
                if (ForbiddenExtensions.Contains(Path.GetExtension(name)))
                    throw new InvalidDataException("ZIP 包含禁止的可执行文件、脚本或快捷方式：" + name);
                if (!AcceptCompression(entry.Length, entry.CompressedLength))
                    throw new InvalidDataException("ZIP 条目压缩比超过 1000 或压缩长度异常。");
                if (entry.Length > MaxFileBytes || entry.Length > MaxExtractedBytes - total)
                    throw new InvalidDataException("ZIP 单文件或解压总量超过安全限制。");
                total += entry.Length;
                fileNames.Add(name);
                files.Add(new(entry, name));
            }

            if (files.Count == 0) throw new InvalidDataException("ZIP 不包含普通文件。");
            foreach (string path in seen)
            {
                if (fileNames.Contains(path) && directoryNames.Contains(path))
                    throw new InvalidDataException("ZIP 文件和目录名称相同：" + path);
                string[] parts = path.Split('/');
                for (int i = 1; i < parts.Length; i++)
                {
                    string parent = string.Join("/", parts.Take(i));
                    if (fileNames.Contains(parent))
                        throw new InvalidDataException("ZIP 文件与其子路径冲突：" + path);
                }
            }

            var manifests = files.Where(item =>
                Path.GetFileName(item.Name).Equals("mod_manifest.json", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(item.Name).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (manifests.Length != 1)
                throw new InvalidDataException(manifests.Length == 0
                    ? "ZIP 缺少 mod_manifest.json 或 manifest.json。"
                    : "ZIP 中存在多个 MOD 清单，不允许猜测安装目标。");
            ArchiveFile manifest = manifests[0];
            if (manifest.Entry.Length <= 0 || manifest.Entry.Length > MaxManifestBytes)
                throw new InvalidDataException("MOD 清单为空或超过 128 KiB。");

            string prefix = manifest.Name.Contains('/')
                ? manifest.Name[..(manifest.Name.LastIndexOf('/') + 1)] : "";
            if (files.Any(item => !item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("ZIP 包含 MOD 主目录之外的其他文件。");
            string folder = prefix.Length == 0
                ? "" : prefix.TrimEnd('/').Split('/').Last();
            string folderReason = prefix.Length == 0
                ? "清单位于 ZIP 根目录，目标文件夹名取 MOD ID。"
                : "清单位于包装目录，剥离外层目录并取清单父目录名。";

            Directory.CreateDirectory(extracted);
            foreach (ArchiveFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = SafeCombine(extracted, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = file.Entry.Open();
                await using var dest = new FileStream(destination, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                var buffer = new byte[64 * 1024];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    written += count;
                    if (written > file.Entry.Length)
                        throw new InvalidDataException("ZIP 实际解压长度超过中央目录声明。");
                    await dest.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (written != file.Entry.Length)
                    throw new InvalidDataException("ZIP 实际解压长度与中央目录不一致。");
            }

            string currentSha = await HashFileAsync(zip, cancellationToken);
            if (!currentSha.Equals(archiveSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ZIP 在暂存期间发生变化，请重新选择。");

            string manifestOnDisk = SafeCombine(extracted, manifest.Name);
            ManifestDetails details = ReadManifest(manifestOnDisk);
            if (folder.Length == 0) folder = details.Id;
            ValidateSegment(folder);
            string payload = prefix.Length == 0
                ? extracted : SafeCombine(extracted, prefix.TrimEnd('/'));
            GameEnvironment.NoLinks(payload);
            var payloadFiles = Snapshot(payload, cancellationToken);
            if (payloadFiles.Count != files.Count)
                throw new InvalidDataException("暂存文件数量与 ZIP 清单不符。");
            if (details.HasDll && !payloadFiles.Any(file =>
                    file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ||
                details.HasPck && !payloadFiles.Any(file =>
                    file.RelativePath.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("MOD 清单声明 DLL/PCK，但实际资源缺失。");

            return new LocalZipPackage(zip, archiveSha, workspace, payload,
                details.Id, details.Name, details.Version, details.Author,
                details.Description, manifest.Name, prefix.Length == 0 ? "." : prefix.TrimEnd('/'),
                folder, folderReason,
                details.Dependencies, Array.AsReadOnly(payloadFiles.ToArray()), total);
        }
    }

    public LocalZipReadOnlyContext InspectReadOnly(
        string gameRoot, LocalZipPackage package, string dataRoot)
    {
        string root = Path.GetFullPath(gameRoot);
        string mods = Path.Combine(root, "mods");
        string target = Path.Combine(mods, package.DestinationFolderName);
        var conflicts = new List<LocalZipConflict>();
        if (package.DestinationFolderName.Equals("_SpirePatch_Fixes", StringComparison.OrdinalIgnoreCase) ||
            package.DestinationFolderName.StartsWith(".spirepatch", StringComparison.OrdinalIgnoreCase) ||
            package.DestinationFolderName.Equals(ModManager.StorageName, StringComparison.OrdinalIgnoreCase))
            conflicts.Add(new(LocalZipConflictKind.ReservedDirectory,
                "目标文件夹名称为管理器保留名称：" + package.DestinationFolderName));
        try
        {
            GameEnvironment.NoLinks(mods);
            GameEnvironment.NoLinks(target);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            conflicts.Add(new(LocalZipConflictKind.InventoryProblem, "目标路径包含链接或不可安全访问：" + e.Message));
        }

        InventorySnapshot inventory = ReadInventory(root);
        foreach (string problem in inventory.Problems)
            conflicts.Add(new(LocalZipConflictKind.InventoryProblem, problem));
        foreach (InventoryItem match in inventory.Items.Where(item =>
            item.Id.Equals(package.ModId, StringComparison.OrdinalIgnoreCase)))
        {
            conflicts.Add(new(match.Enabled
                ? LocalZipConflictKind.SameIdEnabled : LocalZipConflictKind.SameIdDisabled,
                "相同 MOD ID 已" + (match.Enabled ? "启用" : "停用") + "：" + match.Folder));
            if (!match.Folder.Equals(target, StringComparison.OrdinalIgnoreCase))
                conflicts.Add(new(LocalZipConflictKind.SameIdDifferentDirectory,
                    "同 ID 的现有 MOD 位于不同目录：" + match.Folder));
        }

        if (File.Exists(target))
            conflicts.Add(new(LocalZipConflictKind.TargetFileOccupied,
                "目标位置已存在同名文件：" + target));
        else if (Directory.Exists(target))
        {
            var other = inventory.Items.FirstOrDefault(item =>
                item.Enabled && item.Folder.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                !item.Id.Equals(package.ModId, StringComparison.OrdinalIgnoreCase));
            conflicts.Add(new(other is null
                ? LocalZipConflictKind.TargetDirectoryOccupied : LocalZipConflictKind.TargetDirectoryDifferentId,
                other is null ? "目标目录已经存在，禁止覆盖：" + target
                    : "同名目标目录属于其他 MOD ID：" + other.Id));
        }

        LocalZipSourceHints sources = ReadSources(dataRoot, root, package.ModId);
        return new(Array.AsReadOnly(conflicts.ToArray()), sources);
    }

    public async Task<LocalZipPreviewVerification> VerifyAsync(
        LocalZipPackage package, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string zipSha = await HashFileAsync(package.ZipPath, cancellationToken);
            if (!zipSha.Equals(package.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                return new(false, "原始 ZIP 的 SHA-256 已变化。");
            string workspace = Path.GetFullPath(package.WorkspaceDirectory);
            string extraction = Path.Combine(workspace, "extracted");
            string payload = Path.GetFullPath(package.PayloadDirectory);
            if (!IsInside(payload, extraction) || !Directory.Exists(payload))
                return new(false, "暂存产物路径不存在或越过工作区。");
            GameEnvironment.NoLinks(workspace);
            GameEnvironment.NoLinks(payload);
            var current = Snapshot(payload, cancellationToken);
            if (current.Count != package.Files.Count ||
                current.Where((file, index) =>
                    !file.RelativePath.Equals(package.Files[index].RelativePath, StringComparison.OrdinalIgnoreCase) ||
                    !file.Sha256.Equals(package.Files[index].Sha256, StringComparison.OrdinalIgnoreCase) ||
                    file.SizeBytes != package.Files[index].SizeBytes).Any())
                return new(false, "暂存文件内容、路径或数量发生变化。");
            foreach (string file in WalkFiles(extraction, cancellationToken))
                if (!IsInside(file, payload))
                    return new(false, "暂存目录新增了未验证的文件。");
            return new(true, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or InvalidDataException)
        {
            return new(false, "暂存复核失败：" + error.Message);
        }
    }

    public void Discard(string workspaceDirectory)
    {
        string path = Path.GetFullPath(workspaceDirectory);
        if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),
            @"\Alocalzip-[0-9a-fA-F]{32}\z"))
            throw new InvalidOperationException("拒绝清理非本地 ZIP 工作区。");
        if (File.Exists(path) || Directory.Exists(path)) DeleteWithoutFollowingLinks(path);
    }

    static string CheckedArchivePath(string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathCharacters ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("\\", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            Path.IsPathRooted(path))
            throw new InvalidDataException("ZIP 包含绝对路径、盘符、UNC 或非法分隔符。");
        string clean = directory ? path[..^1] : path;
        string[] parts = clean.Split('/');
        if (parts.Length is 0 or > MaxDepth)
            throw new InvalidDataException("ZIP 路径层数超过 16。");
        foreach (string part in parts) ValidateSegment(part);
        return string.Join("/", parts);
    }

    static void ValidateSegment(string segment)
    {
        if (segment.Length is 0 or > 120 || segment is "." or ".." ||
            segment.EndsWith(".", StringComparison.Ordinal) ||
            segment.EndsWith(" ", StringComparison.Ordinal) ||
            segment.Any(ch => char.IsControl(ch) || "<>:\"|?*\\/".Contains(ch)))
            throw new InvalidDataException("不安全的 Windows 文件名：" + segment);
        string head = segment.Split('.')[0].ToUpperInvariant();
        if (head is "CON" or "PRN" or "AUX" or "NUL" ||
            (head.Length == 4 && (head.StartsWith("COM", StringComparison.Ordinal) ||
            head.StartsWith("LPT", StringComparison.Ordinal)) && head[3] is >= '1' and <= '9')
            throw new InvalidDataException("ZIP 包含 Windows 保留设备名：" + segment);
    }

    static string SafeCombine(string parent, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(parent,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(full, parent) || full.Equals(Path.GetFullPath(parent),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP 目标路径越过隔离暂存目录。");
        GameEnvironment.NoLinks(full);
        return full;
    }

    static bool IsInside(string path, string root)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static ManifestDetails ReadManifest(string path)
    {
        if (new FileInfo(path).Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("清单大小超出限制。");
        using var input = File.OpenRead(path);
        using var json = JsonDocument.Parse(input,
            new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow });
        CheckUniqueProperties(json.RootElement);
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MOD 清单必须是 JSON 对象。");
        string id = RequiredText(root, "id", 120, false);
        string version = RequiredText(root, "version", 80, true);
        if (!char.IsAsciiLetterOrDigit(version[0]) ||
            version.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || "._+-".Contains(ch))))
            throw new InvalidDataException("MOD version 含非法字符。");
        string name = OptionalText(root, "name", 180) ?? id;
        string author = OptionalText(root, "author", 180) ?? "";
        string description = OptionalText(root, "description", 4096) ?? "";
        bool dll = OptionalBool(root, "has_dll");
        bool pck = OptionalBool(root, "has_pck");
        if (!dll && !pck) throw new InvalidDataException("清单未声明可加载的 DLL/PCK。");
        if (!HasProperty(root, "has_dll") && !HasProperty(root, "has_pck"))
            throw new InvalidDataException("清单缺少 has_dll/has_pck。");
        var dependencies = new List<LocalZipDependency>();
        if (TryGet(root, "dependencies", out JsonElement deps))
        {
            if (deps.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("dependencies 必须为数组。");
            foreach (JsonElement item in deps.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string dep = RequiredRawText(item.GetString(), "dependency id", 120, false);
                    dependencies.Add(new(dep, null, null, false));
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("依赖项必须是字符串或对象。");
                string depId = RequiredText(item, "id", 120, false);
                bool optional = OptionalBool(item, "optional");
                string? minimum = OptionalText(item, "min_version", 80);
                string? maximum = OptionalText(item, "max_version", 80);
                dependencies.Add(new(depId, minimum, maximum, optional));
            }
        }
        if (dependencies.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("MOD 声明重复依赖 ID。");
        return new(id, name, version, author, description,
            Array.AsReadOnly(dependencies.ToArray()), dll, pck);
    }

    static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in value.EnumerateArray()) CheckUniqueProperties(element);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty field in value.EnumerateObject())
            {
                if (!seen.Add(field.Name))
                    throw new InvalidDataException("JSON 包含重复字段（含大小写差异）：" + field.Name);
                CheckUniqueProperties(field.Value);
            }
        }
    }

    static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (JsonProperty p in root.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { value = p.Value; return true; }
        value = default;
        return false;
    }
    static bool HasProperty(JsonElement root, string name) => TryGet(root, name, out _);
    static string RequiredText(JsonElement root, string name, int max, bool allowVersionPunctuation)
    {
        if (!TryGet(root, name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("MOD 清单缺少有效 " + name + "。");
        return RequiredRawText(value.GetString(), name, max, allowVersionPunctuation);
    }
    static string RequiredRawText(string? value, string name, int max, bool allowVersionPunctuation)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max ||
            !value.Equals(value.Trim(), StringComparison.Ordinal) ||
            value.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)))
            throw new InvalidDataException("清单字段非法：" + name);
        _ = allowVersionPunctuation;
        return value;
    }
    static string? OptionalText(JsonElement root, string name, int max)
    {
        if (!TryGet(root, name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("MOD 清单字段类型错误：" + name);
        string text = value.GetString() ?? "";
        if (text.Length > max || text.Any(char.IsControl))
            throw new InvalidDataException("MOD 清单字段过长或包含控制字符：" + name);
        return text;
    }
    static bool OptionalBool(JsonElement root, string name)
    {
        if (!TryGet(root, name, out JsonElement value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("MOD 清单布尔字段格式错误：" + name);
        return value.GetBoolean();
    }

    static IReadOnlyList<LocalZipPayloadFile> Snapshot(string root, CancellationToken cancellationToken)
    {
        return WalkFiles(root, cancellationToken)
            .Select(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long length = new FileInfo(path).Length;
                using var stream = File.OpenRead(path);
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                return new LocalZipPayloadFile(
                    Path.GetRelativePath(root, path).Replace('\\', '/'), length, hash);
            }).OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static IEnumerable<string> WalkFiles(string directory, CancellationToken cancellationToken)
    {
        GameEnvironment.NoLinks(directory);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = pending.Pop();
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attrs = File.GetAttributes(child);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("检测到符号链接或重解析点：" + child);
                if ((attrs & FileAttributes.Directory) != 0) pending.Push(child);
                else yield return child;
            }
        }
    }

    static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int length;
        while ((length = await file.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            incremental.AppendData(buffer, 0, length);
        }
        return Convert.ToHexString(incremental.GetHashAndReset());
    }

    static InventorySnapshot ReadInventory(string root)
    {
        var result = new InventorySnapshot(new(), new());
        string recovery = Path.Combine(root, "尖塔修补站_环境恢复暂存");
        if (Directory.Exists(recovery))
        {
            try
            {
                GameEnvironment.NoLinks(recovery);
                if (Directory.EnumerateFileSystemEntries(recovery).Any())
                    result.Problems.Add("发现未处理的环境恢复暂存标记；只读预览不会自动执行恢复。");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            { result.Problems.Add("环境恢复暂存标记无法安全读取：" + e.Message); }
        }

        string mods = Path.Combine(root, "mods");
        if (Directory.Exists(mods)) ScanManifestFiles(mods, true, result);
        string disabled = Path.Combine(root, ModManager.StorageName);
        if (Directory.Exists(disabled))
        {
            try
            {
                GameEnvironment.NoLinks(disabled);
                foreach (string slot in Directory.EnumerateDirectories(disabled))
                {
                    GameEnvironment.NoLinks(slot);
                    string record = Path.Combine(slot, "记录.json");
                    if (!File.Exists(record))
                    {
                        result.Problems.Add("停用区缺少记录.json：" + slot);
                        continue;
                    }
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(record),
                            new JsonDocumentOptions { MaxDepth = 32 });
                        JsonElement obj = json.RootElement;
                        if (obj.ValueKind != JsonValueKind.Object) throw new InvalidDataException("记录不是对象");
                        string id = RequiredText(obj, "ModId", 120, false);
                        string status = RequiredText(obj, "Status", 50, false);
                        string original = OptionalText(obj, "Original", 600) ?? "";
                        string folder = original.Length > 0
                            ? Path.GetFullPath(Path.Combine(root, original.Replace('/', Path.DirectorySeparatorChar)))
                            : Path.Combine(slot, "内容");
                        if (status is not ("enabled" or "failed-restored"))
                            result.Items.Add(new InventoryItem(id, folder, false));
                        if (status is not ("disabled" or "enabled" or "failed-restored"))
                            result.Problems.Add("停用区记录需要恢复或状态未知：" + slot + " / " + status);
                    }
                    catch (Exception e) when (e is JsonException or InvalidDataException or
                        IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        result.Problems.Add("停用区记录无法只读核对：" + record + "：" + e.Message);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result.Problems.Add("停用区无法读取：" + e.Message);
            }
        }
        foreach (var group in result.Items.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
            result.Problems.Add("已存在重复 MOD ID（包含停用区）：" + group.Key);
        return result;
    }

    static void ScanManifestFiles(string root, bool enabled, InventorySnapshot snapshot)
    {
        try
        {
            foreach (string path in WalkFiles(root, CancellationToken.None)
                .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
                    JsonElement item = doc.RootElement;
                    if (item.ValueKind != JsonValueKind.Object ||
                        !TryGet(item, "id", out JsonElement id) ||
                        !HasProperty(item, "has_dll") && !HasProperty(item, "has_pck")) continue;
                    if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                        throw new InvalidDataException("清单 MOD ID 无效。");
                    snapshot.Items.Add(new InventoryItem(id.GetString()!,
                        Path.GetDirectoryName(path)!, enabled));
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or
                    IOException or UnauthorizedAccessException)
                {
                    snapshot.Problems.Add("无法核对现有 MOD 清单：" + path + "：" + e.Message);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            InvalidOperationException or InvalidDataException)
        {
            snapshot.Problems.Add("无法只读扫描 MOD 文件树：" + e.Message);
        }
    }

    static LocalZipSourceHints ReadSources(string dataRoot, string root, string modId)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())));
        var warnings = new List<string>();
        string? github = ReadBinding(Path.Combine(dataRoot, "GitHub模组更新", key + ".json"),
            modId, warnings, "GitHub");
        string? workshop = ReadBinding(Path.Combine(dataRoot, "创意工坊来源", key + ".json"),
            modId, warnings, "Workshop");
        string baselinePath = Path.Combine(dataRoot, "创意工坊来源", key + ".versions.json");
        bool baseline = false;
        if (File.Exists(baselinePath))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(baselinePath));
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("基线不是对象。");
                baseline = TryGet(json.RootElement, modId, out _);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                JsonException or InvalidDataException)
            {
                warnings.Add("Workshop 历史基线无法读取：" + e.Message);
            }
        }
        return new(github, workshop, baseline, null, null, Array.AsReadOnly(warnings.ToArray()));
    }

    static string? ReadBinding(string file, string modId, List<string> warnings, string kind)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("来源绑定不是 JSON 对象。");
            if (!TryGet(json.RootElement, modId, out JsonElement found)) return null;
            if (found.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("来源绑定值格式不正确。");
            return found.GetString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException)
        {
            warnings.Add(kind + " 历史来源绑定无法核对：" + e.Message);
            return null;
        }
    }

    static void DeleteWithoutFollowingLinks(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, false);
            else File.Delete(path);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
                DeleteWithoutFollowingLinks(child);
            Directory.Delete(path, false);
        }
        else File.Delete(path);
    }
}
