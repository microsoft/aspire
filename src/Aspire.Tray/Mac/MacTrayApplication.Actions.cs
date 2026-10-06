// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication
{
    private readonly CancellationTokenSource _openShutdown = new();
    private readonly List<Task> _openTasks = [];
    private readonly List<(string Path, string Title)> _installedFolderApplications = [];

    private void DiscoverFolderApplications()
    {
        _installedFolderApplications.Clear();
        var workspace = AppKit.Get(AppKit.Class("NSWorkspace"), "sharedWorkspace");
        foreach (var (bundleId, title) in s_folderApplications)
        {
            var url = AppKit.Get(workspace, "URLForApplicationWithBundleIdentifier:", AppKit.String(bundleId));
            if (url != 0)
            {
                _installedFolderApplications.Add((AppKit.Text(AppKit.Get(url, "path")), title));
            }
        }
    }

    private void AddOpenInMenu(nint menu, string path)
    {
        var item = AddItem(menu, "Open In", null, enabled: true);
        var submenu = CreateMenu();
        try
        {
            var workspace = AppKit.Get(AppKit.Class("NSWorkspace"), "sharedWorkspace");
            var count = 0;
            // Services requires a responder/selection/pasteboard contract that a status-menu
            // project row does not have. Resolve a small list of folder-capable apps through
            // Launch Services instead, including installations outside /Applications.
            // https://developer.apple.com/documentation/appkit/nsworkspace/urlforapplication(withbundleidentifier:)
            foreach (var (applicationPath, title) in _installedFolderApplications)
            {
                var action = AddItem(submenu, title, "openIn:", enabled: true);
                AttachOpenInTarget(action, path, applicationPath);
                var icon = AppKit.Get(workspace, "iconForFile:", AppKit.String(applicationPath));
                var copy = AppKit.Get(icon, "copy");
                try
                {
                    AppKit.SendSize(copy, AppKit.Selector("setSize:"), new(16, 16));
                    AppKit.Set(action, "setImage:", copy);
                }
                finally
                {
                    AppKit.Release(copy);
                }
                count++;
            }
            if (count == 0)
            {
                AddItem(submenu, "No supported applications installed", null, enabled: false);
            }
            AppKit.Set(item, "setSubmenu:", submenu);
        }
        finally
        {
            AppKit.Release(submenu);
        }
    }

    private static void AttachPath(nint item, string path)
        => AppKit.Set(item, "setRepresentedObject:", AppKit.String(path));

    private static string SelectedPath(nint item)
    {
        var value = AppKit.Get(item, "representedObject");
        if (value == 0)
        {
            throw new InvalidOperationException("The selected AppHost is no longer available.");
        }
        return AppKit.Text(value);
    }

    private static unsafe void AttachOpenInTarget(nint item, string path, string application)
    {
        // This private immutable payload survives menu teardown just like exact-instance
        // stop identities: [NSString AppHostPath, NSString applicationBundlePath].
        nint* values = stackalloc nint[2] { AppKit.String(path), AppKit.String(application) };
        var target = AppKit.CreateArray(AppKit.Class("NSArray"), AppKit.Selector("arrayWithObjects:count:"), values, 2);
        AppKit.Set(item, "setRepresentedObject:", target);
    }

    private void RunAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ReportActionFailure(ex, "The selected action could not be completed. Try again.");
        }
    }

    private bool RequireProjectFile(string path)
    {
        if (!TrayAppHostPath.IsMissing(path))
        {
            TrayAppHostPath.RequireExistingFile(path);
            return true;
        }
        if (_controller.State.AppHosts.Any(host => host.IsPinned && host.Id.AppHostPath == path))
        {
            _controller.PruneMissingPinnedAppHosts();
            return false;
        }
        if (_controller.State.RecentAppHosts.Any(host => host.Id.AppHostPath == path))
        {
            if (ConfirmAction("AppHost not found",
                $"This AppHost no longer exists:\n\n{path}\n\nRemove it from recently opened AppHosts?", "Remove"))
            {
                _controller.RemoveRecent(path);
            }
        }
        else
        {
            throw new InvalidOperationException("AppHost file not found.");
        }
        return false;
    }

    private void StartAppHost(string path)
    {
        if (RequireProjectFile(path))
        {
            _controller.RequestStart(path);
        }
    }

    private void OpenIn(nint item)
    {
        var target = AppKit.Get(item, "representedObject");
        var path = AppKit.Text(AppKit.Get(target, "objectAtIndex:", 0));
        var application = AppKit.Text(AppKit.Get(target, "objectAtIndex:", 1));
        if (RequireProjectFile(path))
        {
            OpenFolder(Path.GetDirectoryName(path)!, application);
        }
    }

    private void ShowInFinder(string path)
    {
        if (!RequireProjectFile(path))
        {
            return;
        }
        OpenWithLaunchServices(["-R", "--", path]);
    }

    private static void CopyPathToPasteboard(string path)
    {
        // Use the retained path, not its compact/single-line menu label or a file URL.
        // Copy remains useful after the AppHost file has moved or been deleted.
        var pasteboard = AppKit.Get(AppKit.Class("NSPasteboard"), "generalPasteboard");
        if (pasteboard == 0)
        {
            throw new InvalidOperationException("macOS could not access the clipboard.");
        }
        AppKit.Get(pasteboard, "clearContents");
        // setString:forType: returns BOOL, unlike the pointer-returning objc_msgSend overload.
        // https://developer.apple.com/documentation/appkit/nspasteboard/setstring(_:fortype:)
        if (AppKit.SendTwoPointersReturningBool(pasteboard, AppKit.Selector("setString:forType:"),
            AppKit.String(path), AppKit.Constant("NSPasteboardTypeString")) == 0)
        {
            throw new InvalidOperationException("macOS could not copy the AppHost path to the clipboard. Try again.");
        }
    }

    private void OpenFolder(string path, string application)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException("The selected folder no longer exists.");
        }
        OpenWithLaunchServices(["-a", application, "--", path]);
    }

    private void OpenWithLaunchServices(string[] arguments)
    {
        _openTasks.RemoveAll(task => task.IsCompleted);
        _openTasks.Add(OpenWithLaunchServicesAsync(arguments));
    }

    private async Task OpenWithLaunchServicesAsync(string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _openShutdown.Token);
        var startInfo = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("macOS could not launch the selected application.");
            try
            {
                // Launch Services owns the opened application. Only wait for/reap our
                // short-lived `open` helper; quitting the tray must not close the user's app.
                var stderr = CliProcess.DrainAsync(process.StandardError, cancellation.Token);
                var stdout = CliProcess.DrainAsync(process.StandardOutput, cancellation.Token);
                await Task.WhenAll(process.WaitForExitAsync(cancellation.Token), stderr, stdout).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("macOS could not open the selected project folder.");
                }
            }
            finally
            {
                await CliProcess.TerminateOwnedChildAsync(process).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_openShutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportActionFailure(ex, "macOS could not open the selected project folder.");
        }
    }

    private void ClearRecent()
    {
        if (ConfirmAction("Do you want to clear all recently opened AppHosts?",
            "This action is irreversible.\n\nPinned AppHosts will be kept.", "Clear"))
        {
            _controller.ClearRecent();
        }
    }

    private bool ConfirmAction(string message, string detail, string actionTitle)
    {
        var alert = AppKit.Get(AppKit.Class("NSAlert"), "new");
        _modalDepth++;
        try
        {
            AppKit.Set(alert, "setMessageText:", AppKit.String(message));
            AppKit.Set(alert, "setInformativeText:", AppKit.String(detail));
            AppKit.Set(alert, "setIcon:", _brandImage);
            var cancel = AppKit.Get(alert, "addButtonWithTitle:", AppKit.String("Cancel"));
            var action = AppKit.Get(alert, "addButtonWithTitle:", AppKit.String(actionTitle));
            AppKit.SendVoid(alert, AppKit.Selector("layout"));
            AppKit.Set(AppKit.Get(alert, "window"), "setDefaultButtonCell:", AppKit.Get(cancel, "cell"));
            AppKit.Set(action, "setKeyEquivalent:", AppKit.String(""));
            if (_confirmAction is not null && !_interactiveSmoke)
            {
                return _confirmAction(new(message, detail, actionTitle,
                    AppKit.Text(AppKit.Get(cancel, "keyEquivalent")) == "\r",
                    AppKit.Text(AppKit.Get(action, "keyEquivalent")) == ""));
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStartAppHost(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.RunAction(() => app.StartAppHost(SelectedPath(item))));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPinAppHost(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.RunAction(() => app._controller.SetPinned(SelectedPath(item), true)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUnpinAppHost(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.RunAction(() => app._controller.SetPinned(SelectedPath(item), false)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClearRecent(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app.RunAction(app.ClearRecent));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOpenIn(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.RunAction(() => app.OpenIn(item)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShowInFinder(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.RunAction(() => app.ShowInFinder(SelectedPath(item))));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCopyPath(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.RunAction(() => app._copyPath(Path.GetDirectoryName(SelectedPath(item))!)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOpenDocumentation(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app.RunAction(() => app._openDashboard(new Uri("https://aspire.dev"))));

    private static readonly (string BundleId, string Title)[] s_folderApplications =
    [
        ("com.microsoft.VSCode", "Visual Studio Code"),
        ("com.microsoft.VSCodeInsiders", "Visual Studio Code - Insiders"),
        ("com.apple.Terminal", "Terminal"),
        ("com.mitchellh.ghostty", "Ghostty"),
        ("com.googlecode.iterm2", "iTerm"),
        ("com.jetbrains.rider", "Rider"),
        ("com.apple.dt.Xcode", "Xcode")
    ];
}
