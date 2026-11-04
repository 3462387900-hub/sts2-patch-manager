using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpirePatch.Workshop;

public sealed class WorkshopException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed record WorkshopMetadata(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] ulong WorkshopId,
    string Title, uint AppId, long Size, DateTimeOffset UpdatedUtc, string? FileUrl,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] ulong? ManifestId, string FileName);
public sealed record DownloadProgress(string Stage, long Bytes, long Total, string? File = null);
public sealed record VerifiedFile(string Path, long Bytes, string Sha1, string Sha256);
public sealed record DownloadReceipt(WorkshopMetadata Metadata, string Route, string Integrity,
    DateTimeOffset CompletedUtc, IReadOnlyList<VerifiedFile> Files);
public sealed record DownloadResult(string Directory, bool Reused, DownloadReceipt Receipt);
public sealed record DownloadOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int Attempts { get; init; } = 3;
    public long MaxBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxFiles { get; init; } = 10000;
    public Action<string>? Log { get; init; }
    public void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || Attempts is < 1 or > 5 || MaxBytes <= 0 || MaxFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(DownloadOptions));
    }
}

public static class WorkshopJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true, NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public static class WorkshopInput
{
    public const uint AppId = 2868840;
    public static ulong Parse(string input)
    {
        input = input.Trim();
        if (input.All(char.IsAsciiDigit) && ulong.TryParse(input, out var id) && id > 0) return id;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            && uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && uri.AbsolutePath.TrimEnd('/') is "/sharedfiles/filedetails" or "/workshop/filedetails")
        {
            var values = uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2))
                .Where(p => p.Length == 2 && p[0] == "id").ToArray();
            if (values.Length == 1 && values[0][1].All(char.IsAsciiDigit)
                && ulong.TryParse(values[0][1], out id) && id > 0) return id;
        }
        throw new WorkshopException("InvalidInput", "请输入非零 Workshop ID 或 steamcommunity.com 创意工坊条目链接。");
    }
}
