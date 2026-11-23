using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    /// <summary>
    /// A path inside a Syncthing folder
    /// </summary>
    /// <param name="Root">The folder's root, as an absolute path (a drive root keeps its separator, e.g. "E:\")</param>
    /// <param name="RelativePath">The path below the root, with '/' as separator, or "" for the root itself</param>
    public readonly record struct FolderPath(string FolderId, string Root, string RelativePath)
    {
        public bool IsRoot => RelativePath.Length == 0;
    }

    /// <summary>
    /// Maps absolute file system paths to the Syncthing folder containing them
    /// </summary>
    public class FolderPathResolver
    {
        private readonly List<(string FolderId, string Root, string Key)> roots;

        public FolderPathResolver(IEnumerable<(string FolderId, string Root)> folders)
        {
            // Longest root first, so nested folders win over their parents
            roots = folders
                .Where(x => !String.IsNullOrWhiteSpace(x.Root))
                .Select(x => (x.FolderId, Root: NormalizeRoot(x.Root)))
                .Select(x => (x.FolderId, x.Root, Key: x.Root.TrimEnd(Path.DirectorySeparatorChar)))
                .OrderByDescending(x => x.Key.Length)
                .ToList();
        }

        public IEnumerable<string> Roots => roots.Select(x => x.Root);

        public bool TryResolve(string path, out FolderPath folderPath)
        {
            folderPath = default;
            if (String.IsNullOrEmpty(path))
                return false;

            path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

            foreach (var (folderId, root, key) in roots)
            {
                if (!path.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (path.Length == key.Length)
                {
                    folderPath = new FolderPath(folderId, root, String.Empty);
                    return true;
                }

                if (path[key.Length] == Path.DirectorySeparatorChar)
                {
                    var relative = path.Substring(key.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                    folderPath = new FolderPath(folderId, root, relative);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Strips trailing separators, except from a drive root ("E:\"), as "E:" would mean the current directory on E:
        /// </summary>
        public static string NormalizeRoot(string root)
        {
            root = root.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar);
            return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
        }
    }
}
