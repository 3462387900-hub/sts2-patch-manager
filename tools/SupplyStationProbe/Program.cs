using System.Globalization;
using System.Text.Json;
using SpirePatch;

const uint appId = SpireSupplyStationClient.SupportedAppId;
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
bool manifestMode = args.Length > 0 && args[0].Equals("--manifest-check", StringComparison.Ordinal);
int workshopArg = manifestMode ? 1 : 0;
int reportArg = manifestMode ? 2 : 1;

if (args.Length != (manifestMode ? 3 : 2) ||
    !ulong.TryParse(args.ElementAtOrDefault(workshopArg), NumberStyles.None, CultureInfo.InvariantCulture, out var workshopId) ||
    workshopId == 0)
{
    Console.Error.WriteLine("用法：");
    Console.Error.WriteLine("  SpireSupplyStationProbe.exe <WorkshopID> <报告路径>");
    Console.Error.WriteLine("  SpireSupplyStationProbe.exe --manifest-check <WorkshopID> <报告路径>");
    return 2;
}

var reportPath = Path.GetFullPath(args[reportArg]);
var reportDirectory = Path.GetDirectoryName(reportPath);
if (!string.IsNullOrWhiteSpace(reportDirectory))
    Directory.CreateDirectory(reportDirectory);

Console.WriteLine("尖塔补给站真实接口探测");
Console.WriteLine($"AppID：{appId}");
Console.WriteLine($"Workshop ID：{workshopId}");
Console.WriteLine($"User-Agent：{SpireSupplyStationClient.RequiredUserAgent}");
if (manifestMode)
    Console.WriteLine("模式：UGC Manifest 下载与解析（不会下载 Chunk 或 MOD 文件）");
else
    Console.WriteLine("模式：下载描述信息与 CDN 授权验证（不会下载 MOD 文件）");

try
{
    using var client = new SpireSupplyStationClient();
    object result;
    if (manifestMode)
    {
        var manifest = await client.ProbeManifestAsync(appId, workshopId);
        result = manifest;
        Console.WriteLine();
        Console.WriteLine("Manifest 下载并解析成功。");
        Console.WriteLine($"MOD 标题：{manifest.Title}");
        Console.WriteLine($"Manifest ID：{manifest.ManifestId}");
        Console.WriteLine($"文件条目：{manifest.Manifest.FileCount}");
        Console.WriteLine($"唯一 Chunk：{manifest.Manifest.UniqueChunkCount}");
        Console.WriteLine($"文件总大小：{manifest.Manifest.TotalFileBytes:N0} 字节");
        Console.WriteLine($"Chunk 解压后总大小：{manifest.Manifest.TotalChunkUncompressedBytes:N0} 字节");
        Console.WriteLine($"文件名加密：{(manifest.Manifest.FileNamesEncrypted ? "是" : "否")}");
        Console.WriteLine($"Manifest 签名区：{manifest.Manifest.SignatureBytes} 字节（未执行签名验证）");
        Console.WriteLine("Chunk 下载：未执行");
    }
    else
    {
        var probe = await client.ProbeAsync(appId, workshopId);
        result = probe;
        Console.WriteLine();
        Console.WriteLine("探测成功。");
        Console.WriteLine($"下载模式：{probe.Mode}");
        Console.WriteLine($"MOD 标题：{probe.Title}");
        Console.WriteLine($"文件名：{probe.FileName}");
        Console.WriteLine($"CDN 授权校验：{(probe.CdnTokenValidated ? "通过" : "不适用（DIRECT 模式）")}");
        Console.WriteLine("内容下载：未执行");
    }

    var payload = new
    {
        success = true,
        checkedAt = DateTimeOffset.UtcNow,
        userAgent = SpireSupplyStationClient.RequiredUserAgent,
        mode = manifestMode ? "manifest" : "descriptor",
        result
    };

    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(payload, jsonOptions));
    Console.WriteLine($"报告：{reportPath}");
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
        mode = manifestMode ? "manifest" : "descriptor",
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
