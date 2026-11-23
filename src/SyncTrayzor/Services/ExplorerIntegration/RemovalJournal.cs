using Newtonsoft.Json;
using NLog;
using SyncTrayzor.Services.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    public record PendingRemoval(string FolderId, string RelativePath);

    public interface IRemovalJournal
    {
        /// <summary>Records directories whose removal is in progress (they're about to be ignored)</summary>
        void Add(IEnumerable<PendingRemoval> removals);

        /// <summary>Records that these directories are done with: removed, or synced again</summary>
        void Remove(IEnumerable<PendingRemoval> removals);

        /// <summary>Directories whose removal never finished, e.g. because SyncTrayzor or Syncthing stopped meanwhile</summary>
        IReadOnlyList<PendingRemoval> Pending { get; }
    }

    /// <summary>
    /// Remembers removals in progress in a small file next to the configuration, so that a removal interrupted after
    /// its directory was ignored (but before it was emptied, or synced again after a failure) can be rolled back the
    /// next time SyncTrayzor runs. Otherwise the directory would stay ignored with its data, silently not syncing.
    /// </summary>
    public class RemovalJournal : IRemovalJournal
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly object syncRoot = new();
        private readonly string path;

        public RemovalJournal(IApplicationPathsProvider pathsProvider)
        {
            path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pathsProvider.ConfigurationFilePath)), "explorer-pending-removals.json");
        }

        public IReadOnlyList<PendingRemoval> Pending
        {
            get { lock (syncRoot) { return Load(); } }
        }

        public void Add(IEnumerable<PendingRemoval> removals)
        {
            lock (syncRoot)
            {
                var pending = Load();
                pending.AddRange(removals.Except(pending));
                Save(pending);
            }
        }

        public void Remove(IEnumerable<PendingRemoval> removals)
        {
            lock (syncRoot)
            {
                var pending = Load();
                var set = removals.ToHashSet();
                if (pending.RemoveAll(set.Contains) > 0)
                    Save(pending);
            }
        }

        private List<PendingRemoval> Load()
        {
            try
            {
                return File.Exists(path) ? JsonConvert.DeserializeObject<List<PendingRemoval>>(File.ReadAllText(path)) ?? [] : [];
            }
            catch (Exception e)
            {
                logger.Error(e, "Unable to read {0}", path);
                return [];
            }
        }

        private void Save(List<PendingRemoval> pending)
        {
            // Write the new contents first, so the file is never half-written
            if (pending.Count == 0)
            {
                File.Delete(path);
                return;
            }
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(pending, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }
    }
}
