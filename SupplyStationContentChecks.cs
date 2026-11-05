using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

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
    static readonly byte[] ChunkId = Enumerable.Repeat((byte)0x11, 20).ToArray();
    static readonly byte[] RawChunk = [1, 2, 3, 4, 5, 6, 7, 8];

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

    static string Descriptor() =>
        JsonSerializer.Serialize(new
        {
            mode = "UGC",
            appId = AppId,
            publishedFileId = ItemId.ToString(),
            title = "ChunkFixture",
            fileName = "ChunkFixture.zip",
            fileSizeBytes = 4L,
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
            WriteFixed32(writer, 2, 0x12345678);
            WriteVarintField(writer, 3, 0);
            WriteVarintField(writer, 4, 4);
            WriteVarintField(writer, 5, (ulong)RawChunk.Length);
        });
        byte[] file = BuildProto(writer =>
        {
            WriteString(writer, 1, "ZmlsZQ==");
            WriteVarintField(writer, 2, 4);
            WriteVarintField(writer, 3, 0);
            WriteBytes(writer, 5, new byte[20]);
            WriteBytes(writer, 6, chunk);
        });
        byte[] payload = BuildProto(writer => WriteBytes(writer, 1, file));
        byte[] metadata = BuildProto(writer =>
        {
            WriteVarintField(writer, 1, DepotId);
            WriteVarintField(writer, 2, ManifestId);
            WriteVarintField(writer, 3, 123456);
            WriteVarintField(writer, 4, 1);
            WriteVarintField(writer, 5, 4);
            WriteVarintField(writer, 6, (ulong)RawChunk.Length);
            WriteVarintField(writer, 7, 1);
            WriteVarintField(writer, 8, 0x12345678);
            WriteVarintField(writer, 9, 0x12345678);
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
