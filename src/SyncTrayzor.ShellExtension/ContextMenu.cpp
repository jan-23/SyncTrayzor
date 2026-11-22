#include "ContextMenu.h"
#include "PipeClient.h"
#include "StatusCache.h"
#include "resource.h"

#include <shlwapi.h>
#include <strsafe.h>

namespace
{
    // Explorer waits for the menu, but the user is waiting too
    constexpr unsigned long menuTimeout = 1000;
    constexpr unsigned long invokeTimeout = 2000;
    // Don't ship huge selections over the pipe
    constexpr UINT maxSelectedItems = 1000;

    // The SyncTrayzor icon as a menu bitmap, at the current size of small icons
    HBITMAP CreateMenuBitmap()
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        auto icon = static_cast<HICON>(LoadImageW(g_module, MAKEINTRESOURCEW(IDI_SYNCTRAYZOR), IMAGE_ICON, size, size, LR_DEFAULTCOLOR));
        if (icon == nullptr)
            return nullptr;

        BITMAPINFO info = {};
        info.bmiHeader.biSize = sizeof(info.bmiHeader);
        info.bmiHeader.biWidth = size;
        info.bmiHeader.biHeight = -size; // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HDC screen = GetDC(nullptr);
        HBITMAP result = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (result != nullptr)
        {
            HDC dc = CreateCompatibleDC(screen);
            auto previous = SelectObject(dc, result);
            // DrawIconEx keeps the alpha channel when drawing into a zeroed 32 bit DIB
            DrawIconEx(dc, 0, 0, icon, size, size, 0, nullptr, DI_NORMAL);
            SelectObject(dc, previous);
            DeleteDC(dc);
        }
        ReleaseDC(nullptr, screen);
        DestroyIcon(icon);
        return result;
    }
}

ContextMenu::~ContextMenu()
{
    if (menuBitmap != nullptr)
        DeleteObject(menuBitmap);
}

IFACEMETHODIMP ContextMenu::QueryInterface(REFIID riid, void** ppv)
{
    static const QITAB qit[] = {
        QITABENT(ContextMenu, IShellExtInit),
        QITABENT(ContextMenu, IContextMenu),
        { nullptr, 0 },
    };
    return QISearch(this, qit, riid, ppv);
}

IFACEMETHODIMP_(ULONG) ContextMenu::AddRef()
{
    return ++refCount;
}

IFACEMETHODIMP_(ULONG) ContextMenu::Release()
{
    ULONG count = --refCount;
    if (count == 0)
        delete this;
    return count;
}

IFACEMETHODIMP ContextMenu::Initialize(PCIDLIST_ABSOLUTE pidlFolder, IDataObject* pdtobj, HKEY)
{
    paths.clear();
    isBackground = false;

    if (pdtobj != nullptr)
    {
        // Selected files and folders
        FORMATETC format = { CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL };
        STGMEDIUM medium = {};
        if (FAILED(pdtobj->GetData(&format, &medium)))
            return E_INVALIDARG;

        auto drop = static_cast<HDROP>(GlobalLock(medium.hGlobal));
        if (drop != nullptr)
        {
            UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
            if (count <= maxSelectedItems)
            {
                for (UINT i = 0; i < count; i++)
                {
                    UINT length = DragQueryFileW(drop, i, nullptr, 0);
                    std::wstring path(length + 1, L'\0');
                    DragQueryFileW(drop, i, path.data(), length + 1);
                    path.resize(length);
                    paths.push_back(path);
                }
            }
            GlobalUnlock(medium.hGlobal);
        }
        ReleaseStgMedium(&medium);
    }
    else if (pidlFolder != nullptr)
    {
        // Right click on the background of an open folder
        wchar_t path[MAX_PATH * 4];
        if (SHGetPathFromIDListEx(pidlFolder, path, ARRAYSIZE(path), GPFIDL_DEFAULT))
        {
            paths.push_back(path);
            isBackground = true;
        }
    }

    return paths.empty() ? E_INVALIDARG : S_OK;
}

std::wstring ContextMenu::JoinedPaths() const
{
    std::wstring result;
    for (auto& path : paths)
    {
        result += L'\n';
        result += path;
    }
    return result;
}

