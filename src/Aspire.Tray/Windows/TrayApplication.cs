// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aspire.Tray;

[SupportedOSPlatform("windows")]
internal sealed unsafe partial class TrayApplication(TrayController controller, int? smokeSeconds, ITrayStartupSettings startupSettings) : IDisposable
{
    private const string ClassName = "Aspire.Tray.MessageWindow";
    private const nuint TimerId = 1;
    private static TrayApplication? s_current;
    private readonly object _lifecycleGate = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TaskCompletionSource> _restoreRequests = [];
    private nint _module;
    private nint _window;
    private nint _previousDpiContext;
    private uint _taskbarCreated;
    private bool _classRegistered;
    private bool _iconAdded;
    private bool _timerAdded;
    private bool _menuOpen;
    private bool _quitRequested;
    private bool _shutdownRequested;
    private bool _loopEnded;
    private bool _disposed;
    private int _modalDepth;
    private nint _modalOwner;
    private int _dispatchDepth;
    private int _refreshPosted;
    private int _restoreAttempts;
    private long? _smokeDeadline;
    private NativeMethods.NotifyIconData _iconData;
    private NativeMenu? _menu;
    private TrayViewState? _displayedState;
    private Exception? _callbackFailure;
    private Artwork? _artwork;
    private IconState? _iconState;
    private bool _dpiDirty;
    private bool _interactiveSmoke;

    internal int ExitCode { get; private set; }
    internal Action? SmokeTick { get; set; }
    internal Func<string, string, uint, bool>? ConfirmForSmoke { get; set; }
    internal Action<Uri>? OpenUrlForSmoke { get; set; }
    internal Action<string>? CopyPathForSmoke { get; set; }
    internal Action<Exception>? ErrorForSmoke { get; set; }

