// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray.Spike;

// Native inspection/driving hooks live apart from dashboard/stop behavior. The harness uses
// the real Objective-C target/actions and delegate trampolines. One bounded pass opens the
// native menu; no browser is activated and no production timer is constructed.
internal sealed partial class MacTrayApplication
{
    private nint _smokeTimer;
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
    internal event Action<TrayViewState>? MenuUpdated;

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
        var empty = _rows.Count == 0 ? AppKit.Get(_menu, "itemAtIndex:", 2) : 0;
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
                    && AppKit.Get(row.Stop, "action") == AppKit.Selector("stopAppHost:"))).ToArray(),
            checked((int)AppKit.Get(_menu, "numberOfItems")),
            AppKit.Text(AppKit.Get(_header, "title")),
            AppKit.Supports(_header, "subtitle") ? AppKit.Text(AppKit.Get(_header, "subtitle")) : null,
            icon != 0 && AppKit.GetBool(icon, AppKit.Selector("isTemplate")) == 0,
            AppKit.Get(_header, "image") != 0,
            HasNoItemTooltips(_menu),
            AppKit.Text(AppKit.Get(quit, "keyEquivalent")) == "q"
                && AppKit.Get(quit, "keyEquivalentModifierMask") == 1 << 20 && Enabled(quit),
            empty != 0 && !Enabled(empty) && AppKit.Text(AppKit.Get(empty, "title")).StartsWith("AppHosts will appear here", StringComparison.Ordinal),
            _runLoopModes.Count == 3 && _runLoopModes.All(mode => AppKit.CFRunLoopContainsSource(_runLoop, _refreshSource, mode) != 0),
            IsInMenuTrackingMode(),
            AppKit.Text(AppKit.Get(_statusItem, "autosaveName")),
            AppKit.GetBool(_statusItem, AppKit.Selector("isVisible")) != 0);
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
        _exitCode = success ? 0 : 1;
        AppKit.Set(_menu, "performActionForItemAtIndex:", AppKit.Get(_menu, "numberOfItems") - 1);
    }

    private void DisposeSmokeTimer()
    {
        CancelSmokeTracking();
        ReleaseSmokeTimer(ref _smokeTimer);
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
    string HeaderTitle,
    string? HeaderSubtitle,
    bool HasColorIcon,
    bool HasHeaderIcon,
    bool HasNoItemTooltips,
    bool HasCommandQ,
    bool HasEmptyPlaceholder,
    bool DispatcherSupportsAllModes,
    bool IsInMenuTrackingMode,
    string AutosaveName,
    bool IsStatusItemVisible);

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
    bool HasCorrectActions);
