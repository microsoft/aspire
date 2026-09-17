// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private readonly Dictionary<int, ActionTarget> _smokeActions = [];
    private SmokeDialog? _smokeDialog;
    private nint _tooltipFocusForSmoke;

    internal bool IsTrackingForSmoke => _menuOpen;
    internal bool IsTooltipVisibleForSmoke => _menuTooltipWindow != 0 && NativeMethods.IsWindowVisible(_menuTooltipWindow) != 0;
    internal nint MenuForSmoke => _menu!.Handle;
    internal nint IconForSmoke => _iconData.Icon;
    internal IReadOnlyList<AppHostId> RowIdsForSmoke => _menu!.Rows.Select(row => row.Id).ToArray();
    internal Func<bool>? DialogReadyForSmoke { get; set; }
    internal int IconAddFailuresForSmoke { get; set; }
    internal bool SuppressStopForSmoke { get; set; }

    private void ProbeRegistrationForSmoke()
    {
        var data = new NativeMethods.NotifyIconData
        {
            Size = (uint)sizeof(NativeMethods.NotifyIconData),
            Window = _window,
            Id = 2,
            Flags = _iconData.Flags,
            CallbackMessage = _iconData.CallbackMessage,
            // IDI_APPLICATION is shared system artwork; it must not be destroyed.
            Icon = NativeMethods.LoadIcon(0, 32512)
        };
        NativeCallException.Require(data.Icon != 0, "LoadIconW(smoke control)");
        Program.Log($"C# NOTIFYICONDATAW architecture={RuntimeInformation.ProcessArchitecture} size={sizeof(NativeMethods.NotifyIconData)} "
            + $"hwnd={Offset(nameof(data.Window))} id={Offset(nameof(data.Id))} flags={Offset(nameof(data.Flags))} callback={Offset(nameof(data.CallbackMessage))} "
            + $"icon={Offset(nameof(data.Icon))} tip={Offset(nameof(data.Tip))} state={Offset(nameof(data.State))} stateMask={Offset(nameof(data.StateMask))} "
            + $"info={Offset(nameof(data.Info))} version={Offset(nameof(data.Version))} title={Offset(nameof(data.InfoTitle))} "
            + $"infoFlags={Offset(nameof(data.InfoFlags))} guid={Offset(nameof(data.ItemGuid))} balloon={Offset(nameof(data.BalloonIcon))}.");
        var added = NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data);
        Program.Log($"C# stock icon NIM_ADD={added} flags=0x{data.Flags:x}.");
        if (added != 0)
        {
            NativeCallException.Require(NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data) != 0, "Shell_NotifyIconW(smoke control delete)");
        }

        static nint Offset(string field) => Marshal.OffsetOf<NativeMethods.NotifyIconData>(field);
    }

    internal void BeginInteractivePreviewForSmoke()
    {
        RequireSmoke();
        _interactiveSmoke = true;
        _smokeDeadline = null;
        ShowSettings();
    }

    internal void ShowMessageForSmoke()
    {
        RequireSmoke();
        ShowMessage("Aspire smoke", "Native message dialog.", 0);
    }

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
        var owner = _modalOwner != 0 ? _modalOwner : _window;
        var dialog = GetEnabledPopup(owner);
        if (dialog == 0 || dialog == owner)
        {
            return;
        }
        // DM_GETDEFID returns MAKELONG(default button ID, DC_HASDEFID). Verify the
        // actual native dialog, then post a button command through its normal modal loop.
        var defaultId = (nuint)NativeMethods.SendMessage(dialog, 0x400, 0, 0);
        NativeSmokeHarness.Require((defaultId & 0xFFFF) == (uint)pending.DefaultId && (defaultId >> 16) == 0x534B,
            "The actual native dialog has an unsafe default button.");
        if (_stopDetail is not null)
        {
            var checkbox = NativeMethods.GetDlgItem(dialog, StopSuppressionId);
            NativeSmokeHarness.Require(checkbox != 0 && ReadControlText(checkbox) == "&Don't ask again"
                && NativeMethods.SendMessage(checkbox, NativeMethods.BmGetCheck, 0, 0) == 0,
                "Stop confirmation must have an unchecked suppression checkbox.");
            NativeSmokeHarness.Require(ReadControlText(NativeMethods.GetDlgItem(dialog, StopDetailId)) == _stopDetail,
                "The actual stop dialog has incorrect explanatory text.");
            if (SuppressStopForSmoke)
            {
                NativeMethods.SendMessage(checkbox, NativeMethods.BmClick, 0, 0);
                NativeSmokeHarness.Require(NativeMethods.SendMessage(checkbox, NativeMethods.BmGetCheck, 0, 0) == 1,
                    "The native suppression checkbox cannot be selected.");
            }
        }
        var button = NativeMethods.GetDlgItem(dialog, pending.Response);
        NativeCallException.Require(button != 0, "GetDlgItem(smoke dialog response)");
        // WM_COMMAND/BN_CLICKED includes the button HWND in lParam.
        // https://learn.microsoft.com/windows/win32/controls/bn-clicked
        NativeCallException.Require(NativeMethods.PostMessage(dialog, NativeMethods.WmCommand, (nuint)pending.Response, button) != 0, "PostMessageW(smoke dialog response)");
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

    internal void TrackForSmoke(AppHostId id)
    {
        RequireSmoke();
        NativeCallException.Require(NativeMethods.GetCursorPos(out var point) != 0, "GetCursorPos(smoke)");
        PrepareMenu(point);
        TrackMenu(_menu!.Rows.Single(row => row.Id == id).Submenu, point);
    }

    internal void EndTrackingForSmoke()
    {
        RequireSmoke();
        NativeCallException.Require(_menuOpen && NativeMethods.EndMenu() != 0, "EndMenu(smoke)");
    }

    internal void VerifyMenuDetailsForSmoke(AppHostId id, string expected)
    {
        RequireSmoke();
        var row = _menu!.Rows.Single(row => row.Id == id);
        var last = (uint)(NativeMethods.GetMenuItemCount(row.Submenu) - 1);
        NativeSmokeHarness.Require(row.DetailsText == expected
            && ReadText(row.Submenu, last, true) == Literal(AppHostPresentation.GetMenuDetailsLabel(expected)),
            "The final AppHost menu entry has incorrect path/status text.");
    }

    internal void SelectMenuDetailsForSmoke(AppHostId id, bool details)
    {
        RequireSmoke();
        NativeSmokeHarness.Require(_menuOpen, "Menu selection requires the native tracking loop.");
        var row = _menu!.Rows.Single(row => row.Id == id);
        _tooltipFocusForSmoke = NativeMethods.GetFocus();
        // Generate real WM_MENUSELECT notifications, including for the disabled details
        // item, without moving the user's pointer or requiring foreground keyboard input.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-hilitemenuitem
        NativeCallException.Require(NativeMethods.HiliteMenuItem(_window, row.Submenu,
            details ? row.DetailsPosition : row.Pin, (details ? NativeMethods.MfByPosition : 0) | 0x80) != 0,
            "HiliteMenuItem(smoke details)");
    }

    internal void VerifyDetailsTooltipForSmoke(AppHostId id, string expected)
    {
        RequireSmoke();
        NativeSmokeHarness.Require(IsTooltipVisibleForSmoke && _menuTooltipRow?.Id == id,
            "The actual details tooltip is not visible for the selected AppHost lifetime.");
        NativeSmokeHarness.Require(NativeMethods.GetFocus() == _tooltipFocusForSmoke, "The tooltip stole keyboard focus.");
        var text = new char[expected.Length + 2];
        fixed (char* buffer = text)
        {
            var tool = MenuTooltipInfo();
            tool.Text = buffer;
            NativeMethods.SendMessage(_menuTooltipWindow, NativeMethods.TtmGetText, (nuint)text.Length, (nint)(&tool));
            NativeSmokeHarness.Require(new string(buffer) == expected, "The native tooltip must contain the full, untruncated value.");
        }
        NativeCallException.Require(NativeMethods.GetWindowRect(_menuTooltipWindow, out var bounds) != 0,
            "GetWindowRect(smoke tooltip)");
        var monitor = new NativeMethods.MonitorInfo { Size = (uint)sizeof(NativeMethods.MonitorInfo) };
        NativeCallException.Require(NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(_window, 2), ref monitor) != 0,
            "GetMonitorInfoW(smoke tooltip)");
        NativeSmokeHarness.Require(bounds.Right > bounds.Left && bounds.Bottom > bounds.Top
            && bounds.Left >= monitor.Work.Left && bounds.Right <= monitor.Work.Right
            && bounds.Top >= monitor.Work.Top && bounds.Bottom <= monitor.Work.Bottom,
            $"The tooltip must remain within the monitor work area. Tooltip: ({bounds.Left}, {bounds.Top})-({bounds.Right}, {bounds.Bottom}); work area: ({monitor.Work.Left}, {monitor.Work.Top})-({monitor.Work.Right}, {monitor.Work.Bottom}).");
    }

    internal void VerifyNoTooltipForSmoke()
    {
        RequireSmoke();
        NativeSmokeHarness.Require(!IsTooltipVisibleForSmoke && !_menuTooltipVisible && _menuTooltipRow is null,
            "The tooltip remained visible or pending after leaving the details entry.");
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
        NativeSmokeHarness.Require(_artwork is { Original: not 0, Connected: not 0, Disconnected: not 0 }
            && _artwork.Connected != _artwork.Disconnected && _artwork.Original != _artwork.Connected, "Native icon ownership is incomplete.");
        NativeSmokeHarness.Require(_iconAdded && _iconData.Icon == _artwork!.TrayIcon(GetIconState(state)),
            "The native tray icon does not represent discovery and active AppHosts.");
        NativeSmokeHarness.Require(Enum.GetValues<IconState>().Select(_artwork!.TrayIcon).Distinct().Count() == 4,
            "Idle, connecting, active, and unavailable need distinct notification icons.");
        NativeSmokeHarness.Require(Enum.GetValues<AppHostHealth>().Select(_artwork.Status).Distinct().Count() == 4,
            "Status circles must have distinct native bitmaps.");
        VerifyStatusSemanticsForSmoke();
        if (!retained)
        {
            NativeSmokeHarness.Require(menu.Rows.Select(row => row.Id).SequenceEqual(state.AppHosts.Concat(state.RecentAppHosts).Select(row => row.Id)),
                "Native host order differs from the shared view state.");
            NativeSmokeHarness.Require(NativeMethods.GetMenuItemCount(menu.Handle) == state.AppHosts.Count + 7 + (state.ShowStatus ? 1 : 0),
                "Root menu contains an unexpected header or item.");
        }
        var rootTitles = ReadMenuTitles(menu.Handle);
        NativeSmokeHarness.Require(rootTitles.TakeLast(4).SequenceEqual(new[] { "Documentation", SettingsMenuLabel, "", "Quit Aspire" })
            && (ReadItem(menu.Handle, (uint)(rootTitles.Length - 2), true).Type & NativeMethods.MfSeparator) != 0,
            "Root utility actions are missing.");
        foreach (var row in menu.Rows)
        {
            var host = (row.Recent ? state.RecentAppHosts : state.AppHosts).SingleOrDefault(host => host.Id == row.Id);
            var item = ReadItem(row.Parent, row.Position, true);
            NativeSmokeHarness.Require(item.Submenu == row.Submenu && item.Submenu != 0, "A host row is not an action submenu.");
            NativeSmokeHarness.Require(((item.State & 3) == 0) == (host is not null), "Stale row enabled state is incorrect.");
            NativeSmokeHarness.Require(item.Bitmap != 0
                && item.Bitmap == _artwork.Status(GetMenuStatusHealth(host, state.Discovery == DiscoveryState.Live)),
                "The native status circle does not represent the current AppHost.");
            VerifyAction(row.Submenu, row.Dashboard, host?.CanOpenDashboard == true);
            VerifyAction(row.Submenu, row.Stop, host?.CanStop == true);
            VerifyAction(row.Submenu, row.Start, host?.CanStart == true);
            VerifyAction(row.Submenu, row.Pin, host is not null);
            VerifyAction(row.Submenu, row.CopyPath, host is not null);
            var titles = ReadMenuTitles(row.Submenu);
            string[] firstActions = row.Dashboard != 0
                ? ["Open Dashboard", host?.IsStopping == true ? "Stopping..." : StopMenuLabel]
                : [host?.IsStarting == true ? "Starting..." : "Start"];
            NativeSmokeHarness.Require(titles[..^1].SequenceEqual(firstActions.Concat([
                host?.IsPinned == true ? "Unpin" : "Pin", "", "Show in Explorer", "Copy Path", "Open In", ""])),
                "AppHost submenu actions must precede the divider and final details entry.");
            NativeSmokeHarness.Require((ReadItem(row.Submenu, (uint)(firstActions.Length + 1), true).Type & NativeMethods.MfSeparator) != 0,
                "Pin/Unpin must be separated from the file actions by a divider.");
            NativeSmokeHarness.Require(row.DetailsPosition == (uint)(titles.Length - 1)
                && (ReadItem(row.Submenu, row.DetailsPosition - 1, true).Type & NativeMethods.MfSeparator) != 0,
                "AppHost details must be the last entry under a divider.");
            var details = ReadItem(row.Submenu, row.DetailsPosition, true);
            NativeSmokeHarness.Require((details.State & 3) != 0 && details.Id == 0 && details.Submenu == 0,
                "The AppHost details entry must be non-actionable.");
            NativeSmokeHarness.Require(ReadText(row.Parent, row.Position, true)
                == Literal(host is null ? row.Title : AppHostPresentation.GetCompactMenuLabel(host)),
                "Native AppHost labels must retain their name without a status suffix.");
            NativeSmokeHarness.Require(row.DetailsText == AppHostPresentation.GetMenuDetailsText(row.Id.AppHostPath,
                host?.Subtitle ?? "AppHost no longer available")
                && titles[^1] == Literal(AppHostPresentation.GetMenuDetailsLabel(row.DetailsText)),
                "The AppHost details entry is not up to date.");
            NativeSmokeHarness.Require(StringInfo.ParseCombiningCharacters(titles[^1].Replace("&&", "&", StringComparison.Ordinal)).Length <= 45,
                "The entire displayed path/status entry must fit within 45 characters.");
        }
        VerifyMenuStatusIcons(menu.Handle, menu.Rows.Select(row => (row.Parent, row.Position)).ToHashSet());
    }

    private static void VerifyStatusSemanticsForSmoke()
    {
        var host = new AppHostMenuItem(default, "Smoke", "", "Smoke", false, false, false, null)
        {
            IsRunning = true, Health = AppHostHealth.Healthy
        };
        foreach (var health in Enum.GetValues<AppHostHealth>())
        {
            NativeSmokeHarness.Require(GetMenuStatusHealth(host with { Health = health }, true) == health,
                "Running AppHosts must show their reported health.");
        }
        NativeSmokeHarness.Require(GetMenuStatusHealth(host with { IsStarting = true }, true) == AppHostHealth.Warning
            && GetMenuStatusHealth(host with { IsStopping = true }, true) == AppHostHealth.Warning
            && GetMenuStatusHealth(host, false) == AppHostHealth.Warning,
            "Transitional or stale discovery state must not keep a healthy green circle.");
        NativeSmokeHarness.Require(GetMenuStatusHealth(host with { Error = "Stop failed." }, true) == AppHostHealth.Unhealthy
            && GetMenuStatusHealth(host with { IsRunning = false }, true) == AppHostHealth.Unknown
            && GetMenuStatusHealth(null, true) == AppHostHealth.Unknown,
            "Errors need a red circle; stopped or removed AppHosts must be neutral.");
    }

    private static void VerifyMenuStatusIcons(nint menu, IReadOnlySet<(nint Parent, uint Position)> hostPositions)
    {
        var count = NativeMethods.GetMenuItemCount(menu);
        NativeCallException.Require(count >= 0, "GetMenuItemCount(smoke icons)");
        for (var position = 0; position < count; position++)
        {
            var item = ReadItem(menu, (uint)position, true);
            NativeSmokeHarness.Require((item.Bitmap != 0) == hostPositions.Contains((menu, (uint)position)),
                "Only AppHost rows may have status circles; action items must remain text-only.");
            if (item.Submenu != 0)
            {
                VerifyMenuStatusIcons(item.Submenu, hostPositions);
            }
        }
    }

    private static void VerifyAction(nint menu, uint id, bool enabled)
    {
        if (id != 0)
        {
            var item = ReadItem(menu, id, false);
            NativeSmokeHarness.Require(((item.State & 3) == 0) == enabled && item.Bitmap == 0, "Native action state or text-only presentation is incorrect.");
        }
    }

    private static NativeMethods.MenuItemInfo ReadItem(nint menu, uint id, bool position)
    {
        var info = new NativeMethods.MenuItemInfo
        {
            Size = (uint)sizeof(NativeMethods.MenuItemInfo),
            Mask = NativeMethods.MiimState | NativeMethods.MiimBitmap | NativeMethods.MiimSubmenu | NativeMethods.MiimId | NativeMethods.MiimFType
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

    internal nint SettingsWindowForSmoke => _settingsWindow;

    internal void ClickSettingsControlForSmoke(string control)
    {
        RequireSmoke();
        FocusSettings();
        var target = control switch
        {
            "Startup" => _settingsCheckbox,
            "Refresh" => _settingsRefresh,
            _ => throw new ArgumentException("Unknown Settings smoke control.", nameof(control))
        };
        NativeSmokeHarness.Require(target != 0 && NativeMethods.IsWindowVisible(target) != 0, "The Settings control is not visible.");
        NativeMethods.SendMessage(target, NativeMethods.BmClick, 0, 0);
    }

    internal void RefreshSettingsForSmoke()
    {
        RequireSmoke();
        if (NativeMethods.IsWindowVisible(_settingsRefresh) != 0)
        {
            ClickSettingsControlForSmoke("Refresh");
        }
        else
        {
            // Normal Settings has no refresh action; reopening reads external changes.
            ShowSettings();
        }
    }

    internal void CloseSettingsForSmoke()
    {
        RequireSmoke();
        NativeMethods.SendMessage(_settingsWindow, NativeMethods.WmClose, 0, 0);
        NativeSmokeHarness.Require(_settingsWindow == 0 && _settingsIcon == 0
            && _settingsHeadingFont == 0 && _settingsTitleFont == 0 && _settingsBackgroundBrush == 0
            && _settingsCardBrush == 0 && _settingsBorderPen == 0 && _settingsControlBounds.Count == 0,
            "Closing Settings did not release its native window, icon, fonts, and card artwork.");
    }

    internal void VerifySettingsForSmoke(uint checkState, bool enabled, string status)
    {
        RequireSmoke();
        NativeSmokeHarness.Require(_settingsWindow != 0 && _settingsIcon != 0 && _settingsMenuFilter != 0,
            "The modeless Settings window, icon, or local shortcut hook is missing.");
        NativeSmokeHarness.Require(NativeMethods.IsThemeActive() == 0 || NativeMethods.IsAppThemed() != 0,
            "The native executable did not activate Common Controls v6 visual styles.");
        NativeSmokeHarness.Require(ReadControlText(_settingsWindow) == "Aspire Tray Settings"
            && ReadControlText(NativeMethods.GetDlgItem(_settingsWindow, SettingsTitleId)) == "Settings"
            && ReadControlText(NativeMethods.GetDlgItem(_settingsWindow, SettingsGeneralId)) == "General"
            && ReadControlText(NativeMethods.GetDlgItem(_settingsWindow, SettingsAboutId)) == "About",
            "Settings sections are missing.");
        NativeSmokeHarness.Require(NativeMethods.SendMessage(_settingsCheckbox, NativeMethods.BmGetCheck, 0, 0) == (nint)checkState
            && (NativeMethods.IsWindowEnabled(_settingsCheckbox) != 0) == enabled, "The native startup checkbox misrepresents backend state.");
        NativeSmokeHarness.Require(ReadControlText(_settingsStatus) == status, "The Settings status/error text is incorrect.");
        var showDetails = status.Length != 0;
        NativeSmokeHarness.Require((NativeMethods.IsWindowVisible(_settingsStatus) != 0) == showDetails
            && (NativeMethods.IsWindowVisible(_settingsRefresh) != 0) == showDetails,
            "Startup details and Refresh must appear only when startup is unavailable or failed.");
        var expectedControls = new List<string> { "Settings", "General", "&Launch Aspire Tray when I sign in" };
        if (showDetails)
        {
            expectedControls.AddRange([status, "&Refresh startup status"]);
        }
        expectedControls.AddRange(["About", AboutVersionText, "&Close"]);
        var visibleControls = new List<string>();
        for (var control = NativeMethods.GetWindow(_settingsWindow, 5); control != 0; control = NativeMethods.GetWindow(control, 2))
        {
            // GW_CHILD / GW_HWNDNEXT enumerate the actual direct child controls.
            if (NativeMethods.IsWindowVisible(control) != 0)
            {
                visibleControls.Add(ReadControlText(control));
            }
        }
        NativeSmokeHarness.Require(visibleControls.SequenceEqual(expectedControls),
            "Settings does not expose the expected compact controls in reading and tab order.");
        char* className = stackalloc char[32];
        foreach (var label in new[] { _settingsStatus, _settingsVersion })
        {
            var length = NativeMethods.GetClassName(label, className, 32);
            NativeCallException.Require(length != 0, "GetClassNameW(Settings label)");
            NativeSmokeHarness.Require(new ReadOnlySpan<char>(className, length).Equals("Static", StringComparison.OrdinalIgnoreCase),
                "Settings information must be exposed as a label rather than an editable field.");
        }
        NativeCallException.Require(NativeMethods.GetWindowRect(_settingsStatus, out var statusBounds) != 0, "GetWindowRect(Settings status smoke)");
        NativeCallException.Require(NativeMethods.GetWindowRect(_settingsRefresh, out var refreshBounds) != 0, "GetWindowRect(Settings refresh smoke)");
        NativeCallException.Require(NativeMethods.GetWindowRect(_settingsClose, out var closeBounds) != 0, "GetWindowRect(Settings close smoke)");
        NativeCallException.Require(NativeMethods.GetWindowRect(_settingsWindow, out var windowBounds) != 0, "GetWindowRect(Settings window smoke)");
        NativeSmokeHarness.Require((!showDetails || statusBounds.Bottom < refreshBounds.Top)
            && (_settingsScrollMaximum > 0 || closeBounds.Bottom < windowBounds.Bottom),
            "Wrapping status text overlaps an action or pushes it outside Settings.");
        NativeSmokeHarness.Require((!showDetails || _settingsControlBounds[_settingsRefresh].Bottom < _settingsGeneralBounds.Bottom)
            && _settingsControlBounds[_settingsVersion].Bottom < _settingsAboutBounds.Bottom
            && _settingsGeneralBounds.Bottom < _settingsAboutBounds.Top,
            "Settings actions extend outside their section or the sections overlap.");
        NativeSmokeHarness.Require(ReadControlText(_settingsVersion) == AboutVersionText, "Settings About information is incomplete.");
        NativeSmokeHarness.Require(ReadControlText(_settingsCheckbox) == "&Launch Aspire Tray when I sign in",
            "The native checkbox is not clearly labeled.");
        NativeSmokeHarness.Require(NativeMethods.IsChild(_settingsWindow, NativeMethods.GetFocus()) != 0,
            "Settings did not retain keyboard focus.");
    }

    internal void VerifySettingsScrollingForSmoke()
    {
        RequireSmoke();
        var monitor = new NativeMethods.MonitorInfo { Size = (uint)sizeof(NativeMethods.MonitorInfo) };
        NativeCallException.Require(NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(_settingsWindow, 2), ref monitor) != 0,
            "GetMonitorInfoW(Settings scroll smoke)");
        var original = _settingsStatusText;
        try
        {
            // Size the fixture from the actual desktop so this exercises overflow even
            // on a large monitor, without changing resolution or OS accessibility settings.
            var lines = (monitor.Work.Bottom - monitor.Work.Top) / SettingsRect(0, 0, 0, 8).Bottom + 1;
            SetSettingsStatus(string.Join("\r\n", Enumerable.Repeat("Startup detail requiring additional space.", lines)));
            NativeSmokeHarness.Require(_settingsScrollMaximum > 0, "Long Settings details did not create a scrollable viewport.");
            NativeCallException.Require(NativeMethods.GetWindowRect(_settingsWindow, out var window) != 0, "GetWindowRect(Settings scroll smoke)");
            NativeSmokeHarness.Require(window.Top >= monitor.Work.Top && window.Bottom <= monitor.Work.Bottom,
                "Settings overflow extends beyond the monitor work area.");
            NativeMethods.SetFocus(_settingsClose);
            VerifySettingsFocusVisibleForSmoke(_settingsClose);
            NativeMethods.SendMessage(_settingsWindow, NativeMethods.WmVerticalScroll, 6, 0); // SB_TOP.
            NativeSmokeHarness.Require(_settingsScrollPosition == 0, "Settings scrollbar did not return to the top.");
            NativeMethods.SendMessage(_settingsWindow, NativeMethods.WmVerticalScroll, 7, 0); // SB_BOTTOM.
            var scroll = new NativeMethods.ScrollInfo { Size = (uint)sizeof(NativeMethods.ScrollInfo), Mask = 4 };
            NativeCallException.Require(NativeMethods.GetScrollInfo(_settingsWindow, 1, ref scroll) != 0, "GetScrollInfo(Settings scroll smoke)");
            NativeSmokeHarness.Require(scroll.Position == _settingsScrollMaximum && _settingsScrollPosition == scroll.Position,
                "The native scrollbar and Settings content disagree about their position.");
            NativeMethods.SetFocus(_settingsCheckbox);
            VerifySettingsFocusVisibleForSmoke(_settingsCheckbox);
        }
        finally
        {
            SetSettingsStatus(original);
        }
    }

    private void VerifySettingsFocusVisibleForSmoke(nint control)
    {
        NativeCallException.Require(NativeMethods.GetClientRect(_settingsWindow, out var client) != 0, "GetClientRect(Settings focus smoke)");
        NativeCallException.Require(NativeMethods.GetWindowRect(control, out var bounds) != 0, "GetWindowRect(Settings focus smoke)");
        var origin = new NativeMethods.Point();
        NativeCallException.Require(NativeMethods.ClientToScreen(_settingsWindow, ref origin) != 0, "ClientToScreen(Settings focus smoke)");
        NativeSmokeHarness.Require(NativeMethods.GetFocus() == control
            && bounds.Top >= origin.Y && bounds.Bottom <= origin.Y + client.Bottom,
            "Keyboard focus did not scroll the complete native Settings control into view.");
    }

    private static string ReadControlText(nint control)
    {
        var length = NativeMethods.GetWindowTextLength(control);
        var text = new char[length + 1];
        fixed (char* buffer = text)
        {
            var count = NativeMethods.GetWindowText(control, buffer, text.Length);
            NativeCallException.Require(count == length, "GetWindowTextW(Settings smoke)");
            return new string(buffer, 0, count);
        }
    }

    private sealed record SmokeDialog(int DefaultId, int Response);
}
