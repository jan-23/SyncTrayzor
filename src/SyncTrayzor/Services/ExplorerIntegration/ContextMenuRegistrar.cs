using Microsoft.Win32;
using NLog;
using System;
using System.IO;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    public interface IContextMenuRegistrar
    {
        void Register();
        void Unregister();
    }

    /// <summary>
    /// Registers the shell extension's context menu handler for the current user.
    /// </summary>
    /// <remarks>
    /// This needs no admin rights, so it also works for the portable version, and follows the
    /// "Explorer integration" setting. The overlay icon handlers have to be registered for the whole
    /// machine (Windows only reads them from HKLM), so the installer does that.
    /// </remarks>
    public class ContextMenuRegistrar : IContextMenuRegistrar
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        // Must match CLSID_ContextMenu in SyncTrayzor.ShellExtension
        public const string ContextMenuClsid = "{C928D838-7BCF-4D1F-AF8A-192E2AF76B61}";
        public const string ShellExtensionFileName = "SyncTrayzor.ShellExtension.dll";

        private const string handlerName = "SyncTrayzor";
        private const string clsidKey = @"Software\Classes\CLSID\" + ContextMenuClsid;

        // Files and folders, the background of an open folder, and drives
        private static readonly string[] handlerKeys =
        [
            @"Software\Classes\AllFilesystemObjects\shellex\ContextMenuHandlers\" + handlerName,
            @"Software\Classes\Directory\Background\shellex\ContextMenuHandlers\" + handlerName,
            @"Software\Classes\Drive\shellex\ContextMenuHandlers\" + handlerName,
        ];

        public static string ShellExtensionPath => Path.Combine(AppContext.BaseDirectory, ShellExtensionFileName);

        public void Register()
        {
            var dllPath = ShellExtensionPath;
            if (!File.Exists(dllPath))
            {
                logger.Warn("Not registering the Explorer context menu, as {0} doesn't exist", dllPath);
                return;
            }

            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(clsidKey))
                {
                    key.SetValue(null, "SyncTrayzor Explorer integration");
                }
                using (var key = Registry.CurrentUser.CreateSubKey(clsidKey + @"\InprocServer32"))
                {
                    key.SetValue(null, dllPath);
                    key.SetValue("ThreadingModel", "Apartment");
                }
                foreach (var handlerKey in handlerKeys)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(handlerKey);
                    key.SetValue(null, ContextMenuClsid);
                }
                logger.Info("Registered the Explorer context menu ({0})", dllPath);
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to register the Explorer context menu");
            }
        }

        public void Unregister()
        {
            try
            {
                foreach (var handlerKey in handlerKeys)
                    Registry.CurrentUser.DeleteSubKeyTree(handlerKey, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(clsidKey, throwOnMissingSubKey: false);
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to unregister the Explorer context menu");
            }
        }
    }
}
