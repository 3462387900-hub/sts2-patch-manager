using System.Net;
using System.Text.Json;
using SpirePatch.Workshop;
using SteamKit2;
using SteamKit2.Discovery;
using SteamKit2.Internal;
using System.IO.Compression;
using SteamKit2.CDN;

// Fixed-sample round-two diagnostic. No account credentials or changes to production's AppID guard.
if (args.Length != 2) throw new ArgumentException("<new isolated directory> <HTTP proxy URL>");
var sandbox = TestSandbox.Initialize(args[0]);
var proxy = new WebProxy(new Uri(args[1]));
var options = new DownloadOptions { Attempts = 1, RequestTimeout = TimeSpan.FromSeconds(30) };
using var http = new SteamHttp(options, new SocketsHttpHandler { Proxy = proxy, AllowAutoRedirect = false, UseCookies = false });
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(4));
var events = new List<string>();
void Log(string s) { events.Add(s); Console.WriteLine(s); }
var directory = await http.BytesAsync(new("https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=0&cmtype=websockets"), 1024 * 1024, cancel.Token);
using var directoryJson = JsonDocument.Parse(directory);
var endpoints = directoryJson.RootElement.GetProperty("response").GetProperty("serverlist").EnumerateArray()
    .Where(s => s.GetProperty("type").GetString() == "websockets")
    .Select(s => s.GetProperty("endpoint").GetString()!)
    .Where(s => Uri.TryCreate("https://" + s, UriKind.Absolute, out var u) && u.Host.EndsWith(".steamserver.net") && u.Port == 443)
    .Take(2).ToArray();
