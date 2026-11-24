#pragma once

#include "Common.h"

namespace StatusCache
{
    // True if the path is inside a Syncthing folder (cheap: the folder roots are cached)
    bool IsInSyncthingFolder(const std::wstring& path);

    // The state shown by the overlay icons. Answers within a few milliseconds, or returns None.
    ItemState GetState(const std::wstring& path);
}
