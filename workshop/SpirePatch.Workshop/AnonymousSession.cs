using System.Net;
using System.Text.Json;
using SteamKit2;
using SteamKit2.Discovery;
using SteamKit2.Internal;

namespace SpirePatch.Workshop;

/// <summary>Only LogOnAnonymous. Never reads a Steam installation, account, cookie or license cache.</summary>
public sealed class AnonymousSession : IAsyncDisposable
{
    readonly SteamClient client;
    readonly CallbackManager callbacks;
    readonly CancellationTokenSource stop = new();
    readonly Task pump;
    readonly TaskCompletionSource login = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly DownloadOptions options;
    public AnonymousSession(DownloadOptions options, IWebProxy? proxy = null)
    {
        this.options = options;
        client = new(SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.WebSocket)
            .WithDirectoryFetch(false).WithConnectionTimeout(options.RequestTimeout)
            .WithHttpClientFactory(_ => new HttpClient(new SocketsHttpHandler
            { UseCookies = false, Proxy = proxy, UseProxy = true }) { Timeout = options.RequestTimeout })));
        callbacks = new(client);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            options.Log?.Invoke("CM connected; requesting LogOnAnonymous");
            client.GetHandler<SteamUser>()!.LogOnAnonymous();
        });
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => login.TrySetException(
            new IOException("CM WebSocket disconnected before anonymous logon completed.")));
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(c =>
        {
            options.Log?.Invoke("Anonymous logon EResult=" + c.Result);
            if (c.Result == EResult.OK) login.TrySetResult();
            else login.TrySetException(ResultFailure("AnonymousLogon", c.Result));
        });
        pump = Task.Run(() =>
        {
            try { while (!stop.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
            catch (Exception e) { login.TrySetException(e); }
        });
    }

    public static async Task<AnonymousSession> ConnectAsync(SteamHttp http, DownloadOptions options, IWebProxy? proxy, CancellationToken ct)
    {
        var bytes = await http.BytesAsync(new("https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=0&cmtype=websockets"), 1024 * 1024, ct);
        using var json = JsonDocument.Parse(bytes);
        var endpoints = json.RootElement.GetProperty("response").GetProperty("serverlist").EnumerateArray()
            .Where(s => s.GetProperty("type").GetString() == "websockets")
            .Select(s => s.GetProperty("endpoint").GetString()!)
            .Where(e => Uri.TryCreate("https://" + e, UriKind.Absolute, out var uri)
                && uri.Host.EndsWith(".steamserver.net", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0)
            .Distinct().OrderByDescending(e => e.EndsWith(":443", StringComparison.Ordinal)).Take(options.Attempts).ToArray();
        if (endpoints.Length == 0) throw new WorkshopException("NetworkFailure", "Steam 未返回可用 CM WebSocket 节点。");
        Exception? last = null;
        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            var session = new AnonymousSession(options, proxy);
            try
            {
                options.Log?.Invoke("CM endpoint=" + endpoint);
                session.client.Connect(ServerRecord.CreateWebSocketServer(endpoint));
                await session.login.Task.WaitAsync(options.RequestTimeout, ct);
                return session;
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                last = e; options.Log?.Invoke("CM transport failed: " + e.Message);
                await session.DisposeAsync();
            }
            catch { await session.DisposeAsync(); throw; }
        }
        throw new WorkshopException("NetworkFailure", "所有 CM 连接尝试失败；未获得 Steam 权限判定。", last);
    }

    static WorkshopException ResultFailure(string stage, EResult result) => new(
        result is EResult.AccessDenied or EResult.InsufficientPrivilege or EResult.AccountNotFound ? "AccessDenied" : "SteamResult",
        $"{stage}: Steam EResult={result} ({(int)result})。匿名授权未获准；停止此路径，不尝试绕过。");

    public static void RequireOk(string stage, EResult result)
    {
        if (result != EResult.OK) throw ResultFailure(stage, result);
    }

    async Task<T> RpcAsync<T>(string stage, Func<Task<T>> request, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await request().WaitAsync(options.RequestTimeout, ct); }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                if (attempt >= options.Attempts) throw new WorkshopException("NetworkTimeout", stage + " CM 请求超时或连接失败。", e);
                options.Log?.Invoke($"{stage}: retry {attempt + 1}/{options.Attempts}");
                await Task.Delay(400 * attempt, ct);
            }
        }
    }

    public sealed record CdnEndpoint(string Host, string VHost);

    public async Task<(ulong Code, byte[] Key, IReadOnlyList<CdnEndpoint> Servers)> AuthorizeAsync(ulong manifest, CancellationToken ct)
    {
        var service = client.GetHandler<SteamUnifiedMessages>()!.CreateService<ContentServerDirectory>();
        // Inspect the EResult ourselves; SteamContent.GetManifestRequestCode discards it.
        var code = await RpcAsync("ManifestRequestCode", () => service.GetManifestRequestCode(new CContentServerDirectory_GetManifestRequestCode_Request
        { app_id = WorkshopInput.AppId, depot_id = WorkshopInput.AppId, manifest_id = manifest }).ToTask(), ct);
        options.Log?.Invoke("Manifest authorization EResult=" + code.Result);
        RequireOk("ManifestRequestCode", code.Result);
        if (code.Body.manifest_request_code == 0)
            throw new WorkshopException("AccessDenied", "Steam 未签发 Manifest 请求码；不尝试无请求码回退。");
        var key = await RpcAsync("DepotDecryptionKey", () => client.GetHandler<SteamApps>()!.GetDepotDecryptionKey(WorkshopInput.AppId, WorkshopInput.AppId).ToTask(), ct);
        options.Log?.Invoke("Depot key EResult=" + key.Result);
        RequireOk("DepotDecryptionKey", key.Result);
        if (key.DepotKey is not { Length: 32 }) throw new WorkshopException("InvalidProtocol", "Steam 未返回有效 depot key。");
        var servers = await RpcAsync("ContentServerDirectory", () => service.GetServersForSteamPipe(
            new CContentServerDirectory_GetServersForSteamPipe_Request { cell_id = client.CellID ?? 0, max_servers = 20 }).ToTask(), ct);
        RequireOk("ContentServerDirectory", servers.Result);
        var selected = servers.Body.servers.Where(s => !s.use_as_proxy && (s.type is "CDN" or "SteamCache")
            && (s.allowed_app_ids.Count == 0 || s.allowed_app_ids.Contains(WorkshopInput.AppId)))
            .OrderBy(s => s.weighted_load).Take(5).Select(s => new CdnEndpoint(s.host,
                string.IsNullOrEmpty(s.vhost) ? s.host : s.vhost)).ToArray();
        if (selected.Length == 0) throw new WorkshopException("NetworkFailure", "Steam 未返回适用 CDN 节点。");
        return (code.Body.manifest_request_code, key.DepotKey, selected);
    }
    public async Task<string> CdnTokenAsync(string host, CancellationToken ct)
    {
        var result = await RpcAsync("CDNAuthToken", () => client.GetHandler<SteamContent>()!.GetCDNAuthToken(WorkshopInput.AppId, WorkshopInput.AppId, host), ct);
        RequireOk("CDNAuthToken", result.Result);
        return result.Token.TrimStart('?');
    }
    public async ValueTask DisposeAsync()
    {
        client.Disconnect(); stop.Cancel(); await pump; stop.Dispose();
    }
}
