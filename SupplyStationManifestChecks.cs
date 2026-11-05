using System.IO;
using System.Net.Http;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal static class SupplyStationManifestChecks
{
    const uint AppId = SpireSupplyStationClient.SupportedAppId;
    const ulong ItemId = 3830005491;
    const ulong ManifestId = 2983186950050217793;
    const uint DepotId = 2868841;
    const string Future = "2099-01-01T00:00:00Z";
    static readonly string DepotKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

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
        var requests = new List<(string Host, string Path, string Query, string UserAgent, string? Authorization, bool HasCookie)>();
        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            requests.Add(Capture(request));
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, Descriptor());
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                return Json(request, Token("http://cdn.example", "token=manifest-probe&expiration_time=4102444800"));
            if (request.RequestUri.Host == "cdn.example" &&
                request.RequestUri.AbsolutePath == $"/depot/{DepotId}/manifest/{ManifestId}/5/18446744073709551614")
                return Bytes(request, manifestZip);
            throw new InvalidOperationException("Unexpected manifest probe request: " + request.RequestUri);
        })))
        {
            var result = await client.ProbeManifestAsync(AppId, ItemId);
            Check(result.ManifestDownloaded && result.ManifestParsed,
                "authorized CDN manifest is downloaded and parsed");
            Check(result.Manifest.DepotId == DepotId && result.Manifest.ManifestId == ManifestId,
                "manifest metadata identity matches the descriptor Depot ID and Manifest ID");
            Check(result.Manifest.FileCount == 1 && result.Manifest.UniqueChunkCount == 1,
                "protobuf payload yields one file mapping and one unique Chunk");
            Check(result.Manifest.TotalFileBytes == 4 &&
                result.Manifest.TotalChunkCompressedBytes == 8 &&
                result.Manifest.TotalChunkUncompressedBytes == 4,
                "file and Chunk sizes are read from the manifest payload");
            Check(result.Manifest.FileNamesEncrypted && result.Manifest.SignatureBytes == 4,
                "manifest encryption metadata and signature section are detected");
            Check(requests.Count == 3 &&
                requests[0].Path.EndsWith("/descriptor", StringComparison.Ordinal) &&
                requests[1].Path.EndsWith("/cdn/token", StringComparison.Ordinal) &&
                requests[2].Host == "cdn.example" &&
                requests[2].Path == $"/depot/{DepotId}/manifest/{ManifestId}/5/18446744073709551614" &&
                requests[2].Query.Contains("token=manifest-probe", StringComparison.Ordinal),
                "manifest request uses the descriptor request code and the CDN token query");
            Check(requests.All(item =>
                item.UserAgent == "Sts2PatchManager/" + ManagerUpdates.CurrentVersion &&
                item.Authorization is null && !item.HasCookie),
                "descriptor, token and CDN requests use the manager User-Agent without Steam credentials or cookies");
            string serialized = JsonSerializer.Serialize(result);
            Check(!serialized.Contains(DepotKey, StringComparison.Ordinal) &&
                !serialized.Contains("18446744073709551614", StringComparison.Ordinal) &&
                !serialized.Contains("manifest-probe", StringComparison.Ordinal),
                "manifest report never exposes Depot Key, manifest request code or CDN token");
        }

        int cdnAttempts = 0;
        int tokenCalls = 0;
        bool refreshRequested = false;
        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, Descriptor());
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
            {
                tokenCalls++;
                refreshRequested |= request.RequestUri.Query.Contains("refresh=true", StringComparison.Ordinal);
                string token = refreshRequested ? "token=fresh-token&expiration_time=4102444800" :
                    "token=stale-token&expiration_time=4102444800";
                return Json(request, Token("http://cdn.example", token));
            }
            if (request.RequestUri.Host == "cdn.example")
            {
                cdnAttempts++;
                return request.RequestUri.Query.Contains("token=fresh-token", StringComparison.Ordinal)
                    ? Bytes(request, manifestZip)
                    : new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            throw new InvalidOperationException("Unexpected auth-refresh request.");
        })))
        {
            var result = await client.ProbeManifestAsync(AppId, ItemId);
            Check(result.ManifestParsed && cdnAttempts == 2 && tokenCalls == 2 && refreshRequested,
                "CDN 403 triggers one explicit token refresh and a successful manifest retry");
        }

        int mirrorRequests = 0;
        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, Descriptor(includeMirror: true));
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                return Json(request, Token("http://" + request.RequestUri.Query.Split("host=")[1].Split('&')[0],
                    "token=mirror-authorized&expiration_time=4102444800"));
            if (request.RequestUri.Host == "cdn.example")
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://mirror.example" + request.RequestUri.PathAndQuery.Split('?')[0]) }
                };
            if (request.RequestUri.Host == "mirror.example")
            {
                mirrorRequests++;
                return Bytes(request, manifestZip);
            }
            throw new InvalidOperationException("Unexpected mirror request.");
        })))
        {
            var result = await client.ProbeManifestAsync(AppId, ItemId);
            Check(result.ManifestParsed && mirrorRequests == 1,
                "HTTPS redirect to another descriptor-authorized CDN host is followed with a host-specific token");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, Descriptor());
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                return Json(request, Token("http://cdn.example", "token=manifest-probe&expiration_time=4102444800"));
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://unlisted.example/manifest") }
            };
        })))
        {
            await ExpectFailure(() => client.ProbeManifestAsync(AppId, ItemId), Check,
                "redirect to a CDN host not listed in the descriptor is rejected");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, Descriptor(manifestId: (ManifestId + 1).ToString()));
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                return Json(request, Token("http://cdn.example", "token=manifest-probe&expiration_time=4102444800"));
            return Bytes(request, manifestZip);
        })))
        {
            await ExpectFailure(() => client.ProbeManifestAsync(AppId, ItemId), Check,
                "manifest with a different ID from the descriptor is rejected");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, Descriptor());
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                return Json(request, Token("http://cdn.example", "token=manifest-probe&expiration_time=4102444800"));
            return Bytes(request, [1, 2, 3, 4, 5]);
        })))
        {
            await ExpectFailure(() => client.ProbeManifestAsync(AppId, ItemId), Check,
                "invalid or non-ZIP manifest response is rejected");
        }

        Check(true, "phase-2A manifest tests completed without downloading chunks or touching a game directory");
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
        Func<Task<SupplyStationManifestProbeResult>> action,
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

    static string Descriptor(string manifestId = "2983186950050217793", bool includeMirror = false) =>
        JsonSerializer.Serialize(new
        {
            mode = "UGC",
            appId = AppId,
            publishedFileId = ItemId.ToString(),
            title = "ManifestFixture",
            fileName = "ManifestFixture.zip",
            fileSizeBytes = 8100000L,
            contentVersion = "fixture-v1",
            manifestId,
            depotId = DepotId,
            requestCode = "18446744073709551614",
            endpoints = includeMirror
                ? new[] { new { origin = "http://cdn.example", query = (string?)null },
                          new { origin = "https://mirror.example", query = (string?)null } }
                : new[] { new { origin = "http://cdn.example", query = (string?)null } },
            depotKeyBase64 = DepotKey,
            expiresAt = Future
        });

    static string Token(string origin, string query) =>
        JsonSerializer.Serialize(new { origin, query });

    static byte[] CreateManifestZip()
    {
        byte[] chunk = BuildProto(writer =>
        {
            WriteBytes(writer, 1, Enumerable.Repeat((byte)0x11, 20).ToArray());
            WriteFixed32(writer, 2, 0x12345678);
            WriteVarintField(writer, 3, 0);
            WriteVarintField(writer, 4, 4);
            WriteVarintField(writer, 5, 8);
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
            WriteVarintField(writer, 6, 8);
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
