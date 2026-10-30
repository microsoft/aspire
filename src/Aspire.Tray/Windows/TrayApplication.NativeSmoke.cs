// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

// Smoke hooks drive the real flyout: clicks and keys go through its window procedure as
// WM_LBUTTONDOWN/UP and WM_KEYDOWN, presses through the same Invoke path UI Automation uses,
// and actions through the panel host. No browser or application is launched.
internal sealed unsafe partial class TrayApplication
{
    private SmokeDialog? _smokeDialog;

    internal nint IconForSmoke => _iconData.Icon;
    internal int IconAddFailuresForSmoke { get; set; }

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
        ShowPreviewPanel();
    }

    // PNG encoder of GDI+ (ImageCodecInfo.Clsid for image/png).
    private static readonly Guid s_pngEncoder = new("557CF406-1A04-11D3-9A73-0000F81EF32E");
    private static TrayPanelPalette? s_previewPalette;
    private string? _previewExport;
    private long _previewStateAt;
    private long _previewCaptureAt;

    /// <summary>
    /// Opens the interactive preview's panel. For alignment screenshots, environment variables
    /// pick its palette (ASPIRE_TRAY_SMOKE_PREVIEW_APPEARANCE: light or dark), its state
    /// (ASPIRE_TRAY_SMOKE_PREVIEW_STATE: hover shows the first row's buttons, actions opens its
    /// action list, settings opens the Settings view), and a JSON file to export the scene to
    /// (ASPIRE_TRAY_SMOKE_PREVIEW_EXPORT). The state is applied a moment after the panel opens,
    /// once the preview's fake AppHosts have arrived, and the scene is exported together with a
    /// PNG of the composited screen under the panel once DWM has presented it, so both describe
    /// the same frame.
    /// </summary>
    private void ShowPreviewPanel()
    {
        s_previewPalette = Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PREVIEW_APPEARANCE") switch
        {
            "light" => TrayPanelPalette.Light,
            "dark" => TrayPanelPalette.Dark,
            _ => null
        };
        ShowPanel();
        _previewStateAt = Environment.TickCount64 + 1500;
        _previewExport = Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PREVIEW_EXPORT") is { Length: > 0 } export ? export : null;
    }

    /// <summary>Applies the preview's state on the timer, after the fake AppHosts have arrived.</summary>
    private void ApplyPreviewState()
    {
        if (_previewStateAt == 0 || Environment.TickCount64 < _previewStateAt || !_panelVisible)
        {
            return;
        }
        _previewStateAt = 0;
        var panel = _panel!;
        var first = panel.Scene.Targets.FirstOrDefault(target => target.Kind == PanelTargetKind.Row);
        switch (Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PREVIEW_STATE"))
        {
            case "hover" when first is not null:
                // Straight to the panel, not through WM_MOUSEMOVE: the real pointer is elsewhere,
                // and TrackMouseEvent would report it leaving at once.
                ApplyPanelResponse(panel.PointerMoved(first.HitBounds.X + 40, first.HitBounds.CenterY));
                break;
            case "actions" when first is not null:
                ApplyPanelResponse(panel.Invoke(first.Key with { Kind = PanelTargetKind.More }));
                break;
            case "settings":
                RunPanelAction(new(PanelActionKind.Settings));
                break;
        }
        _previewCaptureAt = Environment.TickCount64 + 700;
    }

    /// <summary>
    /// Exports the scene and saves the composited screen under the panel window, as presented,
    /// once the state has been applied and painted.
    /// </summary>
    private void CapturePreviewForSmoke()
    {
        ApplyPreviewState();
        if (_previewExport is not { } export || _previewCaptureAt == 0 || Environment.TickCount64 < _previewCaptureAt || !_panelVisible)
        {
            return;
        }
        _previewExport = null;
        var path = Path.ChangeExtension(export, ".png");
        NativeCallException.Require(NativeMethods.GetWindowRect(_panelWindow, out var rect) != 0, "GetWindowRect(capture)");
        TrayPanelSceneJson.Write(export, _panel!.Scene, "Windows", Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PREVIEW_APPEARANCE") ?? "system",
            PanelScale, _panelWindow, (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var screen = NativeMethods.GetDC(0);
        NativeCallException.Require(screen != 0, "GetDC(screen)");
        var memory = NativeMethods.CreateCompatibleDC(screen);
        var bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
        try
        {
            var previous = NativeMethods.SelectObject(memory, bitmap);
            // SRCCOPY | CAPTUREBLT: read the composited desktop, including layered windows.
            NativeCallException.Require(NativeMethods.BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020 | 0x40000000) != 0,
                "BitBlt(capture)");
            NativeMethods.SelectObject(memory, previous);
            RequireGdiplus(NativeMethods.GdipCreateBitmapFromHBITMAP(bitmap, 0, out var image), "GdipCreateBitmapFromHBITMAP");
            try
            {
                RequireGdiplus(NativeMethods.GdipSaveImageToFile(image, path, in s_pngEncoder, 0), "GdipSaveImageToFile");
            }
            finally
            {
                NativeMethods.GdipDisposeImage(image);
            }
            Program.Log($"Preview captured to {path}.");
        }
        finally
        {
            Cleanup(NativeMethods.DeleteObject(bitmap) != 0, "DeleteObject(capture)");
            Cleanup(NativeMethods.DeleteDC(memory) != 0, "DeleteDC(capture)");
            NativeMethods.ReleaseDC(0, screen);
        }
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
        var button = NativeMethods.GetDlgItem(dialog, pending.Response);
        NativeCallException.Require(button != 0, "GetDlgItem(smoke dialog response)");
        // WM_COMMAND/BN_CLICKED includes the button HWND in lParam.
        // https://learn.microsoft.com/windows/win32/controls/bn-clicked
        NativeCallException.Require(NativeMethods.PostMessage(dialog, NativeMethods.WmCommand, (nuint)pending.Response, button) != 0, "PostMessageW(smoke dialog response)");
        _smokeDialog = null;
    }

    /// <summary>Shows the panel when it is hidden (Windows may deactivate it in a background run).</summary>
    private TrayPanel EnsurePanelForSmoke()
    {
        RequireSmoke();
        if (!_panelVisible)
        {
            ShowPanel();
        }
        return _panel!;
    }

    internal TrayPanel PanelForSmoke => EnsurePanelForSmoke();

    /// <summary>Finds a target, expanding the recent list and scrolling as a user would to reach it.</summary>
    private PanelHitTarget FindPanelTargetForSmoke(PanelTargetKind kind, AppHostId? host, int item = 0)
    {
        var panel = EnsurePanelForSmoke();
        if (host is { } id && !panel.IsRecentExpanded && panel.State.RecentAppHosts.Any(recent => recent.Id == id)
            && !panel.Scene.Targets.Any(target => target.Key.Host == id))
        {
            ClickPanelForSmoke(panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ToggleRecent));
        }
        var target = panel.Scene.Targets.FirstOrDefault(target => target.Kind == kind && target.Key.Host == host && target.Key.Item == item)
            ?? throw new InvalidOperationException($"The panel has no {kind} target for the AppHost.");
        if (target.HitBounds.IsEmpty)
        {
            // Scroll it into view as the wheel would. A positive delta reveals content above.
            var viewport = panel.Viewport;
            ApplyPanelResponse(panel.Scroll(target.Bounds.Y < viewport.Y ? viewport.Y - target.Bounds.Y : viewport.Bottom - target.Bounds.Bottom));
            target = panel.Scene.Targets.Single(other => other.Key == target.Key);
        }
        return target;
    }

    /// <summary>Clicks a target with real mouse messages sent to the panel's window procedure.</summary>
    internal void ClickPanelForSmoke(PanelTargetKind kind, AppHostId? host = null, int item = 0)
        => ClickPanelForSmoke(FindPanelTargetForSmoke(kind, host, item));

    private void ClickPanelForSmoke(PanelHitTarget target)
    {
        // Client coordinates in device pixels, packed as MAKELPARAM(x, y).
        var x = (int)Math.Round(target.HitBounds.CenterX * PanelScale);
        var y = (int)Math.Round(target.HitBounds.CenterY * PanelScale);
        var point = (nint)(uint)((ushort)x | ((ushort)y << 16));
        NativeMethods.SendMessage(_panelWindow, NativeMethods.WmMouseMove, 0, point);
        NativeMethods.SendMessage(_panelWindow, NativeMethods.WmLeftButtonDown, 1, point);
        NativeMethods.SendMessage(_panelWindow, NativeMethods.WmLeftButtonUp, 0, point);
    }

    /// <summary>Presses a target the way UI Automation's Invoke does.</summary>
    internal void PressPanelForSmoke(PanelTargetKind kind, AppHostId? host = null)
        => ApplyPanelResponse(_panel!.Invoke(FindPanelTargetForSmoke(kind, host).Key));

    /// <summary>Sends a key to the panel's window procedure.</summary>
    internal void SendPanelKeyForSmoke(int virtualKey)
    {
        EnsurePanelForSmoke();
        NativeMethods.SendMessage(_panelWindow, NativeMethods.WmKeyDown, (nuint)virtualKey, 0);
    }

    /// <summary>Opens a row's action list and picks one of its items.</summary>
    internal void PickRowActionForSmoke(AppHostId host, PanelTargetKind kind, int item = 0)
    {
        ClickPanelForSmoke(PanelTargetKind.More, host);
        ClickPanelForSmoke(kind, host, item);
        NativeSmokeHarness.Require(_panel!.ActionsRow is null, "Picking an action must close the action list.");
    }

    /// <summary>
    /// Opens a row's action list through UI Automation, checks its items and their order, moves
    /// into it with the keyboard, and closes it with Escape.
    /// </summary>
    internal void VerifyActionListForSmoke(AppHostId host)
    {
        PressPanelForSmoke(PanelTargetKind.More, host);
        var panel = _panel!;
        var row = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Row && target.Key.Host == host);
        var name = row.Host!.DisplayName;
        var expected = (_installedApplications.Length > 0 ? [$"Open {name} in…"] : Array.Empty<string>())
            .Concat([$"Show {name} in File Explorer", $"Copy the folder path of {name}", row.Host.IsPinned ? $"Unpin {name}" : $"Pin {name}"])
            .Concat(row.Key.Section == PanelSection.Recent ? [$"Remove {name} from recent AppHosts"] : []);
        var items = panel.Scene.Targets.Where(target => target.Key.Host == host && target.Bounds.Y > row.Bounds.Bottom).ToList();
        NativeSmokeHarness.Require(panel.ActionsRow == (row.Key.Section, host) && items.Select(target => target.AccessibleName).SequenceEqual(expected),
            $"The action list does not offer the expected items: {string.Join(", ", items.Select(target => target.AccessibleName))}.");
        SendPanelKeyForSmoke(0x28); // VK_DOWN
        NativeSmokeHarness.Require(panel.FocusedTarget == items[0].Key, "Down must move from the More button into the action list.");
        if (_installedApplications.Length > 0)
        {
            // "Open in" shows each installed application under it, drawn with its icon or the glyph.
            SendPanelKeyForSmoke(0x0D); // VK_RETURN
            NativeSmokeHarness.Require(panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.OpenIn).Select(target => target.AccessibleName)
                    .SequenceEqual(_installedApplications.Select(application => $"Open {name} in {application.Title}"))
                && panel.Scene.Commands.OfType<AppIconCommand>().Count() == _installedApplications.Length,
                "Open in must list every installed application with an icon.");
            // Paint now, so a failing icon extraction or DrawIconEx surfaces in smoke.
            NativeMethods.UpdateWindow(_panelWindow);
            SendPanelKeyForSmoke(0x1B); // VK_ESCAPE
            NativeSmokeHarness.Require(panel.FocusedTarget == items[0].Key && !panel.Scene.Targets.Any(target => target.Kind == PanelTargetKind.OpenIn),
                "Escape must hide the Open in applications and return to Open in.");
        }
        SendPanelKeyForSmoke(0x1B); // VK_ESCAPE
        NativeSmokeHarness.Require(panel.ActionsRow is null && _panelVisible && panel.FocusedTarget?.Kind == PanelTargetKind.More,
            "Escape must close the action list, keep the panel open and return focus to More.");
    }

    /// <summary>Runs an action as if the panel had produced it from a scene that is now out of date.</summary>
    internal void RunPanelActionForSmoke(PanelAction action)
    {
        EnsurePanelForSmoke();
        RunPanelAction(action);
    }

    /// <summary>Hides and reopens the panel, which revalidates pinned AppHosts like a user reopening it.</summary>
    internal void ReopenPanelForSmoke()
    {
        HidePanel();
        _panelHiddenAt = 0;
        ShowPanel();
    }

    /// <summary>
    /// Checks that every notification the icon sends opens the panel: a left click (NIN_SELECT), a
    /// right click or the context-menu key (WM_CONTEXTMENU) and keyboard selection (NIN_KEYSELECT).
    /// Keyboard activations also show the keyboard focus; for WM_CONTEXTMENU that is decided from
    /// the pointer position, so a synthetic one counts as keyboard unless the pointer happens to
    /// rest on the icon. Also checks Ctrl+comma and the Settings view with isolated startup settings.
    /// </summary>
    internal void VerifyActivationForSmoke()
    {
        RequireSmoke();
        foreach (var (notification, expected) in new (uint, bool?)[]
        {
            (NativeMethods.NinSelect, false), (NativeMethods.WmContextMenu, null), (NativeMethods.NinKeySelect, true)
        })
        {
            HidePanel();
            _panelHiddenAt = 0;
            var keyboard = expected ?? IsKeyboardContextMenu();
            NativeMethods.SendMessage(_window, NativeMethods.TrayCallback, 0, (nint)notification);
            NativeSmokeHarness.Require(_panelVisible && (_panel!.FocusedTarget is not null) == keyboard,
                $"Notification 0x{notification:x} must open the panel{(keyboard ? " with keyboard focus" : "")}.");
            // A repeated notification right after opening (a double-click) keeps it open.
            NativeMethods.SendMessage(_window, NativeMethods.TrayCallback, 0, (nint)notification);
            NativeSmokeHarness.Require(_panelVisible, "A repeated activation closed the panel it had just opened.");
            _panelShownAt = 0;
            NativeMethods.SendMessage(_window, NativeMethods.TrayCallback, 0, (nint)notification);
            NativeSmokeHarness.Require(!_panelVisible, "Activating the icon again must close the panel.");
        }
        _panelHiddenAt = 0;
        ShowPanel();
        NativeSmokeHarness.Require(!_panel!.IsSettingsOpen, "Settings opened unexpectedly.");
        // Ctrl+comma: mark Ctrl down in this thread's key state table, which GetKeyState reads,
        // rather than synthesizing global input with SendInput.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setkeyboardstate
        var keys = new byte[256];
        fixed (byte* state = keys)
        {
            NativeCallException.Require(NativeMethods.GetKeyboardState(state) != 0, "GetKeyboardState(smoke)");
            var control = keys[0x11];
            keys[0x11] = 0x80;
            NativeCallException.Require(NativeMethods.SetKeyboardState(state) != 0, "SetKeyboardState(smoke)");
            try
            {
                SendPanelKeyForSmoke(0xBC); // VK_OEM_COMMA
            }
            finally
            {
                keys[0x11] = control;
                NativeMethods.SetKeyboardState(state);
            }
        }
        NativeSmokeHarness.Require(_panel.IsSettingsOpen, "Ctrl+comma must open the Settings view in the panel.");
        SendPanelKeyForSmoke(0x1B); // VK_ESCAPE
        NativeSmokeHarness.Require(!_panel.IsSettingsOpen && _panelVisible, "Escape must leave the Settings view and keep the panel open.");
        HidePanel();
    }

    /// <summary>
    /// Opens the Settings view with the gear and checks its launch-at-sign-in switch against the
    /// isolated startup settings: <paramref name="enabled"/> is whether it can be pressed and
    /// <paramref name="description"/> the text under it.
    /// </summary>
    internal void VerifySettingsViewForSmoke(bool on, bool enabled, string description)
    {
        var panel = EnsurePanelForSmoke();
        if (!panel.IsSettingsOpen)
        {
            ClickPanelForSmoke(PanelTargetKind.Settings);
        }
        else
        {
            // Re-read the settings, as reopening the view does.
            RunPanelAction(new(PanelActionKind.Settings));
        }
        var toggle = panel.Scene.Accessibility.Single(element => element.Target?.Kind == PanelTargetKind.LaunchAtSignIn);
        NativeSmokeHarness.Require(toggle.Role == PanelAccessibleRole.Switch && toggle.Checked == on && toggle.Enabled == enabled
            && toggle.Description == description,
            $"The Settings switch misrepresents startup state: checked={toggle.Checked}, enabled={toggle.Enabled}, description={toggle.Description}.");
    }

    internal void ToggleLaunchAtSignInForSmoke()
    {
        EnsurePanelForSmoke();
        ClickPanelForSmoke(PanelTargetKind.LaunchAtSignIn);
    }

    internal void CloseSettingsViewForSmoke()
    {
        if (_panel is { IsSettingsOpen: true })
        {
            ClickPanelForSmoke(PanelTargetKind.Back);
        }
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

    internal void VerifyNativeStateForSmoke()
    {
        RequireSmoke();
        var state = controller.State;
        NativeSmokeHarness.Require(_artwork is { Original: not 0, Running: not 0, Attention: not 0, Dimmed: not 0 },
            "Native icon ownership is incomplete.");
        NativeSmokeHarness.Require(_iconAdded && _iconData.Icon == _artwork!.TrayIcon(TrayIconStatusRules.Get(state)),
            "The native tray icon does not represent discovery and active AppHosts.");
        // Connecting and unavailable deliberately share the dimmed logo; the tooltip tells them apart.
        NativeSmokeHarness.Require(Enum.GetValues<TrayIconStatus>().Select(_artwork!.TrayIcon).Distinct().Count() == 4
            && _artwork.TrayIcon(TrayIconStatus.Connecting) == _artwork.TrayIcon(TrayIconStatus.Unavailable),
            "Idle, running, attention, and dimmed need distinct notification icons.");
        var panel = EnsurePanelForSmoke();
        var rows = panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.Row && target.Key.Section != PanelSection.Recent).ToList();
        NativeSmokeHarness.Require(rows.Select(row => row.Host!.Id).SequenceEqual(
                state.AppHosts.Where(host => host.Id.AppHostPid != 0).Concat(state.AppHosts.Where(host => host.Id.AppHostPid == 0)).Select(host => host.Id)),
            "The panel does not list the AppHosts of the shared view state in order.");
        foreach (var row in rows)
        {
            var host = state.AppHosts.Single(host => host.Id == row.Host!.Id);
            bool Enabled(PanelTargetKind kind) => panel.Scene.Targets.FirstOrDefault(target => target.Key == row.Key with { Kind = kind })?.Enabled == true;
            NativeSmokeHarness.Require(row.AccessibleName.StartsWith($"{host.DisplayName}, ", StringComparison.Ordinal)
                && Enabled(PanelTargetKind.Dashboard) == host.CanOpenDashboard && Enabled(PanelTargetKind.Stop) == host.CanStop
                && Enabled(PanelTargetKind.Start) == host.CanStart, "A panel row misrepresents its AppHost's actions.");
        }
        NativeSmokeHarness.Require(_iconData.CallbackMessage == NativeMethods.TrayCallback, "The icon must report activations to the panel.");
        NativeSmokeHarness.Require(panel.Scene.Accessibility.Single(element => element.Key.Label == "header.status").Bounds.CenterY == TrayPanel.HeaderHeight / 2
            && TrayPanel.HeaderHeight == TrayPanel.FooterHeight, "The header must be one line, as tall as the footer.");
        NativeSmokeHarness.Require(_panelTooltip != 0 && _panelTooltips.Select(tooltip => tooltip.Text).SequenceEqual(
                panel.Scene.Targets.Where(target => target.Tooltip is not null && !target.HitBounds.IsEmpty).Select(target => target.Tooltip!))
            && NativeMethods.SendMessage(_panelTooltip, 0x040D, 0, 0) == _panelTooltips.Count, // TTM_GETTOOLCOUNT
            "Every visible icon button must have exactly one native tooltip.");
        NativeSmokeHarness.Require(panel.Scene.Accessibility.Single(element => element.Target?.Kind == PanelTargetKind.Quit) is
            { Name: "Exit", Description: "Exit the tray. AppHosts keep running.", Target.Tooltip: "Exit the tray. AppHosts keep running." },
            "The Exit button must say it closes only the tray.");
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
