// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Owns one browser target (tab) for one browser-log session. The host may be shared by many sessions, but each
// BrowserTargetSession has its own browser CDP connection, attached target session id, instrumentation setup,
// lifecycle monitoring, and reconnection loop.
internal sealed class BrowserTargetSession : IBrowserTargetSession
{
    private static readonly TimeSpan s_connectionRecoveryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_connectionRecoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_closeTargetTimeout = TimeSpan.FromSeconds(3);

    private readonly TaskCompletionSource<BrowserTargetSessionResult> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BrowserConnectionDiagnosticsLogger _connectionDiagnostics;
    private readonly Func<BrowserLogsCdpProtocolEvent, ValueTask> _eventHandler;
    private readonly IBrowserHost _host;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly bool _reuseInitialBlankTarget;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TimeProvider _timeProvider;
    private readonly Uri _url;

    private BrowserLogsCdpConnection? _connection;
    private Task<BrowserTargetSessionResult>? _monitorTask;
    private int _disposed;
    private string? _targetId;
    private string? _targetSessionId;

    private BrowserTargetSession(
        IBrowserHost host,
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        bool reuseInitialBlankTarget)
    {
        _connectionDiagnostics = connectionDiagnostics;
        _eventHandler = eventHandler;
        _host = host;
        _logger = logger;
        _reuseInitialBlankTarget = reuseInitialBlankTarget;
        _sessionId = sessionId;
        _timeProvider = timeProvider;
        _url = url;
    }

    public string TargetId => _targetId ?? throw new InvalidOperationException("Browser target id is not available before the target session starts.");

    public string TargetSessionId => _targetSessionId ?? throw new InvalidOperationException("Browser target session id is not available before the target session starts.");

    public Task<BrowserTargetSessionResult> Completion => _monitorTask ?? throw new InvalidOperationException("Browser target session has not started.");

    internal static BrowserTargetSessionResult? TryGetTargetCompletion(BrowserLogsCdpProtocolEvent protocolEvent, string? targetId, string? targetSessionId)
    {
        return protocolEvent switch
        {
            BrowserLogsTargetDestroyedEvent targetDestroyed when string.Equals(targetDestroyed.TargetId, targetId, StringComparison.Ordinal) =>
                new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.TargetClosed, Error: null),

            BrowserLogsTargetCrashedEvent targetCrashed when string.Equals(targetCrashed.TargetId, targetId, StringComparison.Ordinal) =>
                new BrowserTargetSessionResult(
                    BrowserTargetSessionCompletionKind.TargetCrashed,
                    new InvalidOperationException($"Tracked browser target crashed with status '{targetCrashed.Parameters.Status}' and error code '{targetCrashed.Parameters.ErrorCode}'.")),

            BrowserLogsDetachedFromTargetEvent detached when
                string.Equals(detached.DetachedSessionId, targetSessionId, StringComparison.Ordinal) ||
                string.Equals(detached.TargetId, targetId, StringComparison.Ordinal) =>
                new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.TargetClosed, Error: null),

