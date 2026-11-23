using NLog;
using Stylet;
using SyncTrayzor.Localization;
using SyncTrayzor.NotifyIcon;
using SyncTrayzor.Pages.SelectiveSync;
using SyncTrayzor.Properties;
using SyncTrayzor.Syncthing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    public record ExplorerMenuItem(string Verb, string Text);

    public interface IExplorerCommandHandler
    {
        /// <summary>Returns the items for the SyncTrayzor submenu, or an empty list if it shouldn't be shown</summary>
        /// <param name="paths">The selected items</param>
        /// <param name="isBackground">The menu is for the background of an open folder, rather than for selected items</param>
        Task<IReadOnlyList<ExplorerMenuItem>> GetMenuAsync(IReadOnlyList<string> paths, bool isBackground, CancellationToken cancellationToken);

        /// <summary>Runs a menu item in the background. The outcome is reported in a balloon.</summary>
        void Invoke(string verb, IReadOnlyList<string> paths);
    }

    public class ExplorerCommandHandler : IExplorerCommandHandler
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        // Namespaced, so they can't be mistaken for the shell's own verbs (like "open") by callers invoking by name
        public const string VerbPause = "synctrayzor.pause";
        public const string VerbResume = "synctrayzor.resume";
        public const string VerbRescan = "synctrayzor.rescan";
        public const string VerbRemove = "synctrayzor.remove";
        public const string VerbDownload = "synctrayzor.download";
        public const string VerbChoose = "synctrayzor.choose";
        public const string VerbOpen = "synctrayzor.open";

        // Building the menu must be quick, as Explorer waits for it
        private static readonly TimeSpan ignoresLoadTimeout = TimeSpan.FromMilliseconds(400);

        private readonly ISyncthingManager syncthingManager;
        private readonly IExplorerStatusService statusService;
        private readonly ISelectiveSyncService selectiveSyncService;
        private readonly INotifyIconManager notifyIconManager;
        private readonly IApplicationWindowState windowState;
        private readonly IWindowManager windowManager;
        private readonly Func<SelectiveSyncViewModel> selectiveSyncViewModelFactory;

        public ExplorerCommandHandler(
            ISyncthingManager syncthingManager,
            IExplorerStatusService statusService,
            ISelectiveSyncService selectiveSyncService,
            INotifyIconManager notifyIconManager,
            IApplicationWindowState windowState,
            IWindowManager windowManager,
            Func<SelectiveSyncViewModel> selectiveSyncViewModelFactory)
        {
            this.syncthingManager = syncthingManager;
            this.statusService = statusService;
            this.selectiveSyncService = selectiveSyncService;
            this.notifyIconManager = notifyIconManager;
            this.windowState = windowState;
            this.windowManager = windowManager;
            this.selectiveSyncViewModelFactory = selectiveSyncViewModelFactory;
        }

        public async Task<IReadOnlyList<ExplorerMenuItem>> GetMenuAsync(IReadOnlyList<string> paths, bool isBackground, CancellationToken cancellationToken)
        {
            if (!TryResolveAll(paths, out var folderPaths))
                return [];

            var items = new List<ExplorerMenuItem>();
            var paused = folderPaths.Any(x => statusService.IsFolderPaused(x.FolderId));
            // Syncthing has no ignore patterns for receive-encrypted folders, so subfolders can't be chosen there
            var canChoose = folderPaths.All(x => syncthingManager.Folders.TryFetchById(x.FolderId, out var folder) && !folder.IsReceiveEncrypted);

            if (folderPaths.Count == 1 && folderPaths[0].IsRoot)
            {
                items.Add(paused
                    ? new ExplorerMenuItem(VerbResume, Resources.ExplorerMenu_ResumeFolder)
                    : new ExplorerMenuItem(VerbPause, Resources.ExplorerMenu_PauseFolder));
                if (!paused)
                {
                    if (canChoose)
                        items.Add(new ExplorerMenuItem(VerbChoose, Resources.ExplorerMenu_ChooseSubfolders));
                    items.Add(new ExplorerMenuItem(VerbRescan, Resources.ExplorerMenu_Rescan));
                }
            }
            else if (!paused && folderPaths.All(x => !x.IsRoot))
            {
                // Removing and downloading work on selected directories only, not on the folder whose background was
                // clicked (that's too easy to do by accident)
                if (canChoose && !isBackground && paths.All(Directory.Exists))
                {
                    var ignored = await GetIgnoredAsync(folderPaths, cancellationToken);
                    if (ignored.All(x => x == false))
                    {
                        if (selectiveSyncService.CanRemove)
                            items.Add(new ExplorerMenuItem(VerbRemove, Resources.ExplorerMenu_RemoveFromDevice));
                    }
                    else if (ignored.All(x => x == true))
                    {
                        items.Add(new ExplorerMenuItem(VerbDownload, Resources.ExplorerMenu_DownloadToDevice));
                    }
                }
                if (canChoose && folderPaths.Count == 1 && Directory.Exists(paths[0]) && statusService.IsIgnored(folderPaths[0]) == false)
                    items.Add(new ExplorerMenuItem(VerbChoose, Resources.ExplorerMenu_ChooseSubfolders));
                items.Add(new ExplorerMenuItem(VerbRescan, Resources.ExplorerMenu_Rescan));
            }
            // A mix of folder roots and items inside folders, or a paused folder: only offer to open SyncTrayzor

            items.Add(new ExplorerMenuItem(VerbOpen, Resources.ExplorerMenu_Open));
            return items;
        }

        private async Task<List<bool?>> GetIgnoredAsync(IReadOnlyList<FolderPath> folderPaths, CancellationToken cancellationToken)
        {
            // The patterns are normally loaded already; if not, give it a moment
            foreach (var folderId in folderPaths.Where(x => statusService.IsIgnored(x) == null).Select(x => x.FolderId).Distinct())
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ignoresLoadTimeout);
                try
                {
                    await statusService.RefreshIgnoresAsync(folderId, timeoutCts.Token);
                }
                catch (Exception e)
                {
                    logger.Debug(e, "Unable to load ignore patterns of folder {0} for the context menu", folderId);
                }
            }
            return folderPaths.Select(statusService.IsIgnored).ToList();
        }

        public void Invoke(string verb, IReadOnlyList<string> paths)
        {
            _ = InvokeAsync(verb, paths);
        }

        private async Task InvokeAsync(string verb, IReadOnlyList<string> paths)
        {
            if (verb == VerbOpen)
            {
                Execute.OnUIThread(() => windowState.EnsureInForeground());
                return;
            }

            if (!TryResolveAll(paths, out var folderPaths))
                return;

            switch (verb)
            {
                case VerbPause:
                case VerbResume:
                    await RunAsync(async () =>
                    {
                        foreach (var folderId in folderPaths.Where(x => x.IsRoot).Select(x => x.FolderId).Distinct())
                            await syncthingManager.SetFolderPausedAsync(folderId, verb == VerbPause);
                    });
                    break;

                case VerbRescan:
                    await RunAsync(async () =>
                    {
                        foreach (var path in folderPaths)
                            await syncthingManager.ScanAsync(path.FolderId, path.IsRoot ? null : path.RelativePath);
                    });
                    break;

                case VerbRemove:
                    await RemoveAsync(folderPaths);
                    break;

                case VerbDownload:
                    if (await RunAsync(() => selectiveSyncService.DownloadToDeviceAsync(folderPaths, CancellationToken.None)))
                        ShowInfo(Resources.SelectiveSync_Downloading_Title, Localizer.F(Resources.SelectiveSync_Downloading_Message, Names(folderPaths)));
                    break;

                case VerbChoose:
                    var directory = folderPaths[0];
                    Execute.OnUIThread(() =>
                    {
                        var vm = selectiveSyncViewModelFactory();
                        vm.Directory = directory;
                        windowManager.ShowWindow(vm);
                    });
                    break;

                default:
                    logger.Warn("Unknown Explorer command {0}", verb);
                    break;
            }
        }

        private async Task RemoveAsync(IReadOnlyList<FolderPath> folderPaths)
        {
            var names = Names(folderPaths);
            var confirmed = false;
            await Execute.OnUIThreadAsync(() => confirmed = windowManager.ShowMessageBox(
                Localizer.F(Resources.SelectiveSync_ConfirmRemove_Message, names),
                Resources.SelectiveSync_ConfirmRemove_Title,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel) == MessageBoxResult.OK);
            if (!confirmed)
                return;

            // Checking every file can take a while for large folders
            ShowInfo(Resources.SelectiveSync_Removing_Title, Localizer.F(Resources.SelectiveSync_Removing_Message, names));

            IReadOnlyList<RemovalResult> results = [];
            if (!await RunAsync(async () => results = await selectiveSyncService.RemoveFromDeviceAsync(folderPaths, CancellationToken.None)))
                return;

            var removed = results.Where(x => x.Succeeded).Select(x => x.Directory).ToList();
            var errors = results.Where(x => !x.Succeeded).Select(x => x.Error).ToList();
            if (errors.Count > 0)
                ShowError(String.Join(Environment.NewLine, errors));
            else if (removed.Count > 0)
                ShowInfo(Resources.SelectiveSync_Removed_Title, Localizer.F(Resources.SelectiveSync_Removed_Message, Names(removed)));
        }

        private async Task<bool> RunAsync(Func<Task> action)
        {
            try
            {
                await action();
                return true;
            }
            catch (SelectiveSyncException e)
            {
                logger.Info("Explorer command refused: {0}", e.Message);
                ShowError(e.Message);
            }
            catch (Exception e)
            {
                logger.Error(e, "Explorer command failed");
                ShowError(Localizer.F(Resources.ExplorerIntegration_ActionFailed_Message, e.Message));
            }
            return false;
        }

        private static string Names(IEnumerable<FolderPath> paths) =>
            String.Join(", ", paths.Select(x => x.IsRoot ? x.Root : x.RelativePath.Substring(x.RelativePath.LastIndexOf('/') + 1)));

        private void ShowInfo(string title, string message) =>
            Execute.OnUIThread(() => notifyIconManager.ShowInfoBalloon(title, message));

        private void ShowError(string message) =>
            Execute.OnUIThread(() => notifyIconManager.ShowErrorBalloon(Resources.ExplorerIntegration_ActionFailed_Title, message));

        private bool TryResolveAll(IReadOnlyList<string> paths, out List<FolderPath> folderPaths)
        {
            folderPaths = new List<FolderPath>(paths.Count);
            if (paths.Count == 0)
                return false;

            foreach (var path in paths)
            {
                if (!statusService.TryResolve(path, out var folderPath))
                    return false;
                folderPaths.Add(folderPath);
            }
            return true;
        }
    }
}
