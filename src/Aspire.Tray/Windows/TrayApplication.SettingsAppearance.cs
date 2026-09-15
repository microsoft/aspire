// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private readonly Dictionary<nint, NativeMethods.Rect> _settingsControlBounds = [];
    private nint _settingsHeadingFont;
    private nint _settingsTitleFont;
    private nint _settingsBackgroundBrush;
    private nint _settingsCardBrush;
    private nint _settingsBorderPen;
    private uint _settingsTextColor;
    private NativeMethods.Rect _settingsGeneralBounds;
    private NativeMethods.Rect _settingsAboutBounds;
    private int _settingsScrollPosition;
    private int _settingsScrollMaximum;
    private int _settingsWheelRemainder;

    private void UpdateSettingsAppearance()
    {
        var dpi = NativeMethods.GetDpiForWindow(_settingsWindow);
        UpdateSettingsFont(ref _settingsTitleFont, checked((int)(21 * dpi / 72)), [_settingsTitle]);
        UpdateSettingsFont(ref _settingsHeadingFont, checked((int)(15 * dpi / 72)), [_settingsGeneral, _settingsAbout]);

        var contrast = new NativeMethods.HighContrast { Size = (uint)sizeof(NativeMethods.HighContrast) };
        NativeCallException.Require(NativeMethods.GetHighContrast(0x42, contrast.Size, ref contrast, 0) != 0,
            "SystemParametersInfoW(SPI_GETHIGHCONTRAST)");
        var highContrast = (contrast.Flags & 1) != 0;
        // Keep the custom surfaces readable with the user's high-contrast palette.
        // Ordinary light-mode colors match the system Settings-style cards.
        _settingsTextColor = highContrast ? NativeMethods.GetSysColor(8) : 0x001A1A1A;
        ReplaceSettingsObject(ref _settingsBackgroundBrush,
            NativeMethods.CreateSolidBrush(highContrast ? NativeMethods.GetSysColor(5) : 0x00F3F3F3));
        ReplaceSettingsObject(ref _settingsCardBrush,
            NativeMethods.CreateSolidBrush(highContrast ? NativeMethods.GetSysColor(5) : 0x00FAFAFA));
        ReplaceSettingsObject(ref _settingsBorderPen,
            NativeMethods.CreatePen(0, Math.Max(1, checked((int)dpi / 96)),
                highContrast ? NativeMethods.GetSysColor(8) : 0x00E2E2E2));
    }

    private void UpdateSettingsFont(ref nint ownedFont, int height, ReadOnlySpan<nint> controls)
    {
        var font = NativeMethods.CreateFont(-height, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        NativeCallException.Require(font != 0, "CreateFontW(Settings)");
        foreach (var control in controls)
        {
            NativeMethods.SendMessage(control, 0x30, (nuint)font, 1); // WM_SETFONT.
        }
        // Controls must release the old font before its GDI handle is deleted.
        ReplaceSettingsObject(ref ownedFont, font);
    }

    private void ReplaceSettingsObject(ref nint owned, nint replacement)
    {
        NativeCallException.Require(replacement != 0, "Create Settings GDI object");
        if (owned != 0)
        {
            Cleanup(NativeMethods.DeleteObject(owned) != 0, "DeleteObject(Settings)");
        }
        owned = replacement;
    }

    private void LayoutSettings()
    {
        // Dialog units follow the dialog manager's DPI-scaled body font. Measure full
        // labels so backend errors grow the card instead of hiding inside edit controls.
        _settingsControlBounds.Clear();
        var generalBottom = 98;
        if (_settingsStatusText.Length != 0)
        {
            var statusHeight = MeasureSettingsText(_settingsStatusText, 316);
            var refreshTop = 94 + statusHeight + 4;
            _settingsControlBounds[_settingsStatus] = SettingsRect(32, 94, 316, statusHeight);
            _settingsControlBounds[_settingsRefresh] = SettingsRect(32, refreshTop, 90, 15);
            generalBottom = refreshTop + 15 + 12;
        }
        var aboutTop = generalBottom + 12;
        var versionTop = aboutTop + 30;
        var versionHeight = MeasureSettingsText(AboutVersionText, 316);
        var aboutBottom = versionTop + versionHeight + 12;
        _settingsControlBounds[_settingsTitle] = SettingsRect(20, 16, 340, 22);
        _settingsControlBounds[_settingsGeneral] = SettingsRect(32, 56, 316, 14);
        _settingsControlBounds[_settingsCheckbox] = SettingsRect(32, 74, 316, 12);
        _settingsControlBounds[_settingsAbout] = SettingsRect(32, aboutTop + 12, 316, 14);
        _settingsControlBounds[_settingsVersion] = SettingsRect(32, versionTop, 316, versionHeight);
        if (_settingsPreview != 0)
        {
            _settingsControlBounds[_settingsPreview] = SettingsRect(32, aboutBottom - 6, 90, 15);
            aboutBottom += 21;
        }
        var closeTop = aboutBottom + 12;
        _settingsControlBounds[_settingsClose] = SettingsRect(326, closeTop, 34, 15);
        _settingsGeneralBounds = SettingsRect(20, 44, 340, generalBottom - 44);
        _settingsAboutBounds = SettingsRect(20, aboutTop, 340, aboutBottom - aboutTop);
        var desired = SettingsRect(0, 0, 380, closeTop + 15 + 12);
        NativeCallException.Require(NativeMethods.GetWindowRect(_settingsWindow, out var window) != 0, "GetWindowRect(Settings)");
        NativeCallException.Require(NativeMethods.GetClientRect(_settingsWindow, out var client) != 0, "GetClientRect(Settings)");
        var monitor = new NativeMethods.MonitorInfo { Size = (uint)sizeof(NativeMethods.MonitorInfo) };
        NativeCallException.Require(NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(_settingsWindow, 2), ref monitor) != 0,
            "GetMonitorInfoW(Settings)");
        var frameHeight = window.Bottom - window.Top - client.Bottom;
        var pageHeight = Math.Min(desired.Bottom, monitor.Work.Bottom - monitor.Work.Top - frameHeight);
        _settingsScrollMaximum = Math.Max(0, desired.Bottom - pageHeight);
        _settingsScrollPosition = Math.Clamp(_settingsScrollPosition, 0, _settingsScrollMaximum);
        var scroll = new NativeMethods.ScrollInfo
        {
            Size = (uint)sizeof(NativeMethods.ScrollInfo), Mask = 1 | 2 | 4, // SIF_RANGE | SIF_PAGE | SIF_POS.
            Maximum = desired.Bottom - 1, Page = (uint)pageHeight, Position = _settingsScrollPosition
        };
        NativeMethods.SetScrollInfo(_settingsWindow, 1, in scroll, 1); // SB_VERT; hidden when all content fits.
        // Showing a scrollbar changes the non-client width, not the card's text width.
        NativeCallException.Require(NativeMethods.GetClientRect(_settingsWindow, out client) != 0, "GetClientRect(Settings scroll)");
        var width = desired.Right + window.Right - window.Left - client.Right;
        var height = pageHeight + frameHeight;
        var x = Math.Clamp(window.Left, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        var y = Math.Clamp(window.Top, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        NativeCallException.Require(NativeMethods.SetWindowPos(_settingsWindow, 0, x, y, width, height, 0x4 | 0x10) != 0,
            "SetWindowPos(Settings size)");
        PositionSettingsControls();
        EnsureSettingsControlVisible(NativeMethods.GetFocus());
    }

    private NativeMethods.Rect SettingsRect(int x, int y, int width, int height)
    {
        var rect = new NativeMethods.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        NativeCallException.Require(NativeMethods.MapDialogRect(_settingsWindow, ref rect) != 0, "MapDialogRect(Settings)");
        return rect;
    }

    private void PositionSettingsControls()
    {
        foreach (var (control, bounds) in _settingsControlBounds)
        {
            NativeCallException.Require(NativeMethods.SetWindowPos(control, 0, bounds.Left, bounds.Top - _settingsScrollPosition,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x4 | 0x10) != 0, "SetWindowPos(Settings control)");
        }
        NativeCallException.Require(NativeMethods.RedrawWindow(_settingsWindow, 0, 0, 1 | 4 | 0x80) != 0,
            "RedrawWindow(Settings)"); // RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN.
    }

    private void EnsureSettingsControlVisible(nint control)
    {
        if (!_settingsControlBounds.TryGetValue(control, out var bounds))
        {
            return;
        }
        NativeCallException.Require(NativeMethods.GetClientRect(_settingsWindow, out var client) != 0, "GetClientRect(Settings focus)");
        if (bounds.Top < _settingsScrollPosition)
        {
            SetSettingsScrollPosition(bounds.Top - SettingsRect(0, 0, 0, 8).Bottom);
        }
        else if (bounds.Bottom > _settingsScrollPosition + client.Bottom)
        {
            SetSettingsScrollPosition(bounds.Bottom - client.Bottom + SettingsRect(0, 0, 0, 8).Bottom);
        }
    }

    private void SetSettingsScrollPosition(int position)
    {
        position = Math.Clamp(position, 0, _settingsScrollMaximum);
        if (position == _settingsScrollPosition)
        {
            return;
        }
        _settingsScrollPosition = position;
        var scroll = new NativeMethods.ScrollInfo { Size = (uint)sizeof(NativeMethods.ScrollInfo), Mask = 4, Position = position };
        NativeMethods.SetScrollInfo(_settingsWindow, 1, in scroll, 1);
        PositionSettingsControls();
    }

    private void ScrollSettings(uint message, nuint wParam)
    {
        NativeCallException.Require(NativeMethods.GetClientRect(_settingsWindow, out var client) != 0, "GetClientRect(Settings scroll)");
        var line = SettingsRect(0, 0, 0, 8).Bottom;
        if (message == NativeMethods.WmMouseWheel)
        {
            uint lines = 0;
            NativeCallException.Require(NativeMethods.GetWheelScrollLines(0x68, 0, ref lines, 0) != 0,
                "SystemParametersInfoW(SPI_GETWHEELSCROLLLINES)");
            _settingsWheelRemainder += (short)(wParam >> 16);
            var steps = _settingsWheelRemainder / 120;
            _settingsWheelRemainder %= 120;
            var distance = lines == uint.MaxValue ? client.Bottom : checked((int)lines * line);
            SetSettingsScrollPosition(_settingsScrollPosition - steps * distance);
            return;
        }
        var scroll = new NativeMethods.ScrollInfo { Size = (uint)sizeof(NativeMethods.ScrollInfo), Mask = 0x10 }; // SIF_TRACKPOS.
        var command = (int)(wParam & 0xFFFF);
        if (command is 4 or 5)
        {
            NativeCallException.Require(NativeMethods.GetScrollInfo(_settingsWindow, 1, ref scroll) != 0, "GetScrollInfo(Settings)");
        }
        SetSettingsScrollPosition(command switch
        {
            0 => _settingsScrollPosition - line,
            1 => _settingsScrollPosition + line,
            2 => _settingsScrollPosition - client.Bottom,
            3 => _settingsScrollPosition + client.Bottom,
            4 or 5 => scroll.TrackPosition,
            6 => 0,
            7 => _settingsScrollMaximum,
            _ => _settingsScrollPosition
        });
    }

    private int MeasureSettingsText(string text, int width)
    {
        var units = SettingsRect(0, 0, width, 8);
        var dc = NativeMethods.GetDC(_settingsWindow);
        NativeCallException.Require(dc != 0, "GetDC(Settings measure)");
        var previous = NativeMethods.SelectObject(dc, NativeMethods.SendMessage(_settingsWindow, 0x31, 0, 0));
        try
        {
            NativeCallException.Require(previous != 0 && previous != -1, "SelectObject(Settings measure)");
            var rect = new NativeMethods.Rect { Right = units.Right };
            NativeCallException.Require(NativeMethods.DrawText(dc, text, text.Length, ref rect,
                0x10 | 0x400 | 0x800) > 0, "DrawTextW(Settings measure)"); // WORDBREAK | CALCRECT | NOPREFIX.
            return checked((int)Math.Ceiling(rect.Bottom * 8d / units.Bottom)) + 2;
        }
        finally
        {
            if (previous != 0 && previous != -1)
            {
                Cleanup(NativeMethods.SelectObject(dc, previous) != 0, "SelectObject(Settings measure restore)");
            }
            Cleanup(NativeMethods.ReleaseDC(_settingsWindow, dc) != 0, "ReleaseDC(Settings measure)");
        }
    }

    private void PaintSettingsBackground(nint dc)
    {
        NativeCallException.Require(NativeMethods.GetClientRect(_settingsWindow, out var client) != 0, "GetClientRect(Settings paint)");
        NativeCallException.Require(NativeMethods.FillRect(dc, in client, _settingsBackgroundBrush) != 0, "FillRect(Settings)");
        var saved = NativeMethods.SaveDC(dc);
        NativeCallException.Require(saved != 0, "SaveDC(Settings)");
        try
        {
            NativeCallException.Require(NativeMethods.SelectObject(dc, _settingsCardBrush) != 0, "SelectObject(Settings card)");
            NativeCallException.Require(NativeMethods.SelectObject(dc, _settingsBorderPen) != 0, "SelectObject(Settings border)");
            var diameter = checked((int)(16 * NativeMethods.GetDpiForWindow(_settingsWindow) / 96));
            foreach (var bounds in new[] { _settingsGeneralBounds, _settingsAboutBounds })
            {
                NativeCallException.Require(NativeMethods.RoundRect(dc, bounds.Left, bounds.Top - _settingsScrollPosition,
                    bounds.Right, bounds.Bottom - _settingsScrollPosition, diameter, diameter) != 0, "RoundRect(Settings card)");
            }
        }
        finally
        {
            Cleanup(NativeMethods.RestoreDC(dc, saved) != 0, "RestoreDC(Settings)");
        }
    }

    private void DisposeSettingsAppearance()
    {
        foreach (var resource in new[] { _settingsHeadingFont, _settingsTitleFont, _settingsBackgroundBrush, _settingsCardBrush, _settingsBorderPen })
        {
            if (resource != 0)
            {
                Cleanup(NativeMethods.DeleteObject(resource) != 0, "DeleteObject(Settings appearance)");
            }
        }
        _settingsHeadingFont = _settingsTitleFont = _settingsBackgroundBrush = _settingsCardBrush = _settingsBorderPen = 0;
        _settingsScrollPosition = _settingsScrollMaximum = _settingsWheelRemainder = 0;
        _settingsControlBounds.Clear();
    }
}
