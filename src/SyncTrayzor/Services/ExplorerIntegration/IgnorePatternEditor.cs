using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    /// <summary>
    /// Edits the lines of a folder's .stignore file to (un)ignore individual paths.
    /// </summary>
    /// <remarks>
    /// Syncthing uses the first pattern that matches, so new lines are inserted at the top (after a possible #escape
    /// line, which must precede all patterns), where they win over what follows them. The exception is an #include
    /// placed before the #escape line: its patterns come first. Callers which depend on the result check it.
    ///
    /// <see cref="Ignore"/> and <see cref="Unignore"/> undo each other: ignoring leaves an existing exception for the
    /// same path in place (the new line above it wins), so un-ignoring only has to remove that line again.
    /// </remarks>
    public static class IgnorePatternEditor
    {
        private const string escapePrefix = "#escape";

        // Syncthing's default escape character on Windows, where '\' is the path separator
        public const char DefaultEscapeChar = '|';

        // Characters with a special meaning in glob patterns, which are valid in file names
        private static readonly char[] globChars = ['*', '?', '[', ']', '{', '}', ','];

        /// <summary>
        /// Returns new lines, with the given folder-relative paths ignored
        /// </summary>
        public static List<string> Ignore(IReadOnlyList<string> lines, IEnumerable<string> relativePaths)
        {
            var escapeChar = FindEscapeChar(lines);
            var patterns = relativePaths.Select(x => PatternFor(x, escapeChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Drop lines which would duplicate the new ones. Exceptions ("!/path") stay: the new line above them wins,
            // and they apply again once the line is removed
            var result = lines.Where(line => !patterns.Any(p => IsSamePattern(line, p))).ToList();
            result.InsertRange(InsertPosition(result), patterns);
            return result;
        }

        /// <summary>
        /// Returns new lines, with the given folder-relative paths no longer ignored.
        /// </summary>
        /// <remarks>
        /// If a path was ignored through a line naming exactly that path, the line is removed.
        /// Otherwise it is ignored by a more general pattern, and an exception ("!/path") is added.
        /// </remarks>
        public static List<string> Unignore(IReadOnlyList<string> lines, IEnumerable<string> relativePaths)
        {
            var escapeChar = FindEscapeChar(lines);
            var result = lines.ToList();
            var exceptions = new List<string>();

            foreach (var pattern in relativePaths.Select(x => PatternFor(x, escapeChar)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                int removed = result.RemoveAll(line => IsSamePattern(line, pattern));
                if (removed == 0 && !result.Any(line => IsSamePattern(line, "!" + pattern)))
                    exceptions.Add("!" + pattern);
            }

            result.InsertRange(InsertPosition(result), exceptions);
            return result;
        }

        /// <summary>
        /// Returns a pattern matching exactly the given folder-relative path (using '/' as separator)
        /// </summary>
        public static string PatternFor(string relativePath, char escapeChar = DefaultEscapeChar)
        {
            if (String.IsNullOrEmpty(relativePath))
                throw new ArgumentException("Can't create a pattern for the folder root", nameof(relativePath));

            var sb = new StringBuilder("/");
            foreach (var c in relativePath.Replace('\\', '/').Trim('/'))
            {
                if (c == escapeChar || Array.IndexOf(globChars, c) >= 0)
                    sb.Append(escapeChar);
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static char FindEscapeChar(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                if (!line.StartsWith(escapePrefix, StringComparison.Ordinal))
                    continue;

                var rest = line.Substring(escapePrefix.Length).Trim();
                if (rest.StartsWith('='))
                {
                    var value = rest.Substring(1).Trim();
                    if (value.Length > 0)
                        return value[0];
                }
            }
            return DefaultEscapeChar;
        }

        private static int InsertPosition(List<string> lines)
        {
            int index = lines.FindIndex(x => x.StartsWith(escapePrefix, StringComparison.Ordinal));
            return index + 1;
        }

        private static bool IsSamePattern(string line, string pattern)
        {
            // Matching is case-insensitive on Windows, and a trailing slash doesn't change the meaning
            return String.Equals(line.Trim().TrimEnd('/'), pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
