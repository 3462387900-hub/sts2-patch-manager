using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

internal enum ModSourceKind
{
    GitHub,
    Workshop
}

internal enum ModSourceTrustLevel
{
    Unknown,
    Unbound,
    UserConfirmed,
    ManifestDeclared,
    RemoteManifestVerified,
    UnverifiedCandidate
}

internal enum ModSourceCheckState
{
    Unbound,
    NotChecked,
    CandidatesFound,
    NoCandidate,
    UpdateAvailable,
    UpToDate,
    NeedsReview,
    Failed,
    Unsupported,
    CacheExpired
}

internal enum ModSourceCacheState
{
    Missing,
    Fresh,
    Expired
}

internal enum ModSourceFailureKind
{
    None,
    Network,
    Timeout,
    RateLimited,
    InvalidResponse,
    LocalRead,
    SourceUnavailable,
    Unsupported,
    Unknown
}

internal enum WorkshopBaselineState
{
    Unbound,
    Missing,
    Current,
    LocalVersionChanged
}

internal sealed record ModSourceCandidateDiagnostic(
    string Repository,
    string Reason,
    int Confidence,
    ModSourceTrustLevel Trust);

internal sealed record ModSourceEntryDiagnostic(
    ModSourceKind SourceType,
    bool IsBound,
    string? Identifier,
    ModSourceTrustLevel Trust,
    ModSourceCheckState State,
    string Status,
    string LocalVersion,
    string LatestVersion,
    string? Url,
    string? RemoteManifestId,
    ModSourceCacheState CacheState,
    bool CacheHit,
    bool CanUpdate,
    bool NeedsReview,
    ModSourceFailureKind FailureKind,
    string? FailureReason,
    DateTimeOffset? CheckedAt,
    IReadOnlyList<ModSourceCandidateDiagnostic> Candidates,
    string Message = "");

internal sealed record ModSourceCheckResult(
    string GameRoot,
    string ModFolder,
    string ModId,
    string LocalVersion,
    ModSourceEntryDiagnostic GitHub,
    ModSourceEntryDiagnostic Workshop,
    bool IsStale = false,
    bool Cancelled = false)
{
    internal bool HasUpdate => GitHub.CanUpdate || Workshop.CanUpdate;
    internal bool GitHubBound => GitHub.IsBound;
    internal bool WorkshopBound => Workshop.IsBound;
    internal bool WorkshopNeedsReview => Workshop.NeedsReview;

    internal string SummaryText
    {
        get
        {
            var parts = new List<string>();
            if (GitHub.IsBound)
            {
                string status = GitHub.State == ModSourceCheckState.NotChecked
                    ? "已关联 · 待检查"
                    : GitHub.Status + (GitHub.LatestVersion.Length > 0
                        ? " · " + GitHub.LatestVersion
                        : "");
                parts.Add("GitHub：" + status);
            }
            else if (GitHub.State == ModSourceCheckState.Failed)
                parts.Add("GitHub：查找受限，可重试");
            else if (GitHub.State == ModSourceCheckState.CacheExpired)
                parts.Add("GitHub：来源缓存已过期，可重新查找");
            else if (GitHub.Candidates.Count > 0)
            {
                bool verified = GitHub.Candidates.Any(candidate =>
                    candidate.Trust is ModSourceTrustLevel.ManifestDeclared or
                        ModSourceTrustLevel.RemoteManifestVerified);
                parts.Add("GitHub：" + (verified ? "已核验来源" : "疑似来源") +
                    " " + GitHub.Candidates.Count + " 个 · 待确认");
            }
            else if (GitHub.State == ModSourceCheckState.NoCandidate)
                parts.Add("GitHub：暂无来源");

            if (Workshop.IsBound)
            {
                string status = Workshop.State == ModSourceCheckState.NotChecked
                    ? "已关联 · 待检查"
                    : Workshop.Status;
                parts.Add("创意工坊：" + status);
            }

            return string.Join(" · ", parts);
        }
    }
}

internal sealed record ModSourceBackendGitHubCheck(
    string Repository,
    string LocalVersion,
    string LatestVersion,
    string ReleaseUrl,
    string Status,
    string Message,
    DateTimeOffset CheckedAt);

internal sealed record ModSourceBackendDiscoveryCandidate(
    string Repository,
    string Reason,
    int Score,
    bool Verified);

internal sealed record ModSourceBackendDiscoveryResult(
    string ModId,
    ModSourceBackendDiscoveryCandidate[] Candidates,
    string Note,
    bool Retryable = false);

internal sealed record ModSourceBackendWorkshopCheck(
    ulong WorkshopId,
    string LocalVersion,
    string RemoteTitle,
    string RemoteContentVersion,
    string RemoteManifestId,
    string Status,
    string Message,
    DateTimeOffset CheckedAt);

