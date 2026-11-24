using System.IO;

namespace SpirePatch;

internal sealed class ModFileChangeEventArgs : EventArgs
{
    public string Root { get; }
    public bool Overflow { get; }
    public bool TreeChanged { get; }
    public int Version { get; }
    public bool FirstInBurst { get; }
    // Native notifications do not identify the precise flag that fired. Retain the
    // event kind, path and configured mask for diagnostics without re-reading files.
    public string? WatchPath { get; }
    public string? ChangedPath { get; }
    public string? OldPath { get; }
    public WatcherChangeTypes? ChangeType { get; }
    public NotifyFilters? NotifyMask { get; }
    public string? Error { get; }

    public ModFileChangeEventArgs(string root, bool overflow, bool treeChanged, int version, bool firstInBurst,
        string? watchPath = null, string? changedPath = null, string? oldPath = null,
        WatcherChangeTypes? changeType = null, NotifyFilters? notifyMask = null, string? error = null)
    {
        Root = root; Overflow = overflow; TreeChanged = treeChanged; Version = version; FirstInBurst = firstInBurst;
        WatchPath = watchPath; ChangedPath = changedPath; OldPath = oldPath;
        ChangeType = changeType; NotifyMask = notifyMask; Error = error;
    }
}

internal sealed class ModFileWatchQuietEventArgs : EventArgs
{
    public string Root { get; }
    public bool Overflow { get; }
    public bool TreeChanged { get; }
    public int Version { get; }
    public ModFileWatchQuietEventArgs(string root, bool overflow, bool treeChanged, int version)
    {
        Root = root; Overflow = overflow; TreeChanged = treeChanged; Version = version;
    }
}

internal sealed class ModFileWatchFailureEventArgs : EventArgs
{
    public string Root { get; }
    public string Message { get; }
    public ModFileWatchFailureEventArgs(string root, string message) { Root = root; Message = message; }
}

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
    // The root watcher only needs to detect appearance, removal or replacement
    // of the two owned trees. Their recursive watchers own content notifications.
    // Creation timestamps and access timestamps are not MOD content/version
    // inputs. In particular, do not subscribe to redundant creation-time metadata
    // on freshly copied files: Windows can deliver deferred metadata notifications.
    const NotifyFilters TreeNames = NotifyFilters.FileName | NotifyFilters.DirectoryName;
    const NotifyFilters ContentChanges = TreeNames | NotifyFilters.LastWrite | NotifyFilters.Size;

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

    // Read-only synchronization points for Application workflows. These values are
    // updated under the same lock as watcher events, before UI dispatch is required.
    public string? CurrentRoot { get { lock (gate) return watchedRoot; } }
    public int ChangeVersion { get { lock (gate) return changeVersion; } }

    /// <summary>
    /// Commits a small consumer-side state update while watcher notifications are
    /// excluded by the same lock that protects ChangeVersion. This closes the gap
    /// between an Application workflow's final version check and UI publication.
    /// </summary>
    public bool TryRunIfCurrent(string root, int version, Action action)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        lock (gate)
        {
            if (disposed || version != changeVersion)
                return false;
            if (watchedRoot is not null &&
                !root.Equals(watchedRoot, StringComparison.OrdinalIgnoreCase))
                return false;
            action();
            return true;
        }
    }

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
            NotifyFilter = onlyTopFolders ? TreeNames : ContentChanges,
            InternalBufferSize = 32 * 1024
        };

        if (onlyTopFolders)
        {
            watcher.Filters.Clear();
            watcher.Filters.Add("mods");
            watcher.Filters.Add(ModManager.StorageName);
        }

        void FileChanged(FileSystemEventArgs e) =>
            QueueChange(root, overflow: false, treeChanged: onlyTopFolders,
                watchPath: folder, changedPath: e.FullPath, oldPath: (e as RenamedEventArgs)?.OldFullPath,
                changeType: e.ChangeType, notifyMask: watcher.NotifyFilter);

        watcher.Changed += (_, e) => FileChanged(e);
        watcher.Created += (_, e) => FileChanged(e);
        watcher.Deleted += (_, e) => FileChanged(e);
        watcher.Renamed += (_, e) => FileChanged(e);
        watcher.Error += (_, e) => QueueChange(root, overflow: true, treeChanged: onlyTopFolders,
            watchPath: folder, notifyMask: watcher.NotifyFilter, error: e.GetException()?.ToString());

        watchers.Add(watcher);
        watcher.EnableRaisingEvents = true;
    }

    // Windows can emit LastWrite notifications for ordinary directories when
    // enumerating freshly populated trees. Directory timestamps are not MOD
    // inventory/content inputs. Their child file writes and all name/structure
    // changes have independent notifications. Never suppress an unknown path
    // or a reparse point: those can indicate a replaced or unsafe tree.
    internal static bool IsOrdinaryDirectoryMetadataChange(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // The entry disappeared, is inaccessible, or cannot be classified.
            // Conservatively treat the original notification as a real change.
            return false;
        }
    }

    void QueueChange(string root, bool overflow, bool treeChanged,
        string? watchPath = null, string? changedPath = null, string? oldPath = null,
        WatcherChangeTypes? changeType = null, NotifyFilters? notifyMask = null, string? error = null)
    {
        ModFileChangeEventArgs args;
        lock (gate)
        {
            if (disposed || !root.Equals(watchedRoot, StringComparison.OrdinalIgnoreCase))
                return;

            // Classify under the same lock as generation publication. Only a
            // recursive Changed notification on an existing ordinary directory
            // is metadata noise; never suppress creates, deletes, renames, root
            // tree events, buffer overflows or any file Changed notification.
            if (!overflow && !treeChanged && changeType == WatcherChangeTypes.Changed &&
                changedPath != null && IsOrdinaryDirectoryMetadataChange(changedPath))
                return;

            bool first = !dirty;
            dirty = true;
            this.overflow |= overflow;
            this.treeChanged |= treeChanged;
            changeVersion++;
            args = new(root, overflow, treeChanged, changeVersion, first,
                watchPath, changedPath, oldPath, changeType, notifyMask, error);
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
