#include "StatusCache.h"
#include "PipeClient.h"

#include <mutex>
#include <shared_mutex>
#include <unordered_map>

namespace
{
    // How long the list of Syncthing folders is reused before asking SyncTrayzor again
    constexpr ULONGLONG rootsLifetime = 3000;
    // Explorer asks each overlay handler in turn about the same item, so remember answers briefly.
    // This must stay below the delay with which SyncTrayzor tells Explorer to refresh changed items.
    constexpr ULONGLONG stateLifetime = 200;
    constexpr size_t maxCachedStates = 1024;
    // Overlay handlers run on Explorer's threads, so never keep them waiting long
    constexpr unsigned long rootsTimeout = 100;
    constexpr unsigned long stateTimeout = 100;

    std::wstring ToLower(const std::wstring& value)
    {
        if (value.empty())
            return value;
        std::wstring result(value.size(), L'\0');
        int length = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, value.c_str(), static_cast<int>(value.size()),
            result.data(), static_cast<int>(result.size()), nullptr, nullptr, 0);
        result.resize(length > 0 ? length : 0);
        return result;
    }

    std::shared_mutex rootsMutex;
    std::vector<std::wstring> roots; // lower case, without trailing separator
    ULONGLONG rootsFetched = 0;
    std::atomic<bool> rootsRefreshing = false;

    std::mutex statesMutex;
    struct CachedState
    {
        ItemState state;
        ULONGLONG fetched;
    };
    std::unordered_map<std::wstring, CachedState> states;

    void RefreshRootsIfNeeded()
    {
        {
            std::shared_lock lock(rootsMutex);
            if (rootsFetched != 0 && GetTickCount64() - rootsFetched < rootsLifetime)
                return;
        }

        // Only one thread refreshes; the others carry on with what we have
        if (rootsRefreshing.exchange(true))
            return;

        std::vector<std::wstring> newRoots;
        bool serverMissing = false;
        auto response = PipeRequest(L"ROOTS", rootsTimeout, &serverMissing);
        if (response)
        {
            size_t start = 0;
            while (start < response->size())
            {
                size_t end = response->find(L'\n', start);
                if (end == std::wstring::npos)
                    end = response->size();
                auto root = ToLower(response->substr(start, end - start));
                while (!root.empty() && root.back() == L'\\')
                    root.pop_back();
                if (!root.empty())
                    newRoots.push_back(root);
                start = end + 1;
            }
        }

        {
            std::unique_lock lock(rootsMutex);
            // If SyncTrayzor was just slow to answer, keep what we know, rather than hiding everything until next time
            if (response || serverMissing)
                roots = std::move(newRoots);
            rootsFetched = GetTickCount64();
        }
        rootsRefreshing = false;
    }

    bool IsUnderRoot(const std::wstring& lowerPath)
    {
        std::shared_lock lock(rootsMutex);
        for (auto& root : roots)
        {
            if (lowerPath.size() < root.size() || lowerPath.compare(0, root.size(), root) != 0)
                continue;
            if (lowerPath.size() == root.size() || lowerPath[root.size()] == L'\\')
                return true;
        }
        return false;
    }
}

namespace StatusCache
{
    bool IsInSyncthingFolder(const std::wstring& path)
    {
        RefreshRootsIfNeeded();
        return IsUnderRoot(ToLower(path));
    }

    ItemState GetState(const std::wstring& path)
    {
        RefreshRootsIfNeeded();
        auto key = ToLower(path);
        if (!IsUnderRoot(key))
            return ItemState::None;

        auto now = GetTickCount64();
        {
            std::lock_guard lock(statesMutex);
            auto it = states.find(key);
            if (it != states.end() && now - it->second.fetched < stateLifetime)
                return it->second.state;
        }

        auto state = ItemState::None;
        if (auto response = PipeRequest(L"STATE\n" + path, stateTimeout))
        {
            int value = _wtoi(response->c_str());
            if (value >= static_cast<int>(ItemState::None) && value <= static_cast<int>(ItemState::Error))
                state = static_cast<ItemState>(value);
        }

        {
            std::lock_guard lock(statesMutex);
            if (states.size() >= maxCachedStates)
                states.clear();
            states[key] = { state, GetTickCount64() };
        }
        return state;
    }
}
