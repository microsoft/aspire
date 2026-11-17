// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication
{
    private readonly CancellationTokenSource _openShutdown = new();
    private readonly List<Task> _openTasks = [];
    private readonly List<(string Path, string Title)> _installedFolderApplications = [];

    private void DiscoverFolderApplications()
    {
        _installedFolderApplications.Clear();
        ReleaseAppIcons();
        // Services requires a responder/selection/pasteboard contract that a panel row does not
        // have. Resolve a small list of folder-capable apps through Launch Services instead,
        // including installations outside /Applications.
        // https://developer.apple.com/documentation/appkit/nsworkspace/urlforapplication(withbundleidentifier:)
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

    /// <summary>
    /// Checks that an AppHost's source file still exists before acting on it. A missing pinned
    /// AppHost is pruned. A missing recent one is offered for removal inline in the panel.
    /// </summary>
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
        if (_controller.State.RecentAppHosts.FirstOrDefault(host => host.Id.AppHostPath == path) is { } recent && _panel is not null)
        {
            ApplyPanelResponse(_panel.ConfirmRemoveMissing(recent));
            return false;
        }
        throw new InvalidOperationException("AppHost file not found.");
    }

    private void StartAppHost(string path)
    {
        if (RequireProjectFile(path))
        {
            _controller.RequestStart(path);
        }
    }

    private void OpenIn(string path, string title)
    {
        var application = _installedFolderApplications.FirstOrDefault(application => application.Title == title).Path
            ?? throw new InvalidOperationException("The selected application is no longer available.");
        if (RequireProjectFile(path))
        {
            HidePanel();
            OpenFolder(Path.GetDirectoryName(path)!, application);
        }
    }

    private void ShowInFinder(string path)
    {
        if (RequireProjectFile(path))
        {
            HidePanel();
            OpenWithLaunchServices(["-R", "--", path]);
        }
    }

    private static void CopyPathToPasteboard(string path)
    {
        // Use the AppHost's own path, not its compact row label or a file URL.
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