    internal Task WaitUntilReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    internal Task RestoreIconAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleGate)
        {
            if (_shutdownRequested || _loopEnded || _disposed)
            {
                return Task.FromException(new InvalidOperationException("The tray is shutting down."));
            }
            _restoreRequests.Add(completion);
            if (_window != 0 && NativeMethods.PostMessage(_window, NativeMethods.RestoreMessage, 0, 0) == 0)
            {
                _restoreRequests.Remove(completion);
                completion.SetException(new NativeCallException("PostMessageW(restore)", Marshal.GetLastPInvokeError()));
            }
        }
        return completion.Task.WaitAsync(cancellationToken);
    }

    internal void RequestQuit()
    {
        lock (_lifecycleGate)
        {
            _shutdownRequested = true;
            if (_window != 0 && !_loopEnded && NativeMethods.PostMessage(_window, NativeMethods.QuitMessage, 0, 0) == 0)
            {
                // The timer also observes this flag if posting fails. Never post WM_QUIT on
                // an activation worker: it belongs to the native loop's owning thread.
                _callbackFailure ??= new NativeCallException("PostMessageW(quit)", Marshal.GetLastPInvokeError());
                Program.Log(_callbackFailure.Message);
            }
        }
    }

    internal void Run()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
            || sizeof(NativeMethods.WindowClass) != 72 || sizeof(NativeMethods.Message) != 48
            || sizeof(NativeMethods.NotifyIconData) != 976 || sizeof(NativeMethods.MenuItemInfo) != 80
            || sizeof(NativeMethods.IconInfo) != 32 || sizeof(NativeMethods.BitmapInfo) != 44
            || sizeof(NativeMethods.ToolInfo) != 72)
        {
            throw new PlatformNotSupportedException("This frontend requires the Windows x64 or ARM64 native layouts.");
        }
        if (s_current is not null)
        {
            throw new InvalidOperationException("A native tray message loop is already active.");
        }
        // Keep the receiver rooted until DestroyWindow and UnregisterClass stop callbacks.
        s_current = this;
        try
        {
            Initialize();
            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, 0, 0, 0);
                NativeCallException.Require(result != -1, "GetMessageW");
                if (result == 0)
                {
                    break;
                }
                if (HandleSettingsShortcut(in message)
                    || (_settingsWindow != 0 && NativeMethods.IsDialogMessage(_settingsWindow, ref message) != 0))
                {
                    continue;
                }
                NativeMethods.TranslateMessage(in message);
                NativeMethods.DispatchMessage(in message);
            }
            if (_callbackFailure is not null)
            {
                throw _callbackFailure;
            }
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _loopEnded = true;
                _ready.TrySetException(new InvalidOperationException("The tray message loop ended."));
                FailRestoreRequests(new InvalidOperationException("The tray message loop ended."));
            }
        }
    }

    private void Initialize()
    {
        // The embedded Common Controls v6 manifest opts both managed and NativeAOT
        // executables into themed controls; loading the standard classes activates them.
        // https://learn.microsoft.com/windows/win32/controls/cookbook-overview
        var controls = new NativeMethods.CommonControls
        {
            Size = (uint)sizeof(NativeMethods.CommonControls),
            Classes = 0x4000 | 0x4 // ICC_STANDARD_CLASSES | ICC_BAR_CLASSES (tooltips).
        };
        NativeCallException.Require(NativeMethods.InitCommonControlsEx(in controls) != 0, "InitCommonControlsEx");
        _previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(-4);
        NativeCallException.Require(_previousDpiContext != 0, "SetThreadDpiAwarenessContext");
        _module = NativeMethods.GetModuleHandle(null);
        NativeCallException.Require(_module != 0, "GetModuleHandleW");
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        NativeCallException.Require(_taskbarCreated != 0, "RegisterWindowMessageW");
        fixed (char* name = ClassName)
        {
            var windowClass = new NativeMethods.WindowClass
            {
                WindowProcedure = &WindowProcedure, Instance = _module, ClassName = name
            };
            NativeCallException.Require(NativeMethods.RegisterClass(ref windowClass) != 0, "RegisterClassW");
        }
        _classRegistered = true;
        // HWND_MESSAGE would not receive Explorer's TaskbarCreated broadcast.
        var window = NativeMethods.CreateWindowEx(0, ClassName, "Aspire Tray", 0, 0, 0, 0, 0, 0, 0, _module, 0);
        NativeCallException.Require(window != 0, "CreateWindowExW");
        lock (_lifecycleGate)
        {
            _window = window;
        }
        _iconData = new NativeMethods.NotifyIconData
        {
            Size = (uint)sizeof(NativeMethods.NotifyIconData), Window = window, Id = 1,
            Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip | NativeMethods.NifShowTip,
            CallbackMessage = NativeMethods.TrayCallback
        };
        _installedApplications = FolderApplication.Discover(message =>
        {
            Program.Log(message);
            controller.ReportActionError(message);
        });
        RefreshMenu();
        if (smokeSeconds is not null)
        {
            ProbeRegistrationForSmoke();
        }
        if (!TryAddIcon())
        {
            // An Explorer window can exist before its notification area accepts icons.
            // Retry on the UI timer, just as for TaskbarCreated, without acknowledging readiness.
            Program.Log("Windows notification area is not ready. Retrying tray icon creation.");
        }
        _timerAdded = NativeMethods.SetTimer(window, TimerId, 200, 0) != 0;
        NativeCallException.Require(_timerAdded, "SetTimer");
        InstallSettingsMenuFilter();
        _smokeDeadline = smokeSeconds is int seconds ? Environment.TickCount64 + seconds * 1000L : null;
        controller.Changed += RequestRefresh;
        // Allocation is not readiness: this message must actually pass through DispatchMessage.
        NativeCallException.Require(NativeMethods.PostMessage(window, NativeMethods.ReadyMessage, 0, 0) != 0, "PostMessageW(ready)");
    }

    private void RequestRefresh()
    {
        lock (_lifecycleGate)
        {
            if (_window == 0 || _shutdownRequested || _loopEnded || _disposed || Interlocked.Exchange(ref _refreshPosted, 1) != 0)
            {
                return;
            }
            if (NativeMethods.PostMessage(_window, NativeMethods.RefreshMessage, 0, 0) == 0)
            {
                Interlocked.Exchange(ref _refreshPosted, 0);
                _callbackFailure ??= new NativeCallException("PostMessageW(refresh)", Marshal.GetLastPInvokeError());
                _shutdownRequested = true;
                Program.Log(_callbackFailure.Message);
            }
        }
    }

    private bool TryAddIcon()
    {
        if (smokeSeconds is not null && IconAddFailuresForSmoke > 0)
        {
            IconAddFailuresForSmoke--;
            return false;
        }
        // Shell_NotifyIcon does not promise a last-error value.
        if (NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref _iconData) == 0)
        {
            if (smokeSeconds is not null && _restoreAttempts is 0 or 49)
            {
                Program.Log($"Shell_NotifyIconW(NIM_ADD) rejected the smoke icon: architecture={RuntimeInformation.ProcessArchitecture}, size={_iconData.Size}, attempt={_restoreAttempts + 1}.");
            }
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

    private void RestoreOnLoop()
    {
        lock (_lifecycleGate)
        {
            if (_shutdownRequested)
            {
                FailRestoreRequests(new InvalidOperationException("The tray is shutting down."));
                return;
            }
        }
        RefreshMenu();
        if (!_iconAdded && !TryAddIcon())
        {
            // Explorer may broadcast while its notification area is still initializing.
            return;
        }
        if (NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref _iconData) == 0)
        {
            _iconAdded = false;
            _restoreAttempts = 0;
            return;
        }
        lock (_lifecycleGate)
        {
            if (_shutdownRequested)
            {
                FailRestoreRequests(new InvalidOperationException("The tray is shutting down."));
                return;
            }
            _ready.TrySetResult();
            foreach (var request in _restoreRequests)
            {
                request.TrySetResult();
            }
            _restoreRequests.Clear();
        }
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
            // Managed exceptions must not cross user32 callback frames, including nested menus.
            if (s_current is { } application)
            {
                application._callbackFailure ??= ex;
                application.ExitCode = 1;
                Program.Log($"Native callback failed: {ex.Message}");
                application.QuitOnLoop();
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
            RestoreOnLoop();
            return 0;
        }
        switch (message)
        {
            case NativeMethods.ReadyMessage:
                lock (_lifecycleGate)
                {
                    if (_shutdownRequested)
                    {
                        QuitOnLoop();
                        return 0;
                    }
                }
                RestoreOnLoop();
                return 0;
            case NativeMethods.RefreshMessage:
                Interlocked.Exchange(ref _refreshPosted, 0);
                if (!_quitRequested)
                {
                    RefreshMenu();
                }
                return 0;
            case NativeMethods.RestoreMessage:
                RestoreOnLoop();
                return 0;
            case NativeMethods.QuitMessage:
                QuitOnLoop();
                return 0;
            case NativeMethods.SettingsMessage when !_quitRequested:
                _settingsShortcutPending = false;
                Dispatch(new(ActionKind.Settings));
                return 0;
            case NativeMethods.SmokeMessage when smokeSeconds is not null:
                Dispatch(_smokeActions[checked((int)wParam)]);
                return 0;
            case NativeMethods.WmTimer when wParam == TimerId:
                OnTimer();
                return 0;
            case NativeMethods.WmDpiChanged:
            case NativeMethods.WmSettingChange:
            case NativeMethods.WmSysColorChange:
            case NativeMethods.WmThemeChanged:
                _dpiDirty = true;
                RequestRefresh();
                return 0;
            case NativeMethods.WmMenuRightButtonUp when _menuOpen && _modalDepth == 0:
                PinContextRow(lParam, (uint)wParam);
                return 0;
            case NativeMethods.WmMenuSelect:
                SelectMenuTooltip(wParam, lParam);
                return 0;
            case NativeMethods.TrayCallback when !_menuOpen && !_quitRequested && _modalDepth == 0:
                // NIM_SETVERSION(4): LOWORD(lParam)=event; wParam holds signed screen coordinates.
                var notification = (uint)((nuint)lParam & 0xFFFF);
                if (notification is NativeMethods.WmContextMenu or NativeMethods.NinSelect or NativeMethods.NinKeySelect)
                {
                    ShowMenu(wParam);
                }
                return 0;
            case NativeMethods.WmClose:
            case NativeMethods.WmDestroy:
            case NativeMethods.WmEndSession when wParam != 0:
                QuitOnLoop();
                return 0;
            default:
                return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void OnTimer()
    {
        lock (_lifecycleGate)
        {
            if (_shutdownRequested)
            {
                QuitOnLoop();
                return;
            }
        }
        if (!_iconAdded)
        {
            RestoreOnLoop();
            if (!_iconAdded && ++_restoreAttempts >= 50)
            {
                throw new NativeCallException("Shell_NotifyIconW(Explorer recovery)");
            }
        }
        RefreshMenu();
        UpdateMenuTooltip();
        if (smokeSeconds is not null && _modalDepth != 0)
        {
            CompleteDialogForSmoke();
        }
        if (_ready.Task.IsCompletedSuccessfully && _modalDepth == 0)
        {
            SmokeTick?.Invoke();
        }
        if (!_quitRequested && _smokeDeadline is long deadline && Environment.TickCount64 >= deadline)
        {
            throw new TimeoutException("The Windows native smoke assertions did not finish before the deadline.");
        }
    }

    private void QuitOnLoop()
    {
        if (_quitRequested)
        {
            return;
        }
        _quitRequested = true;
        HideMenuTooltip();
        lock (_lifecycleGate)
        {
            _shutdownRequested = true;
            FailRestoreRequests(new InvalidOperationException("The tray is shutting down."));
        }
        if (_menuOpen)
        {
            Cleanup(NativeMethods.EndMenu() != 0, "EndMenu");
        }
        if (_modalDepth != 0)
        {
            // WM_QUIT alone does not dismiss an owned MessageBox reliably. Close its popup
            // as Cancel before ending the main loop; never target another application's UI.
            var owner = _modalOwner != 0 ? _modalOwner : _window;
            var popup = GetEnabledPopup(owner);
            if (popup != 0 && popup != owner)
            {
                Cleanup(NativeMethods.PostMessage(popup, NativeMethods.WmClose, 0, 0) != 0, "PostMessageW(close dialog)");
            }
        }
        NativeMethods.PostQuitMessage(ExitCode);
    }

    private static nint GetEnabledPopup(nint owner)
    {
        // A dialog need not have received focus, particularly on a CI desktop.
        // GW_ENABLEDPOPUP finds our owned popup without relying on activation history.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindow
        return NativeMethods.GetWindow(owner, NativeMethods.GwEnabledPopup);
    }

    private void FailRestoreRequests(Exception exception)
    {
        foreach (var request in _restoreRequests)
        {
            request.TrySetException(exception);
        }
        _restoreRequests.Clear();
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
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _shutdownRequested = true;
            _ready.TrySetException(new ObjectDisposedException(nameof(TrayApplication)));
            FailRestoreRequests(new ObjectDisposedException(nameof(TrayApplication)));
            controller.Changed -= RequestRefresh;
            if (_timerAdded)
            {
                Cleanup(NativeMethods.KillTimer(_window, TimerId) != 0, "KillTimer");
            }
            if (_iconAdded)
            {
                Cleanup(NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref _iconData) != 0, "Shell_NotifyIconW(NIM_DELETE)");
            }
            if (!DisposeMenuTooltip())
            {
                return;
            }
            _menu?.Dispose();
            if (!DisposeSettings())
            {
                return;
            }
            if (_window != 0)
            {
                // Keep the static receiver if destruction fails; native code could still call it.
                if (NativeMethods.DestroyWindow(_window) == 0)
                {
                    Cleanup(false, "DestroyWindow");
                    return;
                }
                _window = 0;
            }
            if (_classRegistered && NativeMethods.UnregisterClass(ClassName, _module) == 0)
            {
                Cleanup(false, "UnregisterClassW");
                return;
            }
            _artwork?.Dispose();
            if (_previousDpiContext != 0)
            {
                Cleanup(NativeMethods.SetThreadDpiAwarenessContext(_previousDpiContext) != 0, "SetThreadDpiAwarenessContext(restore)");
            }
            if (ReferenceEquals(s_current, this))
            {
                s_current = null;
            }
        }
    }
}
