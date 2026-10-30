// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Callback invoker that uses JSON-RPC to invoke callbacks on a remote client.
/// </summary>
internal sealed class JsonRpcCallbackInvoker : ICallbackInvoker, IDisposable, IAsyncDisposable
{
    private readonly TimeSpan _callbackTimeout = IntegrationHostConfiguration.Default.CallbackTimeout;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    private JsonRpc? _clientRpc;
    private readonly object _callbackGate = new();
    private bool _acceptingCallbacks = true;
    private bool _hasCallbacks;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<JsonRpcCallbackInvoker> _logger;
    private readonly bool _retireOnTimeout;
    private Task _retirement = Task.CompletedTask;
    private bool _disposed;

    public JsonRpcCallbackInvoker(ILogger<JsonRpcCallbackInvoker> logger)
    {
        _logger = logger;
        LifetimeToken = _lifetime.Token;
    }

    public JsonRpcCallbackInvoker(ILogger<JsonRpcCallbackInvoker> logger, IntegrationHostConfiguration configuration, TimeProvider timeProvider)
        : this(logger)
    {
        _retireOnTimeout = configuration.Enabled;
        _callbackTimeout = configuration.CallbackTimeout;
        _timeProvider = timeProvider;
    }

    internal CancellationToken LifetimeToken { get; }

    internal void RegisterCallback()
    {
        lock (_callbackGate)
        {
            ObjectDisposedException.ThrowIf(!_acceptingCallbacks, this);
            _hasCallbacks = true;
        }
    }

    internal bool StopAcceptingCallbacks()
    {
        lock (_callbackGate)
        {
            // Serialize retirement with proxy creation. A late RPC must not attach
            // a callback from a dead host after its supervisor decides it can recover.
            if (_acceptingCallbacks)
            {
                _acceptingCallbacks = false;
                // Cancellation callbacks can retire other connections. Do not execute
                // them inline while holding the callback or registry admission gates.
                _retirement = _lifetime.CancelAsync();
            }
            return _hasCallbacks;
        }
    }

    /// <summary>
    /// Sets the JSON-RPC connection to use for invoking callbacks.
    /// </summary>
    /// <param name="clientRpc">The JSON-RPC connection.</param>
    public void SetConnection(JsonRpc clientRpc)
    {
        _clientRpc = clientRpc;
        clientRpc.Disconnected += (_, _) => StopAcceptingCallbacks();
    }

    /// <inheritdoc />
    public bool IsConnected => _clientRpc is not null && !_clientRpc.Completion.IsCompleted;

    /// <inheritdoc />
    public Task<TResult> InvokeAsync<TResult>(string callbackId, JsonNode? args, CancellationToken cancellationToken = default)
        => InvokeAsync<TResult>(callbackId, args, cancellationToken, _callbackTimeout);

    internal async Task<TResult> InvokeAsync<TResult>(string callbackId, JsonNode? args, CancellationToken cancellationToken, TimeSpan timeout)
    {
        if (_clientRpc is null)
        {
            throw new InvalidOperationException("No client connection available for callback invocation");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, LifetimeToken);
        var started = _timeProvider.GetTimestamp();
        _logger.LogDebug("Callback {CallbackId} started on connection {Host}, timeout {Timeout}.",
            callbackId, _clientRpc.GetHashCode(), timeout);

        try
        {
            return await _clientRpc.InvokeWithCancellationAsync<TResult>(
                "invokeCallback",
                [callbackId, args],
                cts.Token).WaitAsync(timeout, _timeProvider, cts.Token).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            cts.Cancel();
            if (!_retireOnTimeout)
            {
                _logger.LogError(ex, "Callback {CallbackId} timed out on connection {Host} after {Elapsed}, timeout {Timeout}.",
                    callbackId, _clientRpc.GetHashCode(), _timeProvider.GetElapsedTime(started), timeout);
                throw new TimeoutException($"Callback '{callbackId}' timed out after {timeout.TotalSeconds}s.", ex);
            }

            // A stalled integration callback can outlive its invocation and mutate shared
            // handles. Retire its owner before admitting replacement integration hosts.
            StopAcceptingCallbacks();
            _clientRpc.Dispose();
            _logger.LogError(ex, "Callback {CallbackId} stalled on connection {Host} after {Elapsed}, timeout {Timeout}. Retired its owner connection.",
                callbackId, _clientRpc.GetHashCode(), _timeProvider.GetElapsedTime(started), timeout);
            throw new TimeoutException($"Callback '{callbackId}' timed out after {timeout.TotalSeconds}s; its owner connection was retired.", ex);
        }
        finally
        {
            _logger.LogDebug("Callback {CallbackId} finished on connection {Host} after {Elapsed}.",
                callbackId, _clientRpc.GetHashCode(), _timeProvider.GetElapsedTime(started));
        }
    }

    /// <inheritdoc />
    public async Task InvokeAsync(string callbackId, JsonNode? args, CancellationToken cancellationToken = default)
    {
        await InvokeAsync<object?>(callbackId, args, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        StopAcceptingCallbacks();
        lock (_callbackGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        try
        {
            await _retirement.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Callback owner cancellation failed while disposing connection {Host}.", _clientRpc?.GetHashCode());
            throw;
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
