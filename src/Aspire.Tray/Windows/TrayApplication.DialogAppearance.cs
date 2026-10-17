// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private readonly Dictionary<nint, (MenuPalette Palette, nint Brush)> _dialogAppearance = [];

    private void UpdateDialogAppearance(nint dialog)
    {
        var palette = MenuPalette.Load();
        var brush = NativeMethods.CreateSolidBrush(palette.Background);
        NativeCallException.Require(brush != 0, "CreateSolidBrush(dialog)");
        if (_dialogAppearance.Remove(dialog, out var previous))
        {
            NativeMethods.DeleteObject(previous.Brush);
        }
        _dialogAppearance.Add(dialog, (palette, brush));
        uint dark = palette == MenuPalette.Dark ? 1u : 0u;
        // Older Windows versions can decline these optional title-bar attributes.
        NativeMethods.DwmSetWindowAttribute(dialog, 20, in dark, sizeof(uint));
        var background = palette.Background;
        NativeMethods.DwmSetWindowAttribute(dialog, 35, in background, sizeof(uint));
        for (var child = NativeMethods.GetWindow(dialog, 5); child != 0; child = NativeMethods.GetWindow(child, 2))
        {
            NativeMethods.SetWindowTheme(child, dark != 0 ? "DarkMode_Explorer" : "Explorer", null);
        }
        NativeMethods.RedrawWindow(dialog, 0, 0, 1 | 4 | 0x80);
    }

    private void ReleaseDialogAppearance(nint dialog)
    {
        if (_dialogAppearance.Remove(dialog, out var appearance))
        {
            NativeMethods.DeleteObject(appearance.Brush);
        }
    }

    private bool PaintDialog(nint dialog, uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (!_dialogAppearance.TryGetValue(dialog, out var appearance))
        {
            return false;
        }
        var palette = appearance.Palette;
        if (message == NativeMethods.WmCtlColorDialog)
        {
            result = appearance.Brush;
            return true;
        }
        if (message is NativeMethods.WmCtlColorStatic or NativeMethods.WmCtlColorButton or 0x133)
        {
            var muted = NativeMethods.IsWindowEnabled(lParam) == 0 || lParam == _settingsStartupDescription
                || lParam == _settingsVersion || lParam == _settingsAboutDescription;
            NativeMethods.SetTextColor((nint)wParam, muted ? palette.Disabled : palette.Text);
            NativeMethods.SetBkColor((nint)wParam, palette.Background);
            NativeMethods.SetBkMode((nint)wParam, 1);
            result = appearance.Brush;
            return true;
        }
        if (message != 0x4E || lParam == 0) // WM_NOTIFY / NM_CUSTOMDRAW.
        {
            return false;
        }
        var draw = (NativeMethods.DialogCustomDraw*)lParam;
        if (draw->Header.Code != -12 || draw->Stage != 1 ||
            (NativeMethods.GetWindowLong(draw->Header.Window, -16) & 0xF) is not (0 or 1))
        {
            return false;
        }
        var saved = NativeMethods.SaveDC(draw->Dc);
        try
        {
            var bounds = draw->Bounds;
            NativeMethods.FillRect(draw->Dc, in bounds, appearance.Brush);
            var pressed = (draw->State & 1) != 0;
            var hot = (draw->State & 0x40) != 0;
            var fill = NativeMethods.CreateSolidBrush(pressed || hot ? palette.Separator : palette.Hover);
            var oldBrush = NativeMethods.SelectObject(draw->Dc, fill);
            NativeMethods.SelectObject(draw->Dc, NativeMethods.GetStockObject(8)); // NULL_PEN.
            var radius = checked((int)(8 * NativeMethods.GetDpiForWindow(dialog) / 96));
            NativeMethods.RoundRect(draw->Dc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, radius, radius);
            NativeMethods.SelectObject(draw->Dc, oldBrush);
            NativeMethods.DeleteObject(fill);
            NativeMethods.SelectObject(draw->Dc, NativeMethods.SendMessage(draw->Header.Window, 0x31, 0, 0));
            NativeMethods.SetBkMode(draw->Dc, 1);
            NativeMethods.SetTextColor(draw->Dc, (draw->State & 4) != 0 ? palette.Disabled : palette.Text);
            var length = NativeMethods.GetWindowTextLength(draw->Header.Window);
            var text = new char[length + 1];
            fixed (char* buffer = text)
            {
                NativeMethods.GetWindowText(draw->Header.Window, buffer, text.Length);
            }
            NativeMethods.DrawText(draw->Dc, new string(text, 0, length), length, ref bounds, 1 | 4 | 0x20);
            if ((draw->State & 0x10) != 0 && (draw->State & 0x200) == 0)
            {
                bounds.Left += 4;
                bounds.Top += 4;
                bounds.Right -= 4;
                bounds.Bottom -= 4;
                NativeMethods.DrawFocusRect(draw->Dc, in bounds);
            }
        }
        finally
        {
            NativeMethods.RestoreDC(draw->Dc, saved);
        }
        // Dialog procedures return notification results through DWLP_MSGRESULT.
        NativeMethods.SetWindowLongPtr(dialog, 0, 4); // CDRF_SKIPDEFAULT.
        result = 1;
        return true;
    }
}
