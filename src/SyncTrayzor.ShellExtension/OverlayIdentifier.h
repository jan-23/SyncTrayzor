#pragma once

#include "Common.h"

// One overlay icon. Windows asks each registered overlay handler whether its icon applies to an item,
// so there is one instance of this class per state.
class OverlayIdentifier : public IShellIconOverlayIdentifier
{
public:
    OverlayIdentifier(ItemState state, int iconIndex);

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IShellIconOverlayIdentifier
    IFACEMETHODIMP IsMemberOf(PCWSTR pwszPath, DWORD dwAttrib) override;
    IFACEMETHODIMP GetOverlayInfo(PWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags) override;
    IFACEMETHODIMP GetPriority(int* pPriority) override;

private:
    ~OverlayIdentifier() = default;

    std::atomic<ULONG> refCount = 1;
    ModuleReference moduleReference;
    const ItemState state;
    const int iconIndex;
};
