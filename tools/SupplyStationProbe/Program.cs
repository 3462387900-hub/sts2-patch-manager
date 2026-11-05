using System.Globalization;
using System.Text.Json;
using SpirePatch;

const uint appId = SpireSupplyStationClient.SupportedAppId;
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

if (args.Length != 2 ||
    !ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var workshopId) ||
    workshopId == 0)
{
    Console.Error.WriteLine("用法：SpireSupplyStationProbe.exe <WorkshopID> <报告路径>");
    return 2;
}

var reportPath = Path.GetFullPath(args[1]);
var reportDirectory = Path.GetDirectoryName(reportPath);
if (!string.IsNullOrWhiteSpace(reportDirectory))
    Directory.CreateDirectory(reportDirectory);

Console.WriteLine("尖塔补给站真实接口探测");
Console.WriteLine($"AppID：{appId}");
Console.WriteLine($"Workshop ID：{workshopId}");
Console.WriteLine($"User-Agent：{SpireSupplyStationClient.RequiredUserAgent}");
Console.WriteLine("正在请求下载描述信息；UGC 条目还会验证一个 CDN 授权，不会下载 MOD 文件。");

try
{
    using var client = new SpireSupplyStationClient();
    var result = await client.ProbeAsync(appId, workshopId);
    var payload = new
    {
        success = true,
        checkedAt = DateTimeOffset.UtcNow,
        userAgent = SpireSupplyStationClient.RequiredUserAgent,
        result
    };

    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(payload, jsonOptions));
    Console.WriteLine();
    Console.WriteLine("探测成功。");
    Console.WriteLine($"下载模式：{result.Mode}");
    Console.WriteLine($"MOD 标题：{result.Title}");
    Console.WriteLine($"文件名：{result.FileName}");
    Console.WriteLine($"CDN 授权校验：{(result.CdnTokenValidated ? "通过" : "不适用（DIRECT 模式）")}");
    Console.WriteLine($"报告：{reportPath}");
    Console.WriteLine("内容下载：未执行");
    return 0;
}
catch (Exception error)
{
    var payload = new
    {
        success = false,
        checkedAt = DateTimeOffset.UtcNow,
        appId,
        workshopId = workshopId.ToString(CultureInfo.InvariantCulture),
        userAgent = SpireSupplyStationClient.RequiredUserAgent,
        errorType = error.GetType().Name,
        error = error.Message
    };

    try
    {
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(payload, jsonOptions));
        Console.Error.WriteLine($"失败报告：{reportPath}");
    }
    catch (Exception reportError)
    {
        Console.Error.WriteLine($"无法写入报告：{reportError.Message}");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine($"探测失败：{error.GetType().Name}");
    Console.Error.WriteLine(error.Message);
    return 1;
}
