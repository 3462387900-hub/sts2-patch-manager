using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpirePatch;

internal sealed record SupplyStationAssembledFileResult(
    string Path,
    long SizeBytes,
    string Sha1,
    bool Sha1Verified);

internal sealed record SupplyStationAssemblyProbeResult(
    uint AppId,
    string PublishedFileId,
    string Title,
    string ManifestId,
    uint DepotId,
    int ChunkCount,
    int ProcessedChunks,
    int FileCount,
    long TotalFileBytes,
    string OutputDirectory,
    IReadOnlyList<SupplyStationAssembledFileResult> Files,
    bool FileNamesDecrypted,
    bool ChunkChecksumsVerified,
    bool FileSha1Verified,
    bool ManifestSignatureVerified,
    bool GameBranchVerified,
    string Source,
    string SourceDisplayName,
    bool ModFilesAssembled,
    string Note);

/// <summary>
/// Reassembles a UGC depot into a new isolated output directory. The source raw
/// Chunk directory is treated as untrusted input and every Chunk and file is
/// checked against the freshly retrieved Steam Manifest.
/// </summary>
internal static class SupplyStationModAssembler
{
    private const uint DepotDirectoryFlag = 64;
    private const int MaxChunkBytes = 64 * 1024 * 1024;
    private const int MaxReportBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16
    };
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static SupplyStationAssemblyProbeResult Assemble(
        uint appId,
        string publishedFileId,
        string title,
        string manifestId,
        uint depotId,
        SteamDepotManifestData manifest,
        string rawChunkDirectory,
        string outputDirectory,
        byte[] depotKey,
        bool fileNamesWereEncrypted,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(depotKey);
        if (depotKey.Length != 32)
            throw new InvalidDataException("Steam Depot Key 必须为 32 字节。");

        string rawRoot = Path.GetFullPath(rawChunkDirectory);
        if (!Directory.Exists(rawRoot))
            throw new DirectoryNotFoundException("原始 Chunk 目录不存在。");

        RawChunkManifestReport rawReport = ReadRawChunkReport(rawRoot);
        ValidateRawChunkReport(rawReport, appId, publishedFileId, manifestId, depotId);

        var uniqueChunks = manifest.UniqueChunks();
        if (uniqueChunks.Count == 0)
            throw new InvalidDataException("Steam Manifest 不包含可组装的 Chunk。");
        if (rawReport.Chunks.Count != uniqueChunks.Count)
            throw new InvalidDataException("本地原始 Chunk 清单数量与当前 Steam Manifest 不一致。");

        var rawRecords = new Dictionary<string, RawChunkManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in rawReport.Chunks)
        {
            if (!IsSha1Hex(entry.Id) ||
                !string.Equals(entry.FileName, entry.Id + ".chunk", StringComparison.Ordinal) ||
                !rawRecords.TryAdd(entry.Id, entry))
                throw new InvalidDataException("本地 Chunk 清单包含重复 ID 或不安全的文件名。");
        }

        foreach (var chunk in uniqueChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rawRecords.TryGetValue(chunk.Id, out var entry))
                throw new InvalidDataException($"本地缺少 Manifest 所需的原始 Chunk：{chunk.Id}。");
            if (entry.CompressedLength != chunk.CompressedLength)
                throw new InvalidDataException($"Chunk {chunk.Id} 的本地长度记录与 Manifest 不一致。");
        }

        string finalDirectory = Path.GetFullPath(outputDirectory);
        string? parentDirectory = Path.GetDirectoryName(finalDirectory);
        string leafName = Path.GetFileName(finalDirectory);
        if (string.IsNullOrWhiteSpace(parentDirectory) ||
            string.IsNullOrWhiteSpace(leafName) ||
            string.Equals(finalDirectory, Path.GetPathRoot(finalDirectory), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MOD 输出目录必须是一个具体的子目录。", nameof(outputDirectory));

        string rawPrefix = Path.TrimEndingDirectorySeparator(rawRoot) + Path.DirectorySeparatorChar;
        if (finalDirectory.Equals(rawRoot, StringComparison.OrdinalIgnoreCase) ||
            finalDirectory.StartsWith(rawPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("MOD 输出目录不能位于原始 Chunk 目录内部。");

        if (File.Exists(finalDirectory))
            throw new IOException("MOD 输出路径已经被同名文件占用。");
        if (Directory.Exists(finalDirectory) && Directory.EnumerateFileSystemEntries(finalDirectory).Any())
            throw new IOException("MOD 输出目录必须不存在或为空；为避免覆盖，不会修改已有文件。");

        Directory.CreateDirectory(parentDirectory);
        string stagingDirectory = Path.Combine(
            parentDirectory,
            "." + leafName + ".spire-assemble-" + Guid.NewGuid().ToString("N"));
        string processedDirectory = Path.Combine(stagingDirectory, ".spire-processed-chunks");
        Directory.CreateDirectory(processedDirectory);

        try
        {
            string rawChunksDirectory = Path.Combine(rawRoot, "chunks");
            if (!Directory.Exists(rawChunksDirectory))
                throw new DirectoryNotFoundException("原始 Chunk 目录中缺少 chunks 子目录。");

            int processedCount = 0;
            long totalProcessedBytes = 0;
            foreach (var chunk in uniqueChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RawChunkManifestEntry entry = rawRecords[chunk.Id];
                string rawPath = Path.Combine(rawChunksDirectory, chunk.Id + ".chunk");
                if (!File.Exists(rawPath))
                    throw new FileNotFoundException($"缺少原始 Chunk 文件：{chunk.Id}。", rawPath);

                var info = new FileInfo(rawPath);
                if (info.Length != chunk.CompressedLength || info.Length > MaxChunkBytes)
                    throw new InvalidDataException($"原始 Chunk {chunk.Id} 长度与 Manifest 不一致或超出安全限制。");

                byte[] rawBytes = File.ReadAllBytes(rawPath);
                try
                {
                    byte[] expectedSha;
                    try
                    {
                        expectedSha = Convert.FromHexString(entry.Sha256);
                    }
                    catch (FormatException error)
                    {
                        throw new InvalidDataException($"原始 Chunk {chunk.Id} 的 SHA-256 记录无效。", error);
                    }

                    if (expectedSha.Length != 32 ||
                        !CryptographicOperations.FixedTimeEquals(
                            SHA256.HashData(rawBytes), expectedSha))
                        throw new InvalidDataException($"原始 Chunk {chunk.Id} 的 SHA-256 校验失败。");

                    byte[] plain = SteamDepotChunkProcessor.Process(rawBytes, chunk, depotKey);
                    try
                    {
                        string processedPath = Path.Combine(processedDirectory, chunk.Id + ".bin");
                        using var output = new FileStream(
                            processedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        output.Write(plain);
                        output.Flush(flushToDisk: true);
                        totalProcessedBytes = checked(totalProcessedBytes + plain.LongLength);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plain);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(rawBytes);
                }

                processedCount++;
                progress?.Invoke(
                    $"Chunk 解密与解压 {processedCount}/{uniqueChunks.Count}：{chunk.Id}");
            }

            var files = AssembleFiles(
                manifest,
                processedDirectory,
                stagingDirectory,
                cancellationToken,
                progress);

            Directory.Delete(processedDirectory, recursive: true);

            long totalFileBytes = files.Sum(file => file.SizeBytes);
            var report = new
            {
                appId,
                publishedFileId,
                title,
                manifestId,
                depotId,
                chunkCount = uniqueChunks.Count,
                processedChunks = processedCount,
                processedChunkBytes = totalProcessedBytes,
                fileCount = files.Count,
                totalFileBytes,
                files,
                fileNamesDecrypted = fileNamesWereEncrypted,
                chunkChecksumsVerified = true,
                fileSha1Verified = true,
                manifestSignatureVerified = false,
                gameBranchVerified = false,
                modFilesAssembled = true,
                source = "spire_supply_station",
                sourceDisplayName = "尖塔补给站（Apricityx）",
                note = "Chunk 已完成 AES 解密、压缩解码和 Adler32 校验；文件已按 Manifest 偏移重组并通过 SHA-1 校验。未验证 Manifest 签名或本机游戏分支，也未安装到游戏目录。"
            };
            File.WriteAllText(
                Path.Combine(stagingDirectory, "assembly-manifest.json"),
                JsonSerializer.Serialize(report, OutputJsonOptions));

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(finalDirectory))
            {
                if (Directory.EnumerateFileSystemEntries(finalDirectory).Any())
                    throw new IOException("MOD 输出目录在组装期间被其他程序写入；为避免覆盖，未移动结果。");
                Directory.Delete(finalDirectory);
            }
            Directory.Move(stagingDirectory, finalDirectory);

            return new SupplyStationAssemblyProbeResult(
                appId,
                publishedFileId,
                title,
                manifestId,
                depotId,
                uniqueChunks.Count,
                processedCount,
                files.Count,
                totalFileBytes,
                finalDirectory,
                files,
                fileNamesWereEncrypted,
                true,
                true,
                false,
                false,
                "spire_supply_station",
                "尖塔补给站（Apricityx）",
                true,
                "Chunk 已完成 AES 解密、压缩解码和 Adler32 校验；文件已按 Manifest 偏移重组并通过 SHA-1 校验。未验证 Manifest 签名或本机游戏分支，也未写入游戏目录。");
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch
            {
                // Preserve the original processing failure.
            }
            throw;
        }
    }

    private static List<SupplyStationAssembledFileResult> AssembleFiles(
        SteamDepotManifestData manifest,
        string processedDirectory,
        string stagingDirectory,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        var prepared = new List<(SteamDepotManifestFile File, string RelativePath, bool IsDirectory)>();
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(file.LinkTarget))
                throw new InvalidDataException("Manifest 包含符号链接条目；当前安全组装阶段不创建符号链接，已停止以避免生成不完整 MOD。");

            bool trailingSlash = file.Path.EndsWith("/", StringComparison.Ordinal);
            string relative = ValidateRelativePath(file.Path, allowTrailingSlash: true);
            bool hasChildren = manifest.Files.Any(other =>
                !ReferenceEquals(other, file) &&
                other.Path.Replace('\\', '/').StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase));
            bool isDirectory = (file.Flags & DepotDirectoryFlag) != 0 || trailingSlash || hasChildren;
            prepared.Add((file, relative, isDirectory));
        }

        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in prepared)
        {
            var targetSet = item.IsDirectory ? directoryPaths : filePaths;
            if (!targetSet.Add(item.RelativePath))
                throw new InvalidDataException($"Manifest 包含大小写冲突或重复的输出路径：{item.RelativePath}。");
        }
        if (filePaths.Overlaps(directoryPaths))
            throw new InvalidDataException("Manifest 将同一个输出路径同时声明为文件和目录。");

        foreach (string filePath in filePaths)
        {
            string[] segments = filePath.Split('/');
            for (int i = 1; i < segments.Length; i++)
            {
                string parent = string.Join("/", segments.Take(i));
                if (filePaths.Contains(parent))
                    throw new InvalidDataException($"Manifest 文件路径存在父子冲突：{parent}。");
            }
        }

        foreach (var item in prepared.Where(item => item.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = ResolveSafeOutputPath(stagingDirectory, item.RelativePath);
            Directory.CreateDirectory(target);
        }

        var results = new List<SupplyStationAssembledFileResult>();
        foreach (var item in prepared.Where(item => !item.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SteamDepotManifestFile file = item.File;
            if (file.ShaContent.Length != 20)
                throw new InvalidDataException($"Manifest 文件缺少有效 SHA-1：{item.RelativePath}。");
            if (file.Size < 0)
                throw new InvalidDataException($"Manifest 文件大小无效：{item.RelativePath}。");

            var orderedChunks = file.Chunks.OrderBy(chunk => chunk.Offset).ToArray();
            ulong nextOffset = 0;
            foreach (var chunk in orderedChunks)
            {
                if (chunk.Offset != nextOffset)
                    throw new InvalidDataException($"Manifest 文件 Chunk 偏移不连续：{item.RelativePath}。");
                nextOffset = checked(nextOffset + chunk.UncompressedLength);
            }
            if (nextOffset != (ulong)file.Size)
                throw new InvalidDataException($"Manifest 文件 Chunk 未完整覆盖文件：{item.RelativePath}。");

            string targetPath = ResolveSafeOutputPath(stagingDirectory, item.RelativePath);
            string? parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            byte[] actualSha;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            using (var output = new FileStream(
                       targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 64 * 1024, FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[64 * 1024];
                foreach (var chunk in orderedChunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string processedPath = Path.Combine(processedDirectory, chunk.Id + ".bin");
                    if (!File.Exists(processedPath))
                        throw new FileNotFoundException($"缺少已处理 Chunk：{chunk.Id}。", processedPath);

                    using var input = new FileStream(
                        processedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: buffer.Length, FileOptions.SequentialScan);
                    long copied = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        copied = checked(copied + read);
                        if (copied > chunk.UncompressedLength)
                            throw new InvalidDataException($"已处理 Chunk {chunk.Id} 超出 Manifest 声明长度。");
                        output.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                    }
                    if (copied != chunk.UncompressedLength)
                        throw new InvalidDataException($"已处理 Chunk {chunk.Id} 长度不足。");
                }

                output.Flush(flushToDisk: true);
                if (output.Length != file.Size)
                    throw new InvalidDataException($"组装文件长度不匹配：{item.RelativePath}。");
                actualSha = hash.GetHashAndReset();
            }

            if (!CryptographicOperations.FixedTimeEquals(actualSha, file.ShaContent))
                throw new InvalidDataException(
                    $"组装文件 SHA-1 校验失败：{item.RelativePath}（expected={Convert.ToHexString(file.ShaContent)} actual={Convert.ToHexString(actualSha)}）。");

            results.Add(new SupplyStationAssembledFileResult(
                item.RelativePath,
                file.Size,
                Convert.ToHexString(actualSha),
                true));
            progress?.Invoke($"文件重组并校验成功：{item.RelativePath}（{file.Size:N0} 字节）。");
        }

        if (results.Count == 0)
            throw new InvalidDataException("Manifest 中没有可组装的普通文件。");
        return results;
    }

    private static RawChunkManifestReport ReadRawChunkReport(string rawRoot)
    {
        string reportPath = Path.Combine(rawRoot, "chunk-manifest.json");
        if (!File.Exists(reportPath))
            throw new FileNotFoundException("原始 Chunk 目录缺少 chunk-manifest.json。", reportPath);

        var info = new FileInfo(reportPath);
        if (info.Length <= 0 || info.Length > MaxReportBytes)
            throw new InvalidDataException("原始 Chunk 清单为空或超过 8 MiB。");

        string json = File.ReadAllText(reportPath);
        return JsonSerializer.Deserialize<RawChunkManifestReport>(json, ReportJsonOptions)
            ?? throw new InvalidDataException("原始 Chunk 清单 JSON 无效。");
    }

    private static void ValidateRawChunkReport(
        RawChunkManifestReport report,
        uint appId,
        string publishedFileId,
        string manifestId,
        uint depotId)
    {
        if (report.AppId != appId ||
            !string.Equals(report.PublishedFileId, publishedFileId, StringComparison.Ordinal) ||
            !string.Equals(report.ManifestId, manifestId, StringComparison.Ordinal) ||
            report.DepotId != depotId ||
            !report.RawChunksDownloaded ||
            report.ChunkDecryptionPerformed ||
            report.ModFilesAssembled ||
            !string.Equals(report.Source, "spire_supply_station", StringComparison.Ordinal))
            throw new InvalidDataException("原始 Chunk 清单与当前 Workshop 条目或 Manifest 不匹配，或不是未处理的原始 Chunk 报告。");
        if (report.Chunks is null || report.Chunks.Count == 0)
            throw new InvalidDataException("原始 Chunk 清单没有 Chunk 记录。");
    }

    private static bool IsSha1Hex(string? value)
    {
        if (value is null || value.Length != 40)
            return false;
        return value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

    private static string ValidateRelativePath(string rawPath, bool allowTrailingSlash)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > 2048)
            throw new InvalidDataException("Manifest 包含空或过长的文件路径。");

        string normalized = rawPath.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.Contains(':') ||
            normalized.Contains('\0') ||
            Path.IsPathRooted(normalized))
            throw new InvalidDataException($"Manifest 包含绝对路径或不安全路径：{rawPath}。");

        if (allowTrailingSlash)
            normalized = normalized.TrimEnd('/');
        string[] segments = normalized.Split('/');
        if (segments.Length == 0 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.Any(character =>
                    char.IsControl(character) || "<>:\"/\\|?*".Contains(character))))
            throw new InvalidDataException($"Manifest 包含不安全的 Windows 文件路径：{rawPath}。");

        foreach (string segment in segments)
        {
            string deviceName = segment.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (deviceName.Length == 4 &&
                 (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                  deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 deviceName[3] is >= '1' and <= '9'))
                throw new InvalidDataException($"Manifest 使用了 Windows 保留设备名：{rawPath}。");
        }

        return string.Join('/', segments);
    }

    private static string ResolveSafeOutputPath(string root, string relativePath)
    {
        string rootPath = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(
            rootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest 输出路径越过了隔离输出目录。");
        return candidate;
    }

    private sealed class RawChunkManifestReport
    {
        [JsonPropertyName("appId")] public uint AppId { get; set; }
        [JsonPropertyName("publishedFileId")] public string PublishedFileId { get; set; } = "";
        [JsonPropertyName("manifestId")] public string ManifestId { get; set; } = "";
        [JsonPropertyName("depotId")] public uint DepotId { get; set; }
        [JsonPropertyName("rawChunksDownloaded")] public bool RawChunksDownloaded { get; set; }
        [JsonPropertyName("chunkDecryptionPerformed")] public bool ChunkDecryptionPerformed { get; set; }
        [JsonPropertyName("modFilesAssembled")] public bool ModFilesAssembled { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("chunks")] public List<RawChunkManifestEntry> Chunks { get; set; } = [];
    }

    private sealed class RawChunkManifestEntry
    {
        public string Id { get; set; } = "";
        public uint CompressedLength { get; set; }
        public string Sha256 { get; set; } = "";
        public string FileName { get; set; } = "";
    }
}
