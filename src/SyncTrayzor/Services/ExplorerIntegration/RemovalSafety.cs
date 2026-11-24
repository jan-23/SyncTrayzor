using SyncTrayzor.Syncthing.ApiClient;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    public enum FileCheckResult
    {
        /// <summary>The file may be deleted: another device has a valid copy of exactly this version</summary>
        Ok,
        /// <summary>Syncthing doesn't know the file, or its index doesn't match the file on disk (not scanned yet)</summary>
        NotScanned,
        /// <summary>This device has changes Syncthing hasn't synced (newer than, or conflicting with, the newest version)</summary>
        LocalChanges,
        /// <summary>No other trusted device that's connected right now has a valid copy of the newest version</summary>
        NoValidCopyElsewhere,
    }

    /// <summary>
    /// The checks which must pass before a file in a folder's local copy may be deleted
    /// </summary>
    public static class RemovalSafety
    {
        /// <param name="file">Syncthing's /rest/db/file for the file, or null if it isn't in the index</param>
        /// <param name="diskSize">The file's size on disk, when it was checked</param>
        /// <param name="diskModifiedUtc">The file's modification time on disk, when it was checked</param>
        /// <param name="modTimeWindow">Modification times within this window count as equal (the folder's modTimeWindowS)</param>
        /// <param name="untrustedDevices">Devices which only have an encrypted copy, which doesn't count</param>
        public static FileCheckResult CheckFile(DbFile file, long diskSize, DateTime diskModifiedUtc, TimeSpan modTimeWindow, IReadOnlyCollection<string> untrustedDevices)
        {
            var local = file?.Local;
            var global = file?.Global;
            if (local == null || local.Deleted)
                return FileCheckResult.NotScanned;

            // The index must describe what's on disk, otherwise the newest local content isn't known to Syncthing.
            // Like Syncthing, compare modification times exactly unless the folder allows a window.
            if (local.Size != diskSize || (local.Modified.ToUniversalTime() - diskModifiedUtc).Duration() > modTimeWindow)
                return FileCheckResult.NotScanned;

            // Receive-only changes, conflicts, or anything else Syncthing treats specially
            if (local.Invalid || local.Ignored || local.LocalFlags != 0)
                return FileCheckResult.LocalChanges;

            if (global == null || global.Deleted)
                return FileCheckResult.NoValidCopyElsewhere;

            // This device must have the newest version: otherwise its content may be a change nobody else has
            if (!SameVersion(local.Version, global.Version))
                return FileCheckResult.LocalChanges;

            if (!HasValidCopyElsewhere(file, untrustedDevices))
                return FileCheckResult.NoValidCopyElsewhere;

            return FileCheckResult.Ok;
        }

        /// <summary>
        /// Checked again right before deleting (when this device already ignores the file): the newest version must
        /// still be the one checked, and another device must still have a valid copy of it. This catches another
        /// device which removed the same files at the same time.
        /// </summary>
        public static bool StillAvailable(DbFile file, IReadOnlyCollection<string> checkedVersion, IReadOnlyCollection<string> untrustedDevices)
        {
            var global = file?.Global;
            return global != null && !global.Deleted && SameVersion(global.Version, checkedVersion) && HasValidCopyElsewhere(file, untrustedDevices);
        }

        // Availability lists the other devices which are connected right now and have a valid copy of the newest
        // version (with Syncthing > v2.1.7; older versions also list devices which ignore the file)
        private static bool HasValidCopyElsewhere(DbFile file, IReadOnlyCollection<string> untrustedDevices)
        {
            return file.Availability?.Any(x => !x.FromTemporary && !String.IsNullOrEmpty(x.DeviceId) && !untrustedDevices.Contains(x.DeviceId)) == true;
        }

        public static bool SameVersion(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
        {
            if (a == null || b == null || a.Count == 0)
                return false;
            return new HashSet<string>(a, StringComparer.Ordinal).SetEquals(b);
        }

        /// <summary>
        /// Returns the local items which the ignore patterns don't ignore, and whose deletion would therefore be synced
        /// </summary>
        public static IReadOnlyList<string> NotIgnored(IgnoreMatcher matcher, IEnumerable<string> localItems)
        {
            return localItems.Where(x => !matcher.IsIgnored(x)).ToList();
        }
    }
}
