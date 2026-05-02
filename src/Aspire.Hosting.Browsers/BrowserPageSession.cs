// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Factory for browser-level CDP connections.
internal delegate Task<IBrowserLogsCdpConnection> BrowserLogsCdpConnectionFactory(
    Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
    ILogger<BrowserLogsSessionManager> logger,
    CancellationToken cancellationToken);

// Owns one browser page/tab for one browser-log session. CDP calls pages "targets", but this layer intentionally
// models the user-visible page session. The host may be shared by many sessions, while each BrowserPageSession has
// its own browser CDP connection, attached target session id, instrumentation setup, lifecycle monitoring, and
// reconnection loop.
internal sealed class BrowserPageSession : IBrowserPageSession
{
    // Keep reconnects quick and local to transient CDP connection loss. A 200 ms cadence gives the browser a few chances to
    // recover within the 5 s window without making the dashboard look healthy after the page is truly gone. Target close
    // uses a shorter 3 s budget because disposal should not block AppHost shutdown on an unresponsive browser.
    private static readonly TimeSpan s_connectionRecoveryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_connectionRecoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_closeTargetTimeout = TimeSpan.FromSeconds(3);

    private readonly TaskCompletionSource<BrowserPageSessionResult> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BrowserConnectionDiagnosticsLogger _connectionDiagnostics;
    private readonly BrowserLogsCdpConnectionFactory _connectionFactory;
    private readonly Func<BrowserLogsCdpProtocolEvent, ValueTask> _eventHandler;
    private readonly IBrowserHost _host;
    // Serializes every operation that replaces, disposes, or sends a command through the current CDP connection.
    // Without this, screenshot capture can read a live connection reference while reconnect/dispose tears down that
    // same connection underneath it.
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly bool _reuseInitialBlankTarget;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TimeProvider _timeProvider;
    private readonly Uri _url;

    private IBrowserLogsCdpConnection? _connection;
    private Task<BrowserPageSessionResult>? _monitorTask;
    private int _disposed;
    private string? _targetId;
    private string? _targetSessionId;

