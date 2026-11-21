using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

// All network activity in this test is intercepted by a local HttpMessageHandler.
internal static class SupplyStationNetworkHardeningChecks
{
    const ulong ItemId = 3830005491;
    const uint AppId = SpireSupplyStationClient.SupportedAppId;
    const string Cdn = "st.dl.eccdnx.com";
    const string Mirror = "st.dl.bscstorage.net";
    static readonly string Key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await respond(request, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }

    static Handler Mock(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new((r, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(r)); });

    static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    static string Descriptor(params string[] hosts) => JsonSerializer.Serialize(new
    {
        mode = "UGC", appId = AppId, publishedFileId = ItemId.ToString(),
        title = "SafetyFixture", fileName = "SafetyFixture.zip",
        fileSizeBytes = 1024L, contentVersion = "test", manifestId = "2983186950050217793",
        depotId = AppId, requestCode = "123456789",
        endpoints = hosts.Select(h => new { origin = "https://" + h, query = (string?)null }),
        depotKeyBase64 = Key, expiresAt = "2099-01-01T00:00:00Z"
    });

    static string Token(string host) => JsonSerializer.Serialize(new
    { origin = "https://" + host, query = "token=fixture&expiration_time=4102444800" });

    static async Task<T> Throws<T>(Func<Task> callback) where T : Exception
    {
        try { await callback(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        int apiCalls = 0;
        using (var client = new SpireSupplyStationClient(Mock(request =>
        {
            apiCalls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45)) }
            };
        })))
        {
            var limited = await Throws<SupplyStationRateLimitException>(
                () => client.ReadContentIdentityAsync(AppId, ItemId));
            check(apiCalls == 1 && limited.RetryAfter >= TimeSpan.FromSeconds(45),
                "API 429 obeys Retry-After and does not retry");
            await Throws<SupplyStationRateLimitException>(
                () => client.ReadContentIdentityAsync(AppId, ItemId));
            check(apiCalls == 1, "API 429 prevents further requests within the cooling window");
        }

        int cdnCalls = 0;
        using (var client = new SpireSupplyStationClient(Mock(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(Descriptor(Cdn, Mirror));
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                return Json(Token(request.RequestUri.Query.Contains(Mirror, StringComparison.Ordinal) ? Mirror : Cdn));
            cdnCalls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1)) } };
        })))
        {
            await Throws<SupplyStationRateLimitException>(
                () => client.ProbeManifestAsync(AppId, ItemId));
            check(cdnCalls == 1, "CDN 429 stops before attempting another endpoint");
        }

        int refreshCalls = 0;
        using (var client = new SpireSupplyStationClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(Descriptor(Cdn));
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
            {
                bool refresh = request.RequestUri.Query.Contains("refresh=true", StringComparison.Ordinal);
                if (refresh) Interlocked.Increment(ref refreshCalls);
                return Json(JsonSerializer.Serialize(new {
                    origin = "https://" + Cdn,
                    query = (refresh ? "token=fresh" : "token=stale") + "&expiration_time=4102444800"
                }));
            }
            if (request.RequestUri.Query.Contains("token=stale", StringComparison.Ordinal))
            {
                await Task.Delay(25, token); // synchronize stale-token responses across concurrent callers
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([1, 2, 3]) }; // locally invalid ZIP; no real CDN
        })))
        {
            var concurrent = Enumerable.Range(0, 8).Select(async _ =>
            {
                try { await client.ProbeManifestAsync(AppId, ItemId); }
                catch (InvalidDataException) { }
                catch (HttpRequestException) { }
            });
            await Task.WhenAll(concurrent);
            check(refreshCalls == 1, "eight concurrent 403 responses share one CDN token refresh");
        }

        foreach (var bad in new[] { "localhost", "127.0.0.1", "10.0.0.5", "192.168.1.8", "evil.example" })
        {
            int tokenCalls = 0;
            using var client = new SpireSupplyStationClient(Mock(request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    tokenCalls++;
                return Json(Descriptor(bad));
            }));
            await Throws<InvalidDataException>(() => client.ProbeAsync(AppId, ItemId));
            check(tokenCalls == 0, "untrusted or internal initial CDN rejected: " + bad);
        }

        using (var client = new SpireSupplyStationClient(
            Mock(request => request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal)
                ? Json(Descriptor(Cdn)) : Json(Token(Cdn))),
            dnsResolver: (host, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.1") })))
        {
            await Throws<HttpRequestException>(() => client.ProbeManifestAsync(AppId, ItemId));
            check(true, "resolved private IP rejected without real DNS or CDN access");
        }

        int attempts = 0;
        string[] hosts = [Cdn, Mirror, "steamcontent.com", "steamusercontent.com",
            "steampowered.com", "steamstatic.com", "steamgames.com", "steamserver.net",
            "akamaized.net", "akamaihd.net", "valve.net", "steamchina.com",
            "ssl.hwcdn.net", "steam.cdn.on.net", "steam.cdn.orcon.net.nz", "st.dl.pinyuncloud.com"];
        using (var client = new SpireSupplyStationClient(Mock(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(Descriptor(hosts));
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
            {
                string host = Uri.UnescapeDataString(request.RequestUri.Query.Split("host=")[1].Split('&')[0]);
                return Json(Token(host));
            }
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        })))
        {
            await Throws<SupplyStationBudgetExceededException>(
                () => client.ProbeManifestAsync(AppId, ItemId));
            check(attempts <= 12, "all-mirror failure respects one shared Workshop request budget");
        }

        using (var client = new SpireSupplyStationClient(
            new Handler(async (request, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json(Descriptor(Cdn));
            }), apiTimeout: TimeSpan.FromMilliseconds(25)))
        {
            await Throws<TimeoutException>(() => client.ReadContentIdentityAsync(AppId, ItemId));
            check(true, "API timeout includes SendAsync and maps to TimeoutException");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Throws<OperationCanceledException>(
                () => client.ReadContentIdentityAsync(AppId, ItemId, cancel.Token));
            check(true, "user cancellation is not confused with an internal timeout");
        }

        int fallbackCalls = 0;
        using (var client = new SpireSupplyStationClient(
            new Handler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                    return Json(Descriptor(Cdn, Mirror));
                if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                {
                    string host = Uri.UnescapeDataString(request.RequestUri.Query.Split("host=")[1].Split('&')[0]);
                    return Json(Token(host));
                }
                if (request.RequestUri.Host == Cdn)
                {
                    fallbackCalls++;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                fallbackCalls++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }), cdnTimeout: TimeSpan.FromMilliseconds(25)))
        {
            await Throws<HttpRequestException>(() => client.ProbeManifestAsync(AppId, ItemId));
            check(fallbackCalls >= 2, "CDN timeout participates in fallback to another trusted host");
        }

        string? original = Environment.GetEnvironmentVariable("SPIREPATCH_NETWORK_ISOLATED");
        try
        {
            Environment.SetEnvironmentVariable("SPIREPATCH_NETWORK_ISOLATED", "1");
            bool denied = false;
            try { using var forbidden = new SpireSupplyStationClient(); }
            catch (InvalidOperationException) { denied = true; }
            check(denied, "CI isolation refuses a default live HttpClient");
            using var mocked = new SpireSupplyStationClient(Mock(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            check(mocked is not null, "CI isolation still permits explicit mock handlers");
        }
        finally { Environment.SetEnvironmentVariable("SPIREPATCH_NETWORK_ISOLATED", original); }
    }
}
