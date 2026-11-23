using NLog;
using SyncTrayzor.Syncthing;
using SyncTrayzor.Syncthing.Folders;
using SyncTrayzor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    public interface IExplorerStatusService : IDisposable
    {
        bool IsEnabled { get; set; }

        /// <summary>Root paths of all Syncthing folders, or empty if the integration is disabled or Syncthing isn't running</summary>
        IReadOnlyList<string> Roots { get; }

        bool TryResolve(string path, out FolderPath folderPath);

        bool IsFolderPaused(string folderId);

        /// <summary>Returns the state of an item from memory, without blocking</summary>
        ExplorerItemState GetState(string path);

        /// <summary>Whether the item is ignored (also through an ignored parent), or null if the folder's patterns aren't loaded yet</summary>
        bool? IsIgnored(FolderPath path);

        /// <summary>Loads the folder's ignore patterns from Syncthing now, e.g. after changing them, and returns the matcher</summary>
        Task<IgnoreMatcher> RefreshIgnoresAsync(string folderId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Keeps track of the sync state of the items in all Syncthing folders, for the Explorer overlay icons.
    /// </summary>
    /// <remarks>
    /// All state is derived from Syncthing's events (folder summaries, errors, state changes), the list of needed
    /// items (only fetched for folders which need something) and each folder's ignore patterns (matched locally,
    /// see <see cref="IgnoreMatcher"/>). Queries from Explorer are answered from memory, so Explorer is never
    /// blocked on Syncthing.
    /// </remarks>
    public class ExplorerStatusService : IExplorerStatusService
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        // Fetch at most this many needed items per folder. Beyond that, everything in the folder is shown as syncing.
        private const int maxNeededItems = 5000;
        // Track at most this many local changes per folder. Beyond that, everything in the folder is shown as syncing
        // until Syncthing has scanned.
        private const int maxLocalChanges = 10000;
        // Remember this many recently shown items per folder, to refresh their icons when the ignore patterns change
        private const int maxRecentlyShown = 5000;
        // Local changes are dropped after this, even if Syncthing never scanned them
        private static readonly TimeSpan localChangeLifetime = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan neededRefreshInterval = TimeSpan.FromSeconds(2);

        private readonly ISyncthingManager syncthingManager;
        private readonly IShellChangeNotifier shellNotifier;
        private readonly IFileWatcherFactory fileWatcherFactory;

        private readonly object syncRoot = new();
        private Dictionary<string, FolderEntry> folders = new();
        private FolderPathResolver resolver = new([]);
        private IReadOnlyList<string> roots = [];
        private bool isEnabled;
        private long ignoresRequestCount;

        private CancellationTokenSource workerCts;
        private readonly SemaphoreSlim workSignal = new(0);

        public ExplorerStatusService(ISyncthingManager syncthingManager, IShellChangeNotifier shellNotifier, IFileWatcherFactory fileWatcherFactory)
        {
            this.syncthingManager = syncthingManager;
            this.shellNotifier = shellNotifier;
            this.fileWatcherFactory = fileWatcherFactory;

            this.syncthingManager.StateChanged += (o, e) => RebuildFolders();
            this.syncthingManager.DataLoaded += (o, e) => RebuildFolders();
            this.syncthingManager.Folders.FoldersChanged += (o, e) => RebuildFolders();
            this.syncthingManager.Folders.StatusChanged += (o, e) => FolderStatusChanged(e.FolderId, e.FolderStatus?.NeedTotalItems ?? 0);
            this.syncthingManager.Folders.SyncStateChanged += (o, e) => FolderSyncStateChanged(e.FolderId, e.PrevSyncState, e.SyncState);
            this.syncthingManager.Folders.FolderErrorsChanged += (o, e) => FolderErrorsChanged(e.FolderId, e.Errors);
        }

        public bool IsEnabled
        {
            get { lock (syncRoot) { return isEnabled; } }
            set
            {
                lock (syncRoot)
                {
                    if (isEnabled == value)
                        return;
                    isEnabled = value;

                    if (value)
                    {
                        workerCts = new CancellationTokenSource();
                        _ = WorkerLoopAsync(workerCts.Token);
                    }
                    else
                    {
                        workerCts?.Cancel();
                        workerCts = null;
                    }
                }
                RebuildFolders();
            }
        }

        public IReadOnlyList<string> Roots
        {
            get { lock (syncRoot) { return roots; } }
        }

        public bool TryResolve(string path, out FolderPath folderPath)
        {
            lock (syncRoot)
            {
                return resolver.TryResolve(path, out folderPath);
            }
        }

        public bool IsFolderPaused(string folderId)
        {
            lock (syncRoot)
            {
                return folders.TryGetValue(folderId, out var entry) && entry.Info.IsPaused;
            }
        }

        public ExplorerItemState GetState(string path)
        {
            lock (syncRoot)
            {
                if (!resolver.TryResolve(path, out var folderPath) || !folders.TryGetValue(folderPath.FolderId, out var entry))
                    return ExplorerItemState.None;

                if (entry.Info.IsPaused)
                    return ExplorerItemState.None;

                entry.RememberShown(folderPath.RelativePath);
                return ExplorerStateCalculator.Calculate(entry.Info, folderPath.RelativePath, IsIgnored(entry, folderPath.RelativePath));
            }
        }

        public bool? IsIgnored(FolderPath path)
        {
            lock (syncRoot)
            {
                return folders.TryGetValue(path.FolderId, out var entry) ? IsIgnored(entry, path.RelativePath) : null;
            }
        }

        private static bool? IsIgnored(FolderEntry entry, string relativePath)
        {
            // The root can't be ignored
            if (relativePath.Length == 0)
                return false;
            return entry.Matcher?.IsIgnored(relativePath);
        }

        public async Task<IgnoreMatcher> RefreshIgnoresAsync(string folderId, CancellationToken cancellationToken)
        {
            // Refreshes may overlap: a response to an older request mustn't replace a newer one
            var generation = Interlocked.Increment(ref ignoresRequestCount);
            var patterns = await syncthingManager.FetchExpandedIgnoresAsync(folderId, cancellationToken);
            var matcher = new IgnoreMatcher(patterns);

            var changed = new List<string>();
            lock (syncRoot)
            {
                if (!folders.TryGetValue(folderId, out var entry))
                    return matcher;
                if (generation < entry.MatcherGeneration)
                    return matcher;
                entry.MatcherGeneration = generation;

                var previous = entry.Matcher;
                if (previous != null && entry.Patterns.SequenceEqual(patterns))
                    return previous;

                entry.Matcher = matcher;
                entry.Patterns = patterns;

                // Refresh whatever Explorer has shown whose ignore state changed (or was unknown so far)
                foreach (var path in entry.RecentlyShown)
                {
                    if (previous == null || previous.IsIgnored(path) != matcher.IsIgnored(path))
                        changed.Add(ToAbsolute(entry, path));
                }
            }

            logger.Debug("Loaded {0} ignore patterns for folder {1}", patterns.Count, folderId);
            shellNotifier.ItemsChanged(changed);
            return matcher;
        }

        private void RebuildFolders()
        {
            var notifyDirectories = new List<string>();
            lock (syncRoot)
            {
                var currentFolders = isEnabled && syncthingManager.State == SyncthingState.Running
                    ? syncthingManager.Folders.FetchAll() ?? []
                    : [];

                var newFolders = new Dictionary<string, FolderEntry>();
                foreach (var folder in currentFolders)
                {
                    if (folders.TryGetValue(folder.FolderId, out var existing) &&
                        String.Equals(existing.Root, FolderEntry.NormalizeRoot(folder.Path), StringComparison.OrdinalIgnoreCase))
                    {
                        if (existing.Info.IsPaused != folder.IsPaused)
                            notifyDirectories.Add(existing.Root);
                        existing.Info.IsPaused = folder.IsPaused;
                        existing.UpdateWatcher(folder.IsPaused ? null : fileWatcherFactory);
                        newFolders[folder.FolderId] = existing;
                        folders.Remove(folder.FolderId);
                    }
                    else
                    {
                        var entry = new FolderEntry(folder.FolderId, folder.Path, folder.IsFsWatcherEnabled, this);
                        entry.Info.IsPaused = folder.IsPaused;
                        entry.Info.HasFolderError = folder.SyncState == FolderSyncState.Error;
                        foreach (var error in folder.FolderErrors)
                            entry.Info.Errors.Add(error.Path);
                        entry.UpdateWatcher(folder.IsPaused ? null : fileWatcherFactory);
                        newFolders[folder.FolderId] = entry;
                        notifyDirectories.Add(entry.Root);

                        ScheduleIgnoresRefresh(entry);
                        UpdateNeedTotal(entry, folder.Status?.NeedTotalItems ?? 0, []);
                    }
                }

                // Whatever is left was removed
                foreach (var removed in folders.Values)
                {
                    removed.Dispose();
                    notifyDirectories.Add(removed.Root);
                }

                folders = newFolders;
                resolver = new FolderPathResolver(newFolders.Values.Select(x => (x.FolderId, x.Root)));
                roots = resolver.Roots.ToList().AsReadOnly();
            }

            foreach (var directory in notifyDirectories)
                NotifyDirectoryAndRoot(directory);
        }

        private void FolderStatusChanged(string folderId, int needTotalItems)
        {
            var changed = new List<string>();
            lock (syncRoot)
            {
                if (!folders.TryGetValue(folderId, out var entry))
                    return;
                UpdateNeedTotal(entry, needTotalItems, changed);
            }

            shellNotifier.ItemsChanged(changed);
        }

        /// <summary>Must be called with the lock held. Adds the items whose state changed to <paramref name="changed"/>.</summary>
        private void UpdateNeedTotal(FolderEntry entry, int needTotalItems, List<string> changed)
        {
            if (needTotalItems > maxNeededItems)
            {
                // Too many to fetch: everything is shown as syncing, so there's no point fetching the list
                if (!entry.Info.NeededOverflow)
                {
                    changed.AddRange(entry.Info.Needed.Paths.SelectMany(WithAncestors).Distinct().Select(x => ToAbsolute(entry, x)));
                    changed.Add(entry.Root);
                    entry.Info.Needed.Clear();
                    entry.Info.NeededOverflow = true;
                    shellNotifier.DirectoryChanged(entry.Root);
                }
                entry.NeededRefreshDue = null;
            }
            else if (needTotalItems > 0)
            {
                ScheduleNeededRefresh(entry);
            }
            else if (entry.Info.Needed.Count > 0 || entry.Info.NeededOverflow)
            {
                changed.AddRange(entry.Info.Needed.Paths.SelectMany(WithAncestors).Distinct().Select(x => ToAbsolute(entry, x)));
                if (entry.Info.NeededOverflow)
                {
                    changed.Add(entry.Root);
                    shellNotifier.DirectoryChanged(entry.Root);
                }
                entry.Info.Needed.Clear();
                entry.Info.NeededOverflow = false;
                entry.NeededRefreshDue = null;
            }
        }

        private void FolderSyncStateChanged(string folderId, FolderSyncState prevState, FolderSyncState newState)
        {
            var changed = new List<string>();
            bool folderErrorChanged = false;
            string root = null;
            lock (syncRoot)
            {
                if (!folders.TryGetValue(folderId, out var entry))
                    return;
                root = entry.Root;

                var hasFolderError = newState == FolderSyncState.Error;
                folderErrorChanged = hasFolderError != entry.Info.HasFolderError;
                entry.Info.HasFolderError = hasFolderError;

                if (newState == FolderSyncState.Scanning && prevState != FolderSyncState.Scanning)
                {
                    entry.ScanStarted = DateTime.UtcNow;
                    // Syncthing reloads the ignore patterns at the start of each scan
                    ScheduleIgnoresRefresh(entry);
                }
                else if (prevState == FolderSyncState.Scanning && newState != FolderSyncState.Scanning && entry.ScanStarted != null)
                {
                    // Everything changed before the scan started has been seen by Syncthing now
                    changed.AddRange(entry.RemoveLocalChangesBefore(entry.ScanStarted.Value).Select(x => ToAbsolute(entry, x)));
                    entry.ScanStarted = null;
                }
            }

            if (folderErrorChanged)
                NotifyDirectoryAndRoot(root);
            shellNotifier.ItemsChanged(changed);
        }

        private void FolderErrorsChanged(string folderId, IEnumerable<FolderError> errors)
        {
            var changed = new List<string>();
            lock (syncRoot)
            {
                if (!folders.TryGetValue(folderId, out var entry))
                    return;

                var before = entry.Info.Errors.Paths.ToList();
                entry.Info.Errors.Clear();
                foreach (var error in errors)
                    entry.Info.Errors.Add(error.Path);

                changed.AddRange(before.Concat(entry.Info.Errors.Paths).SelectMany(WithAncestors).Distinct(StringComparer.OrdinalIgnoreCase).Select(x => ToAbsolute(entry, x)));
            }
            shellNotifier.ItemsChanged(changed);
        }

        private void LocalPathChanged(FolderEntry entry, string relativePath)
        {
            relativePath = PathSet.Normalize(relativePath);
            if (relativePath.Length == 0)
                return;

            List<string> changed;
            bool overflowStarted;
            lock (syncRoot)
            {
                if (!folders.TryGetValue(entry.FolderId, out var current) || current != entry)
                    return;
                // Changes to ignored items (and Syncthing's own files) are never synced, so they don't count
                if (entry.Matcher?.IsIgnored(relativePath) != false)
                    return;

                var wasOverflowing = entry.Info.LocalChangesOverflow;
                var added = entry.AddLocalChange(relativePath, DateTime.UtcNow, maxLocalChanges);
                overflowStarted = !wasOverflowing && entry.Info.LocalChangesOverflow;
                if (!added && !overflowStarted)
                    return;

                // Explorer refreshes the changed item by itself, but not its parents
                changed = overflowStarted ? [entry.Root] : PathSet.Ancestors(relativePath).Select(x => ToAbsolute(entry, x)).ToList();
            }

            if (overflowStarted)
            {
                logger.Debug("Too many local changes in folder {0}, showing everything as syncing until the next scan", entry.FolderId);
                shellNotifier.DirectoryChanged(entry.Root);
            }
            shellNotifier.ItemsChanged(changed);
        }

        private void ScheduleIgnoresRefresh(FolderEntry entry)
        {
            entry.IgnoresRefreshDue = true;
            workSignal.Release();
        }

        private void ScheduleNeededRefresh(FolderEntry entry)
        {
            if (entry.NeededRefreshDue != null)
                return;

            var due = entry.LastNeededRefresh + neededRefreshInterval;
            entry.NeededRefreshDue = due > DateTime.UtcNow ? due : DateTime.UtcNow;
            workSignal.Release();
        }

        private async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await workSignal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                    while (workSignal.CurrentCount > 0)
                        workSignal.Wait(0);

                    var ignoresRefreshes = new List<string>();
                    var neededRefreshes = new List<string>();
                    var expiredChanges = new List<string>();
                    lock (syncRoot)
                    {
                        var now = DateTime.UtcNow;
                        foreach (var entry in folders.Values)
                        {
                            if (entry.IgnoresRefreshDue)
                            {
                                ignoresRefreshes.Add(entry.FolderId);
                                entry.IgnoresRefreshDue = false;
                            }
                            if (entry.NeededRefreshDue <= now)
                            {
                                neededRefreshes.Add(entry.FolderId);
                                entry.NeededRefreshDue = null;
                                entry.LastNeededRefresh = now;
                            }
                            // Folders without Syncthing's watcher only see local changes when scanning, so keep showing them until then
                            if (entry.IsFsWatcherEnabled)
                                expiredChanges.AddRange(entry.RemoveLocalChangesBefore(now - localChangeLifetime).Select(x => ToAbsolute(entry, x)));
                        }
                    }
                    shellNotifier.ItemsChanged(expiredChanges);

                    foreach (var folderId in ignoresRefreshes)
                    {
                        try
                        {
                            await RefreshIgnoresAsync(folderId, cancellationToken);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            logger.Debug(e, "Failed to load ignore patterns for folder {0}", folderId);
                        }
                    }

                    foreach (var folderId in neededRefreshes)
                    {
                        await RefreshNeededAsync(folderId, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                logger.Error(e, "Explorer status worker failed");
            }
        }

        private async Task RefreshNeededAsync(string folderId, CancellationToken cancellationToken)
        {
            IReadOnlyList<string> needed;
            try
            {
                needed = await syncthingManager.FetchNeededItemsAsync(folderId, maxNeededItems + 1, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.Debug(e, "Failed to fetch needed items for folder {0}", folderId);
                return;
            }

            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> changedAbsolute;
            lock (syncRoot)
            {
                if (!folders.TryGetValue(folderId, out var entry))
                    return;

                var overflow = needed.Count > maxNeededItems;
                if (overflow != entry.Info.NeededOverflow)
                {
                    entry.Info.NeededOverflow = overflow;
                    changed.Add(String.Empty);
                }

                var newNeeded = new HashSet<string>(needed.Take(maxNeededItems).Select(PathSet.Normalize), StringComparer.OrdinalIgnoreCase);
                foreach (var path in entry.Info.Needed.Paths.Where(x => !newNeeded.Contains(x)).ToList())
                {
                    entry.Info.Needed.Remove(path);
                    changed.UnionWith(WithAncestors(path));
                }
                foreach (var path in newNeeded)
                {
                    if (entry.Info.Needed.Add(path))
                        changed.UnionWith(WithAncestors(path));
                }

                if (changed.Count > 0)
                    logger.Debug("Folder {0} needs {1} items", folderId, entry.Info.Needed.Count);

                changedAbsolute = changed.Select(x => ToAbsolute(entry, x)).ToList();
            }
            shellNotifier.ItemsChanged(changedAbsolute);
        }

        private void NotifyDirectoryAndRoot(string root)
        {
            shellNotifier.ItemsChanged([root]);
            shellNotifier.DirectoryChanged(root);
        }

        private static IEnumerable<string> WithAncestors(string path) => PathSet.Ancestors(path).Append(path);

        private static string ToAbsolute(FolderEntry entry, string relativePath) =>
            relativePath.Length == 0 ? entry.Root : Path.Combine(entry.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            IsEnabled = false;
            lock (syncRoot)
            {
                foreach (var entry in folders.Values)
                    entry.Dispose();
                folders.Clear();
            }
        }

        private sealed class FolderEntry : IDisposable
        {
            private readonly ExplorerStatusService owner;
            private readonly Dictionary<string, DateTime> localChangeTimes = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> recentlyShown = new(StringComparer.OrdinalIgnoreCase);
            private DateTime? localChangesOverflowSince;
            private FileWatcher watcher;

            public string FolderId { get; }
            public string Root { get; }
            public bool IsFsWatcherEnabled { get; }
            public FolderSyncInfo Info { get; } = new();
            public IgnoreMatcher Matcher { get; set; }
            public long MatcherGeneration { get; set; }
            public IReadOnlyList<string> Patterns { get; set; } = [];
            public bool IgnoresRefreshDue { get; set; }
            public DateTime? NeededRefreshDue { get; set; }
            public DateTime LastNeededRefresh { get; set; } = DateTime.MinValue;
            public DateTime? ScanStarted { get; set; }
            public IEnumerable<string> RecentlyShown => recentlyShown;

            public FolderEntry(string folderId, string root, bool isFsWatcherEnabled, ExplorerStatusService owner)
            {
                FolderId = folderId;
                Root = NormalizeRoot(root);
                IsFsWatcherEnabled = isFsWatcherEnabled;
                this.owner = owner;
            }

            public static string NormalizeRoot(string root)
            {
                // Explorer always uses long paths, so expand any 8.3 short names (e.g. "JOHNDO~1")
                if (root.Contains('~'))
                {
                    try
                    {
                        root = PathEx.GetLongPathName(root);
                    }
                    catch (Exception e)
                    {
                        logger.Debug(e, "Unable to expand short path {0}", root);
                    }
                }
                return FolderPathResolver.NormalizeRoot(root);
            }

            public void RememberShown(string relativePath)
            {
                if (recentlyShown.Count >= maxRecentlyShown)
                    recentlyShown.Clear();
                recentlyShown.Add(relativePath);
            }

            public void UpdateWatcher(IFileWatcherFactory factory)
            {
                if (factory == null)
                {
                    watcher?.Dispose();
                    watcher = null;
                    return;
                }
                if (watcher != null)
                    return;

                try
                {
                    watcher = factory.Create(FileWatcherMode.All, Root, TimeSpan.FromMinutes(1));
                    watcher.PathChanged += (o, e) => owner.LocalPathChanged(this, e.Path);
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Unable to watch {0} for local changes", Root);
                }
            }

            /// <summary>
            /// Records a local change. Returns false if it was already known, or if there are too many to track: then
            /// the whole folder counts as changed until the next scan (<see cref="FolderSyncInfo.LocalChangesOverflow"/>).
            /// </summary>
            public bool AddLocalChange(string relativePath, DateTime time, int maxLocalChanges)
            {
                if (Info.LocalChangesOverflow)
                {
                    localChangesOverflowSince ??= time;
                    return false;
                }
                if (!localChangeTimes.ContainsKey(relativePath) && localChangeTimes.Count >= maxLocalChanges)
                {
                    localChangeTimes.Clear();
                    Info.LocalChanges.Clear();
                    Info.LocalChangesOverflow = true;
                    localChangesOverflowSince = time;
                    return false;
                }
                localChangeTimes[relativePath] = time;
                return Info.LocalChanges.Add(relativePath);
            }

            public List<string> RemoveLocalChangesBefore(DateTime time)
            {
                var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // The overflow ends once everything since it started has been seen
                if (Info.LocalChangesOverflow && localChangesOverflowSince < time)
                {
                    Info.LocalChangesOverflow = false;
                    localChangesOverflowSince = null;
                    changed.Add(String.Empty);
                }

                var removed = localChangeTimes.Where(x => x.Value < time).Select(x => x.Key).ToList();
                foreach (var path in removed)
                {
                    localChangeTimes.Remove(path);
                    Info.LocalChanges.Remove(path);
                    changed.UnionWith(WithAncestors(path));
                }
                return changed.ToList();
            }

            public void Dispose()
            {
                watcher?.Dispose();
                watcher = null;
            }
        }
    }
}
