namespace SyncTrayzor.Services.ExplorerIntegration
{
    /// <summary>
    /// What is known about one Syncthing folder, for the purpose of computing item states
    /// </summary>
    public class FolderSyncInfo
    {
        public bool IsPaused { get; set; }

        /// <summary>The folder as a whole is in an error state (e.g. its path is missing)</summary>
        public bool HasFolderError { get; set; }

        /// <summary>Items this device needs from other devices</summary>
        public PathSet Needed { get; } = new();

        /// <summary>Syncthing needs more items than we fetched, so every item is assumed to be syncing</summary>
        public bool NeededOverflow { get; set; }

        /// <summary>Items which failed to sync</summary>
        public PathSet Errors { get; } = new();

        /// <summary>Items changed locally since the last scan</summary>
        public PathSet LocalChanges { get; } = new();

        /// <summary>Too many local changes to track: everything is assumed to be changed until the next scan</summary>
        public bool LocalChangesOverflow { get; set; }
    }

    public static class ExplorerStateCalculator
    {
        /// <param name="info">The folder containing the item</param>
        /// <param name="relativePath">The item, relative to the folder root ("" for the root itself)</param>
        /// <param name="isIgnored">Whether the item is ignored, or null if that isn't known yet</param>
        public static ExplorerItemState Calculate(FolderSyncInfo info, string relativePath, bool? isIgnored)
        {
            if (info.IsPaused)
                return ExplorerItemState.None;

            if (isIgnored == true)
                return ExplorerItemState.Ignored;

            // Errors and needed items come from Syncthing's index, so these items aren't ignored
            if (info.HasFolderError || info.Errors.ContainsOrHasDescendant(relativePath))
                return ExplorerItemState.Error;
            if (info.Needed.ContainsOrHasDescendant(relativePath))
                return ExplorerItemState.Syncing;

            // Anything else may be ignored, so don't claim it's up to date (or syncing) until we know it isn't
            if (isIgnored == null)
                return ExplorerItemState.None;

            if (info.NeededOverflow || info.LocalChangesOverflow || info.LocalChanges.ContainsOrHasDescendant(relativePath))
                return ExplorerItemState.Syncing;

            return ExplorerItemState.UpToDate;
        }
    }
}
