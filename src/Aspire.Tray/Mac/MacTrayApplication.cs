// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication : IDisposable
{
    private readonly TrayController _controller;
    private readonly Action<Uri> _openDashboard;
    private readonly Func<StopConfirmation, bool>? _confirmStop;
    private readonly string _autosaveName;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private int _exitCode;

    public MacTrayApplication(
        TrayController controller,
        string autosaveName,
        Action<Uri>? openDashboard = null,
        Func<StopConfirmation, bool>? confirmStop = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(autosaveName);
        _autosaveName = autosaveName;
        _controller = controller;
        _openDashboard = openDashboard ?? OpenDashboardInBrowser;
        _confirmStop = confirmStop;
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
        try
        {
            var selected = _controller.RequireLiveInstance(id);
            if (ConfirmStop(selected))
            {
                // Discovery can replace a process while the modal alert is open.
                // RequestStop revalidates this exact lifetime and does not block the UI.
                _controller.RequestStop(selected.Id);
            }
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

internal sealed record StopConfirmation(
    AppHostId AppHost,
    string Message,
    int ButtonCount,
    bool CancelIsDefault,
    bool StopRequiresExplicitChoice,
    bool HasColorIcon);
