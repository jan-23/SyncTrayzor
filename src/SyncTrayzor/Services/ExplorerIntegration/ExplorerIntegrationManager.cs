using System;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    public interface IExplorerIntegrationManager : IDisposable
    {
        /// <summary>Shows sync status icons and the SyncTrayzor submenu in Explorer</summary>
        bool IsEnabled { get; set; }
    }

    public sealed class ExplorerIntegrationManager : IExplorerIntegrationManager
    {
        private readonly IExplorerStatusService statusService;
        private readonly IContextMenuRegistrar registrar;
        private readonly ExplorerIntegrationServer server;
        private bool? isEnabled;

        public ExplorerIntegrationManager(
            IExplorerStatusService statusService,
            IExplorerCommandHandler commandHandler,
            IContextMenuRegistrar registrar)
        {
            this.statusService = statusService;
            this.registrar = registrar;
            server = new ExplorerIntegrationServer(statusService, commandHandler);
        }

        public bool IsEnabled
        {
            get => isEnabled == true;
            set
            {
                if (isEnabled == value)
                    return;
                isEnabled = value;

                statusService.IsEnabled = value;
                if (value)
                {
                    // Register on every start, as the portable version may have been moved
                    registrar.Register();
                    server.Start();
                }
                else
                {
                    server.Stop();
                    registrar.Unregister();
                }
            }
        }

        public void Dispose()
        {
            // Leave the registration in place: the shell extension does nothing while SyncTrayzor isn't running
            server.Dispose();
            statusService.Dispose();
        }
    }
}
