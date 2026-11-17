// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Server;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Native.Rpc;

internal sealed class NativeRpcPeerFactory(NativeServerOptions options, ILogger logger) : INativeRpcPeerFactory
{
    public INativeRpcPeer Create(Stream stream) => new NativeRpcPeer(stream, options, logger);
}

/// <summary>Multiplexes bounded requests and responses over one caller-owned duplex stream.</summary>
internal sealed class NativeRpcPeer(Stream stream, NativeServerOptions options, ILogger logger) : INativeRpcPeer
{
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _writes = new(1);
    private readonly SemaphoreSlim _outbound = new(options.MaximumConcurrentRequests);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new(StringComparer.Ordinal);
    private long _nextId;
    private int _running;
    public CancellationToken Closed => _closed.Token;

    public async Task<JsonNode?> RequestAsync(string method, JsonArray arguments, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = $"native:{Interlocked.Increment(ref _nextId)}";
        if (!_outbound.Wait(0, cancellationToken))
        {
            throw RpcFault.RpcBusy("The peer request limit was reached.");
        }
        _pending.TryAdd(id, completion);
        try
        {
            using var deadline = new CancellationTokenSource(options.ProviderInvocationTimeout, options.TimeProvider);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token, deadline.Token);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = arguments.DeepClone()
            }, NativeRpcMessageJsonContext.Default.JsonNode);
            if (payload.Length > options.MaximumRequestBytes)
            {
                throw RpcFault.RequestTooLarge("The request exceeds the size limit.");
            }
            try
            {
                // The deadline covers both a blocked write and the provider reply.
                await WriteAsync(payload, lifetime.Token).ConfigureAwait(false);
                return await completion.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_closed.IsCancellationRequested)
            {
                // Stop accepting replies, not replaying a call whose side effects may
                // already have occurred. The provider registry revokes this peer.
                throw RpcFault.ProviderTimeout("The provider invocation deadline was reached.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _outbound.Release();
        }
    }

    public async Task RunAsync(Func<JsonElement, Task<byte[]?>> dispatch, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("The RPC peer is already running.");
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var framing = new NativeRpcFraming(stream, options.MaximumRequestBytes);
        var requests = new List<Task>();
        try
        {
            while (await framing.ReadAsync(lifetime.Token).ConfigureAwait(false) is { } payload)
            {
                JsonDocument? document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
                try
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException("An RPC envelope must be an object.");
                    }
                    NativeRpcProtocol.ValidateUniqueProperties(document.RootElement);
                    // JSON-RPC is bidirectional: {"id":"native:1","result":...} is a
                    // reply, not an inbound call. Peer-supplied IDs never select authority.
                    if (!document.RootElement.TryGetProperty("method", out _))
                    {
                        AcceptResponse(document.RootElement);
                        continue;
                    }
                    for (var index = requests.Count - 1; index >= 0; index--)
                    {
                        if (requests[index].IsCompleted)
                        {
                            await requests[index].ConfigureAwait(false);
                            requests.RemoveAt(index);
                        }
                    }
                    if (requests.Count >= options.MaximumConcurrentRequests)
                    {
                        throw new InvalidDataException("The connection response backlog exceeds the limit.");
                    }
                    // Dispatch borrows the parsed envelope across awaits. Transfer
                    // disposal to the response task rather than cloning or reparsing it.
                    requests.Add(RespondAsync(document));
                    document = null;
                }
                finally
                {
                    document?.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or JsonException or RpcFault)
        {
            logger.LogWarning("Native connection closed: {ErrorType}.", exception.GetType().Name);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            Dispose();
            await Task.WhenAll(requests).ConfigureAwait(false);
        }

        async Task RespondAsync(JsonDocument document)
        {
            using var envelope = document;
            try
            {
                var response = await dispatch(envelope.RootElement).ConfigureAwait(false);
                if (response is null || lifetime.IsCancellationRequested)
                {
                    return;
                }
                await WriteAsync(response, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                Dispose();
            }
        }
    }

    private void AcceptResponse(JsonElement response)
    {
        var envelope = NativeRpcProtocol.ParseResponse(response);
        // Late replies after local cancellation are ignored, never treated as calls.
        if (!_pending.TryRemove(envelope.Id, out var completion))
        {
            return;
        }
        if (envelope.Error is { } error)
        {
            try
            {
                NativeRpcProtocol.ValidateResponseError(error);
            }
            catch (InvalidDataException)
            {
                completion.TrySetCanceled();
                throw;
            }
            completion.TrySetException(RpcFault.ProviderFailed("The provider rejected the invocation."));
            // Observe a fault if the initiating caller concurrently stopped waiting.
            _ = completion.Task.Exception;
        }
        else
        {
            completion.TrySetResult(NativeRpcProtocol.ToNode(envelope.Result));
        }
    }

    public Task SendAsync(byte[] payload, CancellationToken cancellationToken) => WriteAsync(payload, cancellationToken);

    private async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await NativeRpcFraming.WriteAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    public void Dispose()
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }
        _closed.Cancel();
        foreach (var (id, completion) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                completion.TrySetCanceled(_closed.Token);
            }
        }
    }
}
