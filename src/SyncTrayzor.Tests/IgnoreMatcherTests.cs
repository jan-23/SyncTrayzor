using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SyncTrayzor.Services.ExplorerIntegration;
using Xunit;

namespace SyncTrayzor.Tests
{
    public class IgnoreMatcherTests
    {
        // TestData/syncthing_ignore_vectors.json was generated with Syncthing's own lib/ignore on Windows: for each
        // .stignore, the expanded patterns returned by GET /rest/db/ignores, and whether Syncthing ignores each path
        // (including through ignored parent directories). The C# matcher must give the same answers.
        private class Vector
        {
            public string Stignore { get; set; }
            public List<string> Expanded { get; set; }
            public List<Result> Results { get; set; }
        }

        private class Result
        {
            public string Path { get; set; }
            public bool Ignored { get; set; }
            public bool Deletable { get; set; }
        }

        private static List<Vector> LoadVectors()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", "syncthing_ignore_vectors.json");
            return JsonConvert.DeserializeObject<List<Vector>>(File.ReadAllText(path));
        }

        public static IEnumerable<object[]> Cases() =>
            LoadVectors().SelectMany((v, i) => v.Results.Select(r => new object[] { i, r.Path }));

        [Theory]
        [MemberData(nameof(Cases))]
        public void MatchesSyncthing(int vectorIndex, string path)
        {
            var vector = LoadVectors()[vectorIndex];
            var expected = vector.Results.Single(x => x.Path == path);
            var matcher = new IgnoreMatcher(vector.Expanded ?? []);

            var result = matcher.MatchPath(path);

            Assert.True(expected.Ignored == result.Ignored,
                $"{path} with .stignore {JsonConvert.ToString(vector.Stignore)}: expected ignored={expected.Ignored}");
            Assert.True(expected.Deletable == result.Deletable,
                $"{path} with .stignore {JsonConvert.ToString(vector.Stignore)}: expected deletable={expected.Deletable}");
        }

        [Fact]
        public void LiteralBlocksKeepFirstMatchOrder()
        {
            // An exception for a.txt, then literal ignores (merged into one block), then a wildcard
            var matcher = new IgnoreMatcher([
                "(?i)!/photos/2019/keep.jpg",
                "(?i)/photos/2019", "(?i)/photos/2019/**", "(?i)/photos/2020", "(?i)/photos/2020/**",
                "(?i)*.tmp", "(?i)**/*.tmp", // as Syncthing expands "*.tmp"
                "(?i)!/photos/2020/late.jpg", // too late: /photos/2020/** already matched
            ]);

            Assert.False(matcher.IsIgnored("Photos/2019/keep.jpg"));
            Assert.True(matcher.IsIgnored("Photos/2019/other.jpg"));
            Assert.True(matcher.IsIgnored("Photos/2020"));
            Assert.True(matcher.IsIgnored("Photos/2020/late.jpg"));
            Assert.False(matcher.IsIgnored("Photos/2021/a.jpg"));
            Assert.True(matcher.IsIgnored("Photos/2021/a.tmp"));
            // The exception before the block means the scanner can't skip the ignored directories
            Assert.False(matcher.Match("Photos/2020").CanSkipDir);
        }

        [Fact]
        public void ManyRemovedSubfoldersAreFast()
        {
            // As after removing 5000 subfolders from this device
            var patterns = Enumerable.Range(0, 5000).SelectMany(i => new[] { $"(?i)/archive/{i}", $"(?i)/archive/{i}/**" }).ToList();
            patterns.Add("(?i)*.tmp");
            var matcher = new IgnoreMatcher(patterns);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20000; i++)
            {
                Assert.Equal(i % 2 == 0, matcher.IsIgnored(i % 2 == 0 ? $"Archive/{i % 5000}/photo{i}.jpg" : $"Current/{i}/photo.jpg"));
            }
            // Each lookup is a few hash lookups; with one regex per pattern this took several seconds
            Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds}ms");
        }

        [Theory]
        [InlineData("*.txt", "a.txt", true)]
        [InlineData("*.txt", "a/b.txt", false)]
        [InlineData("**.txt", "a/b.txt", true)]
        [InlineData("a?c", "abc", true)]
        [InlineData("a?c", "a/c", false)]
        [InlineData("[a-c]x", "bx", true)]
        [InlineData("[!a-c]x", "dx", true)]
        [InlineData("[!a-c]x", "/x", false)]
        [InlineData("{a,b*}.md", "bxx.md", true)]
        [InlineData(@"a\*b", "a*b", true)]
        [InlineData(@"a\*b", "axb", false)]
        [InlineData("a.b", "axb", false)]
        [InlineData("a/**/z", "a/z", true)]
        [InlineData("a/**/z", "a/b/c/z", true)]
        [InlineData("a/**/z", "az", false)]
        public void GlobToRegex(string glob, string path, bool matches)
        {
            Assert.Equal(matches, System.Text.RegularExpressions.Regex.IsMatch(path, IgnoreMatcher.GlobToRegex(glob)));
        }
    }
}
