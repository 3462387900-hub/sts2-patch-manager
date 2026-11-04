using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpirePatch.Workshop;
using SteamKit2;
using SteamKit2.CDN;

// Deterministic offline fixtures. These tests are explicitly NOT evidence of a live Steam download.
var testRoot = Path.GetFullPath(args.Single());
var sandbox = TestSandbox.Initialize(testRoot);
var passed = new List<string>();
var options = new DownloadOptions { RequestTimeout = TimeSpan.FromMilliseconds(100), Attempts = 2 };
var bytes = Encoding.UTF8.GetBytes("SpirePatch fixture, not a real workshop mod.\n");
const ulong Id = 1234, Manifest = 5678;
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> action)
{
    await action(); passed.Add(name); Console.WriteLine("PASS " + name);
}
async Task Reject(string code, Func<Task> action)
{
    try { await action(); } catch (WorkshopException e) when (e.Code == code) { return; }
    throw new Exception("Expected failure: " + code);
}
byte[] Metadata(uint app = WorkshopInput.AppId, int result = 1, long? size = null, string? url = "https://steamusercontent.com/fixture", string fileType = "0")
    => JsonSerializer.SerializeToUtf8Bytes(new { response = new { publishedfiledetails = new[] { new
    { publishedfileid = Id.ToString(), result, consumer_app_id = app, file_size = (size ?? bytes.Length).ToString(),
        time_updated = 1700000000, file_url = url, hcontent_file = Manifest.ToString(), title = "Offline fixture", visibility = 0, file_type = fileType }
    } } });
HttpResponseMessage Response(byte[] data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new ByteArrayContent(data) };
TestSandbox Sub(string name) => TestSandbox.Initialize(Path.Combine(testRoot, name));
DepotManifest MakeManifest(string path = "mod/fixture.bin") => new()
{
    DepotID = WorkshopInput.AppId, ManifestGID = Manifest, TotalUncompressedSize = (ulong)bytes.Length,
    Files = [new() { FileName = path, TotalSize = (ulong)bytes.Length, FileHash = SHA1.HashData(bytes),
        Chunks = [new(SHA1.HashData(bytes), DepotChunk.AdlerHash(bytes), 0, (uint)bytes.Length, (uint)bytes.Length)] }]
};

