// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private readonly Dictionary<int, ActionTarget> _smokeActions = [];
    private SmokeDialog? _smokeDialog;

    internal bool IsTrackingForSmoke => _menuOpen;
    internal nint MenuForSmoke => _menu!.Handle;
    internal nint IconForSmoke => _iconData.Icon;
    internal IReadOnlyList<AppHostId> RowIdsForSmoke => _menu!.Rows.Select(row => row.Id).ToArray();
    internal Func<bool>? DialogReadyForSmoke { get; set; }

    private void CompleteDialogForSmoke()
    {
        if (_smokeDialog is not { } pending)
        {
            return;
        }
        if (DialogReadyForSmoke?.Invoke() == false)
        {
            return;
        }
        var dialog = GetEnabledPopup();
        if (dialog == 0 || dialog == _window)
        {
            return;
        }
        // DM_GETDEFID returns MAKELONG(default button ID, DC_HASDEFID). Verify the
        // actual native dialog, then post a button command through its normal modal loop.
        var defaultId = (nuint)NativeMethods.SendMessage(dialog, 0x400, 0, 0);
        NativeSmokeHarness.Require((defaultId & 0xFFFF) == (uint)pending.DefaultId && (defaultId >> 16) == 0x534B,
            "The actual native dialog has an unsafe default button.");
        NativeCallException.Require(NativeMethods.PostMessage(dialog, 0x111, (nuint)pending.Response, 0) != 0, "PostMessageW(smoke dialog response)");
        _smokeDialog = null;
    }

    internal int CaptureActionForSmoke(string kind, AppHostId id = default)
    {
        RequireSmoke();
        var action = _menu!.Commands.Values.Single(action => action.Kind.ToString() == kind && action.Id == id);
        var token = _smokeActions.Count + 1;
        _smokeActions.Add(token, action);
        return token;
    }

    internal void InvokeActionForSmoke(int token)
    {
        RequireSmoke();
        NativeMethods.SendMessage(_window, NativeMethods.SmokeMessage, (nuint)token, 0);
    }

    internal void TrackForSmoke()
    {
        RequireSmoke();
        NativeCallException.Require(NativeMethods.GetCursorPos(out var point) != 0, "GetCursorPos(smoke)");
        ShowMenu((nuint)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16)));
    }

    internal void EndTrackingForSmoke()
    {
        RequireSmoke();
        NativeCallException.Require(_menuOpen && NativeMethods.EndMenu() != 0, "EndMenu(smoke)");
    }

    internal void RestartExplorerForSmoke()
    {
        RequireSmoke();
        NativeCallException.Require(NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref _iconData) != 0, "Shell_NotifyIconW(smoke delete)");
        _iconAdded = false;
        NativeMethods.SendMessage(_window, _taskbarCreated, 0, 0);
        NativeSmokeHarness.Require(_iconAdded, "Explorer restart did not restore the native icon.");
    }

    internal void InvalidateArtworkForSmoke()
    {
        RequireSmoke();
        // A synthetic notification exercises invalidation, not a real monitor-DPI transition.
        NativeMethods.SendMessage(_window, NativeMethods.WmDpiChanged, 0, 0);
    }

    internal void VerifyNativeStateForSmoke(bool retained = false)
    {
        RequireSmoke();
        var state = controller.State;
        var menu = _menu!;
        NativeSmokeHarness.Require(_artwork is { Original: not 0, Connected: not 0, Disconnected: not 0, Globe: not 0 }
            && _artwork.Connected != _artwork.Disconnected && _artwork.Original != _artwork.Connected, "Native icon ownership is incomplete.");
        NativeSmokeHarness.Require(_iconAdded && _iconData.Icon == (state.HasActiveAppHosts ? _artwork!.Connected : _artwork!.Disconnected),
            "The native tray icon does not represent active AppHosts.");
        if (!retained)
        {
            NativeSmokeHarness.Require(menu.Rows.Select(row => row.Id).SequenceEqual(state.AppHosts.Concat(state.RecentAppHosts).Select(row => row.Id)),
                "Native host order differs from the shared view state.");
            NativeSmokeHarness.Require(NativeMethods.GetMenuItemCount(menu.Handle) == state.AppHosts.Count + 6 + (state.ShowStatus ? 1 : 0),
                "Root menu contains an unexpected header or item.");
        }
        var rootTitles = ReadMenuTitles(menu.Handle);
        NativeSmokeHarness.Require(rootTitles.TakeLast(3).SequenceEqual(new[] { "Documentation", "About Aspire", "Quit Aspire" }),
            "Root utility actions are missing.");
        foreach (var row in menu.Rows)
        {
            var host = (row.Recent ? state.RecentAppHosts : state.AppHosts).SingleOrDefault(host => host.Id == row.Id);
            var item = ReadItem(row.Parent, row.Position, true);
            NativeSmokeHarness.Require(item.Submenu == row.Submenu && item.Submenu != 0, "A host row is not an action submenu.");
            NativeSmokeHarness.Require(((item.State & 3) == 0) == (host is not null), "Stale row enabled state is incorrect.");
            NativeSmokeHarness.Require(item.Bitmap == _artwork!.Health(host?.Health ?? AppHostHealth.Unknown, host?.IsRunning ?? false),
                "Native row health bitmap differs from shared state.");
            VerifyAction(row.Submenu, row.Dashboard, host?.CanOpenDashboard == true, _artwork.Globe);
            VerifyAction(row.Submenu, row.Stop, host?.CanStop == true);
            VerifyAction(row.Submenu, row.Start, host?.CanStart == true);
            VerifyAction(row.Submenu, row.Pin, host is not null);
            if (host is not null)
            {
                NativeSmokeHarness.Require(ReadText(row.Parent, row.Position, true) == Literal(HostLabel(host)), "Native row text was not refreshed.");
            }
        }
        var documentation = menu.Commands.Single(pair => pair.Value.Kind == ActionKind.Documentation);
        NativeSmokeHarness.Require(ReadItem(menu.Handle, documentation.Key, false).Bitmap == _artwork!.Globe,
            "Documentation and Open Dashboard must use the same icon.");
    }

    private static void VerifyAction(nint menu, uint id, bool enabled, nint bitmap = 0)
    {
        if (id != 0)
        {
            var item = ReadItem(menu, id, false);
            NativeSmokeHarness.Require(((item.State & 3) == 0) == enabled && item.Bitmap == bitmap, "Native action state or bitmap is incorrect.");
        }
    }

    private static NativeMethods.MenuItemInfo ReadItem(nint menu, uint id, bool position)
    {
        var info = new NativeMethods.MenuItemInfo
        {
            Size = (uint)sizeof(NativeMethods.MenuItemInfo),
            Mask = NativeMethods.MiimState | NativeMethods.MiimBitmap | NativeMethods.MiimSubmenu | NativeMethods.MiimId
        };
        NativeCallException.Require(NativeMethods.GetMenuItemInfo(menu, id, position ? 1 : 0, ref info) != 0, "GetMenuItemInfoW(smoke)");
        return info;
    }

    private static string ReadText(nint menu, uint id, bool position)
    {
        var info = new NativeMethods.MenuItemInfo { Size = (uint)sizeof(NativeMethods.MenuItemInfo), Mask = NativeMethods.MiimString };
        NativeCallException.Require(NativeMethods.GetMenuItemInfo(menu, id, position ? 1 : 0, ref info) != 0, "GetMenuItemInfoW(smoke text length)");
        var text = new char[info.TextLength + 1];
        fixed (char* buffer = text)
        {
            info.Text = buffer;
            info.TextLength = (uint)text.Length;
            NativeCallException.Require(NativeMethods.GetMenuItemInfo(menu, id, position ? 1 : 0, ref info) != 0, "GetMenuItemInfoW(smoke text)");
            return new string(buffer);
        }
    }

    private static string[] ReadMenuTitles(nint menu)
    {
        var count = NativeMethods.GetMenuItemCount(menu);
        NativeCallException.Require(count >= 0, "GetMenuItemCount(smoke)");
        return Enumerable.Range(0, count).Select(position => ReadText(menu, (uint)position, true)).ToArray();
    }

    private void RequireSmoke()
    {
        if (smokeSeconds is null)
        {
            throw new InvalidOperationException("Native smoke hooks are unavailable in normal mode.");
        }
    }

    private sealed record SmokeDialog(int DefaultId, int Response);
}
