using System.Net;
using System.Text.Json;
using SpirePatch.Workshop;

if (args.Length < 2)
{
    Console.WriteLine("SpirePatch 创意工坊隔离验证（不会安装 MOD）\ninit <全新测试目录>\nmetadata <ID或链接> --sandbox <目录>\ndownload <ID或链接> --sandbox <目录>\n可选：--proxy http://127.0.0.1:端口 --timeout-seconds 30 --cancel-after-seconds 120\nCtrl+C 取消。所有结果仅保存在已初始化的测试目录。");
    return 2;
}
TestSandbox? sandbox = null;
ulong? requestedId = null;
var events = new List<object>();
var run = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
var outputLock = new object();
void Log(string message)
{
    lock (outputLock) { Console.Error.WriteLine(message); events.Add(new { utc = DateTimeOffset.UtcNow, message }); }
}
string? Option(string name)
{
    int at = Array.IndexOf(args, name);
    if (at < 0) return null;
    if (at + 1 >= args.Length) throw new ArgumentException("选项缺少参数：" + name);
    return args[at + 1];
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
try
{
    if (args[0] == "init")
    {
        sandbox = TestSandbox.Initialize(args[1]); Console.WriteLine(sandbox.Root); return 0;
    }
    if (args[0] is not ("metadata" or "download")) throw new ArgumentException("未知命令。");
    requestedId = WorkshopInput.Parse(args[1]);
    sandbox = new TestSandbox(Option("--sandbox") ?? throw new ArgumentException("必须指定 --sandbox。"));
    var seconds = double.Parse(Option("--timeout-seconds") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
    if (seconds is < 1 or > 300) throw new ArgumentException("单次请求超时范围为 1–300 秒。");
    if (Option("--cancel-after-seconds") is string after) cancel.CancelAfter(TimeSpan.FromSeconds(double.Parse(after, System.Globalization.CultureInfo.InvariantCulture)));
    IWebProxy? proxy = null;
    if (Option("--proxy") is string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.UserInfo.Length != 0)
            throw new ArgumentException("代理地址必须为不含账号密码的 HTTP(S) URL。");
        proxy = new WebProxy(u);
    }
    var options = new DownloadOptions { RequestTimeout = TimeSpan.FromSeconds(seconds), Log = Log };
    using var http = new SteamHttp(options, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, Proxy = proxy, UseProxy = true });
    if (args[0] == "metadata")
    {
        var (metadata, raw) = await new MetadataClient(http).QueryAsync(args[1], cancel.Token);
        using var held = sandbox.Lock();
        await sandbox.WriteBytesAsync($"metadata/{metadata.WorkshopId}.json", raw, cancel.Token);
        await sandbox.WriteJsonAsync($"runs/{run}.json", new { status = "metadata-only", command = args[0], requestedId, metadata, downloaded = false, events }, cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(metadata, WorkshopJson.Options));
    }
    else
    {
        var result = await new WorkshopDownloader(http, options, proxy).DownloadAsync(args[1], sandbox,
            new InlineProgress(p => Log($"{p.Stage}: {p.Bytes}/{p.Total} {p.File}")), cancel.Token);
        await sandbox.WriteJsonAsync($"runs/{run}.json", new { status = "download-complete", command = args[0], requestedId, result, events }, cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, WorkshopJson.Options));
    }
    return 0;
}
catch (Exception e)
{
    var code = e is OperationCanceledException && cancel.IsCancellationRequested ? "Cancelled"
        : e is TimeoutException ? "NetworkTimeout" : (e as WorkshopException)?.Code ?? e.GetType().Name;
    var failure = new { status = "failed", command = args[0], requestedId, downloaded = false, code, message = e.Message, events };
    Console.Error.WriteLine(JsonSerializer.Serialize(failure, WorkshopJson.Options));
    if (sandbox != null)
    {
        try { await sandbox.WriteJsonAsync($"runs/{run}.json", failure); }
        catch (Exception logging) { Console.Error.WriteLine("结果记录失败：" + logging.Message); }
    }
    return code == "Cancelled" ? 130 : 1;
}

sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value) => report(value);
}
