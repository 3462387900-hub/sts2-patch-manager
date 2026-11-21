using System.IO;

namespace SpirePatch;

internal sealed record ModFileChangeEventArgs(
    string Root, bool Overflow, bool TreeChanged, int Version, bool FirstInBurst) : EventArgs;

internal sealed record ModFileWatchQuietEventArgs(
    string Root, bool Overflow, bool TreeChanged, int Version) : EventArgs;

internal sealed record ModFileWatchFailureEventArgs(string Root, string Message) : EventArgs;

internal sealed record ModFileWatchReconfigurationResult(bool Recreated, string? Error);

internal interface IModFileWatcherService : IDisposable
{
    event EventHandler<ModFileChangeEventArgs>? ChangeDetected;
    event EventHandler<ModFileWatchQuietEventArgs>? QuietPeriodElapsed;
    event EventHandler<ModFileWatchFailureEventArgs>? MonitoringFailed;
    void Start(string root);
    void Stop();
    void DeferQuietPeriod();
    ModFileWatchReconfigurationResult CompleteQuietPeriod(int version);
}

/// <summary>
/// Watches only the game root and the two MOD-owned trees. This service has no UI
/// dispatcher or window lifetime dependency; consumers marshal its events as needed.
/// </summary>
internal sealed class ModFileWatcherService : IModFileWatcherService
{
    static readonly NotifyFilters WatchedChanges =
        NotifyFilters.FileName | NotifyFilters.DirectoryName |
        NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;

    readonly object gate = new();
    readonly List<FileSystemWatcher> watchers = [];
    readonly Timer debounce;
    readonly TimeSpan debounceDelay;
    string? watchedRoot;
    bool dirty;
    bool overflow;
    bool treeChanged;
    bool disposed;
    int changeVersion;

    public event EventHandler<ModFileChangeEventArgs>? ChangeDetected;
    public event EventHandler<ModFileWatchQuietEventArgs>? QuietPeriodElapsed;
    public event EventHandler<ModFileWatchFailureEventArgs>? MonitoringFailed;

    public ModFileWatcherService(TimeSpan? debounceDelay = null)
    {
        this.debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(850);
        debounce = new Timer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string? failure = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (root.Equals(watchedRoot, StringComparison.OrdinalIgnoreCase))
                return;

            StopWatchersLocked();
            watchedRoot = root;
            dirty = false;
            overflow = false;
            treeChanged = false;
            changeVersion = 0;
            try
            {
                CreateWatchersLocked(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                StopWatchersLocked();
                watchedRoot = null;
                dirty = true;
                overflow = true;
                failure = error.Message;
            }
        }

        if (failure is not null)
            MonitoringFailed?.Invoke(this, new(root, failure));
    }

    public void Stop()
    {
        lock (gate)
        {
            if (disposed) return;
            debounce.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            StopWatchersLocked();
            watchedRoot = null;
            dirty = false;
            overflow = false;
            treeChanged = false;
            changeVersion = 0;
        }
    }

    public void DeferQuietPeriod()
    {
        lock (gate)
        {
            if (disposed || watchedRoot is null || !dirty) return;
            debounce.Change(debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Called by the consumer after it has checked whether a scan or file transaction
    /// is still running. A stale timer callback cannot rebuild a newer watch session.
    /// </summary>
    public ModFileWatchReconfigurationResult CompleteQuietPeriod(int version)
    {
        string? root;
        lock (gate)
        {
            if (disposed || watchedRoot is null || !dirty || version != changeVersion)
                return new(false, null);

            root = watchedRoot;
            if (!overflow && !treeChanged && watchers.Count >= 2)
                return new(false, null);

            try
            {
                StopWatchersLocked();
                CreateWatchersLocked(root);
                overflow = false;
                treeChanged = false;
                return new(true, null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                StopWatchersLocked();
                watchedRoot = null;
                overflow = true;
                dirty = true;
                return new(false, error.Message);
            }
        }
    }

    void CreateWatchersLocked(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("游戏目录不存在，无法启动 MOD 文件监测。");

        WatchFolderLocked(root, root, recursive: false, onlyTopFolders: true);

        string mods = Path.Combine(root, "mods");
        if (Directory.Exists(mods))
            WatchFolderLocked(root, mods, recursive: true, onlyTopFolders: false);

        string disabled = Path.Combine(root, ModManager.StorageName);
        if (Directory.Exists(disabled))
            WatchFolderLocked(root, disabled, recursive: true, onlyTopFolders: false);
    }

    void WatchFolderLocked(string root, string folder, bool recursive, bool onlyTopFolders)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = WatchedChanges,
            InternalBufferSize = 32 * 1024
        };

        if (onlyTopFolders)
        {
            watcher.Filters.Clear();
            watcher.Filters.Add("mods");
            watcher.Filters.Add(ModManager.StorageName);
        }

        watcher.Changed += (_, _) => QueueChange(root, overflow: false, onlyTopFolders);
        watcher.Created += (_, _) => QueueChange(root, overflow: false, onlyTopFolders);
        watcher.Deleted += (_, _) => QueueChange(root, overflow: false, onlyTopFolders);
        watcher.Renamed += (_, _) => QueueChange(root, overflow: false, onlyTopFolders);
        watcher.Error += (_, _) => QueueChange(root, overflow: true, onlyTopFolders);

        watchers.Add(watcher);
        watcher.EnableRaisingEvents = true;
    }

    void QueueChange(string root, bool overflow, bool treeChanged)
    {
        ModFileChangeEventArgs args;
        lock (gate)
        {
            if (disposed || !root.Equals(watchedRoot, StringComparison.OrdinalIgnoreCase))
                return;

            bool first = !dirty;
            dirty = true;
            this.overflow |= overflow;
            this.treeChanged |= treeChanged;
            changeVersion++;
            args = new(root, overflow, treeChanged, changeVersion, first);
            debounce.Change(debounceDelay, Timeout.InfiniteTimeSpan);
        }

        ChangeDetected?.Invoke(this, args);
    }

    void OnDebounceElapsed(object? state)
    {
        ModFileWatchQuietEventArgs? args = null;
        lock (gate)
        {
            if (disposed || watchedRoot is null || !dirty)
                return;
            args = new(watchedRoot, overflow, treeChanged, changeVersion);
        }

        QuietPeriodElapsed?.Invoke(this, args);
    }

    internal void SimulateBufferOverflowForTests(string root) =>
        QueueChange(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), overflow: true, treeChanged: true);

    void StopWatchersLocked()
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        watchers.Clear();
    }

    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(ModFileWatcherService));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            debounce.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            StopWatchersLocked();
            watchedRoot = null;
            disposed = true;
        }
        debounce.Dispose();
    }
}
