// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aspire.Tray.Spike;

[SupportedOSPlatform("windows")]
internal sealed unsafe class TrayApplication(TrayController controller, int? smokeSeconds) : IDisposable
{
    private const string ClassName = "Aspire.Tray.Spike.MessageWindow";
    private const nuint TimerId = 1;
    private const uint QuitCommand = 1;
    private const uint FirstDashboardCommand = 100;
    private static TrayApplication? s_current;
    private nint _module;
    private nint _window;
    private nint _icon;
    private uint _taskbarCreated;
    private bool _classRegistered;
    private bool _iconAdded;
    private bool _timerAdded;
    private bool _menuOpen;
    private bool _quitRequested;
    private bool _disposed;
    private int _restoreAttempts;
    private long? _smokeDeadline;
    private NativeMethods.NotifyIconData _iconData;
    private NativeMenu? _menu;
    private TrayViewState? _displayedState;
    private Exception? _callbackFailure;
    private string? _actionStatus;

    internal int ExitCode { get; private set; }

    internal void Run()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
            || sizeof(NativeMethods.WindowClass) != 72
            || sizeof(NativeMethods.Message) != 48
            || sizeof(NativeMethods.NotifyIconData) != 976)
        {
            throw new PlatformNotSupportedException("This spike requires the Windows x64 or ARM64 native layouts.");
        }
        if (s_current is not null)
        {
            throw new InvalidOperationException("A native tray message loop is already active.");
        }

        // The static root anchors the managed receiver for the unmanaged function pointer.
        // It is cleared only after DestroyWindow and UnregisterClass stop native callbacks.
        s_current = this;
        _module = NativeMethods.GetModuleHandle(null);
        NativeCallException.Require(_module != 0, "GetModuleHandleW");
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        NativeCallException.Require(_taskbarCreated != 0, "RegisterWindowMessageW");
        _icon = NativeMethods.LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Aspire.ico"),
            NativeMethods.ImageIcon, NativeMethods.GetSystemMetrics(NativeMethods.SmCxSmallIcon),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCySmallIcon), NativeMethods.LrLoadFromFile);
        NativeCallException.Require(_icon != 0, "LoadImageW");

        fixed (char* name = ClassName)
        {
            var windowClass = new NativeMethods.WindowClass
            {
                WindowProcedure = &WindowProcedure,
                Instance = _module,
                Icon = _icon,
                ClassName = name
            };
            NativeCallException.Require(NativeMethods.RegisterClass(ref windowClass) != 0, "RegisterClassW");
        }
        _classRegistered = true;

        // A hidden top-level window (not HWND_MESSAGE) receives Explorer's TaskbarCreated broadcast.
        _window = NativeMethods.CreateWindowEx(0, ClassName, "Aspire tray spike", 0,
            0, 0, 0, 0, 0, 0, _module, 0);
        NativeCallException.Require(_window != 0, "CreateWindowExW");
        _iconData = new NativeMethods.NotifyIconData
        {
            Size = (uint)sizeof(NativeMethods.NotifyIconData),
            Window = _window,
            Id = 1,
            Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip | NativeMethods.NifShowTip,
            CallbackMessage = NativeMethods.TrayCallback,
            Icon = _icon
        };
        fixed (char* tip = _iconData.Tip)
        {
            "Aspire tray spike (read only)".AsSpan().CopyTo(new Span<char>(tip, 128));
        }
        if (!TryAddIcon())
        {
            throw new NativeCallException("Shell_NotifyIconW(NIM_ADD)");
        }

        RefreshMenu();
        _timerAdded = NativeMethods.SetTimer(_window, TimerId, 1000, 0) != 0;
        NativeCallException.Require(_timerAdded, "SetTimer");
        _smokeDeadline = smokeSeconds is int seconds ? Environment.TickCount64 + seconds * 1000L : null;
        Program.Log($"Windows tray ready: HWND=0x{_window:X}, HICON=0x{_icon:X}, HMENU=0x{_menu!.Handle:X}, smoke={smokeSeconds is not null}.");

        while (true)
        {
            var result = NativeMethods.GetMessage(out var message, 0, 0, 0);
            NativeCallException.Require(result != -1, "GetMessageW");
            if (result == 0)
            {
                break;
            }
            NativeMethods.TranslateMessage(in message);
            NativeMethods.DispatchMessage(in message);
        }
        if (_callbackFailure is not null)
        {
            throw _callbackFailure;
        }
    }

    private bool TryAddIcon()
    {
        // Shell_NotifyIcon does not promise a last-error value, so do not report a stale one.
        if (NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref _iconData) == 0)
        {
            return false;
        }
        _iconAdded = true;
        _iconData.Version = NativeMethods.NotifyIconVersion4;
        if (NativeMethods.ShellNotifyIcon(NativeMethods.NimSetVersion, ref _iconData) == 0)
        {
            throw new NativeCallException("Shell_NotifyIconW(NIM_SETVERSION)");
        }

        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } application)
            {
                return application.HandleMessage(window, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Managed exceptions must never unwind through user32's unmanaged callback frames.
            if (s_current is { } application)
            {
                application._callbackFailure ??= ex;
                application.ExitCode = 1;
                application.RequestQuit();
            }
            else
            {
                NativeMethods.PostQuitMessage(1);
            }
            return 0;
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            _iconAdded = false;
            _restoreAttempts = 0;
            Program.Log("Explorer restarted; restoring the notification icon.");
            return 0;
        }
        switch (message)
        {
            case NativeMethods.WmTimer when wParam == TimerId:
                OnTimer();
                return 0;
            case NativeMethods.TrayCallback when !_menuOpen && !_quitRequested:
                // Version 4 packs the event in LOWORD(lParam), and signed screen coordinates
                // in wParam; it is not the old wParam=icon ID, lParam=event protocol.
                var notification = (uint)((nuint)lParam & 0xFFFF);
                if (notification is NativeMethods.WmContextMenu or NativeMethods.NinSelect or NativeMethods.NinKeySelect)
                {
                    ShowMenu(wParam);
                }
                return 0;
            case NativeMethods.WmClose:
            case NativeMethods.WmDestroy:
            case NativeMethods.WmEndSession when wParam != 0:
                RequestQuit();
                return 0;
            default:
                return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void OnTimer()
    {
        if (_quitRequested)
        {
            return;
        }
        if (!_iconAdded)
        {
            if (TryAddIcon())
            {
                Program.Log("Notification icon restored after Explorer restart.");
            }
            else if (++_restoreAttempts >= 10)
            {
                throw new NativeCallException("Shell_NotifyIconW(NIM_ADD after Explorer restart)");
            }
        }

        // TrackPopupMenuEx pumps a nested native loop, including WM_TIMER. Never replace or
        // destroy its active HMENU; dashboard commands revalidate discovery after selection.
        if (!_menuOpen)
        {
            RefreshMenu();
        }
        if (_smokeDeadline is long deadline && Environment.TickCount64 >= deadline)
        {
            if (!_iconAdded)
            {
                ExitCode = 1;
                Program.Log("Notification icon restoration did not complete before the smoke deadline.");
            }
            var state = controller.State;
            Program.Log($"Windows tray smoke completed: AppHosts={state.AppHosts.Count}, discovery={state.Discovery}.");
            RequestQuit();
        }
    }

    private void RefreshMenu()
    {
        var state = controller.State;
        if (ReferenceEquals(state, _displayedState))
        {
            return;
        }

        var replacement = BuildMenu(state);
        _menu?.Dispose();
        _menu = replacement;
        _displayedState = state;
        Program.Log($"Tray state: AppHosts={state.AppHosts.Count}, discovery={state.Discovery}, status={state.Status}, HMENU=0x{replacement.Handle:X}.");
    }

    private NativeMenu BuildMenu(TrayViewState state)
    {
        var root = new NativeMenu(this);
        try
        {
            Append(root.Handle, NativeMethods.MfGrayed, 0, state.Status);
            if (_actionStatus is not null)
            {
                Append(root.Handle, NativeMethods.MfGrayed, 0, _actionStatus);
            }
            Append(root.Handle, NativeMethods.MfSeparator, 0, null);
            if (state.AppHosts.Count == 0)
            {
                Append(root.Handle, NativeMethods.MfGrayed, 0, state.Discovery == DiscoveryState.Live ? "No AppHosts running" : "No AppHosts received yet");
            }

            var command = FirstDashboardCommand;
            foreach (var host in state.AppHosts)
            {
                var submenu = NativeMethods.CreatePopupMenu();
                NativeCallException.Require(submenu != 0, "CreatePopupMenu");
                var attached = false;
                try
                {
                    Append(submenu, NativeMethods.MfGrayed, 0, $"Path: {host.Id.AppHostPath}");
                    Append(submenu, NativeMethods.MfGrayed, 0, $"PID: {host.Id.AppHostPid}");
                    Append(submenu, NativeMethods.MfSeparator, 0, null);
                    Append(submenu, host.CanOpenDashboard ? 0u : NativeMethods.MfGrayed,
                        command, "Open dashboard");
                    Append(root.Handle, NativeMethods.MfPopup, (nuint)submenu, $"{host.Title} - {host.Subtitle}");
                    attached = true;
                    root.DashboardCommands.Add(command++, host.Id);
                }
                finally
                {
                    // Once appended, ownership transfers to the root's recursive DestroyMenu.
                    if (!attached)
                    {
                        Cleanup(NativeMethods.DestroyMenu(submenu) != 0, "DestroyMenu(unattached submenu)");
                    }
                }
            }
            Append(root.Handle, NativeMethods.MfSeparator, 0, null);
            Append(root.Handle, 0, QuitCommand, "Quit");
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private static void Append(nint menu, uint flags, nuint id, string? text)
    {
        // Win32 menu text interprets '&' as a mnemonic and tabs as shortcut separators.
        // Paths such as C:\src\A&B\AppHost.cs must remain literal, not become accelerators.
        var literal = text?.Replace("&", "&&", StringComparison.Ordinal).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        NativeCallException.Require(NativeMethods.AppendMenu(menu, flags, id, literal) != 0, "AppendMenuW");
    }

    private void ShowMenu(nuint coordinates)
    {
        RefreshMenu();
        var menu = _menu!;
        var point = new NativeMethods.Point
        {
            X = unchecked((short)(coordinates & 0xFFFF)),
            Y = unchecked((short)((coordinates >> 16) & 0xFFFF))
        };
        if (point.X == -1 && point.Y == -1)
        {
            NativeCallException.Require(NativeMethods.GetCursorPos(out point) != 0, "GetCursorPos");
        }
        if (NativeMethods.SetForegroundWindow(_window) == 0)
        {
            throw new NativeCallException("SetForegroundWindow");
        }

        uint selected;
        _menuOpen = true;
        try
        {
            selected = NativeMethods.TrackPopupMenuEx(menu.Handle,
                NativeMethods.TpmReturnCmd | NativeMethods.TpmNonotify | NativeMethods.TpmRightButton,
                point.X, point.Y, _window, 0);
            var error = Marshal.GetLastPInvokeError();
            if (selected == 0 && error != 0)
            {
                throw new NativeCallException("TrackPopupMenuEx", error);
            }
        }
        finally
        {
            _menuOpen = false;
        }

        // Required for notification-area context menus to dismiss correctly on subsequent opens.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-trackpopupmenuex
        NativeCallException.Require(NativeMethods.PostMessage(_window, NativeMethods.WmNull, 0, 0) != 0, "PostMessageW");
        if (_quitRequested)
        {
            return;
        }
        if (selected == QuitCommand)
        {
            RequestQuit();
        }
        else if (menu.DashboardCommands.TryGetValue(selected, out var host))
        {
            OpenDashboard(host);
        }
    }

    private void OpenDashboard(AppHostId selected)
    {
        if (smokeSeconds is not null)
        {
            Program.Log("Dashboard launch suppressed during smoke.");
            return;
        }
        Uri? uri = null;
        try
        {
            uri = controller.GetDashboardUri(selected);
        }
        catch (InvalidOperationException ex)
        {
            _actionStatus = ex.Message;
        }
        if (uri is not null)
        {
            // Pass the validated http(s) URI as the file to the OS URL handler. No command
            // interpreter, interpolated shell command, or credentials in diagnostic output.
            var result = NativeMethods.ShellExecute(_window, "open", uri.AbsoluteUri, null, null, 1);
            if (result <= 32)
            {
                ExitCode = 1;
                _actionStatus = $"Could not open the dashboard (native error {result}).";
            }
            else
            {
                _actionStatus = null;
            }
        }
        if (_actionStatus is not null)
        {
            Program.Log(_actionStatus);
            if (NativeMethods.MessageBox(_window, _actionStatus, "Aspire tray spike", NativeMethods.MbIconError) == 0)
            {
                ExitCode = 1;
                Program.Log("MessageBoxW failed while displaying the dashboard error.");
            }
        }
        _displayedState = null;
        RefreshMenu();
    }

    private void RequestQuit()
    {
        _quitRequested = true;
        if (_menuOpen)
        {
            Cleanup(NativeMethods.EndMenu() != 0, "EndMenu");
        }
        NativeMethods.PostQuitMessage(ExitCode);
    }

    private void Cleanup(bool success, string operation)
    {
        if (!success)
        {
            ExitCode = 1;
            Program.Log($"{operation} failed during native cleanup.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_timerAdded)
        {
            Cleanup(NativeMethods.KillTimer(_window, TimerId) != 0, "KillTimer");
        }
        if (_iconAdded)
        {
            Cleanup(NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref _iconData) != 0, "Shell_NotifyIconW(NIM_DELETE)");
        }
        _menu?.Dispose();
        if (_window != 0)
        {
            Cleanup(NativeMethods.DestroyWindow(_window) != 0, "DestroyWindow");
        }
        if (_classRegistered)
        {
            Cleanup(NativeMethods.UnregisterClass(ClassName, _module) != 0, "UnregisterClassW");
        }
        if (_icon != 0)
        {
            Cleanup(NativeMethods.DestroyIcon(_icon) != 0, "DestroyIcon");
        }
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
        Program.Log($"Windows tray cleanup complete: exit={ExitCode}.");
    }

    private sealed class NativeMenu : IDisposable
    {
        private readonly TrayApplication _owner;
        private bool _disposed;

        internal NativeMenu(TrayApplication owner)
        {
            _owner = owner;
            Handle = NativeMethods.CreatePopupMenu();
            NativeCallException.Require(Handle != 0, "CreatePopupMenu");
        }

        internal nint Handle { get; }
        internal Dictionary<uint, AppHostId> DashboardCommands { get; } = [];

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _owner.Cleanup(NativeMethods.DestroyMenu(Handle) != 0, "DestroyMenu");
            }
        }
    }
}
