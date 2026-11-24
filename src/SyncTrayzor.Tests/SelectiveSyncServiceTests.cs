using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SyncTrayzor.Services.ExplorerIntegration;
using SyncTrayzor.Syncthing;
using SyncTrayzor.Syncthing.ApiClient;
using SyncTrayzor.Syncthing.Folders;
using Xunit;

namespace SyncTrayzor.Tests
{
    /// <summary>
    /// Removing subfolders from this device, against a fake Syncthing and a real temporary folder
    /// </summary>
    public sealed class SelectiveSyncServiceTests : IDisposable
    {
        private const string FolderId = "photos";
        private const string LocalDevice = "LAPTOP";
        private const string OtherDevice = "SERVER";

        private readonly string root = Path.Combine(Path.GetTempPath(), "SyncTrayzorTests-" + Guid.NewGuid().ToString("N"));
        private readonly FakeSyncthing syncthing;
        private readonly FakeStatus status;
        private readonly FakeRecycleBin recycleBin = new();
        private readonly FakeJournal journal = new();
        private readonly SelectiveSyncService service;

        public SelectiveSyncServiceTests()
        {
            Directory.CreateDirectory(root);
            syncthing = FakeSyncthing.Create(root);
            status = new FakeStatus(syncthing, root);
            service = new SelectiveSyncService(syncthing.Manager, status, new NullShellNotifier(), recycleBin, journal)
            {
                ConfirmIgnoredTimeout = TimeSpan.FromSeconds(1),
            };
        }

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private string CreateFile(string relativePath, string content = "data")
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            syncthing.MarkSynced(relativePath, path);
            return path;
        }

        private FolderPath Dir(string relativePath) => new(FolderId, root, relativePath);

        private async Task<RemovalResult> RemoveAsync(string relativePath) =>
            (await service.RemoveFromDeviceAsync([Dir(relativePath)], CancellationToken.None)).Single();

