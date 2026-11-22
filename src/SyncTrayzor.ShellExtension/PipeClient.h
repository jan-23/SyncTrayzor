#pragma once

#include <optional>
#include <string>

// Talks to SyncTrayzor's ExplorerIntegrationServer.
// Sends one request and returns the response, or nothing if SyncTrayzor isn't running, isn't
// owned by the current user, or didn't answer in time. Never blocks for longer than the timeout.
// If serverMissing is given, it's set to whether SyncTrayzor isn't running at all (as opposed to e.g. answering too slowly).
std::optional<std::wstring> PipeRequest(const std::wstring& request, unsigned long timeoutMilliseconds, bool* serverMissing = nullptr);
