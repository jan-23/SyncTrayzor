using System;
using SyncTrayzor.Services.ExplorerIntegration;
using SyncTrayzor.Syncthing.ApiClient;
using Xunit;

namespace SyncTrayzor.Tests
{
    public class RemovalSafetyTests
    {
        private static readonly DateTime modified = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        // A file this device has at the newest version, which the other device has a valid copy of
        private static DbFile SyncedFile() => new()
        {
            Local = new DbFileInfo() { Name = "a.jpg", Size = 100, Modified = modified, Version = ["SERVER:1"] },
            Global = new DbFileInfo() { Name = "a.jpg", Size = 100, Modified = modified, Version = ["SERVER:1"] },
            Availability = [new DbFileAvailability() { DeviceId = "SERVER" }],
        };

        private static FileCheckResult Check(DbFile file) => RemovalSafety.CheckFile(file, 100, modified, TimeSpan.Zero, []);

        [Fact]
        public void SyncedFileMayBeDeleted()
        {
            Assert.Equal(FileCheckResult.Ok, Check(SyncedFile()));
        }

        [Fact]
        public void ComparesModificationTimesExactlyByDefault()
        {
            // A same-size edit right after the scan must not pass for the scanned version
            Assert.Equal(FileCheckResult.NotScanned, RemovalSafety.CheckFile(SyncedFile(), 100, modified.AddMilliseconds(1), TimeSpan.Zero, []));
        }

        [Fact]
        public void UsesTheFoldersModificationTimeWindow()
        {
            Assert.Equal(FileCheckResult.Ok, RemovalSafety.CheckFile(SyncedFile(), 100, modified.AddSeconds(1), TimeSpan.FromSeconds(2), []));
        }

        [Fact]
        public void NotInTheIndex()
        {
            Assert.Equal(FileCheckResult.NotScanned, Check(null));
        }

        [Fact]
        public void ChangedOnDiskSinceTheLastScan()
        {
            Assert.Equal(FileCheckResult.NotScanned, RemovalSafety.CheckFile(SyncedFile(), 101, modified, TimeSpan.Zero, []));
            Assert.Equal(FileCheckResult.NotScanned, RemovalSafety.CheckFile(SyncedFile(), 100, modified.AddMinutes(1), TimeSpan.Zero, []));
        }

        [Fact]
        public void LocalVersionIsNotTheNewest()
        {
            // E.g. a local change which lost a conflict, or one in a receive-only folder
            var file = SyncedFile();
            file.Local.Version = ["LAPTOP:5"];
            Assert.Equal(FileCheckResult.LocalChanges, Check(file));
        }

        [Fact]
        public void LocalEntryHasFlags()
        {
            var file = SyncedFile();
            file.Local.LocalFlags = 8; // FlagLocalReceiveOnly
            Assert.Equal(FileCheckResult.LocalChanges, Check(file));
        }

        [Fact]
        public void NoDeviceHasAValidCopy()
        {
            // E.g. only this device has the file, the other device ignores it, or it isn't connected
            var file = SyncedFile();
            file.Availability = [];
            Assert.Equal(FileCheckResult.NoValidCopyElsewhere, Check(file));
        }

        [Fact]
        public void OnlyPartlyDownloadedElsewhere()
        {
            var file = SyncedFile();
            file.Availability = [new DbFileAvailability() { DeviceId = "SERVER", FromTemporary = true }];
            Assert.Equal(FileCheckResult.NoValidCopyElsewhere, Check(file));
        }

        [Fact]
        public void UntrustedDevicesDontCount()
        {
            // They only have an encrypted copy
            Assert.Equal(FileCheckResult.NoValidCopyElsewhere, RemovalSafety.CheckFile(SyncedFile(), 100, modified, TimeSpan.Zero, ["SERVER"]));
        }

        [Fact]
        public void StillAvailableRightBeforeDeleting()
        {
            var file = SyncedFile();
            file.Local.Ignored = true; // by then, this device ignores it
            Assert.True(RemovalSafety.StillAvailable(file, ["SERVER:1"], []));
        }

        [Fact]
        public void NoLongerAvailableRightBeforeDeleting()
        {
            var gone = SyncedFile();
            gone.Availability = [];
            Assert.False(RemovalSafety.StillAvailable(gone, ["SERVER:1"], []));

            var changed = SyncedFile();
            changed.Global.Version = ["SERVER:2"];
            Assert.False(RemovalSafety.StillAvailable(changed, ["SERVER:1"], []));
        }

        [Fact]
        public void DeletedEverywhereElse()
        {
            var file = SyncedFile();
            file.Global.Deleted = true;
            Assert.Equal(FileCheckResult.NoValidCopyElsewhere, Check(file));
        }

        [Fact]
        public void VersionsCompareAsSets()
        {
            Assert.True(RemovalSafety.SameVersion(["A:1", "B:2"], ["B:2", "A:1"]));
            Assert.False(RemovalSafety.SameVersion(["A:1"], ["A:1", "B:2"]));
            Assert.False(RemovalSafety.SameVersion([], []));
        }

        [Fact]
        public void FindsItemsKeptByAnException()
        {
            // As Syncthing expands "!/Photos/2019/keep" and "/Photos/2019"
            var matcher = new IgnoreMatcher(["(?i)!/photos/2019/keep", "(?i)!/photos/2019/keep/**", "(?i)/photos/2019", "(?i)/photos/2019/**"]);
            var local = new[] { "Photos/2019/a.jpg", "Photos/2019/keep", "Photos/2019/keep/b.jpg" };
            Assert.Equal(["Photos/2019/keep", "Photos/2019/keep/b.jpg"], RemovalSafety.NotIgnored(matcher, local));
        }
    }
}
