using System.Net;
using System.Net.Http.Headers;

namespace SpirePatch.Workshop;

public sealed class SteamHttp : IDisposable
{
    readonly HttpClient client;
    readonly DownloadOptions options;
    public SteamHttp(DownloadOptions options, HttpMessageHandler? handler = null)
    {
        options.Validate(); this.options = options;
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch-Workshop-Phase1/0.1");
    }
    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || !(new[] { "steampowered.com", "steamusercontent.com", "steamcontent.com", "steamstatic.com" }
                .Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))))
            throw new WorkshopException("UnsafeUrl", "只允许 Steam 官方 HTTPS 内容地址；拒绝自定义主机、端口和重定向目标。");
        return uri;
    }
    public async Task<T> RetryAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.RequestTimeout);
            try { return await action(timeout.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or TimeoutException)
            {
                var transient = e is not HttpRequestException http || http.StatusCode is null
                    or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500;
                if (!transient || attempt >= options.Attempts)
                    throw new WorkshopException(e is OperationCanceledException or TimeoutException ? "NetworkTimeout" : "NetworkFailure",
                        $"{operation} 失败（第 {attempt} 次）：{e.Message}", e);
                options.Log?.Invoke($"{operation}: transient failure, retry {attempt + 1}/{options.Attempts} ({e.GetType().Name})");
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }
        }
    }

    public async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct, Func<HttpContent>? form = null)
    {
        for (int redirect = 0; redirect <= 4; redirect++)
        {
            ValidateUrl(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (form != null) request.Content = form();
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (form != null || location == null) throw new WorkshopException("UnexpectedRedirect", "Steam API 出现非预期重定向。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var status = (int)response.StatusCode; response.Dispose();
                throw new WorkshopException("AccessDenied", $"Steam HTTP {status} 拒绝匿名访问；不会尝试移除授权参数或绕过权限。");
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode; response.Dispose();
                throw new HttpRequestException($"Steam HTTP {(int)status}", null, status);
            }
            return response;
        }
        throw new WorkshopException("UnexpectedRedirect", "Steam 重定向次数超限。");
    }

    public async Task<byte[]> BytesAsync(Uri uri, int limit, CancellationToken ct, Func<HttpContent>? form = null)
        => await RetryAsync("HTTP " + uri.Host + uri.AbsolutePath, async token =>
        {
            using var response = await SendAsync(uri, token, form);
            if (response.Content.Headers.ContentLength > limit) throw new WorkshopException("SizeLimit", "响应超过大小上限。");
            await using var source = await response.Content.ReadAsStreamAsync(token);
            using var result = new MemoryStream();
            var buffer = new byte[65536]; int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                if (result.Length + read > limit) throw new WorkshopException("SizeLimit", "响应超过大小上限。");
                result.Write(buffer, 0, read);
            }
            return result.ToArray();
        }, ct);
    public void Dispose() => client.Dispose();
}