await Test("input accepts ID and full URL", () =>
{
    Check(WorkshopInput.Parse("3830179251") == 3830179251, "id");
    Check(WorkshopInput.Parse("https://steamcommunity.com/sharedfiles/filedetails/?id=3830179251&searchtext=x") == 3830179251, "url");
    return Task.CompletedTask;
});
await Test("input rejects misleading host, duplicate ID and overflow", async () =>
{
    foreach (var input in new[] { "0", "-1", "18446744073709551616", "https://steamcommunity.com.evil.test/sharedfiles/filedetails/?id=1", "https://steamcommunity.com/sharedfiles/filedetails/?id=1&id=2" })
        await Reject("InvalidInput", () => { WorkshopInput.Parse(input); return Task.CompletedTask; });
});
await Test("metadata validates app and ID", async () =>
{
    Check(MetadataClient.Parse(Metadata(), Id).ManifestId == Manifest, "manifest");
    await Reject("WrongApp", () => { MetadataClient.Parse(Metadata(480), Id); return Task.CompletedTask; });
    await Reject("InvalidMetadata", () => { MetadataClient.Parse(Metadata(), Id + 1); return Task.CompletedTask; });
});
await Test("metadata rejects missing/private/deleted, collection and malformed JSON", async () =>
{
    await Reject("MetadataUnavailable", () => { MetadataClient.Parse(Metadata(result: 9), Id); return Task.CompletedTask; });
    await Reject("UnsupportedItem", () => { MetadataClient.Parse(Metadata(fileType: "2"), Id); return Task.CompletedTask; });
    await Reject("InvalidMetadata", () => { MetadataClient.Parse("{}"u8.ToArray(), Id); return Task.CompletedTask; });
});
await Test("sandbox refuses adopting existing directory", () => Reject("UnsafeDirectory", () => { TestSandbox.Initialize(testRoot); return Task.CompletedTask; }));
await Test("sandbox rejects game ancestors (fixture only)", async () =>
{
    sandbox.MakeDirectory("game-fixture"); await sandbox.WriteBytesAsync("game-fixture/SlayTheSpire2.exe", [], default);
    await Reject("UnsafeDirectory", () => { TestSandbox.Initialize(sandbox.Child("game-fixture/download")); return Task.CompletedTask; });
    Check(!Directory.Exists(sandbox.Child("game-fixture/download")), "must not create child");
});
await Test("sandbox rejects traversal, ADS, device names and absolute paths", async () =>
{
    foreach (var path in new[] { "../outside", "a/../../x", "C:/x", "//server/share", "a:stream", "con.txt", "COM1", "a.", "a ", "a//b", "a\\..\\x" })
        await Reject("UnsafePath", () => { sandbox.Child(path); return Task.CompletedTask; });
});
await Test("sandbox rejects junction to outside payload", async () =>
{
    sandbox.MakeDirectory("link-target");
    if (OperatingSystem.IsWindows())
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + sandbox.Child("junction").Replace("'", "''") + "' -Target '" + sandbox.Child("link-target").Replace("'", "''") + "' | Out-Null");
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "junction fixture creation failed");
    }
    else Directory.CreateSymbolicLink(sandbox.Child("junction"), sandbox.Child("link-target"));
    await Reject("UnsafePath", () => { sandbox.Child("junction/escape.bin"); return Task.CompletedTask; });
});
await Test("sandbox serializes writers", async () =>
{
    using var held = sandbox.Lock(); await Reject("Busy", () => { using var second = sandbox.Lock(); return Task.CompletedTask; });
});
await Test("URL rejects HTTP, localhost, credentials, ports and lookalike host", async () =>
{
    foreach (var url in new[] { "http://steamusercontent.com/x", "https://127.0.0.1/x", "https://steamusercontent.com.evil.test/x", "https://user@steamusercontent.com/x", "https://steamusercontent.com:8080/x" })
        await Reject("UnsafeUrl", () => { SteamHttp.ValidateUrl(url); return Task.CompletedTask; });
});
await Test("direct fixture downloads, hashes and reuses without second content GET", async () =>
{
    int gets = 0;
    using var http = new SteamHttp(options, new FakeHandler((r, _) =>
    {
        if (r.Method == HttpMethod.Post) return Task.FromResult(Response(Metadata()));
        gets++; return Task.FromResult(Response(bytes));
    }));
    var box = Sub("direct"); var downloader = new WorkshopDownloader(http, options);
    var first = await downloader.DownloadAsync(Id.ToString(), box, null, default);
    Check(!first.Reused && first.Receipt.Files[0].Sha256 == Convert.ToHexString(SHA256.HashData(bytes)), "hash");
    var second = await downloader.DownloadAsync(Id.ToString(), box, null, default);
    Check(second.Reused && gets == 1, "reuse");
    await File.WriteAllTextAsync(Path.Combine(first.Directory, "content.bin"), "tampered");
    await Reject("ExistingContentChanged", () => downloader.DownloadAsync(Id.ToString(), box, null, default));
    Check(await File.ReadAllTextAsync(Path.Combine(first.Directory, "content.bin")) == "tampered", "must not overwrite");
});
await Test("wrong app is rejected before content GET", async () =>
{
    int gets = 0;
    using var http = new SteamHttp(options, new FakeHandler((r, _) => { if (r.Method == HttpMethod.Get) gets++; return Task.FromResult(Response(Metadata(480))); }));
    await Reject("WrongApp", () => new WorkshopDownloader(http, options).DownloadAsync(Id.ToString(), Sub("wrong-app"), null, default));
    Check(gets == 0, "unexpected content GET");
});
await Test("HTTP 503 retries and then succeeds", async () =>
{
    int requests = 0;
    using var http = new SteamHttp(options, new FakeHandler((_, _) => Task.FromResult(++requests == 1 ? Response([], HttpStatusCode.ServiceUnavailable) : Response(bytes))));
    Check((await http.BytesAsync(new("https://steamusercontent.com/fixture"), 1024, default)).SequenceEqual(bytes) && requests == 2, "retry");
});
await Test("HTTP 403 stops without retry", async () =>
{
    int requests = 0;
    using var http = new SteamHttp(options, new FakeHandler((_, _) => { requests++; return Task.FromResult(Response([], HttpStatusCode.Forbidden)); }));
    await Reject("AccessDenied", () => http.BytesAsync(new("https://steamusercontent.com/fixture"), 1024, default));
    Check(requests == 1, "must not retry denied request");
});
await Test("HTTP timeout has bounded retries", async () =>
{
    int requests = 0;
    using var http = new SteamHttp(options, new FakeHandler(async (_, ct) => { requests++; await Task.Delay(10000, ct); return Response([]); }));
    await Reject("NetworkTimeout", () => http.BytesAsync(new("https://steamusercontent.com/fixture"), 1024, default));
    Check(requests == 2, "timeout retry bound");
});
await Test("HTTP cancellation stops promptly without retry", async () =>
{
    int requests = 0; using var cts = new CancellationTokenSource(30);
    using var http = new SteamHttp(options, new FakeHandler(async (_, ct) => { requests++; await Task.Delay(10000, ct); return Response([]); }));
    try { await http.BytesAsync(new("https://steamusercontent.com/fixture"), 1024, cts.Token); throw new Exception("cancel missing"); }
    catch (OperationCanceledException) { Check(requests == 1, "cancel retry"); }
});
await Test("redirect validation blocks SSRF before request", async () =>
{
    int requests = 0;
    using var http = new SteamHttp(options, new FakeHandler((_, _) => { requests++; var r = Response([], HttpStatusCode.Found); r.Headers.Location = new("https://127.0.0.1/secret"); return Task.FromResult(r); }));
    await Reject("UnsafeUrl", () => http.BytesAsync(new("https://steamusercontent.com/fixture"), 1024, default));
    Check(requests == 1, "SSRF second request");
});
await Test("size mismatch never publishes completed content", async () =>
{
    using var http = new SteamHttp(options, new FakeHandler((r, _) => Task.FromResult(Response(r.Method == HttpMethod.Post ? Metadata(size: bytes.Length + 1) : bytes))));
    var box = Sub("size-mismatch");
    await Reject("IntegrityFailure", () => new WorkshopDownloader(http, options).DownloadAsync(Id.ToString(), box, null, default));
    Check(!Directory.Exists(box.Child("completed")), "premature completion");
    Check(Directory.GetFiles(box.Child(".incomplete"), "failure.json", SearchOption.AllDirectories).Length == 1, "quarantine marker");
});
await Test("truncated HTTP body retries, isolates failure and deletes parts", async () =>
{
    int requests = 0;
    using var http = new SteamHttp(options, new FakeHandler((r, _) =>
    {
        if (r.Method == HttpMethod.Post) return Task.FromResult(Response(Metadata()));
        requests++;
        var response = Response([]); response.Content = new StreamContent(new MemoryStream(bytes[..10]));
        response.Content.Headers.ContentLength = bytes.Length; return Task.FromResult(response);
    }));
    var box = Sub("truncated");
    await Reject("NetworkFailure", () => new WorkshopDownloader(http, options).DownloadAsync(Id.ToString(), box, null, default));
    Check(requests == 2 && !Directory.Exists(box.Child("completed")), "truncated retries/publication");
    Check(Directory.GetFiles(box.Root, "*.part", SearchOption.AllDirectories).Length == 0, "partial files leaked");
});
await Test("cancellation while streaming isolates output", async () =>
{
    var box = Sub("cancel-stream"); using var cts = new CancellationTokenSource();
    using var http = new SteamHttp(options, new FakeHandler((r, _) => Task.FromResult(Response(r.Method == HttpMethod.Post ? Metadata() : bytes))));
    try
    {
        await new WorkshopDownloader(http, options).DownloadAsync(Id.ToString(), box, new ProgressInline(p => { if (p.Stage == "http") cts.Cancel(); }), cts.Token);
        throw new Exception("cancel missing");
    }
    catch (OperationCanceledException) { Check(!Directory.Exists(box.Child("completed")), "cancelled content published"); }
});
await Test("manifest rejects unsafe paths, symlinks and case collisions", async () =>
{
    await Reject("UnsafePath", () => { ManifestPlan.Create(MakeManifest("../escape"), Manifest, options); return Task.CompletedTask; });
    var link = MakeManifest(); link.Files![0].LinkTarget = "../escape";
    await Reject("UnsafePath", () => { ManifestPlan.Create(link, Manifest, options); return Task.CompletedTask; });
    var dup = MakeManifest(); dup.Files!.Add(MakeManifest("MOD/FIXTURE.BIN").Files![0]);
    await Reject("UnsafePath", () => { ManifestPlan.Create(dup, Manifest, options); return Task.CompletedTask; });
});
await Test("manifest rejects gaps, overlaps, wrong depot and missing hashes", async () =>
{
    foreach (var change in new Action<DepotManifest>[] { m => m.DepotID = 480, m => m.Files![0].Chunks[0].Offset = 1, m => m.Files![0].FileHash = [], m => m.Files![0].Chunks.Add(m.Files[0].Chunks[0]) })
    {
        var manifest = MakeManifest(); change(manifest);
        await Reject("InvalidManifest", () => { ManifestPlan.Create(manifest, Manifest, options); return Task.CompletedTask; });
    }
});
await Test("chunks assemble with strict SHA-1 and resume after interruption", async () =>
{
    var box = Sub("chunks"); var plan = ManifestPlan.Create(MakeManifest(), Manifest, options); int downloads = 0;
    Task<byte[]> Fetch(DepotManifest.ChunkData _, CancellationToken ct) { downloads++; return Task.FromResult(bytes); }
    using var cts = new CancellationTokenSource();
    try { await plan.AssembleAsync(box, ".incomplete/cancel", Fetch, new ProgressInline(_ => cts.Cancel()), cts.Token); throw new Exception("cancel missing"); }
    catch (OperationCanceledException) { }
    var output = await plan.AssembleAsync(box, ".incomplete/retry", Fetch, null, default);
    Check(downloads == 1 && output[0].Bytes == bytes.Length, "verified chunk resume");
    var chunkFile = box.Child("cache/" + Convert.ToHexString(SHA1.HashData(bytes)) + ".chunk");
    await File.WriteAllBytesAsync(chunkFile, new byte[bytes.Length]);
    await plan.AssembleAsync(box, ".incomplete/repair", Fetch, null, default);
    Check(downloads == 2, "corrupt cache must re-download");
});
await Test("corrupt chunk and incorrect final SHA-1 fail closed", async () =>
{
    var plan = ManifestPlan.Create(MakeManifest(), Manifest, options);
    await Reject("IntegrityFailure", () => plan.AssembleAsync(Sub("bad-chunk"), ".incomplete/test", (_, _) => Task.FromResult(new byte[bytes.Length]), null, default));
    var wrong = MakeManifest(); wrong.Files![0].FileHash = new byte[20];
    await Reject("IntegrityFailure", () => ManifestPlan.Create(wrong, Manifest, options).AssembleAsync(Sub("bad-file"), ".incomplete/test", (_, _) => Task.FromResult(bytes), null, default));
});
await Test("Steam denied result is never treated as a usable zero request code", () => Reject("AccessDenied", () => { AnonymousSession.RequireOk("ManifestRequestCode", EResult.AccessDenied); return Task.CompletedTask; }));