SteamClient? client = null;
Task? pump = null;
CancellationTokenSource? pumpStop = null;
try
{
    foreach (var endpoint in endpoints)
    {
        client = new(SteamConfiguration.Create(b => b.WithDirectoryFetch(false).WithProtocolTypes(ProtocolTypes.WebSocket)
            .WithConnectionTimeout(TimeSpan.FromSeconds(20)).WithHttpClientFactory(_ => new HttpClient(new SocketsHttpHandler { Proxy = proxy, UseCookies = false }))));
        var callbacks = new CallbackManager(client);
        var login = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => client.GetHandler<SteamUser>()!.LogOnAnonymous());
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => login.TrySetException(new IOException("CM disconnected")));
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(c =>
        {
            Log("Anonymous logon=" + c.Result);
            if (c.Result == EResult.OK) login.TrySetResult();
            else login.TrySetException(new WorkshopException("LogonDenied", c.Result.ToString()));
        });
        pumpStop = new();
        pump = Task.Run(() => { while (!pumpStop.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); });
        Log("CM=" + endpoint);
        client.Connect(ServerRecord.CreateWebSocketServer(endpoint));
        try { await login.Task.WaitAsync(TimeSpan.FromSeconds(25), cancel.Token); break; }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            Log("Transport failure=" + e.Message); client.Disconnect(); pumpStop.Cancel(); await pump; pumpStop.Dispose();
            client = null; pumpStop = null; pump = null;
        }
    }
    if (client == null || !client.IsConnected) throw new IOException("No CM connection");
    var apps = client.GetHandler<SteamApps>()!;
    var appIds = new uint[] { 2868840, 108600, 294100 };
    var tokens = await apps.PICSGetAccessTokens(appIds, []).ToTask().WaitAsync(TimeSpan.FromSeconds(30), cancel.Token);
    var info = await apps.PICSGetProductInfo(appIds.Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.GetValueOrDefault(id))), [])
        .ToTask().WaitAsync(TimeSpan.FromSeconds(30), cancel.Token);
    var depots = new Dictionary<uint, uint>();
    foreach (var app in (info.Results ?? throw new IOException("PICS returned no results")).SelectMany(r => r.Apps).Select(p => p.Value))
    {
        var kv = app.KeyValues;
        uint workshop = kv["depots"]["workshopdepot"].AsUnsignedInteger();
        depots[app.ID] = workshop == 0 ? app.ID : workshop;
        var result = new { appId = app.ID, name = kv["common"]["name"].Value, workshopDepot = workshop,
            resolvedDepot = depots[app.ID], depotKeys = kv["depots"].Children.Select(c => c.Name).ToArray(), changeNumber = app.ChangeNumber };
        Log(JsonSerializer.Serialize(result));
        await sandbox.WriteJsonAsync($"appinfo/{app.ID}.json", result, cancel.Token);
    }
    // The two STS2 denied requests from round one are deliberately not repeated.
    foreach (var (appId, id) in new (uint, ulong)[] { (108600, 2169435993), (294100, 3459650108) })
    {
        bool manifestDownloaded = false;
        try
        {
            var raw = await http.BytesAsync(new("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/"), 2 * 1024 * 1024, cancel.Token,
                () => new FormUrlEncodedContent(new Dictionary<string, string> { ["itemcount"] = "1", ["publishedfileids[0]"] = id.ToString() }));
            await sandbox.WriteBytesAsync($"metadata/{id}.json", raw, cancel.Token);
            using var json = JsonDocument.Parse(raw);
            var d = json.RootElement.GetProperty("response").GetProperty("publishedfiledetails")[0];
            if (d.GetProperty("result").GetInt32() != 1 || d.GetProperty("consumer_app_id").GetUInt32() != appId
                || d.GetProperty("publishedfileid").GetString() != id.ToString() || d.GetProperty("visibility").GetInt32() != 0)
                throw new InvalidDataException("Unexpected/private control metadata");
            var manifestId = ulong.Parse(d.GetProperty("hcontent_file").ToString());
            if (!string.IsNullOrEmpty(d.GetProperty("file_url").GetString())) throw new InvalidDataException("Control changed to file_url; reassess separately");
            if (!depots.TryGetValue(appId, out var depot)) throw new InvalidDataException("PICS appinfo missing");
            Log($"Control {id}: app={appId}, depot={depot}, manifest={manifestId}");
            var service = client.GetHandler<SteamUnifiedMessages>()!.CreateService<ContentServerDirectory>();
            var code = await service.GetManifestRequestCode(new CContentServerDirectory_GetManifestRequestCode_Request
            { app_id = appId, depot_id = depot, manifest_id = manifestId }).ToTask().WaitAsync(TimeSpan.FromSeconds(30), cancel.Token);
            Log($"Control {id}: request-code EResult={code.Result}");
            AnonymousSession.RequireOk("ManifestRequestCode", code.Result);
            if (code.Body.manifest_request_code == 0) throw new WorkshopException("NoRequestCode", "No nonzero request code issued");
            var key = await apps.GetDepotDecryptionKey(depot, appId).ToTask().WaitAsync(TimeSpan.FromSeconds(30), cancel.Token);
            Log($"Control {id}: depot-key EResult={key.Result}");
            AnonymousSession.RequireOk("DepotKey", key.Result);
            if (key.DepotKey is not { Length: 32 }) throw new InvalidDataException("Missing depot key");
            var servers = await client.GetHandler<SteamContent>()!.GetServersForSteamPipe().WaitAsync(TimeSpan.FromSeconds(30), cancel.Token);
            var server = servers.First(s => !s.UseAsProxy && s.Protocol == Server.ConnectionProtocol.HTTPS
                && (s.Type is "CDN" or "SteamCache") && (s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(appId))
                && !string.IsNullOrEmpty(s.VHost) && s.VHost.EndsWith(".steamcontent.com", StringComparison.OrdinalIgnoreCase));
            Log($"Control {id}: CDN={server.VHost}");
            async Task<byte[]> Content(string suffix, int limit, CancellationToken ct) => await http.BytesAsync(
                SteamHttp.ValidateUrl($"https://{server.VHost}/depot/{depot}/{suffix}"), limit, ct);
            var manifestZip = await Content($"manifest/{manifestId}/5/{code.Body.manifest_request_code}", 4 * 1024 * 1024, cancel.Token);
            using var zip = new ZipArchive(new MemoryStream(manifestZip));
            if (zip.Entries.Count != 1 || zip.Entries[0].Length > 8 * 1024 * 1024) throw new InvalidDataException("Manifest ZIP size/entry limit");
            using var entry = zip.Entries[0].Open();
            using var unpacked = new MemoryStream();
            var buffer = new byte[65536]; int n;
            while ((n = await entry.ReadAsync(buffer, cancel.Token)) > 0)
            {
                if (unpacked.Length + n > 8 * 1024 * 1024) throw new InvalidDataException("Manifest expansion limit");
                unpacked.Write(buffer, 0, n);
            }
            var manifest = DepotManifest.Deserialize(unpacked.ToArray());
            if (manifest.DepotID != depot || manifest.ManifestGID != manifestId || !manifest.DecryptFilenames(key.DepotKey))
                throw new InvalidDataException("Unexpected manifest identity or encryption");
            manifestDownloaded = true;
            await sandbox.WriteBytesAsync($"manifests/{id}.zip", manifestZip, cancel.Token);
            var plan = ControlPlan(manifest); // validates actual control depot before this; never rewrites manifest identity
            Log($"Control {id}: manifest files={plan.Files.Count}, bytes={plan.TotalBytes}");
            var files = await plan.AssembleAsync(sandbox, $".incomplete/{id}", async (chunk, ct) =>
            {
                var rawChunk = await Content("chunk/" + Convert.ToHexString(chunk.ChunkID!).ToLowerInvariant(), checked((int)chunk.CompressedLength), ct);
                if (rawChunk.Length != chunk.CompressedLength) throw new InvalidDataException("Chunk compressed length mismatch");
                var output = new byte[chunk.UncompressedLength];
                if (DepotChunk.Process(chunk, rawChunk, output, key.DepotKey) != output.Length) throw new InvalidDataException("Chunk decoded length mismatch");
                return output;
            }, null, cancel.Token);
            sandbox.MakeDirectory("completed");
            Directory.Move(sandbox.Child($".incomplete/{id}"), sandbox.Child($"completed/{id}"));
            Log($"Control {id}: downloaded and verified {files.Count} files, {files.Sum(f => f.Bytes)} bytes");
            await sandbox.WriteJsonAsync($"results/{id}.json", new { id, appId, depot, manifestId = manifestId.ToString(),
                route = "anonymous CM request-code + depot-key + HTTPS CDN + SteamKit2 chunk decode",
                manifestDownloaded, filesDownloaded = true, fileCount = files.Count, bytes = files.Sum(f => f.Bytes),
                integrity = "chunk length/Adler32/SHA1 and manifest file length/SHA1; local SHA256", files }, cancel.Token);
        }
        catch (Exception e)
        {
            Environment.ExitCode = 1;
            Log($"Control {id}: {e.Message}");
            await sandbox.WriteJsonAsync($"results/{id}.json", new { id, appId, manifestDownloaded, filesDownloaded = false,
                code = (e as WorkshopException)?.Code ?? e.GetType().Name, error = e.Message }, default);
        }
    }
}

