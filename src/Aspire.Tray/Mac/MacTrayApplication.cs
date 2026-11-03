// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication : IDisposable
{
    private readonly TrayController _controller;
    private readonly ITrayStartupSettings _startupSettings;
    private readonly Action<Uri> _openDashboard;
    private readonly Action<string> _copyPath;
    private readonly string _autosaveName;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private int _exitCode;

    public MacTrayApplication(
        TrayController controller,
        string autosaveName,
        ITrayStartupSettings startupSettings,
        Action<Uri>? openDashboard = null,
        Action<string>? copyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(autosaveName);
        ArgumentNullException.ThrowIfNull(startupSettings);
        _autosaveName = autosaveName;
        _controller = controller;
        _startupSettings = startupSettings;
        _openDashboard = openDashboard ?? OpenDashboardInBrowser;
        _copyPath = copyPath ?? CopyPathToPasteboard;
        InitializeNative();
        _controller.Changed += RequestRefresh;
        RequestRefresh();
    }

    public int Run()
    {
        VerifyUIThread();
        RunNativeApplication();
        return _exitCode;
    }

    public void Dispose()
    {
        VerifyUIThread();
        _controller.Changed -= RequestRefresh;
        _openShutdown.Cancel();
        Task.WhenAll(_openTasks).GetAwaiter().GetResult();
        _openShutdown.Dispose();
        DisposeNative();
    }

    private void OpenDashboard(AppHostId id)
    {
        try
        {
            _openDashboard(_controller.GetDashboardUri(id));
        }
        catch (Exception ex)
        {
            ReportActionFailure(ex, "Unable to open dashboard. Try again.");
        }
    }

    private void StopAppHost(AppHostId id)
    {
        // Stopping isn't confirmed, as in the dashboard. RequestStop revalidates the exact
        // process lifetime, so a stale or replaced AppHost is never stopped by mistake.
        try
        {
            _controller.RequestStop(_controller.RequireLiveInstance(id).Id);
        }
        catch (Exception ex)
        {
            ReportActionFailure(ex, "Unable to request a stop. Try again.");
        }
    }

    private void ReportActionFailure(Exception exception, string fallback)
    {
        LogFailure("Tray action rejected", exception);
        _controller.ReportActionError(exception is InvalidOperationException ? exception.Message : fallback);
    }

    private void VerifyUIThread()
    {
        if (Environment.CurrentManagedThreadId != _uiThread)
        {
            throw new InvalidOperationException("AppKit operations must run on the application's original thread.");
        }
    }

    private static void LogFailure(string category, Exception exception)
    {
        // URLs and CLI payloads can contain credentials. Log only the exception category.
        try
        {
            Console.Error.WriteLine($"{category} ({exception.GetType().Name}).");
        }
        catch
        {
            // Logging must never unwind a reverse P/Invoke callback.
        }
    }
}
