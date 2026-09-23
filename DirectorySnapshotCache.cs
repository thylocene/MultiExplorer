using System.Collections.Concurrent;

namespace MultiExplorer;

/// <summary>
/// Immutable metadata captured by a single, non-recursive directory enumeration.
/// The snapshot is shared by the managed Details view, item counts, and filtering.
/// </summary>
internal sealed class DirectorySnapshot(
    string path,
    DateTime capturedUtc,
    IReadOnlyList<DirectorySnapshotItem> items)
{
    internal string Path { get; } = path;
    internal DateTime CapturedUtc { get; } = capturedUtc;
    internal IReadOnlyList<DirectorySnapshotItem> Items { get; } = items;

    internal DirectoryItemCount CountItems(
        bool showHiddenItems,
        bool showProtectedSystemItems)
    {
        int fileCount = 0;
        int folderCount = 0;
        foreach (DirectorySnapshotItem item in Items)
        {
            if (!showHiddenItems && item.Attributes.HasFlag(FileAttributes.Hidden))
                continue;
            if (!showProtectedSystemItems && item.Attributes.HasFlag(FileAttributes.System))
                continue;

            if (item.IsDirectory)
                folderCount++;
            else
                fileCount++;
        }

        return new DirectoryItemCount(fileCount, folderCount);
    }
}

internal readonly record struct DirectorySnapshotItem(
    string Name,
    string FullPath,
    DateTime LastWriteTime,
    DateTime CreationTime,
    FileAttributes Attributes,
    long Length)
{
    internal bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);
    internal string Extension => IsDirectory ? string.Empty : System.IO.Path.GetExtension(Name);
}

/// <summary>
/// Coalesces directory scans and owns one filesystem watcher per observed path.
/// A short maximum cache age provides a fallback when a filesystem does not emit
/// reliable change notifications.
/// </summary>
internal static class DirectorySnapshotCache
{
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(45);
    private static readonly ConcurrentDictionary<string, SnapshotState> Snapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WatchGate = new();
    private static readonly Dictionary<string, WatchRegistration> Watches =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _nextSubscriptionId;

