using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SteamKit2;
using SteamKit2.CDN;

namespace SpirePatch.Workshop;

public sealed class WorkshopDownloader(SteamHttp http, DownloadOptions options, IWebProxy? proxy = null)
{
    public async Task<DownloadResult> DownloadAsync(string input, TestSandbox sandbox, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        options.Validate();
        using var held = sandbox.Lock();
        var (metadata, raw) = await new MetadataClient(http).QueryAsync(input, ct);
        await sandbox.WriteBytesAsync($"metadata/{metadata.WorkshopId}.json", raw, ct);
        if (metadata.Size > options.MaxBytes) throw new WorkshopException("SizeLimit", "条目大小超过下载上限。");
        progress?.Report(new("metadata", 0, metadata.Size, metadata.Title));
        if (metadata.FileUrl != null) return await DirectAsync(metadata, sandbox, progress, ct);
        if (metadata.ManifestId == null) throw new WorkshopException("NoDownloadSource", "元数据没有 file_url 或 hcontent_file；没有下载文件。");
        return await CdnAsync(metadata, sandbox, progress, ct);
    }

    async Task<DownloadResult> CdnAsync(WorkshopMetadata metadata, TestSandbox sandbox, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("anonymous-cm", 0, metadata.Size));
        await using var session = await AnonymousSession.ConnectAsync(http, options, proxy, ct);
        var auth = await session.AuthorizeAsync(metadata.ManifestId!.Value, ct);
        var tokens = new Dictionary<string, string>();
        async Task<byte[]> CdnBytes(string path, int limit, CancellationToken token)
        {
            WorkshopException? last = null;
            foreach (var server in auth.Servers)
            {
                if (string.IsNullOrEmpty(server.Host) || string.IsNullOrEmpty(server.VHost)) continue;
                var baseUri = SteamHttp.ValidateUrl("https://" + server.VHost + "/" + path);
                try
                {
                    options.Log?.Invoke("CDN " + server.VHost + " " + path.Split('/')[2]);
                    var url = tokens.TryGetValue(server.Host, out var cached) ? new Uri(baseUri + "?" + cached) : baseUri;
                    try { return await http.BytesAsync(url, limit, token); }
                    catch (WorkshopException e) when (e.Code == "AccessDenied" && !tokens.ContainsKey(server.Host))
                    {
                        // Normal CDN authentication, only after CM manifest and key authorization.
                        var issued = await session.CdnTokenAsync(server.Host, token);
                        tokens[server.Host] = issued;
                        return await http.BytesAsync(new Uri(baseUri + "?" + issued), limit, token);
                    }
                }
                catch (WorkshopException e) when (e.Code is "NetworkFailure" or "NetworkTimeout") { last = e; }
            }
            throw last ?? new WorkshopException("NetworkFailure", "没有可用的 Steam CDN 节点。");
        }
        var manifestZip = await CdnBytes($"depot/{WorkshopInput.AppId}/manifest/{metadata.ManifestId}/5/{auth.Code}", 16 * 1024 * 1024, ct);
        using var zip = new ZipArchive(new MemoryStream(manifestZip), ZipArchiveMode.Read);
        if (zip.Entries.Count != 1 || zip.Entries[0].Length > 32 * 1024 * 1024)
            throw new WorkshopException("InvalidManifest", "Manifest 压缩包结构或大小无效。");
        await using var entry = zip.Entries[0].Open();
        using var manifestBytes = new MemoryStream();
        var manifestBuffer = new byte[65536]; int manifestRead;
        while ((manifestRead = await entry.ReadAsync(manifestBuffer, ct)) > 0)
        {
            if (manifestBytes.Length + manifestRead > 32 * 1024 * 1024)
                throw new WorkshopException("SizeLimit", "Manifest 解压内容超过上限。");
            manifestBytes.Write(manifestBuffer, 0, manifestRead);
        }
        if (manifestBytes.Length != zip.Entries[0].Length) throw new WorkshopException("IntegrityFailure", "Manifest 解压长度不匹配。");
        var manifest = DepotManifest.Deserialize(manifestBytes.ToArray());
        if (!manifest.DecryptFilenames(auth.Key)) throw new WorkshopException("IntegrityFailure", "Manifest 文件名解密失败。");
        var plan = ManifestPlan.Create(manifest, metadata.ManifestId.Value, options);
        var destination = $"completed/{metadata.WorkshopId}/{metadata.ManifestId}";
        var existing = await TryReuseAsync(sandbox, destination, metadata, plan, ct);
        if (existing != null) return existing;
        var stage = ".incomplete/" + Guid.NewGuid().ToString("N");
        sandbox.MakeDirectory(stage);
        try
        {
            await sandbox.WriteBytesAsync(stage + "/manifest.zip", manifestZip, ct);
            var files = await plan.AssembleAsync(sandbox, stage, async (chunk, token) =>
            {
                var raw = await CdnBytes($"depot/{WorkshopInput.AppId}/chunk/{Convert.ToHexString(chunk.ChunkID!).ToLowerInvariant()}", checked((int)chunk.CompressedLength), token);
                if (raw.Length != chunk.CompressedLength) throw new WorkshopException("IntegrityFailure", "CDN Chunk 压缩长度不符。");
                var output = new byte[chunk.UncompressedLength];
                try
                {
                    var length = DepotChunk.Process(chunk, raw, output, auth.Key);
                    if (length != output.Length) throw new InvalidDataException("Chunk decoded length mismatch.");
                }
                catch (Exception e) when (e is InvalidDataException or CryptographicException)
                { throw new WorkshopException("IntegrityFailure", "SteamKit Chunk 解密/解压/校验失败。", e); }
                return output;
            }, progress, ct);
            var receipt = new DownloadReceipt(metadata, "Steam-CM-anonymous/CDN", "Manifest SHA-1 + chunk SHA-1/Adler32 + size; local SHA-256", DateTimeOffset.UtcNow, files);
            return await CompleteAsync(sandbox, stage, destination, receipt, ct);
        }
        catch (Exception e) { await QuarantineAsync(sandbox, stage, e); throw; }
    }

    async Task<DownloadResult> DirectAsync(WorkshopMetadata metadata, TestSandbox sandbox, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var uri = SteamHttp.ValidateUrl(metadata.FileUrl!);
        var version = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{metadata.UpdatedUtc.ToUnixTimeSeconds()}|{metadata.Size}|{metadata.FileUrl}")))[..24];
        var destination = $"completed/{metadata.WorkshopId}/http-{version}";
        var existing = await TryReuseAsync(sandbox, destination, metadata, null, ct);
        if (existing != null) return existing;
        var stage = ".incomplete/" + Guid.NewGuid().ToString("N");
        sandbox.MakeDirectory(stage + "/payload");
        try
        {
            var verified = await http.RetryAsync("HTTP direct download", async token =>
            {
                using var response = await http.SendAsync(uri, token);
                if (response.Content.Headers.ContentLength is long length && length != metadata.Size)
                    throw new WorkshopException("IntegrityFailure", "公开直链 Content-Length 与 Steam 元数据不符。");
                var attempt = stage + "/payload/" + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    await using (var source = await response.Content.ReadAsStreamAsync(token))
                    await using (var output = new FileStream(sandbox.Child(attempt), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[65536]; long bytes = 0; int n;
                        while ((n = await source.ReadAsync(buffer, token)) > 0)
                        {
                            bytes += n;
                            if (bytes > metadata.Size || bytes > options.MaxBytes) throw new WorkshopException("IntegrityFailure", "下载内容超过声明大小。");
                            await output.WriteAsync(buffer.AsMemory(0, n), token);
                            progress?.Report(new("http", bytes, metadata.Size, "content.bin"));
                        }
                        if (bytes != metadata.Size) throw new IOException("公开直链提前结束，大小不匹配。");
                    }
                    var hash = await ManifestPlan.HashFileAsync(sandbox.Child(attempt), "content.bin", token);
                    File.Move(sandbox.Child(attempt), sandbox.Child(stage + "/payload/content.bin"));
                    return hash;
                }
                finally { if (File.Exists(sandbox.Child(attempt))) File.Delete(sandbox.Child(attempt)); }
            }, ct);
            var receipt = new DownloadReceipt(metadata, "HTTP-file_url", "HTTPS + metadata size; local SHA-256 only (Steam supplies no authoritative file hash)", DateTimeOffset.UtcNow, [verified]);
            return await CompleteAsync(sandbox, stage, destination, receipt, ct);
        }
        catch (Exception e) { await QuarantineAsync(sandbox, stage, e); throw; }
    }

    static async Task<DownloadResult> CompleteAsync(TestSandbox sandbox, string stage, string destination, DownloadReceipt receipt, CancellationToken ct)
    {
        await sandbox.WriteJsonAsync(stage + "/receipt.json", receipt, ct);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(sandbox.Child(destination))!);
        Directory.Move(sandbox.Child(stage), sandbox.Child(destination));
        return new(sandbox.Child(destination + "/payload"), false, receipt);
    }

    static async Task QuarantineAsync(TestSandbox sandbox, string stage, Exception error)
    {
        await sandbox.WriteJsonAsync(stage + "/failure.json", new
        { status = "incomplete-do-not-install", code = (error as WorkshopException)?.Code ?? error.GetType().Name, error = error.Message, utc = DateTimeOffset.UtcNow });
    }

    public static async Task<DownloadResult?> TryReuseAsync(TestSandbox sandbox, string destination, WorkshopMetadata metadata, ManifestPlan? plan, CancellationToken ct)
    {
        if (!Directory.Exists(sandbox.Child(destination))) return null;
        var receiptPath = sandbox.Child(destination + "/receipt.json");
        if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 4 * 1024 * 1024)
            throw new WorkshopException("ExistingContentChanged", "已有下载缺少有效收据，拒绝覆盖。");
        var receipt = JsonSerializer.Deserialize<DownloadReceipt>(await File.ReadAllBytesAsync(receiptPath, ct), WorkshopJson.Options);
        if (receipt == null || receipt.Metadata != metadata || receipt.Files.Count == 0)
            throw new WorkshopException("ExistingContentChanged", "已有下载与当前元数据不同，拒绝覆盖。");
        var expectedPaths = plan?.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(["content.bin"], StringComparer.OrdinalIgnoreCase);
        if (receipt.Files.Count != expectedPaths.Count || !expectedPaths.SetEquals(receipt.Files.Select(f => f.Path)))
            throw new WorkshopException("ExistingContentChanged", "已有文件列表不匹配，拒绝覆盖。");
        var actualPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string relative)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(sandbox.Child(relative)))
            {
                var child = relative + "/" + Path.GetFileName(item);
                var checkedPath = sandbox.Child(child);
                if (Directory.Exists(checkedPath)) Walk(child);
                else actualPaths.Add(Path.GetRelativePath(sandbox.Child(destination + "/payload"), checkedPath).Replace('\\', '/'));
            }
        }
        Walk(destination + "/payload");
        if (!actualPaths.SetEquals(expectedPaths)) throw new WorkshopException("ExistingContentChanged", "已有目录增加或缺少文件，拒绝覆盖。");
        foreach (var file in receipt.Files)
        {
            var full = sandbox.Child(destination + "/payload/" + file.Path);
            if (!File.Exists(full)) throw new WorkshopException("ExistingContentChanged", "已有下载缺少文件。");
            var actual = await ManifestPlan.HashFileAsync(full, file.Path, ct);
            var trusted = plan?.Files.Single(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (actual != file || (trusted != null && (actual.Bytes != trusted.Size || actual.Sha1 != Convert.ToHexString(trusted.Sha1)))
                || (plan == null && actual.Bytes != metadata.Size))
                throw new WorkshopException("ExistingContentChanged", "已有下载校验失败，拒绝覆盖：" + file.Path);
        }
        return new(sandbox.Child(destination + "/payload"), true, receipt);
    }
}
