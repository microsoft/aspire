// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Callback invoker that uses JSON-RPC to invoke callbacks on a remote client.
/// </summary>
internal sealed class JsonRpcCallbackInvoker : ICallbackInvoker
{
    private static readonly TimeSpan s_callbackTimeout = TimeSpan.FromSeconds(60);

    private JsonRpc? _clientRpc;

    /// <summary>
    /// Sets the JSON-RPC connection to use for invoking callbacks.
    /// </summary>
    /// <param name="clientRpc">The JSON-RPC connection.</param>
    public void SetConnection(JsonRpc clientRpc)
    {
        _clientRpc = clientRpc;
    }

    /// <inheritdoc />
    public bool IsConnected => _clientRpc != null;

    /// <inheritdoc />
    public async Task<TResult> InvokeAsync<TResult>(string callbackId, JsonNode? args, CancellationToken cancellationToken = default)
    {
        if (_clientRpc == null)
        {
            throw new InvalidOperationException("No client connection available for callback invocation");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(s_callbackTimeout);

        try
        {
            return await _clientRpc.InvokeWithCancellationAsync<TResult>(
                "invokeCallback",
                [callbackId, args],
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Callback '{callbackId}' timed out after {s_callbackTimeout.TotalSeconds}s");
        }
    }

    /// <inheritdoc />
    public async Task InvokeAsync(string callbackId, JsonNode? args, CancellationToken cancellationToken = default)
    {
        await InvokeAsync<object?>(callbackId, args, cancellationToken).ConfigureAwait(false);
    }
}
