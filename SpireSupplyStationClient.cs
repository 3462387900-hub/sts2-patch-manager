using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpirePatch;

internal sealed record SupplyStationProbeResult(
    uint AppId,
    string PublishedFileId,
    string Mode,
    string Title,
    string FileName,
    long? FileSizeBytes,
    string ContentVersion,
    string? ManifestId,
    uint? DepotId,
    DateTimeOffset ExpiresAt,
    string[] CdnHosts,
    bool CdnTokenValidated,
    bool ContentDownloaded,
    string Source,
    string SourceDisplayName,
    string RequestedBranch,
    bool GameBranchVerified,
    string Note);

// Phase 1 only resolves and validates the public protocol. It never downloads or installs a MOD.
internal sealed class SpireSupplyStationClient : IDisposable
{
    internal const uint SupportedAppId = 2868840;
    internal const string BaseUrl = "https://workshop.apricityx.top";
    internal const string Source = "spire_supply_station";
    internal const string SourceDisplayName = "尖塔补给站（Apricityx）";
    internal const string RequiredUserAgent = "Sts2PatchManager/0.1.11";
    internal const string DefaultBranchNamespace = "supply-station-default";
    const string ApiHost = "workshop.apricityx.top";
    const int MaxJsonBytes = 1024 * 1024;
    const long MaxFileSizeBytes = 2L * 1024 * 1024 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16
    };

    readonly HttpClient client;

    public SpireSupplyStationClient(HttpMessageHandler? handler = null)
    {
        var transport = handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true
        };
        client = new HttpClient(transport, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(RequiredUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void Dispose() => client.Dispose();

    public async Task<SupplyStationProbeResult> ProbeAsync(
        uint appId,
        ulong publishedFileId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(appId, publishedFileId);
        var request = new WorkshopRequest(appId, publishedFileId);
        var descriptor = await ResolveDescriptorAsync(request, cancellationToken);

        bool tokenValidated = false;
        string[] hosts = [];
        if (descriptor.Mode == "UGC")
        {
            hosts = descriptor.Endpoints
                .Select(endpoint => NormalizeCdnOrigin(endpoint.Origin).Host)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Validate one authorized CDN token without requesting a manifest or content chunk.
            await GetCdnTokenAsync(request, descriptor.Endpoints[0], cancellationToken);
            tokenValidated = true;
        }

        return new SupplyStationProbeResult(
            appId,
            publishedFileId.ToString(CultureInfo.InvariantCulture),
            descriptor.Mode,
            descriptor.Title,
            descriptor.FileName,
            descriptor.FileSizeBytes,
            descriptor.ContentVersion,
            descriptor.ManifestId,
            descriptor.DepotId,
            descriptor.ExpiresAt,
            hosts,
            tokenValidated,
            false,
            Source,
            SourceDisplayName,
            DefaultBranchNamespace,
            false,
            "已验证下载描述信息" + (tokenValidated ? "及 CDN 授权接口" : "") +
            "；尚未下载内容，也未验证其与本机游戏分支的匹配关系。");
    }

    async Task<Descriptor> ResolveDescriptorAsync(
        WorkshopRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = await LoadDescriptorAsync(request, refresh: false, cancellationToken);
        if (descriptor.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var refreshed = await LoadDescriptorAsync(request, refresh: true, cancellationToken);
            if (!SameContentIdentity(descriptor, refreshed))
                throw new InvalidDataException(
                    "刷新下载授权时，下载站返回的 MOD 内容身份发生变化；请重新开始验证。");
            descriptor = refreshed;
        }

        if (descriptor.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("尖塔补给站返回的下载授权已过期。");

        return descriptor;
    }

    async Task<Descriptor> LoadDescriptorAsync(
        WorkshopRequest request,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var descriptor = await GetJsonAsync<Descriptor>(
            BuildApiUri("descriptor", request, refresh: refresh),
            cancellationToken);
        ValidateDescriptor(descriptor, request);
        return descriptor;
    }

    async Task GetCdnTokenAsync(
        WorkshopRequest request,
        Endpoint endpoint,
        CancellationToken cancellationToken)
    {
        var expectedOrigin = NormalizeCdnOrigin(endpoint.Origin);
        var token = await GetJsonAsync<CdnToken>(
            BuildApiUri("cdn/token", request, host: expectedOrigin.Host),
            cancellationToken);

        var actualOrigin = NormalizeCdnOrigin(token.Origin);
        if (!SameOrigin(expectedOrigin, actualOrigin))
            throw new InvalidDataException("CDN 授权返回了与下载描述不匹配的服务器地址。");

        string query = token.Query.Trim().TrimStart('?');
        if (query.Length == 0 || query.Contains('\r') || query.Contains('\n'))
            throw new InvalidDataException("CDN 授权内容为空或格式不安全。");

        var expiration = ReadTokenExpiration(query);
        if (expiration <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("CDN 授权已经过期。");
    }

    async Task<T> GetJsonAsync<T>(Uri uri, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("尖塔补给站返回了重定向；已按安全策略拒绝跟随。");

        if ((int)response.StatusCode is 403 or 429)
            throw new InvalidOperationException(
                "尖塔补给站暂时拒绝请求（HTTP " + (int)response.StatusCode + "）。");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                "尖塔补给站请求失败（HTTP " + (int)response.StatusCode + "）。");

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null ||
            finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase) ||
            finalUri.Port != 443)
            throw new InvalidDataException("接口响应来源与固定下载站地址不一致。");

        if (response.Content.Headers.ContentLength is long length && length > MaxJsonBytes)
            throw new InvalidDataException("尖塔补给站响应超过 1 MiB 限制。");

        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)) > 0)
        {
            if (output.Length + read > MaxJsonBytes)
                throw new InvalidDataException("尖塔补给站响应超过 1 MiB 限制。");
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(output.ToArray(), JsonOptions)
                ?? throw new InvalidDataException("尖塔补给站返回了空 JSON 对象。");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("尖塔补给站返回的 JSON 格式无效。", error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("尖塔补给站请求超时。");
        }
    }

    static Uri BuildApiUri(
        string endpoint,
        WorkshopRequest request,
        string? host = null,
        bool refresh = false)
    {
        var query = new List<string>
        {
            "appId=" + request.AppId.ToString(CultureInfo.InvariantCulture),
            "publishedFileId=" + request.PublishedFileId.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(host))
            query.Add("host=" + Uri.EscapeDataString(host));
        if (refresh)
            query.Add("refresh=true");

        return new Uri(
            BaseUrl + "/api/downloads/browser/" + endpoint + "?" + string.Join("&", query),
            UriKind.Absolute);
    }

    static void ValidateRequest(uint appId, ulong publishedFileId)
    {
        if (appId != SupportedAppId)
            throw new ArgumentOutOfRangeException(
                nameof(appId), "当前只支持《杀戮尖塔 2》（AppID 2868840）。");
        if (publishedFileId == 0)
            throw new ArgumentOutOfRangeException(nameof(publishedFileId), "Workshop ID 不能为 0。");
    }

    static void ValidateDescriptor(Descriptor descriptor, WorkshopRequest request)
    {
        if (descriptor.AppId != request.AppId ||
            !ulong.TryParse(
                descriptor.PublishedFileId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var returnedId) ||
            returnedId != request.PublishedFileId)
            throw new InvalidDataException("尖塔补给站返回的 Workshop 条目与请求 ID 不一致。");

        if (descriptor.Mode is not ("DIRECT" or "UGC"))
            throw new InvalidDataException("尖塔补给站返回了不支持的下载模式。");
        if (string.IsNullOrWhiteSpace(descriptor.Title) || descriptor.Title.Length > 512)
            throw new InvalidDataException("下载描述缺少有效的 MOD 标题。");
        if (string.IsNullOrWhiteSpace(descriptor.ContentVersion) ||
            descriptor.ContentVersion.Length > 128)
            throw new InvalidDataException("下载描述缺少有效的内容版本标识。");

        ValidateFileName(descriptor.FileName);

        if (descriptor.FileSizeBytes is long size &&
            (size <= 0 || size > MaxFileSizeBytes))
            throw new InvalidDataException("MOD 文件大小超出当前支持范围。");

        if (!DateTimeOffset.TryParse(
                descriptor.ExpiresAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiresAt))
            throw new InvalidDataException("尖塔补给站返回的授权过期时间无效。");
        descriptor.ExpiresAt = expiresAt;

        if (descriptor.Mode == "DIRECT")
        {
            if (!Uri.TryCreate(descriptor.DirectUrl, UriKind.Absolute, out var directUrl) ||
                directUrl.Scheme != Uri.UriSchemeHttps ||
                directUrl.Port != 443 ||
                directUrl.UserInfo.Length != 0 ||
                directUrl.Fragment.Length != 0)
                throw new InvalidDataException("直接下载地址不是安全的 HTTPS 地址。");
            return;
        }

        if (!ulong.TryParse(
                descriptor.ManifestId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _) ||
            descriptor.DepotId is null or 0 ||
            !ulong.TryParse(
                descriptor.RequestCode,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _))
            throw new InvalidDataException("UGC 下载描述缺少有效的 Manifest、Depot 或请求码。");

        byte[] depotKey;
        try
        {
            depotKey = Convert.FromBase64String(descriptor.DepotKeyBase64 ?? "");
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("UGC 下载描述中的 Depot Key 格式无效。", error);
        }

        if (depotKey.Length != 32)
            throw new InvalidDataException("UGC 下载描述中的 Depot Key 长度无效。");

        if (descriptor.Endpoints.Count is < 1 or > 16)
            throw new InvalidDataException("UGC 下载描述没有提供有效的 CDN 节点。");

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in descriptor.Endpoints)
        {
            var origin = NormalizeCdnOrigin(endpoint.Origin);
            if (!hosts.Add(origin.Host))
                throw new InvalidDataException("UGC 下载描述包含重复的 CDN 节点。");
            if (endpoint.Query is not null &&
                (endpoint.Query.Contains('\r') || endpoint.Query.Contains('\n')))
                throw new InvalidDataException("CDN 节点授权参数格式不安全。");
        }
    }

    static void ValidateFileName(string? fileName)
    {
        const string invalid = "<>:\"/\\|?*";
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Length > 240 ||
            fileName is "." or ".." ||
            fileName.Any(character => char.IsControl(character) || invalid.Contains(character)) ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.'))
            throw new InvalidDataException("下载描述中的文件名不安全。");

        string deviceName = fileName.Split('.')[0];
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];
        if (reserved.Contains(deviceName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("下载描述使用了 Windows 保留文件名。");
    }

    static Uri NormalizeCdnOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") ||
            origin.UserInfo.Length != 0 ||
            origin.Port != (origin.Scheme == "https" ? 443 : 80) ||
            origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 ||
            origin.Fragment.Length != 0)
            throw new InvalidDataException("尖塔补给站返回了无效的 CDN 节点地址。");

        return new UriBuilder(origin)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = 443
        }.Uri;
    }

    static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme &&
        left.Port == right.Port &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase);

    static DateTimeOffset ReadTokenExpiration(string query)
    {
        foreach (string field in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = field.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) != "expiration_time")
                continue;
            if (pair.Length == 2 &&
                long.TryParse(
                    Uri.UnescapeDataString(pair[1]),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var seconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { break; }
            }
            break;
        }

        // The reference client uses a short fallback lifetime when the CDN omits this field.
        return DateTimeOffset.UtcNow.AddMinutes(5);
    }

    static bool SameContentIdentity(Descriptor left, Descriptor right) =>
        left.Mode == right.Mode &&
        left.AppId == right.AppId &&
        left.PublishedFileId == right.PublishedFileId &&
        left.FileName == right.FileName &&
        left.FileSizeBytes == right.FileSizeBytes &&
        left.ContentVersion == right.ContentVersion &&
        left.ManifestId == right.ManifestId &&
        left.DepotId == right.DepotId &&
        left.DepotKeyBase64 == right.DepotKeyBase64;

    sealed record WorkshopRequest(uint AppId, ulong PublishedFileId);

    sealed class Descriptor
    {
        [JsonPropertyName("mode")] public string Mode { get; init; } = "";
        [JsonPropertyName("appId")] public uint AppId { get; init; }
        [JsonPropertyName("publishedFileId")] public string PublishedFileId { get; init; } = "";
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("fileName")] public string FileName { get; init; } = "";
        [JsonPropertyName("fileSizeBytes")] public long? FileSizeBytes { get; init; }
        [JsonPropertyName("contentVersion")] public string ContentVersion { get; init; } = "";
        [JsonPropertyName("directUrl")] public string? DirectUrl { get; init; }
        [JsonPropertyName("manifestId")] public string? ManifestId { get; init; }
        [JsonPropertyName("depotId")] public uint? DepotId { get; init; }
        [JsonPropertyName("requestCode")] public string? RequestCode { get; init; }
        [JsonPropertyName("endpoints")] public List<Endpoint> Endpoints { get; init; } = [];
        [JsonPropertyName("depotKeyBase64")] public string? DepotKeyBase64 { get; init; }
        [JsonPropertyName("expiresAt")] public string ExpiresAtText { get; init; } = "";
        [JsonIgnore] public DateTimeOffset ExpiresAt { get; set; }
    }

    sealed class Endpoint
    {
        [JsonPropertyName("origin")] public string Origin { get; init; } = "";
        [JsonPropertyName("query")] public string? Query { get; init; }
    }

    sealed class CdnToken
    {
        [JsonPropertyName("origin")] public string Origin { get; init; } = "";
        [JsonPropertyName("query")] public string Query { get; init; } = "";
    }
}
