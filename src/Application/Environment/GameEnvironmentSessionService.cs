namespace SpirePatch;

internal sealed class GameEnvironmentDirectoryChangingEventArgs : EventArgs
{
    public string? PreviousRoot { get; }
    public string CurrentRoot { get; }
    public GameEnvironmentDirectoryChangingEventArgs(string? previousRoot, string currentRoot)
    { PreviousRoot = previousRoot; CurrentRoot = currentRoot; }
}
internal sealed class GameEnvironmentDirectoryChangedEventArgs : EventArgs
{
    public string? PreviousRoot { get; }
    public string CurrentRoot { get; }
    public GameEnvironmentDirectoryChangedEventArgs(string? previousRoot, string currentRoot)
    { PreviousRoot = previousRoot; CurrentRoot = currentRoot; }
}
internal sealed record GameEnvironmentSessionResult(
    string Root, bool Changed, string? PreferenceError);

/// <summary>
/// Coordinates a selected game-directory session without depending on WPF.
/// The UI subscribes to the change events to clear its own view state.
/// </summary>
internal sealed class GameEnvironmentSessionService
{
    readonly string settingsPath;
    readonly IModFileWatcherService fileWatcher;

    public string? CurrentRoot { get; private set; }

    public event EventHandler<GameEnvironmentDirectoryChangingEventArgs>? DirectoryChanging;
    public event EventHandler<GameEnvironmentDirectoryChangedEventArgs>? DirectoryChanged;

    public GameEnvironmentSessionService(
        string settingsPath,
        IModFileWatcherService fileWatcher,
        string? initialRoot = null)
    {
        this.settingsPath = settingsPath;
        this.fileWatcher = fileWatcher;
        CurrentRoot = initialRoot is null ? null : Path.GetFullPath(initialRoot).TrimEnd(Path.DirectorySeparatorChar);
    }

    public string? LoadRememberedRoot() => GameEnvironment.LoadRoot(settingsPath);

    public Task<List<GameCandidate>> DiscoverAsync(string? savedRoot, CancellationToken cancellationToken = default) =>
        Task.Run(() => GameEnvironment.Discover(savedRoot), cancellationToken);

    public GameEnvironmentSessionResult SelectRoot(string path, bool remember)
    {
        string root = GameEnvironment.Root(path);
        string? previous = CurrentRoot;
        bool changed = previous is null ||
            !root.Equals(previous, StringComparison.OrdinalIgnoreCase);

        if (changed)
        {
            DirectoryChanging?.Invoke(this, new(previous, root));
            CurrentRoot = root;
            DirectoryChanged?.Invoke(this, new(previous, root));
        }

        // Start is idempotent for the current root and replaces watchers for a new root.
        fileWatcher.Start(root);

        string? preferenceError = null;
        if (remember)
        {
            try { GameEnvironment.SaveRoot(settingsPath, root); }
            catch (Exception error) { preferenceError = error.Message; }
        }

        return new(root, changed, preferenceError);
    }

    public void StopWatching() => fileWatcher.Stop();
}