IFACEMETHODIMP ContextMenu::QueryContextMenu(HMENU hmenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags)
{
    verbs.clear();

    if ((uFlags & CMF_DEFAULTONLY) != 0)
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);

    // Cheap check first, so menus elsewhere don't wait for SyncTrayzor
    for (auto& path : paths)
    {
        if (!StatusCache::IsInSyncthingFolder(path))
            return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
    }

    auto response = PipeRequest((isBackground ? L"MENU_BACKGROUND" : L"MENU") + JoinedPaths(), menuTimeout);
    if (!response || response->empty())
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);

    HMENU submenu = CreatePopupMenu();
    if (submenu == nullptr)
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);

    // Each line is "verb\ttext"
    UINT id = idCmdFirst;
    size_t start = 0;
    while (start < response->size() && id < idCmdLast)
    {
        size_t end = response->find(L'\n', start);
        if (end == std::wstring::npos)
            end = response->size();
        auto line = response->substr(start, end - start);
        start = end + 1;

        auto tab = line.find(L'\t');
        if (tab == std::wstring::npos)
            continue;

        verbs.push_back(line.substr(0, tab));
        auto text = line.substr(tab + 1);
        InsertMenuW(submenu, static_cast<UINT>(verbs.size() - 1), MF_BYPOSITION | MF_STRING, id++, text.c_str());
    }

    if (verbs.empty())
    {
        DestroyMenu(submenu);
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
    }

    wchar_t title[] = L"SyncTrayzor";
    MENUITEMINFOW item = {};
    item.cbSize = sizeof(item);
    item.fMask = MIIM_SUBMENU | MIIM_STRING | MIIM_ID | MIIM_BITMAP;
    item.wID = id++;
    item.hSubMenu = submenu;
    item.dwTypeData = title;
    if (menuBitmap == nullptr)
        menuBitmap = CreateMenuBitmap();
    item.hbmpItem = menuBitmap;
    if (!InsertMenuItemW(hmenu, indexMenu, TRUE, &item))
    {
        DestroyMenu(submenu);
        verbs.clear();
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
    }

    return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, id - idCmdFirst);
}

IFACEMETHODIMP ContextMenu::InvokeCommand(CMINVOKECOMMANDINFO* pici)
{
    if (pici == nullptr)
        return E_INVALIDARG;

    std::wstring verb;
    bool isUnicode = pici->cbSize >= sizeof(CMINVOKECOMMANDINFOEX) && (pici->fMask & CMIC_MASK_UNICODE) != 0;
    if (isUnicode && HIWORD(reinterpret_cast<CMINVOKECOMMANDINFOEX*>(pici)->lpVerbW) != 0)
    {
        verb = reinterpret_cast<CMINVOKECOMMANDINFOEX*>(pici)->lpVerbW;
    }
    else if (HIWORD(pici->lpVerb) != 0)
    {
        // ANSI verb strings are only used by old callers; our verbs are plain ASCII
        for (auto p = pici->lpVerb; *p != '\0'; p++)
            verb += static_cast<wchar_t>(*p);
    }
    else
    {
        auto index = LOWORD(pici->lpVerb);
        if (index >= verbs.size())
            return E_FAIL;
        verb = verbs[index];
    }

    bool known = false;
    for (auto& v : verbs)
        known |= v == verb;
    if (!known)
        return E_FAIL;

    // SyncTrayzor may show a window (a confirmation, the subfolder chooser), which Windows only lets it bring to the
    // front with Explorer's permission
    AllowSetForegroundWindow(ASFW_ANY);
    PipeRequest(L"INVOKE\n" + verb + JoinedPaths(), invokeTimeout);
    return S_OK;
}

IFACEMETHODIMP ContextMenu::GetCommandString(UINT_PTR idCmd, UINT uType, UINT*, CHAR* pszName, UINT cchMax)
{
    if (idCmd >= verbs.size())
        return E_INVALIDARG;

    switch (uType)
    {
    case GCS_VERBW:
        return StringCchCopyW(reinterpret_cast<PWSTR>(pszName), cchMax, verbs[idCmd].c_str());
    case GCS_VALIDATEW:
        return S_OK;
    default:
        return E_NOTIMPL;
    }
}
