using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

/// <summary>
/// Adapts existing GitHub and Workshop read-only infrastructure to the Application
/// source-check contract. This adapter does not bind sources, download packages,
/// install updates, or write MOD files.
/// </summary>
internal sealed class InfrastructureModSourceCheckBackend : IModSourceCheckBackend
{
    readonly GitHubModUpdates githubUpdates;
    readonly GitHubModDiscovery githubDiscovery;
    readonly WorkshopModSources workshopSources;
    readonly WorkshopModUpdates workshopUpdates;

    public InfrastructureModSourceCheckBackend(
        GitHubModUpdates githubUpdates,
        GitHubModDiscovery githubDiscovery,
        WorkshopModSources workshopSources,
        WorkshopModUpdates workshopUpdates)
    {
        this.githubUpdates = githubUpdates ?? throw new ArgumentNullException(nameof(githubUpdates));
        this.githubDiscovery = githubDiscovery ?? throw new ArgumentNullException(nameof(githubDiscovery));
        this.workshopSources = workshopSources ?? throw new ArgumentNullException(nameof(workshopSources));
        this.workshopUpdates = workshopUpdates ?? throw new ArgumentNullException(nameof(workshopUpdates));
    }

    public ModSourceBackendSnapshot Read(ManagedMod mod)
    {
        string? repository = githubUpdates.Repository(mod.Id);
        ulong? workshopId = workshopSources.Get(mod.Id);
        WorkshopBaselineState baselineState = WorkshopBaselineState.Unbound;

        if (workshopId.HasValue)
        {
            var baseline = workshopSources.GetBaseline(mod.Id);
            baselineState = baseline is null
                ? WorkshopBaselineState.Missing
                : baseline.WorkshopId != workshopId.Value ||
                    !baseline.LocalVersion.Equals(mod.Version, StringComparison.Ordinal)
                    ? WorkshopBaselineState.LocalVersionChanged
                    : WorkshopBaselineState.Current;
        }

        var githubResult = githubUpdates.Cached(mod.Id);
        var discoveryResult = githubDiscovery.Cached(mod);
        var workshopResult = workshopUpdates.Cached(mod.Id);

        return new ModSourceBackendSnapshot(
            repository,
            githubResult is null ? null : new ModSourceBackendGitHubCheck(
                githubResult.Repository, githubResult.LocalVersion, githubResult.LatestVersion,
                githubResult.ReleaseUrl, githubResult.Status, githubResult.Message, githubResult.CheckedAt),
            discoveryResult is null ? null : new ModSourceBackendDiscoveryResult(
                discoveryResult.ModId,
                discoveryResult.Candidates.Select(candidate => new ModSourceBackendDiscoveryCandidate(
                    candidate.Repository, candidate.Reason, candidate.Score, candidate.Verified)).ToArray(),
                discoveryResult.Note,
                discoveryResult.Retryable),
            workshopId,
            workshopResult is null ? null : new ModSourceBackendWorkshopCheck(
                workshopResult.WorkshopId, workshopResult.LocalVersion, workshopResult.RemoteTitle,
                workshopResult.RemoteContentVersion, workshopResult.RemoteManifestId,
                workshopResult.Status, workshopResult.Message, workshopResult.CheckedAt),
            baselineState);
    }

    public async Task<ModSourceBackendDiscoveryResult> DiscoverGitHubAsync(
        ManagedMod mod,
        CancellationToken cancellationToken)
    {
        var result = await githubDiscovery.DiscoverAsync(
            mod, force: true, ct: cancellationToken);
        return new ModSourceBackendDiscoveryResult(
            result.ModId,
            result.Candidates.Select(candidate => new ModSourceBackendDiscoveryCandidate(
                candidate.Repository, candidate.Reason, candidate.Score, candidate.Verified)).ToArray(),
            result.Note,
            result.Retryable);
    }

    public async Task<ModSourceBackendGitHubCheck> CheckGitHubAsync(
        ManagedMod mod,
        CancellationToken cancellationToken)
    {
        var result = await githubUpdates.CheckAsync(
            mod, force: true, ct: cancellationToken);
        return new ModSourceBackendGitHubCheck(
            result.Repository, result.LocalVersion, result.LatestVersion,
            result.ReleaseUrl, result.Status, result.Message, result.CheckedAt);
    }

    public async Task<ModSourceBackendWorkshopCheck> CheckWorkshopAsync(
        ManagedMod mod,
        ulong workshopId,
        CancellationToken cancellationToken)
    {
        var result = await workshopUpdates.CheckAsync(
            mod, workshopId, workshopSources, force: true,
            cancellationToken: cancellationToken);
        return new ModSourceBackendWorkshopCheck(
            result.WorkshopId, result.LocalVersion, result.RemoteTitle,
            result.RemoteContentVersion, result.RemoteManifestId,
            result.Status, result.Message, result.CheckedAt);
    }
}
