// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class BrowserTargetSession : IBrowserTargetSession
{
    private static readonly TimeSpan s_connectionRecoveryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_connectionRecoveryTimeout = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource<BrowserTargetSessionResult> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<BrowserLogsProtocolEvent, ValueTask> _eventHandler;
    private readonly IBrowserHost _host;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly bool _reuseInitialBlankTarget;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TimeProvider _timeProvider;
    private readonly Uri _url;

    private ChromeDevToolsConnection? _connection;
    private Task<BrowserTargetSessionResult>? _monitorTask;
    private int _disposed;
    private string? _targetId;
    private string? _targetSessionId;

    private BrowserTargetSession(
        IBrowserHost host,
        string sessionId,
        Uri url,
        Func<BrowserLogsProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        bool reuseInitialBlankTarget)
    {
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

    public static async Task<BrowserTargetSession> StartAsync(
        IBrowserHost host,
        string sessionId,
        Uri url,
        Func<BrowserLogsProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        bool reuseInitialBlankTarget,
        CancellationToken cancellationToken)
    {
        var targetSession = new BrowserTargetSession(host, sessionId, url, eventHandler, logger, timeProvider, reuseInitialBlankTarget);
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
                using var closeTargetCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
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

        _connection = await ChromeDevToolsConnection.ConnectAsync(_host.DebugEndpoint, HandleEventAsync, _logger, cancellationToken).ConfigureAwait(false);
        await _connection.EnableTargetDiscoveryAsync(cancellationToken).ConfigureAwait(false);

        if (createTarget)
        {
            _targetId = await CreateTargetAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_targetId is null)
        {
            throw new InvalidOperationException("Tracked browser target id is not available.");
        }

        var attachToTargetResult = await _connection.AttachToTargetAsync(_targetId, cancellationToken).ConfigureAwait(false);
        _targetSessionId = attachToTargetResult.SessionId
            ?? throw new InvalidOperationException("Browser target attachment did not return a session id.");

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
                    return new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.BrowserExited, Error: null);
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

        var reconnectDeadline = _timeProvider.GetUtcNow() + s_connectionRecoveryTimeout;
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
            _logger.LogDebug(lastError, "Timed out reconnecting tracked browser target session '{SessionId}'.", _sessionId);
        }

        return false;
    }

    private async ValueTask HandleEventAsync(BrowserLogsProtocolEvent protocolEvent)
    {
        switch (protocolEvent)
        {
            case BrowserLogsTargetDestroyedEvent targetDestroyed when string.Equals(targetDestroyed.TargetId, _targetId, StringComparison.Ordinal):
                _completionSource.TrySetResult(new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.TargetClosed, Error: null));
                return;
            case BrowserLogsTargetCrashedEvent targetCrashed when string.Equals(targetCrashed.TargetId, _targetId, StringComparison.Ordinal):
                _completionSource.TrySetResult(new BrowserTargetSessionResult(
                    BrowserTargetSessionCompletionKind.TargetCrashed,
                    new InvalidOperationException($"Tracked browser target crashed with status '{targetCrashed.Parameters.Status}' and error code '{targetCrashed.Parameters.ErrorCode}'.")));
                return;
            case BrowserLogsDetachedFromTargetEvent detached when
                string.Equals(detached.DetachedSessionId, _targetSessionId, StringComparison.Ordinal) ||
                string.Equals(detached.TargetId, _targetId, StringComparison.Ordinal):
                _completionSource.TrySetResult(new BrowserTargetSessionResult(BrowserTargetSessionCompletionKind.TargetClosed, Error: null));
                return;
            case BrowserLogsInspectorDetachedEvent inspectorDetached when string.Equals(inspectorDetached.SessionId, _targetSessionId, StringComparison.Ordinal):
                var completionKind = string.Equals(inspectorDetached.Reason, "target_closed", StringComparison.OrdinalIgnoreCase)
                    ? BrowserTargetSessionCompletionKind.TargetClosed
                    : BrowserTargetSessionCompletionKind.ConnectionLost;
                _completionSource.TrySetResult(new BrowserTargetSessionResult(
                    completionKind,
                    completionKind == BrowserTargetSessionCompletionKind.ConnectionLost
                        ? new InvalidOperationException($"Tracked browser inspector detached: {inspectorDetached.Reason ?? "unknown reason"}.")
                        : null));
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
