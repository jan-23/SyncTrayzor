using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    public interface IRecycleBin
    {
        /// <summary>Whether the drive containing the path has a Recycle Bin (network drives usually don't)</summary>
        bool IsAvailableFor(string path);

        /// <summary>
        /// Moves files to the Recycle Bin. Returns those which weren't moved, for example because they're in use, or
        /// because they're too large for the Recycle Bin and the user declined deleting them permanently.
        /// </summary>
        IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths);
    }

    public class RecycleBin : IRecycleBin
    {
        // Files per call: each call is slow, but a failure stops the rest of its batch
        private const int batchSize = 200;

        public bool IsAvailableFor(string path)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (String.IsNullOrEmpty(root))
                return false;
            var info = new SHQUERYRBINFO() { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBinW(root, ref info) == 0;
        }

        public IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths)
        {
            var notMoved = new List<string>();
            foreach (var batch in paths.Chunk(batchSize))
            {
                var operation = new SHFILEOPSTRUCT()
                {
                    wFunc = FO_DELETE,
                    // Null-separated, double-null-terminated list of paths
                    pFrom = String.Join("\0", batch.Select(Path.GetFullPath)) + "\0\0",
                    // FOF_WANTNUKEWARNING: ask before deleting permanently (e.g. too large for the Recycle Bin), instead
                    // of doing so silently. Declining counts as a failure.
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
                };
                int result = SHFileOperationW(ref operation);
                var left = batch.Where(x => File.Exists(x) || Directory.Exists(x)).ToList();
                notMoved.AddRange(left);
                if (result != 0 || operation.fAnyOperationsAborted || left.Count > 0)
                {
                    // Don't carry on after a failure: the caller rolls back
                    notMoved.AddRange(paths.Skip(paths.ToList().IndexOf(batch[^1]) + 1));
                    break;
                }
            }
            return notMoved;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
    }
}
