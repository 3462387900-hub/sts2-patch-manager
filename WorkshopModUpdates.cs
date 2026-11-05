using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace SpirePatch;

internal sealed record WorkshopModUpdateResult(
    ulong WorkshopId,
    string LocalVersion,
    string RemoteTitle,
    string RemoteContentVersion,
    string RemoteManifestId,
    string Status,
    string Message,
    DateTimeOffset CheckedAt);

/// <summary>
/// Read-only Workshop update checks. It compares the current public Manifest ID
/// with a baseline recorded only after the local MOD payload has been verified.
/// </summary>
internal sealed class WorkshopModUpdates
{
    readonly Dictionary<string, WorkshopModUpdateResult> checks = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<SpireSupplyStationClient> clientFactory;
    static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(15);

    public WorkshopModUpdates(Func<SpireSupplyStationClient>? clientFactory = null)
    {
        this.clientFactory = clientFactory ?? (() => new SpireSupplyStationClient());
    }

    public WorkshopModUpdateResult? Cached(string modId) => checks.GetValueOrDefault(modId);

    public void Invalidate(string modId) => checks.Remove(modId);

    public async Task<WorkshopModUpdateResult> CheckAsync(
        ManagedMod mod,
        ulong workshopId,
        WorkshopModSources sources,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (workshopId == 0 || sources.Get(mod.Id) != workshopId)
            throw new InvalidOperationException("当前 MOD 尚未关联这个 Workshop ID。");

        var now = DateTimeOffset.UtcNow;
        var baseline = sources.GetBaseline(mod.Id);
        if (baseline is null || baseline.WorkshopId != workshopId)
            return Store(mod.Id, new WorkshopModUpdateResult(
                workshopId, mod.Version, "", "", "", "待首次核验",
                "该 MOD 在版本基线功能加入前已关联来源。首次核验会完整下载并对比文件；确认一致后才建立基线。",
                now));

        bool baselineCurrent = await Task.Run(
            () => sources.IsBaselineCurrent(mod, workshopId),
            cancellationToken);
        if (!baselineCurrent)
            return Store(mod.Id, new WorkshopModUpdateResult(
                workshopId, mod.Version, "", "", "", "本地文件已变化",
                "本地版本或文件内容与上次验证时不同。需要重新下载并核对文件，不能直接判断线上是否有更新。",
                now));

        if (!force &&
            checks.TryGetValue(mod.Id, out var cached) &&
            cached.WorkshopId == workshopId &&
            cached.LocalVersion == mod.Version &&
            now - cached.CheckedAt < CacheAge)
            return cached;

        try
        {
            using var client = clientFactory();
            var remote = await client.ReadContentIdentityAsync(
                SpireSupplyStationClient.SupportedAppId,
                workshopId,
                cancellationToken);

            if (!remote.Mode.Equals("UGC", StringComparison.Ordinal) ||
                !ulong.TryParse(remote.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong remoteManifestNumber))
                return Store(mod.Id, new WorkshopModUpdateResult(
                    workshopId, mod.Version, remote.Title, remote.ContentVersion, "",
                    "暂不支持",
                    "该条目使用非 UGC 下载模式或缺少有效 Manifest；当前管理器只对 UGC Manifest 提供更新检测。",
                    DateTimeOffset.UtcNow));

            string remoteManifestId = remoteManifestNumber.ToString(CultureInfo.InvariantCulture);
            bool baselineValid = ulong.TryParse(
                baseline.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong baselineManifestNumber);
            bool changed = !baselineValid || remoteManifestNumber != baselineManifestNumber;
            return Store(mod.Id, new WorkshopModUpdateResult(
                workshopId,
                mod.Version,
                remote.Title,
                remote.ContentVersion,
                remoteManifestId,
                changed ? "有更新" : "已是最新",
                changed
                    ? "创意工坊 Manifest 已变化。点击获取更新预览后，会重新下载、校验并展示实际 MOD 版本；不会自动覆盖本地文件。"
                    : "创意工坊 Manifest 与上次已验证安装的版本一致。",
                DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or
                                      InvalidOperationException or TimeoutException or IOException or
                                      JsonException or OperationCanceledException)
        {
            return Store(mod.Id, new WorkshopModUpdateResult(
                workshopId, mod.Version, "", "", "", "检查失败",
                "创意工坊检查未完成：" + error.Message,
                DateTimeOffset.UtcNow));
        }
    }

    public async Task<int> CheckAllAsync(
        IEnumerable<ManagedMod> mods,
        WorkshopModSources sources,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var mod in mods
            .Where(item => !ModPresentation.IsIndependentPatch(item))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workshopId = sources.Get(mod.Id);
            if (workshopId is null)
                continue;

            await CheckAsync(mod, workshopId.Value, sources, cancellationToken: cancellationToken);
            count++;
        }

        return count;
    }

    WorkshopModUpdateResult Store(string modId, WorkshopModUpdateResult result)
    {
        checks[modId] = result;
        return result;
    }
}
