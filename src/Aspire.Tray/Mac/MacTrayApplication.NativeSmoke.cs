// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

// Native inspection/driving hooks live apart from dashboard/stop behavior. The harness uses
// the real Objective-C target/actions and delegate trampolines. One bounded pass opens the
// native menu; no browser is activated and no production timer is constructed.
internal sealed partial class MacTrayApplication
{
    private nint _smokeTimer;
    private nint _smokePreviewTimer;
    private nint _smokeTrackingStartTimer;
    private nint _smokeTrackingWorkerTimer;
    private nint _smokeTrackingDeadlineTimer;
    private nint _smokeTrackingMenu;
    private nint _smokeRetainedStopSender;
    private Action? _smokeTimeout;
    private Action? _smokeMenuOpened;
    private Action<bool>? _smokeMenuClosed;
    private bool _smokeTrackingDeadlineFired;
    private long _smokeTrackingStartedAt;
    private bool _interactiveSmoke;
    internal event Action<TrayViewState>? MenuUpdated;

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

    private nint CreateSmokeTimer(double seconds, string action, bool trackingOnly = false)
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
            if (trackingOnly && mode != AppKit.Constant("NSEventTrackingRunLoopMode"))
            {
                continue;
            }
            // Both the global watchdog and the tracking deadline must fire inside NSMenu's
            // nested loop, even if the production dispatcher is accidentally default-only.
            AppKit.SendVoidTwoPointers(runLoop, AppKit.Selector("addTimer:forMode:"), timer, mode);
        }
        return timer;
    }

    internal void BeginRealMenuTrackingForSmoke(Action opened, Action<bool> closed)
    {
        VerifyUIThread();
        _smokeMenuOpened = opened;
        _smokeMenuClosed = closed;
        // Open from a separate one-shot callback, not from the refresh source itself. The
        // test must not depend on whether CFRunLoop permits recursive source performance.
        _smokeTrackingStartTimer = CreateSmokeTimer(0.01, "smokeTrackingStart:");
    }

    private void OpenSmokeTrackingMenu()
    {
        ReleaseSmokeTimer(ref _smokeTrackingStartTimer);
        _smokeTrackingMenu = AppKit.Get(_menu, "retain");
        _smokeTrackingDeadlineFired = false;
        _smokeTrackingStartedAt = Environment.TickCount64;
        try
        {
            _smokeTrackingDeadlineTimer = CreateSmokeTimer(2, "smokeTrackingDeadline:");
            // menuWillOpen runs before the nested tracking loop starts. Schedule the producer
            // trigger in tracking mode itself so a fast worker cannot publish in that gap.
            _smokeTrackingWorkerTimer = CreateSmokeTimer(0, "smokeTrackingWorker:", trackingOnly: true);
            // This is AppKit's actual synchronous menu-tracking loop, anchored at the status
            // button, not a synthetic delegate call or accessibility-driven screen operation.
            // https://developer.apple.com/documentation/appkit/nsmenu/popup(positioning:at:in:)
            TraceSmokeTracking("popup-begin");
            var selected = AppKit.PopUpMenu(_smokeTrackingMenu, AppKit.Selector("popUpMenuPositioningItem:atLocation:inView:"),
                0, new(0, 0), AppKit.Get(_statusItem, "button"));
            TraceSmokeTracking($"popup-return selected={selected}");
        }
        finally
        {
            CancelSmokeTracking();
            ReleaseSmokeTimer(ref _smokeTrackingWorkerTimer);
            ReleaseSmokeTimer(ref _smokeTrackingDeadlineTimer);
            AppKit.Release(_smokeTrackingMenu);
            _smokeTrackingMenu = 0;
            _smokeMenuOpened = null;
            var closed = _smokeMenuClosed;
            _smokeMenuClosed = null;
            closed?.Invoke(_smokeTrackingDeadlineFired);
        }
    }

    private void StartSmokeTrackingWorker()
    {
        TraceSmokeTracking("worker-trigger");
        if (_smokeTrackingMenu != 0 && _openMenus.Contains(_smokeTrackingMenu))
        {
            var opened = _smokeMenuOpened;
            _smokeMenuOpened = null;
            opened?.Invoke();
        }
    }

    private void FinishSmokeTracking()
    {
        TraceSmokeTracking("deadline");
        _smokeTrackingDeadlineFired = true;
        CancelSmokeTracking();
    }

    internal void CompleteRealMenuTrackingForSmoke()
    {
        VerifyUIThread();
        if (_smokeTrackingDeadlineTimer == 0 || !IsInMenuTrackingMode())
        {
            throw new InvalidOperationException("The tracking-mode verification must finish while the real menu is open.");
        }
        // Once the posted refresh has been verified, close from the next timer callback.
        // Holding an already-verified popup open for the full watchdog interval only adds
        // exposure to unrelated input/window changes. A stalled dispatcher still leaves
        // the original two-second watchdog in place and fails the progress assertion.
        var nextTurn = AppKit.SendDouble(AppKit.Class("NSDate"), AppKit.Selector("dateWithTimeIntervalSinceNow:"), 0.01);
        AppKit.Set(_smokeTrackingDeadlineTimer, "setFireDate:", nextTurn);
        TraceSmokeTracking("verified-close-scheduled");
    }

    private void CancelSmokeTracking()
    {
        if (_smokeTrackingMenu != 0)
        {
            AppKit.SendVoid(_smokeTrackingMenu, AppKit.Selector("cancelTrackingWithoutAnimation"));
        }
    }

    private void TraceSmokeTracking(string stage)
    {
        if (_smokeTrackingMenu != 0)
        {
            var currentEvent = AppKit.Get(_application, "currentEvent");
            Console.WriteLine($"Native tracking {stage}; elapsed={Environment.TickCount64 - _smokeTrackingStartedAt}ms; trackingMode={IsInMenuTrackingMode()}; openMenus={_openMenus.Count}; eventType={AppKit.Get(currentEvent, "type")}.");
        }
    }

    private bool IsInMenuTrackingMode()
    {
        var mode = AppKit.CFRunLoopCopyCurrentMode(_runLoop);
        if (mode == 0)
        {
            return false;
        }
        try
        {
            return AppKit.Text(mode) == AppKit.Text(AppKit.Constant("NSEventTrackingRunLoopMode"));
        }
        finally
        {
            AppKit.CFRelease(mode);
        }
    }

    internal NativeMenuInspection InspectMenu()
    {
        VerifyUIThread();
        var icon = AppKit.Get(AppKit.Get(_statusItem, "button"), "image");
        var quit = AppKit.Get(_menu, "itemAtIndex:", AppKit.Get(_menu, "numberOfItems") - 1);
        return new(
            _rows.Select(row => new NativeRowInspection(row.Id,
                AppKit.Text(AppKit.Get(row.Item, "title")),
                AppKit.Supports(row.Item, "subtitle") ? AppKit.Text(AppKit.Get(row.Item, "subtitle")) : null,
                AppKit.Text(AppKit.Get(row.Item, "accessibilityLabel")),
                Enabled(row.Item), Enabled(row.Dashboard), Enabled(row.Stop),
                AppKit.Text(AppKit.Get(row.Stop, "title")),
                AppKit.Get(row.Item, "image") != 0
                    && AppKit.Get(row.Dashboard, "image") != 0 && AppKit.Get(row.Stop, "image") != 0,
                AppKit.Get(row.Dashboard, "action") == AppKit.Selector("openDashboard:")
                    && AppKit.Get(row.Stop, "action") == AppKit.Selector("stopAppHost:"),
                Enabled(row.Start),
                AppKit.Text(AppKit.Get(row.Pin, "title")),
                _healthImages.Single(pair => pair.Value == AppKit.Get(row.Item, "image")).Key)).ToArray(),
            checked((int)AppKit.Get(_menu, "numberOfItems")),
            _header == 0 ? null : AppKit.Text(AppKit.Get(_header, "title")),
            icon != 0 && AppKit.GetBool(icon, AppKit.Selector("isTemplate")) == 0,
            AppKit.Text(AppKit.Get(AppKit.Get(_statusItem, "button"), "title")) == "",
            HasNoItemTooltips(_menu),
            AppKit.Text(AppKit.Get(quit, "keyEquivalent")) == "q"
                && AppKit.Get(quit, "keyEquivalentModifierMask") == 1 << 20 && Enabled(quit),
            _rows.Count == 0 && _header != 0 && !Enabled(_header),
            _runLoopModes.Count == 3 && _runLoopModes.All(mode => AppKit.CFRunLoopContainsSource(_runLoop, _refreshSource, mode) != 0),
            IsInMenuTrackingMode(),
            AppKit.Text(AppKit.Get(_statusItem, "autosaveName")),
            AppKit.GetBool(_statusItem, AppKit.Selector("isVisible")) != 0,
            _recentRows.Select(row => row.Id.AppHostPath).ToArray(),
            Enabled(_clearRecent),
            AppKit.Get(AppKit.Get(_statusItem, "button"), "image") == GetTrayImage(_controller.State.HasActiveAppHosts));
    }

    internal void PerformPinForSmoke(string path, bool useContextMenu)
    {
        var row = _rows.Concat(_recentRows).First(row => row.Id.AppHostPath == path);
        if (!useContextMenu)
        {
            AppKit.SendVoidPointer(_target, AppKit.Get(row.Pin, "action"), row.Pin);
            return;
        }
        var pinned = AppKit.Get(row.Pin, "action") == AppKit.Selector("unpinAppHost:");
        var menu = CreatePinContextMenu(path, pinned);
        try
        {
            var item = AppKit.Get(menu, "itemAtIndex:", 0);
            if (AppKit.Get(item, "image") == 0 || SelectedPath(item) != path)
            {
                throw new InvalidOperationException("The pin context menu lost its icon or selected path.");
            }
            AppKit.Set(menu, "performActionForItemAtIndex:", 0);
        }
        finally
        {
            AppKit.Set(menu, "setDelegate:", 0);
            _menus.Remove(menu);
            AppKit.Release(menu);
        }
    }

    internal void PerformStartForSmoke(string path)
    {
        var row = _rows.Concat(_recentRows).Single(row => row.Id.AppHostPath == path);
        AppKit.Set(_target, "startAppHost:", row.Start);
    }

    internal void PerformClearRecentForSmoke()
        => AppKit.Set(_target, "clearRecent:", _clearRecent);

    internal void VerifyInformationItemsForSmoke()
    {
        var expected = new[] { "", "Open Recent", "Documentation", "Settings\u2026", "", "Quit Aspire" };
        for (var i = 0; i < expected.Length; i++)
        {
            var item = AppKit.Get(_menu, "itemAtIndex:", AppKit.Get(_menu, "numberOfItems") - expected.Length + i);
            if (AppKit.Text(AppKit.Get(item, "title")) != expected[i]
                || (i is 2 or 3 && (!Enabled(item) || AppKit.Get(item, "submenu") != 0)))
            {
                throw new InvalidOperationException("The top-level information actions do not match the expected menu.");
            }
            if (i == 2)
            {
                var documentationArtwork = AppKit.Get(AppKit.Get(item, "image"), "TIFFRepresentation");
                var dashboardIcon = AppKit.SendTwoPointers(AppKit.Class("NSImage"),
                    AppKit.Selector("imageWithSystemSymbolName:accessibilityDescription:"),
                    AppKit.String("arrow.up.right.square"), AppKit.String("Open dashboard"));
                var dashboardArtwork = AppKit.Get(dashboardIcon, "TIFFRepresentation");
                if (documentationArtwork == 0 || dashboardArtwork == 0
                    || AppKit.SendReturningBool(documentationArtwork, AppKit.Selector("isEqualToData:"), dashboardArtwork) == 0)
                {
                    throw new InvalidOperationException("Documentation must use the same icon as Open Dashboard.");
                }
            }
            if (i == 3 && (AppKit.Text(AppKit.Get(item, "keyEquivalent")) != ","
                || AppKit.Get(item, "keyEquivalentModifierMask") != 1 << 20
                || AppKit.Get(item, "action") != AppKit.Selector("showSettings:")))
            {
                throw new InvalidOperationException("Settings must use the normal Command-comma menu shortcut.");
            }
        }
    }

    internal void VerifySettingsForSmoke(MemoryTrayStartupSettings startupSettings)
    {
        VerifyUIThread();
        if (!ReferenceEquals(_startupSettings, startupSettings) || startupSettings.Read().Enabled || _settingsWindow != 0)
        {
            throw new InvalidOperationException("Settings smoke must start with untouched, disabled in-memory settings.");
        }

        void OpenFromStatusMenu()
            => AppKit.Set(_menu, "performActionForItemAtIndex:", AppKit.Get(_menu, "numberOfItems") - 3);

        void VerifyState(bool enabled)
        {
            var expected = startupSettings.Read();
            var status = $"Launch at sign-in is {(enabled ? "on" : "off")}.\n{expected.Detail}";
            if (expected.Enabled != enabled || AppKit.Get(_startupCheckbox, "state") != (enabled ? 1 : 0)
                || !Enabled(_startupCheckbox) || AppKit.Text(AppKit.Get(_startupStatus, "stringValue")) != status)
            {
                throw new InvalidOperationException("The native launch-at-sign-in control disagrees with the in-memory registration.");
            }
        }

        SetTrackingForSmoke(null, open: true);
        OpenFromStatusMenu();
        if (_settingsWindow != 0 || !_settingsRequested || startupSettings.Read().Enabled)
        {
            throw new InvalidOperationException("Opening Settings must defer while the status menu is tracking.");
        }
        SetTrackingForSmoke(null, open: false);
        _modalDepth++;
        try
        {
            ShowPendingSettings();
            if (_settingsWindow != 0 || !_settingsRequested)
            {
                throw new InvalidOperationException("Opening Settings must defer during a modal confirmation.");
            }
        }
        finally
        {
            _modalDepth--;
        }
        ShowPendingSettings();
        VerifyState(false);
        var window = _settingsWindow;
        if (window == 0 || AppKit.GetBool(window, AppKit.Selector("isVisible")) == 0
            || AppKit.GetBool(window, AppKit.Selector("canBecomeKeyWindow")) == 0
            || AppKit.Text(AppKit.Get(_startupCheckbox, "title")) != "Launch Aspire Tray when I sign in"
            || AppKit.Text(AppKit.Get(_settingsAbout, "stringValue")) != SettingsAboutText
            || AppKit.Text(AppKit.Get(_settingsDocumentation, "title")) != "Documentation - https://aspire.dev")
        {
            throw new InvalidOperationException("Settings is missing its native window, startup control, About information, or documentation.");
        }
        var subviews = AppKit.Get(AppKit.Get(window, "contentView"), "subviews");
        var labels = new List<string>();
        for (nint i = 0; i < AppKit.Get(subviews, "count"); i++)
        {
            var view = AppKit.Get(subviews, "objectAtIndex:", i);
            if (AppKit.Supports(view, "stringValue"))
            {
                labels.Add(AppKit.Text(AppKit.Get(view, "stringValue")));
            }
        }
        if (!labels.Contains("General") || !labels.Contains("About")
            || !labels.Contains("Only the tray starts at sign-in. AppHosts are not started."))
        {
            throw new InvalidOperationException("Settings must label its sections and explain that startup never starts AppHosts.");
        }

        // performClick: changes the actual NSButton state and invokes its registered
        // Objective-C target/action; setting state directly would not test user interaction.
        AppKit.Set(_startupCheckbox, "performClick:", 0);
        VerifyState(true);
        OpenFromStatusMenu();
        VerifyState(true);
        AppKit.Set(window, "performClose:", 0);
        if (AppKit.GetBool(window, AppKit.Selector("isVisible")) != 0 || !startupSettings.Read().Enabled)
        {
            throw new InvalidOperationException("Closing Settings changed the startup preference or left the window visible.");
        }
        OpenFromStatusMenu();
        VerifyState(true);
        AppKit.Set(_startupCheckbox, "performClick:", 0);
        VerifyState(false);

        // Deliver an app-local key event through AppKit, not a system-wide hotkey.
        // Also change the fake registration externally to verify the window re-reads it.
        startupSettings.SetEnabled(true);
        var keyEvent = AppKit.CreateKeyEvent(AppKit.Class("NSEvent"),
            AppKit.Selector("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            10, new(0, 0), 1 << 20, 0, AppKit.Get(window, "windowNumber"), 0, AppKit.String(","), AppKit.String(","), 0, 43);
        if (AppKit.SendReturningBool(_applicationMenu, AppKit.Selector("performKeyEquivalent:"), keyEvent) == 0)
        {
            throw new InvalidOperationException("The focused application's Command-comma shortcut did not invoke Settings.");
        }
        VerifyState(true);
        var orderedWindows = AppKit.Get(_application, "orderedWindows");
        if (_settingsWindow != window || AppKit.Get(orderedWindows, "count") == 0
            || AppKit.Get(orderedWindows, "objectAtIndex:", 0) != window)
        {
            throw new InvalidOperationException("Reopening Settings must order the same retained native window first.");
        }
        if (AppKit.GetBool(window, AppKit.Selector("isKeyWindow")) == 0)
        {
            // A background harness cannot require macOS to transfer foreground focus.
            // Do not bypass that policy or claim the interactive focus check passed.
            Console.WriteLine("Settings window reuse, ordering, and app-local shortcut verified; macOS did not grant foreground focus to the background smoke process. Interactive focus verification remains required.");
        }
        AppKit.Set(_startupCheckbox, "performClick:", 0);
        VerifyState(false);
        AppKit.Set(_settingsDocumentation, "performClick:", 0);
        AppKit.Set(_menu, "performActionForItemAtIndex:", AppKit.Get(_menu, "numberOfItems") - 4);
        AppKit.Set(window, "performClose:", 0);
        VerifyState(false);
    }

    internal void VerifyStatusArtworkForSmoke()
    {
        foreach (var health in Enum.GetValues<AppHostHealth>())
        {
            var size = AppKit.GetSize(GetHealthImage(health), AppKit.Selector("size"));
            if (size != new AppKit.NativeSize(12, 12))
            {
                throw new InvalidOperationException("AppHost status artwork must use a consistent 12-point size.");
            }
        }
        if (AppKit.GetSize(GetTrayImage(true), AppKit.Selector("size")) != new AppKit.NativeSize(22, 22))
        {
            throw new InvalidOperationException("The tray artwork must use the larger 22-point canvas.");
        }
        VerifyTrayMarkForSmoke();
        VerifyIconPixel(GetTrayImage(true), new(10, 21.5), 0, 0, "transparent top margin");
        VerifyIconPixel(GetTrayImage(true), new(0.5, 0.5), 0, 0, "transparent mark background");
        VerifyIconPixel(GetTrayImage(true), new(17.5, 4.5), 0x512BD4, 1, "lower-right connected purple circle");
        VerifyIconPixel(GetTrayImage(true), new(17.5, 9.5), 0, 0, "transparent connected border");
        VerifyIconPixel(GetTrayImage(true), new(12.5, 4.5), 0, 0, "transparent connected inner border");
        VerifyIconPixel(GetTrayImage(false), new(17.5, 4.5), 0x606060, 1, "lower-right disconnected cross");
        VerifyIconPixel(GetTrayImage(false), new(17.5, 9.5), 0, 0, "transparent disconnected border");
        VerifyIconPixel(GetHealthImage(AppHostHealth.Healthy), new(6, 10), 0x4A9F30, 1, "available green");
        VerifyIconPixel(GetHealthImage(AppHostHealth.Healthy), new(5.3, 4), 0x4A9F30, 1, "solid available circle");
        VerifyIconPixel(GetHealthImage(AppHostHealth.Warning), new(3, 6), 0xDFA638, 1, "away amber");
        VerifyIconPixel(GetHealthImage(AppHostHealth.Warning), new(6, 7), 0xDFA638, 1, "solid waiting circle");
        VerifyIconPixel(GetHealthImage(AppHostHealth.Unhealthy), new(6, 6), 0xB52A29, 1, "busy red");
        VerifyIconPixel(GetHealthImage(AppHostHealth.Unknown), new(6, 6), 0xFFFFFF, 1, "white not-started circle");
    }

    private unsafe void VerifyTrayMarkForSmoke()
    {
        if (AppKit.GetSize(_trayMarkImage, AppKit.Selector("size")) != new AppKit.NativeSize(20, 20)
            || AppKit.GetBool(_trayMarkImage, AppKit.Selector("isTemplate")) == 0)
        {
            throw new InvalidOperationException("The supplied tray template must have a 20-point logical size.");
        }
        nuint* sourcePixel = stackalloc nuint[4];
        nuint* renderedPixel = stackalloc nuint[4];
        foreach (var scale in new[] { 1, 2 })
        {
            var source = GetBitmapForSmoke(_trayMarkImage, 20 * scale);
            foreach (var connected in new[] { false, true })
            {
                var image = GetTrayImage(connected);
                if (AppKit.GetBool(image, AppKit.Selector("isTemplate")) != 0)
                {
                    throw new InvalidOperationException("The composite must preserve the connection badge's color.");
                }
                var rendered = GetBitmapForSmoke(image, 22 * scale);
                var opaquePixels = 0;
                var transparentPixels = 0;
                var left = 20 * scale;
                var top = 20 * scale;
                var right = -1;
                var bottom = -1;
                for (var y = 0; y < 20 * scale; y++)
                {
                    for (var x = 0; x < 20 * scale; x++)
                    {
                        // The badge intentionally cuts into the lower-right mark. Elsewhere,
                        // compare every source alpha sample, including its separator cutouts.
                        if (x >= 12 * scale && y >= 11 * scale)
                        {
                            continue;
                        }
                        AppKit.GetPixel(source, AppKit.Selector("getPixel:atX:y:"), sourcePixel, x, y);
                        // Bitmap rows start at the top: the (0, 1) mark has one point above it.
                        AppKit.GetPixel(rendered, AppKit.Selector("getPixel:atX:y:"), renderedPixel, x, y + scale);
                        if (Math.Abs((double)sourcePixel[3] - renderedPixel[3]) > 1
                            || (sourcePixel[3] == 255 && (renderedPixel[0] != 255 || renderedPixel[1] != 255 || renderedPixel[2] != 255)))
                        {
                            throw new InvalidOperationException($"The supplied tray mask changed at ({x}, {y}), {scale}x, connected={connected}: expected alpha {sourcePixel[3]}, got RGBA ({renderedPixel[0]}, {renderedPixel[1]}, {renderedPixel[2]}, {renderedPixel[3]}).");
                        }
                        opaquePixels += sourcePixel[3] == 255 ? 1 : 0;
                        transparentPixels += sourcePixel[3] == 0 ? 1 : 0;
                        if (renderedPixel[3] >= 128)
                        {
                            left = Math.Min(left, x);
                            top = Math.Min(top, y);
                            right = Math.Max(right, x);
                            bottom = Math.Max(bottom, y);
                        }
                    }
                }
                if (opaquePixels == 0 || transparentPixels == 0)
                {
                    throw new InvalidOperationException("The tray template must include opaque artwork and transparent cutouts.");
                }
                // Measure the visible white mark, excluding the badge, rather than just its
                // image frame. Transparent source padding previously made a 20-point icon tiny.
                if (right - left + 1 < 15 * scale || bottom - top + 1 < 17 * scale)
                {
                    throw new InvalidOperationException($"The visible tray mark is too small at {scale}x: {right - left + 1} by {bottom - top + 1} pixels.");
                }
                Console.WriteLine($"Visible tray mark at {scale}x, connected={connected}: {right - left + 1} by {bottom - top + 1} pixels.");
            }
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

    internal void PerformDashboardForSmoke(AppHostId id)
    {
        var row = _rows.Single(row => row.Id == id);
        AppKit.Set(row.Submenu, "performActionForItemAtIndex:", 0);
    }

    internal void PerformStopForSmoke(AppHostId id)
    {
        var row = _rows.Single(row => row.Id == id);
        AppKit.Set(row.Submenu, "performActionForItemAtIndex:", 2);
    }

    internal void PerformStaleStopForSmoke(AppHostId id)
    {
        // An already-dispatched action may outlive its enabled state. Exercise the target
        // directly to prove latest-state/lifetime revalidation, not just menu disabling.
        var row = _rows.Single(row => row.Id == id);
        AppKit.Set(_target, "stopAppHost:", row.Stop);
    }

    internal void RetainStopSenderForSmoke(AppHostId id)
    {
        ReleaseRetainedStopSenderForSmoke();
        _smokeRetainedStopSender = AppKit.Get(_rows.Single(row => row.Id == id).Stop, "retain");
    }

    internal bool RetainedStopSenderIsFromPreviousMenuForSmoke(AppHostId id)
        => _smokeRetainedStopSender != 0 && _smokeRetainedStopSender != _rows.Single(row => row.Id == id).Stop;

    internal void PerformRetainedStopForSmoke()
    {
        if (_smokeRetainedStopSender == 0)
        {
            throw new InvalidOperationException("No native sender was retained for the delayed-action test.");
        }
        // Simulate Cocoa retaining the selected item across menuDidClose and dispatching its
        // original target/action after the tray has already installed a replacement menu.
        AppKit.SendVoidPointer(AppKit.Get(_smokeRetainedStopSender, "target"),
            AppKit.Get(_smokeRetainedStopSender, "action"), _smokeRetainedStopSender);
    }

    internal void ReleaseRetainedStopSenderForSmoke()
    {
        AppKit.Release(_smokeRetainedStopSender);
        _smokeRetainedStopSender = 0;
    }

    internal void SetTrackingForSmoke(AppHostId? submenu, bool open)
    {
        var menu = submenu is AppHostId id ? _rows.Single(row => row.Id == id).Submenu : _menu;
        AppKit.Set(_target, open ? "menuWillOpen:" : "menuDidClose:", menu);
    }

    internal void FinishSmoke(bool success)
    {
        // Failure or the global watchdog must close a real tracked menu before quitting.
        CancelSmokeTracking();
        if (success && _settingsWindow != 0)
        {
            ShowSettings();
        }
        _exitCode = success ? 0 : 1;
        AppKit.Set(_menu, "performActionForItemAtIndex:", AppKit.Get(_menu, "numberOfItems") - 1);
        if (_settingsWindow != 0 && AppKit.GetBool(_settingsWindow, AppKit.Selector("isVisible")) != 0)
        {
            throw new InvalidOperationException("Quit left the Settings window visible.");
        }
    }

    private void DisposeSmokeTimer()
    {
        CancelSmokeTracking();
        ReleaseSmokeTimer(ref _smokeTimer);
        ReleaseSmokeTimer(ref _smokePreviewTimer);
        ReleaseSmokeTimer(ref _smokeTrackingStartTimer);
        ReleaseSmokeTimer(ref _smokeTrackingWorkerTimer);
        ReleaseSmokeTimer(ref _smokeTrackingDeadlineTimer);
        ReleaseRetainedStopSenderForSmoke();
        _smokeTimeout = null;
        _smokeMenuOpened = null;
        _smokeMenuClosed = null;
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
    private static void OnSmokeTrackingStart(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app.OpenSmokeTrackingMenu());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSmokeTrackingWorker(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app.StartSmokeTrackingWorker());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSmokeTrackingDeadline(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app.FinishSmokeTracking());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSmokeShowPreview(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) =>
        {
            ReleaseSmokeTimer(ref app._smokePreviewTimer);
            app.ShowSettings();
        });

    private static bool Enabled(nint item) => AppKit.GetBool(item, AppKit.Selector("isEnabled")) != 0;

    private static bool HasNoItemTooltips(nint menu)
    {
        var count = AppKit.Get(menu, "numberOfItems");
        for (nint i = 0; i < count; i++)
        {
            var item = AppKit.Get(menu, "itemAtIndex:", i);
            if (AppKit.Get(item, "toolTip") != 0)
            {
                return false;
            }
            var submenu = AppKit.Get(item, "submenu");
            if (submenu != 0 && !HasNoItemTooltips(submenu))
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed record NativeMenuInspection(
    IReadOnlyList<NativeRowInspection> Rows,
    int ItemCount,
    string? StatusNotice,
    bool HasColorIcon,
    bool HasIconOnlyTitle,
    bool HasNoItemTooltips,
    bool HasCommandQ,
    bool HasEmptyPlaceholder,
    bool DispatcherSupportsAllModes,
    bool IsInMenuTrackingMode,
    string AutosaveName,
    bool IsStatusItemVisible,
    IReadOnlyList<string> RecentPaths,
    bool CanClearRecent,
    bool HasExpectedConnectionBadge);

internal sealed record NativeRowInspection(
    AppHostId Id,
    string Title,
    string? Subtitle,
    string AccessibilityLabel,
    bool Enabled,
    bool CanOpenDashboard,
    bool CanStop,
    string StopTitle,
    bool HasActionIcons,
    bool HasCorrectActions,
    bool CanStart,
    string PinTitle,
    AppHostHealth Health);