            BrowserLogsInspectorDetachedEvent inspectorDetached when string.Equals(inspectorDetached.SessionId, targetSessionId, StringComparison.Ordinal) =>
                string.Equals(inspectorDetached.Reason, "target_closed", StringComparison.OrdinalIgnoreCase)
                    ? new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.TargetClosed, Error: null)
                    : new BrowserTargetSessionResult(
                        BrowserTargetSessionCompletionKind.ConnectionLost,
                        new InvalidOperationException($"Tracked browser inspector detached: {inspectorDetached.Reason ?? "unknown reason"}.")),

            _ => null
        };
    }

    public static async Task<BrowserTargetSession> StartAsync(
        IBrowserHost host,
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        bool reuseInitialBlankTarget,
        CancellationToken cancellationToken)
    {
        var targetSession = new BrowserTargetSession(host, sessionId, url, connectionDiagnostics, eventHandler, logger, timeProvider, reuseInitialBlankTarget);
        try
        {
            await targetSession.ConnectAsync(createTarget: true, cancellationToken).ConfigureAwait(false);
            targetSession._monitorTask = targetSession.MonitorAsync();
            return targetSession;
        }
        catch
        {
            await targetSession.DisposeAsync().ConfigureAwait(false);
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

        var connection = _connection;
        if (connection is not null && _targetId is not null)
        {
            try
            {
                using var closeTargetCts = new CancellationTokenSource(s_closeTargetTimeout);
                await connection.CloseTargetAsync(_targetId, closeTargetCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to close tracked browser target '{TargetId}' for session '{SessionId}'.", _targetId, _sessionId);
            }
        }

        _completionSource.TrySetResult(new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.Stopped, Error: null));

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
    }

    private async Task ConnectAsync(bool createTarget, CancellationToken cancellationToken)
    {
        await DisposeConnectionAsync().ConfigureAwait(false);

        _connection = await BrowserLogsCdpConnection.ConnectAsync(_host.DebugEndpoint, HandleEventAsync, _logger, cancellationToken).ConfigureAwait(false);
        // Target discovery must be re-enabled for every browser-level connection, including reconnects. The
        // subscription is attached to this websocket, not to the browser process, and it is what makes Chromium emit
        // targetDestroyed/targetCrashed/detachedFromTarget events that tell us whether the tracked tab is gone.
        await _connection.EnableTargetDiscoveryAsync(cancellationToken).ConfigureAwait(false);

        if (createTarget)
        {
            _targetId = await CreateTargetAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_targetId is null)
        {
            throw new InvalidOperationException("Tracked browser target id is not available.");
        }

        // Reconnects reuse the existing target id. A transient websocket drop does not necessarily close the browser
        // tab, so recovering should reattach to the same page instead of opening a duplicate tab in the user's browser.
        var attachToTargetResult = await _connection.AttachToTargetAsync(_targetId, cancellationToken).ConfigureAwait(false);
        _targetSessionId = attachToTargetResult.SessionId
            ?? throw new InvalidOperationException("Browser target attachment did not return a session id.");

        // Runtime/Log/Page/Network subscriptions are scoped to the attached target session. They have to be re-enabled
        // after every attach, including reconnects, or the page keeps running with no events flowing back to resource logs.
        await _connection.EnablePageInstrumentationAsync(_targetSessionId, cancellationToken).ConfigureAwait(false);

        if (createTarget)
        {
            await _connection.NavigateAsync(_targetSessionId, _url, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> CreateTargetAsync(CancellationToken cancellationToken)
    {
        if (_reuseInitialBlankTarget && _connection is not null)
        {
            var targets = await _connection.GetTargetsAsync(cancellationToken).ConfigureAwait(false);
            if (BrowserLogsRunningSession.TrySelectTrackedTargetId(targets.TargetInfos) is { } targetId)
            {
                return targetId;
            }
        }

        // If no safe startup page target is available, create a fresh page target so we do not navigate an unrelated
        // page in a real browser window.
        var createTargetResult = await _connection!.CreateTargetAsync(cancellationToken).ConfigureAwait(false);
        return createTargetResult.TargetId
            ?? throw new InvalidOperationException("Browser target creation did not return a target id.");
    }

    private async Task<BrowserTargetSessionResult> MonitorAsync()
    {
        try
        {
            while (true)
            {
                var connection = _connection ?? throw new InvalidOperationException("Tracked browser debug connection is not available.");
                var completedTask = await Task.WhenAny(_host.Termination, connection.Completion, _completionSource.Task).ConfigureAwait(false);

                if (completedTask == _completionSource.Task)
                {
                    return await _completionSource.Task.ConfigureAwait(false);
                }

                if (completedTask == _host.Termination)
                {
                    var error = new InvalidOperationException($"Tracked browser host '{_host.Identity}' ended before the target session completed.");
                    _connectionDiagnostics.LogHostTerminated(error);
                    return new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.BrowserExited, error);
                }

                Exception? connectionError = null;
                try
                {
                    await connection.Completion.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    connectionError = ex;
                }

                if (_stopCts.IsCancellationRequested)
                {
                    return new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.Stopped, Error: null);
                }

                connectionError ??= new InvalidOperationException("The tracked browser debug connection closed without reporting a reason.");
                _connectionDiagnostics.LogConnectionLost(connectionError);
                if (await TryReconnectAsync(connectionError).ConfigureAwait(false))
                {
                    continue;
                }

                return new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.ConnectionLost, connectionError);
            }
        }
        finally
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> TryReconnectAsync(Exception connectionError)
    {
        await DisposeConnectionAsync().ConfigureAwait(false);

        // In a real browser the CDP websocket can disappear briefly while the tab keeps running (for example during
        // browser hiccups or network stack resets). Give it a short recovery window so logs continue without opening
        // another tab, but fail fast enough that the dashboard does not look healthy after the target is truly gone.
        var reconnectDeadline = _timeProvider.GetUtcNow() + s_connectionRecoveryTimeout;
        var reconnectAttempt = 0;
        Exception? lastError = connectionError;

        while (!_stopCts.IsCancellationRequested && _timeProvider.GetUtcNow() < reconnectDeadline)
        {
            if (_host.Termination.IsCompleted)
            {
                return false;
            }

            try
            {
                await ConnectAsync(createTarget: false, _stopCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                lastError = ex;
                reconnectAttempt++;
                _connectionDiagnostics.LogReconnectAttemptFailed(reconnectAttempt, ex);
                await DisposeConnectionAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(s_connectionRecoveryDelay, _stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
            {
                return false;
            }
        }

        if (lastError is not null)
        {
            _connectionDiagnostics.LogReconnectFailed(lastError);
            _logger.LogDebug(lastError, "Timed out reconnecting tracked browser target session '{SessionId}'.", _sessionId);
        }

        return false;
    }

    private async ValueTask HandleEventAsync(BrowserLogsCdpProtocolEvent protocolEvent)
    {
        // Browser-level lifecycle events often are not stamped with the attached page session id, so check completion
        // first. Only after that should ordinary Runtime/Log/Network/Page events be filtered to this target session.
        if (TryGetTargetCompletion(protocolEvent, _targetId, _targetSessionId) is { } targetCompletion)
        {
            _completionSource.TrySetResult(targetCompletion);
            return;
        }

        if (string.Equals(protocolEvent.SessionId, _targetSessionId, StringComparison.Ordinal))
        {
            await _eventHandler(protocolEvent).ConfigureAwait(false);
        }
    }

    private async Task DisposeConnectionAsync()
    {
        var connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
