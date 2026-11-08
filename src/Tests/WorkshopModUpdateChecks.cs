using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal static class WorkshopModUpdateChecks
{
    const ulong WorkshopId = 3737335127;
    const uint AppId = SpireSupplyStationClient.SupportedAppId;
    const string Future = "2099-01-01T00:00:00Z";
    static readonly string DepotKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
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

        string root = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(output))!,
            "workshop-update-fixture-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "manager-data");
        string gameRoot = Path.Combine(root, "game");
        string modFolder = Path.Combine(gameRoot, "mods", "Fixture Mod");
        Directory.CreateDirectory(modFolder);
        try
        {
            string manifest = Path.Combine(modFolder, "Fixture.json");
            string dll = Path.Combine(modFolder, "Fixture.dll");
            File.WriteAllText(manifest,
                """{"id":"FixtureMod","name":"Fixture Mod","version":"1.0.0","has_dll":true,"has_pck":false}""");
            File.WriteAllText(dll, "fixture dll v1");
            var mod = new ManagedMod(
                "FixtureMod", "Fixture Mod", "1.0.0", "Test Author", "",
                modFolder, manifest, true, []);

            var sources = new WorkshopModSources(dataRoot, gameRoot);
            sources.Bind(mod.Id, WorkshopId);

            int requestsWithoutBaseline = 0;
            var noBaselineService = new WorkshopModUpdates(() =>
                new SpireSupplyStationClient(new Handler(request =>
                {
                    requestsWithoutBaseline++;
                    return Json(request, Descriptor("1001"));
                })));
            var pending = await noBaselineService.CheckAsync(mod, WorkshopId, sources, force: true);
            Check(pending.Status == "待首次核验" && requestsWithoutBaseline == 0,
                "legacy source bindings are not guessed as current and require a verified first baseline");

            var initialFiles = ModManager.FileSnapshot(modFolder);
            sources.RecordBaseline(mod.Id, WorkshopId, "1001", mod.Version, initialFiles);
            var reloadedSources = new WorkshopModSources(dataRoot, gameRoot);
            Check(reloadedSources.Get(mod.Id) == WorkshopId &&
                reloadedSources.GetBaseline(mod.Id)?.ManifestId == "1001" &&
                reloadedSources.IsBaselineCurrent(mod, WorkshopId),
                "Workshop source and verified Manifest baseline survive restart");

            int sameManifestRequests = 0;
            var sameManifestService = new WorkshopModUpdates(() =>
                new SpireSupplyStationClient(new Handler(request =>
                {
                    sameManifestRequests++;
                    return Json(request, Descriptor("1001"));
                })));
            var latest = await sameManifestService.CheckAsync(mod, WorkshopId, reloadedSources, force: true);
            Check(latest.Status == "已是最新" && sameManifestRequests == 1,
                "unchanged remote Manifest is reported as current after a verified local snapshot");

            int changedManifestRequests = 0;
            var changedManifestService = new WorkshopModUpdates(() =>
                new SpireSupplyStationClient(new Handler(request =>
                {
                    changedManifestRequests++;
                    return Json(request, Descriptor("1002"));
                })));
            var changed = await changedManifestService.CheckAsync(mod, WorkshopId, reloadedSources, force: true);
            Check(changed.Status == "有更新" && changed.RemoteManifestId == "1002" &&
                changedManifestRequests == 1,
                "a changed remote Manifest is reported as an update without downloading MOD content");

            File.WriteAllText(dll, "manual local edit");
            int requestsAfterLocalEdit = 0;
            var localEditService = new WorkshopModUpdates(() =>
                new SpireSupplyStationClient(new Handler(request =>
                {
                    requestsAfterLocalEdit++;
                    return Json(request, Descriptor("1002"));
                })));
            var localEdit = await localEditService.CheckAsync(mod, WorkshopId, reloadedSources, force: true);
            Check(localEdit.Status == "本地文件已变化" && requestsAfterLocalEdit == 0,
                "manually changed local files invalidate the baseline and prevent false update claims");

            reloadedSources.Unbind(mod.Id);
            var afterUnbind = new WorkshopModSources(dataRoot, gameRoot);
            Check(afterUnbind.Get(mod.Id) is null && afterUnbind.GetBaseline(mod.Id) is null,
                "unbinding a Workshop source also removes its persisted version baseline");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    static HttpResponseMessage Json(HttpRequestMessage request, string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };

    static string Descriptor(string manifestId) =>
        JsonSerializer.Serialize(new
        {
            mode = "UGC",
            appId = AppId,
            publishedFileId = WorkshopId.ToString(),
            title = "Fixture Workshop Mod",
            fileName = "FixtureMod.zip",
            fileSizeBytes = 1024L,
            contentVersion = "fixture-content-v1",
            directUrl = (string?)null,
            manifestId,
            depotId = 2868840u,
            requestCode = "123456789",
            endpoints = new[]
            {
                new { origin = "http://cdn.example", query = "bootstrap=fixture-secret" }
            },
            depotKeyBase64 = DepotKey,
            expiresAt = Future
        });
}
