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
    if (!process)
    {
        std::wprintf(L"%ls pid=%lu session=%lu process-error=%lu\n", label, pid, session, GetLastError());
        return;
    }
    USHORT processMachine = 0;
    USHORT nativeMachine = 0;
    if (IsWow64Process2(process, &processMachine, &nativeMachine))
    {
        // UNKNOWN (0) means not a WOW process, not an unknown native architecture.
        // https://learn.microsoft.com/windows/win32/api/wow64apiset/nf-wow64apiset-iswow64process2
        std::wprintf(L"%ls pid=%lu process-machine=0x%04x native-machine=0x%04x\n",
            label, pid, static_cast<unsigned int>(processMachine), static_cast<unsigned int>(nativeMachine));
    }
    else
    {
        std::wprintf(L"%ls pid=%lu architecture-error=%lu\n", label, pid, GetLastError());
    }
    HANDLE token = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token))
    {
        std::wprintf(L"%ls pid=%lu session=%lu token-error=%lu\n", label, pid, session, GetLastError());
        CloseHandle(process);
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

static void ProbeWindow(const wchar_t* label, HWND window)
{
    if (!window)
    {
        std::wprintf(L"%ls window-not-found\n", label);
        return;
    }
    DWORD pid = 0;
    DWORD thread = GetWindowThreadProcessId(window, &pid);
    DWORD_PTR result = 0;
    ULONGLONG started = GetTickCount64();
    SetLastError(ERROR_SUCCESS);
    LRESULT delivered = SendMessageTimeoutW(window, WM_NULL, 0, 0,
        SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT, 2000, &result);
    DWORD error = delivered ? ERROR_SUCCESS : GetLastError();
    // Delivery is the return value, not the WM_NULL result (normally zero).
    // Responsiveness does not establish that the notification area accepts icons.
    std::wprintf(L"%ls hwnd=%p pid=%lu thread=%lu WM_NULL-delivered=%d result=%llu error=%lu elapsed-ms=%llu\n",
        label, static_cast<void*>(window), pid, thread, delivered != 0,
        static_cast<unsigned long long>(result), error,
        static_cast<unsigned long long>(GetTickCount64() - started));
}

static bool ProbeRegistration(HWND window, HICON icon, UINT id, UINT flags)
{
    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = window;
    data.uID = id;
    data.uFlags = flags;
    data.uCallbackMessage = WM_APP + 1;
    data.hIcon = icon;
    ULONGLONG started = GetTickCount64();
    BOOL added = Shell_NotifyIconW(NIM_ADD, &data);
    // Shell_NotifyIconW does not document GetLastError as a failure contract.
    std::wprintf(L"SDK stock icon NIM_ADD=%d flags=0x%x id=%u elapsed-ms=%llu\n",
        added, flags, id, static_cast<unsigned long long>(GetTickCount64() - started));
    if (added)
    {
        BOOL deleted = Shell_NotifyIconW(NIM_DELETE, &data);
        std::wprintf(L"SDK stock icon NIM_DELETE=%d id=%u\n", deleted, id);
        return deleted != FALSE;
    }
    // Registration rejection is evidence, not a replacement for the mandatory product smoke.
    return true;
}

int wmain()
{
#if defined(_M_ARM64)
    std::wprintf(L"SDK control compiled-target=arm64\n");
#elif defined(_M_X64)
    std::wprintf(L"SDK control compiled-target=x64\n");
#else
#error Unsupported SDK control architecture
#endif
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
    ProbeWindow(L"Taskbar before registration", taskbar);
    // TrayNotifyWnd is an implementation detail: absence is diagnostic, not a skip condition.
    HWND notificationArea = taskbar ? FindWindowExW(taskbar, nullptr, L"TrayNotifyWnd", nullptr) : nullptr;
    ProbeWindow(L"Notification area", notificationArea);

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
    HICON icon = LoadIconW(nullptr, IDI_APPLICATION);
    if (!icon)
    {
        std::wprintf(L"SDK LoadIconW failed: %lu\n", GetLastError());
        DestroyWindow(window);
        UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);
        return 1;
    }
    bool cleanup = ProbeRegistration(window, icon, 2, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
    // Isolate NIF_SHOWTIP handling before NIM_SETVERSION, leaving the empty tip and
    // all other inputs unchanged. Each probe owns a distinct icon ID.
    cleanup = ProbeRegistration(window, icon, 3, NIF_MESSAGE | NIF_ICON | NIF_TIP) && cleanup;
    ProbeWindow(L"Taskbar after registration", FindWindowW(L"Shell_TrayWnd", nullptr));
    cleanup = DestroyWindow(window) && cleanup;
    cleanup = UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance) && cleanup;
    return cleanup ? 0 : 1;
}