    internal static Task<DirectorySnapshot> GetAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = NormalizePath(path);
        SnapshotState state = Snapshots.GetOrAdd(
            normalizedPath,
            static value => new SnapshotState(value));
        return state.GetAsync(cancellationToken);
    }

    internal static DirectorySnapshot LoadSnapshot(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = NormalizePath(path);
        var directory = new DirectoryInfo(normalizedPath);
        if (!directory.Exists)
            throw new DirectoryNotFoundException(
                $"The directory '{normalizedPath}' does not exist.");

        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        var items = new List<DirectorySnapshotItem>();
        foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileAttributes attributes = item.Attributes;
                long length = item is FileInfo file ? file.Length : 0;
                items.Add(new DirectorySnapshotItem(
                    item.Name,
                    item.FullName,
                    item.LastWriteTime,
                    item.CreationTime,
                    attributes,
                    length));
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException)
            {
                AppLog.Debug(ex, nameof(LoadSnapshot),
                    $"Skipped directory entry '{item.FullName}'.");
            }
        }

        return new DirectorySnapshot(normalizedPath, DateTime.UtcNow, items);
    }

    internal static void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string normalizedPath;
        try
        {
            normalizedPath = NormalizePath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException)
        {
            return;
        }

        if (Snapshots.TryGetValue(normalizedPath, out SnapshotState? state))
            state.Invalidate();
    }

    internal static IDisposable Watch(string path, Action changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(changed);
        string normalizedPath = NormalizePath(path);
        long subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);

        lock (WatchGate)
        {
            if (!Watches.TryGetValue(normalizedPath, out WatchRegistration? registration))
            {
                registration = new WatchRegistration(normalizedPath);
                Watches.Add(normalizedPath, registration);
            }

            registration.Add(subscriptionId, changed);
        }

        return new WatchSubscription(normalizedPath, subscriptionId);
    }

    private static string NormalizePath(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    private static void Unsubscribe(string path, long subscriptionId)
    {
        WatchRegistration? registrationToDispose = null;
        lock (WatchGate)
        {
            if (!Watches.TryGetValue(path, out WatchRegistration? registration))
                return;

            registration.Remove(subscriptionId);
            if (registration.HasSubscribers) return;

            Watches.Remove(path);
            registrationToDispose = registration;
        }

        registrationToDispose.Dispose();
        Snapshots.TryRemove(path, out _);
    }

    private sealed class SnapshotState(string path)
    {
        private readonly object _gate = new();
        private DirectorySnapshot? _snapshot;
        private Task<DirectorySnapshot>? _loadTask;
        private int _generation;

        internal Task<DirectorySnapshot> GetAsync(CancellationToken cancellationToken)
        {
            Task<DirectorySnapshot> sharedTask;
            lock (_gate)
            {
                if (_snapshot is { } snapshot
                    && DateTime.UtcNow - snapshot.CapturedUtc <= MaximumSnapshotAge)
                    return Task.FromResult(snapshot);

                if (_loadTask is null)
                {
                    int generation = _generation;
                    _loadTask = Task.Run(() => LoadSnapshot(path, CancellationToken.None));
                    _ = ObserveLoadAsync(_loadTask, generation);
                }

                sharedTask = _loadTask;
            }

            return cancellationToken.CanBeCanceled
                ? sharedTask.WaitAsync(cancellationToken)
                : sharedTask;
        }

        internal void Invalidate()
        {
            lock (_gate)
            {
                _generation++;
                _snapshot = null;
                _loadTask = null;
            }
        }

        private async Task ObserveLoadAsync(
            Task<DirectorySnapshot> loadTask,
            int generation)
        {
            try
            {
                DirectorySnapshot snapshot = await loadTask.ConfigureAwait(false);
                lock (_gate)
                {
                    if (_generation != generation || !ReferenceEquals(_loadTask, loadTask))
                        return;

                    _snapshot = snapshot;
                    _loadTask = null;
                }
            }
            catch
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_loadTask, loadTask))
                        _loadTask = null;
                }
            }
        }
    }

    private sealed class WatchRegistration : IDisposable
    {
        private readonly string _path;
        private readonly Dictionary<long, Action> _subscribers = [];
        private FileSystemWatcher? _watcher;

        internal WatchRegistration(string path)
        {
            _path = path;
            TryCreateWatcher();
        }

        internal bool HasSubscribers => _subscribers.Count > 0;

        internal void Add(long id, Action callback) => _subscribers.Add(id, callback);

        internal void Remove(long id) => _subscribers.Remove(id);

        private void TryCreateWatcher()
        {
            try
            {
                var watcher = new FileSystemWatcher(_path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.Attributes,
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or DirectoryNotFoundException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException)
            {
                AppLog.Debug(ex, nameof(DirectorySnapshotCache),
                    $"Could not monitor '{_path}' for directory changes.");
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e) => NotifyChanged();

        private void OnRenamed(object sender, RenamedEventArgs e) => NotifyChanged();

        private void OnError(object sender, ErrorEventArgs e)
        {
            DisposeWatcher();
            TryCreateWatcher();
            NotifyChanged();
        }

        private void NotifyChanged()
        {
            Invalidate(_path);
            Action[] callbacks;
            lock (WatchGate)
                callbacks = _subscribers.Values.ToArray();

            foreach (Action callback in callbacks)
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    AppLog.Debug(ex, nameof(DirectorySnapshotCache),
                        "A directory snapshot subscriber failed.");
                }
            }
        }

        public void Dispose() => DisposeWatcher();

        private void DisposeWatcher()
        {
            FileSystemWatcher? watcher = _watcher;
            _watcher = null;
            if (watcher is null) return;

            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
    }

    private sealed class WatchSubscription(string path, long subscriptionId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Unsubscribe(path, subscriptionId);
        }
    }
}
