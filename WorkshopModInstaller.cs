using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal sealed record WorkshopModPrepared(
    string GameRoot,
    ulong WorkshopId,
    string WorkshopTitle,
    string ManifestId,
    string ModId,
    string ModName,
    string Version,
    bool HasDll,
    bool HasPck,
    string PayloadDirectory,
    string DestinationDirectory,
    string WorkspaceDirectory,
    string InitialInventoryToken,
    Dictionary<string, string> PayloadSnapshot,
    int FileCount,
    long TotalBytes,
    bool IsUpdate,
    bool IsUnchanged,
    string? ExistingFolder,
    string? ExistingVersion);

internal sealed record WorkshopModInstallResult(
    ulong WorkshopId,
    string ModId,
    string ModName,
    string Version,
    string DestinationDirectory,
    string? BackupFolder,
    bool Changed);

/// <summary>
/// Downloads one Steam Workshop UGC item through the authorized public Supply Station
/// descriptor, decrypts and assembles it in manager-owned staging, validates its MOD
/// manifest, then installs it through an explicit preview/commit transaction.
/// </summary>
internal sealed class WorkshopModInstaller : IDisposable
{
    private const int MaxManifestBytes = 128 * 1024;
    private const int MaxFiles = 2000;
    private const long MaxPayloadBytes = 300L * 1024 * 1024;
    private static readonly char[] InvalidWindowsCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly string dataRoot;
    private readonly HttpMessageHandler? handler;

    public WorkshopModInstaller(string dataRoot, HttpMessageHandler? handler = null)
    {
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.handler = handler;
    }

    public void Dispose()
    {
        // SpireSupplyStationClient owns and disposes the injected handler for each operation.
    }

