// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

// Native inspection/driving hooks live apart from dashboard/stop behavior. The harness drives
// the real flyout panel: clicks go through the same pointer path as mouse events, presses through
// the VoiceOver path, and actions through the panel host. No browser is activated and no
// production timer is constructed.
internal sealed partial class MacTrayApplication
{
    private nint _smokeTimer;
    private nint _smokePreviewTimer;
    private Action? _smokeTimeout;
    private bool _interactiveSmoke;
    internal event Action<TrayViewState>? StateRendered;

    internal void VerifyInaccessibleRecentProjectForSmoke(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Native permission smoke requires macOS.");
        }
        VerifyUIThread();
        var directory = Path.GetDirectoryName(path)!;
        var mode = File.GetUnixFileMode(directory);
        var accessDenied = false;
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            try
            {
                EnsurePanelForSmoke();
                RequireProjectFile(path);
            }
            catch (UnauthorizedAccessException)
            {
                accessDenied = true;
            }
        }
        finally
        {
            File.SetUnixFileMode(directory, mode);
        }
        if (!accessDenied || !_controller.State.RecentAppHosts.Any(host => host.Id.AppHostPath == path) || _panel!.Confirmation is not null)
        {
            throw new InvalidOperationException("An inaccessible recent project must report access denied without offering to remove its history.");
        }
    }

    internal void EnableInteractiveSmoke()
    {
        _interactiveSmoke = true;
        ReleaseSmokeTimer(ref _smokeTimer);
        _smokePreviewTimer = CreateSmokeTimer(0.1, "smokeShowPreview:");
    }

    internal void SetSmokeDeadline(int seconds, Action timeout)
    {
        VerifyUIThread();
        _smokeTimeout = timeout;
        _smokeTimer = CreateSmokeTimer(seconds, "smokeTimeout:");
    }

    private nint CreateSmokeTimer(double seconds, string action)
    {
        var timer = AppKit.CreateTimer(AppKit.Class("NSTimer"),
            AppKit.Selector("timerWithTimeInterval:target:selector:userInfo:repeats:"),
            seconds, _target, AppKit.Selector(action), 0, 0);
        if (timer == 0)
        {
            throw new InvalidOperationException("Could not create the smoke deadline.");
        }
        AppKit.Get(timer, "retain");
        var runLoop = AppKit.Get(AppKit.Class("NSRunLoop"), "mainRunLoop");
        foreach (var mode in _runLoopModes)
        {
            // The watchdog must also fire inside nested event-tracking and modal loops.
            AppKit.SendVoidTwoPointers(runLoop, AppKit.Selector("addTimer:forMode:"), timer, mode);
        }
        return timer;
    }

    /// <summary>Shows the panel when it is hidden (macOS may take key status away from a background run).</summary>
    private TrayPanel EnsurePanelForSmoke()
    {
        VerifyUIThread();
        if (!_panelVisible)
        {
            ShowPanel();
        }
        return _panel!;
    }

    internal NativePanelInspection InspectPanelForSmoke()
    {
        var panel = EnsurePanelForSmoke();
        var scene = panel.Scene;
        var button = AppKit.Get(_statusItem, "button");
        var icon = AppKit.Get(button, "image");
        string? Label(string key) => scene.Accessibility.FirstOrDefault(element => element.Key == PanelAccessibleKey.For(key))?.Name;
        bool Enabled(PanelHitTarget row, PanelTargetKind kind)
            => scene.Targets.FirstOrDefault(target => target.Key == row.Key with { Kind = kind })?.Enabled == true;
        return new(
            scene.Targets.Where(target => target.Kind == PanelTargetKind.Row).Select(row => new PanelRowInspection(
                row.Host!.Id, row.Key.Section, row.AccessibleName,
                Enabled(row, PanelTargetKind.Dashboard), Enabled(row, PanelTargetKind.Stop), Enabled(row, PanelTargetKind.Start))).ToArray(),
            Label("header.status") ?? "",
            Label("empty.title"),
            Label("notice"),
            icon != 0 && AppKit.GetBool(icon, AppKit.Selector("isTemplate")) != 0,
            AppKit.Text(AppKit.Get(button, "title")) == "",
            AppKit.Get(_statusItem, "menu") == 0,
            _runLoopModes.Count == 3 && _runLoopModes.All(mode => AppKit.CFRunLoopContainsSource(_runLoop, _refreshSource, mode) != 0),
            AppKit.Text(AppKit.Get(_statusItem, "autosaveName")),
            AppKit.GetBool(_statusItem, AppKit.Selector("isVisible")) != 0,
            icon == GetTrayImage(TrayIconStatusRules.HasBadge(TrayIconStatusRules.Get(_controller.State))),
            AppKit.Text(AppKit.Get(button, "accessibilityLabel")),
            panel.Confirmation);
    }

    internal string DescribePanelForSmoke()
        => $"visible={_panelVisible}; maxHeight={_panel!.MaxHeight}; height={_panel.Scene.Height}; targets={string.Join(", ", _panel!.Scene.Targets.Select(target => $"{target.Kind}@{target.HitBounds.Y:F0}"))}";

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

    /// <summary>Clicks a target through the same pointer path as mouse events.</summary>
    internal void ClickPanelForSmoke(PanelTargetKind kind, AppHostId? host = null, int item = 0)
        => ClickPanelForSmoke(FindPanelTargetForSmoke(kind, host, item));

    /// <summary>Presses a target the way VoiceOver does (AXPress).</summary>
    internal void PressPanelForSmoke(PanelTargetKind kind, AppHostId? host = null)
        => OnPanelElementPressed(PanelAccessibleKey.For(FindPanelTargetForSmoke(kind, host).Key));

    /// <summary>Opens a row's action list and picks one of its items.</summary>
    internal void PickRowActionForSmoke(AppHostId host, PanelTargetKind kind, int item = 0)
    {
        ClickPanelForSmoke(PanelTargetKind.More, host);
        ClickPanelForSmoke(kind, host, item);
        if (_panel!.ActionsRow is not null)
        {
            throw new InvalidOperationException("Picking an action must close the action list.");
        }
    }

    /// <summary>
    /// Opens a row's action list through VoiceOver, checks its items and their order, moves into
    /// it with the keyboard, and closes it with Escape.
    /// </summary>
    internal void VerifyActionListForSmoke(AppHostId host)
    {
        PressPanelForSmoke(PanelTargetKind.More, host);
        var panel = _panel!;
        var row = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Row && target.Key.Host == host);
        var name = row.Host!.DisplayName;
        var expected = (_installedFolderApplications.Count > 0 ? [$"Open {name} in…"] : Array.Empty<string>())
            .Concat([$"Show {name} in Finder", $"Copy the folder path of {name}", row.Host.IsPinned ? $"Unpin {name}" : $"Pin {name}"])
            .Concat(row.Key.Section == PanelSection.Recent ? [$"Remove {name} from recent AppHosts"] : []);
        var items = panel.Scene.Targets.Where(target => target.Key.Host == host && target.Bounds.Y > row.Bounds.Bottom).ToList();
        if (panel.ActionsRow != (row.Key.Section, host) || !items.Select(target => target.AccessibleName).SequenceEqual(expected))
        {
            throw new InvalidOperationException($"The action list does not offer the expected items: {string.Join(", ", items.Select(target => target.AccessibleName))}.");
        }
        VerifyPanelAccessibilityForSmoke("an open action list");
        ApplyPanelResponse(panel.Key(PanelKey.Down));
        if (panel.FocusedTarget != items[0].Key)
        {
            throw new InvalidOperationException("Down must move from the More button into the action list.");
        }
        if (_installedFolderApplications.Count > 0)
        {
            // "Open in" shows each installed application under it, with its Finder icon.
            ApplyPanelResponse(panel.Key(PanelKey.Enter));
            var applications = panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.OpenIn).Select(target => target.AccessibleName);
            var icons = panel.Scene.Commands.OfType<AppIconCommand>().ToList();
            if (!applications.SequenceEqual(_installedFolderApplications.Select(application => $"Open {name} in {application.Title}"))
                || icons.Count != _installedFolderApplications.Count || icons.Any(icon => GetAppIcon(icon.Application) == 0))
            {
                throw new InvalidOperationException("Open in must list every installed application with its icon.");
            }
            VerifyPanelAccessibilityForSmoke("the Open in applications");
            ApplyPanelResponse(panel.Key(PanelKey.Escape));
            if (panel.FocusedTarget != items[0].Key || panel.Scene.Targets.Any(target => target.Kind == PanelTargetKind.OpenIn))
            {
                throw new InvalidOperationException("Escape must hide the Open in applications and return to Open in.");
            }
        }
        ApplyPanelResponse(panel.Key(PanelKey.Escape));
        if (panel.ActionsRow is not null || !_panelVisible || panel.FocusedTarget?.Kind != PanelTargetKind.More)
        {
            throw new InvalidOperationException("Escape must close the action list, keep the panel open and return focus to More.");
        }
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
    /// Checks that every way of activating the status item opens the panel and that none attaches
    /// a menu: left and right clicks, keyboard and VoiceOver activation (which also show the keyboard
    /// focus), and the button's own click, as AXPress performs it. Also checks Command-comma.
    /// </summary>
    internal void VerifyActivationForSmoke()
    {
        VerifyUIThread();
        foreach (var (eventType, keyboard) in new[] { (LeftMouseDownEvent, false), (RightMouseDownEvent, false), (0L, true) })
        {
            HidePanel();
            _panelHiddenAt = 0;
            ToggleFromStatusItem(eventType);
            if (!_panelVisible || (_panel!.FocusedTarget is not null) != keyboard)
            {
                throw new InvalidOperationException($"Activation {eventType} must open the panel{(keyboard ? " with keyboard focus" : "")}.");
            }
            _panelHiddenAt = 0;
            ToggleFromStatusItem(eventType);
            if (_panelVisible)
            {
                throw new InvalidOperationException("Activating the status item again must close the panel.");
            }
        }
        _panelHiddenAt = 0;
        AppKit.Set(AppKit.Get(_statusItem, "button"), "performClick:", 0);
        if (!_panelVisible || AppKit.Get(_statusItem, "menu") != 0)
        {
            throw new InvalidOperationException("Pressing the status item must open the panel, never a menu.");
        }
        // Command-comma (kVK_ANSI_Comma) through the panel's own key handling.
        var commandComma = AppKit.CreateKeyEvent(AppKit.Class("NSEvent"),
            AppKit.Selector("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            10, new(0, 0), (nuint)CommandKeyMask, 0, AppKit.Get(_panelWindow, "windowNumber"), 0, AppKit.String(","), AppKit.String(","), 0, 43);
        HandlePanelEvent("key", commandComma);
        if (!_panel!.IsSettingsOpen)
        {
            throw new InvalidOperationException("Command-comma must open the Settings view in the panel.");
        }
        HidePanel();
    }

    internal void VerifyStatusArtworkForSmoke()
    {
        foreach (var discovery in new[] { DiscoveryState.Disconnected, DiscoveryState.Incompatible, DiscoveryState.LimitExceeded })
        {
            if (TrayIconStatusRules.Get(new(discovery, [], "")) != TrayIconStatus.Unavailable)
            {
                throw new InvalidOperationException("Discovery failures must never use the idle artwork.");
            }
        }
        foreach (var badge in new[] { false, true })
        {
            var image = GetTrayImage(badge);
            if (AppKit.GetSize(image, AppKit.Selector("size")) != new AppKit.NativeSize(22, 22)
                || AppKit.GetBool(image, AppKit.Selector("isTemplate")) == 0)
            {
                throw new InvalidOperationException("The tray artwork must be an appearance-adaptive 22-point template.");
            }
        }
        if (AppKit.SendReturningBool(AppKit.Get(GetTrayImage(false), "TIFFRepresentation"), AppKit.Selector("isEqualToData:"),
            AppKit.Get(GetTrayImage(true), "TIFFRepresentation")) != 0)
        {
            throw new InvalidOperationException("The badged tray artwork must cut out room for the status dot.");
        }
        VerifyTrayMarkForSmoke();
        VerifyIconPixel(GetTrayImage(true), new(10, 21.5), 0, 0, "transparent top margin");
        VerifyIconPixel(GetTrayImage(true), new(0.5, 0.5), 0, 0, "transparent mark background");
        VerifyIconPixel(GetTrayImage(true), new(17, 4.5), 0, 0, "badge cut-out");
        VerifyIconPixel(GetTrayImage(true), new(12.5, 4.5), 0, 0, "badge gap");
        VerifyTrayStatusesForSmoke();
    }

    /// <summary>
    /// Applies each icon status to the real status button and checks the image, the dimmed
    /// state, and the dot's visibility, color and placement over the cut-out.
    /// </summary>
    private void VerifyTrayStatusesForSmoke()
    {
        var running = new AppHostMenuItem(new("/smoke/Shop/apphost.cs", 1, 1), "Shop", "", "Shop", true, true, false, null)
        {
            Health = AppHostHealth.Healthy
        };
        var button = AppKit.Get(_statusItem, "button");
        var captureDirectory = Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PANEL_CAPTURE_DIR");
        try
        {
            foreach (var (status, state) in new (TrayIconStatus, TrayViewState)[]
            {
                (TrayIconStatus.Idle, new(DiscoveryState.Live, [], "")),
                (TrayIconStatus.Running, new(DiscoveryState.Live, [running], "")),
                (TrayIconStatus.Attention, new(DiscoveryState.Live, [running with { Health = AppHostHealth.Unhealthy }], "")),
                (TrayIconStatus.Connecting, new(DiscoveryState.Connecting, [], "")),
                (TrayIconStatus.Unavailable, new(DiscoveryState.Disconnected, [running], ""))
            })
            {
                if (TrayIconStatusRules.Get(state) != status)
                {
                    throw new InvalidOperationException($"Unexpected tray icon status for {status}.");
                }
                ApplyTrayIcon(status);
                var badge = TrayIconStatusRules.HasBadge(status);
                var badgeVisible = _trayBadge != 0 && AppKit.GetBool(_trayBadge, AppKit.Selector("isHidden")) == 0;
                if (AppKit.Get(button, "image") != GetTrayImage(badge) || badgeVisible != badge
                    || (AppKit.GetBool(button, AppKit.Selector("appearsDisabled")) != 0) != TrayIconStatusRules.IsDimmed(status))
                {
                    throw new InvalidOperationException($"The status button does not show the {status} icon.");
                }
                if (badge)
                {
                    var expected = AppKit.Get(AppKit.Class("NSColor"), status == TrayIconStatus.Running ? "systemGreenColor" : "systemOrangeColor");
                    var frame = AppKit.GetRect(_trayBadge, "frame");
                    var bounds = AppKit.GetRect(button, "bounds");
                    if (AppKit.SendReturningBool(AppKit.Get(_trayBadge, "fillColor"), AppKit.Selector("isEqual:"), expected) == 0
                        || frame.Size != s_trayBadgeRect.Size
                        || frame.Origin.X < 0 || frame.Origin.Y < 0
                        || frame.Origin.X + frame.Size.Width > bounds.Size.Width || frame.Origin.Y + frame.Size.Height > bounds.Size.Height)
                    {
                        throw new InvalidOperationException($"The {status} dot has the wrong color or does not fit the status button.");
                    }
                }
                if (captureDirectory is { Length: > 0 })
                {
                    Directory.CreateDirectory(captureDirectory);
                    foreach (var (name, appearance) in new[] { ("light", "NSAppearanceNameAqua"), ("dark", "NSAppearanceNameDarkAqua") })
                    {
                        AppKit.Set(button, "setAppearance:", AppKit.Get(AppKit.Class("NSAppearance"), "appearanceNamed:", AppKit.Constant(appearance)));
                        CaptureView(button, Path.Combine(captureDirectory, $"tray-{status.ToString().ToLowerInvariant()}-{name}.png"));
                    }
                    AppKit.Set(button, "setAppearance:", 0);
                }
            }
        }
        finally
        {
            ApplyTrayIcon(TrayIconStatusRules.Get(_controller.State));
        }
    }

    private static void CaptureView(nint view, string path)
    {
        var bounds = AppKit.GetRect(view, "bounds");
        var bitmap = AppKit.SendRect(view, AppKit.Selector("bitmapImageRepForCachingDisplayInRect:"), bounds);
        AppKit.SendRectAndPointer(view, AppKit.Selector("cacheDisplayInRect:toBitmapImageRep:"), bounds, bitmap);
        var data = AppKit.SendTwoPointers(bitmap, AppKit.Selector("representationUsingType:properties:"), 4,
            AppKit.Get(AppKit.Class("NSDictionary"), "dictionary"));
        if (data == 0 || AppKit.SendTwoPointersReturningBool(data, AppKit.Selector("writeToFile:atomically:"), AppKit.String(path), 1) == 0)
        {
            throw new InvalidOperationException("Could not write the tray capture.");
        }
    }

    private unsafe void VerifyTrayMarkForSmoke()
    {
        if (AppKit.GetBool(_trayMarkImage, AppKit.Selector("isTemplate")) == 0)
        {
            throw new InvalidOperationException("The tray logo must be a template image.");
        }
        nuint* pixel = stackalloc nuint[4];
        foreach (var scale in new[] { 1, 2 })
        {
            var rendered = GetBitmapForSmoke(GetTrayImage(false), 22 * scale);
            var size = 22 * scale;
            var left = size;
            var top = size;
            var right = -1;
            var bottom = -1;
            var opaque = 0;
            var partial = 0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    AppKit.GetPixel(rendered, AppKit.Selector("getPixel:atX:y:"), pixel, x, y);
                    if (pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0)
                    {
                        throw new InvalidOperationException("Template artwork must be black; only alpha may vary.");
                    }
                    // The outline is about 1.5 points wide, so at 1x antialiasing keeps it a little
                    // under full coverage.
                    opaque += pixel[3] >= 200 ? 1 : 0;
                    // The logo's lighter facets become partly transparent rather than vanishing.
                    partial += pixel[3] is >= 30 and <= 180 ? 1 : 0;
                    if (pixel[3] >= 128)
                    {
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }
            if (opaque == 0 || partial < 10 * scale * scale)
            {
                throw new InvalidOperationException($"The monochrome logo lost its outline or facets at {scale}x ({opaque} opaque, {partial} partial pixels).");
            }
            // Measure the visible logo, not the image frame: padding must not shrink the icon below
            // the size of the system's menu bar glyphs (about 14 points tall).
            if (right - left + 1 < 15 * scale || bottom - top + 1 < 13 * scale)
            {
                throw new InvalidOperationException($"The visible tray logo is too small at {scale}x: {right - left + 1} by {bottom - top + 1} pixels.");
            }
            Console.WriteLine($"Visible tray logo at {scale}x: {right - left + 1} by {bottom - top + 1} pixels; {opaque} outline and {partial} facet pixels.");
        }
    }

    private static nint GetBitmapForSmoke(nint image, int pixels)
    {
        var representations = AppKit.Get(image, "representations");
        if (AppKit.Get(representations, "count") != 2)
        {
            throw new InvalidOperationException("Tray artwork must include standard and Retina representations.");
        }
        for (var i = 0; i < 2; i++)
        {
            var bitmap = AppKit.Get(representations, "objectAtIndex:", i);
            if (AppKit.Get(bitmap, "pixelsWide") == pixels && AppKit.Get(bitmap, "pixelsHigh") == pixels
                && AppKit.Get(bitmap, "bitsPerSample") == 8 && AppKit.Get(bitmap, "samplesPerPixel") == 4)
            {
                return bitmap;
            }
        }
        throw new InvalidOperationException($"Missing {pixels}-pixel RGBA tray representation.");
    }

    private static unsafe void VerifyIconPixel(nint image, AppKit.NativePoint point, uint expectedRgb, double expectedAlpha, string description)
    {
        var bitmap = AppKit.Get(AppKit.Get(image, "representations"), "objectAtIndex:", 0);
        var size = AppKit.GetSize(image, AppKit.Selector("size"));
        var width = AppKit.Get(bitmap, "pixelsWide");
        var height = AppKit.Get(bitmap, "pixelsHigh");
        if (AppKit.Get(bitmap, "bitsPerSample") != 8 || AppKit.Get(bitmap, "samplesPerPixel") != 4
            || AppKit.SendReturningBool(AppKit.Get(bitmap, "colorSpace"), AppKit.Selector("isEqual:"),
                AppKit.Get(AppKit.Class("NSColorSpace"), "sRGBColorSpace")) == 0)
        {
            throw new InvalidOperationException("Status artwork must use an 8-bit sRGB RGBA representation.");
        }
        // Read the encoded sRGB samples directly. colorAtX:y: wraps samples in a calibrated
        // NSColor; converting that color to sRGB would apply a second color-space transform.
        nuint* components = stackalloc nuint[4];
        AppKit.GetPixel(bitmap, AppKit.Selector("getPixel:atX:y:"), components,
            (nint)(point.X / size.Width * width), (nint)((size.Height - point.Y) / size.Height * height));
        var red = components[0] / 255.0;
        var green = components[1] / 255.0;
        var blue = components[2] / 255.0;
        var alpha = components[3] / 255.0;
        const double Tolerance = 0.025;
        if (Math.Abs(alpha - expectedAlpha) > Tolerance
            || (expectedAlpha > 0
                && (Math.Abs(red - (expectedRgb >> 16 & 0xff) / 255.0) > Tolerance
                    || Math.Abs(green - (expectedRgb >> 8 & 0xff) / 255.0) > Tolerance
                    || Math.Abs(blue - (expectedRgb & 0xff) / 255.0) > Tolerance)))
        {
            throw new InvalidOperationException($"Unexpected {description} pixel: RGBA ({red:F3}, {green:F3}, {blue:F3}, {alpha:F3}).");
        }
    }

    internal void HideStatusItemForSmoke()
        => AppKit.SendBool(_statusItem, AppKit.Selector("setVisible:"), 0);

    internal bool HasRestoredPlacementForSmoke()
    {
        var defaults = AppKit.Get(AppKit.Class("NSUserDefaults"), "standardUserDefaults");
        var position = AppKit.Get(defaults, "objectForKey:", AppKit.String($"NSStatusItem Preferred Position {_autosaveName}"));
        return position != 0 && AppKit.Text(AppKit.Get(position, "stringValue")) == "300";
    }

    internal void FinishSmoke(bool success)
    {
        _exitCode = success ? 0 : 1;
        // Quit through the panel, as a user would.
        EnsurePanelForSmoke();
        ApplyPanelResponse(_panel!.Invoke(_panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Quit).Key));
    }

    private void DisposeSmokeTimer()
    {
        ReleaseSmokeTimer(ref _smokeTimer);
        ReleaseSmokeTimer(ref _smokePreviewTimer);
        _smokeTimeout = null;
    }

    private static void ReleaseSmokeTimer(ref nint timer)
    {
        if (timer != 0)
        {
            AppKit.SendVoid(timer, AppKit.Selector("invalidate"));
            AppKit.Release(timer);
            timer = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSmokeShowPreview(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) =>
        {
            ReleaseSmokeTimer(ref app._smokePreviewTimer);
            app.ShowPreviewPanel();
        });

    /// <summary>
    /// Opens the interactive preview's panel. For alignment screenshots, environment variables
    /// pick its appearance (ASPIRE_TRAY_SMOKE_PREVIEW_APPEARANCE: light or dark), its state
    /// (ASPIRE_TRAY_SMOKE_PREVIEW_STATE: hover shows the first row's buttons, actions opens its
    /// action list, settings opens the Settings view) and a JSON file to export the scene and the
    /// window's number and frame to (ASPIRE_TRAY_SMOKE_PREVIEW_EXPORT), so `screencapture -l`
    /// can capture exactly that window.
    /// </summary>
    private void ShowPreviewPanel()
    {
        var appearance = Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PREVIEW_APPEARANCE");
        if (appearance is "light" or "dark")
        {
            AppKit.Set(_application, "setAppearance:", AppKit.Get(AppKit.Class("NSAppearance"), "appearanceNamed:",
                AppKit.Constant(appearance == "dark" ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua")));
        }
        ShowPanel();
        var panel = _panel!;
        var first = panel.Scene.Targets.FirstOrDefault(target => target.Kind == PanelTargetKind.Row);
        switch (Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PREVIEW_STATE"))
        {
            case "hover" when first is not null:
                ApplyPanelResponse(panel.PointerMoved(first.HitBounds.X + 40, first.HitBounds.CenterY));
                break;
            case "actions" when first is not null:
                ApplyPanelResponse(panel.Invoke(first.Key with { Kind = PanelTargetKind.More }));
                break;
            case "settings":
                RunPanelAction(new(PanelActionKind.Settings));
                break;
        }
        if (Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PREVIEW_EXPORT") is { Length: > 0 } export)
        {
            LayoutPanel();
            var frame = AppKit.GetRect(_panelWindow, "frame");
            var scale = AppKit.GetDouble(_panelWindow, AppKit.Selector("backingScaleFactor"));
            TrayPanelSceneJson.Write(export, panel.Scene, "macOS", appearance ?? "system", scale, AppKit.Get(_panelWindow, "windowNumber"),
                ((int)frame.Origin.X, (int)frame.Origin.Y, (int)frame.Size.Width, (int)frame.Size.Height));
            Console.WriteLine($"Preview scene exported to {export}.");
        }
    }
}

internal sealed record NativePanelInspection(
    IReadOnlyList<PanelRowInspection> Rows,
    string HeaderStatus,
    string? EmptyTitle,
    string? Notice,
    bool HasTemplateIcon,
    bool HasIconOnlyTitle,
    bool HasNoMenu,
    bool DispatcherSupportsAllModes,
    string AutosaveName,
    bool IsStatusItemVisible,
    bool HasExpectedConnectionBadge,
    string AccessibilityLabel,
    PanelConfirmKind? Confirmation);

internal sealed record PanelRowInspection(
    AppHostId Id,
    PanelSection Section,
    string Name,
    bool CanOpenDashboard,
    bool CanStop,
    bool CanStart);