internal sealed record ModSourceBackendSnapshot(
    string? GitHubRepository,
    ModSourceBackendGitHubCheck? GitHubCached,
    ModSourceBackendDiscoveryResult? GitHubDiscoveryCached,
    ulong? WorkshopId,
    ModSourceBackendWorkshopCheck? WorkshopCached,
    WorkshopBaselineState WorkshopBaseline);

internal interface IModSourceCheckBackend
{
    ModSourceBackendSnapshot Read(ManagedMod mod);

    Task<ModSourceBackendDiscoveryResult> DiscoverGitHubAsync(
        ManagedMod mod,
        CancellationToken cancellationToken);

    Task<ModSourceBackendGitHubCheck> CheckGitHubAsync(
        ManagedMod mod,
        CancellationToken cancellationToken);

    Task<ModSourceBackendWorkshopCheck> CheckWorkshopAsync(
        ManagedMod mod,
        ulong workshopId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates source discovery and read-only update checks. Per-MOD/per-source
/// generations prevent superseded requests from publishing over newer results.
/// A service instance belongs to one game-root scope.
/// </summary>
internal sealed class ModSourceCheckWorkflowService
{
    static readonly TimeSpan GitHubCacheAge = TimeSpan.FromMinutes(30);
    static readonly TimeSpan WorkshopCacheAge = TimeSpan.FromMinutes(15);
    static readonly TimeSpan DiscoveryPositiveCacheAge = TimeSpan.FromHours(6);
    static readonly TimeSpan DiscoveryNegativeCacheAge = TimeSpan.FromMinutes(30);

    sealed record Failure(
        ModSourceFailureKind Kind,
        string Reason,
        DateTimeOffset At);

    sealed record PublishedState(
        string? GitHubRepository = null,
        ModSourceBackendGitHubCheck? GitHubResult = null,
        DateTimeOffset? GitHubCheckedAt = null,
        Failure? GitHubFailure = null,
        ModSourceBackendDiscoveryResult? DiscoveryResult = null,
        DateTimeOffset? DiscoveryCheckedAt = null,
        Failure? DiscoveryFailure = null,
        ulong? WorkshopId = null,
        ModSourceBackendWorkshopCheck? WorkshopResult = null,
        DateTimeOffset? WorkshopCheckedAt = null,
        Failure? WorkshopFailure = null);

    sealed record ActiveOperation(long Generation, CancellationTokenSource Cancellation);

    sealed record Execution<T>(
        T? Value,
        Exception? Error,
        bool Cancelled,
        bool Stale,
        bool Applied) where T : class;

    readonly string gameRoot;
    readonly IModSourceCheckBackend backend;
    readonly Func<DateTimeOffset> utcNow;
    readonly object gate = new();
    readonly Dictionary<string, PublishedState> published =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ActiveOperation> active =
        new(StringComparer.OrdinalIgnoreCase);
    long nextGeneration;
    long scopeGeneration;

    public ModSourceCheckWorkflowService(
        string gameRoot,
        IModSourceCheckBackend backend,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        this.gameRoot = NormalizePath(gameRoot);
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public ModSourceCheckResult Inspect(ManagedMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var snapshot = backend.Read(mod);
        string key = ModKey(mod);
        PublishedState state = GetOrSeedPublished(key, snapshot);
        bool githubMatches = string.Equals(
            state.GitHubRepository, snapshot.GitHubRepository, StringComparison.OrdinalIgnoreCase);
        bool workshopMatches = state.WorkshopId == snapshot.WorkshopId;

        ModSourceBackendGitHubCheck? githubResult = githubMatches
            ? state.GitHubResult ?? snapshot.GitHubCached
            : snapshot.GitHubCached;
        Failure? githubFailure = githubMatches ? state.GitHubFailure : null;
        ModSourceBackendDiscoveryResult? discoveryResult = githubMatches
            ? state.DiscoveryResult ?? snapshot.GitHubDiscoveryCached
            : snapshot.GitHubDiscoveryCached;
        DateTimeOffset? discoveryCheckedAt = githubMatches
            ? state.DiscoveryCheckedAt
            : null;
        Failure? discoveryFailure = githubMatches ? state.DiscoveryFailure : null;

        ModSourceBackendWorkshopCheck? workshopResult = workshopMatches
            ? state.WorkshopResult ?? snapshot.WorkshopCached
            : snapshot.WorkshopCached;
        Failure? workshopFailure = workshopMatches ? state.WorkshopFailure : null;

        var github = BuildGitHubEntry(
            mod, snapshot.GitHubRepository, githubResult, githubFailure,
            discoveryResult, discoveryCheckedAt, discoveryFailure);

        var workshop = BuildWorkshopEntry(
            mod, snapshot.WorkshopId, snapshot.WorkshopBaseline,
            workshopResult, workshopFailure);

        return new ModSourceCheckResult(
            gameRoot, NormalizePath(mod.Folder), mod.Id, mod.Version, github, workshop);
    }

    public async Task<ModSourceCheckResult> DiscoverGitHubAsync(
        ManagedMod mod,
        bool force = false,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default)
    {
        var before = Inspect(mod);
        if (before.GitHub.IsBound)
            return before;

        if (!force && before.GitHub.CacheState == ModSourceCacheState.Fresh &&
            before.GitHub.State is ModSourceCheckState.CandidatesFound or ModSourceCheckState.NoCandidate)
            return before with { GitHub = before.GitHub with { CacheHit = true } };

        string key = ModKey(mod);
        var execution = await ExecuteLatestAsync(
            key + "|github-discovery",
            token => backend.DiscoverGitHubAsync(mod, token),
            result => PublishDiscovery(key, before.GitHub.Identifier, result),
            error => PublishDiscoveryFailure(key, before.GitHub.Identifier, error),
            isCurrent,
            cancellationToken);

        var result = Inspect(mod);
        if (execution.Cancelled || execution.Stale)
            return result with { Cancelled = execution.Cancelled, IsStale = execution.Stale };

        if (!execution.Applied && execution.Error is not null)
            return result;

        return result with
        {
            GitHub = result.GitHub with { CacheHit = false }
        };
    }

    public async Task<ModSourceCheckResult> CheckGitHubAsync(
        ManagedMod mod,
        bool force = false,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default)
    {
        var before = Inspect(mod);
        if (!before.GitHub.IsBound)
            return before;

        if (!force && before.GitHub.CacheState == ModSourceCacheState.Fresh &&
            before.GitHub.State != ModSourceCheckState.Failed)
            return before with { GitHub = before.GitHub with { CacheHit = true } };

        string key = ModKey(mod);
        var execution = await ExecuteLatestAsync(
            key + "|github-check",
            token => backend.CheckGitHubAsync(mod, token),
            result => PublishGitHubResult(key, before.GitHub.Identifier, result),
            error => PublishGitHubFailure(key, before.GitHub.Identifier, error),
            isCurrent,
            cancellationToken);

        var result = Inspect(mod);
        if (execution.Cancelled || execution.Stale)
            return result with { Cancelled = execution.Cancelled, IsStale = execution.Stale };

        if (!execution.Applied && execution.Error is not null)
            return result;

        return result with
        {
            GitHub = result.GitHub with { CacheHit = false }
        };
    }

    public async Task<ModSourceCheckResult> CheckWorkshopAsync(
        ManagedMod mod,
        bool force = false,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default)
    {
        var before = Inspect(mod);
        if (!before.Workshop.IsBound ||
            !ulong.TryParse(before.Workshop.Identifier, out ulong workshopId) ||
            workshopId == 0)
            return before;

        if (!force && before.Workshop.CacheState == ModSourceCacheState.Fresh &&
            before.Workshop.State != ModSourceCheckState.Failed)
            return before with { Workshop = before.Workshop with { CacheHit = true } };

        string key = ModKey(mod);
        var execution = await ExecuteLatestAsync(
            key + "|workshop-check",
            token => backend.CheckWorkshopAsync(mod, workshopId, token),
            result => PublishWorkshopResult(key, workshopId, result),
            error => PublishWorkshopFailure(key, workshopId, error),
            isCurrent,
            cancellationToken);

        var result = Inspect(mod);
        if (execution.Cancelled || execution.Stale)
            return result with { Cancelled = execution.Cancelled, IsStale = execution.Stale };

        if (!execution.Applied && execution.Error is not null)
            return result;

        return result with
        {
            Workshop = result.Workshop with { CacheHit = false }
        };
    }

    public async Task<ModSourceCheckBatchResult> CheckAllAsync(
        IEnumerable<ManagedMod> mods,
        bool force = false,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mods);
        int githubCount = 0;
        int workshopCount = 0;
        var results = new List<ModSourceCheckResult>();

        foreach (var mod in mods
            .Where(item => !ModPresentation.IsIndependentPatch(item))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(isCurrent))
                return CreateBatchResult(results, githubCount, workshopCount, stale: true);

            var current = Inspect(mod);
            if (current.GitHub.IsBound)
            {
                current = await CheckGitHubAsync(
                    mod, force, isCurrent, cancellationToken);
                if (current.Cancelled || current.IsStale)
                    return CreateBatchResult(
                        results.Append(current), githubCount, workshopCount,
                        stale: current.IsStale, cancelled: current.Cancelled);
                githubCount++;
            }

            if (current.Workshop.IsBound)
            {
                current = await CheckWorkshopAsync(
                    mod, force, isCurrent, cancellationToken);
                if (current.Cancelled || current.IsStale)
                    return CreateBatchResult(
                        results.Append(current), githubCount, workshopCount,
                        stale: current.IsStale, cancelled: current.Cancelled);
                workshopCount++;
            }

            results.Add(Inspect(mod));
        }

        return CreateBatchResult(results, githubCount, workshopCount);
    }

    public void CancelAll()
    {
        lock (gate)
        {
            scopeGeneration++;
            foreach (var operation in active.Values)
                operation.Cancellation.Cancel();
            active.Clear();
        }
    }

    public void Invalidate(ManagedMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        string key = ModKey(mod);
        lock (gate)
        {
            published.Remove(key);
            CancelForKey(key);
        }
    }

    public void InvalidateAll()
    {
        lock (gate)
        {
            published.Clear();
            scopeGeneration++;
            foreach (var operation in active.Values)
                operation.Cancellation.Cancel();
            active.Clear();
        }
    }

    PublishedState GetOrSeedPublished(string key, ModSourceBackendSnapshot snapshot)
    {
        lock (gate)
        {
            if (published.TryGetValue(key, out var existing))
                return existing;

            var seeded = new PublishedState(
                GitHubRepository: snapshot.GitHubRepository,
                GitHubResult: IsGitHubFailure(snapshot.GitHubCached) ? null : snapshot.GitHubCached,
                GitHubCheckedAt: IsGitHubFailure(snapshot.GitHubCached)
                    ? null
                    : snapshot.GitHubCached?.CheckedAt,
                GitHubFailure: IsGitHubFailure(snapshot.GitHubCached)
                    ? FailureFromMessage(snapshot.GitHubCached!.Message, snapshot.GitHubCached.CheckedAt)
                    : null,
                DiscoveryResult: snapshot.GitHubDiscoveryCached is { Retryable: false }
                    ? snapshot.GitHubDiscoveryCached
                    : null,
                DiscoveryCheckedAt: snapshot.GitHubDiscoveryCached is { Retryable: false }
                    ? utcNow()
                    : null,
                DiscoveryFailure: snapshot.GitHubDiscoveryCached is { Retryable: true }
                    ? FailureFromMessage(snapshot.GitHubDiscoveryCached.Note, utcNow())
                    : null,
                WorkshopId: snapshot.WorkshopId,
                WorkshopResult: IsWorkshopFailure(snapshot.WorkshopCached)
                    ? null
                    : snapshot.WorkshopCached,
                WorkshopCheckedAt: IsWorkshopFailure(snapshot.WorkshopCached)
                    ? null
                    : snapshot.WorkshopCached?.CheckedAt,
                WorkshopFailure: IsWorkshopFailure(snapshot.WorkshopCached)
                    ? FailureFromMessage(snapshot.WorkshopCached!.Message, snapshot.WorkshopCached.CheckedAt)
                    : null);

            published[key] = seeded;
            return seeded;
        }
    }

    async Task<Execution<T>> ExecuteLatestAsync<T>(
        string operationKey,
        Func<CancellationToken, Task<T>> execute,
        Action<T> publishValue,
        Action<Exception> publishError,
        Func<bool>? isCurrent,
        CancellationToken cancellationToken) where T : class
    {
        CancellationTokenSource linked;
        long operationGeneration;
        long currentScope;

        lock (gate)
        {
            if (active.TryGetValue(operationKey, out var previous))
                previous.Cancellation.Cancel();

            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationGeneration = ++nextGeneration;
            currentScope = scopeGeneration;
            active[operationKey] = new ActiveOperation(operationGeneration, linked);
        }

        bool IsOwner()
        {
            lock (gate)
                return currentScope == scopeGeneration &&
                    active.TryGetValue(operationKey, out var current) &&
                    current.Generation == operationGeneration &&
                    ReferenceEquals(current.Cancellation, linked);
        }

        bool CanPublish()
        {
            if (linked.IsCancellationRequested || !IsOwner())
                return false;
            return IsCurrent(isCurrent);
        }

        try
        {
            if (!CanPublish())
                return new Execution<T>(null, null, linked.IsCancellationRequested, true, false);

            T value = await execute(linked.Token);
            if (linked.IsCancellationRequested || !IsCurrent(isCurrent))
                return new Execution<T>(value, null, linked.IsCancellationRequested, true, false);

            lock (gate)
            {
                if (currentScope != scopeGeneration ||
                    !active.TryGetValue(operationKey, out var current) ||
                    current.Generation != operationGeneration ||
                    !ReferenceEquals(current.Cancellation, linked) ||
                    linked.IsCancellationRequested ||
                    !IsCurrent(isCurrent))
                    return new Execution<T>(value, null, linked.IsCancellationRequested, true, false);

                // The generation check and publication are atomic with respect to a newer request.
                publishValue(value);
            }
            return new Execution<T>(value, null, false, false, true);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested || !IsOwner())
        {
            return new Execution<T>(null, null, true, !IsOwner(), false);
        }
        catch (Exception error)
        {
            bool applied = false;
            lock (gate)
            {
                if (currentScope == scopeGeneration &&
                    active.TryGetValue(operationKey, out var current) &&
                    current.Generation == operationGeneration &&
                    ReferenceEquals(current.Cancellation, linked) &&
                    !linked.IsCancellationRequested &&
                    IsCurrent(isCurrent))
                {
                    publishError(error);
                    applied = true;
                }
            }
            return new Execution<T>(null, error, false, !applied, applied);
        }
        finally
        {
            lock (gate)
            {
                if (active.TryGetValue(operationKey, out var current) &&
                    current.Generation == operationGeneration &&
                    ReferenceEquals(current.Cancellation, linked))
                    active.Remove(operationKey);
            }
            linked.Dispose();
        }
    }

    void PublishGitHubResult(string key, string? repository, ModSourceBackendGitHubCheck result)
    {
        lock (gate)
        {
            var current = published.GetValueOrDefault(key) ?? new PublishedState();
            current = current with { GitHubRepository = repository };
            if (IsGitHubFailure(result))
            {
                current = current with
                {
                    GitHubFailure = FailureFromMessage(result.Message, result.CheckedAt)
                };
            }
            else
            {
                current = current with
                {
                    GitHubResult = result,
                    GitHubCheckedAt = result.CheckedAt,
                    GitHubFailure = null
                };
            }
            published[key] = current;
        }
    }

    void PublishGitHubFailure(string key, string? repository, Exception error)
    {
        lock (gate)
        {
            var current = published.GetValueOrDefault(key) ?? new PublishedState();
            published[key] = current with
            {
                GitHubRepository = repository,
                GitHubFailure = FailureFromException(error, utcNow())
            };
        }
    }

    void PublishDiscovery(string key, string? repository, ModSourceBackendDiscoveryResult result)
    {
        lock (gate)
        {
            var current = published.GetValueOrDefault(key) ?? new PublishedState();
            current = current with { GitHubRepository = repository };
            if (result.Retryable)
            {
                current = current with
                {
                    DiscoveryFailure = FailureFromMessage(result.Note, utcNow())
                };
            }
            else
            {
                current = current with
                {
                    DiscoveryResult = result,
                    DiscoveryCheckedAt = utcNow(),
                    DiscoveryFailure = null
                };
            }
            published[key] = current;
        }
    }

    void PublishDiscoveryFailure(string key, string? repository, Exception error)
    {
        lock (gate)
        {
            var current = published.GetValueOrDefault(key) ?? new PublishedState();
            published[key] = current with
            {
                GitHubRepository = repository,
                DiscoveryFailure = FailureFromException(error, utcNow())
            };
        }
    }

    void PublishWorkshopResult(string key, ulong workshopId, ModSourceBackendWorkshopCheck result)
    {
        lock (gate)
        {
            var current = published.GetValueOrDefault(key) ?? new PublishedState();
            current = current with { WorkshopId = workshopId };
            if (IsWorkshopFailure(result))
            {
                current = current with
                {
                    WorkshopFailure = FailureFromMessage(result.Message, result.CheckedAt)
                };
            }
            else
            {
                current = current with
                {
                    WorkshopResult = result,
                    WorkshopCheckedAt = result.CheckedAt,
                    WorkshopFailure = null
                };
            }
            published[key] = current;
        }
    }

    void PublishWorkshopFailure(string key, ulong workshopId, Exception error)
    {
        lock (gate)
        {
            var current = published.GetValueOrDefault(key) ?? new PublishedState();
            published[key] = current with
            {
                WorkshopId = workshopId,
                WorkshopFailure = FailureFromException(error, utcNow())
            };
        }
    }

    ModSourceEntryDiagnostic BuildGitHubEntry(
        ManagedMod mod,
        string? repository,
        ModSourceBackendGitHubCheck? result,
        Failure? failure,
        ModSourceBackendDiscoveryResult? discovery,
        DateTimeOffset? discoveryCheckedAt,
        Failure? discoveryFailure)
    {
        if (!string.IsNullOrWhiteSpace(repository))
        {
            if (failure is not null)
                return FailedEntry(
                    ModSourceKind.GitHub, repository, mod.Version,
                    result?.LatestVersion ?? "", result?.ReleaseUrl,
                    failure, result?.CheckedAt);

            if (result is null)
                return Entry(
                    ModSourceKind.GitHub, true, repository, ModSourceTrustLevel.UserConfirmed,
                    ModSourceCheckState.NotChecked, "已关联 · 待检查", mod.Version, "", null, null,
                    ModSourceCacheState.Missing, false, false, false,
                    ModSourceFailureKind.None, null, null, []);

            bool fresh = IsFresh(result.CheckedAt, GitHubCacheAge) &&
                result.LocalVersion.Equals(mod.Version, StringComparison.Ordinal);
            if (!fresh)
                return Entry(
                    ModSourceKind.GitHub, true, repository, ModSourceTrustLevel.UserConfirmed,
                    ModSourceCheckState.CacheExpired, "缓存已过期 · " + result.Status,
                    mod.Version, result.LatestVersion, result.ReleaseUrl, null,
                    ModSourceCacheState.Expired, false, false, true,
                    ModSourceFailureKind.None, null, result.CheckedAt, [], result.Message);

            var state = result.Status switch
            {
                "有更新" => ModSourceCheckState.UpdateAvailable,
                "已是最新" => ModSourceCheckState.UpToDate,
                "无法检测" => ModSourceCheckState.Failed,
                _ => ModSourceCheckState.NeedsReview
            };
            return Entry(
                ModSourceKind.GitHub, true, repository, ModSourceTrustLevel.UserConfirmed,
                state, result.Status, mod.Version, result.LatestVersion, result.ReleaseUrl, null,
                ModSourceCacheState.Fresh, true,
                state == ModSourceCheckState.UpdateAvailable,
                state == ModSourceCheckState.NeedsReview,
                ModSourceFailureKind.None, null, result.CheckedAt, [], result.Message);
        }

        var candidates = (discovery?.Candidates ?? [])
            .Select(candidate => new ModSourceCandidateDiagnostic(
                candidate.Repository,
                candidate.Reason,
                candidate.Score,
                candidate.Reason.StartsWith("MOD 清单声明", StringComparison.Ordinal)
                    ? ModSourceTrustLevel.ManifestDeclared
                    : candidate.Verified
                        ? ModSourceTrustLevel.RemoteManifestVerified
                        : ModSourceTrustLevel.UnverifiedCandidate))
            .ToArray();

        if (discoveryFailure is not null)
            return Entry(
                ModSourceKind.GitHub, false, null,
                candidates.FirstOrDefault()?.Trust ?? ModSourceTrustLevel.Unknown,
                ModSourceCheckState.Failed, "查找受限", mod.Version, "", null, null,
                ModSourceCacheState.Missing, false, false, false,
                discoveryFailure.Kind, discoveryFailure.Reason, discoveryFailure.At, candidates);

        if (discovery is null)
            return Entry(
                ModSourceKind.GitHub, false, null, ModSourceTrustLevel.Unbound,
                ModSourceCheckState.Unbound, "未关联来源", mod.Version, "", null, null,
                ModSourceCacheState.Missing, false, false, false,
                ModSourceFailureKind.None, null, null, []);

        TimeSpan discoveryAge = candidates.Length > 0
            ? DiscoveryPositiveCacheAge
            : DiscoveryNegativeCacheAge;
        DateTimeOffset checkedAt = discoveryCheckedAt ?? utcNow();
        bool discoveryFresh = IsFresh(checkedAt, discoveryAge);
        if (!discoveryFresh)
            return Entry(
                ModSourceKind.GitHub, false, null,
                candidates.FirstOrDefault()?.Trust ?? ModSourceTrustLevel.Unbound,
                ModSourceCheckState.CacheExpired, "来源缓存已过期",
                mod.Version, "", null, null,
                ModSourceCacheState.Expired, false, false, false,
                ModSourceFailureKind.None, null, checkedAt, candidates);

        if (candidates.Length > 0)
            return Entry(
                ModSourceKind.GitHub, false, null, candidates[0].Trust,
                ModSourceCheckState.CandidatesFound, "候选待确认",
                mod.Version, "", null, null,
                ModSourceCacheState.Fresh, true, false, true,
                ModSourceFailureKind.None, discovery.Note, checkedAt, candidates, discovery.Note);

        return Entry(
            ModSourceKind.GitHub, false, null, ModSourceTrustLevel.Unbound,
            ModSourceCheckState.NoCandidate, "暂无来源",
            mod.Version, "", null, null,
            ModSourceCacheState.Fresh, true, false, false,
            ModSourceFailureKind.None, discovery.Note, checkedAt, [], discovery.Note);
    }

    ModSourceEntryDiagnostic BuildWorkshopEntry(
        ManagedMod mod,
        ulong? workshopId,
        WorkshopBaselineState baseline,
        ModSourceBackendWorkshopCheck? result,
        Failure? failure)
    {
        if (!workshopId.HasValue || workshopId.Value == 0)
            return Entry(
                ModSourceKind.Workshop, false, null, ModSourceTrustLevel.Unbound,
                ModSourceCheckState.Unbound, "未关联来源", mod.Version, "", null, null,
                ModSourceCacheState.Missing, false, false, false,
                ModSourceFailureKind.None, null, null, []);

        string identifier = workshopId.Value.ToString();
        if (baseline == WorkshopBaselineState.Missing)
        {
            bool reviewCached = result is not null &&
                result.WorkshopId == workshopId.Value &&
                result.LocalVersion.Equals(mod.Version, StringComparison.Ordinal) &&
                result.Status == "待首次核验" &&
                IsFresh(result.CheckedAt, WorkshopCacheAge);
            return Entry(
                ModSourceKind.Workshop, true, identifier, ModSourceTrustLevel.UserConfirmed,
                ModSourceCheckState.NeedsReview, "待首次核验", mod.Version, "", null, null,
                reviewCached ? ModSourceCacheState.Fresh : ModSourceCacheState.Missing,
                reviewCached, false, true,
                ModSourceFailureKind.None, null, result?.CheckedAt, [],
                result?.Message ?? "尚未建立已验证的本地版本基线。");
        }

        if (baseline == WorkshopBaselineState.LocalVersionChanged)
        {
            bool reviewCached = result is not null &&
                result.WorkshopId == workshopId.Value &&
                result.LocalVersion.Equals(mod.Version, StringComparison.Ordinal) &&
                result.Status == "本地文件已变化" &&
                IsFresh(result.CheckedAt, WorkshopCacheAge);
            return Entry(
                ModSourceKind.Workshop, true, identifier, ModSourceTrustLevel.UserConfirmed,
                ModSourceCheckState.NeedsReview, "本地文件已变化", mod.Version, "", null, null,
                reviewCached ? ModSourceCacheState.Fresh : ModSourceCacheState.Expired,
                reviewCached, false, true,
                ModSourceFailureKind.None, null, result?.CheckedAt, [],
                result?.Message ?? "本地版本与已验证的版本基线不一致。");
        }

        if (failure is not null)
            return FailedEntry(
                ModSourceKind.Workshop, identifier, mod.Version,
                result?.RemoteContentVersion ?? result?.RemoteManifestId ?? "",
                null, failure, result?.CheckedAt);

        if (result is null)
            return Entry(
                ModSourceKind.Workshop, true, identifier, ModSourceTrustLevel.UserConfirmed,
                ModSourceCheckState.NotChecked, "已关联 · 待检查", mod.Version, "", null, null,
                ModSourceCacheState.Missing, false, false, false,
                ModSourceFailureKind.None, null, null, []);

        bool fresh = IsFresh(result.CheckedAt, WorkshopCacheAge) &&
            result.WorkshopId == workshopId.Value &&
            result.LocalVersion.Equals(mod.Version, StringComparison.Ordinal);
        if (!fresh)
            return Entry(
                ModSourceKind.Workshop, true, identifier, ModSourceTrustLevel.UserConfirmed,
                ModSourceCheckState.CacheExpired, "缓存已过期 · " + result.Status,
                mod.Version,
                result.RemoteContentVersion.Length > 0
                    ? result.RemoteContentVersion
                    : result.RemoteManifestId,
                null, result.RemoteManifestId,
                ModSourceCacheState.Expired, false, false, true,
                ModSourceFailureKind.None, null, result.CheckedAt, [], result.Message);

        var state = result.Status switch
        {
            "有更新" => ModSourceCheckState.UpdateAvailable,
            "已是最新" => ModSourceCheckState.UpToDate,
            "检查失败" => ModSourceCheckState.Failed,
            "暂不支持" => ModSourceCheckState.Unsupported,
            _ => ModSourceCheckState.NeedsReview
        };
        return Entry(
            ModSourceKind.Workshop, true, identifier, ModSourceTrustLevel.UserConfirmed,
            state, result.Status, mod.Version,
            result.RemoteContentVersion.Length > 0
                ? result.RemoteContentVersion
                : result.RemoteManifestId,
            null, result.RemoteManifestId,
            ModSourceCacheState.Fresh, true,
            state == ModSourceCheckState.UpdateAvailable,
            state is ModSourceCheckState.NeedsReview or ModSourceCheckState.Unsupported,
            state == ModSourceCheckState.Unsupported
                ? ModSourceFailureKind.Unsupported
                : ModSourceFailureKind.None,
            state == ModSourceCheckState.Unsupported ? result.Message : null,
            result.CheckedAt, [], result.Message);
    }

    ModSourceEntryDiagnostic FailedEntry(
        ModSourceKind source,
        string identifier,
        string localVersion,
        string latestVersion,
        string? url,
        Failure failure,
        DateTimeOffset? lastSuccessAt)
    {
        return Entry(
            source, true, identifier, ModSourceTrustLevel.UserConfirmed,
            ModSourceCheckState.Failed,
            source == ModSourceKind.GitHub ? "无法检测" : "检查失败",
            localVersion, latestVersion, url, null,
            lastSuccessAt.HasValue ? ModSourceCacheState.Expired : ModSourceCacheState.Missing,
            false, false, false, failure.Kind, failure.Reason, failure.At, [], failure.Reason);
    }

    static ModSourceEntryDiagnostic Entry(
        ModSourceKind source,
        bool bound,
        string? identifier,
        ModSourceTrustLevel trust,
        ModSourceCheckState state,
        string status,
        string localVersion,
        string latestVersion,
        string? url,
        string? remoteManifestId,
        ModSourceCacheState cacheState,
        bool cacheHit,
        bool canUpdate,
        bool needsReview,
        ModSourceFailureKind failureKind,
        string? failureReason,
        DateTimeOffset? checkedAt,
        IReadOnlyList<ModSourceCandidateDiagnostic> candidates,
        string? message = null) =>
        new(
            source, bound, identifier, trust, state, status, localVersion, latestVersion,
            url, remoteManifestId, cacheState, cacheHit, canUpdate, needsReview,
            failureKind, failureReason, checkedAt, candidates,
            message ?? failureReason ?? status);

    ModSourceCheckBatchResult CreateBatchResult(
        IEnumerable<ModSourceCheckResult> results,
        int githubCount,
        int workshopCount,
        bool stale = false,
        bool cancelled = false)
    {
        var materialized = results.ToArray();
        return new ModSourceCheckBatchResult(
            githubCount,
            workshopCount,
            materialized.Count(item => item.GitHub.CanUpdate),
            materialized.Count(item => item.Workshop.CanUpdate),
            materialized.Count(item => item.WorkshopNeedsReview),
            materialized.Count(item =>
                item.GitHub.State == ModSourceCheckState.Failed ||
                item.Workshop.State == ModSourceCheckState.Failed),
            materialized,
            stale,
            cancelled);
    }

    bool IsFresh(DateTimeOffset checkedAt, TimeSpan maxAge)
    {
        var age = utcNow() - checkedAt;
        return age >= TimeSpan.Zero && age < maxAge;
    }

    static bool IsGitHubFailure(ModSourceBackendGitHubCheck? result) =>
        result?.Status == "无法检测";

    static bool IsWorkshopFailure(ModSourceBackendWorkshopCheck? result) =>
        result?.Status == "检查失败";

    static bool IsCurrent(Func<bool>? isCurrent)
    {
        if (isCurrent is null)
            return true;
        try { return isCurrent(); }
        catch { return false; }
    }

    static Failure FailureFromMessage(string message, DateTimeOffset at) =>
        new(ClassifyFailure(message), message, at);

    static Failure FailureFromException(Exception error, DateTimeOffset at) =>
        new(ClassifyFailure(error), error.Message, at);

    static ModSourceFailureKind ClassifyFailure(string message)
    {
        if (message.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return ModSourceFailureKind.Timeout;
        if (message.Contains("额度", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase))
            return ModSourceFailureKind.RateLimited;
        if (message.Contains("网络", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("请求失败", StringComparison.OrdinalIgnoreCase))
            return ModSourceFailureKind.Network;
        if (message.Contains("读取", StringComparison.OrdinalIgnoreCase))
            return ModSourceFailureKind.LocalRead;
        if (message.Contains("解析", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("无效", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("过大", StringComparison.OrdinalIgnoreCase))
            return ModSourceFailureKind.InvalidResponse;
        return ModSourceFailureKind.SourceUnavailable;
    }

    static ModSourceFailureKind ClassifyFailure(Exception error) => error switch
    {
        TimeoutException => ModSourceFailureKind.Timeout,
        OperationCanceledException => ModSourceFailureKind.Timeout,
        System.Net.Http.HttpRequestException => ModSourceFailureKind.Network,
        IOException => ModSourceFailureKind.LocalRead,
        System.Text.Json.JsonException => ModSourceFailureKind.InvalidResponse,
        InvalidDataException => ModSourceFailureKind.InvalidResponse,
        _ => ModSourceFailureKind.Unknown
    };

    void CancelForKey(string key)
    {
        foreach (string operationKey in active.Keys
            .Where(item => item.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            active[operationKey].Cancellation.Cancel();
            active.Remove(operationKey);
        }
    }

    string ModKey(ManagedMod mod) =>
        gameRoot + "|" + NormalizePath(mod.Folder) + "|" + mod.Id + "|" +
        mod.Version + "|" + NormalizePath(mod.Manifest);

    static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

internal sealed record ModSourceCheckBatchResult(
    int GitHubChecked,
    int WorkshopChecked,
    int GitHubUpdates,
    int WorkshopUpdates,
    int WorkshopNeedsReview,
    int Failed,
    IReadOnlyList<ModSourceCheckResult> Results,
    bool IsStale = false,
    bool Cancelled = false);
