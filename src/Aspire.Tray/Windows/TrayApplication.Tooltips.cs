// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private nint _menuTooltipWindow;
    private nint _menuTooltipBuffer;
    private string? _menuTooltipText;
    private HostRow? _menuTooltipRow;
    private long _menuTooltipDeadline;
    private bool _menuTooltipVisible;

    private void SelectMenuTooltip(nuint selection, nint menu)
    {
        // WM_MENUSELECT packs the command ID and flags into wParam for ordinary items.
        // Only the disabled details entry has ID 0 in an AppHost action submenu; exclude
        // separators and popups, and never attach this tooltip to the parent AppHost row.
        // https://learn.microsoft.com/windows/win32/menurc/wm-menuselect
        var flags = (uint)((selection >> 16) & 0xFFFF);
        var command = (uint)(selection & 0xFFFF);
        var row = _menuOpen && !_quitRequested && _modalDepth == 0 && _dispatchDepth == 0
            && menu != 0 && command == 0 && (flags & (NativeMethods.MfPopup | NativeMethods.MfSeparator)) == 0
            ? _menu?.Rows.SingleOrDefault(row => row.Submenu == menu)
            : null;
        if (ReferenceEquals(row, _menuTooltipRow))
        {
            return;
        }
        HideMenuTooltip();
        if (row is null)
        {
            return;
        }
        EnsureMenuTooltip();
        _menuTooltipRow = row;
        var delay = NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmGetDelayTime, 3, 0);
        _menuTooltipDeadline = Environment.TickCount64 + (long)delay;
    }

    private void EnsureMenuTooltip()
    {
        if (_menuTooltipWindow != 0)
        {
            return;
        }
        // HMENU items are not child windows, so use explicit tracking. NOPREFIX preserves
        // literal ampersands in paths; NOACTIVATE keeps focus in the menu.
        // https://learn.microsoft.com/windows/win32/controls/implement-tracking-tooltips
        _menuTooltipWindow = NativeMethods.CreateWindowEx(
            0x08000008, "tooltips_class32", "", 0x80000033,
            0, 0, 0, 0, _window, 0, _module, 0); // TOPMOST | NOACTIVATE; POPUP | ALWAYSTIP | NOPREFIX | NOANIMATE | NOFADE.
        NativeCallException.Require(_menuTooltipWindow != 0, "CreateWindowExW(menu tooltip)");
        var tool = MenuTooltipInfo();
        NativeCallException.Require(NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmAddTool, 0, (nint)(&tool)) != 0,
            "TTM_ADDTOOLW");
    }

    private NativeMethods.ToolInfo MenuTooltipInfo() => new()
    {
        Size = (uint)sizeof(NativeMethods.ToolInfo),
        Flags = 0x20 | 0x80, // TTF_TRACK | TTF_ABSOLUTE.
        Window = _window,
        Id = 1,
        Text = (char*)_menuTooltipBuffer
    };

    private void UpdateMenuTooltip()
    {
        if (_menuTooltipRow is not { } row || (!_menuTooltipVisible && Environment.TickCount64 < _menuTooltipDeadline))
        {
            return;
        }
        if (_menuTooltipVisible && _menuTooltipText == row.DetailsText)
        {
            return;
        }

        // The control can retain the text beyond SendMessage. Keep the unmanaged buffer
        // until its replacement is installed or the tooltip window has been destroyed.
        var previous = _menuTooltipBuffer;
        _menuTooltipBuffer = Marshal.StringToCoTaskMemUni(row.DetailsText);
        var tool = MenuTooltipInfo();
        NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmUpdateTipText, 0, (nint)(&tool));
        Marshal.FreeCoTaskMem(previous);
        _menuTooltipText = row.DetailsText;

        PositionMenuTooltip(row, ref tool);
        if (!_menuTooltipVisible)
        {
            NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmTrackActivate, 1, (nint)(&tool));
            _menuTooltipVisible = true;
        }
    }

    private void PositionMenuTooltip(HostRow row, ref NativeMethods.ToolInfo tool)
    {
        NativeCallException.Require(NativeMethods.GetMenuItemRect(0, row.Submenu, 0, out var first) != 0,
            "GetMenuItemRect(tooltip first)");
        NativeCallException.Require(NativeMethods.GetMenuItemRect(0, row.Submenu, row.DetailsPosition, out var last) != 0,
            "GetMenuItemRect(tooltip details)");
        var monitor = new NativeMethods.MonitorInfo { Size = (uint)sizeof(NativeMethods.MonitorInfo) };
        NativeCallException.Require(NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(_window, 2), ref monitor) != 0,
            "GetMonitorInfoW(tooltip)");
        var dpi = NativeMethods.GetDpiForWindow(_window);
        var gap = (int)Math.Max(4, dpi / 24);
        // Full paths can be much wider than a monitor. Wrap the tooltip, not its menu label.
        var maximumWidth = Math.Max(1, Math.Min((int)(600 * dpi / 96), monitor.Work.Right - monitor.Work.Left - 4 * gap));
        NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmSetMaxTipWidth, 0, maximumWidth);
        nuint size;
        fixed (NativeMethods.ToolInfo* info = &tool)
        {
            size = (nuint)NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmGetBubbleSize, 0, (nint)info);
        }
        var width = (int)(size & 0xFFFF);
        var height = (int)((size >> 16) & 0xFFFF);
        NativeCallException.Require(width > 0 && height > 0, "TTM_GETBUBBLESIZE");
        var x = Math.Clamp(last.Left, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        // Prefer the space below the final entry; otherwise use space above the popup.
        var y = last.Bottom + gap;
        if (y + height > monitor.Work.Bottom)
        {
            y = first.Top - height - gap;
        }
        y = Math.Clamp(y, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmTrackPosition, 0,
            unchecked((nint)((uint)(ushort)x | ((uint)(ushort)y << 16))));
    }

    private void HideMenuTooltip()
    {
        _menuTooltipRow = null;
        _menuTooltipVisible = false;
        if (_menuTooltipWindow != 0)
        {
            var tool = MenuTooltipInfo();
            NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmTrackActivate, 0, (nint)(&tool));
        }
    }

    private bool DisposeMenuTooltip()
    {
        HideMenuTooltip();
        if (_menuTooltipWindow != 0)
        {
            if (NativeMethods.DestroyWindow(_menuTooltipWindow) == 0)
            {
                Cleanup(false, "DestroyWindow(menu tooltip)");
                return false;
            }
            _menuTooltipWindow = 0;
        }
        Marshal.FreeCoTaskMem(_menuTooltipBuffer);
        _menuTooltipBuffer = 0;
        _menuTooltipText = null;
        return true;
    }
}
