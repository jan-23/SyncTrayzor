using NLog;
using SyncTrayzor.Localization;
using SyncTrayzor.Properties;
using SyncTrayzor.Syncthing;
using SyncTrayzor.Syncthing.Folders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    /// <summary>A removal or download was refused or failed. The message is meant for the user.</summary>
    public class SelectiveSyncException : Exception
    {
        public SelectiveSyncException(string message) : base(message) { }
    }

    public record SubfolderInfo(string Name, FolderPath Path, bool IsSynced);

    /// <summary>The outcome of removing one directory: <see cref="Error"/> is null if it was removed</summary>
    public record RemovalResult(FolderPath Directory, string Error)
    {
        public bool Succeeded => Error == null;
    }

    public interface ISelectiveSyncService
    {
        /// <summary>
        /// Whether directories can be removed from this device. This needs a Syncthing version which reliably
        /// reports which other devices have a valid copy of a file.
        /// </summary>
        bool CanRemove { get; }

        /// <summary>
        /// Allows removing directories although the Syncthing version can't report reliably which other devices have
        /// a valid copy (see <see cref="Syncthing.ISyncthingCapabilities.SupportsReliableAvailability"/>). Then a copy
        /// on a device which ignores the files counts too, so the last copy may be deleted.
        /// </summary>
        bool SkipSyncthingVersionCheck { get; set; }

        /// <summary>The question to confirm before removing the named directories, with a warning if it's less safe</summary>
        string ConfirmRemoveMessage(string names);

        /// <summary>
        /// Stops syncing subfolders to this device and deletes their local copies (to the Recycle Bin), keeping empty
        /// placeholders. See <see cref="SelectiveSyncService"/> for the checks. Each directory is checked on its own;
        /// the ignore patterns are changed once for all of them.
        /// </summary>
        Task<IReadOnlyList<RemovalResult>> RemoveFromDeviceAsync(IReadOnlyList<FolderPath> directories, CancellationToken cancellationToken);

        /// <summary>
        /// Starts syncing subfolders to this device again, which SyncTrayzor removed. Syncthing then downloads them.
        /// Throws a <see cref="SelectiveSyncException"/> if one stays excluded by the user's own ignore patterns.
        /// </summary>
        Task DownloadToDeviceAsync(IReadOnlyList<FolderPath> directories, CancellationToken cancellationToken);

        /// <summary>The direct subfolders of a directory, as known to any device, and whether each is synced to this one</summary>
        Task<IReadOnlyList<SubfolderInfo>> ListSubfoldersAsync(FolderPath directory, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Removes subfolders from this device, and downloads them again.
    /// </summary>
    /// <remarks>
    /// Removing must never lose data or delete anything on other devices. A directory is only removed if:
    /// <list type="number">
    ///   <item>no other Syncthing folder contains it or is inside it, it and its contents are no links or junctions, it
    ///   contains no ignore file used by an #include, no paths too long for the Recycle Bin, and no files which the
    ///   ignore patterns already exclude (this device has the only copy of those, except for "(?d)" patterns);</item>
    ///   <item>after a rescan, every file on disk matches Syncthing's index, this device has the newest version, and
    ///   another trusted device that's connected right now has a valid copy of that version;</item>
    ///   <item>nobody changed the ignore patterns during these checks;</item>
    ///   <item>Syncthing's index confirms the directory is ignored (so it won't notice the deletion), no exception
    ///   pattern keeps anything inside it synced, and after a short wait (so that another device removing the same
    ///   directory at the same time has announced that), another device still has each file.</item>
    /// </list>
    /// Then only the checked files are moved to the Recycle Bin, and only if they haven't changed since.
    ///
    /// If anything fails once the directory is about to be ignored, the ignore patterns are restored: Syncthing then
    /// downloads whatever was already deleted, as it doesn't treat files missing after an ignore as deletions. Removals
    /// in progress are written to a <see cref="IRemovalJournal"/>, so an interrupted one is rolled back on the next start.
    /// </remarks>
    public class SelectiveSyncService : ISelectiveSyncService
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private static readonly TimeSpan confirmPollInterval = TimeSpan.FromMilliseconds(500);
        // Requests to Syncthing in parallel when checking a directory's files
        private const int fileCheckParallelism = 8;
        // SHFileOperation can't handle longer paths
        private const int maxPathLength = 259;

        private readonly ISyncthingManager syncthingManager;
        private readonly IExplorerStatusService statusService;
        private readonly IShellChangeNotifier shellNotifier;
        private readonly IRecycleBin recycleBin;
        private readonly IRemovalJournal journal;

        // Changes to .stignore are read-modify-write, so do one operation at a time
        private readonly SemaphoreSlim operationLock = new(1, 1);

        public SelectiveSyncService(
            ISyncthingManager syncthingManager,
            IExplorerStatusService statusService,
            IShellChangeNotifier shellNotifier,
            IRecycleBin recycleBin,
            IRemovalJournal journal)
        {
            this.syncthingManager = syncthingManager;
            this.statusService = statusService;
            this.shellNotifier = shellNotifier;
            this.recycleBin = recycleBin;
            this.journal = journal;

            // Roll back removals which were interrupted, once Syncthing is running
            this.syncthingManager.DataLoaded += (o, e) => _ = RecoverPendingRemovalsAsync();
        }

        public bool CanRemove => SkipSyncthingVersionCheck || syncthingManager.Capabilities.SupportsReliableAvailability;

        // Set from the configuration, read by operations on other threads
        private volatile bool skipSyncthingVersionCheck;
        public bool SkipSyncthingVersionCheck
        {
            get => skipSyncthingVersionCheck;
            set
            {
                if (value && !skipSyncthingVersionCheck)
                    logger.Warn("Removing subfolders is allowed with any Syncthing version: copies on devices which ignore them may count as valid");
                skipSyncthingVersionCheck = value;
            }
        }

        /// <summary>How long to wait for Syncthing to confirm that removed directories are ignored</summary>
        public TimeSpan ConfirmIgnoredTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// How long to wait after that before checking once more that other devices have the files: if another device
        /// removes the same directory at the same time, this gives its index update time to arrive.
        /// </summary>
        public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(10);

        public string ConfirmRemoveMessage(string names)
        {
            var message = Localizer.F(Resources.SelectiveSync_ConfirmRemove_Message, names);
            if (!syncthingManager.Capabilities.SupportsReliableAvailability)
                message += Environment.NewLine + Environment.NewLine + Resources.SelectiveSync_ConfirmRemove_UnreliableWarning;
            return message;
        }

        public Task<IReadOnlyList<RemovalResult>> RemoveFromDeviceAsync(IReadOnlyList<FolderPath> directories, CancellationToken cancellationToken)
        {
            // Off the UI thread: enumerating and deleting files can take a while
            return Task.Run(async () =>
            {
                await operationLock.WaitAsync(cancellationToken);
                try
                {
                    var results = new List<RemovalResult>();
                    foreach (var group in directories.Distinct().GroupBy(x => x.FolderId))
                        results.AddRange(await RemoveFromFolderAsync(group.Key, group.ToList(), cancellationToken));
                    return (IReadOnlyList<RemovalResult>)results;
                }
                finally
                {
                    operationLock.Release();
                }
            }, cancellationToken);
        }

        /// <summary>What was checked in a directory, and may be deleted if it hasn't changed since</summary>
        private sealed record RemovalPlan(FolderPath Directory, string LocalPath, bool ExistedLocally, Dictionary<string, FileState> Files);

        /// <param name="GlobalVersion">The newest version which another device had, or null for files which are never synced</param>
        private sealed record FileState(long Size, DateTime ModifiedUtc, IReadOnlyList<string> GlobalVersion);

        private async Task<List<RemovalResult>> RemoveFromFolderAsync(string folderId, List<FolderPath> directories, CancellationToken cancellationToken)
        {
            var results = new List<RemovalResult>();
            var plans = new List<RemovalPlan>();

            // 1. Check each directory (nothing is changed yet)
            var originalLines = await syncthingManager.FetchIgnoresAsync(folderId, cancellationToken);
            var matcher = await statusService.RefreshIgnoresAsync(folderId, cancellationToken);
            foreach (var directory in directories)
            {
                try
                {
                    plans.Add(await PrepareAsync(directory, matcher, originalLines, cancellationToken));
                }
                catch (SelectiveSyncException e)
                {
                    logger.Info("Not removing {0} in folder {1}: {2}", directory.RelativePath, folderId, e.Message);
                    results.Add(new RemovalResult(directory, e.Message));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.Warn(e, "Checking {0} in folder {1} failed", directory.RelativePath, folderId);
                    results.Add(new RemovalResult(directory, Localizer.F(Resources.SelectiveSync_Error_CheckFailed, DisplayName(directory), e.Message)));
                }
            }
            if (plans.Count == 0)
                return results;

            // The checks take a while: don't overwrite changes made to the ignore patterns meanwhile
            var currentLines = await syncthingManager.FetchIgnoresAsync(folderId, cancellationToken);
            if (!currentLines.SequenceEqual(originalLines))
            {
                logger.Info("The ignore patterns of folder {0} changed while checking: not removing anything", folderId);
                foreach (var plan in plans)
                    results.Add(new RemovalResult(plan.Directory, Localizer.F(Resources.SelectiveSync_Error_IgnoresChanged, DisplayName(plan.Directory))));
                return results;
            }

            // From here on, anything that goes wrong must be rolled back, so nothing is left ignored with local data.
            // The journal makes sure that also happens if SyncTrayzor is interrupted.
            var pending = plans.Select(x => new PendingRemoval(folderId, x.Directory.RelativePath)).ToList();
            journal.Add(pending);
            var removed = new List<RemovalPlan>();
            var failed = new Dictionary<FolderPath, string>();
            IReadOnlyList<string> writtenLines = null;
            try
            {
                // 2. Ignore them all at once, so Syncthing only rescans once
                logger.Info("Removing {0} in folder {1} from this device", String.Join(", ", plans.Select(x => x.Directory.RelativePath)), folderId);
                writtenLines = IgnorePatternEditor.Ignore(originalLines, plans.Select(x => x.Directory.RelativePath).ToList());
                await syncthingManager.SetIgnoresAsync(folderId, writtenLines, cancellationToken);

                // 3. Wait until Syncthing has applied that, so it won't notice the deletions
                var confirmed = await WaitUntilIgnoredInIndexAsync(folderId, plans.Select(x => x.Directory).ToList(), cancellationToken);
                if (confirmed.Count > 0)
                    await Task.Delay(SettleDelay, cancellationToken);
                matcher = await statusService.RefreshIgnoresAsync(folderId, cancellationToken);
                var folder = FetchFolder(folderId);

                // 4. Delete what was checked
                foreach (var plan in plans)
                {
                    var error = confirmed.Contains(plan.Directory)
                        ? await DeleteCheckedFilesAsync(plan, matcher, folder, cancellationToken)
                        : Localizer.F(Resources.SelectiveSync_Error_NotConfirmed, DisplayName(plan.Directory));
                    if (error == null)
                        removed.Add(plan);
                    else
                        failed[plan.Directory] = error;
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Removing directories in folder {0} failed", folderId);
                var message = Localizer.F(Resources.ExplorerIntegration_ActionFailed_Message, e.Message);
                foreach (var plan in plans.Where(x => !removed.Contains(x)))
                    failed.TryAdd(plan.Directory, message);
            }

            var rollBackWritten = true;
            if (failed.Count > 0)
            {
                // 5. Restore the ignore patterns of the directories which weren't removed
                var (stillIgnored, written) = await RollBackAsync(folderId, originalLines, writtenLines,
                    removed.Select(x => x.Directory.RelativePath).ToList(), failed.Keys.ToList());
                rollBackWritten = written;
                foreach (var directory in stillIgnored)
                    failed[directory] = Localizer.F(Resources.SelectiveSync_Error_Incomplete, DisplayName(directory));
            }

            // Removed directories and written rollbacks are done with. A rollback which couldn't be written stays in the
            // journal, to be retried on the next start.
            var done = rollBackWritten ? pending : pending.Where(x => removed.Any(r => r.Directory.RelativePath == x.RelativePath));
            journal.Remove(done);

            foreach (var plan in plans)
            {
                if (failed.TryGetValue(plan.Directory, out var error))
                {
                    logger.Info("Not removing {0} in folder {1}: {2}", plan.Directory.RelativePath, folderId, error);
                    results.Add(new RemovalResult(plan.Directory, error));
                }
                else
                {
                    logger.Info("Removed {0} in folder {1} from this device", plan.Directory.RelativePath, folderId);
                    results.Add(new RemovalResult(plan.Directory, null));
                }
                shellNotifier.ItemsChanged([plan.LocalPath]);
                shellNotifier.DirectoryChanged(plan.LocalPath);
            }
            return results;
        }

        /// <summary>
        /// Makes the failed directories synced again. Restores exactly the original lines (plus the removed directories)
        /// if nobody changed .stignore meanwhile, or else un-ignores the failed directories. Returns those which are
        /// still ignored afterwards, and whether the rollback could be written at all.
        /// </summary>
        private async Task<(List<FolderPath> StillIgnored, bool Written)> RollBackAsync(string folderId, IReadOnlyList<string> originalLines,
            IReadOnlyList<string> writtenLines, IReadOnlyList<string> removedPaths, IReadOnlyList<FolderPath> failed)
        {
            try
            {
                var current = await syncthingManager.FetchIgnoresAsync(folderId, CancellationToken.None);
                List<string> restored;
                if (writtenLines == null || current.SequenceEqual(originalLines))
                    restored = null; // the patterns were never changed
                else if (current.SequenceEqual(writtenLines))
                    restored = IgnorePatternEditor.Ignore(originalLines, removedPaths);
                else
                    restored = IgnorePatternEditor.RemoveIgnores(current, failed.Select(x => x.RelativePath));

                if (restored != null && !restored.SequenceEqual(current))
                    await syncthingManager.SetIgnoresAsync(folderId, restored, CancellationToken.None);

                // Syncthing then downloads whatever was already deleted again
                var matcher = await statusService.RefreshIgnoresAsync(folderId, CancellationToken.None);
                return (failed.Where(x => matcher.IsIgnored(x.RelativePath)).ToList(), true);
            }
            catch (Exception e)
            {
                logger.Error(e, "Rolling back the removal of {0} in folder {1} failed", String.Join(", ", failed.Select(x => x.RelativePath)), folderId);
                return (failed.ToList(), false);
            }
        }

        /// <summary>Rolls back removals which were interrupted (recorded in the journal)</summary>
        private async Task RecoverPendingRemovalsAsync()
        {
            var pending = journal.Pending;
            if (pending.Count == 0)
                return;

            await operationLock.WaitAsync();
            try
            {
                foreach (var group in journal.Pending.GroupBy(x => x.FolderId))
                {
                    try
                    {
                        logger.Warn("Removing {0} in folder {1} was interrupted: syncing it again", String.Join(", ", group.Select(x => x.RelativePath)), group.Key);
                        if (syncthingManager.Folders.TryFetchById(group.Key, out _))
                        {
                            var lines = await syncthingManager.FetchIgnoresAsync(group.Key, CancellationToken.None);
                            // Only remove what the removal added: if that's gone already, the user's own patterns apply
                            var restored = IgnorePatternEditor.RemoveIgnores(lines, group.Select(x => x.RelativePath));
                            if (!restored.SequenceEqual(lines))
                                await syncthingManager.SetIgnoresAsync(group.Key, restored, CancellationToken.None);
                            await statusService.RefreshIgnoresAsync(group.Key, CancellationToken.None);
                        }
                        journal.Remove(group);
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Unable to sync interrupted removals in folder {0} again; will retry", group.Key);
                    }
                }
            }
            finally
            {
                operationLock.Release();
            }
        }

        private async Task<RemovalPlan> PrepareAsync(FolderPath directory, IgnoreMatcher matcher, IReadOnlyList<string> lines, CancellationToken cancellationToken)
        {
            var name = DisplayName(directory);
            var localPath = ToAbsolute(directory);

            if (!CanRemove)
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_Unsupported, name));
            if (directory.IsRoot)
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_IsRoot, name));
            // A directory which isn't here (yet) can be removed too: then only its download stops
            if (File.Exists(localPath))
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_NotADirectory, name));
            var existsLocally = Directory.Exists(localPath);
            var folder = FetchFolder(directory.FolderId);
            if (folder == null || folder.IsReceiveEncrypted)
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_NotADirectory, name));
            if (folder.IsPaused)
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_FolderPaused, name));
            if (!folder.DeviceIds.Any(x => x != syncthingManager.LocalDeviceId && !folder.UntrustedDeviceIds.Contains(x)))
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_NotShared, name));
            if (matcher.IsIgnored(directory.RelativePath))
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_AlreadyRemoved, name));
            if (!recycleBin.IsAvailableFor(localPath))
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_NoRecycleBin, name));

            // Another Syncthing folder inside (or being this directory) would sync the deletion to its own devices...
            var otherRoots = syncthingManager.Folders.FetchAll()
                .Where(x => x.FolderId != directory.FolderId)
                .Select(x => ExplorerStatusService.NormalizeRoot(x.Path))
                .ToList();
            var nestedRoot = otherRoots.FirstOrDefault(x => IsBelow(x, localPath) || SamePath(x, localPath));
            if (nestedRoot != null)
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_NestedFolder, name, nestedRoot));
            // ... and so would one which contains this folder (it isn't affected by this folder's ignore patterns)
            var enclosingRoot = otherRoots.FirstOrDefault(x => IsBelow(localPath, x));
            if (enclosingRoot != null)
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_InsideOtherFolder, name, enclosingRoot));

            // The directory itself, or a directory above it, may be a link to somewhere else
            for (var current = new DirectoryInfo(localPath); current != null && !SamePath(current.FullName, directory.Root); current = current.Parent)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_Link, name, current.FullName));
            }

            // Syncthing would fail to load the ignore patterns (and stop syncing the whole folder) without an #include file
            var include = IncludedFiles(directory.Root, lines).FirstOrDefault(x => IsBelow(x, localPath));
            if (include != null)
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_IncludeFile, name, Path.GetRelativePath(localPath, include)));

            // Make sure Syncthing has seen every local change below the directory, including its deletion (this waits for the scan)
            try
            {
                await syncthingManager.ScanOrThrowAsync(directory.FolderId, directory.RelativePath);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.Info(e, "Scanning {0} failed", directory.RelativePath);
                throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_ScanFailed, name));
            }

            // Take stock of what's on disk. Everything found here is checked, and only this is deleted later.
            var files = new Dictionary<string, FileState>(StringComparer.Ordinal);
            var toCheck = new List<string>();
            var deletable = new List<string>();
            foreach (var entry in existsLocally ? EnumerateEntries(localPath) : [])
            {
                var relativePath = ToRelative(directory, localPath, entry.FullName);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_Link, name, ToDisplay(directory, relativePath)));
                if (entry.FullName.Length > maxPathLength)
                    throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_PathTooLong, name, ToDisplay(directory, relativePath)));
                if (entry is not FileInfo file)
                    continue;

                // Syncthing's own temporary files (e.g. downloads in progress) are never synced and may go
                if (IgnoreMatcher.IsTemporary(relativePath) || IgnoreMatcher.IsInternal(relativePath))
                {
                    files[relativePath] = new FileState(file.Length, file.LastWriteTimeUtc, null);
                    continue;
                }

                var match = matcher.MatchPath(relativePath);
                if (match.Ignored)
                {
                    // Syncthing itself may delete "(?d)" files. Any other ignored file only exists on this device.
                    if (!match.Deletable)
                        throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_IgnoredInside, name, ToDisplay(directory, relativePath)));
                    deletable.Add(relativePath);
                    files[relativePath] = new FileState(file.Length, file.LastWriteTimeUtc, null);
                    continue;
                }

                files[relativePath] = new FileState(file.Length, file.LastWriteTimeUtc, null);
                toCheck.Add(relativePath);
            }

            // "(?d)" files: Syncthing's index must agree that they're ignored, rather than relying on our matcher alone
            foreach (var path in deletable)
            {
                var dbFile = await syncthingManager.FetchFileAsync(directory.FolderId, path, cancellationToken);
                if (dbFile?.Local != null && !dbFile.Local.Deleted && !dbFile.Local.Ignored)
                    throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_NotScanned, name, ToDisplay(directory, path)));
            }

            var failure = await CheckFilesAsync(directory.FolderId, folder, toCheck, files, cancellationToken);
            if (failure != null)
            {
                var (path, result) = failure.Value;
                var display = ToDisplay(directory, path);
                throw new SelectiveSyncException(result switch
                {
                    FileCheckResult.NotScanned => Localizer.F(Resources.SelectiveSync_Error_NotScanned, name, display),
                    FileCheckResult.LocalChanges => Localizer.F(Resources.SelectiveSync_Error_LocalChanges, name, display),
                    _ => Localizer.F(Resources.SelectiveSync_Error_NoValidCopy, name, display),
                });
            }

            return new RemovalPlan(directory, localPath, existsLocally, files);
        }

        /// <summary>
        /// Checks the files in parallel, recording the newest version of each. Returns the first which may not be
        /// deleted, or null.
        /// </summary>
        private async Task<(string Path, FileCheckResult Result)?> CheckFilesAsync(string folderId, Folder folder, List<string> paths, Dictionary<string, FileState> states, CancellationToken cancellationToken)
        {
            (string, FileCheckResult)? failure = null;
            var syncRoot = new object();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var options = new ParallelOptions() { MaxDegreeOfParallelism = fileCheckParallelism, CancellationToken = cts.Token };
                await Parallel.ForEachAsync(paths, options, async (path, token) =>
                {
                    var dbFile = await syncthingManager.FetchFileAsync(folderId, path, token);
                    FileState state;
                    lock (syncRoot)
                        state = states[path];
                    var result = RemovalSafety.CheckFile(dbFile, state.Size, state.ModifiedUtc, folder.ModTimeWindow, folder.UntrustedDeviceIds);
                    lock (syncRoot)
                    {
                        if (result == FileCheckResult.Ok)
                            states[path] = state with { GlobalVersion = dbFile.Global.Version };
                        else
                            failure ??= (path, result);
                    }
                    if (result != FileCheckResult.Ok)
                        cts.Cancel(); // no need to check the rest
                });
            }
            catch (OperationCanceledException) when (failure != null)
            {
            }
            return failure;
        }

        /// <summary>
        /// Moves the checked files to the Recycle Bin, if nothing changed since they were checked and other devices still
        /// have them, and removes the directories left empty. Returns an error message, or null if the directory is now
        /// an empty placeholder.
        /// </summary>
        private async Task<string> DeleteCheckedFilesAsync(RemovalPlan plan, IgnoreMatcher matcher, Folder folder, CancellationToken cancellationToken)
        {
            var name = DisplayName(plan.Directory);

            // Nothing was downloaded yet: just leave a placeholder. (If Syncthing created it meanwhile, its contents
            // weren't checked, and the check below refuses.)
            if (!plan.ExistedLocally && !Directory.Exists(plan.LocalPath))
            {
                Directory.CreateDirectory(plan.LocalPath);
                return null;
            }

            // Compare what's on disk now with what was checked
            var current = new List<(string RelativePath, FileSystemInfo Entry)>();
            foreach (var entry in EnumerateEntries(plan.LocalPath))
                current.Add((ToRelative(plan.Directory, plan.LocalPath, entry.FullName), entry));

            // An exception ("!" pattern) may still sync something inside, whose deletion would be synced
            var stillSynced = RemovalSafety.NotIgnored(matcher, current.Select(x => x.RelativePath));
            if (stillSynced.Count > 0)
                return Localizer.F(Resources.SelectiveSync_Error_ExceptionInside, name, ToDisplay(plan.Directory, stillSynced[0]));

            foreach (var (relativePath, entry) in current)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return Localizer.F(Resources.SelectiveSync_Error_Link, name, ToDisplay(plan.Directory, relativePath));
                if (entry is FileInfo file && !IsUnchanged(plan, relativePath, file))
                    return Localizer.F(Resources.SelectiveSync_Error_ChangedMeanwhile, name, ToDisplay(plan.Directory, relativePath));
            }

            // Another device may have removed the same files meanwhile: check again right before deleting
            var checkedFiles = plan.Files.Where(x => x.Value.GlobalVersion != null).ToList();
            string gone = null;
            await Parallel.ForEachAsync(checkedFiles, new ParallelOptions() { MaxDegreeOfParallelism = fileCheckParallelism, CancellationToken = cancellationToken }, async (file, token) =>
            {
                var dbFile = await syncthingManager.FetchFileAsync(plan.Directory.FolderId, file.Key, token);
                if (!RemovalSafety.StillAvailable(dbFile, file.Value.GlobalVersion, folder.UntrustedDeviceIds))
                    Interlocked.CompareExchange(ref gone, file.Key, null);
            });
            if (gone != null)
                return Localizer.F(Resources.SelectiveSync_Error_NoLongerAvailable, name, ToDisplay(plan.Directory, gone));

            // Check each file once more right before deleting, as it may be written to at any time
            var toDelete = new List<string>();
            foreach (var (relativePath, entry) in current.Where(x => x.Entry is FileInfo))
            {
                var file = new FileInfo(entry.FullName);
                if (!file.Exists)
                    continue;
                if (!IsUnchanged(plan, relativePath, file))
                    return Localizer.F(Resources.SelectiveSync_Error_ChangedMeanwhile, name, ToDisplay(plan.Directory, relativePath));
                toDelete.Add(file.FullName);
            }
            var notMoved = recycleBin.MoveToRecycleBin(toDelete);
            if (notMoved.Count > 0)
                return Localizer.F(Resources.SelectiveSync_Error_RecycleFailed, name, Path.GetRelativePath(plan.LocalPath, notMoved[0]));

            // Remove the directories left empty, deepest first, keeping the directory itself as a placeholder
            foreach (var directory in current.Where(x => x.Entry is DirectoryInfo).Select(x => x.Entry.FullName).OrderByDescending(x => x.Length))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(directory).Any())
                        continue;
                    // E.g. folders customized in Explorer are read-only
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                        info.Attributes &= ~FileAttributes.ReadOnly;
                    info.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    logger.Debug(e, "Unable to delete empty directory {0}", directory);
                }
            }

            var left = Directory.EnumerateFileSystemEntries(plan.LocalPath).FirstOrDefault();
            if (left != null)
                return Localizer.F(Resources.SelectiveSync_Error_ChangedMeanwhile, name, Path.GetRelativePath(plan.LocalPath, left));
            return null;
        }

        private static bool IsUnchanged(RemovalPlan plan, string relativePath, FileInfo file)
        {
            return plan.Files.TryGetValue(relativePath, out var state) &&
                state.Size == file.Length && state.ModifiedUtc == file.LastWriteTimeUtc;
        }

        public Task DownloadToDeviceAsync(IReadOnlyList<FolderPath> directories, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                await operationLock.WaitAsync(cancellationToken);
                try
                {
                    foreach (var group in directories.Where(x => !x.IsRoot).GroupBy(x => x.FolderId))
                    {
                        logger.Info("Downloading {0} in folder {1} to this device", String.Join(", ", group.Select(x => x.RelativePath)), group.Key);
                        var lines = await syncthingManager.FetchIgnoresAsync(group.Key, cancellationToken);
                        var newLines = IgnorePatternEditor.RemoveIgnores(lines, group.Select(x => x.RelativePath));
                        if (!newLines.SequenceEqual(lines))
                            await syncthingManager.SetIgnoresAsync(group.Key, newLines, cancellationToken);
                        // Syncthing rescans after saving, which applies the patterns and starts downloading
                        var matcher = await statusService.RefreshIgnoresAsync(group.Key, cancellationToken);

                        // SyncTrayzor only removes its own lines. What the user's own patterns exclude, they change themselves.
                        var stillIgnored = group.Where(x => matcher.IsIgnored(x.RelativePath)).ToList();
                        if (stillIgnored.Count > 0)
                            throw new SelectiveSyncException(Localizer.F(Resources.SelectiveSync_Error_IgnoredByUser, DisplayName(stillIgnored[0])));
                    }
                }
                finally
                {
                    operationLock.Release();
                }
            }, cancellationToken);
        }

        public Task<IReadOnlyList<SubfolderInfo>> ListSubfoldersAsync(FolderPath directory, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                var matcher = await statusService.RefreshIgnoresAsync(directory.FolderId, cancellationToken);
                var prefix = directory.RelativePath;

                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in await syncthingManager.BrowseDirectoriesAsync(directory.FolderId, prefix, cancellationToken))
                {
                    if (entry.IsDirectory)
                        names.Add(entry.Name);
                }

                var localPath = ToAbsolute(directory);
                if (Directory.Exists(localPath))
                {
                    foreach (var local in Directory.EnumerateDirectories(localPath))
                        names.Add(Path.GetFileName(local));
                }

                var result = new List<SubfolderInfo>(names.Count);
                foreach (var name in names)
                {
                    var relativePath = prefix.Length == 0 ? name : prefix + "/" + name;
                    // Syncthing's own directories (.stversions, .stfolder) aren't something to choose
                    if (IgnoreMatcher.IsInternal(relativePath))
                        continue;
                    var path = directory with { RelativePath = relativePath };
                    result.Add(new SubfolderInfo(name, path, !matcher.IsIgnored(relativePath)));
                }
                return (IReadOnlyList<SubfolderInfo>)result;
            }, cancellationToken);
        }

        private async Task<HashSet<FolderPath>> WaitUntilIgnoredInIndexAsync(string folderId, List<FolderPath> directories, CancellationToken cancellationToken)
        {
            var confirmed = new HashSet<FolderPath>();
            var start = DateTime.UtcNow;
            bool rescanRequested = false;
            while (DateTime.UtcNow - start < ConfirmIgnoredTimeout)
            {
                foreach (var directory in directories.Where(x => !confirmed.Contains(x)))
                {
                    if (await syncthingManager.IsIgnoredInIndexAsync(folderId, directory.RelativePath, cancellationToken) == true)
                        confirmed.Add(directory);
                }
                if (confirmed.Count == directories.Count)
                    break;

                // Syncthing schedules a scan after the ignore patterns change. If that's slow to happen, ask for one.
                if (!rescanRequested && DateTime.UtcNow - start > TimeSpan.FromSeconds(10))
                {
                    rescanRequested = true;
                    await syncthingManager.ScanAsync(folderId, null);
                    continue;
                }

                await Task.Delay(confirmPollInterval, cancellationToken);
            }
            return confirmed;
        }

        /// <summary>The files used by "#include" lines, as absolute paths (following includes in included files)</summary>
        private static List<string> IncludedFiles(string root, IEnumerable<string> lines)
        {
            var result = new List<string>();
            var queue = new Queue<(string File, IEnumerable<string> Lines)>();
            queue.Enqueue((Path.Combine(root, ".stignore"), lines));
            while (queue.Count > 0 && result.Count < 100)
            {
                var (file, fileLines) = queue.Dequeue();
                foreach (var line in fileLines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("#include", StringComparison.Ordinal))
                        continue;
                    var fields = trimmed.Split(' ', 2);
                    if (fields.Length != 2 || String.IsNullOrWhiteSpace(fields[1]))
                        continue;
                    var included = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), fields[1].Trim()));
                    if (result.Contains(included, StringComparer.OrdinalIgnoreCase))
                        continue;
                    result.Add(included);
                    try
                    {
                        if (File.Exists(included))
                            queue.Enqueue((included, File.ReadAllLines(included)));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        logger.Debug(e, "Unable to read included ignore file {0}", included);
                    }
                }
            }
            return result;
        }

        private Folder FetchFolder(string folderId) =>
            syncthingManager.Folders.TryFetchById(folderId, out var folder) ? folder : null;

        private static IEnumerable<FileSystemInfo> EnumerateEntries(string localPath)
        {
            // Don't skip hidden or system files (the default), and don't follow links into other places
            var options = new EnumerationOptions()
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
            };
            return new DirectoryInfo(localPath).EnumerateFileSystemInfos("*", options);
        }

        private static bool SamePath(string a, string b) =>
            String.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        // True if path is strictly inside directory
        private static bool IsBelow(string path, string directory) =>
            path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static string ToRelative(FolderPath directory, string localPath, string fullPath) =>
            directory.RelativePath + "/" + Path.GetRelativePath(localPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');

        // For messages: the path below the directory being removed
        private static string ToDisplay(FolderPath directory, string relativePath) =>
            relativePath.Substring(directory.RelativePath.Length + 1);

        private static string ToAbsolute(FolderPath path) =>
            path.IsRoot ? path.Root : Path.Combine(path.Root, path.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        private static string DisplayName(FolderPath path) =>
            path.IsRoot ? Path.GetFileName(path.Root.TrimEnd(Path.DirectorySeparatorChar)) : path.RelativePath.Substring(path.RelativePath.LastIndexOf('/') + 1);
    }
}
