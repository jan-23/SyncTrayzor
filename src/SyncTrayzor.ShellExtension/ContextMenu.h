#pragma once

#include "Common.h"

// Adds a "SyncTrayzor" submenu to files and folders inside Syncthing folders. The items and their
// (localized) text come from SyncTrayzor, which also carries out the commands.
class ContextMenu : public IShellExtInit, public IContextMenu
{
public:
    ContextMenu() = default;

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IShellExtInit
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder, IDataObject* pdtobj, HKEY hkeyProgID) override;

    // IContextMenu
    IFACEMETHODIMP QueryContextMenu(HMENU hmenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags) override;
    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* pici) override;
    IFACEMETHODIMP GetCommandString(UINT_PTR idCmd, UINT uType, UINT* pReserved, CHAR* pszName, UINT cchMax) override;

private:
    ~ContextMenu();

    std::wstring JoinedPaths() const;

    std::atomic<ULONG> refCount = 1;
    ModuleReference moduleReference;
    std::vector<std::wstring> paths;
    std::vector<std::wstring> verbs;
    // The menu was opened on the background of a folder, rather than on selected items
    bool isBackground = false;
    // The SyncTrayzor icon shown next to the submenu; it must live as long as the menu
    HBITMAP menuBitmap = nullptr;
};
