#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>

#include <atomic>
#include <string>
#include <vector>

// Must match ContextMenuRegistrar.ContextMenuClsid in SyncTrayzor
// {C928D838-7BCF-4D1F-AF8A-192E2AF76B61}
inline constexpr GUID CLSID_ContextMenu = { 0xc928d838, 0x7bcf, 0x4d1f, { 0xaf, 0x8a, 0x19, 0x2e, 0x2a, 0xf7, 0x6b, 0x61 } };

// {1A9210BF-5296-4320-BD74-7DD97F0CEFF0}
inline constexpr GUID CLSID_OverlayUpToDate = { 0x1a9210bf, 0x5296, 0x4320, { 0xbd, 0x74, 0x7d, 0xd9, 0x7f, 0x0c, 0xef, 0xf0 } };
// {C8C83B0E-D4C0-48F0-9D4D-E8F459581D7F}
inline constexpr GUID CLSID_OverlaySyncing = { 0xc8c83b0e, 0xd4c0, 0x48f0, { 0x9d, 0x4d, 0xe8, 0xf4, 0x59, 0x58, 0x1d, 0x7f } };
// {A80987BB-6D2D-4F31-8CC4-E2388C4E0350}
inline constexpr GUID CLSID_OverlayIgnored = { 0xa80987bb, 0x6d2d, 0x4f31, { 0x8c, 0xc4, 0xe2, 0x38, 0x8c, 0x4e, 0x03, 0x50 } };
// {A82436B3-2AC6-48A0-AD4D-AD11FFF77BCA}
inline constexpr GUID CLSID_OverlayError = { 0xa82436b3, 0x2ac6, 0x48a0, { 0xad, 0x4d, 0xad, 0x11, 0xff, 0xf7, 0x7b, 0xca } };

// Must match ExplorerItemState in SyncTrayzor
enum class ItemState : int
{
    None = 0,
    UpToDate = 1,
    Syncing = 2,
    Ignored = 3,
    Error = 4,
};

extern HMODULE g_module;
extern std::atomic<long> g_objectCount;

// Keeps the DLL loaded while COM objects created from it exist
class ModuleReference
{
public:
    ModuleReference() { ++g_objectCount; }
    ~ModuleReference() { --g_objectCount; }
    ModuleReference(const ModuleReference&) = delete;
    ModuleReference& operator=(const ModuleReference&) = delete;
};

std::wstring GetModulePath();
