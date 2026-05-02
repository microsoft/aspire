// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class SafariBidiPageSession : IBrowserPageSession
{
    private static readonly TimeSpan s_closeContextTimeout = TimeSpan.FromSeconds(3);

    private readonly TaskCompletionSource<BrowserPageSessionResult> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly BrowserConnectionDiagnosticsLogger _connectionDiagnostics;
    private readonly BrowserLogsBidiConnectionFactory _connectionFactory;
    private readonly Func<BrowserDiagnosticEvent, ValueTask> _eventHandler;
    private readonly SafariWebDriverHost _host;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Uri _url;

    private IBrowserLogsBidiConnection? _connection;
    private Task<BrowserPageSessionResult>? _monitorTask;
    private int _disposed;
    private string? _targetId;

    private SafariBidiPageSession(
        SafariWebDriverHost host,
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        Func<BrowserDiagnosticEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        BrowserLogsBidiConnectionFactory connectionFactory)
    {
        _connectionDiagnostics = connectionDiagnostics;
        _connectionFactory = connectionFactory;
        _eventHandler = eventHandler;
        _host = host;
        _logger = logger;
        _sessionId = sessionId;
        _url = url;
    }

    public string TargetId => _targetId ?? throw new InvalidOperationException("Safari WebDriver BiDi context id is not available before the page session starts.");

    public string TargetSessionId => TargetId;

    public Task<BrowserPageSessionResult> Completion => _monitorTask ?? throw new InvalidOperationException("Safari page session has not started.");

    public async Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var connection = _connection ?? throw new InvalidOperationException("Safari WebDriver BiDi connection is not available.");
            using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
            var result = await connection.CaptureScreenshotAsync(TargetId, captureCts.Token).ConfigureAwait(false);
            return new BrowserLogsCaptureScreenshotResult { Data = result.Data };
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public static async Task<SafariBidiPageSession> StartAsync(
        SafariWebDriverHost host,
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        Func<BrowserDiagnosticEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        BrowserLogsBidiConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        _ = timeProvider;
        var pageSession = new SafariBidiPageSession(host, sessionId, url, connectionDiagnostics, eventHandler, logger, connectionFactory);

        try
        {
            await pageSession.InitializeAsync(cancellationToken).ConfigureAwait(false);
            pageSession._monitorTask = pageSession.MonitorAsync();
            return pageSession;
        }
        catch
        {
            await pageSession.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopCts.Cancel();

        await _connectionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_connection is not null && _targetId is not null)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(s_closeContextTimeout);
                    await _connection.CloseBrowsingContextAsync(_targetId, closeCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to close Safari BiDi context '{ContextId}' for session '{SessionId}'.", _targetId, _sessionId);
                }
            }
        }
        finally
        {
            _connectionLock.Release();
        }

        _completionSource.TrySetResult(new BrowserPageSessionResult(BrowserPageSessionCompletionKind.Stopped, Error: null));
        await DisposeConnectionAsync().ConfigureAwait(false);

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _stopCts.Dispose();
        _connectionLock.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Safari flow:
            // 1. Connect to the WebDriver BiDi websocket returned by the WebDriver New Session response.
            // 2. Create one top-level browsing context.
            // 3. Subscribe to BrowserLogs diagnostics events for only that context.
            // 4. Navigate the context to the resource URL.
            _connection = await _connectionFactory(
                _host.WebDriverSession.WebSocketUrl,
                HandleEventAsync,
                _logger,
                cancellationToken).ConfigureAwait(false);

            var createContextResult = await _connection.CreateBrowsingContextAsync(cancellationToken).ConfigureAwait(false);
            _targetId = createContextResult.Context
                ?? throw new InvalidOperationException("Safari WebDriver BiDi context creation did not return a context id.");

            await _connection.SubscribeAsync(_targetId, cancellationToken).ConfigureAwait(false);
            await _connection.NavigateAsync(_targetId, _url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _connectionDiagnostics.LogSetupFailure("Setting up the Safari WebDriver BiDi page", ex);
            throw;
        }
    }

    private async Task<BrowserPageSessionResult> MonitorAsync()
    {
        try
        {
            var connection = _connection ?? throw new InvalidOperationException("Safari WebDriver BiDi connection is not available.");

            while (true)
            {
                var completedTask = await Task.WhenAny(_host.Termination, connection.Completion, _completionSource.Task).ConfigureAwait(false);
                if (completedTask == _completionSource.Task)
                {
                    return await _completionSource.Task.ConfigureAwait(false);
                }

                if (_stopCts.IsCancellationRequested)
                {
                    return new BrowserPageSessionResult(BrowserPageSessionCompletionKind.Stopped, Error: null);
                }

                if (completedTask == _host.Termination)
                {
                    var error = new InvalidOperationException($"Safari WebDriver process exited before the tracked browser session '{_sessionId}' completed.");
                    return new BrowserPageSessionResult(BrowserPageSessionCompletionKind.BrowserExited, error);
                }

                try
                {
                    await connection.Completion.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _connectionDiagnostics.LogConnectionLost(ex);
                    return new BrowserPageSessionResult(BrowserPageSessionCompletionKind.ConnectionLost, ex);
                }

                return new BrowserPageSessionResult(
                    BrowserPageSessionCompletionKind.ConnectionLost,
                    new InvalidOperationException("Safari WebDriver BiDi connection closed before the tracked browser session completed."));
            }
        }
        finally
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask HandleEventAsync(BrowserLogsBidiProtocolEvent protocolEvent)
    {
        if (!string.Equals(protocolEvent.Context, _targetId, StringComparison.Ordinal))
        {
            return;
        }

        if (protocolEvent is BrowserLogsBidiBrowsingContextDestroyedEvent)
        {
            // browsingContext.contextDestroyed is Safari telling us the page/tab we created is gone; that is normal
            // completion, unlike an unexpected safaridriver exit or BiDi websocket close.
            _completionSource.TrySetResult(new BrowserPageSessionResult(BrowserPageSessionCompletionKind.PageClosed, Error: null));
            return;
        }

        if (protocolEvent is BrowserLogsBidiNetworkResponseCompletedEvent responseCompletedEvent)
        {
            // BiDi responseCompleted is the terminal successful network event. BrowserLogs keeps response details
            // and completion as separate normalized events so the existing logger can emit one final resource log line
            // with status and duration.
            foreach (var diagnosticEvent in BrowserLogsBidiEventMapper.MapResponseCompleted(responseCompletedEvent))
            {
                await _eventHandler(diagnosticEvent).ConfigureAwait(false);
            }

            return;
        }

        if (BrowserLogsBidiEventMapper.TryMap(protocolEvent) is { } mappedEvent)
        {
            await _eventHandler(mappedEvent).ConfigureAwait(false);
        }
    }

    private async Task DisposeConnectionAsync()
    {
        await _connectionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var connection = _connection;
            _connection = null;

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }
}
