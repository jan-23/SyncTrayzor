using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    /// <summary>
    /// Matches folder-relative paths against a folder's ignore patterns, the way Syncthing does.
    /// </summary>
    /// <remarks>
    /// This works on the "expanded" patterns returned by Syncthing's GET /rest/db/ignores: Syncthing has already
    /// parsed .stignore, resolved #include files and expanded each line into the globs it actually matches
    /// (for example "foo" into "foo", "foo/**", "**/foo" and "**/foo/**"). What's left is matching those globs,
    /// which mirrors lib/ignore/ignore.go (Match, MatchPath and allowsSkippingIgnoredDirs).
    ///
    /// Folders with many subfolders removed from this device have many patterns like "/a/b" and "/a/b/**".
    /// Consecutive patterns without wildcards (and with the same flags) are therefore checked with hash lookups
    /// instead of one regex each, and results are cached, as Explorer asks about the same items over and over.
    /// </remarks>
    public class IgnoreMatcher
    {
        public static readonly IgnoreMatcher Empty = new([]);

        private const int maxCachedResults = 100_000;

        private readonly List<Step> steps = [];
        private readonly ConcurrentDictionary<string, MatchResult> cache = new(StringComparer.Ordinal);

        public IgnoreMatcher(IEnumerable<string> expandedPatterns)
        {
            foreach (var expanded in expandedPatterns)
            {
                var pattern = Pattern.TryParse(expanded);
                if (pattern == null)
                    continue;

                // Merge literal patterns into the previous step if it's a literal block with the same flags: as they
                // can't match the same path differently, checking them together gives the same first match
                var last = steps.Count > 0 ? steps[^1] : null;
                if (pattern.Literal != null && last?.IsLiteralBlock == true && last.HasSameFlags(pattern))
                    last.Add(pattern);
                else
                    steps.Add(Step.From(pattern));
            }
        }

        /// <summary>True if the path, or one of its parent directories, is ignored</summary>
        public bool IsIgnored(string relativePath) => MatchPath(relativePath).Ignored;

        /// <summary>
        /// The effective result for a path, taking ignored parent directories into account like Syncthing's scanner:
        /// if a parent directory is ignored and may be skipped, everything below it is ignored too.
        /// </summary>
        public MatchResult MatchPath(string relativePath)
        {
            relativePath = relativePath.Replace('\\', '/').Trim('/');
            if (cache.TryGetValue(relativePath, out var cached))
                return cached;

            var result = MatchPathUncached(relativePath);
            if (cache.Count >= maxCachedResults)
                cache.Clear();
            cache[relativePath] = result;
            return result;
        }

        private MatchResult MatchPathUncached(string relativePath)
        {
            for (int i = relativePath.IndexOf('/'); i >= 0; i = relativePath.IndexOf('/', i + 1))
            {
                var result = Match(relativePath.Substring(0, i));
                if (result.Ignored && result.CanSkipDir)
                    return result;
            }
            return Match(relativePath);
        }

        /// <summary>Matches only the path itself, like Syncthing's Matcher.Match</summary>
        public MatchResult Match(string relativePath)
        {
            relativePath = relativePath.Replace('\\', '/').Trim('/');
            if (relativePath.Length == 0)
                return MatchResult.NotIgnored;
            if (IsTemporary(relativePath) || IsInternal(relativePath))
                return new MatchResult(true, false, true);

            string lowerPath = null;
            bool canSkipDir = true;
            foreach (var step in steps)
            {
                if (canSkipDir && !step.AllowsSkippingIgnoredDirs)
                    canSkipDir = false;

                var path = relativePath;
                if (step.CaseFolded)
                    path = lowerPath ??= relativePath.ToLowerInvariant();

                if (step.Matches(path))
                    return new MatchResult(step.Ignored, step.Ignored && step.Deletable, step.Ignored && canSkipDir);
            }
            return MatchResult.NotIgnored;
        }

        // lib/fs: Syncthing's own files are always ignored (compared case-sensitively, like Syncthing does)
        private static readonly string[] internals = [".stfolder", ".stignore", ".stversions"];

        public static bool IsInternal(string path)
        {
            foreach (var internalName in internals)
            {
                if (path.Equals(internalName, StringComparison.Ordinal) ||
                    path.StartsWith(internalName + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Syncthing's temporary files, e.g. of downloads in progress (lib/fs IsTemporary)</summary>
        public static bool IsTemporary(string path)
        {
            var name = path.Substring(path.LastIndexOf('/') + 1);
            return name.StartsWith("~syncthing~", StringComparison.Ordinal) ||
                name.StartsWith(".syncthing.", StringComparison.Ordinal);
        }

        public readonly record struct MatchResult(bool Ignored, bool Deletable, bool CanSkipDir)
        {
            public static readonly MatchResult NotIgnored = new(false, false, false);
        }

        /// <summary>One glob pattern, or a block of consecutive literal patterns with the same flags</summary>
        private sealed class Step
        {
            private Regex regex;
            private HashSet<string> exact;     // literal patterns matching exactly this path
            private HashSet<string> prefixes;  // literal patterns "dir/**", stored as "dir"

            public bool IsLiteralBlock => regex == null;
            public bool Ignored { get; private init; }
            public bool CaseFolded { get; private init; }
            public bool Deletable { get; private init; }
            public bool AllowsSkippingIgnoredDirs { get; private init; }

            public static Step From(Pattern pattern)
            {
                var step = new Step()
                {
                    Ignored = pattern.Ignored,
                    CaseFolded = pattern.CaseFolded,
                    Deletable = pattern.Deletable,
                    AllowsSkippingIgnoredDirs = pattern.AllowsSkippingIgnoredDirs,
                };
                if (pattern.Literal == null)
                {
                    step.regex = pattern.Regex;
                }
                else
                {
                    step.exact = new HashSet<string>(StringComparer.Ordinal);
                    step.prefixes = new HashSet<string>(StringComparer.Ordinal);
                    step.Add(pattern);
                }
                return step;
            }

            public bool HasSameFlags(Pattern pattern) =>
                Ignored == pattern.Ignored && CaseFolded == pattern.CaseFolded &&
                Deletable == pattern.Deletable && AllowsSkippingIgnoredDirs == pattern.AllowsSkippingIgnoredDirs;

            public void Add(Pattern pattern)
            {
                if (pattern.IsPrefix)
                    prefixes.Add(pattern.Literal);
                else
                    exact.Add(pattern.Literal);
            }

            public bool Matches(string path)
            {
                if (regex != null)
                    return regex.IsMatch(path);
                if (exact.Contains(path))
                    return true;
                if (prefixes.Count > 0)
                {
                    for (int i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
                    {
                        if (prefixes.Contains(path.Substring(0, i)))
                            return true;
                    }
                }
                return false;
            }
        }

        private sealed class Pattern
        {
            public Regex Regex { get; private init; }
            /// <summary>If the glob has no wildcards: the path it matches (or, if <see cref="IsPrefix"/>, everything below)</summary>
            public string Literal { get; private init; }
            public bool IsPrefix { get; private init; }
            public bool Ignored { get; private init; }
            public bool CaseFolded { get; private init; }
            public bool Deletable { get; private init; }
            public bool AllowsSkippingIgnoredDirs { get; private init; }

            private static readonly char[] globSpecialChars = ['*', '?', '[', ']', '{', '}', '\\'];

            public static Pattern TryParse(string expanded)
            {
                // Prefixes may come in any order, each at most once (as in Syncthing's parseLine)
                bool negated = false, caseFolded = false, deletable = false;
                var line = expanded;
                while (true)
                {
                    if (!negated && line.StartsWith('!'))
                    {
                        negated = true;
                        line = line.Substring(1);
                    }
                    else if (!caseFolded && line.StartsWith("(?i)", StringComparison.Ordinal))
                    {
                        caseFolded = true;
                        line = line.Substring(4);
                    }
                    else if (!deletable && line.StartsWith("(?d)", StringComparison.Ordinal))
                    {
                        deletable = true;
                        line = line.Substring(4);
                    }
                    else
                    {
                        break;
                    }
                }

                if (line.Length == 0)
                    return null;

                // A leading '/' anchors the pattern to the folder root; it isn't part of the glob
                var glob = line.StartsWith('/') ? line.Substring(1) : line;

                string literal = null;
                bool isPrefix = false;
                var body = glob.EndsWith("/**", StringComparison.Ordinal) ? glob.Substring(0, glob.Length - 3) : glob;
                if (body.Length > 0 && body.IndexOfAny(globSpecialChars) < 0)
                {
                    literal = body;
                    isPrefix = body.Length != glob.Length;
                }

                Regex regex = null;
                if (literal == null)
                {
                    try
                    {
                        regex = new Regex(GlobToRegex(glob), RegexOptions.CultureInvariant | RegexOptions.Singleline);
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }
                }

                return new Pattern()
                {
                    Regex = regex,
                    Literal = literal,
                    IsPrefix = isPrefix,
                    Ignored = !negated,
                    CaseFolded = caseFolded,
                    Deletable = deletable,
                    AllowsSkippingIgnoredDirs = !negated || IncludeAllowsSkipping(line),
                };
            }

            // An include ("!") pattern stops the scanner from skipping ignored directories, unless it can only
            // match something directly in the folder root
            private static bool IncludeAllowsSkipping(string pattern)
            {
                if (pattern.Length == 0 || pattern[0] != '/')
                    return false;
                if (pattern.EndsWith("/**", StringComparison.Ordinal))
                    pattern = pattern.Substring(0, pattern.Length - 3);
                if (pattern.Length == 0)
                    return true;
                if (pattern.Substring(1).Contains('/'))
                    return false;
                var withoutTrailing = pattern.EndsWith("**", StringComparison.Ordinal) ? pattern.Substring(0, pattern.Length - 2) : pattern;
                return !withoutTrailing.Contains("**", StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Converts a glob in the syntax Syncthing uses (github.com/gobwas/glob with '/' as separator) into a regex
        /// matching the whole path: '*' and '?' don't match '/', '**' matches anything, [...] and [!...] are
        /// character classes, {a,b} are alternatives and '\' escapes the next character.
        /// </summary>
        public static string GlobToRegex(string glob)
        {
            var sb = new StringBuilder("^");
            int index = 0;
            AppendGlob(glob, ref index, sb, insideAlternatives: false);
            sb.Append('$');
            return sb.ToString();
        }

        private static void AppendGlob(string glob, ref int i, StringBuilder sb, bool insideAlternatives)
        {
            while (i < glob.Length)
            {
                char c = glob[i];
                if (insideAlternatives && (c == ',' || c == '}'))
                    return;

                switch (c)
                {
                    case '\\':
                        i++;
                        if (i < glob.Length)
                            sb.Append(Regex.Escape(glob[i].ToString()));
                        i++;
                        break;

                    case '*':
                        if (i > 0 && glob[i - 1] == '/' && i + 2 < glob.Length && glob[i + 1] == '*' && glob[i + 2] == '/')
                        {
                            // "a/**/z" also matches "a/z" in gobwas/glob (and so in Syncthing)
                            sb.Append("(?:.*/)?");
                            i += 3;
                        }
                        else if (i + 1 < glob.Length && glob[i + 1] == '*')
                        {
                            sb.Append(".*");
                            i += 2;
                        }
                        else
                        {
                            sb.Append("[^/]*");
                            i++;
                        }
                        break;

                    case '?':
                        sb.Append("[^/]");
                        i++;
                        break;

                    case '[':
                        AppendCharacterClass(glob, ref i, sb);
                        break;

                    case '{':
                        i++;
                        sb.Append("(?:");
                        while (true)
                        {
                            AppendGlob(glob, ref i, sb, insideAlternatives: true);
                            if (i >= glob.Length)
                                throw new ArgumentException("Unterminated alternatives in glob");
                            if (glob[i] == '}')
                            {
                                i++;
                                break;
                            }
                            sb.Append('|');
                            i++; // ','
                        }
                        sb.Append(')');
                        break;

                    default:
                        sb.Append(Regex.Escape(c.ToString()));
                        i++;
                        break;
                }
            }
        }

        private static void AppendCharacterClass(string glob, ref int i, StringBuilder sb)
        {
            int start = i;
            i++; // '['
            bool negated = i < glob.Length && glob[i] == '!';
            if (negated)
                i++;

            var content = new StringBuilder();
            while (i < glob.Length && glob[i] != ']')
            {
                char c = glob[i];
                if (c == '\\' && i + 1 < glob.Length)
                {
                    i++;
                    c = glob[i];
                }
                // Escape everything regex-special inside a class, but keep ranges ("a-z") working
                if (c == '-' && content.Length > 0 && i + 1 < glob.Length && glob[i + 1] != ']')
                    content.Append('-');
                else
                    content.Append(c is '\\' or ']' or '[' or '^' or '-' ? "\\" + c : c.ToString());
                i++;
            }

            if (i >= glob.Length)
                throw new ArgumentException($"Unterminated character class in glob at {start}");

            i++; // ']'
            // Like gobwas/glob, a character class never matches the separator
            sb.Append(negated ? "[^/" : "[").Append(content).Append(']');
        }
    }
}
