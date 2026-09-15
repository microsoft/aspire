// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication : IDisposable
{
    private readonly TrayController _controller;
    private readonly ITrayStartupSettings _startupSettings;
    private readonly Action<Uri> _openDashboard;
    private readonly Func<StopConfirmation, StopConfirmationResult>? _confirmStop;
    private readonly Func<TrayConfirmation, bool>? _confirmAction;
    private readonly Action<string> _copyPath;
    private readonly string _autosaveName;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private int _exitCode;

    public MacTrayApplication(
        TrayController controller,
        string autosaveName,
        ITrayStartupSettings startupSettings,
        Action<Uri>? openDashboard = null,
        Func<StopConfirmation, StopConfirmationResult>? confirmStop = null,
        Func<TrayConfirmation, bool>? confirmAction = null,
        Action<string>? copyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(autosaveName);
        ArgumentNullException.ThrowIfNull(startupSettings);
        _autosaveName = autosaveName;
        _controller = controller;
        _startupSettings = startupSettings;
        _openDashboard = openDashboard ?? OpenDashboardInBrowser;
        _confirmStop = confirmStop;
        _confirmAction = confirmAction;
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
        try
        {
            var selected = _controller.RequireLiveInstance(id);
            var confirmation = _controller.ConfirmStop ? ConfirmStop(selected) : new StopConfirmationResult(true, false);
            if (confirmation.Confirmed)
            {
                // Dispatch revalidates the exact lifetime after the modal alert. A canceled
                // or stale action must not suppress later warnings.
                _controller.RequestStop(selected.Id);
                if (confirmation.DontAskAgain)
                {
                    _controller.SetConfirmStop(false);
                }
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
    string Detail,
    int ButtonCount,
    bool CancelIsDefault,
    bool StopRequiresExplicitChoice,
    bool HasColorIcon,
    bool HasStandardButtonContrast,
    bool ShowsSuppressionButton,
    string SuppressionTitle,
    bool SuppressionIsChecked);

internal readonly record struct StopConfirmationResult(bool Confirmed, bool DontAskAgain);

internal sealed record TrayConfirmation(
    string Message,
    string Detail,
    string ActionTitle,
    bool CancelIsDefault,
    bool ActionRequiresExplicitChoice);
