namespace SpirePatch;

internal sealed record ModEnvironmentSnapshotWorkflowRestoreResult(
    ModEnvironmentSnapshotRestoreResult Restore,
    ModInventory? RefreshedInventory,
    string? CacheWarning,
    string? RefreshError);

/// <summary>
/// Application workflow for listing, creating, previewing, restoring and deleting
/// complete MOD-environment snapshots. File copying and validation remain in Domain.
/// </summary>
internal sealed class ModEnvironmentSnapshotWorkflowService
{
    readonly string gameRoot;
    readonly string dataRoot;
    readonly Func<string, ModInventory> scanInventory;
    readonly Func<IReadOnlyCollection<string>, string?>? invalidateCaches;

    public ModEnvironmentSnapshotWorkflowService(
        string gameRoot,
        string dataRoot,
        Func<string, ModInventory>? scanInventory = null,
        Func<IReadOnlyCollection<string>, string?>? invalidateCaches = null)
    {
        this.gameRoot = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar);
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.scanInventory = scanInventory ?? ModManager.Scan;
        this.invalidateCaches = invalidateCaches;
    }

    public ModEnvironmentSnapshotEntry[] List() =>
        ModEnvironmentSnapshots.List(gameRoot, dataRoot);

    public Task<ModEnvironmentSnapshot> CreateAsync(
        string purpose = "手动快照",
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ModEnvironmentSnapshots.Create(gameRoot, dataRoot, purpose), cancellationToken);

    public Task<ModEnvironmentSnapshotPreview> PreviewRestoreAsync(
        string snapshotId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ModEnvironmentSnapshots.PreviewRestore(gameRoot, dataRoot, snapshotId), cancellationToken);

    /// <summary>
    /// This method is the explicit confirmation boundary. Once the domain restore
    /// starts, cancellation is not used to skip the mandatory post-restore scan.
    /// </summary>
    public async Task<ModEnvironmentSnapshotWorkflowRestoreResult> ConfirmRestoreAsync(
        ModEnvironmentSnapshotPreview preview,
        IEnumerable<string>? previousModIds = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restored = await Task.Run(
            () => ModEnvironmentSnapshots.Restore(gameRoot, dataRoot, preview.SnapshotId, preview.Token),
            cancellationToken);

        ModInventory? inventory = null;
        string? refreshError = null;
        string? cacheWarning = null;
        try
        {
            inventory = await Task.Run(() => scanInventory(gameRoot));
            var ids = (previousModIds ?? [])
                .Concat(inventory.Mods.Select(mod => mod.Id))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            try { cacheWarning = invalidateCaches?.Invoke(ids); }
            catch (Exception error) { cacheWarning = error.Message; }
        }
        catch (Exception error)
        {
            refreshError = error.Message;
        }

        return new(restored, inventory, cacheWarning, refreshError);
    }

    public Task DeleteAsync(string snapshotId, CancellationToken cancellationToken = default) =>
        Task.Run(() => ModEnvironmentSnapshots.Delete(gameRoot, dataRoot, snapshotId), cancellationToken);
}
