// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private FolderApplication[] _installedApplications = [];
    private bool _menuStructureDirty;

    private void RefreshMenu()
    {
        controller.PruneMissingPinnedAppHosts();
        var state = controller.State;
        var retained = _menuOpen || _modalDepth != 0 || _dispatchDepth != 0;
        if (_artwork is null || (_dpiDirty && !retained))
        {
            var replacement = new Artwork(this, NativeMethods.GetDpiForWindow(_window));
            _menu?.Dispose();
            _menu = null;
            var previous = _artwork;
            _artwork = replacement;
            _iconState = null;
            _displayedState = null;
            UpdateTrayIcon(state);
            previous?.Dispose();
            _dpiDirty = false;
        }
        UpdateTrayIcon(state);
        if (ReferenceEquals(state, _displayedState) && (retained || !_menuStructureDirty))
        {
            return;
        }
        if (retained && _menu is not null)
        {
            UpdateRetainedMenu(_menu, state);
            _menuStructureDirty = true;
        }
        else
        {
            var replacement = BuildMenu(state);
            _menu?.Dispose();
            _menu = replacement;
            _menuStructureDirty = false;
        }
        _displayedState = state;
    }

    private NativeMenu BuildMenu(TrayViewState state)
    {
        var root = new NativeMenu(this);
        try
        {
            if (state.ShowStatus)
            {
                root.StatusPosition = (uint)NativeMethods.GetMenuItemCount(root.Handle);
                Append(root.Handle, NativeMethods.MfGrayed, 0, state.Status);
            }
            foreach (var host in state.AppHosts)
            {
                AddHost(root, root.Handle, host, recent: false, state.Discovery == DiscoveryState.Live);
            }
            Append(root.Handle, NativeMethods.MfSeparator, 0, null);
            var recent = AddSubmenu(root.Handle, "Recently Opened");
            root.RecentHandle = recent;
            foreach (var host in state.RecentAppHosts)
            {
                AddHost(root, recent, host, recent: true, state.Discovery == DiscoveryState.Live);
            }
            if (state.RecentAppHosts.Count == 0)
            {
                Append(recent, NativeMethods.MfGrayed, 0, "No recently opened AppHosts");
            }
            Append(recent, NativeMethods.MfSeparator, 0, null);
            root.ClearCommand = AddAction(root, recent, "Clear Recently Opened", new(ActionKind.ClearRecent), state.CanClearRecent);
            Append(root.Handle, NativeMethods.MfSeparator, 0, null);
            AddAction(root, root.Handle, "Documentation", new(ActionKind.Documentation), true);
            var settings = AddAction(root, root.Handle, "Settings...", new(ActionKind.Settings), true);
            SetSettingsMenuLabel(root.Handle, settings);
            Append(root.Handle, NativeMethods.MfSeparator, 0, null);
            AddAction(root, root.Handle, "Quit Aspire", new(ActionKind.Quit), true);
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private void AddHost(NativeMenu root, nint parent, AppHostMenuItem host, bool recent, bool discoveryAvailable)
    {
        var position = (uint)NativeMethods.GetMenuItemCount(parent);
        var title = AppHostPresentation.GetCompactMenuLabel(host);
        var submenu = AddSubmenu(parent, title);
        SetStatusBitmap(parent, position, _artwork!.Status(GetMenuStatusHealth(host, discoveryAvailable)));
        var row = new HostRow(parent, position, submenu, host.Id, recent, title)
        {
            DetailsText = AppHostPresentation.GetMenuDetailsText(host.Id.AppHostPath, host.Subtitle)
        };
        root.Rows.Add(row);
        if (host.IsRunning)
        {
            row.Dashboard = AddAction(root, submenu, "Open Dashboard",
                new(ActionKind.Dashboard, host.Id), host.CanOpenDashboard);
            row.Stop = AddAction(root, submenu, host.IsStopping ? "Stopping..." : StopMenuLabel,
                new(ActionKind.Stop, host.Id), host.CanStop);
        }
        else
        {
            row.Start = AddAction(root, submenu, host.IsStarting ? "Starting..." : "Start",
                new(ActionKind.Start, host.Id), host.CanStart);
        }
        row.Pin = AddAction(root, submenu, host.IsPinned ? "Unpin" : "Pin", new(ActionKind.TogglePin, host.Id), true);
        Append(submenu, NativeMethods.MfSeparator, 0, null);
        row.Explorer = AddAction(root, submenu, "Show in Explorer", new(ActionKind.Explorer, host.Id), true);
        row.CopyPath = AddAction(root, submenu, "Copy Path", new(ActionKind.CopyPath, host.Id), true);
        row.OpenIn = AddSubmenu(submenu, "Open In");
        foreach (var application in _installedApplications)
        {
            var id = AddAction(root, row.OpenIn, application.Title, new(ActionKind.OpenIn, host.Id, application), true);
            row.OpenCommands.Add(id);
        }
        if (_installedApplications.Length == 0)
        {
            Append(row.OpenIn, NativeMethods.MfGrayed, 0, "No supported applications installed");
        }
        Append(submenu, NativeMethods.MfSeparator, 0, null);
        row.DetailsPosition = (uint)NativeMethods.GetMenuItemCount(submenu);
        Append(submenu, NativeMethods.MfGrayed, 0, AppHostPresentation.GetMenuDetailsLabel(row.DetailsText));
    }

    private void UpdateRetainedMenu(NativeMenu menu, TrayViewState state)
    {
        if (menu.StatusPosition is uint statusPosition)
        {
            UpdateItem(menu.Handle, statusPosition, true, state.ShowStatus ? state.Status : "Aspire", false);
        }
        foreach (var row in menu.Rows)
        {
            // A row stays bound to the original lifetime even if a PID/path is reused while
            // TrackPopupMenuEx or MessageBox pumps a nested loop. Never rebind a command ID.
            var host = (row.Recent ? state.RecentAppHosts : state.AppHosts).SingleOrDefault(item => item.Id == row.Id);
            UpdateItem(row.Parent, row.Position, true,
                host is null ? row.Title : AppHostPresentation.GetCompactMenuLabel(host),
                host is not null);
            SetStatusBitmap(row.Parent, row.Position, _artwork!.Status(GetMenuStatusHealth(host, state.Discovery == DiscoveryState.Live)));
            row.DetailsText = AppHostPresentation.GetMenuDetailsText(row.Id.AppHostPath, host?.Subtitle ?? "AppHost no longer available");
            UpdateItem(row.Submenu, row.DetailsPosition, true, AppHostPresentation.GetMenuDetailsLabel(row.DetailsText), false);
            UpdateAction(row.Submenu, row.Dashboard, "Open Dashboard", host?.CanOpenDashboard == true);
            UpdateAction(row.Submenu, row.Stop, host?.IsStopping == true ? "Stopping..." : StopMenuLabel, host?.CanStop == true);
            UpdateAction(row.Submenu, row.Start, host?.IsStarting == true ? "Starting..." : "Start", host?.CanStart == true);
            UpdateAction(row.Submenu, row.Pin, host?.IsPinned == true ? "Unpin" : "Pin", host is not null);
            UpdateAction(row.Submenu, row.Explorer, "Show in Explorer", host is not null);
            UpdateAction(row.Submenu, row.CopyPath, "Copy Path", host is not null);
            foreach (var id in row.OpenCommands)
            {
                UpdateAction(row.OpenIn, id, menu.Commands[id].Application!.Title, host is not null);
            }
        }
        UpdateAction(menu.RecentHandle, menu.ClearCommand, "Clear Recently Opened", state.CanClearRecent);
        if (_menuOpen)
        {
            UpdateMenuTooltip();
            // SetMenuItemInfo changes the model, but an already visible popup also needs
            // painting. Limit invalidation to this UI thread's standard menu windows.
            NativeCallException.Require(NativeMethods.EnumThreadWindows(NativeMethods.GetCurrentThreadId(), &RedrawPopup, 0) != 0,
                "EnumThreadWindows(redraw menus)");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RedrawPopup(nint window, nint parameter)
    {
        try
        {
            char* name = stackalloc char[32];
            var count = NativeMethods.GetClassName(window, name, 32);
            NativeCallException.Require(count != 0, "GetClassNameW(menu)");
            if (new ReadOnlySpan<char>(name, count).SequenceEqual("#32768"))
            {
                NativeCallException.Require(NativeMethods.RedrawWindow(window, 0, 0, 0x101) != 0, "RedrawWindow(menu)");
            }
            return 1;
        }
        catch (Exception ex)
        {
            if (s_current is { } application)
            {
                application._callbackFailure ??= ex;
                Program.Log($"Menu redraw callback failed: {ex.Message}");
            }
            return 0;
        }
    }

    private string StopMenuLabel => controller.ConfirmStop ? "Stop AppHost..." : "Stop AppHost";

    private static nint AddSubmenu(nint parent, string title)
    {
        var submenu = NativeMethods.CreatePopupMenu();
        NativeCallException.Require(submenu != 0, "CreatePopupMenu");
        try
        {
            Append(parent, NativeMethods.MfPopup, (nuint)submenu, title);
            return submenu;
        }
        catch
        {
            if (NativeMethods.DestroyMenu(submenu) == 0)
            {
                Program.Log("DestroyMenu failed for an unattached submenu.");
            }
            throw;
        }
    }

    private static uint AddAction(NativeMenu menu, nint parent, string title, ActionTarget target, bool enabled)
    {
        var id = (uint)menu.Commands.Count + 100;
        menu.Commands.Add(id, target);
        Append(parent, enabled ? 0u : NativeMethods.MfGrayed, id, title);
        UpdateItem(parent, id, false, title, enabled);
        return id;
    }

    private static string? Literal(string? text)
        // Win32 treats '&' as a mnemonic and tabs as accelerator separators.
        // Preserve paths such as C:\src\A&B\AppHost.cs as literal display text.
        => text?.Replace("&", "&&", StringComparison.Ordinal).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static void Append(nint menu, uint flags, nuint id, string? text)
        => NativeCallException.Require(NativeMethods.AppendMenu(menu, flags, id, Literal(text)) != 0, "AppendMenuW");

    private static void UpdateAction(nint menu, uint id, string title, bool enabled)
    {
        if (id != 0)
        {
            UpdateItem(menu, id, false, title, enabled);
        }
    }

    private static void SetStatusBitmap(nint menu, uint position, nint bitmap)
    {
        var info = new NativeMethods.MenuItemInfo
        {
            Size = (uint)sizeof(NativeMethods.MenuItemInfo), Mask = NativeMethods.MiimBitmap, Bitmap = bitmap
        };
        NativeCallException.Require(NativeMethods.SetMenuItemInfo(menu, position, 1, ref info) != 0, "SetMenuItemInfoW(status bitmap)");
    }

    private static void UpdateItem(nint menu, uint id, bool byPosition, string title, bool enabled)
    {
        var previous = new NativeMethods.MenuItemInfo
        {
            Size = (uint)sizeof(NativeMethods.MenuItemInfo), Mask = NativeMethods.MiimState
        };
        NativeCallException.Require(NativeMethods.GetMenuItemInfo(menu, id, byPosition ? 1 : 0, ref previous) != 0, "GetMenuItemInfoW(update)");
        fixed (char* text = Literal(title))
        {
            var info = new NativeMethods.MenuItemInfo
            {
                Size = (uint)sizeof(NativeMethods.MenuItemInfo),
                Mask = NativeMethods.MiimString | NativeMethods.MiimState,
                Text = text, State = (previous.State & ~3u) | (enabled ? 0u : NativeMethods.MfGrayed)
            };
            NativeCallException.Require(NativeMethods.SetMenuItemInfo(menu, id, byPosition ? 1 : 0, ref info) != 0, "SetMenuItemInfoW");
        }
    }

    private void ShowMenu(nuint coordinates)
    {
        var point = new NativeMethods.Point
        {
            X = unchecked((short)(coordinates & 0xFFFF)),
            Y = unchecked((short)((coordinates >> 16) & 0xFFFF))
        };
        if (point.X == -1 && point.Y == -1)
        {
            NativeCallException.Require(NativeMethods.GetCursorPos(out point) != 0, "GetCursorPos");
        }
        PrepareMenu(point);
        TrackMenu(_menu!.Handle, point);
    }

    private void PrepareMenu(NativeMethods.Point point)
    {
        // A hidden top-level window otherwise stays on the primary monitor forever. Move its
        // zero-sized bounds to the tray interaction so per-monitor menu/icon DPI follows it.
        NativeCallException.Require(NativeMethods.SetWindowPos(_window, 0, point.X, point.Y, 0, 0, 0x1 | 0x4 | 0x10) != 0, "SetWindowPos(tray monitor)");
        RefreshMenu();
    }

    private void TrackMenu(nint popup, NativeMethods.Point point)
    {
        var menu = _menu!;
        // Foreground activation is a request, not a prerequisite for owning a popup menu.
        // Background smoke runs can be denied activation by Windows' foreground lock.
        if (NativeMethods.SetForegroundWindow(_window) == 0)
        {
            Program.Log("Windows kept the Aspire tray menu in the background.");
        }
        _menuOpen = true;
        try
        {
            Marshal.SetLastPInvokeError(0);
            var selected = NativeMethods.TrackPopupMenuEx(popup,
                NativeMethods.TpmReturnCmd | NativeMethods.TpmRightButton,
                point.X, point.Y, _window, 0);
            var error = Marshal.GetLastPInvokeError();
            if (selected == 0 && error != 0)
            {
                throw new NativeCallException("TrackPopupMenuEx", error);
            }
            // Capture the immutable target before running anything that can pump messages.
            if (!_quitRequested && menu.Commands.TryGetValue(selected, out var target))
            {
                Dispatch(target);
            }
        }
        finally
        {
            HideMenuTooltip();
            _menuOpen = false;
            _displayedState = null;
            if (!_quitRequested)
            {
                RefreshMenu();
            }
        }
        // Required to dismiss notification-area menus reliably on subsequent opens.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-trackpopupmenuex
        NativeCallException.Require(NativeMethods.PostMessage(_window, NativeMethods.WmNull, 0, 0) != 0, "PostMessageW");
    }

    private void PinContextRow(nint menu, uint position)
    {
        var row = _menu?.Rows.SingleOrDefault(row => row.Parent == menu && row.Position == position);
        var host = row is null ? null : controller.State.AppHosts.SingleOrDefault(host => host.Id == row.Id);
        if (host is null || !host.IsRunning)
        {
            return;
        }
        HideMenuTooltip();
        using var context = new NativeMenu(this);
        var command = AddAction(context, context.Handle, host.IsPinned ? "Unpin" : "Pin", new(ActionKind.TogglePin, host.Id), true);
        NativeCallException.Require(NativeMethods.GetCursorPos(out var point) != 0, "GetCursorPos");
        Marshal.SetLastPInvokeError(0);
        var selected = NativeMethods.TrackPopupMenuEx(context.Handle,
            NativeMethods.TpmReturnCmd | NativeMethods.TpmNonotify | NativeMethods.TpmRightButton | 0x1,
            point.X, point.Y, _window, 0); // TPM_RECURSE permits this secondary popup.
        var error = Marshal.GetLastPInvokeError();
        if (selected == 0 && error != 0)
        {
            throw new NativeCallException("TrackPopupMenuEx(pin)", error);
        }
        if (!_quitRequested && selected == command)
        {
            Dispatch(context.Commands[command]);
        }
    }

    private enum ActionKind { Dashboard, Stop, Start, TogglePin, Explorer, CopyPath, OpenIn, ClearRecent, Documentation, Settings, Quit }

    private sealed record ActionTarget(ActionKind Kind, AppHostId Id = default, FolderApplication? Application = null);

    private sealed class HostRow(nint parent, uint position, nint submenu, AppHostId id, bool recent, string title)
    {
        internal nint Parent { get; } = parent;
        internal uint Position { get; } = position;
        internal nint Submenu { get; } = submenu;
        internal AppHostId Id { get; } = id;
        internal bool Recent { get; } = recent;
        internal string Title { get; } = title;
        internal uint DetailsPosition { get; set; }
        internal required string DetailsText { get; set; }
        internal uint Dashboard { get; set; }
        internal uint Stop { get; set; }
        internal uint Start { get; set; }
        internal uint Pin { get; set; }
        internal uint Explorer { get; set; }
        internal uint CopyPath { get; set; }
        internal nint OpenIn { get; set; }
        internal List<uint> OpenCommands { get; } = [];
    }

    private sealed class NativeMenu(TrayApplication owner) : IDisposable
    {
        private bool _disposed;
        internal nint Handle { get; } = Create();
        internal Dictionary<uint, ActionTarget> Commands { get; } = [];
        internal List<HostRow> Rows { get; } = [];
        internal uint? StatusPosition { get; set; }
        internal nint RecentHandle { get; set; }
        internal uint ClearCommand { get; set; }

        private static nint Create()
        {
            var handle = NativeMethods.CreatePopupMenu();
            NativeCallException.Require(handle != 0, "CreatePopupMenu");
            return handle;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.Cleanup(NativeMethods.DestroyMenu(Handle) != 0, "DestroyMenu");
            }
        }
    }
}
