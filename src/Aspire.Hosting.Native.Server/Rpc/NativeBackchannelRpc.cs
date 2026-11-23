// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Runtime;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Shares header framing, cancellation and StreamJsonRpc enumeration between backchannels.</summary>
internal sealed class NativeBackchannelRpc(NativeServerOptions options, ILogger logger, INativeRpcPeerFactory peers) : INativeBackchannelRpc
{
    public NativeBackchannelRpc(NativeServerOptions options, ILogger logger)
        : this(options, logger, new NativeRpcPeerFactory(options, logger))
    {
    }

    public async Task ServeAsync(Stream stream,
        Func<NativeCliRequest, CancellationToken, Task<NativeBackchannelReply>> dispatch, CancellationToken cancellationToken)
    {
        using var firstRequest = new TimeProviderCancellation(options.BackchannelFirstRequestTimeout, options.TimeProvider, cancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(firstRequest.Token);
        using var peer = peers.Create(stream);
        using var disconnected = peer.Closed.Register(lifetime.Cancel);
        var requests = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var cursors = new Dictionary<long, Cursor>();
        var gate = new Lock();
        long nextToken = 0;
        try
        {
            await peer.RunAsync(async envelope =>
            {
                var request = NativeBackchannelProtocol.ParseRequest(envelope);
                // Bound silent and partial-frame clients without imposing an idle
                // deadline on legitimate long-lived log/resource watches.
                firstRequest.StopTimeout();
                if (request.Method == "$/cancelRequest")
                {
                    var id = NativeBackchannelProtocol.ReadCancellationId(request);
                    lock (gate)
                    {
                        if (requests.TryGetValue(id, out var pending))
                        {
                            pending.Cancel();
                        }
                    }
                    return null;
                }
                var key = request.Id?.GetRawText();
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (gate)
                {
                    if (requests.Count >= options.MaximumConcurrentRequests || key is not null && requests.ContainsKey(key))
                    {
                        cancellation.Dispose();
                        throw new InvalidDataException("The request limit was reached or an ID was reused.");
                    }
                    if (key is not null)
                    {
                        requests.Add(key, cancellation);
                    }
                }
                await RespondAsync(request, key, cancellation).ConfigureAwait(false);
                return null;
            }, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or JsonException or RpcFault)
        {
            logger.LogWarning("Backchannel disconnected: {ErrorType}.", exception.GetType().Name);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            foreach (var cursor in cursors.Values)
            {
                await cursor.DisposeAsync().ConfigureAwait(false);
            }
        }

        async Task RespondAsync(NativeCliRequest request, string? key, CancellationTokenSource cancellation)
        {
            NativeBackchannelReply? reply = null;
            NativeCliError? error = null;
            var result = NativeBackchannelProtocol.NullResult;
            try
            {
                try
                {
                    if (request.Method is "$/enumerator/next" or "$/enumerator/abort")
                    {
                        var token = NativeBackchannelProtocol.ReadEnumeratorToken(request);
                        Cursor cursor;
                        lock (gate)
                        {
                            if (!cursors.TryGetValue(token, out cursor!))
                            {
                                throw new InvalidOperationException("The stream has ended.");
                            }
                            if (request.Method == "$/enumerator/abort")
                            {
                                cursors.Remove(token);
                            }
                        }
                        if (request.Method == "$/enumerator/abort")
                        {
                            await cursor.DisposeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            var next = await cursor.NextAsync(cancellation.Token).ConfigureAwait(false);
                            if (next.Finished)
                            {
                                lock (gate)
                                {
                                    cursors.Remove(token);
                                }
                                await cursor.DisposeAsync().ConfigureAwait(false);
                            }
                            result = NativeBackchannelProtocol.EnumeratorResult(next);
                        }
                    }
                    else
                    {
                        reply = await dispatch(request, cancellation.Token).ConfigureAwait(false);
                        result = reply.Result;
                        if (reply.Stream is not null)
                        {
                            lock (gate)
                            {
                                if (cursors.Count >= options.MaximumCliStreams)
                                {
                                    throw new InvalidOperationException("The stream limit was reached.");
                                }
                                var token = checked(++nextToken);
                                cursors.Add(token, new Cursor(reply.Stream, lifetime.Token));
                                result = NativeBackchannelProtocol.StreamDescriptor(token);
                            }
                        }
                    }
                }
                catch (MissingMethodException)
                {
                    error = new(-32601, "The server does not implement this backchannel method.");
                }
                catch (Exception exception) when (exception is ArgumentException or RpcFault or JsonException)
                {
                    error = new(-32602, "The backchannel arguments are invalid.");
                }
                catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
                {
                    error = new(-32000, exception.Message);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested || lifetime.IsCancellationRequested)
                {
                    error = new(-32800, "The backchannel request was cancelled.");
                }
                catch (Exception exception)
                {
                    // Dispatch and iterator failures are request failures, not transport
                    // failures. Log only classification: controller exceptions can carry secrets.
                    using var operation = new NativeDiagnostics.Operation("backchannel.request", null, null, options.TimeProvider)
                    {
                        ErrorType = exception.GetType().FullName
                    };
                    error = new(-32603, "The backchannel request failed internally.");
                }
                if (request.Id is null)
                {
                    // Notifications have no response, but must still perform their side effects.
                    if (error is null)
                    {
                        reply?.AfterResponse?.Invoke();
                    }
                    return;
                }
                if (lifetime.IsCancellationRequested)
                {
                    return;
                }
                await peer.SendAsync(NativeBackchannelProtocol.EncodeResponse(request.Id.Value, result, error), lifetime.Token).ConfigureAwait(false);
                // A stop response must be flushed before cancellation closes either backchannel.
                if (error is null)
                {
                    reply?.AfterResponse?.Invoke();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                // Handle transport failures inside each response, including responses
                // still being drained after the reader has observed a disconnect.
                logger.LogWarning("Backchannel response failed: {ErrorType}.", exception.GetType().Name);
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    if (key is not null)
                    {
                        requests.Remove(key);
                    }
                }
                cancellation.Dispose();
            }
        }
    }

    private sealed class Cursor : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly IAsyncEnumerator<JsonElement> _enumerator;
        private readonly SemaphoreSlim _gate = new(1);
        private bool _finished;
        private int _disposed;

        public Cursor(IAsyncEnumerable<JsonElement> source, CancellationToken cancellationToken)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _enumerator = source.GetAsyncEnumerator(_lifetime.Token);
        }

        public async Task<NativeCliEnumeratorResult> NextAsync(CancellationToken cancellationToken)
        {
            if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The stream already has an outstanding next request.");
            }
            try
            {
                using var registration = cancellationToken.Register(_lifetime.Cancel);
                if (!_finished && await _enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    // StreamJsonRpc IAsyncEnumerable: {"token":1}, next [1] -> {"values":[...],"finished":false}.
                    // https://github.com/microsoft/vs-streamjsonrpc/blob/main/src/StreamJsonRpc/Reflection/MessageFormatterEnumerableTracker.cs
                    return new([_enumerator.Current], false);
                }
                _finished = true;

                return new([], true);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _enumerator.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
