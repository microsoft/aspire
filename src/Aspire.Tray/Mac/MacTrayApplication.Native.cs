// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication
{
    // The only managed static root exists to route unmanaged callbacks. All UI/domain state
    // belongs to the application instance and is released explicitly on its original thread.
    private static MacTrayApplication? s_callbackRoot;
    private readonly object _dispatchGate = new();
    private readonly HashSet<nint> _openMenus = [];
    private readonly List<NativeAppHostRow> _rows = [];
    private readonly List<NativeAppHostRow> _recentRows = [];
    private readonly List<nint> _menus = [];
    private readonly List<nint> _runLoopModes = [];
    private nint _pool;
    private nint _application;
    private nint _target;
    private nint _statusItem;
    private nint _menu;
    private nint _header;
    private nint _recentMenu;
    private nint _clearRecent;
    private nint _brandImage;
    private nint _trayMarkImage;
    private readonly Dictionary<bool, nint> _trayImages = [];
    private readonly Dictionary<AppHostHealth, nint> _healthImages = [];
    private bool _showStatus;
    private nint _runLoop;
    private nint _refreshSource;
    private bool _refreshPending;
    private bool _disposed;
    private bool _quitting;
    private bool _quitRequested;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RestoreRequest? _restoreRequest;
    private int _modalDepth;

    private unsafe void InitializeNative()
    {
        if (Interlocked.CompareExchange(ref s_callbackRoot, this, null) is not null)
        {
            throw new InvalidOperationException("Only one AppKit application can be created in this process.");
        }
        try
        {
            AppKit.Initialize();
            _pool = AppKit.PushPool();
            _application = AppKit.Get(AppKit.Class("NSApplication"), "sharedApplication");
            if (AppKit.Get(_application, "activationPolicy") != 1
                && AppKit.SendReturningBool(_application, AppKit.Selector("setActivationPolicy:"), 1) == 0)
            {
                throw new InvalidOperationException("Could not select accessory application mode.");
            }

            var callbackClass = AppKit.AllocateClass(AppKit.Class("NSObject"), "AspireTrayApplicationCallbacks", 0);
            if (callbackClass == 0)
            {
                throw new InvalidOperationException("Could not allocate the AppKit callback class.");
            }
            AddCallback(callbackClass, "openDashboard:", &OnDashboard);
            AddCallback(callbackClass, "stopAppHost:", &OnStopAppHost);
            AddCallback(callbackClass, "startAppHost:", &OnStartAppHost);
            AddCallback(callbackClass, "pinAppHost:", &OnPinAppHost);
            AddCallback(callbackClass, "unpinAppHost:", &OnUnpinAppHost);
            AddCallback(callbackClass, "clearRecent:", &OnClearRecent);
            AddCallback(callbackClass, "openIn:", &OnOpenIn);
            AddCallback(callbackClass, "showInFinder:", &OnShowInFinder);
            AddCallback(callbackClass, "openDocumentation:", &OnOpenDocumentation);
            AddCallback(callbackClass, "showAbout:", &OnShowAbout);
            AddCallback(callbackClass, "quit:", &OnQuit);
            AddCallback(callbackClass, "menuWillOpen:", &OnMenuWillOpen);
            AddCallback(callbackClass, "menuDidClose:", &OnMenuDidClose);
            AddCallback(callbackClass, "smokeTimeout:", &OnSmokeTimeout);
            AddCallback(callbackClass, "smokeShowPreview:", &OnSmokeShowPreview);
            AddCallback(callbackClass, "smokeTrackingStart:", &OnSmokeTrackingStart);
            AddCallback(callbackClass, "smokeTrackingWorker:", &OnSmokeTrackingWorker);
            AddCallback(callbackClass, "smokeTrackingDeadline:", &OnSmokeTrackingDeadline);
            AppKit.RegisterClass(callbackClass);
            _target = AppKit.Get(callbackClass, "new");

            _runLoop = AppKit.CFRetain(AppKit.CFRunLoopGetMain());
            var context = new AppKit.RunLoopSourceContext
            {
                Info = _target,
                Perform = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnRefresh
            };
            _refreshSource = AppKit.CFRunLoopSourceCreate(0, 0, ref context);
            if (_refreshSource == 0)
            {
                throw new InvalidOperationException("Could not create the main-thread dispatcher.");
            }
            // Explicitly service changes during menu tracking and nested NSAlert modal loops,
            // not just the default loop. CF signaling allocates no autoreleased Objective-C
            // objects on worker threads. A source coalesces notifications until it is performed.
            // https://developer.apple.com/documentation/corefoundation/cfrunloopsourcesignal(_:)
            foreach (var name in new[] { "NSDefaultRunLoopMode", "NSEventTrackingRunLoopMode", "NSModalPanelRunLoopMode" })
            {
                var mode = AppKit.Get(AppKit.Constant(name), "retain");
                _runLoopModes.Add(mode);
                AppKit.CFRunLoopAddSource(_runLoop, _refreshSource, mode);
            }

            CreateStatusItem();
            InstallContextMenuMonitor();
            UpdateMenu(_controller.State);
        }
        catch
        {
            DisposeNative();
            throw;
        }
    }

    private static unsafe void AddCallback(nint callbackClass, string name, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback)
    {
        if (AppKit.AddMethod(callbackClass, AppKit.Selector(name), (nint)callback, "v@:@") == 0)
        {
            throw new InvalidOperationException("Could not register an AppKit callback.");
        }
    }

    private void RunNativeApplication()
    {
        Console.WriteLine("Native AppKit status item created. Watching AppHost discovery.");
        AppKit.SendVoid(_application, AppKit.Selector("run"));
    }

    public Task RestoreIconAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task completion;
        lock (_dispatchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _quitting, this);
            if (_restoreRequest is null || _restoreRequest.CancellationToken.IsCancellationRequested)
            {
                _restoreRequest?.Completion.TrySetCanceled(_restoreRequest.CancellationToken);
                _restoreRequest = new(cancellationToken);
            }
            completion = _restoreRequest.Completion.Task;
        }
        RequestRefresh();
        return completion.WaitAsync(cancellationToken);
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        => _ready.Task.WaitAsync(cancellationToken);

    public void RequestQuit()
    {
        lock (_dispatchGate)
        {
            _quitRequested = true;
        }
        RequestRefresh();
    }

    internal void RequestRefresh()
    {
        lock (_dispatchGate)
        {
            if (_disposed || _quitting || _refreshPending)
            {
                return;
            }
            _refreshPending = true;
            // Dispose holds the same gate while making the source unavailable. No publisher
            // can signal a released source, even if it captured Changed before unsubscription.
            AppKit.CFRunLoopSourceSignal(_refreshSource);
            AppKit.CFRunLoopWakeUp(_runLoop);
        }
    }

    private void Refresh()
    {
        VerifyUIThread();
        bool quitRequested;
        lock (_dispatchGate)
        {
            if (_disposed || _quitting)
            {
                return;
            }
            _refreshPending = false;
            quitRequested = _quitRequested;
        }
        if (quitRequested)
        {
            Quit();
            return;
        }
        RestoreRequestedIcon();
        var state = _controller.State;
        TraceSmokeTracking("refresh-begin");
        UpdateMenu(state);
        ShowPendingPinContextMenu();
        _ready.TrySetResult();
        TraceSmokeTracking("refresh-end");
        MenuUpdated?.Invoke(state);
    }

    private void RestoreRequestedIcon()
    {
        RestoreRequest? request;
        lock (_dispatchGate)
        {
            // Removing a tracked item's window could close a modal loop or invalidate its
            // sender. MenuDidClose/ConfirmStop's finally posts another refresh when safe.
            if (_openMenus.Count != 0 || _modalDepth != 0)
            {
                return;
            }
            request = _restoreRequest;
            _restoreRequest = null;
        }
        if (request is null)
        {
            return;
        }
        if (request.CancellationToken.IsCancellationRequested)
        {
            request.Completion.TrySetCanceled(request.CancellationToken);
            return;
        }

        try
        {
            RemoveStatusItem();
            var defaults = AppKit.Get(AppKit.Class("NSUserDefaults"), "standardUserDefaults");
            // AppKit exposes no public placement setter. For this explicit recovery action
            // only, reset its autosave preference to 300 points from the right edge, the
            // position verified during development. Normal starts preserve the user's arrangement.
            // This preference is undocumented; keep this workaround local, not a general
            // layout policy. Merely setting visible=true does not fix an obscured item.
            // https://developer.apple.com/documentation/appkit/nsstatusitem/autosavename
            AppKit.SetDoubleForKey(defaults, AppKit.Selector("setDouble:forKey:"), 300,
                AppKit.String($"NSStatusItem Preferred Position {_autosaveName}"));
            AppKit.Set(defaults, "removeObjectForKey:", AppKit.String($"NSStatusItem Visible {_autosaveName}"));
            CreateStatusItem();
            AppKit.Set(_statusItem, "setMenu:", _menu);
            UpdateMenu(_controller.State);
            request.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            LogFailure("Unable to restore the tray icon", ex);
            request.Completion.TrySetException(new InvalidOperationException("Unable to restore the tray icon.", ex));
        }
    }

    private void CreateStatusItem()
    {
        var statusBar = AppKit.Get(AppKit.Class("NSStatusBar"), "systemStatusBar");
        _statusItem = AppKit.SendDouble(statusBar, AppKit.Selector("statusItemWithLength:"), -1);
        if (_statusItem == 0)
        {
            throw new InvalidOperationException("Could not create the native status item.");
        }
        AppKit.Get(_statusItem, "retain");
        // Use a stable identity, separate from smoke, to preserve normal user placement.
        AppKit.Set(_statusItem, "setAutosaveName:", AppKit.String(_autosaveName));
        // A requested launch must not inherit a saved hidden flag; this app has no UI
        // outside its status item through which a user could make it visible again.
        AppKit.SendBool(_statusItem, AppKit.Selector("setVisible:"), 1);
        SetIcon();
    }

    private void RemoveStatusItem()
    {
        if (_statusItem != 0)
        {
            AppKit.Set(_statusItem, "setMenu:", 0);
            AppKit.Set(AppKit.Get(AppKit.Class("NSStatusBar"), "systemStatusBar"), "removeStatusItem:", _statusItem);
            AppKit.Release(_statusItem);
            _statusItem = 0;
        }
    }

    private void UpdateMenu(TrayViewState state)
    {
        var structureChanged = _menu == 0 || _showStatus != state.ShowStatus
            || !_rows.Select(row => row.Id).SequenceEqual(state.AppHosts.Select(row => row.Id))
            || !_recentRows.Select(row => row.Id).SequenceEqual(state.RecentAppHosts.Select(row => row.Id));
        if (structureChanged && _openMenus.Count == 0 && _modalDepth == 0)
        {
            RebuildMenu(state);
        }

        if (_header != 0)
        {
            AppKit.Set(_header, "setTitle:", AppKit.String(state.Status));
        }
        var current = state.AppHosts.Concat(state.RecentAppHosts).ToDictionary(row => row.Id);
        foreach (var row in _rows.Concat(_recentRows))
        {
            var exists = current.TryGetValue(row.Id, out var host);
            var subtitle = host?.Subtitle ?? "AppHost no longer available.";
            SetTitleAndSubtitle(row.Item, host?.Title ?? row.Title, subtitle);
            SetEnabled(row.Item, exists);
            SetEnabled(row.Dashboard, host?.CanOpenDashboard == true);
            SetEnabled(row.Stop, host?.CanStop == true);
            SetEnabled(row.Start, host?.CanStart == true);
            SetEnabled(row.Pin, exists);
            AppKit.Set(row.Start, "setTitle:", AppKit.String(host?.IsStarting == true ? "Starting AppHost..." : "Start AppHost"));
            AppKit.Set(row.Pin, "setTitle:", AppKit.String(host?.IsPinned == true ? "Unpin AppHost" : "Pin AppHost"));
            AppKit.Set(row.Pin, "setAction:", AppKit.Selector(host?.IsPinned == true ? "unpinAppHost:" : "pinAppHost:"));
            SetSymbol(row.Pin, host?.IsPinned == true ? "pin.slash" : "pin", host?.IsPinned == true ? "Unpin AppHost" : "Pin AppHost");
            AppKit.Set(row.Stop, "setTitle:", AppKit.String(host?.IsStopping == true ? "Stopping AppHost..." : "Stop AppHost\u2026"));
            var health = host?.Health ?? AppHostHealth.Unknown;
            AppKit.Set(row.Item, "setImage:", GetHealthImage(health));
            AppKit.Set(row.Item, "setAccessibilityLabel:",
                AppKit.String($"{host?.DisplayName ?? row.DisplayName}, {HealthDescription(health)}, {subtitle}, AppHost actions"));
        }
        SetEnabled(_clearRecent, state.CanClearRecent);

        var button = AppKit.Get(_statusItem, "button");
        AppKit.Set(button, "setTitle:", AppKit.String(""));
        AppKit.Set(button, "setImage:", GetTrayImage(state.HasActiveAppHosts));
        AppKit.Set(button, "setToolTip:", AppKit.String($"Aspire\n{state.Status}"));
        AppKit.Set(button, "setAccessibilityLabel:", AppKit.String($"Aspire, {state.Status}"));
    }

    private void RebuildMenu(TrayViewState state)
    {
        // Never mutate membership/order of any menu while its delegate says it is tracking.
        // All rows retain their original AppHostId until the entire menu tree has closed.
        var previous = _menu;
        DetachMenuDelegates();
        _rows.Clear();
        _recentRows.Clear();
        _menus.Clear();
        _menu = CreateMenu();
        try
        {
            PopulateMenu(state);
            AppKit.Set(_statusItem, "setMenu:", _menu);
        }
        finally
        {
            // If population fails, the application still owns the partial replacement and
            // the status item retains its previous menu until shutdown detaches it.
            AppKit.Release(previous);
        }
    }

    private void PopulateMenu(TrayViewState state)
    {
        DiscoverFolderApplications();
        _showStatus = state.ShowStatus;
        _header = 0;
        if (_showStatus)
        {
            _header = AddItem(_menu, state.Status, null, enabled: false);
        }
        foreach (var host in state.AppHosts)
        {
            _rows.Add(AddAppHostMenu(_menu, host));
        }
        AddSeparator(_menu);
        var recent = AddItem(_menu, "Open Recent", null, enabled: true);
        SetSymbol(recent, "clock", "Recently opened AppHosts");
        _recentMenu = CreateMenu();
        try
        {
            foreach (var host in state.RecentAppHosts)
            {
                _recentRows.Add(AddAppHostMenu(_recentMenu, host));
            }
            if (state.RecentAppHosts.Count == 0)
            {
                AddItem(_recentMenu, "No recently opened AppHosts", null, enabled: false);
            }
            AddSeparator(_recentMenu);
            _clearRecent = AddItem(_recentMenu, "Clear Recently Opened\u2026", "clearRecent:", state.CanClearRecent);
            AppKit.Set(recent, "setSubmenu:", _recentMenu);
        }
        finally
        {
            AppKit.Release(_recentMenu);
        }
        var documentation = AddItem(_menu, "Documentation", "openDocumentation:", enabled: true);
        SetSymbol(documentation, "arrow.up.right.square", "Open documentation");
        AddItem(_menu, "About Aspire", "showAbout:", enabled: true);
        AddSeparator(_menu);
        var quit = AddItem(_menu, "Quit Aspire", "quit:", enabled: true);
        AppKit.Set(quit, "setKeyEquivalent:", AppKit.String("q"));
        AppKit.Set(quit, "setKeyEquivalentModifierMask:", 1 << 20);
    }

    private NativeAppHostRow AddAppHostMenu(nint menu, AppHostMenuItem host)
    {
        var item = AddItem(menu, host.Title, null, enabled: true);
        var submenu = CreateMenu();
        try
        {
            var dashboard = AddItem(submenu, "Open Dashboard", "openDashboard:", host.CanOpenDashboard);
            SetSymbol(dashboard, "arrow.up.right.square", "Open dashboard");
            AddSeparator(submenu);
            var stop = AddItem(submenu, "Stop AppHost\u2026", "stopAppHost:", host.CanStop);
            SetSymbol(stop, "stop.circle", "Stop AppHost");
            var start = AddItem(submenu, "Start AppHost", "startAppHost:", host.CanStart);
            SetSymbol(start, "play", "Start AppHost");
            // Visibility is structural: keep a removed live row's disabled actions in place
            // until tracking ends rather than moving the item under the user's pointer.
            AppKit.SendBool(start, AppKit.Selector("setHidden:"), host.IsRunning ? (byte)1 : (byte)0);
            AppKit.SendBool(stop, AppKit.Selector("setHidden:"), host.IsRunning ? (byte)0 : (byte)1);
            AppKit.SendBool(dashboard, AppKit.Selector("setHidden:"), host.IsRunning ? (byte)0 : (byte)1);
            var pin = AddItem(submenu, "Pin AppHost", "pinAppHost:", enabled: true);
            AttachPath(start, host.Id.AppHostPath);
            AttachPath(pin, host.Id.AppHostPath);
            AddSeparator(submenu);
            AddOpenInMenu(submenu, host.Id.AppHostPath);
            var finder = AddItem(submenu, "Show in Finder", "showInFinder:", enabled: true);
            SetSymbol(finder, "folder", "Show in Finder");
            AttachPath(finder, host.Id.AppHostPath);
            AppKit.Set(item, "setSubmenu:", submenu);
            AttachAppHostIdentity(host.Id, dashboard, stop);
            return new(host.Id, host.Title, host.DisplayName, item, submenu, dashboard, stop, start, pin);
        }
        finally
        {
            AppKit.Release(submenu);
        }
    }

    private nint CreateMenu()
    {
        var menu = AppKit.Get(AppKit.Class("NSMenu"), "new");
        AppKit.SendBool(menu, AppKit.Selector("setAutoenablesItems:"), 0);
        AppKit.Set(menu, "setDelegate:", _target);
        _menus.Add(menu);
        return menu;
    }

    private nint AddItem(nint menu, string title, string? action, bool enabled)
    {
        var item = AppKit.SendThreePointers(AppKit.Get(AppKit.Class("NSMenuItem"), "alloc"),
            AppKit.Selector("initWithTitle:action:keyEquivalent:"),
            AppKit.String(title), action is null ? 0 : AppKit.Selector(action), AppKit.String(""));
        try
        {
            SetEnabled(item, enabled);
            AppKit.Set(item, "setTarget:", _target);
            AppKit.Set(menu, "addItem:", item);
            return item;
        }
        finally
        {
            AppKit.Release(item);
        }
    }

    private static void AddSeparator(nint menu)
        => AppKit.Set(menu, "addItem:", AppKit.Get(AppKit.Class("NSMenuItem"), "separatorItem"));

    private static void SetEnabled(nint item, bool enabled)
        => AppKit.SendBool(item, AppKit.Selector("setEnabled:"), enabled ? (byte)1 : (byte)0);

    private static void SetTitleAndSubtitle(nint item, string title, string subtitle)
    {
        if (AppKit.Supports(item, "setSubtitle:"))
        {
            if (AppKit.Text(AppKit.Get(item, "title")) != title)
            {
                AppKit.Set(item, "setTitle:", AppKit.String(title));
            }
            AppKit.Set(item, "setSubtitle:", AppKit.String(subtitle));
        }
        else
        {
            // Older AppKit needs the subtitle in the title; reset rather than append on refresh.
            AppKit.Set(item, "setTitle:", AppKit.String($"{title} - {subtitle}"));
        }
    }

    private static void SetSymbol(nint item, string name, string description)
    {
        var image = AppKit.SendTwoPointers(AppKit.Class("NSImage"),
            AppKit.Selector("imageWithSystemSymbolName:accessibilityDescription:"), AppKit.String(name), AppKit.String(description));
        if (image == 0)
        {
            throw new InvalidOperationException("A required AppKit system symbol is unavailable.");
        }
        AppKit.Set(item, "setImage:", image);
    }

    private void SetIcon()
    {
        if (_brandImage == 0)
        {
            _brandImage = LoadEmbeddedImage("aspire.png");
            AppKit.SendSize(_brandImage, AppKit.Selector("setSize:"), new(24, 24));
            AppKit.SendBool(_brandImage, AppKit.Selector("setTemplate:"), 0);
        }
        if (_trayMarkImage == 0)
        {
            _trayMarkImage = LoadTrayMark();
        }
        var button = AppKit.Get(_statusItem, "button");
        AppKit.Set(button, "setImage:", GetTrayImage(_controller.State.HasActiveAppHosts));
        AppKit.Set(button, "setImagePosition:", 2);
    }

    private static unsafe nint LoadEmbeddedImage(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded icon '{resourceName}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        nint image;
        fixed (byte* address = bytes)
        {
            var data = AppKit.CreateData(AppKit.Class("NSData"), AppKit.Selector("dataWithBytes:length:"), address, (nuint)bytes.Length);
            image = AppKit.Get(AppKit.Get(AppKit.Class("NSImage"), "alloc"), "initWithData:", data);
        }
        if (image == 0)
        {
            throw new InvalidOperationException($"Could not decode the embedded icon '{resourceName}'.");
        }
        return image;
    }

    private static void OpenDashboardInBrowser(Uri uri)
    {
        var url = AppKit.Get(AppKit.Class("NSURL"), "URLWithString:", AppKit.String(uri.AbsoluteUri));
        var workspace = AppKit.Get(AppKit.Class("NSWorkspace"), "sharedWorkspace");
        if (url == 0 || AppKit.SendReturningBool(workspace, AppKit.Selector("openURL:"), url) == 0)
        {
            throw new InvalidOperationException("macOS could not open the dashboard URL.");
        }
    }

    private bool ConfirmStop(AppHostInfo host)
    {
        var alert = AppKit.Get(AppKit.Class("NSAlert"), "new");
        _modalDepth++;
        try
        {
            AppKit.Set(alert, "setMessageText:", AppKit.String($"Stop {AppHostPresentation.GetTitle(host)}?"));
            AppKit.Set(alert, "setInformativeText:", AppKit.String(
                $"Only this AppHost instance will be stopped. Persistent resources are left running.\n\n{host.AppHostPath}\nPID {host.AppHostPid}"));
            AppKit.Set(alert, "setAlertStyle:", 0);
            var icon = AppKit.Get(_brandImage, "copy");
            try
            {
                AppKit.SendSize(icon, AppKit.Selector("setSize:"), new(64, 64));
                AppKit.Set(alert, "setIcon:", icon);
            }
            finally
            {
                AppKit.Release(icon);
            }
            var cancel = AppKit.Get(alert, "addButtonWithTitle:", AppKit.String("Cancel"));
            var stop = AppKit.Get(alert, "addButtonWithTitle:", AppKit.String("Stop AppHost"));
            AppKit.Set(cancel, "setKeyEquivalent:", AppKit.String("\u001b"));
            AppKit.Set(stop, "setKeyEquivalent:", AppKit.String(""));
            // Keep the standard adaptive button text. AppKit's destructive red foreground
            // has poor contrast on the non-default gray button, especially in dark mode.
            AppKit.SendVoid(alert, AppKit.Selector("layout"));
            var window = AppKit.Get(alert, "window");
            // AppKit assigns Return to the default button. Escape still cancels runModal.
            AppKit.Set(window, "setDefaultButtonCell:", AppKit.Get(cancel, "cell"));
            AppKit.Set(stop, "setKeyEquivalent:", AppKit.String(""));

            if (_confirmStop is not null && !_interactiveSmoke)
            {
                return _confirmStop(new(host.Id, AppKit.Text(AppKit.Get(alert, "messageText")),
                    checked((int)AppKit.Get(AppKit.Get(alert, "buttons"), "count")),
                    AppKit.Get(window, "defaultButtonCell") == AppKit.Get(cancel, "cell")
                        && AppKit.Text(AppKit.Get(cancel, "keyEquivalent")) == "\r",
                    AppKit.Text(AppKit.Get(stop, "keyEquivalent")) == "",
                    AppKit.Get(alert, "icon") != 0 && AppKit.GetBool(AppKit.Get(alert, "icon"), AppKit.Selector("isTemplate")) == 0,
                    !AppKit.Supports(stop, "hasDestructiveAction")
                        || AppKit.GetBool(stop, AppKit.Selector("hasDestructiveAction")) == 0));
            }

            AppKit.SendBool(_application, AppKit.Selector("activateIgnoringOtherApps:"), 1);
            return AppKit.Get(alert, "runModal") == 1001;
        }
        finally
        {
            _modalDepth--;
            AppKit.Release(alert);
            RequestRefresh();
        }
    }

    private static unsafe void AttachAppHostIdentity(AppHostId id, nint dashboard, nint stop)
    {
        // AppKit may close a menu before delivering the selected item's target/action.
        // An immutable representedObject is retained by each item, so the identity survives
        // old-menu teardown for exactly as long as Cocoa retains the sender. There is no
        // managed per-menu map or GCHandle to invalidate/reuse during that dispatch gap.
        // https://developer.apple.com/documentation/appkit/nsmenuitem/representedobject
        nint* values = stackalloc nint[3]
        {
            AppKit.String(id.AppHostPath),
            AppKit.SendInt64(AppKit.Class("NSNumber"), AppKit.Selector("numberWithLongLong:"), id.AppHostPid),
            id.ProcessStartTimeUnixMilliseconds is long start
                ? AppKit.SendInt64(AppKit.Class("NSNumber"), AppKit.Selector("numberWithLongLong:"), start)
                : AppKit.Get(AppKit.Class("NSNull"), "null")
        };
        var identity = AppKit.CreateArray(AppKit.Class("NSArray"), AppKit.Selector("arrayWithObjects:count:"), values, 3);
        AppKit.Set(dashboard, "setRepresentedObject:", identity);
        AppKit.Set(stop, "setRepresentedObject:", identity);
    }

    private static AppHostId SelectedInstance(nint sender)
    {
        // Only our command items carry this private native shape:
        // [NSString normalizedPath, NSNumber pid, NSNumber lifetimeMilliseconds | NSNull].
        // Reading the item's own payload, rather than its current menu position, also avoids
        // accidental re-targeting when a replacement menu uses the same row index.
        var identity = AppKit.Get(sender, "representedObject");
        if (identity == 0 || AppKit.Get(identity, "count") != 3)
        {
            throw new InvalidOperationException("The selected AppHost is no longer available.");
        }
        var path = AppKit.Text(AppKit.Get(identity, "objectAtIndex:", 0));
        var pid = checked((int)AppKit.GetInt64(AppKit.Get(identity, "objectAtIndex:", 1), AppKit.Selector("longLongValue")));
        var lifetime = AppKit.Get(identity, "objectAtIndex:", 2);
        return new(path, pid, lifetime == AppKit.Get(AppKit.Class("NSNull"), "null")
            ? null : AppKit.GetInt64(lifetime, AppKit.Selector("longLongValue")));
    }

    private void DispatchAppHostAction(nint sender, Action<AppHostId> action)
    {
        try
        {
            action(SelectedInstance(sender));
        }
        catch (Exception ex)
        {
            ReportActionFailure(ex, "The selected AppHost action is unavailable.");
        }
    }

    private void MenuWillOpen(nint menu)
    {
        _openMenus.Add(menu);
        _controller.PruneMissingPinnedAppHosts();
        TraceSmokeTracking("menu-will-open");
        UpdateMenu(_controller.State);
    }

    private void MenuDidClose(nint menu)
    {
        _openMenus.Remove(menu);
        TraceSmokeTracking("menu-did-close");
        // A parent can close before its child. Defer until all delegates have closed, and
        // until AppKit has returned from this callback before replacing/releasing the tree.
        RequestRefresh();
    }

    private void Quit()
    {
        lock (_dispatchGate)
        {
            _quitting = true;
        }
        foreach (var menu in _openMenus.ToArray())
        {
            AppKit.SendVoid(menu, AppKit.Selector("cancelTrackingWithoutAnimation"));
        }
        if (_modalDepth > 0)
        {
            AppKit.SendVoid(_application, AppKit.Selector("abortModal"));
        }
        AppKit.Set(_application, "stop:", 0);
        // stop: alone does not wake nextEventMatchingMask: when invoked by a run-loop source
        // or timer. A harmless application-defined event lets run return for managed cleanup.
        var wake = AppKit.CreateEvent(AppKit.Class("NSEvent"),
            AppKit.Selector("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            15, new(0, 0), 0, 0, 0, 0, 0, 0, 0);
        AppKit.PostEvent(_application, AppKit.Selector("postEvent:atStart:"), wake, 1);
    }

    private void DetachMenuDelegates()
    {
        foreach (var menu in _menus)
        {
            AppKit.Set(menu, "setDelegate:", 0);
        }
    }

    private void DisposeNative()
    {
        lock (_dispatchGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _ready.TrySetException(new ObjectDisposedException(nameof(MacTrayApplication)));
            _restoreRequest?.Completion.TrySetException(new ObjectDisposedException(nameof(MacTrayApplication)));
            _restoreRequest = null;
        }
        DisposeSmokeTimer();
        DisposeContextMenuMonitor();
        if (_refreshSource != 0)
        {
            AppKit.CFRunLoopSourceInvalidate(_refreshSource);
            foreach (var mode in _runLoopModes)
            {
                AppKit.CFRunLoopRemoveSource(_runLoop, _refreshSource, mode);
            }
            AppKit.CFRelease(_refreshSource);
        }
        foreach (var mode in _runLoopModes)
        {
            AppKit.Release(mode);
        }
        if (_runLoop != 0)
        {
            AppKit.CFRelease(_runLoop);
        }
        DetachMenuDelegates();
        RemoveStatusItem();
        AppKit.Release(_menu);
        AppKit.Release(_brandImage);
        AppKit.Release(_trayMarkImage);
        foreach (var image in _trayImages.Values.Concat(_healthImages.Values))
        {
            AppKit.Release(image);
        }
        AppKit.Release(_target);
        _rows.Clear();
        _recentRows.Clear();
        _menus.Clear();
        Interlocked.CompareExchange(ref s_callbackRoot, null, this);
        if (_pool != 0)
        {
            AppKit.PopPool(_pool);
        }
    }

    private static void Route(nint target, nint sender, Action<MacTrayApplication, nint> callback)
    {
        var application = Volatile.Read(ref s_callbackRoot);
        if (application is null || target != application._target || application._disposed)
        {
            return;
        }
        var pool = AppKit.PushPool();
        try
        {
            application.VerifyUIThread();
            callback(application, sender);
        }
        catch (Exception ex)
        {
            LogFailure("Native UI callback failed", ex);
            application._exitCode = 1;
            try
            {
                application._controller.ReportActionError("The tray encountered an unexpected UI error.");
                application.Quit();
            }
            catch (Exception shutdownException)
            {
                LogFailure("Native UI shutdown failed", shutdownException);
            }
        }
        finally
        {
            AppKit.PopPool(pool);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRefresh(nint context) => Route(context, 0, static (app, _) => app.Refresh());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDashboard(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.DispatchAppHostAction(item, app.OpenDashboard));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStopAppHost(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.DispatchAppHostAction(item, app.StopAppHost));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnQuit(nint self, nint selector, nint sender) => Route(self, sender, static (app, _) => app.Quit());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMenuWillOpen(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, menu) => app.MenuWillOpen(menu));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMenuDidClose(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, menu) => app.MenuDidClose(menu));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSmokeTimeout(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app._smokeTimeout?.Invoke());

    private sealed record NativeAppHostRow(
        AppHostId Id, string Title, string DisplayName, nint Item, nint Submenu, nint Dashboard, nint Stop, nint Start, nint Pin);

    private sealed record RestoreRequest(CancellationToken CancellationToken)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
