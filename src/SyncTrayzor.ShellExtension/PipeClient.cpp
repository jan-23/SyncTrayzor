#include "PipeClient.h"
#include "Common.h"

#include <sddl.h>

#include <memory>

namespace
{
    struct HandleCloser
    {
        void operator()(HANDLE handle) const
        {
            if (handle != nullptr && handle != INVALID_HANDLE_VALUE)
                CloseHandle(handle);
        }
    };
    using UniqueHandle = std::unique_ptr<void, HandleCloser>;

    std::vector<BYTE> GetCurrentUserSid()
    {
        HANDLE rawToken = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &rawToken))
            return {};
        UniqueHandle token(rawToken);

        DWORD size = 0;
        GetTokenInformation(token.get(), TokenUser, nullptr, 0, &size);
        std::vector<BYTE> buffer(size);
        if (size == 0 || !GetTokenInformation(token.get(), TokenUser, buffer.data(), size, &size))
            return {};

        auto sid = reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid;
        std::vector<BYTE> result(GetLengthSid(sid));
        CopySid(static_cast<DWORD>(result.size()), result.data(), sid);
        return result;
    }

    const std::vector<BYTE>& CurrentUserSid()
    {
        static const std::vector<BYTE> sid = GetCurrentUserSid();
        return sid;
    }

    const std::wstring& PipeName()
    {
        // Must match ExplorerIntegrationServer.PipeName in SyncTrayzor
        static const std::wstring name = []
        {
            std::wstring result = L"\\\\.\\pipe\\SyncTrayzor.Explorer.";
            auto& sid = CurrentUserSid();
            LPWSTR sidString = nullptr;
            if (!sid.empty() && ConvertSidToStringSidW(const_cast<BYTE*>(sid.data()), &sidString))
            {
                result += sidString;
                LocalFree(sidString);
            }
            return result;
        }();
        return name;
    }

    // Makes sure that the pipe server runs as the current user, so another user can't impersonate SyncTrayzor.
    // Checked every time: a process ID may be reused once SyncTrayzor has exited.
    bool IsServerTrusted(HANDLE pipe)
    {
        ULONG processId = 0;
        if (!GetNamedPipeServerProcessId(pipe, &processId))
            return false;

        UniqueHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId));
        if (!process)
            return false;

        HANDLE rawToken = nullptr;
        if (!OpenProcessToken(process.get(), TOKEN_QUERY, &rawToken))
            return false;
        UniqueHandle token(rawToken);

        DWORD size = 0;
        GetTokenInformation(token.get(), TokenUser, nullptr, 0, &size);
        std::vector<BYTE> buffer(size);
        if (size == 0 || !GetTokenInformation(token.get(), TokenUser, buffer.data(), size, &size))
            return false;

        auto& ourSid = CurrentUserSid();
        return !ourSid.empty() && EqualSid(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, const_cast<BYTE*>(ourSid.data()));
    }

    ULONGLONG Remaining(ULONGLONG deadline)
    {
        auto now = GetTickCount64();
        return now >= deadline ? 0 : deadline - now;
    }

    // Waits for an overlapped operation, cancelling it if the deadline passes
    bool CompleteOverlapped(HANDLE pipe, OVERLAPPED& overlapped, BOOL started, ULONGLONG deadline, DWORD& transferred, bool& moreData)
    {
        moreData = false;
        if (!started)
        {
            DWORD error = GetLastError();
            if (error == ERROR_MORE_DATA)
            {
                moreData = true;
                return GetOverlappedResult(pipe, &overlapped, &transferred, FALSE) || GetLastError() == ERROR_MORE_DATA;
            }
            if (error != ERROR_IO_PENDING)
                return false;

            if (WaitForSingleObject(overlapped.hEvent, static_cast<DWORD>(Remaining(deadline))) != WAIT_OBJECT_0)
            {
                CancelIoEx(pipe, &overlapped);
                GetOverlappedResult(pipe, &overlapped, &transferred, TRUE);
                return false;
            }
        }

        if (GetOverlappedResult(pipe, &overlapped, &transferred, FALSE))
            return true;
        if (GetLastError() == ERROR_MORE_DATA)
        {
            moreData = true;
            return true;
        }
        return false;
    }
}

std::optional<std::wstring> PipeRequest(const std::wstring& request, unsigned long timeoutMilliseconds, bool* serverMissing)
{
    if (serverMissing != nullptr)
        *serverMissing = false;
    const ULONGLONG deadline = GetTickCount64() + timeoutMilliseconds;
    auto& name = PipeName();

    UniqueHandle pipe;
    while (true)
    {
        // SECURITY_IDENTIFICATION: the server may find out who we are, but not act as us
        HANDLE handle = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (handle != INVALID_HANDLE_VALUE)
        {
            pipe.reset(handle);
            break;
        }

        // All server instances are busy: wait a little, if we have time
        DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY)
        {
            if (serverMissing != nullptr)
                *serverMissing = error == ERROR_FILE_NOT_FOUND;
            return std::nullopt;
        }
        auto remaining = Remaining(deadline);
        if (remaining == 0 || !WaitNamedPipeW(name.c_str(), static_cast<DWORD>(remaining < 50 ? remaining : 50)))
        {
            if (Remaining(deadline) == 0)
                return std::nullopt;
        }
    }

    DWORD mode = PIPE_READMODE_MESSAGE;
    if (!SetNamedPipeHandleState(pipe.get(), &mode, nullptr, nullptr) || !IsServerTrusted(pipe.get()))
        return std::nullopt;

    UniqueHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event)
        return std::nullopt;

    OVERLAPPED overlapped = {};
    overlapped.hEvent = event.get();
    DWORD transferred = 0;
    bool moreData = false;

    auto requestBytes = static_cast<DWORD>(request.size() * sizeof(wchar_t));
    BOOL started = WriteFile(pipe.get(), request.data(), requestBytes, nullptr, &overlapped);
    if (!CompleteOverlapped(pipe.get(), overlapped, started, deadline, transferred, moreData) || transferred != requestBytes)
        return std::nullopt;

    std::vector<char> response;
    char buffer[4096];
    do
    {
        overlapped = {};
        overlapped.hEvent = event.get();
        ResetEvent(event.get());
        started = ReadFile(pipe.get(), buffer, sizeof(buffer), nullptr, &overlapped);
        if (!CompleteOverlapped(pipe.get(), overlapped, started, deadline, transferred, moreData))
            return std::nullopt;
        response.insert(response.end(), buffer, buffer + transferred);
        if (response.size() > 1024 * 1024)
            return std::nullopt;
    } while (moreData);

    return std::wstring(reinterpret_cast<const wchar_t*>(response.data()), response.size() / sizeof(wchar_t));
}