        [Fact]
        public async Task RemovesVerifiedFilesAndKeepsAnEmptyPlaceholder()
        {
            CreateFile("2019/a.jpg");
            CreateFile("2019/sub/b.jpg");
            CreateFile("2020/c.jpg");

            var result = await RemoveAsync("2019");

            Assert.True(result.Succeeded, result.Error);
            Assert.True(Directory.Exists(Path.Combine(root, "2019")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "2019")));
            Assert.Equal(2, recycleBin.Moved.Count);
            Assert.True(File.Exists(Path.Combine(root, "2020", "c.jpg")));
            Assert.Equal(["/2019"], syncthing.Lines);
            Assert.Equal(1, syncthing.SetIgnoresCount);
        }

        [Fact]
        public async Task RemovesSeveralDirectoriesWithOneIgnoreChange()
        {
            CreateFile("2019/a.jpg");
            CreateFile("2020/b.jpg");

            var results = await service.RemoveFromDeviceAsync([Dir("2019"), Dir("2020")], CancellationToken.None);

            Assert.All(results, x => Assert.True(x.Succeeded, x.Error));
            Assert.Equal(1, syncthing.SetIgnoresCount);
            Assert.Equal(2, syncthing.Lines.Count);
        }

        [Fact]
        public async Task RefusesIfAFileIsAlreadyIgnored()
        {
            // Only this device has ignored files, so they must not be deleted
            CreateFile("2019/a.jpg");
            File.WriteAllText(Path.Combine(root, "2019", "edit.psd"), "local only");
            syncthing.Lines = ["*.psd"];

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Contains("edit.psd", result.Error);
            AssertNothingChanged(["*.psd"]);
        }

        [Fact]
        public async Task DeletesDeletableIgnoredFiles()
        {
            CreateFile("2019/a.jpg");
            File.WriteAllText(Path.Combine(root, "2019", "Thumbs.db"), "cache");
            syncthing.Lines = ["(?d)Thumbs.db"];

            var result = await RemoveAsync("2019");

            Assert.True(result.Succeeded, result.Error);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "2019")));
        }

        [Fact]
        public async Task RefusesIfNoOtherDeviceHasAValidCopy()
        {
            CreateFile("2019/a.jpg");
            syncthing.Files["2019/a.jpg"].Availability = [];

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged([]);
        }

        [Fact]
        public async Task RefusesIfTheLocalVersionIsNotTheNewest()
        {
            // E.g. a conflicting local change, or a receive-only folder with local changes
            CreateFile("2019/a.jpg");
            syncthing.Files["2019/a.jpg"].Local.Version = ["LAPTOP:2"];

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged([]);
        }

        [Fact]
        public async Task RefusesIfTheIndexDoesNotMatchTheDisk()
        {
            // Changed after Syncthing's last scan
            var path = CreateFile("2019/a.jpg");
            File.AppendAllText(path, "more");

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged([]);
        }

        [Fact]
        public async Task RefusesIfAnotherSyncthingFolderIsInside()
        {
            CreateFile("2019/a.jpg");
            status.Roots = [root, Path.Combine(root, "2019", "nested")];

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged([]);
        }

        [Fact]
        public async Task RefusesIfTheScanFails()
        {
            CreateFile("2019/a.jpg");
            syncthing.ScanFails = true;

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged([]);
        }

        [Fact]
        public async Task RefusesWithSyncthingVersionsWhichCannotTellValidCopies()
        {
            CreateFile("2019/a.jpg");
            ((SyncthingCapabilities)syncthing.Manager.Capabilities).SyncthingVersion = new Version(2, 1, 7);

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged([]);
        }

        [Fact]
        public async Task CanSkipTheSyncthingVersionCheck()
        {
            CreateFile("2019/a.jpg");
            ((SyncthingCapabilities)syncthing.Manager.Capabilities).SyncthingVersion = new Version(2, 1, 7);
            service.SkipSyncthingVersionCheck = true;

            var result = await RemoveAsync("2019");

            Assert.True(result.Succeeded, result.Error);
            Assert.Single(recycleBin.Moved);
        }

        [Fact]
        public async Task RollsBackIfSyncthingDoesNotConfirm()
        {
            CreateFile("2019/a.jpg");
            syncthing.ConfirmIgnores = false;

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Empty(syncthing.Lines);
            Assert.Empty(recycleBin.Moved);
            Assert.True(File.Exists(Path.Combine(root, "2019", "a.jpg")));
        }

        [Fact]
        public async Task RollsBackIfAFileAppearsMeanwhile()
        {
            CreateFile("2019/a.jpg");
            syncthing.OnConfirm = () => File.WriteAllText(Path.Combine(root, "2019", "new.txt"), "just saved");

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Empty(syncthing.Lines);
            Assert.Empty(recycleBin.Moved);
            Assert.True(File.Exists(Path.Combine(root, "2019", "new.txt")));
        }

        [Fact]
        public async Task RollsBackIfAFileChangesMeanwhile()
        {
            var path = CreateFile("2019/a.jpg");
            syncthing.OnConfirm = () => File.AppendAllText(path, " edited");

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Empty(syncthing.Lines);
            Assert.Empty(recycleBin.Moved);
        }

        [Fact]
        public async Task RollsBackIfTheRecycleBinFails()
        {
            CreateFile("2019/a.jpg");
            recycleBin.Fails = true;

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Empty(syncthing.Lines);
        }

        [Fact]
        public async Task RemovalTakesPrecedenceOverExceptionsInside()
        {
            // The new line goes to the top, so it wins over the user's exception until the directory is downloaded again
            CreateFile("2019/a.jpg");
            CreateFile("2019/keep/b.jpg");
            syncthing.Lines = ["!/2019/keep"];

            var result = await RemoveAsync("2019");

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(["/2019", "!/2019/keep"], syncthing.Lines);

            await service.DownloadToDeviceAsync([Dir("2019")], CancellationToken.None);
            Assert.Equal(["!/2019/keep"], syncthing.Lines);
        }

        [Fact]
        public async Task DownloadRemovesTheIgnoreLine()
        {
            syncthing.Lines = ["/2019", "/2020"];

            await service.DownloadToDeviceAsync([Dir("2019")], CancellationToken.None);

            Assert.Equal(["/2020"], syncthing.Lines);
        }

        [Fact]
        public async Task RefusesIfAnotherSyncthingFolderEncloses()
        {
            // That folder isn't affected by this folder's ignore patterns, so it would sync the deletion
            CreateFile("2019/a.jpg");
            status.Roots = [Path.GetDirectoryName(root), root];

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged([]);
        }

        [Fact]
        public async Task RollbackRestoresAWhitelistExactly()
        {
            CreateFile("2019/a.jpg");
            syncthing.Lines = ["!/2019", "*"];
            syncthing.OnConfirm = () => File.WriteAllText(Path.Combine(root, "2019", "new.txt"), "just saved");

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Equal(["!/2019", "*"], syncthing.Lines);
            Assert.Empty(journal.Pending);
        }

        [Fact]
        public async Task RollsBackIfWritingTheIgnorePatternsFails()
        {
            // E.g. a timeout after Syncthing has saved them
            CreateFile("2019/a.jpg");
            syncthing.SetIgnoresFailures = 1;

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Empty(syncthing.Lines);
            Assert.Empty(recycleBin.Moved);
            Assert.Empty(journal.Pending);
        }

        [Fact]
        public async Task KeepsAFailedRollbackInTheJournalAndRetriesItOnTheNextStart()
        {
            CreateFile("2019/a.jpg");
            syncthing.OnConfirm = () => File.WriteAllText(Path.Combine(root, "2019", "new.txt"), "just saved");
            syncthing.SetIgnoresFailures = 2; // the removal's write is saved but fails; the rollback's isn't saved at all
            syncthing.SavingFailures = 1;

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Contains("Download to this device", result.Error);
            Assert.Equal(["/2019"], syncthing.Lines);
            Assert.Single(journal.Pending);

            // Syncthing (or SyncTrayzor) restarts
            await syncthing.RaiseDataLoadedAsync();

            Assert.Empty(syncthing.Lines);
            Assert.Empty(journal.Pending);
        }

        [Fact]
        public async Task RollsBackIfTheOtherCopyDisappearsBeforeDeleting()
        {
            // E.g. the other device removed the same subfolder at the same time
            CreateFile("2019/a.jpg");
            syncthing.OnConfirm = () => syncthing.Files["2019/a.jpg"].Availability = [];

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            Assert.Empty(syncthing.Lines);
            Assert.Empty(recycleBin.Moved);
        }

        [Fact]
        public async Task RefusesIfSyncthingCannotReadTheIgnorePatterns()
        {
            // Writing anything back would replace the whole .stignore
            CreateFile("2019/a.jpg");
            syncthing.IgnoresUnreadable = true;

            await Assert.ThrowsAsync<InvalidOperationException>(() => RemoveAsync("2019"));
            Assert.Equal(0, syncthing.SetIgnoresCount);
            Assert.Empty(recycleBin.Moved);
        }

        [Fact]
        public async Task RefusesIfItContainsAnIncludedIgnoreFile()
        {
            CreateFile("2019/a.jpg");
            CreateFile("2019/.stglobalignore", "*.tmp");
            syncthing.Lines = ["#include 2019/.stglobalignore"];

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged(["#include 2019/.stglobalignore"]);
        }

        [Fact]
        public async Task RefusesIfOnlyAnUntrustedDeviceHasACopy()
        {
            // An encrypted copy can't be used without the folder password
            CreateFile("2019/a.jpg");
            syncthing.UntrustedOtherDevice = true;

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged([]);
        }

        [Fact]
        public async Task RefusesIfTheIndexDoesNotAgreeThatADeletableFileIsIgnored()
        {
            CreateFile("2019/a.jpg");
            CreateFile("2019/Thumbs.db", "cache"); // in the index as a normal file
            syncthing.Lines = ["(?d)Thumbs.db"];

            var result = await RemoveAsync("2019");

            Assert.False(result.Succeeded);
            AssertNothingChanged(["(?d)Thumbs.db"]);
        }

        [Fact]
        public async Task RemovesReadOnlyEmptyDirectories()
        {
            CreateFile("2019/a.jpg");
            var empty = Directory.CreateDirectory(Path.Combine(root, "2019", "customized"));
            empty.Attributes |= FileAttributes.ReadOnly;

            var result = await RemoveAsync("2019");

            Assert.True(result.Succeeded, result.Error);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "2019")));
        }

        private void AssertNothingChanged(List<string> expectedLines)
        {
            Assert.Equal(expectedLines, syncthing.Lines);
            Assert.Equal(0, syncthing.SetIgnoresCount);
            Assert.Empty(recycleBin.Moved);
        }

        // ── Fakes ─────────────────────────────────────────────────────────────

        /// <summary>Syncthing as far as SelectiveSyncService uses it. Other members throw.</summary>
        public class FakeSyncthing : DispatchProxy
        {
            public ISyncthingManager Manager { get; private set; }
            public List<string> Lines { get; set; } = [];
            public int SetIgnoresCount { get; private set; }
            public Dictionary<string, DbFile> Files { get; } = new(StringComparer.Ordinal);
            public bool ScanFails { get; set; }
            public bool ConfirmIgnores { get; set; } = true;
            public int SetIgnoresFailures { get; set; }
            // How many of the failing writes still save the lines first (as when the response times out)
            public int SavingFailures { get; set; } = int.MaxValue;
            public bool IgnoresUnreadable { get; set; }
            public bool UntrustedOtherDevice
            {
                set => folders = new FakeFolders(new Folder(FolderId, "Photos", folders.FetchAll().Single().Path, true, FolderSyncState.Idle, null, false,
                    [LocalDevice, OtherDevice], "sendreceive", value ? [OtherDevice] : []));
            }
            private EventHandler dataLoaded;

            public Task RaiseDataLoadedAsync()
            {
                dataLoaded?.Invoke(this, EventArgs.Empty);
                // Recovery runs in the background
                return Task.Delay(500);
            }
            public Action OnConfirm { get; set; }

            private readonly SyncthingCapabilities capabilities = new() { SyncthingVersion = new Version(2, 1, 8) };
            private FakeFolders folders;

            public static FakeSyncthing Create(string root)
            {
                var manager = Create<ISyncthingManager, FakeSyncthing>();
                var fake = (FakeSyncthing)(object)manager;
                fake.Manager = manager;
                fake.folders = new FakeFolders(new Folder(FolderId, "Photos", root, true, FolderSyncState.Idle, null, false, [LocalDevice, OtherDevice], "sendreceive"));
                return fake;
            }

            public void MarkSynced(string relativePath, string path)
            {
                var info = new FileInfo(path);
                DbFileInfo Entry() => new() { Name = relativePath, Size = info.Length, Modified = info.LastWriteTimeUtc, Version = ["SERVER:1"] };
                Files[relativePath] = new DbFile()
                {
                    Local = Entry(),
                    Global = Entry(),
                    Availability = [new DbFileAvailability() { DeviceId = OtherDevice }],
                };
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                switch (method.Name)
                {
                    case "get_Capabilities": return capabilities;
                    case "get_Folders": return folders;
                    case "get_LocalDeviceId": return LocalDevice;
                    case "add_DataLoaded":
                        dataLoaded += (EventHandler)args[0];
                        return null;
                    case "FetchIgnoresAsync":
                        if (IgnoresUnreadable)
                            return Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("Syncthing can't read the ignore patterns"));
                        return Task.FromResult<IReadOnlyList<string>>(Lines.ToList());
                    case "SetIgnoresAsync" when SetIgnoresFailures > 0:
                        SetIgnoresFailures--;
                        if (SavingFailures-- > 0)
                            Lines = ((IEnumerable<string>)args[1]).ToList();
                        return Task.FromException(new TaskCanceledException("timeout"));
                    case "SetIgnoresAsync":
                        SetIgnoresCount++;
                        Lines = ((IEnumerable<string>)args[1]).ToList();
                        return Task.CompletedTask;
                    case "ScanOrThrowAsync":
                        return ScanFails ? Task.FromException(new TaskCanceledException("timeout")) : Task.CompletedTask;
                    case "ScanAsync": return Task.CompletedTask;
                    case "FetchFileAsync":
                        return Task.FromResult(Files.TryGetValue((string)args[1], out var file) ? file : null);
                    case "IsIgnoredInIndexAsync":
                        OnConfirm?.Invoke();
                        OnConfirm = null;
                        var ignored = ConfirmIgnores && new IgnoreMatcher(Expand(Lines)).IsIgnored((string)args[1]);
                        return Task.FromResult<bool?>(ignored);
                    default:
                        throw new NotImplementedException(method.Name);
                }
            }
        }

        private sealed class FakeFolders(Folder folder) : ISyncthingFolderManager
        {
            public bool TryFetchById(string folderId, out Folder result)
            {
                result = folderId == folder.FolderId ? folder : null;
                return result != null;
            }

            public IReadOnlyCollection<Folder> FetchAll() => [folder];

            public event EventHandler FoldersChanged { add { } remove { } }
            public event EventHandler<FolderSyncStateChangedEventArgs> SyncStateChanged { add { } remove { } }
            public event EventHandler<FolderStatusChangedEventArgs> StatusChanged { add { } remove { } }
            public event EventHandler<FolderErrorsChangedEventArgs> FolderErrorsChanged { add { } remove { } }
        }

        private sealed class FakeStatus(FakeSyncthing syncthing, string root) : IExplorerStatusService
        {
            public bool IsEnabled { get; set; } = true;
            public IReadOnlyList<string> Roots { get; set; } = [root];
            public bool TryResolve(string path, out FolderPath folderPath) => throw new NotImplementedException();
            public bool IsFolderPaused(string folderId) => false;
            public ExplorerItemState GetState(string path) => ExplorerItemState.None;
            public bool? IsIgnored(FolderPath path) => new IgnoreMatcher(Expand(syncthing.Lines)).IsIgnored(path.RelativePath);
            public Task<IgnoreMatcher> RefreshIgnoresAsync(string folderId, CancellationToken cancellationToken) =>
                Task.FromResult(new IgnoreMatcher(Expand(syncthing.Lines)));
            public void Dispose() { }
        }

        private sealed class FakeRecycleBin : IRecycleBin
        {
            public List<string> Moved { get; } = [];
            public bool Fails { get; set; }
            public bool IsAvailableFor(string path) => true;

            public IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths)
            {
                if (Fails)
                    return paths;
                foreach (var path in paths)
                {
                    File.Delete(path);
                    Moved.Add(path);
                }
                return [];
            }
        }

        private sealed class FakeJournal : IRemovalJournal
        {
            private readonly List<PendingRemoval> pending = [];
            public IReadOnlyList<PendingRemoval> Pending => pending.ToList();
            public void Add(IEnumerable<PendingRemoval> removals) => pending.AddRange(removals);
            public void Remove(IEnumerable<PendingRemoval> removals)
            {
                foreach (var removal in removals.ToList())
                    pending.Remove(removal);
            }
        }

        private sealed class NullShellNotifier : IShellChangeNotifier
        {
            public void ItemsChanged(IEnumerable<string> paths) { }
            public void DirectoryChanged(string path) { }
            public void Dispose() { }
        }

        /// <summary>Expands simple .stignore lines the way Syncthing does on Windows (see lib/ignore/ignore.go)</summary>
        private static List<string> Expand(IEnumerable<string> lines)
        {
            var result = new List<string>();
            foreach (var line in lines)
            {
                var pattern = line;
                var prefix = "(?i)";
                if (pattern.StartsWith("(?d)"))
                {
                    prefix = "(?d)" + prefix;
                    pattern = pattern.Substring(4);
                }
                if (pattern.StartsWith('!'))
                {
                    prefix += "!";
                    pattern = pattern.Substring(1);
                }
                pattern = pattern.ToLowerInvariant();
                foreach (var variant in new[] { pattern, pattern + "/**" })
                {
                    result.Add(prefix + variant);
                    if (!variant.StartsWith('/'))
                        result.Add(prefix + "**/" + variant);
                }
            }
            return result;
        }
    }
}
