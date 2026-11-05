using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal static class SupplyStationChecks
{
    const uint AppId = SpireSupplyStationClient.SupportedAppId;
    const ulong ItemId = 3830005491;
    const string Future = "2099-01-01T00:00:00Z";
    static readonly string DepotKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
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

        var requests = new List<(string Path, string? UserAgent, string? Authorization, string? Cookie, string? Host)>();
        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.UserAgent.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.Contains("Cookie") ? string.Join(";", request.Headers.GetValues("Cookie")) : null,
                request.RequestUri.Query));
            if (request.RequestUri.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, UgcDescriptor());
            if (request.RequestUri.AbsolutePath.EndsWith("/cdn/token", StringComparison.Ordinal))
                return Json(request, Token("http://cdn.example", "token=secret-token&expiration_time=4102444800"));
            throw new InvalidOperationException("Unexpected request path.");
        })))
        {
            var result = await client.ProbeAsync(AppId, ItemId);
            Check(result.Mode == "UGC" && result.PublishedFileId == ItemId.ToString(),
                "UGC descriptor is parsed and bound to the requested Workshop ID");
            Check(result.ManifestId == "2983186950050217793" && result.DepotId == 2868841,
                "64-bit Manifest ID and Depot ID are retained without numeric truncation");
            Check(result.CdnTokenValidated && result.CdnHosts.SequenceEqual(["cdn.example"]),
                "CDN token origin is checked against the authorized endpoint");
            Check(!result.ContentDownloaded && !result.GameBranchVerified &&
                result.RequestedBranch == SpireSupplyStationClient.DefaultBranchNamespace,
                "probe does not claim content download or verified game-branch matching");
            Check(requests.Count == 2 &&
                requests[0].Path.EndsWith("/descriptor", StringComparison.Ordinal) &&
                requests[1].Path.EndsWith("/cdn/token", StringComparison.Ordinal) &&
                requests[1].Host!.Contains("host=cdn.example", StringComparison.Ordinal),
                "only descriptor and CDN-token endpoints are called in phase 1");
            Check(requests.All(item =>
                item.UserAgent!.StartsWith("Sts2PatchManager/", StringComparison.Ordinal) &&
                item.Authorization is null && item.Cookie is null),
                "requests use the manager User-Agent and send no Steam credentials or cookies");
            string report = JsonSerializer.Serialize(result);
            Check(!report.Contains("secret-token", StringComparison.Ordinal) &&
                !report.Contains("descriptor-secret", StringComparison.Ordinal) &&
                !report.Contains(DepotKey, StringComparison.Ordinal) &&
                !report.Contains("18446744073709551614", StringComparison.Ordinal),
                "probe result never exposes CDN tokens, signed endpoint queries, Depot Key or request code");
        }

        bool tokenCalled = false;
        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, DirectDescriptor("https://cdn.example/ancient.zip?signature=private"));
            tokenCalled = true;
            throw new InvalidOperationException("DIRECT mode must not request a CDN token.");
        })))
        {
            var result = await client.ProbeAsync(AppId, ItemId);
            Check(result.Mode == "DIRECT" && !result.CdnTokenValidated && !tokenCalled,
                "DIRECT descriptor is accepted without calling the UGC token endpoint");
            Check(!JsonSerializer.Serialize(result).Contains("signature=private", StringComparison.Ordinal),
                "signed direct-download URL is not exposed in the probe report");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
            Json(request, UgcDescriptor(publishedFileId: "999")))))
        {
            await ExpectFailure(
                () => client.ProbeAsync(AppId, ItemId),
                Check,
                "descriptor for a different Workshop item is rejected before CDN access");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
            Json(request, UgcDescriptor(origin: "https://cdn.example:444")))))
        {
            await ExpectFailure(
                () => client.ProbeAsync(AppId, ItemId),
                Check,
                "CDN origins with custom ports or unsafe URL components are rejected");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
                return Json(request, UgcDescriptor());
            return Json(request, Token("http://other.example", "token=wrong-host"));
        })))
        {
            await ExpectFailure(
                () => client.ProbeAsync(AppId, ItemId),
                Check,
                "CDN token for a different host is rejected");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
            Json(request, DirectDescriptor("http://cdn.example/unsafe.zip")))))
        {
            await ExpectFailure(
                () => client.ProbeAsync(AppId, ItemId),
                Check,
                "DIRECT mode rejects non-HTTPS download addresses");
        }

        int directRefreshCalls = 0;
        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            directRefreshCalls++;
            return Json(request, DirectDescriptor(
                directRefreshCalls == 1
                    ? "https://cdn.example/ancient.zip?signature=old"
                    : "https://cdn.example/ancient.zip?signature=new",
                directRefreshCalls == 1 ? "2000-01-01T00:00:00Z" : Future));
        })))
        {
            var result = await client.ProbeAsync(AppId, ItemId);
            Check(directRefreshCalls == 2 && result.Mode == "DIRECT",
                "expired DIRECT descriptor can refresh a rotating signed URL without changing content identity");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
            Json(request, UgcDescriptor(depotKey: Convert.ToBase64String(new byte[31]))))))
        {
            await ExpectFailure(
                () => client.ProbeAsync(AppId, ItemId),
                Check,
                "UGC descriptor with a Depot Key of the wrong length is rejected");
        }

        int descriptorCalls = 0;
        bool sawRefresh = false;
        using (var client = new SpireSupplyStationClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
            {
                descriptorCalls++;
                sawRefresh |= request.RequestUri.Query.Contains("refresh=true", StringComparison.Ordinal);
                return Json(request, UgcDescriptor(
                    expiresAt: descriptorCalls == 1 ? "2000-01-01T00:00:00Z" : Future));
            }
            return Json(request, Token("http://cdn.example", "token=refreshed&expiration_time=4102444800"));
        })))
        {
            var result = await client.ProbeAsync(AppId, ItemId);
            Check(descriptorCalls == 2 && sawRefresh && result.CdnTokenValidated,
                "expired descriptor is refreshed once before CDN authorization is checked");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://attacker.example/redirect") }
            })))
        {
            await ExpectFailure(
                () => client.ProbeAsync(AppId, ItemId),
                Check,
                "HTTP redirects are rejected rather than followed");
        }

        using (var client = new SpireSupplyStationClient(new Handler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1024 * 1024 + 1])
            })))
        {
            await ExpectFailure(
                () => client.ProbeAsync(AppId, ItemId),
                Check,
                "oversized JSON responses are rejected before parsing");
        }

        Check(true, "phase-1 protocol tests completed without writing to a game directory");
    }

    static async Task ExpectFailure(
        Func<Task<SupplyStationProbeResult>> action,
        Action<bool, string> check,
        string description)
    {
        try
        {
            await action();
            check(false, description);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or
                                      HttpRequestException or TimeoutException)
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

    static string UgcDescriptor(
        string publishedFileId = "3830005491",
        string origin = "http://cdn.example",
        string expiresAt = Future,
        string? depotKey = null)
    {
        return JsonSerializer.Serialize(new
        {
            mode = "UGC",
            appId = AppId,
            publishedFileId,
            title = "AncientEntrances",
            fileName = "AncientEntrances.zip",
            fileSizeBytes = 8100000L,
            contentVersion = "fixture-v1",
            directUrl = (string?)null,
            manifestId = "2983186950050217793",
            depotId = 2868841u,
            requestCode = "18446744073709551614",
            endpoints = new[]
            {
                new
                {
                    origin,
                    query = "bootstrap=descriptor-secret&expiration_time=4102444800"
                }
            },
            depotKeyBase64 = depotKey ?? DepotKey,
            expiresAt
        });
    }

    static string DirectDescriptor(string directUrl, string expiresAt = Future) =>
        JsonSerializer.Serialize(new
        {
            mode = "DIRECT",
            appId = AppId,
            publishedFileId = ItemId.ToString(),
            title = "AncientEntrances",
            fileName = "AncientEntrances.zip",
            fileSizeBytes = 8100000L,
            contentVersion = "fixture-v1",
            directUrl,
            manifestId = (string?)null,
            depotId = (uint?)null,
            requestCode = (string?)null,
            endpoints = Array.Empty<object>(),
            depotKeyBase64 = (string?)null,
            expiresAt
        });

    static string Token(string origin, string query) =>
        JsonSerializer.Serialize(new { origin, query });
}
