// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private readonly Dictionary<nuint, MenuPaintItem> _menuPaintItems = [];
    private readonly Dictionary<nint, nint> _menuBackgroundBrushes = [];
    private MenuPalette _menuPalette;
    private void RefreshMenuPalette()
    {
        _menuPalette = MenuPalette.Load();
        foreach (var menu in _menuBackgroundBrushes.Keys.ToArray())
        {
            SetMenuBackground(menu);
        }
    }

    private void SetMenuBackground(nint menu)
    {
        var brush = NativeMethods.CreateSolidBrush(_menuPalette.Background);
        NativeCallException.Require(brush != 0, "CreateSolidBrush(menu background)");
        var info = new NativeMethods.MenuInfo { Size = (uint)sizeof(NativeMethods.MenuInfo), Mask = 2, Background = brush };
        if (NativeMethods.SetMenuInfo(menu, in info) == 0)
        {
            NativeMethods.DeleteObject(brush);
            throw new NativeCallException("SetMenuInfo(background)", Marshal.GetLastPInvokeError());
        }
        if (_menuBackgroundBrushes.Remove(menu, out var previous))
        {
            NativeMethods.DeleteObject(previous);
        }
        _menuBackgroundBrushes.Add(menu, brush);
    }

    private void ClearMenuAppearance()
    {
        foreach (var item in _menuPaintItems.Values)
        {
            var info = ReadMenuPaintItem(item.Menu, item.Position);
            info.Mask = NativeMethods.MiimFType | NativeMethods.MiimBitmap | 0x20;
            info.Type &= ~0x100u;
            info.ItemData = 0;
            info.Bitmap = item.Bitmap;
            NativeCallException.Require(NativeMethods.SetMenuItemInfo(item.Menu, item.Position, 1, ref info) != 0, "SetMenuItemInfoW(reset appearance)");
            item.Dispose();
        }
        _menuPaintItems.Clear();
        foreach (var (menu, brush) in _menuBackgroundBrushes)
        {
            var info = new NativeMethods.MenuInfo { Size = (uint)sizeof(NativeMethods.MenuInfo), Mask = 2, Background = NativeMethods.GetSysColorBrush(4) };
            NativeCallException.Require(NativeMethods.SetMenuInfo(menu, in info) != 0, "SetMenuInfo(reset background)");
            NativeMethods.DeleteObject(brush);
        }
        _menuBackgroundBrushes.Clear();
    }

    private void ReleaseMenuAppearance(nint menu)
    {
        for (uint position = 0; position < NativeMethods.GetMenuItemCount(menu); position++)
        {
            var info = ReadMenuPaintItem(menu, position);
            if (_menuPaintItems.Remove(info.ItemData, out var item))
            {
                item.Dispose();
            }
            if (info.Submenu != 0)
            {
                ReleaseMenuAppearance(info.Submenu);
            }
        }
        if (_menuBackgroundBrushes.Remove(menu, out var brush))
        {
            NativeMethods.DeleteObject(brush);
        }
    }

    private int MenuPixels(int value) => Math.Max(1, (int)(value * NativeMethods.GetDpiForWindow(_window) / 96));

    private bool IsHostMenuRow(MenuPaintItem item)
        => _menu?.Rows.Any(row => row.Parent == item.Menu && row.Position == item.Position) == true;

    private nint MatchMenuCharacter(nint menu, char character)
    {
        // Labels are literal (e.g. "Settings...\tCtrl+," or "A&&B.AppHost").
        // Match their first display character, skipping disabled rows and separators;
        // repeated initials cycle from the highlighted row rather than executing.
        // https://learn.microsoft.com/windows/win32/menurc/wm-menuchar
        var matches = new List<uint>();
        uint? highlighted = null;
        for (uint position = 0; position < NativeMethods.GetMenuItemCount(menu); position++)
        {
            var info = ReadMenuPaintItem(menu, position);
            if ((info.State & 0x80) != 0) // MFS_HILITE.
            {
                highlighted = position;
            }
            if ((info.State & 3) != 0 || (info.Type & NativeMethods.MfSeparator) != 0
                || !_menuPaintItems.TryGetValue(info.ItemData, out var item))
            {
                continue;
            }
            var text = ReadMenuPaintText(item).Replace("&&", "&", StringComparison.Ordinal);
            if (text.Length != 0 && char.ToUpperInvariant(text[0]) == char.ToUpperInvariant(character))
            {
                matches.Add(position);
            }
        }
        if (matches.Count == 0)
        {
            return 0; // MNC_IGNORE.
        }
        var selected = matches[0];
        if (highlighted is uint current)
        {
            foreach (var match in matches)
            {
                if (match > current)
                {
                    selected = match;
                    break;
                }
            }
        }
        return (nint)(selected | (matches.Count == 1 ? 2u : 3u) << 16); // MNC_EXECUTE / MNC_SELECT.
    }

    private void PrepareMenuAppearance(nint menu)
    {
        SetMenuBackground(menu);
        // Owner drawing preserves USER32's submenu, keyboard, dismissal, and command
        // routing. Each item's data starts with MSAAMENUINFO so accessibility clients
        // can read the label even though Windows no longer paints the text itself.
        // https://learn.microsoft.com/windows/win32/menurc/using-menus#creating-owner-drawn-menu-items
        // https://learn.microsoft.com/windows/win32/winauto/exposing-owner-drawn-menu-items
        for (uint position = 0; position < NativeMethods.GetMenuItemCount(menu); position++)
        {
            var info = ReadMenuPaintItem(menu, position);
            var item = new MenuPaintItem(menu, position, info.Bitmap);
            item.UpdateName(ReadMenuPaintText(item));
            var token = (nuint)item.AccessibleInfo;
            _menuPaintItems.Add(token, item);
            // Native bitmap layout can override owner-drawn row measurements. Keep
            // the artwork in our item data and paint it ourselves for consistent padding.
            info.Mask = NativeMethods.MiimFType | NativeMethods.MiimBitmap | 0x20; // MIIM_DATA.
            info.Type |= 0x100; // MFT_OWNERDRAW.
            info.ItemData = token;
            info.Bitmap = 0;
            NativeCallException.Require(NativeMethods.SetMenuItemInfo(menu, position, 1, ref info) != 0, "SetMenuItemInfoW(appearance)");
            if (info.Submenu != 0)
            {
                PrepareMenuAppearance(info.Submenu);
            }
        }
    }

    private static NativeMethods.MenuItemInfo ReadMenuPaintItem(nint menu, uint position)
    {
        var info = new NativeMethods.MenuItemInfo
        {
            Size = (uint)sizeof(NativeMethods.MenuItemInfo),
            Mask = NativeMethods.MiimFType | NativeMethods.MiimState | NativeMethods.MiimId | NativeMethods.MiimSubmenu | NativeMethods.MiimBitmap | 0x20
        };
        NativeCallException.Require(NativeMethods.GetMenuItemInfo(menu, position, 1, ref info) != 0, "GetMenuItemInfoW(appearance)");
        return info;
    }

    private static string ReadMenuPaintText(MenuPaintItem item)
    {
        var info = new NativeMethods.MenuItemInfo
        {
            Size = (uint)sizeof(NativeMethods.MenuItemInfo), Mask = NativeMethods.MiimString
        };
        NativeCallException.Require(NativeMethods.GetMenuItemInfo(item.Menu, item.Position, 1, ref info) != 0, "GetMenuItemInfoW(text length)");
        var buffer = new char[info.TextLength + 1];
        fixed (char* text = buffer)
        {
            info.Text = text;
            info.TextLength = (uint)buffer.Length;
            NativeCallException.Require(NativeMethods.GetMenuItemInfo(item.Menu, item.Position, 1, ref info) != 0, "GetMenuItemInfoW(text)");
            return new string(text, 0, (int)info.TextLength);
        }
    }

    private bool MeasureMenuItem(nint parameter)
    {
        var measure = (MenuMeasure*)parameter;
        if (measure->ControlType != 1 || !_menuPaintItems.TryGetValue(measure->ItemData, out var item))
        {
            return false;
        }
        var info = ReadMenuPaintItem(item.Menu, item.Position);
        measure->Height = (uint)MenuPixels((info.Type & NativeMethods.MfSeparator) != 0 ? 10 : 36);
        var dc = NativeMethods.GetDC(_window);
        NativeCallException.Require(dc != 0, "GetDC(menu measurement)");
        try
        {
            var width = WithMenuFont(dc, IsHostMenuRow(item) || (info.State & 0x1000) != 0, () =>
            {
                var bounds = new NativeMethods.Rect();
                var text = ReadMenuPaintText(item).Replace('\t', ' ');
                NativeMethods.DrawText(dc, text, text.Length, ref bounds, 0x400 | 0x20); // CALCRECT | SINGLELINE.
                return bounds.Right;
            });
            measure->Width = (uint)(width + MenuPixels(80));
        }
        finally
        {
            NativeMethods.ReleaseDC(_window, dc);
        }
        return true;
    }

    private bool DrawMenuItem(nint parameter)
    {
        var draw = (MenuDraw*)parameter;
        if (draw->ControlType != 1 || !_menuPaintItems.TryGetValue(draw->ItemData, out var item))
        {
            return false;
        }
        var info = ReadMenuPaintItem(item.Menu, item.Position);
        var bounds = draw->Bounds;
        FillMenuRect(draw->Dc, in bounds, _menuPalette.Background);
        if ((info.Type & NativeMethods.MfSeparator) != 0)
        {
            bounds.Left += MenuPixels(12);
            bounds.Right -= MenuPixels(12);
            bounds.Top = (bounds.Top + bounds.Bottom) / 2;
            bounds.Bottom = bounds.Top + 1;
            FillMenuRect(draw->Dc, in bounds, _menuPalette.Separator);
            return true;
        }
        var selected = (draw->State & 1) != 0;
        var disabled = (draw->State & 6) != 0;
        if (selected && !disabled)
        {
            var highlight = bounds;
            highlight.Left += MenuPixels(4);
            highlight.Right -= MenuPixels(4);
            highlight.Top += MenuPixels(2);
            highlight.Bottom -= MenuPixels(2);
            var brush = NativeMethods.CreateSolidBrush(_menuPalette.Hover);
            NativeCallException.Require(brush != 0, "CreateSolidBrush(menu hover)");
            var previousBrush = NativeMethods.SelectObject(draw->Dc, brush);
            var previousPen = NativeMethods.SelectObject(draw->Dc, NativeMethods.GetStockObject(8)); // NULL_PEN.
            try
            {
                NativeMethods.RoundRect(draw->Dc, highlight.Left, highlight.Top, highlight.Right, highlight.Bottom,
                    MenuPixels(8), MenuPixels(8));
            }
            finally
            {
                NativeMethods.SelectObject(draw->Dc, previousPen);
                NativeMethods.SelectObject(draw->Dc, previousBrush);
                NativeMethods.DeleteObject(brush);
            }
        }
        var saved = NativeMethods.SaveDC(draw->Dc);
        try
        {
            NativeMethods.SetBkMode(draw->Dc, 1);
            NativeMethods.SetTextColor(draw->Dc, disabled ? _menuPalette.Disabled : selected ? _menuPalette.SelectedText : _menuPalette.Text);
            WithMenuFont(draw->Dc, IsHostMenuRow(item) || (draw->State & 0x1000) != 0, () =>
            {
                var textBounds = bounds;
                textBounds.Left += MenuPixels(36);
                textBounds.Right -= MenuPixels(28);
                // Only fixed command labels contain tabs, e.g. "Settings...\tCtrl+,".
                // AppHost names and paths already escape literal tabs in Literal().
                var parts = ReadMenuPaintText(item).Split('\t', 2);
                NativeMethods.DrawText(draw->Dc, parts[0], parts[0].Length, ref textBounds, 0x4 | 0x20);
                if (parts.Length == 2)
                {
                    NativeMethods.DrawText(draw->Dc, parts[1], parts[1].Length, ref textBounds, 0x2 | 0x4 | 0x20);
                }
                return 0;
            });
            if (_menu?.Commands.TryGetValue(info.Id, out var command) == true && command.Kind is ActionKind.Documentation or ActionKind.Settings)
            {
                // Utility bitmaps are dark ink. Draw these font glyphs in the current
                // foreground color so they remain legible in dark and contrast themes.
                var glyph = command.Kind == ActionKind.Documentation ? "\uE8F1" : "\uE713";
                var iconBounds = bounds;
                iconBounds.Left += MenuPixels(10);
                iconBounds.Right = iconBounds.Left + MenuPixels(20);
                WithMenuFont(draw->Dc, false, () => NativeMethods.DrawText(draw->Dc, glyph, 1, ref iconBounds, 0x1 | 0x4 | 0x20), "Segoe MDL2 Assets");
            }
            else if (item.Bitmap != 0)
            {
                var bitmapDc = NativeMethods.CreateCompatibleDC(draw->Dc);
                var previous = NativeMethods.SelectObject(bitmapDc, item.Bitmap);
                try
                {
                    var size = MenuPixels(16);
                    NativeMethods.AlphaBlend(draw->Dc, bounds.Left + MenuPixels(12), bounds.Top + (bounds.Bottom - bounds.Top - size) / 2,
                        size, size, bitmapDc, 0, 0, _artwork!.Size, _artwork.Size, 0x01FF0000); // AC_SRC_ALPHA, full source opacity.
                }
                finally
                {
                    NativeMethods.SelectObject(bitmapDc, previous);
                    NativeMethods.DeleteDC(bitmapDc);
                }
            }
        }
        finally
        {
            NativeMethods.RestoreDC(draw->Dc, saved);
        }
        return true;
    }

    private static void FillMenuRect(nint dc, in NativeMethods.Rect bounds, uint color)
    {
        var brush = NativeMethods.CreateSolidBrush(color);
        NativeCallException.Require(brush != 0, "CreateSolidBrush(menu row)");
        try
        {
            NativeMethods.FillRect(dc, in bounds, brush);
        }
        finally
        {
            NativeMethods.DeleteObject(brush);
        }
    }

    private int WithMenuFont(nint dc, bool bold, Func<int> action, string face = "Segoe UI")
    {
        var font = NativeMethods.CreateFont(-MenuPixels(14), 0, 0, 0, bold ? 600 : 400, 0, 0, 0, 1, 0, 0, 5, 0, face);
        NativeCallException.Require(font != 0, "CreateFontW(menu)");
        var previous = NativeMethods.SelectObject(dc, font);
        try
        {
            return action();
        }
        finally
        {
            NativeMethods.SelectObject(dc, previous);
            NativeMethods.DeleteObject(font);
        }
    }

    private sealed class MenuPaintItem(nint menu, uint position, nint bitmap) : IDisposable
    {
        internal nint Menu { get; } = menu;
        internal uint Position { get; } = position;
        internal nint Bitmap { get; set; } = bitmap;
        internal nint AccessibleInfo { get; } = Marshal.AllocHGlobal(sizeof(AccessibleMenuInfo));
        private nint _accessibleText;

        internal void UpdateName(string text)
        {
            var replacement = Marshal.StringToHGlobalUni(text.Replace("&&", "&", StringComparison.Ordinal));
            *(AccessibleMenuInfo*)AccessibleInfo = new()
            {
                Signature = 0xAA0DF00D, // MSAA_MENU_SIG, defined in oleacc.h.
                TextLength = (uint)text.Replace("&&", "&", StringComparison.Ordinal).Length,
                Text = replacement
            };
            Marshal.FreeHGlobal(_accessibleText);
            _accessibleText = replacement;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_accessibleText);
            Marshal.FreeHGlobal(AccessibleInfo);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccessibleMenuInfo
    {
        public uint Signature, TextLength;
        public nint Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuMeasure
    {
        public uint ControlType, ControlId, ItemId, Width, Height;
        public nuint ItemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuDraw
    {
        public uint ControlType, ControlId, ItemId, Action, State;
        public nint Menu, Dc;
        public NativeMethods.Rect Bounds;
        public nuint ItemData;
    }
}
