// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Aspire.Tray;

/// <summary>
/// The flyout panel, the tray's only UI, shown by any activation of the notification icon.
/// Layout, glyphs and interaction live in the shared <see cref="TrayPanel"/>; this part hosts it
/// in a WS_POPUP window, forwards input, and draws the display list: shapes with the GDI+ flat
/// API (anti-aliased) and text with GDI DrawTextW (ClearType on an opaque background).
/// UI Automation sees the panel through TrayApplication.PanelAutomation.cs.
/// </summary>
internal sealed unsafe partial class TrayApplication
{
    private const string PanelClassName = "Aspire.Tray.Flyout";
    private const double PanelScreenMargin = 12;
    private const double WheelStep = 48;

    private readonly Dictionary<(PanelFontRole Role, uint Dpi), nint> _panelFonts = [];
    private TrayPanel? _panel;
    private nint _panelWindow;
    private bool _panelClassRegistered;
    private bool _panelVisible;
    private bool _panelTrackingMouse;
    private bool _panelUsesDwmCorners;
    private bool _panelPaletteDirty = true;
    private long _panelHiddenAt;
    private long _panelShownAt;
    private uint _panelDpi = 96;
    private nuint _gdiplusToken;
    private nint _panelLogo;
    private nint _panelTooltip;
    private readonly Dictionary<(string Application, int Size), nint> _appIcons = [];
    private List<(NativeMethods.Rect Bounds, string Text)> _panelTooltips = [];
    private uint _panelLogoDpi;
    private string _panelFontFace = "Segoe UI Variable Text";

    private double PanelScale => _panelDpi / 96.0;

    private void TogglePanel(bool keyboard)
    {
        if (_panelVisible && Environment.TickCount64 - _panelShownAt < 300)
        {
            // One gesture can arrive twice, such as the two clicks of a double-click or a repeated
            // NIN_KEYSELECT; the panel it just opened stays open.
            return;
        }
        if (_panelVisible || Environment.TickCount64 - _panelHiddenAt < 300)
        {
            // Clicking the icon first deactivates the panel, which already hid it; that same
            // click should not reopen it.
            HidePanel();
            return;
        }
        ShowPanel(keyboard);
    }

    private void ShowPanel(bool keyboard = false)
    {
        if (_modalDepth != 0 || _quitRequested)
        {
            return;
        }
        EnsurePanel();
        controller.PruneMissingPinnedAppHosts();
        _panel!.OpenInApplications = _installedApplications.Select(application => application.Title).ToArray();
        _panel.Update(controller.State);
        _panel.Reset();
        if (keyboard)
        {
            _panel.FocusFirst();
        }
        _panelShownAt = Environment.TickCount64;
        _panelPaletteDirty = true;
        _panelVisible = true;
        LayoutPanel(show: true);
        // Foreground activation is a request. Without it the panel never gets the
        // WM_ACTIVATE(WA_INACTIVE) that closes it when the user clicks elsewhere, so a refused
        // request would leave a topmost popup behind; close it, as a menu that cannot take the
        // foreground does, rather than keep it.
        if (NativeMethods.SetForegroundWindow(_panelWindow) == 0)
        {
            Program.Log("Windows kept the Aspire flyout in the background; it was closed again.");
            HidePanel();
            return;
        }
        NativeMethods.SetFocus(_panelWindow);
    }

