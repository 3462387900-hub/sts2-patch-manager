using System.Text.Json;

namespace SpirePatch.Workshop;

public sealed class MetadataClient(SteamHttp http)
{
    public async Task<(WorkshopMetadata Metadata, byte[] Raw)> QueryAsync(string input, CancellationToken ct)
    {
        var id = WorkshopInput.Parse(input);
        var raw = await http.BytesAsync(new("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/"),
            2 * 1024 * 1024, ct, () => new FormUrlEncodedContent(new Dictionary<string, string>
            { ["itemcount"] = "1", ["publishedfileids[0]"] = id.ToString() }));
        return (Parse(raw, id), raw);
    }
    public static WorkshopMetadata Parse(byte[] raw, ulong expectedId)
    {
        try
        {
            using var json = JsonDocument.Parse(raw);
            var details = json.RootElement.GetProperty("response").GetProperty("publishedfiledetails");
            if (details.GetArrayLength() != 1) throw new WorkshopException("InvalidMetadata", "条目数量不匹配。");
            var d = details[0];
            string S(string key) => d.TryGetProperty(key, out var v) ? v.ToString() : "";
            if (S("result") != "1") throw new WorkshopException("MetadataUnavailable", "Steam 条目查询失败：result=" + S("result") + "（不存在、隐藏或无访问权限）。");
            if (!ulong.TryParse(S("publishedfileid"), out var id) || id != expectedId) throw new WorkshopException("InvalidMetadata", "Workshop ID 不匹配。");
            if (!uint.TryParse(S("consumer_app_id"), out var app) || app != WorkshopInput.AppId)
                throw new WorkshopException("WrongApp", "拒绝其他游戏条目，consumer_app_id=" + S("consumer_app_id"));
            if (S("banned") == "1" || S("visibility") is not ("0" or "")) throw new WorkshopException("AccessDenied", "条目不是公开可访问内容。");
            if (S("file_type") is not ("" or "0" or "3" or "5" or "10" or "11" or "12"))
                throw new WorkshopException("UnsupportedItem", "不支持合集或此条目类型。");
            if (!long.TryParse(S("file_size"), out var size) || size < 0 || !long.TryParse(S("time_updated"), out var updated))
                throw new WorkshopException("InvalidMetadata", "缺少有效文件大小或更新时间。");
            ulong? manifest = ulong.TryParse(S("hcontent_file"), out var m) && m != 0 && m != ulong.MaxValue ? m : null;
            return new(id, S("title"), app, size, DateTimeOffset.FromUnixTimeSeconds(updated),
                string.IsNullOrWhiteSpace(S("file_url")) ? null : S("file_url"), manifest, S("filename"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new WorkshopException("InvalidMetadata", "Steam 元数据格式无效。", e); }
    }
}
