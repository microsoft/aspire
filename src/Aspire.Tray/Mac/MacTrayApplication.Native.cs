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
    private readonly List<nint> _runLoopModes = [];
    private nint _pool;
    private nint _application;
    private nint _target;
    private nint _statusItem;
    private nint _brandImage;
    private nint _trayMarkImage;
    private readonly Dictionary<bool, nint> _trayImages = [];
    private nint _runLoop;
    private nint _refreshSource;
    private bool _refreshPending;
    private bool _disposed;
    private bool _quitting;
    private bool _quitRequested;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RestoreRequest? _restoreRequest;

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
            AddCallback(callbackClass, "smokeTimeout:", &OnSmokeTimeout);
            AddCallback(callbackClass, "smokeShowPreview:", &OnSmokeShowPreview);
            AddPanelCallbacks(callbackClass);
            AppKit.RegisterClass(callbackClass);
            _target = AppKit.Get(callbackClass, "new");
            RegisterPanelClasses();

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
            // Explicitly service changes during event tracking (a click held on the status item)
            // and modal loops, not just the default loop. CF signaling allocates no autoreleased
            // Objective-C objects on worker threads. A source coalesces notifications until it
            // is performed.
            // https://developer.apple.com/documentation/corefoundation/cfrunloopsourcesignal(_:)
            foreach (var name in new[] { "NSDefaultRunLoopMode", "NSEventTrackingRunLoopMode", "NSModalPanelRunLoopMode" })
            {
                var mode = AppKit.Get(AppKit.Constant(name), "retain");
                _runLoopModes.Add(mode);
                AppKit.CFRunLoopAddSource(_runLoop, _refreshSource, mode);
            }

            CreateStatusItem();
            UpdateStatusItem(_controller.State);
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
        UpdateStatusItem(state);
        _ready.TrySetResult();
        StateRendered?.Invoke(state);
    }

    private void RestoreRequestedIcon()
    {
        RestoreRequest? request;
        lock (_dispatchGate)
        {
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
            HidePanel();
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
            UpdateStatusItem(_controller.State);
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
        // No menu is attached: every activation of the status item opens the flyout panel.
        AttachStatusItemAction();
    }

    private void RemoveStatusItem()
    {
        RemoveTrayBadge();
        if (_statusItem != 0)
        {
            AppKit.Set(AppKit.Get(AppKit.Class("NSStatusBar"), "systemStatusBar"), "removeStatusItem:", _statusItem);
            AppKit.Release(_statusItem);
            _statusItem = 0;
        }
    }

    private void UpdateStatusItem(TrayViewState state)
    {
        var button = AppKit.Get(_statusItem, "button");
        AppKit.Set(button, "setTitle:", AppKit.String(""));
        var iconStatus = TrayIconStatusRules.Get(state);
        ApplyTrayIcon(iconStatus);
        AppKit.Set(button, "setToolTip:", AppKit.String($"Aspire\n{state.Status}"));
        AppKit.Set(button, "setAccessibilityLabel:", AppKit.String($"Aspire, {TrayIconStatusRules.Describe(iconStatus)}, {state.Status}"));
        UpdatePanel(state);
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
        AppKit.Set(button, "setImagePosition:", 2);
        ApplyTrayIcon(TrayIconStatusRules.Get(_controller.State));
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

    private void Quit()
    {
        lock (_dispatchGate)
        {
            _quitting = true;
        }
        HidePanel();
        AppKit.Set(_application, "stop:", 0);
        // stop: alone does not wake nextEventMatchingMask: when invoked by a run-loop source
        // or timer. A harmless application-defined event lets run return for managed cleanup.
        var wake = AppKit.CreateEvent(AppKit.Class("NSEvent"),
            AppKit.Selector("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            15, new(0, 0), 0, 0, 0, 0, 0, 0, 0);
        AppKit.PostEvent(_application, AppKit.Selector("postEvent:atStart:"), wake, 1);
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
        DisposePanel();
        RemoveStatusItem();
        AppKit.Release(_brandImage);
        AppKit.Release(_trayMarkImage);
        foreach (var image in _trayImages.Values)
        {
            AppKit.Release(image);
        }
        AppKit.Release(_target);
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
    private static void OnSmokeTimeout(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app._smokeTimeout?.Invoke());

    private sealed record RestoreRequest(CancellationToken CancellationToken)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
