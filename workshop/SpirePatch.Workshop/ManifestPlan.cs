using SteamKit2;
using SteamKit2.CDN;
using System.Security.Cryptography;

namespace SpirePatch.Workshop;

public sealed record PlannedFile(string Path, long Size, byte[] Sha1, IReadOnlyList<DepotManifest.ChunkData> Chunks);
public sealed record ManifestPlan(IReadOnlyList<PlannedFile> Files, long TotalBytes)
{
    public static ManifestPlan Create(DepotManifest manifest, ulong expectedManifest, DownloadOptions options)
    {
        if (manifest.DepotID != WorkshopInput.AppId || manifest.ManifestGID != expectedManifest || manifest.FilenamesEncrypted)
            throw new WorkshopException("InvalidManifest", "Manifest depot/ID 不匹配或文件名仍加密。");
        if (manifest.Files == null || manifest.Files.Count > options.MaxFiles)
            throw new WorkshopException("SizeLimit", "Manifest 文件数量无效或超过上限。");
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var files = new List<PlannedFile>(); long total = 0;
        foreach (var f in manifest.Files)
        {
            var path = TestSandbox.NormalizeRelative(f.FileName);
            bool directory = f.Flags.HasFlag(EDepotFileFlag.Directory);
            if (!string.IsNullOrEmpty(f.LinkTarget) || f.Flags.HasFlag(EDepotFileFlag.Symlink))
                throw new WorkshopException("UnsafePath", "Manifest 含符号链接，不下载：" + path);
            if (!paths.TryAdd(path, directory)) throw new WorkshopException("UnsafePath", "Manifest 存在大小写冲突或重复路径：" + path);
            if (directory) continue;
            if (f.TotalSize > (ulong)options.MaxBytes || f.FileHash.Length != 20)
                throw new WorkshopException("InvalidManifest", "缺少 SHA-1 或文件大小超过上限：" + path);
            total = checked(total + (long)f.TotalSize);
            if (total > options.MaxBytes) throw new WorkshopException("SizeLimit", "Manifest 总大小超过上限。");
            ulong offset = 0;
            var chunks = f.Chunks.OrderBy(c => c.Offset).ToArray();
            foreach (var chunk in chunks)
            {
                if (chunk.ChunkID is not { Length: 20 } || chunk.Offset != offset || chunk.UncompressedLength is 0 or > 16 * 1024 * 1024
                    || chunk.CompressedLength is 0 or > 20 * 1024 * 1024)
                    throw new WorkshopException("InvalidManifest", "文件块越界、重叠、留洞或大小无效：" + path);
                offset = checked(offset + chunk.UncompressedLength);
            }
            if (offset != f.TotalSize) throw new WorkshopException("InvalidManifest", "Chunk 未精确覆盖文件：" + path);
            files.Add(new(path, (long)f.TotalSize, f.FileHash.ToArray(), chunks));
        }
        foreach (var path in paths.Keys)
        {
            var parts = path.Split('/');
            for (int n = 1; n < parts.Length; n++)
                if (paths.TryGetValue(string.Join('/', parts.Take(n)), out var isDirectory) && !isDirectory)
                    throw new WorkshopException("UnsafePath", "Manifest 文件与目录冲突：" + path);
        }
        if (files.Count == 0 || (ulong)total != manifest.TotalUncompressedSize)
            throw new WorkshopException("InvalidManifest", "Manifest 总大小不匹配或没有文件。");
        return new(files, total);
    }

    public static bool ValidChunk(byte[] bytes, DepotManifest.ChunkData chunk) => bytes.Length == chunk.UncompressedLength
        && DepotChunk.AdlerHash(bytes) == chunk.Checksum
        && SHA1.HashData(bytes).AsSpan().SequenceEqual(chunk.ChunkID);

    public async Task<IReadOnlyList<VerifiedFile>> AssembleAsync(TestSandbox sandbox, string stage,
        Func<DepotManifest.ChunkData, CancellationToken, Task<byte[]>> download,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var verified = new List<VerifiedFile>(); long done = 0;
        foreach (var file in Files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = stage + "/payload/" + file.Path;
            var target = sandbox.Child(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(sandbox.Child(relative), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var chunk in file.Chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunkName = "cache/" + Convert.ToHexString(chunk.ChunkID!) + ".chunk";
                    byte[]? bytes = null;
                    if (File.Exists(sandbox.Child(chunkName)))
                    {
                        var cache = sandbox.Child(chunkName);
                        if (new FileInfo(cache).Length == chunk.UncompressedLength)
                        {
                            var candidate = await File.ReadAllBytesAsync(cache, ct);
                            if (ValidChunk(candidate, chunk)) bytes = candidate;
                        }
                    }
                    if (bytes == null)
                    {
                        bytes = await download(chunk, ct);
                        if (!ValidChunk(bytes, chunk)) throw new WorkshopException("IntegrityFailure", "Chunk SHA-1/Adler32/长度校验失败。");
                        await sandbox.WriteBytesAsync(chunkName, bytes, ct);
                    }
                    await output.WriteAsync(bytes, ct);
                    done += bytes.Length; progress?.Report(new("chunks", done, TotalBytes, file.Path));
                }
            }
            var hashes = await HashFileAsync(sandbox.Child(relative), file.Path, ct);
            if (hashes.Bytes != file.Size || !hashes.Sha1.Equals(Convert.ToHexString(file.Sha1), StringComparison.OrdinalIgnoreCase))
                throw new WorkshopException("IntegrityFailure", "组装文件 SHA-1 或长度不匹配：" + file.Path);
            verified.Add(hashes);
        }
        return verified;
    }

    public static async Task<VerifiedFile> HashFileAsync(string full, string relative, CancellationToken ct)
    {
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long size = 0; int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        { sha1.AppendData(buffer, 0, n); sha256.AppendData(buffer, 0, n); size += n; }
        return new(relative, size, Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(sha256.GetHashAndReset()));
    }
}
