using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SyncTrayzor.Syncthing.ApiClient
{
    public interface ISyncthingApiClient
    {
        Task ShutdownAsync();
        Task<List<Event>> FetchEventsAsync(int since, int limit, CancellationToken cancellationToken);
        Task<List<Event>> FetchEventsAsync(int since, CancellationToken cancellationToken);
        Task<Config> FetchConfigAsync();
        Task ScanAsync(string folderId, string subPath);
        Task<SystemInfo> FetchSystemInfoAsync(CancellationToken cancellationToken);
        Task<Connections> FetchConnectionsAsync(CancellationToken cancellationToken);
        Task<SyncthingVersion> FetchVersionAsync(CancellationToken cancellationToken);
        Task RestartAsync();
        Task<FolderStatus> FetchFolderStatusAsync(string folderId, CancellationToken cancellationToken);
        Task<DebugFacilitiesSettings> FetchDebugFacilitiesAsync();
        Task SetDebugFacilitiesAsync(IEnumerable<string> enable, IEnumerable<string> disable);
        Task PauseDeviceAsync(string deviceId);
        Task ResumeDeviceAsync(string deviceId);
        Task SetFolderPausedAsync(string folderId, bool paused);
        Task<Ignores> FetchIgnoresAsync(string folderId, CancellationToken cancellationToken);
        Task SetIgnoresAsync(string folderId, IEnumerable<string> lines, CancellationToken cancellationToken);
        Task<DbFile> FetchFileAsync(string folderId, string file, CancellationToken cancellationToken);
        Task<List<TreeEntry>> BrowseDirectoriesAsync(string folderId, string prefix, CancellationToken cancellationToken);
        Task<FolderNeed> FetchNeedAsync(string folderId, int page, int perPage, CancellationToken cancellationToken);
    }
}
