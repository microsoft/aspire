// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication
{
    private nint _eventMonitor;
    private nint _eventBlock;
    private nint _eventBlockDescriptor;
    private string? _pinContextPath;
    private AppKit.NativePoint _pinContextLocation;

    private unsafe void InstallContextMenuMonitor()
    {
        // A global Objective-C block has no managed captures. It routes to the same rooted
        // application as the target/action callbacks and is removed before that root is freed.
        // https://clang.llvm.org/docs/Block-ABI-Apple.html
        _eventBlockDescriptor = (nint)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        *(BlockDescriptor*)_eventBlockDescriptor = new() { Size = (nuint)sizeof(EventBlock) };
        _eventBlock = (nint)NativeMemory.AllocZeroed((nuint)sizeof(EventBlock));
        var library = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        try
        {
            *(EventBlock*)_eventBlock = new()
            {
                Isa = NativeLibrary.GetExport(library, "_NSConcreteGlobalBlock"),
                Flags = 1 << 28,
                Invoke = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&OnLocalEvent,
                Descriptor = _eventBlockDescriptor
            };
            // Observe only this application's right-clicks, never global keyboard/mouse input.
            // https://developer.apple.com/documentation/appkit/nsevent/addlocalmonitorforevents(matching:handler:)
            _eventMonitor = AppKit.SendTwoPointers(AppKit.Class("NSEvent"),
                AppKit.Selector("addLocalMonitorForEventsMatchingMask:handler:"), 1 << 3, _eventBlock);
            if (_eventMonitor == 0)
            {
                throw new InvalidOperationException("Could not register AppHost context actions.");
            }
            AppKit.Get(_eventMonitor, "retain");
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private nint HandleLocalEvent(nint nativeEvent)
    {
        if (_openMenus.Count == 0 || _modalDepth != 0 || _quitting)
        {
            return nativeEvent;
        }
        var item = AppKit.Get(_menu, "highlightedItem");
        var row = _rows.FirstOrDefault(row => row.Item == item);
        if (row is null)
        {
            item = AppKit.Get(_recentMenu, "highlightedItem");
            row = _recentRows.FirstOrDefault(row => row.Item == item);
        }
        if (row is null)
        {
            return nativeEvent;
        }
        _pinContextPath = row.Id.AppHostPath;
        _pinContextLocation = AppKit.GetPoint(AppKit.Class("NSEvent"), AppKit.Selector("mouseLocation"));
        foreach (var menu in _openMenus.ToArray())
        {
            AppKit.SendVoid(menu, AppKit.Selector("cancelTrackingWithoutAnimation"));
        }
        // Do not open another nested tracking loop inside NSEvent's monitor callback. Close
        // the old tree first and let the main-loop source present the immutable path action.
        RequestRefresh();
        return 0;
    }

    private void ShowPendingPinContextMenu()
    {
        if (_pinContextPath is not string path || _openMenus.Count != 0 || _modalDepth != 0)
        {
            return;
        }
        _pinContextPath = null;
        var host = _controller.State.AppHosts.Concat(_controller.State.RecentAppHosts)
            .FirstOrDefault(host => host.Id.AppHostPath == path);
        if (host is null)
        {
            return;
        }
        var menu = CreatePinContextMenu(path, host.IsPinned);
        try
        {
            AppKit.PopUpMenu(menu, AppKit.Selector("popUpMenuPositioningItem:atLocation:inView:"),
                0, _pinContextLocation, 0);
        }
        finally
        {
            AppKit.Set(menu, "setDelegate:", 0);
            _menus.Remove(menu);
            AppKit.Release(menu);
        }
    }

    private nint CreatePinContextMenu(string path, bool pinned)
    {
        var menu = CreateMenu();
        var item = AddItem(menu, pinned ? "Unpin AppHost" : "Pin AppHost",
            pinned ? "unpinAppHost:" : "pinAppHost:", enabled: true);
        SetSymbol(item, pinned ? "pin.slash" : "pin", pinned ? "Unpin AppHost" : "Pin AppHost");
        AttachPath(item, path);
        return menu;
    }

    private unsafe void DisposeContextMenuMonitor()
    {
        if (_eventMonitor != 0)
        {
            AppKit.Set(AppKit.Class("NSEvent"), "removeMonitor:", _eventMonitor);
            AppKit.Release(_eventMonitor);
            _eventMonitor = 0;
        }
        NativeMemory.Free((void*)_eventBlock);
        NativeMemory.Free((void*)_eventBlockDescriptor);
        _eventBlock = 0;
        _eventBlockDescriptor = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnLocalEvent(nint block, nint nativeEvent)
    {
        var application = Volatile.Read(ref s_callbackRoot);
        if (application is null || application._disposed)
        {
            return nativeEvent;
        }
        try
        {
            application.VerifyUIThread();
            return application.HandleLocalEvent(nativeEvent);
        }
        catch (Exception ex)
        {
            LogFailure("Unable to open AppHost context actions", ex);
            application._controller.ReportActionError("Unable to open AppHost context actions. Use the AppHost submenu instead.");
            return nativeEvent;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventBlock
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }
}
