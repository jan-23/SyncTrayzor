#include "OverlayIdentifier.h"
#include "StatusCache.h"

#include <shlwapi.h>

OverlayIdentifier::OverlayIdentifier(ItemState state, int iconIndex)
    : state(state), iconIndex(iconIndex)
{
}

IFACEMETHODIMP OverlayIdentifier::QueryInterface(REFIID riid, void** ppv)
{
    static const QITAB qit[] = {
        QITABENT(OverlayIdentifier, IShellIconOverlayIdentifier),
        { nullptr, 0 },
    };
    return QISearch(this, qit, riid, ppv);
}

IFACEMETHODIMP_(ULONG) OverlayIdentifier::AddRef()
{
    return ++refCount;
}

IFACEMETHODIMP_(ULONG) OverlayIdentifier::Release()
{
    ULONG count = --refCount;
    if (count == 0)
        delete this;
    return count;
}

IFACEMETHODIMP OverlayIdentifier::IsMemberOf(PCWSTR pwszPath, DWORD)
{
    if (pwszPath == nullptr)
        return E_INVALIDARG;
    return StatusCache::GetState(pwszPath) == state ? S_OK : S_FALSE;
}

IFACEMETHODIMP OverlayIdentifier::GetOverlayInfo(PWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags)
{
    if (pwszIconFile == nullptr || pIndex == nullptr || pdwFlags == nullptr || cchMax <= 0)
        return E_INVALIDARG;

    auto path = GetModulePath();
    if (path.empty() || path.size() >= static_cast<size_t>(cchMax))
        return E_FAIL;

    wcscpy_s(pwszIconFile, cchMax, path.c_str());
    *pIndex = iconIndex;
    *pdwFlags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
    return S_OK;
}

IFACEMETHODIMP OverlayIdentifier::GetPriority(int* pPriority)
{
    if (pPriority == nullptr)
        return E_INVALIDARG;
    // Our states are mutually exclusive, so this only matters relative to other programs' overlays (0 is the
    // highest priority). "Up to date" applies to almost everything, so it gives way to other programs' overlays.
    switch (state)
    {
    case ItemState::Error:
        *pPriority = 0;
        break;
    case ItemState::Syncing:
        *pPriority = 10;
        break;
    case ItemState::Ignored:
        *pPriority = 50;
        break;
    default:
        *pPriority = 90;
        break;
    }
    return S_OK;
}
