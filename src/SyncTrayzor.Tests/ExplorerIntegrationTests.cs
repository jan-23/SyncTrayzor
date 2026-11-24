using System.Collections.Generic;
using SyncTrayzor.Services.ExplorerIntegration;
using Xunit;

namespace SyncTrayzor.Tests
{
    public class IgnorePatternEditorTests
    {
        [Theory]
        [InlineData("build", "/build")]
        [InlineData("a/b c/d.txt", "/a/b c/d.txt")]
        [InlineData(@"a\b", "/a/b")]
        [InlineData("foo[1]", "/foo|[1|]")]
        [InlineData("{x},y", "/|{x|}|,y")]
        public void PatternFor_AnchorsAndEscapes(string relativePath, string expected)
        {
            Assert.Equal(expected, IgnorePatternEditor.PatternFor(relativePath));
        }

        [Fact]
        public void PatternFor_UsesCustomEscapeChar()
        {
            Assert.Equal(@"/a\[b\]", IgnorePatternEditor.PatternFor("a[b]", '\\'));
        }

        [Fact]
        public void Ignore_InsertsAtTop()
        {
            var lines = new List<string> { "*.tmp", "!/keep", "*" };
            var result = IgnorePatternEditor.Ignore(lines, ["docs/old"]);
            Assert.Equal(new[] { "/docs/old", "*.tmp", "!/keep", "*" }, result);
        }

        [Fact]
        public void Ignore_InsertsAfterEscapeLine()
        {
            var lines = new List<string> { "#escape=\\", "*.tmp" };
            var result = IgnorePatternEditor.Ignore(lines, ["a[1]"]);
            Assert.Equal(new[] { "#escape=\\", @"/a\[1\]", "*.tmp" }, result);
        }

        [Fact]
        public void Ignore_KeepsExceptionsAndMovesDuplicatesToTheTop()
        {
            var lines = new List<string> { "!/Docs", "/docs", "*" };
            var result = IgnorePatternEditor.Ignore(lines, ["docs"]);
            Assert.Equal(new[] { "/docs", "!/Docs", "*" }, result);
        }

        [Theory]
        [InlineData(new[] { "!/Photos", "*" }, "Photos")]             // a whitelist
        [InlineData(new[] { "*.tmp", "#include .stglobalignore" }, "a/b")]
        [InlineData(new[] { "#escape=\\", "*.tmp" }, "x[1]")]
        [InlineData(new string[0], "a")]
        public void RemoveIgnoresUndoesIgnore(string[] lines, string path)
        {
            var ignored = IgnorePatternEditor.Ignore(lines, [path]);
            Assert.Equal(lines, IgnorePatternEditor.RemoveIgnores(ignored, [path]));
        }

        [Fact]
        public void Ignore_KeepsIncludes()
        {
            var lines = new List<string> { "#include .stglobalignore" };
            var result = IgnorePatternEditor.Ignore(lines, ["x"]);
            Assert.Equal(new[] { "/x", "#include .stglobalignore" }, result);
        }

        [Fact]
        public void RemoveIgnores_RemovesExactLine()
        {
            var lines = new List<string> { "/build/", "*.tmp" };
            var result = IgnorePatternEditor.RemoveIgnores(lines, ["build"]);
            Assert.Equal(new[] { "*.tmp" }, result);
        }

        [Fact]
        public void RemoveIgnores_LeavesTheUsersOwnPatternsAlone()
        {
            var lines = new List<string> { "*.log", "!/keep" };
            var result = IgnorePatternEditor.RemoveIgnores(lines, ["important.log", "keep"]);
            Assert.Equal(lines, result);
        }

        [Fact]
        public void FindEscapeChar_DefaultsToPipe()
        {
            Assert.Equal('|', IgnorePatternEditor.FindEscapeChar(["*.tmp"]));
            Assert.Equal('\\', IgnorePatternEditor.FindEscapeChar(["#escape = \\", "*.tmp"]));
        }
    }

