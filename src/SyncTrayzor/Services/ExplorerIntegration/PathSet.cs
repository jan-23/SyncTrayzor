using System;
using System.Collections.Generic;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    /// <summary>
    /// A set of folder-relative paths ('/' separated), which can also answer whether
    /// a directory contains any of the paths, without walking the set.
    /// </summary>
    /// <remarks>Not thread-safe</remarks>
    public class PathSet
    {
        private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

        // Directory -> number of paths below it. The folder root is "".
        private readonly Dictionary<string, int> descendantCounts = new(StringComparer.OrdinalIgnoreCase);

        public int Count => paths.Count;

        public IEnumerable<string> Paths => paths;

        public static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

        public bool Add(string path)
        {
            path = Normalize(path);
            if (!paths.Add(path))
                return false;

            foreach (var ancestor in Ancestors(path))
            {
                descendantCounts.TryGetValue(ancestor, out int count);
                descendantCounts[ancestor] = count + 1;
            }
            return true;
        }

        public bool Remove(string path)
        {
            path = Normalize(path);
            if (!paths.Remove(path))
                return false;

            foreach (var ancestor in Ancestors(path))
            {
                int count = descendantCounts[ancestor] - 1;
                if (count == 0)
                    descendantCounts.Remove(ancestor);
                else
                    descendantCounts[ancestor] = count;
            }
            return true;
        }

        public void Clear()
        {
            paths.Clear();
            descendantCounts.Clear();
        }

        public bool Contains(string path) => paths.Contains(Normalize(path));

        /// <summary>
        /// True if the path is in the set, or is a directory containing a path in the set
        /// </summary>
        public bool ContainsOrHasDescendant(string path)
        {
            path = Normalize(path);
            return paths.Contains(path) || descendantCounts.ContainsKey(path);
        }

        /// <summary>
        /// Proper ancestors of a path, from the folder root ("") downwards
        /// </summary>
        public static IEnumerable<string> Ancestors(string path)
        {
            if (path.Length == 0)
                yield break;

            yield return String.Empty;
            for (int i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
            {
                yield return path.Substring(0, i);
            }
        }
    }
}
