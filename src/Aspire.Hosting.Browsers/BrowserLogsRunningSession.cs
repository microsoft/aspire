// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal interface IBrowserLogsRunningSession
{
    string SessionId { get; }

    string BrowserExecutable { get; }

    Uri? BrowserDebugEndpoint { get; }

    BrowserHostOwnership BrowserHostOwnership { get; }

    int? ProcessId { get; }

    DateTime StartedAt { get; }

    string TargetId { get; }

    Task StartCompletionObserver(Func<int?, Exception?, Task> onCompleted);

    Task<byte[]> CaptureScreenshotAsync(BrowserScreenshotCaptureOptions options, CancellationToken cancellationToken);

    Task NavigateAsync(Uri url, CancellationToken cancellationToken);

    Task<string> EvaluateJsonAsync(string expression, TimeSpan? timeout, CancellationToken cancellationToken);

    Task<string> SendCdpCommandJsonAsync(string method, string? parametersJson, string session, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

internal interface IBrowserLogsRunningSessionFactory
{
    Task<IBrowserLogsRunningSession> StartSessionAsync(
        BrowserConfiguration configuration,
        string resourceName,
        Uri url,
        string sessionId,
        ILogger resourceLogger,
        CancellationToken cancellationToken);
}

internal sealed class BrowserLogsRunningSessionFactory : IBrowserLogsRunningSessionFactory, IAsyncDisposable
{
    private readonly BrowserHostRegistry _browserHostRegistry;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly TimeProvider _timeProvider;

    public BrowserLogsRunningSessionFactory(ILogger<BrowserLogsSessionManager> logger, TimeProvider timeProvider)
    {
        _browserHostRegistry = new BrowserHostRegistry(logger, timeProvider);
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<IBrowserLogsRunningSession> StartSessionAsync(
        BrowserConfiguration configuration,
        string resourceName,
        Uri url,
        string sessionId,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        return await BrowserLogsRunningSession.StartAsync(
            configuration,
            resourceName,
            sessionId,
            url,
            _browserHostRegistry,
            resourceLogger,
            _logger,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _browserHostRegistry.DisposeAsync();
}

// Owns one tracked browser page session. The browser host may be shared with other sessions; this type keeps the
// per-resource page lifecycle, diagnostics, and recovery.
internal sealed class BrowserLogsRunningSession : IBrowserLogsRunningSession
{
    private readonly BrowserEventLogger _eventLogger;
    private readonly BrowserConnectionDiagnosticsLogger _connectionDiagnostics;
    private readonly BrowserHostRegistry _browserHostRegistry;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly ILogger _resourceLogger;
    private readonly string _resourceName;
    private readonly BrowserConfiguration _configuration;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TimeProvider _timeProvider;
    private readonly Uri _url;

    private string? _browserExecutable;
    private Uri? _browserEndpoint;
    private BrowserHostLease? _browserHostLease;
    private BrowserHostOwnership? _browserHostOwnership;
    private Task<BrowserSessionResult>? _completion;
    private int _cleanupState;
    private int? _processId;
    private string? _targetId;
    private IBrowserPageSession? _pageSession;
    private string? _targetSessionId;

    private BrowserLogsRunningSession(
        BrowserConfiguration configuration,
        string resourceName,
        string sessionId,
        Uri url,
        BrowserHostRegistry browserHostRegistry,
        ILogger resourceLogger,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider)
    {
        _eventLogger = new BrowserEventLogger(sessionId, resourceLogger);
        _connectionDiagnostics = new BrowserConnectionDiagnosticsLogger(sessionId, resourceLogger);
        _browserHostRegistry = browserHostRegistry;
        _logger = logger;
        _resourceLogger = resourceLogger;
        _resourceName = resourceName;
        _configuration = configuration;
        _sessionId = sessionId;
        _timeProvider = timeProvider;
        _url = url;
    }

    public string SessionId => _sessionId;

    public string BrowserExecutable => _browserExecutable ?? throw new InvalidOperationException("Browser executable is not available before the session starts.");

    public Uri? BrowserDebugEndpoint => _browserEndpoint;

    public BrowserHostOwnership BrowserHostOwnership => _browserHostOwnership ?? throw new InvalidOperationException("Browser host ownership is not available before the session starts.");

    public int? ProcessId => _processId;

    public DateTime StartedAt { get; private set; }

    public string TargetId => _targetId ?? throw new InvalidOperationException("Browser target id is not available before the session starts.");

    private Task<BrowserSessionResult> Completion => _completion ?? throw new InvalidOperationException("Session has not been started.");

    public static async Task<BrowserLogsRunningSession> StartAsync(
        BrowserConfiguration configuration,
        string resourceName,
        string sessionId,
        Uri url,
        BrowserHostRegistry browserHostRegistry,
        ILogger resourceLogger,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = new BrowserLogsRunningSession(configuration, resourceName, sessionId, url, browserHostRegistry, resourceLogger, logger, timeProvider);

        try
        {
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            session._completion = session.MonitorAsync();
            return session;
        }
        catch
        {
            await session.CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task StartCompletionObserver(Func<int?, Exception?, Task> onCompleted)
    {
        return ObserveCompletionAsync(onCompleted);
    }

    public async Task<byte[]> CaptureScreenshotAsync(BrowserScreenshotCaptureOptions options, CancellationToken cancellationToken)
    {
        var pageSession = _pageSession ?? throw new InvalidOperationException("Browser page session is not available.");
        var result = await pageSession.CaptureScreenshotAsync(options, cancellationToken).ConfigureAwait(false);

        var data = result.Data ?? throw new InvalidOperationException("Tracked browser screenshot capture returned no image data.");

        try
        {
            return Convert.FromBase64String(data);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Tracked browser screenshot capture returned invalid image data.", ex);
        }
    }

    public async Task NavigateAsync(Uri url, CancellationToken cancellationToken)
    {
        var pageSession = _pageSession ?? throw new InvalidOperationException("Browser page session is not available.");
        await pageSession.NavigateAsync(url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> EvaluateJsonAsync(string expression, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var pageSession = _pageSession ?? throw new InvalidOperationException("Browser page session is not available.");
        return await pageSession.EvaluateJsonAsync(expression, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SendCdpCommandJsonAsync(string method, string? parametersJson, string session, CancellationToken cancellationToken)
    {
        var pageSession = _pageSession ?? throw new InvalidOperationException("Browser page session is not available.");
        return await pageSession.SendCdpCommandJsonAsync(method, parametersJson, session, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _stopCts.Cancel(); } catch (ObjectDisposedException) { }

        // Stopping a dashboard browser automation session should close only the page target it created. The shared browser
        // process/window is released through the lease and may stay alive while other resource sessions are still active.
        await DisposePageSessionAsync().ConfigureAwait(false);
        await DisposeBrowserHostLeaseAsync().ConfigureAwait(false);

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _resourceLogger.LogInformation(
            "[{SessionId}] Resolving tracked browser host. User data mode: {UserDataMode}; browser: '{Browser}'; profile: '{Profile}'.",
            _sessionId,
            _configuration.UserDataMode,
            _configuration.Browser,
            _configuration.Profile ?? "(default)");

        try
        {
            _browserHostLease = await _browserHostRegistry.AcquireAsync(_configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _connectionDiagnostics.LogSetupFailure("Acquiring the tracked browser host", ex);
            throw;
        }

        var browserHost = _browserHostLease.Host;
        _browserExecutable = browserHost.Identity.ExecutablePath;
        _browserEndpoint = browserHost.DebugEndpoint;
        _browserHostOwnership = browserHost.Ownership;
        _processId = browserHost.ProcessId;
        StartedAt = _timeProvider.GetUtcNow().UtcDateTime;
        _resourceLogger.LogInformation(
            "[{SessionId}] Using {Ownership} tracked browser host '{BrowserExecutable}' at '{Endpoint}'.",
            _sessionId,
            browserHost.Ownership,
            _browserExecutable,
            FormatDebugEndpoint(_browserEndpoint));

        try
        {
            // A running session represents one resource URL, not one browser process. In the playground multiple
            // resources can share a host, while each resource still gets its own page target so console and network
            // events stay scoped to the right resource logs.
            _pageSession = await browserHost.CreatePageSessionAsync(
                _sessionId,
                _url,
                _connectionDiagnostics,
                protocolEvent =>
                {
                    _eventLogger.HandleEvent(protocolEvent);
                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
            _targetId = _pageSession.TargetId;
            _targetSessionId = _pageSession.TargetSessionId;
            _resourceLogger.LogInformation(
                "[{SessionId}] Attached to tracked browser page target '{TargetId}' with target session '{TargetSessionId}'.",
                _sessionId,
                _targetId,
                _targetSessionId);
        }
        catch (Exception ex)
        {
            _connectionDiagnostics.LogSetupFailure("Setting up the tracked browser page", ex);
            throw;
        }

        _resourceLogger.LogInformation("[{SessionId}] Tracking browser console logs for '{Url}'.", _sessionId, _url);
    }

    private async Task<BrowserSessionResult> MonitorAsync()
    {
        try
        {
            var pageSession = _pageSession ?? throw new InvalidOperationException("Browser page session is not available.");
            var result = await pageSession.Completion.ConfigureAwait(false);
            // Closing the tracked tab by hand is a normal completion. Browser process exit, page crash, or an
            // unrecoverable CDP connection loss is surfaced as an error so the dashboard resource shows what happened.
            return result.CompletionKind switch
            {
                BrowserPageSessionCompletionKind.Stopped => new BrowserSessionResult(ExitCode: null, Error: null),
                BrowserPageSessionCompletionKind.PageClosed => new BrowserSessionResult(ExitCode: null, Error: null),
                BrowserPageSessionCompletionKind.BrowserExited => new BrowserSessionResult(ExitCode: null, result.Error),
                BrowserPageSessionCompletionKind.PageCrashed => new BrowserSessionResult(ExitCode: null, result.Error),
                BrowserPageSessionCompletionKind.ConnectionLost => new BrowserSessionResult(ExitCode: null, result.Error),
                _ => new BrowserSessionResult(ExitCode: null, Error: null)
            };
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    private async Task ObserveCompletionAsync(Func<int?, Exception?, Task> onCompleted)
    {
        try
        {
            var result = await Completion.ConfigureAwait(false);
            await onCompleted(result.ExitCode, result.Error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tracked browser completion observer failed for resource '{ResourceName}' and session '{SessionId}'.", _resourceName, _sessionId);
        }
    }

    private async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _cleanupState, 1) != 0)
        {
            return;
        }

        try
        {
            await DisposePageSessionAsync().ConfigureAwait(false);
            await DisposeBrowserHostLeaseAsync().ConfigureAwait(false);
        }
        finally
        {
            // Always dispose the stop CTS even if page or lease cleanup fails; otherwise the CTS and any registrations
            // on it leak until process exit.
            _stopCts.Dispose();
        }
    }

    private async Task DisposePageSessionAsync()
    {
        var pageSession = _pageSession;
        _pageSession = null;

        if (pageSession is not null)
        {
            await pageSession.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string FormatDebugEndpoint(Uri? browserDebugEndpoint) =>
        browserDebugEndpoint?.ToString() ?? "private CDP pipe";

    private async Task DisposeBrowserHostLeaseAsync()
    {
        var browserHostLease = _browserHostLease;
        _browserHostLease = null;

        if (browserHostLease is not null)
        {
            await browserHostLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record BrowserSessionResult(int? ExitCode, Exception? Error);
}
