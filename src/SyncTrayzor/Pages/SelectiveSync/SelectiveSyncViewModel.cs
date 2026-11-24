using NLog;
using Stylet;
using SyncTrayzor.Localization;
using SyncTrayzor.Properties;
using SyncTrayzor.Services.ExplorerIntegration;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SyncTrayzor.Pages.SelectiveSync
{
    public class SubfolderViewModel : PropertyChangedBase
    {
        public SubfolderInfo Info { get; set; }
        public string Name => Info.Name;

        /// <summary>Checked: synced to this device</summary>
        public bool IsSynced { get; set; }

        public bool IsChanged => IsSynced != Info.IsSynced;
        public string Status { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// Lets the user choose which subfolders of a Syncthing folder are synced to this device
    /// </summary>
    public class SelectiveSyncViewModel : Screen
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly ISelectiveSyncService selectiveSyncService;
        private readonly IWindowManager windowManager;

        public FolderPath Directory { get; set; }
        public string DirectoryName => Directory.IsRoot ? Directory.Root : Path.Combine(Directory.Root, Directory.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        public BindableCollection<SubfolderViewModel> Subfolders { get; } = new();
        public bool IsLoading { get; private set; } = true;
        public bool IsApplying { get; private set; }
        public bool HasNoSubfolders => !IsLoading && Subfolders.Count == 0;
        public bool HasSubfolders => !IsLoading && Subfolders.Count > 0;
        public bool CanRemove => selectiveSyncService.CanRemove;
        public string Error { get; private set; }

        public SelectiveSyncViewModel(ISelectiveSyncService selectiveSyncService, IWindowManager windowManager)
        {
            this.selectiveSyncService = selectiveSyncService;
            this.windowManager = windowManager;
            DisplayName = Resources.SelectiveSyncView_Title;
        }

        protected override async void OnInitialActivate()
        {
            try
            {
                var subfolders = await selectiveSyncService.ListSubfoldersAsync(Directory, CancellationToken.None);
                Subfolders.AddRange(subfolders.Select(x => new SubfolderViewModel()
                {
                    Info = x,
                    IsSynced = x.IsSynced,
                    Status = x.IsSynced ? null : Resources.SelectiveSyncView_Status_NotOnThisDevice,
                    // Without removal, a synced subfolder can't be unchecked
                    IsEnabled = CanRemove || !x.IsSynced,
                }));
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to list subfolders of {0}", DirectoryName);
                Error = e.Message;
            }
            finally
            {
                IsLoading = false;
                NotifyOfPropertyChange(nameof(HasNoSubfolders));
                NotifyOfPropertyChange(nameof(HasSubfolders));
                NotifyOfPropertyChange(nameof(CanApply));
            }
        }

        public bool CanApply => !IsLoading && !IsApplying;

        public async Task Apply()
        {
            var downloads = Subfolders.Where(x => x.IsChanged && x.IsSynced).ToList();
            var removals = Subfolders.Where(x => x.IsChanged && !x.IsSynced).ToList();
            if (downloads.Count == 0 && removals.Count == 0)
                return;

            if (removals.Count > 0)
            {
                var names = String.Join(", ", removals.Select(x => x.Name));
                var answer = windowManager.ShowMessageBox(
                    selectiveSyncService.ConfirmRemoveMessage(names),
                    Resources.SelectiveSync_ConfirmRemove_Title,
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Question,
                    System.Windows.MessageBoxResult.Cancel);
                if (answer != System.Windows.MessageBoxResult.OK)
                    return;
            }

            IsApplying = true;
            NotifyOfPropertyChange(nameof(CanApply));
            foreach (var subfolder in downloads.Concat(removals))
                subfolder.IsEnabled = false;

            try
            {
                // Each batch changes the ignore patterns once, so Syncthing only rescans once
                if (downloads.Count > 0)
                    await ApplyDownloadsAsync(downloads);
                if (removals.Count > 0)
                    await ApplyRemovalsAsync(removals);
            }
            finally
            {
                foreach (var subfolder in downloads.Concat(removals))
                    subfolder.IsEnabled = CanRemove || !subfolder.Info.IsSynced;
                IsApplying = false;
                NotifyOfPropertyChange(nameof(CanApply));
            }
        }

        private async Task ApplyDownloadsAsync(System.Collections.Generic.List<SubfolderViewModel> downloads)
        {
            foreach (var subfolder in downloads)
                subfolder.Status = Resources.SelectiveSyncView_Status_Downloading;
            try
            {
                await selectiveSyncService.DownloadToDeviceAsync(downloads.Select(x => x.Info.Path).ToList(), CancellationToken.None);
                foreach (var subfolder in downloads)
                    subfolder.Info = subfolder.Info with { IsSynced = true };
            }
            catch (Exception e)
            {
                logger.Info(e, "Downloading subfolders failed");
                var message = e is SelectiveSyncException ? e.Message : Localizer.F(Resources.ExplorerIntegration_ActionFailed_Message, e.Message);
                // Reload to show which ones are synced now
                var current = await TryListSubfoldersAsync();
                foreach (var subfolder in downloads)
                {
                    var info = current.FirstOrDefault(x => x.Path == subfolder.Info.Path);
                    if (info != null)
                        subfolder.Info = info;
                    subfolder.Status = subfolder.Info.IsSynced ? Resources.SelectiveSyncView_Status_Downloading : message;
                    subfolder.IsSynced = subfolder.Info.IsSynced;
                }
            }
        }

        private async Task ApplyRemovalsAsync(System.Collections.Generic.List<SubfolderViewModel> removals)
        {
            foreach (var subfolder in removals)
                subfolder.Status = Resources.SelectiveSyncView_Status_Removing;
            try
            {
                var results = await selectiveSyncService.RemoveFromDeviceAsync(removals.Select(x => x.Info.Path).ToList(), CancellationToken.None);
                foreach (var subfolder in removals)
                {
                    var result = results.FirstOrDefault(x => x.Directory == subfolder.Info.Path);
                    if (result?.Succeeded == true)
                    {
                        subfolder.Info = subfolder.Info with { IsSynced = false };
                        subfolder.Status = Resources.SelectiveSyncView_Status_Removed;
                    }
                    else
                    {
                        subfolder.Status = result?.Error;
                        // Show what's actually the case
                        subfolder.IsSynced = subfolder.Info.IsSynced;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Info(e, "Removing subfolders failed");
                foreach (var subfolder in removals)
                {
                    subfolder.Status = Localizer.F(Resources.ExplorerIntegration_ActionFailed_Message, e.Message);
                    subfolder.IsSynced = subfolder.Info.IsSynced;
                }
            }
        }

        private async Task<System.Collections.Generic.IReadOnlyList<SubfolderInfo>> TryListSubfoldersAsync()
        {
            try
            {
                return await selectiveSyncService.ListSubfoldersAsync(Directory, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.Info(e, "Failed to list subfolders of {0}", DirectoryName);
                return [];
            }
        }

        public void Close()
        {
            RequestClose();
        }
    }
}
