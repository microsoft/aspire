// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private const int StartupCheckboxId = 2001;
    private const int StartupRefreshId = 2002;
    private const int SettingsGeneralId = 2004;
    private const int SettingsAboutId = 2005;
    private const int SettingsPreviewMenuId = 2007;
    private const int SettingsCloseId = 2; // IDCANCEL also handles the dialog's Escape key.
    private const string SettingsMenuLabel = "Settings...\tCtrl+,";
    private nint _settingsWindow;
    private nint _settingsCheckbox;
    private nint _settingsStartupDescription;
    private nint _settingsStatus;
    private nint _settingsVersion;
    private nint _settingsRefresh;
    private nint _settingsIcon;
    private nint _settingsMenuFilter;
    private nint _settingsGeneral;
    private nint _settingsGeneralSeparator;
    private nint _settingsAbout;
    private nint _settingsAboutSeparator;
    private nint _settingsLogo;
    private nint _settingsProductName;
    private nint _settingsAboutDescription;
    private nint _settingsFooterSeparator;
    private nint _settingsClose;
    private nint _settingsPreview;
    private string _settingsStatusText = "";
    private TrayStartupState? _startupState;
    private bool _settingsInitializing;
    private bool _settingsShortcutPending;

    private static string AboutVersionText => TraySettingsText.GetVersionText();
    private string AboutDescriptionText => TraySettingsText.GetAboutDescription(_interactiveSmoke);

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
            var template = CreateDialogTemplate(_interactiveSmoke ? TraySettingsText.PreviewTitle : TraySettingsText.Title, 304, 200, 9);
            fixed (byte* pointer = template)
            {
                var window = NativeMethods.CreateDialogIndirectParam(_module, pointer, _window, &SettingsDialogProcedure, 0);
                NativeCallException.Require(window != 0, "CreateDialogIndirectParamW(Settings)");
                _settingsWindow = window;
            }
            _settingsGeneral = AddSettingsControl("STATIC", TraySettingsText.General, 0x80, SettingsGeneralId);
            _settingsGeneralSeparator = AddSettingsControl("STATIC", "", 0x10, 0); // SS_ETCHEDHORZ.
            // BS_3STATE (not AUTO3STATE) exposes an accessible checkbox, but only confirmed
            // backend state changes its check mark. A failed write never looks successful.
            _settingsCheckbox = AddSettingsControl("BUTTON", "&" + TraySettingsText.StartupOption,
                0x10000 | 0x5, StartupCheckboxId);
            _settingsStartupDescription = AddSettingsControl("STATIC", TraySettingsText.StartupDescription, 0x80, 0);
            _settingsStatus = AddSettingsControl("STATIC", "", 0x80, 0);
            _settingsRefresh = AddSettingsControl("BUTTON", "&Refresh startup status", 0x10000,
                StartupRefreshId);
            _settingsAbout = AddSettingsControl("STATIC", TraySettingsText.About, 0x80, SettingsAboutId);
            _settingsAboutSeparator = AddSettingsControl("STATIC", "", 0x10, 0);
            _settingsLogo = AddSettingsControl("STATIC", "Aspire logo", 0x3 | 0x40, 0); // SS_ICON | SS_REALSIZECONTROL.
            _settingsProductName = AddSettingsControl("STATIC", "Aspire Tray", 0x80, 0);
            _settingsAboutDescription = AddSettingsControl("STATIC", AboutDescriptionText, 0x80, 0);
            _settingsVersion = AddSettingsControl("STATIC", AboutVersionText, 0x80, 0);
            if (_interactiveSmoke)
            {
                _settingsPreview = AddSettingsControl("BUTTON", "Preview tray &menu", 0x10000, SettingsPreviewMenuId);
            }
            _settingsFooterSeparator = AddSettingsControl("STATIC", "", 0x10, 0);
            _settingsClose = AddSettingsControl("BUTTON", "&Close", 0x10000 | 0x1, SettingsCloseId);
            NativeMethods.SendMessage(_settingsWindow, 0x401, SettingsCloseId, 0); // DM_SETDEFID.
            UpdateSettingsAppearance();
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

    private static byte[] CreateDialogTemplate(string title, short width, short height, ushort pointSize)
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
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(Encoding.Unicode.GetBytes(title + "\0"));
        writer.Write(pointSize);
        writer.Write(Encoding.Unicode.GetBytes("Segoe UI\0"));
        writer.Flush();
        return stream.ToArray();
    }

    private nint AddSettingsControl(string className, string text, uint style, int id)
        // BS_NOTIFY lets keyboard focus scroll native buttons into view on small displays.
        // LayoutSettings assigns the final DPI-scaled bounds after fonts and artwork exist.
        => AddDialogControl(_settingsWindow, className, text, style | (className == "BUTTON" ? 0x4000u : 0),
            id, 0, 0, 1, 1);

    private nint AddDialogControl(nint dialog, string className, string text, uint style, int id, int x, int y, int width, int height)
    {
        var rect = new NativeMethods.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        NativeCallException.Require(NativeMethods.MapDialogRect(dialog, ref rect) != 0, "MapDialogRect");
        var control = NativeMethods.CreateWindowEx(0, className, text, 0x40000000 | 0x10000000 | style,
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, dialog, id, _module, 0);
        NativeCallException.Require(control != 0, $"CreateWindowExW({className})");
        var font = NativeMethods.SendMessage(dialog, 0x31, 0, 0); // WM_GETFONT; owned by the dialog manager.
        NativeCallException.Require(font != 0, "WM_GETFONT");
        NativeMethods.SendMessage(control, 0x30, (nuint)font, 1); // WM_SETFONT.
        return control;
    }

    private void FocusSettings()
    {
        NativeMethods.ShowWindow(_settingsWindow, 9); // SW_RESTORE, including minimized windows.
        // Windows may deny foreground activation when another application owns input.
        // Keep the visible Settings window instead of treating that policy as a creation failure.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setforegroundwindow
        if (NativeMethods.SetForegroundWindow(_settingsWindow) == 0)
        {
            Program.Log("Windows kept Aspire Settings in the background.");
        }
        var focus = NativeMethods.GetFocus();
        if (NativeMethods.IsChild(_settingsWindow, focus) == 0 || NativeMethods.IsWindowEnabled(focus) == 0
            || NativeMethods.IsWindowVisible(focus) == 0)
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
            SetSettingsStatus(TraySettingsText.GetReadError(ex, writeError));
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
        SetSettingsStatus(TraySettingsText.GetStartupStatus(state, error));
    }

    private void SetSettingsStatus(string message)
    {
        NativeCallException.Require(NativeMethods.SetWindowText(_settingsStatus, message) != 0, "SetWindowTextW(Settings status)");
        _settingsStatusText = message;
        var showDetails = message.Length != 0;
        if (!showDetails && NativeMethods.GetFocus() == _settingsRefresh)
        {
            NativeMethods.SetFocus(NativeMethods.IsWindowEnabled(_settingsCheckbox) != 0 ? _settingsCheckbox : _settingsClose);
        }
        NativeMethods.ShowWindow(_settingsStatus, showDetails ? 5 : 0); // SW_SHOW / SW_HIDE.
        NativeMethods.ShowWindow(_settingsRefresh, showDetails ? 5 : 0);
        LayoutSettings();
    }

    private void EnableStartupCheckbox(bool enabled)
    {
        var moveFocus = !enabled && NativeMethods.GetFocus() == _settingsCheckbox;
        NativeMethods.EnableWindow(_settingsCheckbox, enabled ? 1 : 0);
        if (moveFocus)
        {
            NativeMethods.SetFocus(_settingsStatusText.Length != 0 ? _settingsRefresh : _settingsClose);
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
            var message = TraySettingsText.GetWriteError(ex);
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
            case NativeMethods.WmCtlColorDialog:
                return NativeMethods.GetSysColorBrush(15); // COLOR_BTNFACE.
            case NativeMethods.WmCtlColorStatic:
            case NativeMethods.WmCtlColorButton:
                NativeMethods.SetTextColor((nint)wParam, NativeMethods.IsWindowEnabled(lParam) == 0
                    || lParam == _settingsStartupDescription
                    ? NativeMethods.GetSysColor(17) : NativeMethods.GetSysColor(18)); // COLOR_GRAYTEXT / COLOR_BTNTEXT.
                NativeMethods.SetBkMode((nint)wParam, 1); // TRANSPARENT.
                return NativeMethods.GetSysColorBrush(15);
            case NativeMethods.WmVerticalScroll:
            case NativeMethods.WmMouseWheel:
                ScrollSettings(message, wParam);
                return 1;
            case NativeMethods.WmDpiChanged:
            case NativeMethods.WmSettingChange:
            case NativeMethods.WmSysColorChange:
            case NativeMethods.WmThemeChanged:
                // Let the PerMonitorV2 dialog manager scale its font first. Reflow on the
                // next dispatch so our measured labels and owned heading font use that DPI.
                NativeCallException.Require(NativeMethods.PostMessage(window, NativeMethods.SettingsLayoutMessage, 0, 0) != 0,
                    "PostMessageW(Settings layout)");
                return 0;
            case NativeMethods.SettingsLayoutMessage when !_settingsInitializing && _settingsHeadingFont != 0:
                UpdateSettingsAppearance();
                LayoutSettings();
                return 1;
            case NativeMethods.WmCommand when !_settingsInitializing && !_quitRequested:
                var id = (int)(wParam & 0xFFFF);
                var notification = (uint)((wParam >> 16) & 0xFFFF);
                if (notification == 6) // BN_SETFOCUS.
                {
                    EnsureSettingsControlVisible(lParam);
                    return 1;
                }
                if (id == SettingsCloseId && notification == 0)
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
                if (id == StartupRefreshId && lParam == _settingsRefresh && _settingsStatusText.Length != 0)
                {
                    ReadStartupSettings();
                    return 1;
                }
                if (id == SettingsPreviewMenuId && _interactiveSmoke)
                {
                    NativeCallException.Require(NativeMethods.GetCursorPos(out var point) != 0, "GetCursorPos(preview)");
                    ShowMenu((nuint)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16)));
                    return 1;
                }
                return 0;
            case NativeMethods.WmClose:
                CloseSettings();
                return 1;
            case NativeMethods.WmNcDestroy:
                _settingsWindow = 0;
                _settingsCheckbox = 0;
                _settingsStartupDescription = 0;
                _settingsStatus = 0;
                _settingsVersion = 0;
                _settingsRefresh = 0;
                _settingsGeneral = 0;
                _settingsGeneralSeparator = 0;
                _settingsAbout = 0;
                _settingsAboutSeparator = 0;
                _settingsLogo = 0;
                _settingsProductName = 0;
                _settingsAboutDescription = 0;
                _settingsFooterSeparator = 0;
                _settingsClose = 0;
                _settingsPreview = 0;
                _settingsStatusText = "";
                _startupState = null;
                DisposeSettingsAppearance();
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
