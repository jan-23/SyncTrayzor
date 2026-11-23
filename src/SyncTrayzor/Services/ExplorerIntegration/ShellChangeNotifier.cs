using NLog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    public interface IShellChangeNotifier : IDisposable
    {
        /// <summary>Asks Explorer to redraw the given items (which re-queries their overlay icons)</summary>
        void ItemsChanged(IEnumerable<string> paths);

        /// <summary>Asks Explorer to redraw a directory and everything shown inside it</summary>
        void DirectoryChanged(string path);
    }

    /// <summary>
    /// Batches change notifications and sends them to the shell, so that bursts of
    /// sync events don't turn into a flood of SHChangeNotify calls.
    /// </summary>
    public class ShellChangeNotifier : IShellChangeNotifier
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private static readonly TimeSpan flushInterval = TimeSpan.FromMilliseconds(250);

        // Above this, notifying every item is slower than having Explorer refresh the whole directory
        private const int maxItemNotificationsPerFlush = 500;

        private readonly object syncRoot = new();
        private readonly HashSet<string> pendingItems = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> pendingDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer timer;
        private bool timerScheduled;

        public ShellChangeNotifier()
        {
            timer = new Timer(_ => Flush());
        }

        public void ItemsChanged(IEnumerable<string> paths)
        {
            lock (syncRoot)
            {
                pendingItems.UnionWith(paths);
                ScheduleFlush();
            }
        }

        public void DirectoryChanged(string path)
        {
            lock (syncRoot)
            {
                pendingDirectories.Add(path);
                ScheduleFlush();
            }
        }

        private void ScheduleFlush()
        {
            if (timerScheduled)
                return;
            timerScheduled = true;
            timer.Change(flushInterval, Timeout.InfiniteTimeSpan);
        }

        private void Flush()
        {
            string[] items;
            string[] directories;
            lock (syncRoot)
            {
                timerScheduled = false;
                items = new string[pendingItems.Count];
                pendingItems.CopyTo(items);
                pendingItems.Clear();
                directories = new string[pendingDirectories.Count];
                pendingDirectories.CopyTo(directories);
                pendingDirectories.Clear();
            }

            try
            {
                if (items.Length > maxItemNotificationsPerFlush)
                {
                    // Fall back to refreshing the parent directories
                    var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items)
                    {
                        var parent = System.IO.Path.GetDirectoryName(item);
                        if (parent != null)
                            parents.Add(parent);
                    }
                    parents.UnionWith(directories);
                    directories = new string[parents.Count];
                    parents.CopyTo(directories);
                    items = [];
                }

                foreach (var item in items)
                    NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEITEM, NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT, item, IntPtr.Zero);
                foreach (var directory in directories)
                    NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR, NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT, directory, IntPtr.Zero);
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to notify the shell of changes");
            }
        }

        public void Dispose()
        {
            timer.Dispose();
        }

        private static class NativeMethods
        {
            public const int SHCNE_UPDATEDIR = 0x00001000;
            public const int SHCNE_UPDATEITEM = 0x00002000;
            public const uint SHCNF_PATHW = 0x0005;
            public const uint SHCNF_FLUSHNOWAIT = 0x3000;

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            public static extern void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
        }
    }
}
