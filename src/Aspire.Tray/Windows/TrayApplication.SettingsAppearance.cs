// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private readonly Dictionary<nint, NativeMethods.Rect> _settingsControlBounds = [];
    private nint _settingsHeadingFont;
    private NativeMethods.Rect _settingsGeneralBounds;
    private NativeMethods.Rect _settingsAboutBounds;
    private int _settingsScrollPosition;
    private int _settingsScrollMaximum;
    private int _settingsWheelRemainder;

    private void UpdateSettingsAppearance()
    {
        var dpi = NativeMethods.GetDpiForWindow(_settingsWindow);
        var font = NativeMethods.CreateFont(-checked((int)((9 * dpi + 36) / 72)), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        NativeCallException.Require(font != 0, "CreateFontW(Settings)");
        foreach (var control in new[] { _settingsGeneral, _settingsAbout, _settingsProductName })
        {
            NativeMethods.SendMessage(control, 0x30, (nuint)font, 1); // WM_SETFONT.
        }
        // Controls must release the old font before its GDI handle is deleted.
        if (_settingsHeadingFont != 0)
        {
            Cleanup(NativeMethods.DeleteObject(_settingsHeadingFont) != 0, "DeleteObject(Settings font)");
        }
        _settingsHeadingFont = font;

        var size = checked((int)(32 * dpi / 96));
        var icon = NativeMethods.LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Aspire.ico"),
            NativeMethods.ImageIcon, size, size, NativeMethods.LrLoadFromFile);
        NativeCallException.Require(icon != 0, "LoadImageW(Settings logo)");
        NativeMethods.SendMessage(_settingsLogo, 0x170, (nuint)icon, 0); // STM_SETICON; the static control borrows the icon.
        if (_settingsIcon != 0)
        {
            Cleanup(NativeMethods.DestroyIcon(_settingsIcon) != 0, "DestroyIcon(Settings logo)");
        }
        _settingsIcon = icon;
    }

    private void LayoutSettings()
    {
        // Bounds are logical pixels at 96 DPI. Measure wrapped text using the dialog's
        // actual body font so version strings and startup errors can grow the window.
        _settingsControlBounds.Clear();
        var descriptionHeight = MeasureSettingsText(TraySettingsText.StartupDescription, 405);
        var generalBottom = 68 + descriptionHeight;
        if (_settingsStatusText.Length != 0)
        {
            var statusHeight = MeasureSettingsText(_settingsStatusText, 405);
            var statusTop = generalBottom + 12;
            var refreshTop = statusTop + statusHeight + 8;
            _settingsControlBounds[_settingsStatus] = SettingsRect(35, statusTop, 405, statusHeight);
            _settingsControlBounds[_settingsRefresh] = SettingsRect(35, refreshTop, 150, 24);
            generalBottom = refreshTop + 24 + 12;
        }
        var aboutTop = generalBottom + 22;
        var aboutDescriptionHeight = MeasureSettingsText(AboutDescriptionText, 380);
        var versionTop = aboutTop + 50 + aboutDescriptionHeight + 12;
        var versionHeight = MeasureSettingsText(AboutVersionText, 380);
        var aboutBottom = Math.Max(aboutTop + 124, versionTop + versionHeight + 12);
        _settingsControlBounds[_settingsGeneral] = SettingsRect(16, 16, 52, 18);
        _settingsControlBounds[_settingsGeneralSeparator] = SettingsRect(72, 24, 368, 2);
        _settingsControlBounds[_settingsCheckbox] = SettingsRect(16, 43, 424, 20);
        _settingsControlBounds[_settingsStartupDescription] = SettingsRect(35, 68, 405, descriptionHeight);
        _settingsControlBounds[_settingsAbout] = SettingsRect(16, aboutTop, 42, 18);
        _settingsControlBounds[_settingsAboutSeparator] = SettingsRect(62, aboutTop + 8, 378, 2);
        _settingsControlBounds[_settingsLogo] = SettingsRect(16, aboutTop + 30, 32, 32);
        _settingsControlBounds[_settingsProductName] = SettingsRect(60, aboutTop + 28, 380, 18);
        _settingsControlBounds[_settingsAboutDescription] = SettingsRect(60, aboutTop + 50, 380, aboutDescriptionHeight);
        _settingsControlBounds[_settingsVersion] = SettingsRect(60, versionTop, 380, versionHeight);
        var footerTop = aboutBottom + 18;
        var closeTop = footerTop + 14;
        if (_settingsPreview != 0)
        {
            _settingsControlBounds[_settingsPreview] = SettingsRect(16, closeTop, 130, 24);
        }
        _settingsControlBounds[_settingsFooterSeparator] = SettingsRect(0, footerTop, 456, 2);
        _settingsControlBounds[_settingsClose] = SettingsRect(365, closeTop, 75, 24);
        _settingsGeneralBounds = SettingsRect(16, 16, 424, generalBottom - 16);
        _settingsAboutBounds = SettingsRect(16, aboutTop, 424, aboutBottom - aboutTop);
        var desired = SettingsRect(0, 0, 456, closeTop + 24 + 14);
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
        var dpi = NativeMethods.GetDpiForWindow(_settingsWindow);
        int Scale(int value) => checked((int)((value * dpi + 48) / 96));
        return new() { Left = Scale(x), Top = Scale(y), Right = Scale(x + width), Bottom = Scale(y + height) };
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
        var units = SettingsRect(0, 0, width, 96);
        var dc = NativeMethods.GetDC(_settingsWindow);
        NativeCallException.Require(dc != 0, "GetDC(Settings measure)");
        var previous = NativeMethods.SelectObject(dc, NativeMethods.SendMessage(_settingsWindow, 0x31, 0, 0));
        try
        {
            NativeCallException.Require(previous != 0 && previous != -1, "SelectObject(Settings measure)");
            var rect = new NativeMethods.Rect { Right = units.Right };
            NativeCallException.Require(NativeMethods.DrawText(dc, text, text.Length, ref rect,
                0x10 | 0x400 | 0x800) > 0, "DrawTextW(Settings measure)"); // WORDBREAK | CALCRECT | NOPREFIX.
            return checked((int)Math.Ceiling(rect.Bottom * 96d / units.Bottom)) + 2;
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

    private void DisposeSettingsAppearance()
    {
        if (_settingsHeadingFont != 0)
        {
            Cleanup(NativeMethods.DeleteObject(_settingsHeadingFont) != 0, "DeleteObject(Settings font)");
            _settingsHeadingFont = 0;
        }
        _settingsScrollPosition = _settingsScrollMaximum = _settingsWheelRemainder = 0;
        _settingsControlBounds.Clear();
    }
}
