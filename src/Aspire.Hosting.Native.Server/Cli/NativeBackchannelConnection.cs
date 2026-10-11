// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Cli;

/// <summary>Shares header framing, cancellation and StreamJsonRpc enumeration between backchannels.</summary>
internal static class NativeBackchannelConnection
{
    internal sealed record Reply(JsonElement Result, IAsyncEnumerable<JsonElement>? Stream = null, Action? AfterResponse = null);

    internal static Reply Value<T>(T value, JsonTypeInfo<T> typeInfo) => new(JsonSerializer.SerializeToElement(value, typeInfo));

    internal static Reply Streaming<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo) =>
        new(default, EncodeAsync(source, typeInfo));

    private static async IAsyncEnumerable<JsonElement> EncodeAsync<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var value in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return JsonSerializer.SerializeToElement(value, typeInfo);
        }
    }

    internal static async Task ServeAsync(Socket socket, NativeServerOptions options,
        Func<NativeCliRequest, CancellationToken, Task<Reply>> dispatch, CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writes = new SemaphoreSlim(1);
        var framing = new NativeRpcFraming(stream, options.MaximumRequestBytes);
        var requests = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var tasks = new List<Task>();
        var cursors = new Dictionary<long, Cursor>();
        var gate = new Lock();
        long nextToken = 0;
        try
        {
            while (await framing.ReadAsync(lifetime.Token).ConfigureAwait(false) is { } payload)
            {
                await Task.WhenAll(tasks.Where(task => task.IsCompleted)).ConfigureAwait(false);
                tasks.RemoveAll(task => task.IsCompletedSuccessfully);
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
                NativeRpcConnection.ValidateUniqueProperties(document.RootElement);
                var request = JsonSerializer.Deserialize(payload, NativeCliJsonContext.Default.NativeCliRequest)
                    ?? throw new InvalidDataException("Invalid backchannel request.");
                if (request.Jsonrpc != "2.0" ||
                    request.Id is { ValueKind: not (JsonValueKind.String or JsonValueKind.Number) })
                {
                    throw new InvalidDataException("Invalid backchannel envelope.");
                }
                if (request.Method == "$/cancelRequest")
                {
                    var cancel = request.Params?.Deserialize(NativeCliJsonContext.Default.NativeCliCancellation)
                        ?? throw new InvalidDataException("Cancellation requires a request ID.");
                    lock (gate)
                    {
                        if (requests.TryGetValue(cancel.Id.GetRawText(), out var pending))
                        {
                            pending.Cancel();
                        }
                    }
                    continue;
                }
                var key = request.Id?.GetRawText();
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (gate)
                {
                    if (tasks.Count >= options.MaximumConcurrentRequests || key is not null && requests.ContainsKey(key))
                    {
                        cancellation.Dispose();
                        throw new InvalidDataException("The request limit was reached or an ID was reused.");
                    }
                    if (key is not null)
                    {
                        requests.Add(key, cancellation);
                    }
                }
                tasks.Add(RespondAsync(request, key, cancellation));
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or JsonException or RpcFault)
        {
            Console.Error.WriteLine($"Backchannel disconnected: {exception.GetType().Name}.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var cursor in cursors.Values)
            {
                await cursor.DisposeAsync().ConfigureAwait(false);
            }
        }

        async Task RespondAsync(NativeCliRequest request, string? key, CancellationTokenSource cancellation)
        {
            Reply? reply = null;
            NativeCliError? error = null;
            var result = JsonSerializer.SerializeToElement((string?)null, NativeCliJsonContext.Default.String);
            try
            {
                try
                {
                    if (request.Method is "$/enumerator/next" or "$/enumerator/abort")
                    {
                        if (request.Params is not { ValueKind: JsonValueKind.Array } arguments ||
                            arguments.GetArrayLength() != 1 || !arguments[0].TryGetInt64(out var token))
                        {
                            throw new ArgumentException("An enumerator requires one integer token.");
                        }
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
                            result = JsonSerializer.SerializeToElement(await cursor.NextAsync(cancellation.Token).ConfigureAwait(false),
                                NativeCliJsonContext.Default.NativeCliEnumeratorResult);
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
                                result = JsonSerializer.SerializeToElement(new NativeCliStreamDescriptor(token),
                                    NativeCliJsonContext.Default.NativeCliStreamDescriptor);
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
                catch (InvalidOperationException exception)
                {
                    error = new(-32000, exception.Message);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested || lifetime.IsCancellationRequested)
                {
                    error = new(-32800, "The backchannel request was cancelled.");
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
                await writes.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    await NativeRpcFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new NativeCliResponse
                    {
                        Id = request.Id.Value, Result = error is null ? result : null, Error = error
                    }, NativeCliJsonContext.Default.NativeCliResponse), lifetime.Token).ConfigureAwait(false);
                    // A stop response must be flushed before cancellation closes either backchannel.
                    if (error is null)
                    {
                        reply?.AfterResponse?.Invoke();
                    }
                }
                finally
                {
                    writes.Release();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
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