await Test("SteamKit decrypts encrypted ZIP chunk; wrong checksum is rejected", () =>
{
    // Synthetic encrypted depot chunk, no Steam data and no downloaded executable.
    using var zipped = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(zipped, System.IO.Compression.ZipArchiveMode.Create, true))
    { using var entry = zip.CreateEntry("chunk").Open(); entry.Write(bytes); }
    using var aes = Aes.Create(); aes.Key = RandomNumberGenerator.GetBytes(32); aes.IV = RandomNumberGenerator.GetBytes(16);
    var encrypted = aes.EncryptEcb(aes.IV, PaddingMode.None).Concat(aes.EncryptCbc(zipped.ToArray(), aes.IV, PaddingMode.PKCS7)).ToArray();
    var chunk = MakeManifest().Files![0].Chunks[0]; var output = new byte[bytes.Length];
    Check(DepotChunk.Process(chunk, encrypted, output, aes.Key) == bytes.Length && output.SequenceEqual(bytes), "decryption");
    chunk.Checksum++;
    try { DepotChunk.Process(chunk, encrypted, output, aes.Key); throw new Exception("corrupt checksum accepted"); }
    catch (InvalidDataException) { }
    return Task.CompletedTask;
});
await Test("CDN completed fixture revalidates against trusted manifest", async () =>
{
    var box = Sub("cdn-repeat"); var plan = ManifestPlan.Create(MakeManifest(), Manifest, options);
    const string destination = "completed/1234/5678";
    var files = await plan.AssembleAsync(box, destination, (_, _) => Task.FromResult(bytes), null, default);
    var metadata = MetadataClient.Parse(Metadata(url: null), Id);
    await box.WriteJsonAsync(destination + "/receipt.json", new DownloadReceipt(metadata, "fixture", "fixture", DateTimeOffset.UtcNow, files));
    Check((await WorkshopDownloader.TryReuseAsync(box, destination, metadata, plan, default))?.Reused == true, "reuse");
    // Even rewriting the local receipt cannot disguise corruption from a fresh manifest.
    await File.WriteAllBytesAsync(box.Child(destination + "/payload/mod/fixture.bin"), new byte[bytes.Length]);
    var changed = await ManifestPlan.HashFileAsync(box.Child(destination + "/payload/mod/fixture.bin"), "mod/fixture.bin", default);
    await box.WriteJsonAsync(destination + "/receipt.json", new DownloadReceipt(metadata, "fixture", "fixture", DateTimeOffset.UtcNow, [changed]));
    await Reject("ExistingContentChanged", () => WorkshopDownloader.TryReuseAsync(box, destination, metadata, plan, default));
});

await sandbox.WriteJsonAsync("test-results.json", new { kind = "offline-fixtures-not-live-Steam", passed = passed.Count, tests = passed, utc = DateTimeOffset.UtcNow });
Console.WriteLine($"{passed.Count} offline checks passed. No real MOD was downloaded by these tests.");

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
sealed class ProgressInline(Action<DownloadProgress> action) : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value) => action(value);
}
