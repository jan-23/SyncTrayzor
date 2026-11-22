// SyncTrayzor Explorer integration.
//
// This DLL is loaded into Explorer (and every program showing a file dialog), so it is kept small and
// has no logic of its own: it asks SyncTrayzor over a named pipe (see PipeClient.cpp) and shows nothing
// if SyncTrayzor isn't running or doesn't answer quickly.
//
// Registration:
// - The context menu handler is registered for the current user by SyncTrayzor itself, when the
//   "Explorer integration" setting is enabled (ContextMenuRegistrar.cs).
// - The overlay icon handlers can only be registered for the whole machine, so the installer does that
//   by calling DllRegisterServer (regsvr32) below.

#include "Common.h"
#include "ContextMenu.h"
#include "OverlayIdentifier.h"

#include <new>

HMODULE g_module = nullptr;
std::atomic<long> g_objectCount = 0;

namespace
{
    struct OverlayRegistration
    {
        const GUID& clsid;
        // Windows shows only the first ~11 overlay handlers in alphabetical order, shared by all programs.
        // The digit orders ours by importance, in case only some of them make it.
        const wchar_t* name;
    };

    const OverlayRegistration overlays[] = {
        { CLSID_OverlayError, L"SyncTrayzor1Error" },
        { CLSID_OverlaySyncing, L"SyncTrayzor2Syncing" },
        { CLSID_OverlayIgnored, L"SyncTrayzor3Ignored" },
        { CLSID_OverlayUpToDate, L"SyncTrayzor4UpToDate" },
    };

    constexpr wchar_t overlayIdentifiersKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers\\";
    constexpr wchar_t approvedKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved";

    template <typename T>
    class ClassFactory : public IClassFactory
    {
    public:
        using Creator = T* (*)();
        explicit ClassFactory(Creator creator) : creator(creator) {}

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (ppv == nullptr)
                return E_POINTER;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return ++refCount; }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            ULONG count = --refCount;
            if (count == 0)
                delete this;
            return count;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
        {
            if (ppv == nullptr)
                return E_POINTER;
            *ppv = nullptr;
            if (outer != nullptr)
                return CLASS_E_NOAGGREGATION;

            T* instance = creator();
            if (instance == nullptr)
                return E_OUTOFMEMORY;
            HRESULT hr = instance->QueryInterface(riid, ppv);
            instance->Release();
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
                ++g_objectCount;
            else
                --g_objectCount;
            return S_OK;
        }

    private:
        std::atomic<ULONG> refCount = 1;
        ModuleReference moduleReference;
        Creator creator;
    };

    std::wstring GuidToString(const GUID& guid)
    {
        wchar_t buffer[64];
        StringFromGUID2(guid, buffer, ARRAYSIZE(buffer));
        return buffer;
    }

    HRESULT SetStringValue(HKEY root, const std::wstring& subKey, const wchar_t* name, const std::wstring& value)
    {
        HKEY key = nullptr;
        LSTATUS status = RegCreateKeyExW(root, subKey.c_str(), 0, nullptr, REG_OPTION_NON_VOLATILE, KEY_SET_VALUE | KEY_WOW64_64KEY, nullptr, &key, nullptr);
        if (status != ERROR_SUCCESS)
            return HRESULT_FROM_WIN32(status);
        status = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
        RegCloseKey(key);
        return HRESULT_FROM_WIN32(status);
    }

    void DeleteKey(HKEY root, const std::wstring& subKey)
    {
        RegDeleteTreeW(root, subKey.c_str());
        RegDeleteKeyExW(root, subKey.c_str(), KEY_WOW64_64KEY, 0);
    }

    void DeleteValue(HKEY root, const std::wstring& subKey, const std::wstring& name)
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(root, subKey.c_str(), 0, KEY_SET_VALUE | KEY_WOW64_64KEY, &key) == ERROR_SUCCESS)
        {
            RegDeleteValueW(key, name.c_str());
            RegCloseKey(key);
        }
    }

    template <ItemState State, int IconIndex>
    OverlayIdentifier* CreateOverlay()
    {
        return new (std::nothrow) OverlayIdentifier(State, IconIndex);
    }

    ContextMenu* CreateContextMenu()
    {
        return new (std::nothrow) ContextMenu();
    }
}

std::wstring GetModulePath()
{
    std::wstring path(MAX_PATH, L'\0');
    while (true)
    {
        DWORD length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
        if (length == 0)
            return {};
        if (length < path.size())
        {
            path.resize(length);
            return path;
        }
        path.resize(path.size() * 2);
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 ? S_OK : S_FALSE;
}

_Check_return_ STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID* ppv)
{
    if (ppv == nullptr)
        return E_POINTER;
    *ppv = nullptr;

    IClassFactory* factory = nullptr;
    if (rclsid == CLSID_ContextMenu)
        factory = new (std::nothrow) ClassFactory<ContextMenu>(&CreateContextMenu);
    else if (rclsid == CLSID_OverlayUpToDate)
        factory = new (std::nothrow) ClassFactory<OverlayIdentifier>(&CreateOverlay<ItemState::UpToDate, 0>);
    else if (rclsid == CLSID_OverlaySyncing)
        factory = new (std::nothrow) ClassFactory<OverlayIdentifier>(&CreateOverlay<ItemState::Syncing, 1>);
    else if (rclsid == CLSID_OverlayIgnored)
        factory = new (std::nothrow) ClassFactory<OverlayIdentifier>(&CreateOverlay<ItemState::Ignored, 2>);
    else if (rclsid == CLSID_OverlayError)
        factory = new (std::nothrow) ClassFactory<OverlayIdentifier>(&CreateOverlay<ItemState::Error, 3>);
    else
        return CLASS_E_CLASSNOTAVAILABLE;

    if (factory == nullptr)
        return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

// Registers the overlay icon handlers for all users. Needs admin rights; called by the installer.
STDAPI DllRegisterServer()
{
    auto modulePath = GetModulePath();
    if (modulePath.empty())
        return E_FAIL;

    for (auto& overlay : overlays)
    {
        auto clsid = GuidToString(overlay.clsid);
        auto clsidKey = L"Software\\Classes\\CLSID\\" + clsid;
        HRESULT hr = SetStringValue(HKEY_LOCAL_MACHINE, clsidKey, nullptr, overlay.name);
        if (SUCCEEDED(hr))
            hr = SetStringValue(HKEY_LOCAL_MACHINE, clsidKey + L"\\InprocServer32", nullptr, modulePath);
        if (SUCCEEDED(hr))
            hr = SetStringValue(HKEY_LOCAL_MACHINE, clsidKey + L"\\InprocServer32", L"ThreadingModel", L"Apartment");
        if (SUCCEEDED(hr))
            hr = SetStringValue(HKEY_LOCAL_MACHINE, std::wstring(overlayIdentifiersKey) + overlay.name, nullptr, clsid);
        if (SUCCEEDED(hr))
            hr = SetStringValue(HKEY_LOCAL_MACHINE, approvedKey, clsid.c_str(), overlay.name);
        if (FAILED(hr))
            return hr;
    }

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    for (auto& overlay : overlays)
    {
        auto clsid = GuidToString(overlay.clsid);
        DeleteKey(HKEY_LOCAL_MACHINE, std::wstring(overlayIdentifiersKey) + overlay.name);
        DeleteKey(HKEY_LOCAL_MACHINE, L"Software\\Classes\\CLSID\\" + clsid);
        DeleteValue(HKEY_LOCAL_MACHINE, approvedKey, clsid);
    }

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}
