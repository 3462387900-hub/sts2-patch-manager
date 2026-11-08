using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.ZStandard;

namespace SpirePatch;

internal static class SupplyStationContentChecks
{
    const uint AppId = SpireSupplyStationClient.SupportedAppId;
    const ulong ItemId = 3830005491;
    const ulong ManifestId = 2983186950050217793;
    const uint DepotId = 2868841;
    const string RequestCode = "18446744073709551614";
    const string Future = "2099-01-01T00:00:00Z";
    static readonly byte[] DepotKey = Enumerable.Range(0, 32).Select(value => (byte)(value + 17)).ToArray();
    static readonly byte[] PlainChunk = Encoding.UTF8.GetBytes("Base");
    static readonly byte[] RawChunk = EncryptWithEncryptedIv(CreateZipPayload(PlainChunk), DepotKey);
    static readonly byte[] ChunkId = SHA1.HashData(RawChunk);
    static readonly byte[] FileSha1 = SHA1.HashData(PlainChunk);
    static readonly uint ChunkChecksum = SteamDepotChunkProcessor.ComputeSteamAdler32(PlainChunk);

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var lines = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition)
                throw new InvalidOperationException(description);
            lines.Add("PASS " + description);
            File.WriteAllLines(output, lines, Encoding.UTF8);
        }

        byte[] manifestZip = CreateManifestZip();
        string root = Path.Combine(Path.GetTempPath(), "SpireSupplyStationContentChecks-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, "raw-chunks");
        var requests = new List<(string Host, string Path, string Query, string UserAgent, string? Authorization, bool HasCookie)>();
        int tokenCalls = 0;

        try
        {
            using (var client = new SpireSupplyStationClient(new Handler(request =>
            {
                requests.Add(Capture(request));
                if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(request, Descriptor());
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                {
                    tokenCalls++;
                    return Json(request, Token("http://cdn.example", "token=probe-token&expiration_time=4102444800"));
                }
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath == $"/depot/{DepotId}/manifest/{ManifestId}/5/{RequestCode}")
                    return Bytes(request, manifestZip);
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath == $"/depot/{DepotId}/chunk/{Convert.ToHexString(ChunkId)}")
                    return Bytes(request, RawChunk);
                throw new InvalidOperationException("Unexpected chunk probe request: " + request.RequestUri);
            })))
            {
                var result = await client.ProbeChunksAsync(AppId, ItemId, destination, maxConcurrentChunks: 2);
                Check(result.ChunkCount == 1 && result.DownloadedChunks == 1 &&
                    result.TotalCompressedBytes == RawChunk.Length,
                    "all unique raw Chunk records are downloaded and counted");
                Check(File.ReadAllBytes(Path.Combine(destination, "chunks", Convert.ToHexString(ChunkId) + ".chunk"))
                    .SequenceEqual(RawChunk),
                    "raw Chunk bytes are written to the isolated output folder without modification");
                Check(tokenCalls == 1,
                    "one CDN token is reused for manifest and chunk requests to the same authorized host");
                Check(requests.Count == 4 &&
                    requests[0].Path.EndsWith("/descriptor", StringComparison.Ordinal) &&
                    requests[1].Path.EndsWith("/cdn/token", StringComparison.Ordinal) &&
                    requests.Any(item => item.Path.Contains("/manifest/", StringComparison.Ordinal)) &&
                    requests.Any(item => item.Path.Contains("/chunk/", StringComparison.Ordinal)),
                    "probe requests descriptor, token, manifest and the manifest-listed Chunk path");
                Check(requests.All(item =>
                    item.UserAgent == SpireSupplyStationClient.RequiredUserAgent &&
                    item.Authorization is null && !item.HasCookie),
                    "all requests use the manager User-Agent without Steam credentials or cookies");
                string metadata = await File.ReadAllTextAsync(Path.Combine(destination, "chunk-manifest.json"));
                Check(!metadata.Contains(Convert.ToBase64String(DepotKey), StringComparison.Ordinal) &&
                    !metadata.Contains(RequestCode, StringComparison.Ordinal) &&
                    !metadata.Contains("probe-token", StringComparison.Ordinal),
                    "chunk metadata report excludes Depot Key, request code and CDN token");
                Check(!result.ModFilesAssembled,
                    "raw Chunk probe explicitly reports that MOD files were not assembled");
            }

            var assemblyRequests = new List<string>();
            string assembledOutput = Path.Combine(root, "assembled-mod");
            using (var client = new SpireSupplyStationClient(new Handler(request =>
            {
                assemblyRequests.Add(request.RequestUri!.AbsolutePath);
                if (request.RequestUri.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(request, Descriptor());
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                    return Json(request, Token("http://cdn.example", "token=assembly-test&expiration_time=4102444800"));
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/manifest/", StringComparison.Ordinal))
                    return Bytes(request, manifestZip);
                throw new InvalidOperationException("Assembly mode must not redownload raw Chunk bytes.");
            })))
            {
                var assembled = await client.AssembleChunksAsync(
                    AppId, ItemId, destination, assembledOutput);
                Check(assembled.ModFilesAssembled &&
                    assembled.ChunkCount == 1 && assembled.ProcessedChunks == 1 &&
                    assembled.FileCount == 1 && assembled.FileNamesDecrypted &&
                    assembled.ChunkChecksumsVerified && assembled.FileSha1Verified,
                    "assembly mode decrypts the encrypted manifest name and verifies every Chunk and file");
                Check(File.ReadAllBytes(Path.Combine(assembledOutput, "BaseLib.txt")).SequenceEqual(PlainChunk),
                    "AES-decrypted ZIP Chunk is reassembled into the exact Manifest file bytes");
                Check(assemblyRequests.Any(path => path.Contains("/manifest/", StringComparison.Ordinal)) &&
                    !assemblyRequests.Any(path => path.Contains("/chunk/", StringComparison.Ordinal)),
                    "assembly re-fetches the current Manifest but reuses local raw Chunks without downloading them again");
                string assemblyReport = await File.ReadAllTextAsync(Path.Combine(assembledOutput, "assembly-manifest.json"));
                Check(!assemblyReport.Contains(Convert.ToBase64String(DepotKey), StringComparison.Ordinal) &&
                    !assemblyReport.Contains(RequestCode, StringComparison.Ordinal) &&
                    !assemblyReport.Contains("assembly-test", StringComparison.Ordinal),
                    "assembled output report excludes Depot Key, request code and CDN token");
            }

            TestChunkProcessorCompressionFormats(Check);

            var redirectRequests = new List<(string Host, string Scheme, int Port, string Query)>();
            int redirectTokenCalls = 0;
            using (var client = new SpireSupplyStationClient(new Handler(request =>
            {
                redirectRequests.Add((
                    request.RequestUri!.Host,
                    request.RequestUri.Scheme,
                    request.RequestUri.Port,
                    request.RequestUri.Query));
                if (request.RequestUri.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(request, Descriptor());
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                {
                    redirectTokenCalls++;
                    return Json(request, Token("http://cdn.example", "token=redirect-test&expiration_time=4102444800"));
                }
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/manifest/", StringComparison.Ordinal))
                    return Bytes(request, manifestZip);
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/chunk/", StringComparison.Ordinal))
                    return Redirect(request, "https://cache1-atl3.steamcontent.com" + request.RequestUri.AbsolutePath);
                if (request.RequestUri.Host == "cache1-atl3.steamcontent.com" &&
                    request.RequestUri.AbsolutePath.Contains("/chunk/", StringComparison.Ordinal))
                    return Bytes(request, RawChunk);
                throw new InvalidOperationException("Unexpected Steam CDN redirect test request.");
            })))
            {
                string redirectOutput = Path.Combine(root, "redirect-chunks");
                var result = await client.ProbeChunksAsync(AppId, ItemId, redirectOutput);
                var redirected = redirectRequests.Single(item => item.Host == "cache1-atl3.steamcontent.com");
                Check(result.DownloadedChunks == 1 &&
                    redirected.Scheme == "https" && redirected.Port == 443 &&
                    redirected.Query.Contains("token=redirect-test", StringComparison.Ordinal),
                    "Steam CDN follows a trusted cross-host HTTPS redirect and preserves its temporary authorization query");
                Check(redirectTokenCalls == 1,
                    "redirected CDN host reuses the authorized request query instead of requesting an unlisted host token");
            }

            var blockedRedirectRequests = new List<string>();
            string? blockedRedirectError = null;
            using (var client = new SpireSupplyStationClient(new Handler(request =>
            {
                blockedRedirectRequests.Add(request.RequestUri!.Host);
                if (request.RequestUri.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(request, Descriptor());
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                    return Json(request, Token("http://cdn.example", "token=blocked-test&expiration_time=4102444800"));
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/manifest/", StringComparison.Ordinal))
                    return Bytes(request, manifestZip);
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/chunk/", StringComparison.Ordinal))
                    return Redirect(request, "https://evil.example" + request.RequestUri.AbsolutePath);
                throw new InvalidOperationException("Untrusted redirect destination must never be requested.");
            })))
            {
                try
                {
                    await client.ProbeChunksAsync(AppId, ItemId, Path.Combine(root, "blocked-redirect"));
                }
                catch (HttpRequestException error)
                {
                    blockedRedirectError = error.Message;
                }
                Check(blockedRedirectError?.Contains("主机不在可信 Steam CDN 列表中", StringComparison.Ordinal) == true &&
                    !blockedRedirectRequests.Contains("evil.example", StringComparer.OrdinalIgnoreCase),
                    "untrusted cross-host redirects are blocked and the reason is retained in the error report");
            }

            var downgradeRequests = new List<string>();
            string? downgradeError = null;
            using (var client = new SpireSupplyStationClient(new Handler(request =>
            {
                downgradeRequests.Add(request.RequestUri!.Host);
                if (request.RequestUri.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(request, Descriptor());
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                    return Json(request, Token("http://cdn.example", "token=downgrade-test&expiration_time=4102444800"));
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/manifest/", StringComparison.Ordinal))
                    return Bytes(request, manifestZip);
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/chunk/", StringComparison.Ordinal))
                    return Redirect(request, "http://st.dl.eccdnx.com" + request.RequestUri.AbsolutePath);
                throw new InvalidOperationException("HTTP downgrade destination must never be requested.");
            })))
            {
                try
                {
                    await client.ProbeChunksAsync(AppId, ItemId, Path.Combine(root, "downgrade-redirect"));
                }
                catch (HttpRequestException error)
                {
                    downgradeError = error.Message;
                }
                Check(downgradeError?.Contains("只允许 HTTPS 默认端口 443", StringComparison.Ordinal) == true &&
                    !downgradeRequests.Contains("st.dl.eccdnx.com", StringComparer.OrdinalIgnoreCase),
                    "even a trusted Steam CDN host cannot receive a temporary authorization query over HTTP");
            }

            int refreshTokenCalls = 0;
            int chunkAttempts = 0;
            using (var client = new SpireSupplyStationClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(request, Descriptor());
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                {
                    refreshTokenCalls++;
                    bool refresh = request.RequestUri.Query.Contains("refresh=true", StringComparison.Ordinal);
                    return Json(request, Token("http://cdn.example",
                        refresh ? "token=fresh-token&expiration_time=4102444800" :
                                  "token=stale-token&expiration_time=4102444800"));
                }
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/manifest/", StringComparison.Ordinal))
                    return Bytes(request, manifestZip);
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/chunk/", StringComparison.Ordinal))
                {
                    chunkAttempts++;
                    return request.RequestUri.Query.Contains("token=fresh-token", StringComparison.Ordinal)
                        ? Bytes(request, RawChunk)
                        : new HttpResponseMessage(HttpStatusCode.Forbidden);
                }
                throw new InvalidOperationException("Unexpected token refresh request.");
            })))
            {
                string refreshOutput = Path.Combine(root, "refresh-chunks");
                var result = await client.ProbeChunksAsync(AppId, ItemId, refreshOutput);
                Check(result.DownloadedChunks == 1 && chunkAttempts == 2 && refreshTokenCalls == 2,
                    "CDN 403 triggers one refresh=true token request and a successful Chunk retry");
            }

            int wrongLengthAttempts = 0;
            using (var client = new SpireSupplyStationClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(request, Descriptor());
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                    return Json(request, Token("http://cdn.example", "token=length-test&expiration_time=4102444800"));
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/manifest/", StringComparison.Ordinal))
                    return Bytes(request, manifestZip);
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/chunk/", StringComparison.Ordinal))
                {
                    wrongLengthAttempts++;
                    return Bytes(request, RawChunk[..^1]);
                }
                throw new InvalidOperationException("Unexpected length test request.");
            })))
            {
                string failedOutput = Path.Combine(root, "failed-chunks");
                await ExpectFailure(() => client.ProbeChunksAsync(AppId, ItemId, failedOutput), Check,
                    "Chunk response shorter than the Manifest length is rejected");
                Check(wrongLengthAttempts == 3 && !Directory.Exists(failedOutput),
                    "length mismatch retries three times and leaves no partial output directory");
            }

            string protectedOutput = Path.Combine(root, "protected");
            Directory.CreateDirectory(protectedOutput);
            string sentinel = Path.Combine(protectedOutput, "keep.txt");
            await File.WriteAllTextAsync(sentinel, "keep");
            using (var client = new SpireSupplyStationClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(request, Descriptor());
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                    return Json(request, Token("http://cdn.example", "token=protected&expiration_time=4102444800"));
                if (request.RequestUri.Host == "cdn.example" &&
                    request.RequestUri.AbsolutePath.Contains("/manifest/", StringComparison.Ordinal))
                    return Bytes(request, manifestZip);
                throw new InvalidOperationException("Chunk request should not occur for a protected output directory.");
            })))
            {
                await ExpectFailure(() => client.ProbeChunksAsync(AppId, ItemId, protectedOutput), Check,
                    "non-empty output directory is rejected before any Chunk is written");
                Check(await File.ReadAllTextAsync(sentinel) == "keep",
                    "existing output files are preserved when the destination is not empty");
            }

            Check(true, "phase-2B raw Chunk download checks completed without decrypting, assembling or touching a game directory");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup must not mask the protocol assertion that failed.
            }
        }
    }

    static (string Host, string Path, string Query, string UserAgent, string? Authorization, bool HasCookie)
        Capture(HttpRequestMessage request) =>
        (request.RequestUri!.Host,
            request.RequestUri.AbsolutePath,
            request.RequestUri.Query,
            request.Headers.UserAgent.ToString(),
            request.Headers.Authorization?.ToString(),
            request.Headers.Contains("Cookie"));

    static async Task ExpectFailure(
        Func<Task<SupplyStationChunkProbeResult>> action,
        Action<bool, string> check,
        string description)
    {
        try
        {
            await action();
            check(false, description);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or
                                      InvalidOperationException or HttpRequestException or
                                      TimeoutException or IOException)
        {
            check(true, description);
        }
    }

    static HttpResponseMessage Json(HttpRequestMessage request, string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };

    static HttpResponseMessage Bytes(HttpRequestMessage request, byte[] body) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
            RequestMessage = request
        };

    static HttpResponseMessage Redirect(HttpRequestMessage request, string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            RequestMessage = request
        };
        response.Headers.Location = new Uri(location, UriKind.Absolute);
        return response;
    }

    static string Descriptor() =>
        JsonSerializer.Serialize(new
        {
            mode = "UGC",
            appId = AppId,
            publishedFileId = ItemId.ToString(),
            title = "ChunkFixture",
            fileName = "ChunkFixture.zip",
            fileSizeBytes = (long)PlainChunk.Length,
            contentVersion = "fixture-v1",
            manifestId = ManifestId.ToString(),
            depotId = DepotId,
            requestCode = RequestCode,
            endpoints = new[] { new { origin = "http://cdn.example", query = (string?)null } },
            depotKeyBase64 = Convert.ToBase64String(DepotKey),
            expiresAt = Future
        });

    static string Token(string origin, string query) =>
        JsonSerializer.Serialize(new { origin, query });

    static byte[] CreateManifestZip()
    {
        byte[] chunk = BuildProto(writer =>
        {
            WriteBytes(writer, 1, ChunkId);
            WriteFixed32(writer, 2, ChunkChecksum);
            WriteVarintField(writer, 3, 0);
            WriteVarintField(writer, 4, (ulong)PlainChunk.Length);
            WriteVarintField(writer, 5, (ulong)RawChunk.Length);
        });
        byte[] file = BuildProto(writer =>
        {
            WriteString(writer, 1, EncryptManifestName("BaseLib.txt", DepotKey));
            WriteVarintField(writer, 2, (ulong)PlainChunk.Length);
            WriteVarintField(writer, 3, 0);
            WriteBytes(writer, 5, FileSha1);
            WriteBytes(writer, 6, chunk);
        });
        byte[] payload = BuildProto(writer => WriteBytes(writer, 1, file));
        byte[] metadata = BuildProto(writer =>
        {
            WriteVarintField(writer, 1, DepotId);
            WriteVarintField(writer, 2, ManifestId);
            WriteVarintField(writer, 3, 123456);
            WriteVarintField(writer, 4, 1);
            WriteVarintField(writer, 5, (ulong)PlainChunk.Length);
            WriteVarintField(writer, 6, (ulong)RawChunk.Length);
            WriteVarintField(writer, 7, 1);
            WriteVarintField(writer, 8, ChunkChecksum);
            WriteVarintField(writer, 9, ChunkChecksum);
        });

        using var manifest = new MemoryStream();
        WriteSection(manifest, 0x71F617D0, payload);
        WriteSection(manifest, 0x1F4812BE, metadata);
        WriteSection(manifest, 0x1B81B817, [1, 2, 3, 4]);
        WriteUInt32(manifest, 0x32C415AB);

        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("manifest.bin", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            manifest.Position = 0;
            manifest.CopyTo(entryStream);
        }
        return zipBytes.ToArray();
    }

    static void TestChunkProcessorCompressionFormats(Action<bool, string> check)
    {
        foreach (var fixture in new[]
        {
            (Name: "VZip/LZMA", Payload: CreateVZipPayload(PlainChunk)),
            (Name: "VZstd/Zstandard", Payload: CreateVZstdPayload(PlainChunk))
        })
        {
            byte[] raw = EncryptWithEncryptedIv(fixture.Payload, DepotKey);
            var chunk = new SteamDepotManifestChunk(
                Convert.ToHexString(SHA1.HashData(raw)),
                SHA1.HashData(raw),
                ChunkChecksum,
                0,
                (uint)raw.Length,
                (uint)PlainChunk.Length);
            byte[] decoded = SteamDepotChunkProcessor.Process(raw, chunk, DepotKey);
            check(decoded.SequenceEqual(PlainChunk),
                $"{fixture.Name} encrypted Steam Chunk is decrypted, decompressed and Adler32-verified");
        }
    }

    static byte[] CreateZipPayload(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("chunk.bin", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(plain);
        }
        return output.ToArray();
    }

    static byte[] CreateVZipPayload(byte[] plain)
    {
        using var compressed = new MemoryStream();
        byte[] properties;
        using (var encoder = LzmaStream.Create(
                   new LzmaEncoderProperties(false, 1 << 20),
                   isLzma2: false,
                   outputStream: compressed))
        {
            properties = encoder.Properties.ToArray();
            encoder.Write(plain, 0, plain.Length);
        }

        using var output = new MemoryStream();
        WriteUInt16(output, 0x5A56);
        output.WriteByte((byte)'a');
        WriteUInt32(output, 0);
        output.WriteByte(properties[0]);
        output.Write(properties, 1, 4);
        compressed.Position = 0;
        compressed.CopyTo(output);
        WriteUInt32(output, 0);
        WriteUInt32(output, (uint)plain.Length);
        WriteUInt16(output, 0x767A);
        return output.ToArray();
    }

    static byte[] CreateVZstdPayload(byte[] plain)
    {
        using var compressor = new Compressor();
        byte[] compressed = compressor.Wrap(plain).ToArray();
        using var output = new MemoryStream();
        output.Write("VSZa"u8);
        WriteUInt32(output, 0);
        output.Write(compressed);
        WriteUInt32(output, 0);
        WriteUInt32(output, (uint)plain.Length);
        output.Write(new byte[7]);
        return output.ToArray();
    }

    static byte[] EncryptWithEncryptedIv(byte[] plain, byte[] depotKey)
    {
        byte[] iv = Enumerable.Range(0, 16).Select(index => (byte)(0xA0 + index)).ToArray();
        using var ecb = Aes.Create();
        ecb.Key = depotKey;
        ecb.Mode = CipherMode.ECB;
        ecb.Padding = PaddingMode.None;
        using var ecbEncryptor = ecb.CreateEncryptor();
        byte[] encryptedIv = ecbEncryptor.TransformFinalBlock(iv, 0, iv.Length);

        using var cbc = Aes.Create();
        cbc.Key = depotKey;
        cbc.IV = iv;
        cbc.Mode = CipherMode.CBC;
        cbc.Padding = PaddingMode.PKCS7;
        using var cbcEncryptor = cbc.CreateEncryptor();
        byte[] encryptedPayload = cbcEncryptor.TransformFinalBlock(plain, 0, plain.Length);

        byte[] result = new byte[encryptedIv.Length + encryptedPayload.Length];
        Buffer.BlockCopy(encryptedIv, 0, result, 0, encryptedIv.Length);
        Buffer.BlockCopy(encryptedPayload, 0, result, encryptedIv.Length, encryptedPayload.Length);
        CryptographicOperations.ZeroMemory(iv);
        CryptographicOperations.ZeroMemory(encryptedIv);
        CryptographicOperations.ZeroMemory(encryptedPayload);
        return result;
    }

    static string EncryptManifestName(string value, byte[] depotKey) =>
        Convert.ToBase64String(EncryptWithEncryptedIv(Encoding.UTF8.GetBytes(value), depotKey));

    static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    static byte[] BuildProto(Action<MemoryStream> write)
    {
        using var stream = new MemoryStream();
        write(stream);
        return stream.ToArray();
    }

    static void WriteString(Stream stream, int field, string value) =>
        WriteBytes(stream, field, Encoding.UTF8.GetBytes(value));

    static void WriteBytes(Stream stream, int field, byte[] bytes)
    {
        WriteVarint(stream, (ulong)((field << 3) | 2));
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteVarint(stream, (ulong)(field << 3));
        WriteVarint(stream, value);
    }

    static void WriteFixed32(Stream stream, int field, uint value)
    {
        WriteVarint(stream, (ulong)((field << 3) | 5));
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    static void WriteSection(Stream stream, uint magic, byte[] bytes)
    {
        WriteUInt32(stream, magic);
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
}