    public class FolderPathResolverTests
    {
        private readonly FolderPathResolver resolver = new([
            ("photos", @"D:\Sync\Photos"),
            ("raw", @"D:\Sync\Photos\Raw\"),
            ("drive", @"E:\"),
        ]);

        [Theory]
        [InlineData(@"D:\Sync\Photos", "photos", "")]
        [InlineData(@"d:\sync\photos\2024\a.jpg", "photos", "2024/a.jpg")]
        [InlineData(@"D:\Sync\Photos\Raw\x.cr2", "raw", "x.cr2")]
        [InlineData(@"D:\Sync\Photos\Raw", "raw", "")]
        [InlineData(@"E:\dir\file", "drive", "dir/file")]
        public void Resolves(string path, string folderId, string relative)
        {
            Assert.True(resolver.TryResolve(path, out var result));
            Assert.Equal(folderId, result.FolderId);
            Assert.Equal(relative, result.RelativePath);
        }

        [Fact]
        public void DriveRootKeepsItsSeparator()
        {
            // "E:" would mean the current directory on drive E:
            Assert.True(resolver.TryResolve(@"E:\dir", out var result));
            Assert.Equal(@"E:\", result.Root);
            Assert.True(resolver.TryResolve(@"E:\", out result));
            Assert.True(result.IsRoot);
            Assert.Equal(@"E:\", result.Root);
            Assert.Contains(@"E:\", resolver.Roots);
        }

        [Theory]
        [InlineData(@"E:\", @"E:\")]
        [InlineData(@"E:", @"E:\")]
        [InlineData(@"D:\Sync\", @"D:\Sync")]
        [InlineData(@"D:/Sync/Photos", @"D:\Sync\Photos")]
        [InlineData(@"\\server\share\", @"\\server\share")]
        public void NormalizeRoot(string root, string expected)
        {
            Assert.Equal(expected, FolderPathResolver.NormalizeRoot(root));
        }

        [Theory]
        [InlineData(@"D:\Sync\PhotosBackup\a.jpg")]
        [InlineData(@"D:\Sync")]
        [InlineData(@"C:\Windows")]
        [InlineData("")]
        public void DoesNotResolveOutsidePaths(string path)
        {
            Assert.False(resolver.TryResolve(path, out _));
        }
    }

    public class PathSetTests
    {
        [Fact]
        public void TracksDescendants()
        {
            var set = new PathSet();
            set.Add("a/b/c.txt");
            set.Add(@"a\d.txt");

            Assert.True(set.ContainsOrHasDescendant(""));
            Assert.True(set.ContainsOrHasDescendant("A"));
            Assert.True(set.ContainsOrHasDescendant("a/b"));
            Assert.True(set.ContainsOrHasDescendant("a/b/c.txt"));
            Assert.False(set.ContainsOrHasDescendant("a/b/c"));
            Assert.False(set.ContainsOrHasDescendant("b"));

            set.Remove("a/b/c.txt");
            Assert.False(set.ContainsOrHasDescendant("a/b"));
            Assert.True(set.ContainsOrHasDescendant("a"));

            set.Remove("a/d.txt");
            Assert.False(set.ContainsOrHasDescendant(""));
            Assert.Equal(0, set.Count);
        }

        [Fact]
        public void IgnoresDuplicates()
        {
            var set = new PathSet();
            Assert.True(set.Add("a/b"));
            Assert.False(set.Add("A/B"));
            Assert.True(set.Remove("a/b"));
            Assert.False(set.ContainsOrHasDescendant("a"));
        }
    }

    public class ExplorerStateCalculatorTests
    {
        [Fact]
        public void UnknownIgnoreStateShowsNothing()
        {
            Assert.Equal(ExplorerItemState.None, ExplorerStateCalculator.Calculate(new FolderSyncInfo(), "a", null));
        }

        [Fact]
        public void UnknownIgnoreStateStillShowsWhatTheIndexKnows()
        {
            var info = new FolderSyncInfo();
            info.Needed.Add("a/b.txt");
            info.Errors.Add("c.txt");
            info.LocalChanges.Add("d.txt");
            Assert.Equal(ExplorerItemState.Syncing, ExplorerStateCalculator.Calculate(info, "a", null));
            Assert.Equal(ExplorerItemState.Error, ExplorerStateCalculator.Calculate(info, "c.txt", null));
            // A local change might be to an ignored file, so it's not shown until we know
            Assert.Equal(ExplorerItemState.None, ExplorerStateCalculator.Calculate(info, "d.txt", null));
        }

        [Fact]
        public void PausedFolderShowsNothing()
        {
            var info = new FolderSyncInfo() { IsPaused = true };
            Assert.Equal(ExplorerItemState.None, ExplorerStateCalculator.Calculate(info, "", false));
        }

        [Fact]
        public void IgnoredWinsOverEverythingElse()
        {
            var info = new FolderSyncInfo() { HasFolderError = true };
            info.Needed.Add("a");
            Assert.Equal(ExplorerItemState.Ignored, ExplorerStateCalculator.Calculate(info, "a", true));
        }

        [Fact]
        public void ErrorsPropagateToParents()
        {
            var info = new FolderSyncInfo();
            info.Errors.Add("a/b.txt");
            info.Needed.Add("a/c.txt");
            Assert.Equal(ExplorerItemState.Error, ExplorerStateCalculator.Calculate(info, "a", false));
            Assert.Equal(ExplorerItemState.Error, ExplorerStateCalculator.Calculate(info, "", false));
            Assert.Equal(ExplorerItemState.Syncing, ExplorerStateCalculator.Calculate(info, "a/c.txt", false));
        }

        [Fact]
        public void NeededAndLocalChangesAreSyncing()
        {
            var info = new FolderSyncInfo();
            info.Needed.Add("a/b.txt");
            info.LocalChanges.Add("x/y.txt");
            Assert.Equal(ExplorerItemState.Syncing, ExplorerStateCalculator.Calculate(info, "a", false));
            Assert.Equal(ExplorerItemState.Syncing, ExplorerStateCalculator.Calculate(info, "x", false));
            Assert.Equal(ExplorerItemState.UpToDate, ExplorerStateCalculator.Calculate(info, "z", false));
        }

        [Fact]
        public void OverflowMeansEverythingIsSyncing()
        {
            var info = new FolderSyncInfo() { NeededOverflow = true };
            Assert.Equal(ExplorerItemState.Syncing, ExplorerStateCalculator.Calculate(info, "anything", false));
        }
    }
}