    public static bool TryParseWorkshopId(string? input, out ulong workshopId)
    {
        workshopId = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string value = input.Trim();
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out workshopId))
            return workshopId > 0;

        workshopId = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        string? id = null;
        string? appId = null;
        if (uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.TrimEnd('/').Equals("/sharedfiles/filedetails", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.TrimEnd('/').Equals("/workshop/filedetails", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair = part.Split('=', 2);
                    string key = Uri.UnescapeDataString(pair[0]);
                    string item = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
                    if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                    {
                        if (id is not null)
                            return false;
                        id = item;
                    }
                    else if (key.Equals("appid", StringComparison.OrdinalIgnoreCase))
                    {
                        if (appId is not null)
                            return false;
                        appId = item;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
        }
        else if (uri.Host.Equals("workshop.apricityx.top", StringComparison.OrdinalIgnoreCase))
        {
            string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 3 || !segments[0].Equals("mod", StringComparison.OrdinalIgnoreCase))
                return false;
            appId = segments[1];
            id = segments[2];
        }
        else
        {
            return false;
        }

        if (appId is not null &&
            (!uint.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsedAppId) ||
             parsedAppId != SpireSupplyStationClient.SupportedAppId))
            return false;

        return id is not null &&
            ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out workshopId) &&
            workshopId > 0;
    }

    public async Task<WorkshopModPrepared> PrepareAsync(
        string gameRoot,
        ulong workshopId,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (workshopId == 0)
            throw new ArgumentOutOfRangeException(nameof(workshopId), "Workshop ID 不能为 0。");

        string root = GameEnvironment.Root(gameRoot);
        if (GameEnvironment.Running(root))
            throw new InvalidOperationException("请先完全退出游戏，再下载并准备 MOD。");

        InventorySnapshot initialInventory = CaptureInventory(root);
        string stagingRoot = Path.Combine(dataRoot, "尖塔修补站_创意工坊暂存");
        Directory.CreateDirectory(stagingRoot);
        GameEnvironment.NoLinks(stagingRoot);

        string workspace = Path.Combine(stagingRoot, workshopId.ToString(CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            string rawChunks = Path.Combine(workspace, "原始Chunk");
            string assembled = Path.Combine(workspace, "解密组装");
            using var client = new SpireSupplyStationClient(handler);

            progress?.Invoke("正在获取创意工坊下载授权…");
            var chunks = await client.ProbeChunksAsync(
                SpireSupplyStationClient.SupportedAppId,
                workshopId,
                rawChunks,
                maxConcurrentChunks: 4,
                progress,
                cancellationToken);

            if (chunks.DownloadedChunks == 0 || chunks.DownloadedChunks != chunks.ChunkCount)
                throw new InvalidDataException("原始 Chunk 下载未完整完成，已取消安装准备。");

            progress?.Invoke("原始 Chunk 已下载，正在解密、解压并重组 MOD 文件…");
            var assembly = await client.AssembleChunksAsync(
                SpireSupplyStationClient.SupportedAppId,
                workshopId,
                rawChunks,
                assembled,
                progress,
                cancellationToken);

            if (!assembly.ModFilesAssembled ||
                !assembly.ChunkChecksumsVerified ||
                !assembly.FileSha1Verified ||
                assembly.FileCount <= 0)
                throw new InvalidDataException("MOD 文件未通过完整的 Chunk 和文件校验，已取消安装准备。");

            cancellationToken.ThrowIfCancellationRequested();
            return PrepareAssembled(
                root,
                workshopId,
                assembly.Title,
                assembly.ManifestId,
                assembled,
                workspace,
                initialInventory);
        }
        catch
        {
            TryDeleteWorkspace(workspace);
            throw;
        }
    }

    internal static WorkshopModPrepared PrepareAssembled(
        string gameRoot,
        ulong workshopId,
        string workshopTitle,
        string manifestId,
        string assembledDirectory,
        string workspaceDirectory)
    {
        string root = GameEnvironment.Root(gameRoot);
        var inventory = CaptureInventory(root);
        Directory.CreateDirectory(workspaceDirectory);
        return PrepareAssembled(
            root, workshopId, workshopTitle, manifestId,
            assembledDirectory, workspaceDirectory, inventory);
    }

    private static WorkshopModPrepared PrepareAssembled(
        string root,
        ulong workshopId,
        string workshopTitle,
        string manifestId,
        string assembledDirectory,
        string workspaceDirectory,
        InventorySnapshot inventory)
    {
        if (workshopId == 0)
            throw new InvalidDataException("Workshop ID 无效。");

        string assembledRoot = Path.GetFullPath(assembledDirectory);
        if (!Directory.Exists(assembledRoot))
            throw new DirectoryNotFoundException("解密组装目录不存在。");
        GameEnvironment.NoLinks(assembledRoot);

        var candidates = FindModManifests(assembledRoot);
        if (candidates.Count != 1)
            throw new InvalidDataException(candidates.Count == 0
                ? "下载内容中没有找到有效的 MOD 清单（需要 ID 和 has_dll / has_pck）。"
                : "下载内容包含多个 MOD 清单；当前阶段只允许安装单个 MOD，已停止以避免误装整合包。");

        ModManifestCandidate candidate = candidates[0];
        string[] sourceFiles = SafeFiles(candidate.PayloadDirectory);
        if (sourceFiles.Length == 0)
            throw new InvalidDataException("MOD 内容目录为空。");

        bool containsDll = sourceFiles.Any(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        bool containsPck = sourceFiles.Any(path => path.EndsWith(".pck", StringComparison.OrdinalIgnoreCase));
        if ((candidate.HasDll && !containsDll) || (candidate.HasPck && !containsPck))
            throw new InvalidDataException("MOD 清单要求的 DLL 或 PCK 文件缺失。");
        if (!containsDll && !containsPck)
            throw new InvalidDataException("内容目录中没有 DLL 或 PCK 文件，不能识别为可安装 MOD。");

        string workspace = Path.GetFullPath(workspaceDirectory);
        Directory.CreateDirectory(workspace);
        string payload = Path.Combine(workspace, "安装内容");
        if (Directory.Exists(payload) || File.Exists(payload))
            throw new IOException("安装暂存目录已存在，请重新准备。");
        Directory.CreateDirectory(payload);

        long totalBytes = 0;
        foreach (string source in sourceFiles)
        {
            GameEnvironment.NoLinks(source);
            var info = new FileInfo(source);
            if (info.Length < 0 || info.Length > MaxPayloadBytes ||
                totalBytes > MaxPayloadBytes - info.Length)
                throw new InvalidDataException("MOD 文件总大小超出安全限制。");
            totalBytes += info.Length;

            string relative = Path.GetRelativePath(candidate.PayloadDirectory, source);
            ValidateRelativePath(relative);
            string target = SafeCombine(payload, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            if (output.Length != info.Length)
                throw new IOException("复制 MOD 文件时长度不匹配：" + relative);
        }

        string relativeManifest = Path.GetRelativePath(candidate.PayloadDirectory, candidate.ManifestPath);
        string copiedManifest = SafeCombine(payload, relativeManifest);
        var copiedCandidate = ReadModManifest(copiedManifest)
            ?? throw new InvalidDataException("复制后的 MOD 清单无法读取。");
        if (!copiedCandidate.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("复制后的 MOD ID 与原始清单不一致。");

        var payloadSnapshot = ModManager.FileSnapshot(payload);
        if (payloadSnapshot.Count != sourceFiles.Length)
            throw new InvalidDataException("安装暂存文件数量与组装内容不一致。");

        ManagedMod[] matches = inventory.Mods
            .Where(mod => mod.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("本地存在多个相同 MOD ID，无法确定更新目标；请先处理重复模组。");

        string modsRoot = Path.GetFullPath(Path.Combine(root, "mods"));
        GameEnvironment.NoLinks(modsRoot);
        bool isUpdate = matches.Length == 1;
        string destination;
        string? existingFolder = null;
        string? existingVersion = null;
        bool unchanged = false;

        if (isUpdate)
        {
            ManagedMod existing = matches[0];
            if (!existing.Enabled || existing.Storage is not null)
                throw new InvalidOperationException("目标 MOD 当前处于停用状态；请先启用，再执行创意工坊更新。");
            if (!Path.GetDirectoryName(existing.Folder)!.Equals(modsRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetDirectoryName(existing.Manifest)!.Equals(existing.Folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标 MOD 不是独立的 mods 子目录，无法安全自动替换。");

            GameEnvironment.NoLinks(existing.Folder);
            GameEnvironment.NoLinks(existing.Manifest);
            destination = existing.Folder;
            existingFolder = existing.Folder;
            existingVersion = existing.Version;
            unchanged = SameSnapshot(
                inventory.FileSnapshots[existing.Folder],
                payloadSnapshot);
        }
        else
        {
            string folderName = SafeFolderName(Path.GetFileName(candidate.PayloadDirectory));
            if (folderName.Length == 0)
                folderName = SafeFolderName(candidate.Name);
            if (folderName.Length == 0)
                folderName = SafeFolderName(candidate.Id);
            if (folderName.Length == 0)
                throw new InvalidDataException("无法从 MOD 清单生成安全的安装目录名。");

            destination = Path.Combine(modsRoot, folderName);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("安装目录已存在，但其中没有匹配的 MOD ID；为避免覆盖其他文件，已停止：" + destination);
        }

        return new WorkshopModPrepared(
            root,
            workshopId,
            workshopTitle,
            manifestId,
            candidate.Id,
            candidate.Name,
            candidate.Version,
            candidate.HasDll,
            candidate.HasPck,
            payload,
            destination,
            workspace,
            inventory.Token,
            payloadSnapshot,
            payloadSnapshot.Count,
            totalBytes,
            isUpdate,
            unchanged,
            existingFolder,
            existingVersion);
    }

    public static WorkshopModInstallResult Commit(string gameRoot, WorkshopModPrepared prepared)
    {
        string root = GameEnvironment.Root(gameRoot);
        if (!root.Equals(prepared.GameRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前游戏目录与安装预览时不同，请重新准备 MOD。");

        return GameEnvironment.WithLock(root, () =>
        {
            if (GameEnvironment.Running(root))
                throw new InvalidOperationException("游戏正在运行，请先退出后再安装 MOD。");

            InventorySnapshot currentInventory = CaptureInventory(root);
            if (!currentInventory.Token.Equals(prepared.InitialInventoryToken, StringComparison.Ordinal))
                throw new InvalidOperationException("下载期间本地 MOD 环境发生变化，已取消安装；请刷新模组列表后重试。");

            GameEnvironment.NoLinks(prepared.PayloadDirectory);
            var currentPayload = ModManager.FileSnapshot(prepared.PayloadDirectory);
            if (!SameSnapshot(currentPayload, prepared.PayloadSnapshot))
                throw new InvalidDataException("待安装文件在预览后发生变化，已取消安装。");

            ManagedMod[] matches = currentInventory.Mods
                .Where(mod => mod.Id.Equals(prepared.ModId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException("本地存在重复 MOD ID，已取消安装。");

            if (prepared.IsUnchanged)
            {
                if (matches.Length != 1 ||
                    !matches[0].Folder.Equals(prepared.ExistingFolder, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("原 MOD 已变化，请重新准备安装。");

                return new WorkshopModInstallResult(
                    prepared.WorkshopId, prepared.ModId, prepared.ModName, prepared.Version,
                    matches[0].Folder, null, false);
            }

            string modsRoot = Path.GetFullPath(Path.Combine(root, "mods"));
            GameEnvironment.NoLinks(modsRoot);
            Directory.CreateDirectory(modsRoot);

            if (prepared.IsUpdate)
            {
                if (matches.Length != 1)
                    throw new InvalidOperationException("原 MOD 已不存在或 ID 已变化，已取消替换。");

                ManagedMod existing = matches[0];
                if (!existing.Enabled || existing.Storage is not null ||
                    !existing.Folder.Equals(prepared.ExistingFolder, StringComparison.OrdinalIgnoreCase) ||
                    !existing.Folder.Equals(prepared.DestinationDirectory, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetDirectoryName(existing.Folder)!.Equals(modsRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetDirectoryName(existing.Manifest)!.Equals(existing.Folder, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("原 MOD 的位置或状态已变化，已取消替换。");

                string backupParent = BackupRoot(root, prepared.ModId);
                GameEnvironment.NoLinks(backupParent);
                Directory.CreateDirectory(backupParent);
                string backup = Path.Combine(
                    backupParent,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N"));

                Directory.Move(existing.Folder, backup);
                try
                {
                    Directory.Move(prepared.PayloadDirectory, existing.Folder);
                }
                catch
                {
                    if (!Directory.Exists(existing.Folder))
                    {
                        try
                        {
                            Directory.Move(backup, existing.Folder);
                        }
                        catch (Exception rollbackError)
                        {
                            throw new IOException(
                                "创意工坊 MOD 更新失败且自动回滚失败。旧版本仍保存在：" + backup,
                                rollbackError);
                        }
                    }
                    throw;
                }

                return new WorkshopModInstallResult(
                    prepared.WorkshopId, prepared.ModId, prepared.ModName, prepared.Version,
                    existing.Folder, backup, true);
            }

            if (matches.Length != 0)
                throw new InvalidOperationException("下载期间出现了同 ID MOD，已取消新增安装。");

            string expectedParent = Path.GetFullPath(modsRoot);
            if (!Path.GetDirectoryName(prepared.DestinationDirectory)!.Equals(
                    expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("新 MOD 安装路径不在游戏 mods 目录内。");
            if (Directory.Exists(prepared.DestinationDirectory) || File.Exists(prepared.DestinationDirectory))
                throw new IOException("目标安装目录已被占用，未覆盖任何文件。");

            Directory.Move(prepared.PayloadDirectory, prepared.DestinationDirectory);
            return new WorkshopModInstallResult(
                prepared.WorkshopId, prepared.ModId, prepared.ModName, prepared.Version,
                prepared.DestinationDirectory, null, true);
        });
    }

    public static void Cleanup(WorkshopModPrepared? prepared)
    {
        if (prepared is null)
            return;
        TryDeleteWorkspace(prepared.WorkspaceDirectory);
    }

    private static void TryDeleteWorkspace(string workspace)
    {
        try
        {
            if (!Directory.Exists(workspace))
                return;
            GameEnvironment.NoLinks(workspace);
            Directory.Delete(workspace, recursive: true);
        }
        catch (IOException)
        {
            // Keep the original installation result or error; a leftover staging folder is recoverable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static InventorySnapshot CaptureInventory(string root)
    {
        ModInventory inventory = ModManager.Scan(root);
        if (inventory.Issues.Count > 0)
            throw new InvalidOperationException(
                "本地存在重复 ID 或无法读取的 MOD 清单，请先处理模组列表问题：" +
                string.Join("；", inventory.Issues.Take(3)));

        var snapshots = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>();
        foreach (ManagedMod mod in inventory.Mods.OrderBy(m => m.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var files = ModManager.FileSnapshot(mod.Folder);
            snapshots[mod.Folder] = files;
            rows.Add(new
            {
                mod.Id,
                mod.Folder,
                mod.Version,
                mod.Enabled,
                mod.Storage,
                Files = files
            });
        }

        string token = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows))));
        return new InventorySnapshot(token, inventory.Mods.ToArray(), snapshots);
    }

    private static List<ModManifestCandidate> FindModManifests(string root)
    {
        var candidates = new List<ModManifestCandidate>();
        foreach (string path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            GameEnvironment.NoLinks(path);
            if (Path.GetFileName(path).Equals("assembly-manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxManifestBytes)
                continue;

            try
            {
                var candidate = ReadModManifest(path);
                if (candidate is not null)
                    candidates.Add(candidate);
            }
            catch (JsonException)
            {
                // Non-MOD JSON files are allowed inside a Workshop item.
            }
        }
        return candidates;
    }

    private static ModManifestCandidate? ReadModManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("id", out JsonElement idField) ||
            idField.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("has_dll", out JsonElement dllField) ||
            !root.TryGetProperty("has_pck", out JsonElement pckField))
            return null;

        string id = idField.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 ||
            dllField.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            pckField.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;

        bool hasDll = dllField.ValueKind == JsonValueKind.True;
        bool hasPck = pckField.ValueKind == JsonValueKind.True;
        if (!hasDll && !hasPck)
            return null;

        string payloadDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return new ModManifestCandidate(
            path,
            payloadDirectory,
            id,
            ReadString(root, "name", id),
            ReadString(root, "version", "未声明"),
            hasDll,
            hasPck);
    }

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string[] SafeFiles(string root)
    {
        GameEnvironment.NoLinks(root);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        if (files.Length == 0 || files.Length > MaxFiles)
            throw new InvalidDataException("MOD 文件数量为空或超过安全限制。");
        foreach (string file in files)
            GameEnvironment.NoLinks(file);
        return files;
    }

    private static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains(':') || relative.Contains('\0'))
            throw new InvalidDataException("MOD 文件包含不安全的相对路径。");

        string normalized = relative.Replace('\\', '/');
        string[] parts = normalized.Split('/');
        if (parts.Any(part =>
                string.IsNullOrWhiteSpace(part) ||
                part is "." or ".." ||
                part.EndsWith(' ') ||
                part.EndsWith('.') ||
                part.Any(character => char.IsControl(character) || InvalidWindowsCharacters.Contains(character))))
            throw new InvalidDataException("MOD 文件包含不安全的 Windows 文件名：" + relative);

        foreach (string part in parts)
        {
            string device = part.Split('.')[0];
            if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (device.Length == 4 &&
                 (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                  device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 device[3] is >= '1' and <= '9'))
                throw new InvalidDataException("MOD 文件使用了 Windows 保留设备名：" + relative);
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
        string prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MOD 文件路径越过安装暂存目录。");
        return candidate;
    }

    private static string SafeFolderName(string value)
    {
        string name = value.Trim();
        if (name.Length == 0 || name.Length > 100 ||
            name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(InvalidWindowsCharacters) >= 0)
            return "";

        string device = name.Split('.')[0];
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (device.Length == 4 &&
             (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             device[3] is >= '1' and <= '9'))
            return "";

        return name;
    }

    private static string BackupRoot(string root, string modId)
    {
        string safe = modId.Length is > 0 and <= 80 &&
            modId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
            !modId.StartsWith('.')
            ? modId
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modId)))[..20];
        return GameEnvironment.Child(root, "尖塔修补站_MOD备份/" + safe);
    }

    private static bool SameSnapshot(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count &&
        first.All(pair => second.TryGetValue(pair.Key, out string? value) &&
            value.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));

    private sealed record InventorySnapshot(
        string Token,
        ManagedMod[] Mods,
        Dictionary<string, Dictionary<string, string>> FileSnapshots);

    private sealed record ModManifestCandidate(
        string ManifestPath,
        string PayloadDirectory,
        string Id,
        string Name,
        string Version,
        bool HasDll,
        bool HasPck);
}