    private BrowserPageSession(
        IBrowserHost host,
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        BrowserLogsCdpConnectionFactory connectionFactory,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        bool reuseInitialBlankTarget)
    {
        _connectionDiagnostics = connectionDiagnostics;
        _connectionFactory = connectionFactory;
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

    public Task<BrowserPageSessionResult> Completion => _monitorTask ?? throw new InvalidOperationException("Browser page session has not started.");

    public async Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var connection = _connection ?? throw new InvalidOperationException("Tracked browser debug connection is not available.");
            var targetSessionId = _targetSessionId ?? throw new InvalidOperationException("Browser target session id is not available before the target session starts.");

            // Keep the lock for the whole CDP command. Capturing only the fields under the lock is not enough because
            // BrowserPageSession.DisposeAsync and reconnect both dispose the connection object itself.
            using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
            return await connection.CaptureScreenshotAsync(targetSessionId, captureCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task NavigateAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var connection = _connection ?? throw new InvalidOperationException("Tracked browser debug connection is not available.");
            var targetSessionId = _targetSessionId ?? throw new InvalidOperationException("Browser target session id is not available before the target session starts.");

            using var navigateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
            await connection.NavigateAsync(targetSessionId, url, navigateCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<string> EvaluateJsonAsync(string expression, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var connection = _connection ?? throw new InvalidOperationException("Tracked browser debug connection is not available.");
            var targetSessionId = _targetSessionId ?? throw new InvalidOperationException("Browser target session id is not available before the target session starts.");

            using var evaluateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
            var result = await connection.EvaluateAsync(targetSessionId, expression, timeout, evaluateCts.Token).ConfigureAwait(false);

            return result.Result?.Value switch
            {
                BrowserLogsCdpProtocolStringValue stringValue => stringValue.Value,
                BrowserLogsCdpProtocolNullValue => "null",
                null => throw new InvalidOperationException("Tracked browser script evaluation returned no value."),
                _ => throw new InvalidOperationException("Tracked browser script evaluation did not return JSON text.")
            };
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    internal static BrowserPageSessionResult? TryGetPageCompletion(BrowserLogsCdpProtocolEvent protocolEvent, string? targetId, string? targetSessionId)
    {
        return protocolEvent switch
        {
            BrowserLogsTargetDestroyedEvent targetDestroyed when string.Equals(targetDestroyed.TargetId, targetId, StringComparison.Ordinal) =>
                new BrowserPageSessionResult(BrowserPageSessionCompletionKind.PageClosed, Error: null),

            BrowserLogsTargetCrashedEvent targetCrashed when string.Equals(targetCrashed.TargetId, targetId, StringComparison.Ordinal) =>
                new BrowserPageSessionResult(
                    BrowserPageSessionCompletionKind.PageCrashed,
                    new InvalidOperationException($"Tracked browser page crashed with status '{targetCrashed.Parameters.Status}' and error code '{targetCrashed.Parameters.ErrorCode}'.")),

            BrowserLogsDetachedFromTargetEvent detached when
                string.Equals(detached.DetachedSessionId, targetSessionId, StringComparison.Ordinal) ||
                string.Equals(detached.TargetId, targetId, StringComparison.Ordinal) =>
                new BrowserPageSessionResult(BrowserPageSessionCompletionKind.PageClosed, Error: null),

            BrowserLogsInspectorDetachedEvent inspectorDetached when string.Equals(inspectorDetached.SessionId, targetSessionId, StringComparison.Ordinal) =>
                string.Equals(inspectorDetached.Reason, "target_closed", StringComparison.OrdinalIgnoreCase)
                    ? new BrowserPageSessionResult(BrowserPageSessionCompletionKind.PageClosed, Error: null)
                    : new BrowserPageSessionResult(
                        BrowserPageSessionCompletionKind.ConnectionLost,
                        new InvalidOperationException($"Tracked browser inspector detached: {inspectorDetached.Reason ?? "unknown reason"}.")),

            _ => null
        };
    }

    public static async Task<BrowserPageSession> StartAsync(
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
        return await StartAsync(
            host,
            sessionId,
            url,
            connectionDiagnostics,
            host.CreateCdpConnectionAsync,
            eventHandler,
            logger,
            timeProvider,
            reuseInitialBlankTarget,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<BrowserPageSession> StartAsync(
        IBrowserHost host,
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        BrowserLogsCdpConnectionFactory connectionFactory,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        bool reuseInitialBlankTarget,
        CancellationToken cancellationToken)
    {
        var pageSession = new BrowserPageSession(host, sessionId, url, connectionDiagnostics, connectionFactory, eventHandler, logger, timeProvider, reuseInitialBlankTarget);
        try
        {
            await pageSession.ConnectAsync(createTarget: true, cancellationToken).ConfigureAwait(false);
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

        // Cancel first so any in-flight screenshot command holding _connectionLock is interrupted before disposal waits
        // for the lock. Once the lock is acquired, no new capture/reconnect can use the connection while the target is
        // being closed and the connection is being disposed.
        await _connectionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var connection = _connection;
            if (connection is not null && ReferenceEquals(connection, _connection) && _targetId is not null)
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

    private async Task ConnectAsync(bool createTarget, CancellationToken cancellationToken)
    {
        // ConnectAsync is used for startup and reconnect. It swaps the current CDP connection, target attachment, and target
        // session id as one critical section so command callers never observe a half-attached page session.
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DisposeConnectionCoreAsync().ConfigureAwait(false);

            _connection = await _connectionFactory(HandleEventAsync, _logger, cancellationToken).ConfigureAwait(false);
            // Target discovery must be re-enabled for every browser-level connection, including reconnects. The
            // subscription is attached to this connection, not to the browser process, and it is what makes Chromium emit
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

            // Reconnects reuse the existing target id. A transient CDP connection drop does not necessarily close the browser
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
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task<string> CreateTargetAsync(CancellationToken cancellationToken)
    {
        if (_reuseInitialBlankTarget && _connection is not null)
        {
            var targets = await _connection.GetTargetsAsync(cancellationToken).ConfigureAwait(false);
            if (TrySelectReusableStartupPageTargetId(targets.TargetInfos) is { } targetId)
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

    internal static string? TrySelectReusableStartupPageTargetId(IReadOnlyList<BrowserLogsTargetInfo>? targetInfos)
    {
        if (targetInfos is null)
        {
            return null;
        }

        // Only owned browser hosts ask to reuse a startup target. Owned launches append about:blank to the Chromium
        // command line so the first tracked page session can navigate that visible empty tab instead of creating a
        // second tab. Adopted hosts disable this path so Aspire never navigates an arbitrary tab in a user's browser.
        var preferredTarget = targetInfos.FirstOrDefault(static targetInfo =>
            string.Equals(targetInfo.Type, "page", StringComparison.Ordinal) &&
            targetInfo.Attached != true &&
            string.Equals(targetInfo.Url, "about:blank", StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(preferredTarget?.TargetId))
        {
            return preferredTarget.TargetId;
        }

        // Chromium can report another unattached startup page first, especially with restored profile state. Reusing
        // that page is still preferable to opening an extra tab because this helper is never used for adopted browsers.
        return targetInfos.FirstOrDefault(static targetInfo =>
            string.Equals(targetInfo.Type, "page", StringComparison.Ordinal) &&
            targetInfo.Attached != true &&
            !string.IsNullOrWhiteSpace(targetInfo.TargetId))
            ?.TargetId;
    }

    private async Task<BrowserPageSessionResult> MonitorAsync()
    {
        try
        {
            while (true)
            {
                // Don't hold _connectionLock while awaiting the connection completion task: reconnect and disposal need
                // the same lock. Instead snapshot the stable completion task under the lock, then await the snapshot.
                var connectionSnapshot = await GetConnectionSnapshotAsync().ConfigureAwait(false);
                var completedTask = await Task.WhenAny(_host.Termination, connectionSnapshot.Completion, _completionSource.Task).ConfigureAwait(false);

                if (completedTask == _completionSource.Task)
                {
                    return await _completionSource.Task.ConfigureAwait(false);
                }

                if (completedTask == _host.Termination)
                {
                    var error = new InvalidOperationException($"Tracked browser host '{_host.Identity}' ended before the page session completed.");
                    _connectionDiagnostics.LogHostTerminated(error);
                    return new BrowserPageSessionResult(BrowserPageSessionCompletionKind.BrowserExited, error);
                }

                Exception? connectionError = null;
                try
                {
                    await connectionSnapshot.Completion.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    connectionError = ex;
                }

                if (_stopCts.IsCancellationRequested)
                {
                    _logger.LogTrace("Stopping tracked browser page session '{SessionId}' after debug connection completed.", _sessionId);
                    return new BrowserPageSessionResult(BrowserPageSessionCompletionKind.Stopped, Error: null);
                }

                connectionError ??= new InvalidOperationException("The tracked browser debug connection closed without reporting a reason.");
                _connectionDiagnostics.LogConnectionLost(connectionError);
                if (await TryReconnectAsync(connectionError).ConfigureAwait(false))
                {
                    continue;
                }

                return new BrowserPageSessionResult(BrowserPageSessionCompletionKind.ConnectionLost, connectionError);
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

        // In a real browser the CDP connection can disappear briefly while the tab keeps running (for example during
        // browser hiccups or network stack resets). Give it a short recovery window so logs continue without opening
        // another tab, but fail fast enough that the dashboard does not look healthy after the target is truly gone.
        var reconnectDeadline = _timeProvider.GetUtcNow() + s_connectionRecoveryTimeout;
        var reconnectAttempt = 0;
        Exception? lastError = connectionError;

        while (!_stopCts.IsCancellationRequested && _timeProvider.GetUtcNow() < reconnectDeadline)
        {
            if (_host.Termination.IsCompleted)
            {
                _logger.LogTrace("Skipping tracked browser page session reconnect for '{SessionId}' because the browser host has terminated.", _sessionId);
                return false;
            }

            try
            {
                _logger.LogTrace("Attempting to reconnect tracked browser page session '{SessionId}' to target '{TargetId}'.", _sessionId, _targetId);
                await ConnectAsync(createTarget: false, _stopCts.Token).ConfigureAwait(false);
                _logger.LogTrace("Reconnected tracked browser page session '{SessionId}' to target '{TargetId}'.", _sessionId, _targetId);
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
            _logger.LogDebug(lastError, "Timed out reconnecting tracked browser page session '{SessionId}'.", _sessionId);
        }

        return false;
    }

    private async ValueTask HandleEventAsync(BrowserLogsCdpProtocolEvent protocolEvent)
    {
        // Browser-level lifecycle events often are not stamped with the attached page session id, so check completion
        // first. Only after that should ordinary Runtime/Log/Network/Page events be filtered to this target session.
        if (TryGetPageCompletion(protocolEvent, _targetId, _targetSessionId) is { } pageCompletion)
        {
            _completionSource.TrySetResult(pageCompletion);
            return;
        }

        if (string.Equals(protocolEvent.SessionId, _targetSessionId, StringComparison.Ordinal))
        {
            await _eventHandler(protocolEvent).ConfigureAwait(false);
        }
    }

    private async Task<ConnectionSnapshot> GetConnectionSnapshotAsync()
    {
        await _connectionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var connection = _connection ?? throw new InvalidOperationException("Tracked browser debug connection is not available.");
            return new ConnectionSnapshot(connection.Completion);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task DisposeConnectionAsync()
    {
        await _connectionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DisposeConnectionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task DisposeConnectionCoreAsync()
    {
        var connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record ConnectionSnapshot(Task Completion);
}