finally
{
    client?.Disconnect();
    if (pumpStop != null) { pumpStop.Cancel(); if (pump != null) await pump; pumpStop.Dispose(); }
    await sandbox.WriteJsonAsync("session.json", new { anonymous = true, utc = DateTimeOffset.UtcNow, events });
}

static ManifestPlan ControlPlan(DepotManifest manifest)
{
    // Fixed small controls only; reuses production assembly and hashing without weakening its AppID check.
    if (manifest.Files == null || manifest.Files.Count > 1000 || manifest.TotalUncompressedSize > 20 * 1024 * 1024)
        throw new InvalidDataException("Control file-count/size limit");
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var files = new List<PlannedFile>(); long total = 0;
    foreach (var file in manifest.Files)
    {
        var path = TestSandbox.NormalizeRelative(file.FileName);
        if (!names.Add(path) || file.Flags.HasFlag(EDepotFileFlag.Symlink) || !string.IsNullOrEmpty(file.LinkTarget))
            throw new InvalidDataException("Duplicate path or link");
        if (file.Flags.HasFlag(EDepotFileFlag.Directory)) continue;
        if (file.FileHash.Length != 20 || file.TotalSize > 20 * 1024 * 1024) throw new InvalidDataException("Missing file hash or oversized file");
        ulong next = 0;
        var chunks = file.Chunks.OrderBy(c => c.Offset).ToArray();
        foreach (var chunk in chunks)
        {
            if (chunk.Offset != next || chunk.ChunkID is not { Length: 20 } || chunk.UncompressedLength is 0 or > 16 * 1024 * 1024
                || chunk.CompressedLength is 0 or > 20 * 1024 * 1024) throw new InvalidDataException("Invalid chunk range/size/hash");
            next += chunk.UncompressedLength;
        }
        if (next != file.TotalSize) throw new InvalidDataException("Incomplete chunk coverage");
        total = checked(total + (long)file.TotalSize);
        if (total > 20 * 1024 * 1024) throw new InvalidDataException("Control total size limit");
        files.Add(new(path, (long)file.TotalSize, file.FileHash.ToArray(), chunks));
    }
    foreach (var file in files)
        if (names.Any(other => other.StartsWith(file.Path + "/", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("File/directory collision");
    if ((ulong)total != manifest.TotalUncompressedSize || files.Count == 0) throw new InvalidDataException("Manifest total mismatch");
    return new(files, total);
}