    /// <summary>
    /// Whether a WM_CONTEXTMENU activation of the icon came from the keyboard (Shift+F10 or the
    /// context-menu key on the focused icon) rather than a right click, so the panel can show
    /// its keyboard focus. The notification doesn't say; a right click always has the pointer
    /// over the icon, and the keys are usually still down.
    /// </summary>
    private bool IsKeyboardContextMenu()
    {
        if (NativeMethods.GetKeyState(0x5D) < 0 || NativeMethods.GetKeyState(0x79) < 0) // VK_APPS, VK_F10
        {
            return true;
        }
        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            Size = (uint)sizeof(NativeMethods.NotifyIconIdentifier), Window = _window, Id = 1
        };
        return NativeMethods.ShellNotifyIconGetRect(in identifier, out var icon) >= 0
            && NativeMethods.GetCursorPos(out var point) != 0
            && (point.X < icon.Left || point.X >= icon.Right || point.Y < icon.Top || point.Y >= icon.Bottom);
    }

    private void HidePanel()
    {
        if (!_panelVisible)
        {
            return;
        }
        _panelVisible = false;
        _panelHiddenAt = Environment.TickCount64;
        NativeMethods.ShowWindow(_panelWindow, 0); // SW_HIDE
    }

    private void EnsurePanel()
    {
        if (_panelWindow != 0)
        {
            return;
        }
        var input = new NativeMethods.GdiplusStartupInput { GdiplusVersion = 1 };
        if (NativeMethods.GdiplusStartup(out _gdiplusToken, in input, 0) != 0)
        {
            throw new NativeCallException("GdiplusStartup");
        }
        fixed (char* name = PanelClassName)
        {
            var windowClass = new NativeMethods.WindowClass
            {
                Style = NativeMethods.CsDropShadow,
                WindowProcedure = &PanelProcedure,
                Instance = _module,
                Cursor = NativeMethods.LoadCursor(0, 32512), // IDC_ARROW
                ClassName = name
            };
            NativeCallException.Require(NativeMethods.RegisterClass(ref windowClass) != 0, "RegisterClassW(flyout)");
        }
        _panelClassRegistered = true;
        _panel = new TrayPanel(controller.State, GetPanelPalette()) { FileManagerName = "File Explorer", QuitVerb = "Exit" };
        // A tool window keeps the flyout off the taskbar and Alt+Tab; owning it by the
        // message window keeps it above that window's dialogs' z-order rules.
        _panelWindow = NativeMethods.CreateWindowEx(NativeMethods.WsExToolWindow | NativeMethods.WsExTopmost, PanelClassName, "Aspire",
            NativeMethods.WsPopup, 0, 0, 1, 1, _window, 0, _module, 0);
        NativeCallException.Require(_panelWindow != 0, "CreateWindowExW(flyout)");
        // Windows 11 rounds popups through DWM (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2).
        // Windows 10 rejects the attribute; the panel is then rounded with a window region.
        // https://learn.microsoft.com/windows/apps/desktop/modernize/apply-rounded-corners
        var preference = 2;
        _panelUsesDwmCorners = NativeMethods.DwmSetWindowAttribute(_panelWindow, 33, &preference, sizeof(int)) >= 0;
    }

    private void LayoutPanel(bool show)
    {
        var anchor = GetNotifyIconRect();
        var center = new NativeMethods.Point { X = (anchor.Left + anchor.Right) / 2, Y = (anchor.Top + anchor.Bottom) / 2 };
        var monitor = NativeMethods.MonitorFromPoint(center, 2); // MONITOR_DEFAULTTONEAREST
        var info = new NativeMethods.MonitorInfo { Size = (uint)sizeof(NativeMethods.MonitorInfo) };
        NativeCallException.Require(NativeMethods.GetMonitorInfo(monitor, ref info) != 0, "GetMonitorInfoW(flyout)");
        if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) < 0 || dpi == 0)
        {
            dpi = NativeMethods.GetDpiForWindow(_window);
        }
        _panelDpi = dpi == 0 ? 96 : dpi;
        if (_panelPaletteDirty)
        {
            _panel!.Palette = GetPanelPalette();
            _panelPaletteDirty = false;
        }
        var scale = PanelScale;
        var work = info.Work;
        var margin = (int)Math.Round(PanelScreenMargin * scale);
        _panel!.MaxHeight = Math.Min(TrayPanel.DefaultMaxHeight, (work.Bottom - work.Top - 2 * margin) / scale);
        var width = (int)Math.Round(TrayPanel.Width * scale);
        var height = (int)Math.Round(_panel.Scene.Height * scale);

        // The taskbar is on the edge where the work area is smaller than the monitor.
        int x, y;
        if (work.Top > info.Monitor.Top)
        {
            x = center.X - width / 2;
            y = work.Top + margin;
        }
        else if (work.Left > info.Monitor.Left)
        {
            x = work.Left + margin;
            y = center.Y - height / 2;
        }
        else if (work.Right < info.Monitor.Right)
        {
            x = work.Right - margin - width;
            y = center.Y - height / 2;
        }
        else
        {
            x = center.X - width / 2;
            y = work.Bottom - margin - height;
        }
        x = Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - margin - width));
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - margin - height));

        // HWND_TOPMOST; SWP_NOACTIVATE (0x10) unless showing, then SWP_SHOWWINDOW (0x40).
        NativeCallException.Require(NativeMethods.SetWindowPos(_panelWindow, -1, x, y, width, height, show ? 0x40u : 0x10u) != 0,
            "SetWindowPos(flyout)");
        if (!_panelUsesDwmCorners)
        {
            var diameter = (int)Math.Round(2 * TrayPanel.CornerRadius * scale);
            var region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
            // The system owns the region after a successful SetWindowRgn.
            if (region != 0 && NativeMethods.SetWindowRgn(_panelWindow, region, 1) == 0)
            {
                Cleanup(NativeMethods.DeleteObject(region) != 0, "DeleteObject(region)");
            }
        }
        NativeMethods.InvalidateRect(_panelWindow, 0, 0);
        SyncPanelAutomation();
        SyncPanelTooltips();
    }

    /// <summary>
    /// Keeps one tooltip tool per visible icon button. TTF_SUBCLASS lets the tooltip control watch
    /// the panel's mouse messages itself. Tools are replaced only when a tooltip or its rectangle
    /// changes (scrolling, DPI, a new view), so hovering doesn't restart the tooltip's delay.
    /// </summary>
    private void SyncPanelTooltips()
    {
        if (_panelTooltip == 0)
        {
            // The Common Controls v6 manifest selects the themed tooltip; this registers its class.
            var controls = new NativeMethods.CommonControls
            {
                Size = (uint)sizeof(NativeMethods.CommonControls), Classes = NativeMethods.IccBarClasses
            };
            NativeCallException.Require(NativeMethods.InitCommonControlsEx(in controls) != 0, "InitCommonControlsEx");
            // Owned by the panel so it stays above it and hides with it.
            _panelTooltip = NativeMethods.CreateWindowEx(NativeMethods.WsExTopmost, "tooltips_class32", "",
                NativeMethods.WsPopup | NativeMethods.TtsAlwaysTip | NativeMethods.TtsNoPrefix, 0, 0, 0, 0, _panelWindow, 0, _module, 0);
            NativeCallException.Require(_panelTooltip != 0, "CreateWindowExW(flyout tooltip)");
        }
        var tooltips = _panel!.Scene.Targets.Where(target => target.Tooltip is not null && !target.HitBounds.IsEmpty)
            .Select(target => (Bounds: ToDevice(target.HitBounds), Text: target.Tooltip!)).ToList();
        if (tooltips.SequenceEqual(_panelTooltips))
        {
            return;
        }
        for (var i = 0; i < _panelTooltips.Count; i++)
        {
            var tool = new NativeMethods.ToolInfo { Size = (uint)sizeof(NativeMethods.ToolInfo), Window = _panelWindow, Id = (nuint)i };
            NativeMethods.SendMessage(_panelTooltip, NativeMethods.TtmDelTool, 0, (nint)(&tool));
        }
        _panelTooltips = tooltips;
        for (var i = 0; i < tooltips.Count; i++)
        {
            // The control copies the text, so the pinned buffer only has to outlive the call.
            fixed (char* text = tooltips[i].Text)
            {
                var tool = new NativeMethods.ToolInfo
                {
                    Size = (uint)sizeof(NativeMethods.ToolInfo),
                    Flags = NativeMethods.TtfSubclass,
                    Window = _panelWindow,
                    Id = (nuint)i,
                    Rect = tooltips[i].Bounds,
                    Text = text
                };
                NativeCallException.Require(NativeMethods.SendMessage(_panelTooltip, NativeMethods.TtmAddTool, 0, (nint)(&tool)) != 0,
                    "TTM_ADDTOOLW(flyout)");
            }
        }
    }

    private NativeMethods.Rect GetNotifyIconRect()
    {
        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            Size = (uint)sizeof(NativeMethods.NotifyIconIdentifier), Window = _window, Id = 1
        };
        if (NativeMethods.ShellNotifyIconGetRect(in identifier, out var rect) >= 0)
        {
            return rect;
        }
        // The icon can be in the overflow area; fall back to the pointer.
        NativeCallException.Require(NativeMethods.GetCursorPos(out var point) != 0, "GetCursorPos(flyout)");
        return new() { Left = point.X, Top = point.Y, Right = point.X, Bottom = point.Y };
    }

    private void UpdatePanel(TrayViewState state)
    {
        // RefreshState also runs on the 200 ms timer; repaint only for a new state or theme.
        if (!_panelVisible || _panel is null || (ReferenceEquals(state, _panel.State) && !_panelPaletteDirty))
        {
            return;
        }
        if (_panel.Update(state) || _panelPaletteDirty)
        {
            ApplyPanelResponse(PanelResponse.Changed);
        }
    }

    private void ApplyPanelResponse(PanelResponse response)
    {
        if (response.Redraw && _panelVisible)
        {
            NativeMethods.GetWindowRect(_panelWindow, out var rect);
            if (_panelPaletteDirty || Math.Abs(rect.Bottom - rect.Top - Math.Round(_panel!.Scene.Height * PanelScale)) > 0.5)
            {
                LayoutPanel(show: false);
            }
            else
            {
                NativeMethods.InvalidateRect(_panelWindow, 0, 0);
                SyncPanelAutomation();
                SyncPanelTooltips();
            }
        }
        if (response.Action is { } action)
        {
            RunPanelAction(action);
        }
    }

    private void RunPanelAction(PanelAction action)
    {
        var id = action.Host?.Id ?? default;
        switch (action.Kind)
        {
            case PanelActionKind.OpenDashboard:
                HidePanel();
                Dispatch(new(ActionKind.Dashboard, id));
                break;
            case PanelActionKind.Stop:
                Dispatch(new(ActionKind.Stop, id));
                break;
            case PanelActionKind.Start:
                Dispatch(new(ActionKind.Start, id));
                break;
            case PanelActionKind.OpenIn:
                Dispatch(new(ActionKind.OpenIn, id, _installedApplications.FirstOrDefault(application => application.Title == action.Application)));
                break;
            case PanelActionKind.ShowInFolder:
                Dispatch(new(ActionKind.Explorer, id));
                break;
            case PanelActionKind.CopyPath:
                Dispatch(new(ActionKind.CopyPath, id));
                break;
            case PanelActionKind.TogglePin:
                Dispatch(new(ActionKind.TogglePin, id));
                break;
            case PanelActionKind.ClearRecent:
                Dispatch(new(ActionKind.ClearRecent));
                break;
            case PanelActionKind.RemoveRecent:
                Dispatch(new(ActionKind.RemoveRecent, id));
                break;
            case PanelActionKind.Documentation:
                HidePanel();
                Dispatch(new(ActionKind.Documentation));
                break;
            case PanelActionKind.Settings:
                ApplyPanelResponse(_panel!.ShowSettings(PanelSettings.Read(startupSettings, _interactiveSmoke, LogStartupFailure)));
                break;
            case PanelActionKind.SetLaunchAtSignIn:
                ApplyPanelResponse(_panel!.UpdateSettings(
                    PanelSettings.SetLaunchAtSignIn(startupSettings, action.Enabled, _interactiveSmoke, LogStartupFailure)));
                break;
            case PanelActionKind.Quit:
                HidePanel();
                Dispatch(new(ActionKind.Quit));
                break;
            case PanelActionKind.Close:
                HidePanel();
                break;
        }
    }

    private static void LogStartupFailure(Exception exception)
        => Program.Log($"Tray startup settings failed in the panel ({exception.GetType().Name}): {exception.Message}");

    private static TrayPanelPalette GetPanelPalette()
    {
        if (s_previewPalette is { } preview)
        {
            return preview;
        }
        // SPI_GETHIGHCONTRAST; HCF_HIGHCONTRASTON = 1.
        var contrast = new NativeMethods.HighContrast { Size = (uint)sizeof(NativeMethods.HighContrast) };
        if (NativeMethods.GetHighContrast(0x42, contrast.Size, ref contrast, 0) != 0 && (contrast.Flags & 1) != 0)
        {
            // COLOR_WINDOW (5), COLOR_WINDOWTEXT (8), COLOR_HIGHLIGHT (13), COLOR_GRAYTEXT (17).
            return TrayPanelPalette.FromSystemColors(SystemColor(5), SystemColor(8), SystemColor(13), SystemColor(17));
        }
        return IsLightAppTheme() ? TrayPanelPalette.Light : TrayPanelPalette.Dark;

        static PanelColor SystemColor(int index)
        {
            var value = NativeMethods.GetSysColor(index);
            return new((byte)value, (byte)(value >> 8), (byte)(value >> 16));
        }
    }

    private static bool IsLightAppTheme()
    {
        // Windows exposes the app light/dark choice only through this registry value; it is
        // absent on older builds, which were light only.
        // https://learn.microsoft.com/windows/apps/desktop/modernize/apply-windows-themes
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private void PaintPanel()
    {
        var dc = NativeMethods.BeginPaint(_panelWindow, out var paint);
        try
        {
            if (dc == 0 || _panel is null)
            {
                return;
            }
            NativeMethods.GetClientRect(_panelWindow, out var client);
            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            // Double-buffer: draw into a memory bitmap, then copy it in one BitBlt.
            var memory = NativeMethods.CreateCompatibleDC(dc);
            NativeCallException.Require(memory != 0, "CreateCompatibleDC(flyout)");
            var bitmap = NativeMethods.CreateCompatibleBitmap(dc, width, height);
            try
            {
                NativeCallException.Require(bitmap != 0, "CreateCompatibleBitmap(flyout)");
                var previous = NativeMethods.SelectObject(memory, bitmap);
                RenderPanel(memory, width, height);
                NativeCallException.Require(NativeMethods.BitBlt(dc, 0, 0, width, height, memory, 0, 0, 0x00CC0020) != 0, "BitBlt(flyout)"); // SRCCOPY
                NativeMethods.SelectObject(memory, previous);
            }
            finally
            {
                if (bitmap != 0)
                {
                    Cleanup(NativeMethods.DeleteObject(bitmap) != 0, "DeleteObject(flyout bitmap)");
                }
                Cleanup(NativeMethods.DeleteDC(memory) != 0, "DeleteDC(flyout)");
            }
        }
        finally
        {
            NativeMethods.EndPaint(_panelWindow, in paint);
        }
    }

    private void RenderPanel(nint dc, int width, int height)
    {
        var scene = _panel!.Scene;
        var palette = _panel.Palette;
        var scale = (float)PanelScale;
        RequireGdiplus(NativeMethods.GdipCreateFromHDC(dc, out var graphics), "GdipCreateFromHDC");
        try
        {
            // AntiAlias (4) and PixelOffsetModeHalf (4): pixel centers sit at .5 like AppKit and SVG,
            // so a 1-DIP line at an integer coordinate covers whole pixels.
            RequireGdiplus(NativeMethods.GdipSetSmoothingMode(graphics, 4), "GdipSetSmoothingMode");
            RequireGdiplus(NativeMethods.GdipSetPixelOffsetMode(graphics, 4), "GdipSetPixelOffsetMode");
            RequireGdiplus(NativeMethods.GdipScaleWorldTransform(graphics, scale, scale, 0), "GdipScaleWorldTransform");
            // The window is opaque: start from the panel color so the corners outside the
            // rounded body match it where DWM or the region clips them.
            FillPanelPath(graphics, PanelPath.Rectangle(new(0, 0, width / scale + 1, height / scale + 1)), palette.Background);
            var clips = new Stack<(uint State, PanelRect Bounds)>();
            foreach (var command in scene.Commands)
            {
                switch (command)
                {
                    case FillPathCommand fill:
                        FillPanelPath(graphics, fill.Path, fill.Color);
                        break;
                    case StrokePathCommand stroke:
                        StrokePanelPath(graphics, stroke.Path, stroke.Color, stroke.Width);
                        break;
                    case TextCommand text:
                        WithGdiDc(graphics, clips, gdi => DrawPanelText(gdi, text, palette));
                        break;
                    case LogoCommand logo:
                        WithGdiDc(graphics, clips, gdi => DrawPanelLogo(gdi, logo.Bounds));
                        break;
                    case AppIconCommand appIcon:
                        var rect = ToDevice(appIcon.Bounds);
                        if (GetAppIcon(appIcon.Application, rect.Right - rect.Left) is not 0 and var icon)
                        {
                            WithGdiDc(graphics, clips, gdi => NativeCallException.Require(
                                NativeMethods.DrawIconEx(gdi, rect.Left, rect.Top, icon, rect.Right - rect.Left, rect.Bottom - rect.Top, 0, 0, 3) != 0,
                                "DrawIconEx(application icon)")); // DI_NORMAL
                        }
                        else
                        {
                            foreach (var shape in appIcon.Fallback)
                            {
                                if (shape is FillPathCommand fallbackFill)
                                {
                                    FillPanelPath(graphics, fallbackFill.Path, fallbackFill.Color);
                                }
                                else if (shape is StrokePathCommand fallbackStroke)
                                {
                                    StrokePanelPath(graphics, fallbackStroke.Path, fallbackStroke.Color, fallbackStroke.Width);
                                }
                            }
                        }
                        break;
                    case PushClipCommand clip:
                        RequireGdiplus(NativeMethods.GdipSaveGraphics(graphics, out var state), "GdipSaveGraphics");
                        // CombineModeIntersect (1).
                        RequireGdiplus(NativeMethods.GdipSetClipRect(graphics, (float)clip.Bounds.X, (float)clip.Bounds.Y,
                            (float)clip.Bounds.Width, (float)clip.Bounds.Height, 1), "GdipSetClipRect");
                        clips.Push((state, clips.Count == 0 ? clip.Bounds : clips.Peek().Bounds.Intersect(clip.Bounds)));
                        break;
                    case PopClipCommand:
                        RequireGdiplus(NativeMethods.GdipRestoreGraphics(graphics, clips.Pop().State), "GdipRestoreGraphics");
                        break;
                }
            }
        }
        finally
        {
            NativeMethods.GdipDeleteGraphics(graphics);
        }
    }

    private void WithGdiDc(nint graphics, Stack<(uint State, PanelRect Bounds)> clips, Action<nint> draw)
    {
        // GDI text must not interleave with buffered GDI+ output: GdipGetDC flushes GDI+ and
        // lends the DC until GdipReleaseDC. The DC does not carry the GDI+ clip or transform,
        // so apply the current clip in device pixels here.
        RequireGdiplus(NativeMethods.GdipGetDC(graphics, out var dc), "GdipGetDC");
        var saved = NativeMethods.SaveDC(dc);
        try
        {
            if (clips.Count != 0)
            {
                var clip = ToDevice(clips.Peek().Bounds);
                NativeMethods.IntersectClipRect(dc, clip.Left, clip.Top, clip.Right, clip.Bottom);
            }
            draw(dc);
        }
        finally
        {
            NativeMethods.RestoreDC(dc, saved);
            NativeMethods.GdipReleaseDC(graphics, dc);
        }
    }

    private static void FillPanelPath(nint graphics, PanelPath source, PanelColor color)
    {
        var path = CreateGdiplusPath(source);
        try
        {
            RequireGdiplus(NativeMethods.GdipCreateSolidFill(ToArgb(color), out var brush), "GdipCreateSolidFill");
            try
            {
                RequireGdiplus(NativeMethods.GdipFillPath(graphics, brush, path), "GdipFillPath");
            }
            finally
            {
                NativeMethods.GdipDeleteBrush(brush);
            }
        }
        finally
        {
            NativeMethods.GdipDeletePath(path);
        }
    }

    private static void StrokePanelPath(nint graphics, PanelPath source, PanelColor color, double width)
    {
        var path = CreateGdiplusPath(source);
        try
        {
            // UnitWorld (0) so the width scales with the DPI transform; LineCapRound (2), LineJoinRound (2).
            RequireGdiplus(NativeMethods.GdipCreatePen1(ToArgb(color), (float)width, 0, out var pen), "GdipCreatePen1");
            try
            {
                RequireGdiplus(NativeMethods.GdipSetPenLineCap197819(pen, 2, 2, 2), "GdipSetPenLineCap");
                RequireGdiplus(NativeMethods.GdipSetPenLineJoin(pen, 2), "GdipSetPenLineJoin");
                RequireGdiplus(NativeMethods.GdipDrawPath(graphics, pen, path), "GdipDrawPath");
            }
            finally
            {
                NativeMethods.GdipDeletePen(pen);
            }
        }
        finally
        {
            NativeMethods.GdipDeletePath(path);
        }
    }

    /// <summary>
    /// Converts a shared path to a GDI+ path through GdipCreatePath2's point and type arrays.
    /// Types: 0 starts a figure, 1 is a line point, 3 is a Bézier point (three per curve),
    /// and the 0x80 flag on a figure's last point closes it.
    /// https://learn.microsoft.com/windows/win32/api/gdipluspath/nf-gdipluspath-graphicspath-graphicspath(constpointf_constbyte_int_fillmode)
    /// </summary>
    private static nint CreateGdiplusPath(PanelPath source)
    {
        var points = new List<NativeMethods.PointF>();
        var types = new List<byte>();
        foreach (var segment in source.Segments)
        {
            switch (segment.Verb)
            {
                case PanelPathVerb.MoveTo:
                    points.Add(new() { X = (float)segment.X3, Y = (float)segment.Y3 });
                    types.Add(0);
                    break;
                case PanelPathVerb.LineTo:
                    points.Add(new() { X = (float)segment.X3, Y = (float)segment.Y3 });
                    types.Add(1);
                    break;
                case PanelPathVerb.CubicTo:
                    points.Add(new() { X = (float)segment.X1, Y = (float)segment.Y1 });
                    points.Add(new() { X = (float)segment.X2, Y = (float)segment.Y2 });
                    points.Add(new() { X = (float)segment.X3, Y = (float)segment.Y3 });
                    types.Add(3);
                    types.Add(3);
                    types.Add(3);
                    break;
                case PanelPathVerb.Close when types.Count > 0:
                    types[^1] |= 0x80;
                    break;
            }
        }
        var pointArray = points.ToArray();
        var typeArray = types.ToArray();
        fixed (NativeMethods.PointF* pointData = pointArray)
        fixed (byte* typeData = typeArray)
        {
            // FillModeAlternate (0).
            RequireGdiplus(NativeMethods.GdipCreatePath2(pointData, typeData, pointArray.Length, 0, out var path), "GdipCreatePath2");
            return path;
        }
    }

    private void DrawPanelText(nint dc, TextCommand text, TrayPanelPalette palette)
    {
        var font = GetPanelFont(dc, text.Font);
        var previous = NativeMethods.SelectObject(dc, font);
        try
        {
            NativeMethods.SetBkMode(dc, 1); // TRANSPARENT
            // GDI text cannot blend, so resolve any alpha against the panel background.
            var color = text.Color.A == 255 ? text.Color : text.Color.Over(palette.Background);
            NativeMethods.SetTextColor(dc, (uint)(color.R | (color.G << 8) | (color.B << 16)));
            var rect = ToDevice(text.Bounds);
            // DT_NOPREFIX keeps '&' in paths literal.
            var format = 0x800u | text.Alignment switch
            {
                PanelTextAlignment.Center => 0x1u, // DT_CENTER
                PanelTextAlignment.Trailing => 0x2u, // DT_RIGHT
                _ => 0u
            };
            if (text.MaxLines > 1)
            {
                // DT_WORDBREAK | DT_EDITCONTROL (hide a partial last line) | DT_END_ELLIPSIS, top-aligned.
                format |= 0x10 | 0x2000 | 0x8000;
            }
            else
            {
                // DT_SINGLELINE | DT_END_ELLIPSIS (0x8000), top-aligned: the box's top is placed one
                // ascent above the scene's baseline (in whole device pixels), so the baseline lands
                // there exactly. DT_VCENTER centered Segoe UI's line box instead, whose ascent and
                // descent differ from SF's, so text sat at a different height than on macOS.
                format |= 0x20 | 0x8000;
                NativeCallException.Require(NativeMethods.GetTextMetrics(dc, out var metrics) != 0, "GetTextMetricsW(flyout)");
                rect.Top = (int)Math.Round(text.Baseline * PanelScale) - metrics.Ascent;
                rect.Bottom = rect.Top + metrics.Height;
            }
            var value = text.MaxLines <= 1 && text.Truncation == PanelTruncation.Middle
                ? FitMiddle(dc, text.Text, rect.Right - rect.Left)
                : text.Text;
            NativeMethods.DrawText(dc, value, value.Length, ref rect, format);
        }
        finally
        {
            NativeMethods.SelectObject(dc, previous);
        }
    }

    // Middle truncation that matches AppKit's NSLineBreakByTruncatingMiddle: keep both ends of
    // "Running · C:\work\shop\AppHost" and replace the middle with an ellipsis. DT_PATH_ELLIPSIS
    // is not equivalent: it always keeps the text after the last backslash, so on a narrow row
    // it shrinks the status prefix to "Ru…" while the path tail stays intact.
    private static string FitMiddle(nint dc, string value, int width)
    {
        if (MeasureText(dc, value) <= width)
        {
            return value;
        }

        // Cut on text-element boundaries so a surrogate pair or combining sequence stays whole.
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        string Build(int keep)
        {
            var tail = keep / 2;
            var head = keep - tail;
            return value[..(head < elements.Length ? elements[head] : value.Length)] + "\u2026" +
                (tail == 0 ? string.Empty : value[elements[^tail]..]);
        }

        // Binary search the largest number of kept text elements that still fits.
        var low = 0;
        var high = elements.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (MeasureText(dc, Build(middle)) <= width)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        return Build(low);
    }

    private static int MeasureText(nint dc, string value)
    {
        var rect = new NativeMethods.Rect();
        // DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX measures with the font selected into dc.
        NativeMethods.DrawText(dc, value, value.Length, ref rect, 0x400 | 0x20 | 0x800);
        return rect.Right - rect.Left;
    }

    private void DrawPanelLogo(nint dc, PanelRect bounds)
    {
        var rect = ToDevice(bounds);
        var size = rect.Right - rect.Left;
        if (_panelLogo == 0 || _panelLogoDpi != _panelDpi)
        {
            if (_panelLogo != 0)
            {
                Cleanup(NativeMethods.DestroyIcon(_panelLogo) != 0, "DestroyIcon(flyout logo)");
            }
            _panelLogo = NativeMethods.LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Aspire.ico"),
                NativeMethods.ImageIcon, size, size, NativeMethods.LrLoadFromFile);
            NativeCallException.Require(_panelLogo != 0, "LoadImageW(flyout logo)");
            _panelLogoDpi = _panelDpi;
        }
        NativeCallException.Require(NativeMethods.DrawIconEx(dc, rect.Left, rect.Top, _panelLogo, size, size, 0, 0, 3) != 0,
            "DrawIconEx(flyout logo)"); // DI_NORMAL
    }

    /// <summary>
    /// Returns the icon of an installed "Open in" application at a pixel size, or 0 when it has
    /// none that the shell can extract (an app execution alias such as wt.exe is a reparse point
    /// without icon resources), in which case the panel's generic glyph is drawn.
    /// </summary>
    private nint GetAppIcon(string title, int size)
    {
        if (!_appIcons.TryGetValue((title, size), out var icon))
        {
            var executable = _installedApplications.FirstOrDefault(application => application.Title == title)?.Executable;
            if (executable is null || NativeMethods.SHDefExtractIcon(executable, 0, 0, out icon, null, (uint)size) != 0)
            {
                icon = 0;
            }
            _appIcons.Add((title, size), icon);
        }
        return icon;
    }

    private nint GetPanelFont(nint dc, PanelFontRole role)
    {
        if (_panelFonts.TryGetValue((role, _panelDpi), out var font))
        {
            return font;
        }
        // A negative height selects the em size, which matches the DIP sizes used by AppKit.
        // FW_SEMIBOLD (600) or FW_NORMAL (400); CLEARTYPE_QUALITY (5).
        var height = -(int)Math.Round(PanelFonts.Size(role) * PanelScale);
        var weight = PanelFonts.IsSemibold(role) ? 600 : 400;
        font = NativeMethods.CreateFont(height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, _panelFontFace);
        NativeCallException.Require(font != 0, "CreateFontW(flyout)");
        if (_panelFontFace != "Segoe UI" && !UsesFace(dc, font, _panelFontFace))
        {
            // Segoe UI Variable ships with Windows 11; Windows 10 substitutes another face.
            Cleanup(NativeMethods.DeleteObject(font) != 0, "DeleteObject(flyout font)");
            _panelFontFace = "Segoe UI";
            return GetPanelFont(dc, role);
        }
        _panelFonts.Add((role, _panelDpi), font);
        return font;

        static bool UsesFace(nint dc, nint font, string face)
        {
            var previous = NativeMethods.SelectObject(dc, font);
            try
            {
                char* buffer = stackalloc char[64];
                var length = NativeMethods.GetTextFace(dc, 64, buffer);
                return length > 0 && new string(buffer, 0, length - 1).Equals(face, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                NativeMethods.SelectObject(dc, previous);
            }
        }
    }

    private NativeMethods.Rect ToDevice(PanelRect rect)
    {
        var scale = PanelScale;
        return new()
        {
            Left = (int)Math.Round(rect.X * scale),
            Top = (int)Math.Round(rect.Y * scale),
            Right = (int)Math.Round(rect.Right * scale),
            Bottom = (int)Math.Round(rect.Bottom * scale)
        };
    }

    private static uint ToArgb(PanelColor color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);

    private static void RequireGdiplus(int status, string operation)
    {
        if (status != 0)
        {
            throw new NativeCallException($"{operation} (GDI+ status {status})");
        }
    }

    private nint HandlePanelMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (_panel is null)
        {
            return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        }
        // Client coordinates are signed 16-bit values in device pixels.
        var x = unchecked((short)((nuint)lParam & 0xFFFF)) / PanelScale;
        var y = unchecked((short)(((nuint)lParam >> 16) & 0xFFFF)) / PanelScale;
        switch (message)
        {
            case NativeMethods.WmPaint:
                PaintPanel();
                return 0;
            case NativeMethods.WmEraseBackground:
                return 1;
            case NativeMethods.WmMouseMove:
                if (!_panelTrackingMouse)
                {
                    // TME_LEAVE (2) asks for a single WM_MOUSELEAVE.
                    var track = new NativeMethods.TrackMouseEventData
                    {
                        Size = (uint)sizeof(NativeMethods.TrackMouseEventData), Flags = 2, Window = window
                    };
                    _panelTrackingMouse = NativeMethods.TrackMouseEvent(ref track) != 0;
                }
                ApplyPanelResponse(_panel.PointerMoved(x, y));
                return 0;
            case NativeMethods.WmMouseLeave:
                _panelTrackingMouse = false;
                ApplyPanelResponse(_panel.PointerLeft());
                return 0;
            case NativeMethods.WmLeftButtonDown:
                NativeMethods.SetCapture(window);
                ApplyPanelResponse(_panel.PointerDown(x, y));
                return 0;
            case NativeMethods.WmLeftButtonUp:
                NativeMethods.ReleaseCapture();
                ApplyPanelResponse(_panel.PointerUp(x, y));
                return 0;
            case NativeMethods.WmMouseWheel:
                // WHEEL_DELTA (120) per notch; a positive delta means the wheel moved away from the user.
                var delta = unchecked((short)((wParam >> 16) & 0xFFFF));
                ApplyPanelResponse(_panel.Scroll(delta / 120.0 * WheelStep));
                return 0;
            case NativeMethods.WmKeyDown when wParam == 0xBC && NativeMethods.GetKeyState(0x11) < 0:
                // Ctrl+comma (VK_OEM_COMMA with VK_CONTROL) opens Settings, as in other Windows apps.
                if (!_panel.IsSettingsOpen)
                {
                    RunPanelAction(new(PanelActionKind.Settings));
                }
                return 0;
            case NativeMethods.WmKeyDown:
                PanelKey? key = (int)wParam switch
                {
                    0x26 => PanelKey.Up,
                    0x28 => PanelKey.Down,
                    0x09 => NativeMethods.GetKeyState(0x10) < 0 ? PanelKey.ShiftTab : PanelKey.Tab,
                    0x0D => PanelKey.Enter,
                    0x20 => PanelKey.Space,
                    0x1B => PanelKey.Escape,
                    0x24 => PanelKey.Home,
                    0x23 => PanelKey.End,
                    _ => null
                };
                if (key is { } value)
                {
                    ApplyPanelResponse(_panel.Key(value));
                    return 0;
                }
                break;
            case NativeMethods.WmActivate when (wParam & 0xFFFF) == 0 && _previewExport is null:
                // WA_INACTIVE: clicking anywhere else closes the flyout, like a menu. A pending
                // smoke preview capture keeps it open: the console that launched it takes focus back.
                HidePanel();
                return 0;
            case NativeMethods.WmDpiChanged when _panelVisible:
                LayoutPanel(show: false);
                return 0;
            case NativeMethods.WmGetObject when unchecked((int)lParam) == NativeMethods.UiaRootObjectId:
                return HandlePanelGetObject(window, wParam, lParam);
            case WmPanelAutomationInvoke:
                InvokeFromAutomation((int)wParam);
                return 0;
        }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private void DisposePanel()
    {
        if (_panelTooltip != 0)
        {
            Cleanup(NativeMethods.DestroyWindow(_panelTooltip) != 0, "DestroyWindow(flyout tooltip)");
            _panelTooltip = 0;
            _panelTooltips = [];
        }
        if (_panelWindow != 0)
        {
            DisposePanelAutomation();
            Cleanup(NativeMethods.DestroyWindow(_panelWindow) != 0, "DestroyWindow(flyout)");
            _panelWindow = 0;
        }
        if (_panelClassRegistered)
        {
            Cleanup(NativeMethods.UnregisterClass(PanelClassName, _module) != 0, "UnregisterClassW(flyout)");
            _panelClassRegistered = false;
        }
        foreach (var font in _panelFonts.Values)
        {
            Cleanup(NativeMethods.DeleteObject(font) != 0, "DeleteObject(flyout font)");
        }
        _panelFonts.Clear();
        if (_panelLogo != 0)
        {
            Cleanup(NativeMethods.DestroyIcon(_panelLogo) != 0, "DestroyIcon(flyout logo)");
            _panelLogo = 0;
        }
        foreach (var icon in _appIcons.Values.Where(icon => icon != 0))
        {
            Cleanup(NativeMethods.DestroyIcon(icon) != 0, "DestroyIcon(application icon)");
        }
        _appIcons.Clear();
        if (_gdiplusToken != 0)
        {
            NativeMethods.GdiplusShutdown(_gdiplusToken);
            _gdiplusToken = 0;
        }
        _panelVisible = false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint PanelProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } application && window == application._panelWindow)
            {
                return application.HandlePanelMessage(window, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Managed exceptions must not cross user32 callback frames.
            if (s_current is { } application)
            {
                application._callbackFailure ??= ex;
                application.ExitCode = 1;
                Program.Log($"Flyout callback failed: {ex.Message}");
                application.QuitOnLoop();
            }
            return 0;
        }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }
}
