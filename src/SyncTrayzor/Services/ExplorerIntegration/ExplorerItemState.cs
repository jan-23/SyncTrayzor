namespace SyncTrayzor.Services.ExplorerIntegration
{
    /// <summary>
    /// Status of a file or directory, as shown by the Explorer overlay icons.
    /// The numeric values are part of the protocol with the shell extension.
    /// </summary>
    public enum ExplorerItemState
    {
        /// <summary>Not in a Syncthing folder, folder paused, or state not known (yet)</summary>
        None = 0,

        /// <summary>This device has the latest version of the item (and everything inside it)</summary>
        UpToDate = 1,

        /// <summary>This device is missing the latest version of the item (or something inside it), or it was changed locally and not scanned yet</summary>
        Syncing = 2,

        /// <summary>The item is excluded by the folder's ignore patterns</summary>
        Ignored = 3,

        /// <summary>The item (or something inside it) failed to sync</summary>
        Error = 4,
    }
}
