// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <windows.h>
#include <shellapi.h>
#include <cstddef>
#include <cstdio>
#include <vector>

static void PrintProcess(const wchar_t* label, DWORD pid)
{
    DWORD session = 0;
    if (!ProcessIdToSessionId(pid, &session))
    {
        std::wprintf(L"%ls pid=%lu session-error=%lu\n", label, pid, GetLastError());
        return;
    }
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    HANDLE token = nullptr;
    if (!process || !OpenProcessToken(process, TOKEN_QUERY, &token))
    {
        std::wprintf(L"%ls pid=%lu session=%lu token-error=%lu\n", label, pid, session, GetLastError());
        if (process) CloseHandle(process);
        return;
    }
    DWORD size = 0;
    GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &size);
    std::vector<BYTE> buffer(size);
    if (GetTokenInformation(token, TokenIntegrityLevel, buffer.data(), size, &size))
    {
        auto sid = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buffer.data())->Label.Sid;
        auto integrity = *GetSidSubAuthority(sid, *GetSidSubAuthorityCount(sid) - 1);
        std::wprintf(L"%ls pid=%lu session=%lu integrity=0x%lx\n", label, pid, session, integrity);
    }
    else
    {
        std::wprintf(L"%ls pid=%lu session=%lu integrity-error=%lu\n", label, pid, session, GetLastError());
    }
    CloseHandle(token);
    CloseHandle(process);
}

int wmain()
{
    std::wprintf(L"SDK NOTIFYICONDATAW size=%zu hwnd=%zu id=%zu flags=%zu callback=%zu icon=%zu tip=%zu state=%zu stateMask=%zu info=%zu version=%zu title=%zu infoFlags=%zu guid=%zu balloon=%zu\n",
        sizeof(NOTIFYICONDATAW), offsetof(NOTIFYICONDATAW, hWnd), offsetof(NOTIFYICONDATAW, uID),
        offsetof(NOTIFYICONDATAW, uFlags), offsetof(NOTIFYICONDATAW, uCallbackMessage),
        offsetof(NOTIFYICONDATAW, hIcon), offsetof(NOTIFYICONDATAW, szTip), offsetof(NOTIFYICONDATAW, dwState),
        offsetof(NOTIFYICONDATAW, dwStateMask), offsetof(NOTIFYICONDATAW, szInfo),
        offsetof(NOTIFYICONDATAW, uVersion), offsetof(NOTIFYICONDATAW, szInfoTitle),
        offsetof(NOTIFYICONDATAW, dwInfoFlags), offsetof(NOTIFYICONDATAW, guidItem),
        offsetof(NOTIFYICONDATAW, hBalloonIcon));
    PrintProcess(L"Control", GetCurrentProcessId());
    DWORD explorer = 0;
    HWND taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
    GetWindowThreadProcessId(taskbar, &explorer);
    PrintProcess(L"Explorer", explorer);

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"Aspire.Tray.SdkControl";
    if (!RegisterClassW(&windowClass))
    {
        std::wprintf(L"SDK RegisterClassW failed: %lu\n", GetLastError());
        return 1;
    }
    HWND window = CreateWindowExW(0, windowClass.lpszClassName, L"Aspire Tray SDK control",
        0, 0, 0, 0, 0, nullptr, nullptr, windowClass.hInstance, nullptr);
    if (!window)
    {
        std::wprintf(L"SDK CreateWindowExW failed: %lu\n", GetLastError());
        UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);
        return 1;
    }
    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = window;
    data.uID = 2;
    data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    data.uCallbackMessage = WM_APP + 1;
    data.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    if (!data.hIcon)
    {
        std::wprintf(L"SDK LoadIconW failed: %lu\n", GetLastError());
        DestroyWindow(window);
        UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);
        return 1;
    }
    BOOL added = Shell_NotifyIconW(NIM_ADD, &data);
    std::wprintf(L"SDK stock icon NIM_ADD=%d flags=0x%lx\n", added, data.uFlags);
    bool cleanup = true;
    if (added)
    {
        cleanup = Shell_NotifyIconW(NIM_DELETE, &data) != FALSE;
        std::wprintf(L"SDK stock icon NIM_DELETE=%d\n", cleanup);
    }
    cleanup = DestroyWindow(window) && cleanup;
    cleanup = UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance) && cleanup;
    // Registration rejection is evidence, not a replacement for the mandatory product smoke.
    return cleanup ? 0 : 1;
}
