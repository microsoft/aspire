// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private const int StartupCheckboxId = 2001;
    private const int StartupRefreshId = 2002;
    private const int SettingsDocumentationId = 2003;
    private const int SettingsGeneralId = 2004;
    private const int SettingsAboutId = 2005;
    private const int SettingsExplanationId = 2006;
    private const int SettingsCloseId = 2; // IDCANCEL also handles the dialog's Escape key.
    private const string SettingsMenuLabel = "Settings...\tCtrl+,";
    private nint _settingsWindow;
    private nint _settingsCheckbox;
    private nint _settingsStatus;
    private nint _settingsVersion;
    private nint _settingsRefresh;
    private nint _settingsDocumentation;
    private nint _settingsIcon;
    private nint _settingsMenuFilter;
    private TrayStartupState? _startupState;
    private bool _settingsInitializing;
    private bool _settingsShortcutPending;

    private static string AboutVersionText
    {
        get
        {
            var assembly = typeof(TrayApplication).Assembly;
            var version = assembly.GetName().Version?.ToString() ?? "Development build";
            var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
            return $"Aspire Tray\r\nVersion: {version}\r\nBuild: {build}";
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow != 0)
        {
            ReadStartupSettings();
            FocusSettings();
            return;
        }

        _settingsInitializing = true;
        try
        {
            var template = CreateSettingsTemplate();
            fixed (byte* pointer = template)
            {
                var window = NativeMethods.CreateDialogIndirectParam(_module, pointer, _window, &SettingsDialogProcedure, 0);
                NativeCallException.Require(window != 0, "CreateDialogIndirectParamW(Settings)");
                _settingsWindow = window;
            }
            _settingsIcon = NativeMethods.CopyIcon(_artwork!.Original);
            NativeCallException.Require(_settingsIcon != 0, "CopyIcon(Settings)");
            NativeMethods.SendMessage(_settingsWindow, 0x80, 0, _settingsIcon); // WM_SETICON, ICON_SMALL.
            NativeMethods.SendMessage(_settingsWindow, 0x80, 1, _settingsIcon);

            AddSettingsControl("BUTTON", "General", 0x7, SettingsGeneralId, 8, 8, 354, 161);
            // BS_3STATE (not AUTO3STATE) exposes an accessible checkbox, but only confirmed
            // backend state changes its check mark. A failed write never looks successful.
            _settingsCheckbox = AddSettingsControl("BUTTON", "&Launch Aspire Tray when I sign in",
                0x10000 | 0x5, StartupCheckboxId, 20, 24, 329, 16);
            AddSettingsControl("STATIC", "Only Aspire Tray launches at sign-in. AppHosts are not started.",
                0, SettingsExplanationId, 20, 45, 329, 24);
            AddSettingsControl("STATIC", "Startup status:", 0, 0, 20, 72, 329, 10);
            _settingsStatus = AddSettingsControl("EDIT", "", 0x10000 | 0x4 | 0x40 | 0x800 | 0x200000,
                0, 20, 84, 329, 46);
            _settingsRefresh = AddSettingsControl("BUTTON", "&Refresh startup status", 0x10000,
                StartupRefreshId, 20, 137, 112, 18);
            AddSettingsControl("BUTTON", "About", 0x7, SettingsAboutId, 8, 177, 354, 79);
            _settingsVersion = AddSettingsControl("EDIT", AboutVersionText,
                0x10000 | 0x4 | 0x40 | 0x800 | 0x200000, 0, 20, 193, 329, 32);
            _settingsDocumentation = AddSettingsControl("BUTTON", "Open &Documentation (aspire.dev)",
                0x10000, SettingsDocumentationId, 20, 231, 172, 18);
            AddSettingsControl("BUTTON", "&Close", 0x10000 | 0x1, SettingsCloseId, 298, 265, 64, 18);
            NativeMethods.SendMessage(_settingsWindow, 0x401, SettingsCloseId, 0); // DM_SETDEFID.
            ReadStartupSettings();
            FocusSettings();
        }
        catch
        {
            CloseSettings();
            throw;
        }
        finally
        {
            _settingsInitializing = false;
        }
    }

    private static byte[] CreateSettingsTemplate()
    {
        // Standard DLGTEMPLATE: DWORD style/exstyle, WORD count, four SHORT bounds,
        // then zero menu/class, UTF-16 title, WORD point size and UTF-16 font.
        // No controls are embedded; CreateWindowEx adds native controls in tab order.
        // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-dlgtemplate
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(0x80000000u | 0x00C00000u | 0x00080000u | 0x02000000u | 0x80u | 0x40u | 0x800u);
        writer.Write(0x00010000u); // WS_EX_CONTROLPARENT.
        writer.Write((ushort)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)370);
        writer.Write((short)292);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(Encoding.Unicode.GetBytes("Aspire Settings\0"));
        writer.Write((ushort)9);
        writer.Write(Encoding.Unicode.GetBytes("Segoe UI\0"));
        writer.Flush();
        return stream.ToArray();
    }

    private nint AddSettingsControl(string className, string text, uint style, int id, int x, int y, int width, int height)
    {
        var rect = new NativeMethods.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        NativeCallException.Require(NativeMethods.MapDialogRect(_settingsWindow, ref rect) != 0, "MapDialogRect(Settings)");
        var control = NativeMethods.CreateWindowEx(0, className, text, 0x40000000 | 0x10000000 | style,
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, _settingsWindow, id, _module, 0);
        NativeCallException.Require(control != 0, $"CreateWindowExW(Settings {className})");
        var font = NativeMethods.SendMessage(_settingsWindow, 0x31, 0, 0); // WM_GETFONT; owned by the dialog manager.
        NativeCallException.Require(font != 0, "WM_GETFONT(Settings)");
        NativeMethods.SendMessage(control, 0x30, (nuint)font, 1); // WM_SETFONT.
        return control;
    }

    private void FocusSettings()
    {
        NativeMethods.ShowWindow(_settingsWindow, 9); // SW_RESTORE, including minimized windows.
        NativeCallException.Require(NativeMethods.SetForegroundWindow(_settingsWindow) != 0, "SetForegroundWindow(Settings)");
        var focus = NativeMethods.GetFocus();
        if (NativeMethods.IsChild(_settingsWindow, focus) == 0 || NativeMethods.IsWindowEnabled(focus) == 0)
        {
            var target = NativeMethods.IsWindowEnabled(_settingsCheckbox) != 0 ? _settingsCheckbox : _settingsRefresh;
            NativeMethods.SetFocus(target);
            NativeCallException.Require(NativeMethods.GetFocus() == target, "SetFocus(Settings)");
        }
    }

    private void ReadStartupSettings(string? writeError = null)
    {
        TrayStartupState state;
        try
        {
            state = startupSettings.Read();
        }
        catch (Exception ex)
        {
            _startupState = null;
            NativeMethods.SendMessage(_settingsCheckbox, NativeMethods.BmSetCheck, 2, 0);
            EnableStartupCheckbox(false);
            var message = $"Startup state is unknown. Could not read the sign-in setting: {ex.Message}\r\nSelect Refresh startup status to retry.";
            SetSettingsStatus(writeError is null ? message : $"{writeError}\r\n{message}");
            Program.Log($"Reading tray startup settings failed ({ex.GetType().Name}): {ex.Message}");
            return;
        }
        ApplyStartupState(state, writeError);
    }

    private void ApplyStartupState(TrayStartupState state, string? error)
    {
        _startupState = state;
        NativeMethods.SendMessage(_settingsCheckbox, NativeMethods.BmSetCheck, state.Enabled ? 1u : 0u, 0);
        EnableStartupCheckbox(state.Enabled || state.CanEnable);
        var status = state.Enabled ? "Launch at sign-in is on." : "Launch at sign-in is off.";
        if (!state.Enabled && !state.CanEnable)
        {
            status += " Enabling launch at sign-in is unavailable.";
        }
        if (!string.IsNullOrWhiteSpace(state.Detail))
        {
            status += $"\r\n{state.Detail}";
        }
        SetSettingsStatus(error is null ? status : $"{error}\r\n{status}");
    }

    private void SetSettingsStatus(string message)
        => NativeCallException.Require(NativeMethods.SetWindowText(_settingsStatus, message) != 0, "SetWindowTextW(Settings status)");

    private void EnableStartupCheckbox(bool enabled)
    {
        var moveFocus = !enabled && NativeMethods.GetFocus() == _settingsCheckbox;
        NativeMethods.EnableWindow(_settingsCheckbox, enabled ? 1 : 0);
        if (moveFocus)
        {
            NativeMethods.SetFocus(_settingsRefresh);
        }
    }

    private void ChangeStartupSetting()
    {
        if (_settingsInitializing || _startupState is null)
        {
            return;
        }
        // Re-read before user-initiated mutation: registration can change outside this window.
        ReadStartupSettings();
        if (_startupState is not { } current || (!current.Enabled && !current.CanEnable))
        {
            return;
        }
        TrayStartupState updated;
        try
        {
            updated = startupSettings.SetEnabled(!current.Enabled);
        }
        catch (Exception ex)
        {
            var message = $"Could not change launch at sign-in: {ex.Message}\r\nReview the status below, then retry.";
            Program.Log($"Changing tray startup settings failed ({ex.GetType().Name}): {ex.Message}");
            // SetEnabled may have partially completed before failing. Re-read reality instead
            // of restoring the old checkbox or treating the requested value as successful.
            ReadStartupSettings(message);
            return;
        }
        ApplyStartupState(updated, null);
    }

    private void CloseSettings()
    {
        if (_settingsWindow != 0)
        {
            NativeCallException.Require(NativeMethods.DestroyWindow(_settingsWindow) != 0, "DestroyWindow(Settings)");
        }
    }

    private bool DisposeSettings()
    {
        CloseSettings();
        if (_settingsMenuFilter != 0)
        {
            if (NativeMethods.UnhookWindowsHookEx(_settingsMenuFilter) == 0)
            {
                Cleanup(false, "UnhookWindowsHookEx(Settings)");
                return false;
            }
            _settingsMenuFilter = 0;
        }
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SettingsDialogProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } application)
            {
                return application.HandleSettingsMessage(window, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            if (s_current is { } application)
            {
                application._callbackFailure ??= ex;
                application.ExitCode = 1;
                Program.Log($"Settings callback failed: {ex.Message}");
                application.QuitOnLoop();
            }
        }
        return 0;
    }

    private nint HandleSettingsMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.WmInitDialog:
                _settingsWindow = window;
                return 0;
            case NativeMethods.WmCommand when !_settingsInitializing && !_quitRequested:
                var id = (int)(wParam & 0xFFFF);
                var notification = (uint)((wParam >> 16) & 0xFFFF);
                if (id == SettingsCloseId)
                {
                    CloseSettings();
                    return 1;
                }
                if (notification != 0) // BN_CLICKED.
                {
                    return 0;
                }
                if (id == StartupCheckboxId && lParam == _settingsCheckbox)
                {
                    ChangeStartupSetting();
                    return 1;
                }
                if (id == StartupRefreshId && lParam == _settingsRefresh)
                {
                    ReadStartupSettings();
                    return 1;
                }
                if (id == SettingsDocumentationId && lParam == _settingsDocumentation)
                {
                    Dispatch(new(ActionKind.Documentation));
                    return 1;
                }
                return 0;
            case NativeMethods.WmClose:
                CloseSettings();
                return 1;
            case NativeMethods.WmNcDestroy:
                _settingsWindow = 0;
                _settingsCheckbox = 0;
                _settingsStatus = 0;
                _settingsVersion = 0;
                _settingsRefresh = 0;
                _settingsDocumentation = 0;
                _startupState = null;
                if (_settingsIcon != 0)
                {
                    Cleanup(NativeMethods.DestroyIcon(_settingsIcon) != 0, "DestroyIcon(Settings)");
                    _settingsIcon = 0;
                }
                return 0;
            default:
                return 0;
        }
    }

    private static void SetSettingsMenuLabel(nint menu, uint command)
    {
        // Only this fixed label has an accelerator tab; dynamic labels still use Literal.
        fixed (char* text = SettingsMenuLabel)
        {
            var info = new NativeMethods.MenuItemInfo
            {
                Size = (uint)sizeof(NativeMethods.MenuItemInfo), Mask = NativeMethods.MiimString, Text = text
            };
            NativeCallException.Require(NativeMethods.SetMenuItemInfo(menu, command, 0, ref info) != 0, "SetMenuItemInfoW(Settings shortcut)");
        }
    }

    private void InstallSettingsMenuFilter()
    {
        // Thread-local WH_MSGFILTER sees menu-loop keystrokes that never reach Run's pump.
        // This is not a global keyboard hook or hotkey and observes only this UI thread.
        _settingsMenuFilter = NativeMethods.SetWindowsHookEx(-1, &SettingsMenuFilter, 0, NativeMethods.GetCurrentThreadId());
        NativeCallException.Require(_settingsMenuFilter != 0, "SetWindowsHookExW(Settings menu shortcut)");
    }

    private bool HandleSettingsShortcut(in NativeMethods.Message message)
    {
        var localFocus = _menuOpen || message.Window == _window || (_settingsWindow != 0
            && (message.Window == _settingsWindow || NativeMethods.IsChild(_settingsWindow, message.Window) != 0));
        if (_quitRequested || _modalDepth != 0 || !localFocus || message.Id != NativeMethods.WmKeyDown || message.WParam != 0xBC
            || NativeMethods.GetKeyState(0x11) >= 0 || NativeMethods.GetKeyState(0x12) < 0)
        {
            return false;
        }
        if (_settingsShortcutPending)
        {
            return true;
        }
        _settingsShortcutPending = true;
        if (_menuOpen)
        {
            NativeCallException.Require(NativeMethods.EndMenu() != 0, "EndMenu(Settings shortcut)");
        }
        NativeCallException.Require(NativeMethods.PostMessage(_window, NativeMethods.SettingsMessage, 0, 0) != 0, "PostMessageW(Settings shortcut)");
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SettingsMenuFilter(int code, nuint wParam, nint lParam)
    {
        try
        {
            if (code == 2 && s_current is { _menuOpen: true } application
                && application.HandleSettingsShortcut(in *(NativeMethods.Message*)lParam))
            {
                return 1;
            }
        }
        catch (Exception ex)
        {
            if (s_current is { } application)
            {
                application._callbackFailure ??= ex;
                application.ExitCode = 1;
                Program.Log($"Settings shortcut callback failed: {ex.Message}");
                application.QuitOnLoop();
            }
            return 1;
        }
        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }
}
