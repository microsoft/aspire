// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Cli;

/// <summary>Implements the existing CLI backchannel without a native-specific client.</summary>
internal sealed class NativeCliProtocol : IDisposable
{
    private readonly Socket _listener;
    private readonly NativeApplicationServer _server;
    private readonly Action _stop;
    private readonly string _path;
    private readonly Func<CancellationToken, Task<NativeCliDashboardUrls>> _dashboard;
    private readonly NativeCliLogBuffer _logs;
    private readonly NativeServerOptions _options;
    private int _ready;

    public NativeCliProtocol(string path, NativeApplicationServer server, Action stop,
        Func<CancellationToken, Task<NativeCliDashboardUrls>> dashboard, NativeCliLogBuffer logs, NativeServerOptions options)
    {
        if (!Path.IsPathFullyQualified(path) || File.Exists(path))
        {
            throw new ArgumentException("The backchannel requires a fresh absolute socket path.", nameof(path));
        }
        _path = path;
        _server = server;
        _stop = stop;
        _dashboard = dashboard;
        _logs = logs;
        options.Validate();
        _options = options;
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // The CLI owns the parent directory. Only restrict the socket itself;
            // changing an existing parent would affect unrelated applications.
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            _listener.Listen(1);
        }
        catch
        {
            _listener.Dispose();
            throw;
        }
    }

    internal bool IsAppHostReady => Volatile.Read(ref _ready) != 0;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await ServeAsync(socket, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writes = new SemaphoreSlim(1);
        var framing = new NativeRpcFraming(stream, _options.MaximumRequestBytes);
        var requests = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var tasks = new List<Task>();
        var streams = new Dictionary<long, StreamCursor>();
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
                    ?? throw new InvalidDataException("Invalid CLI request.");
                var method = request.Method;
                if (request.Jsonrpc != "2.0")
                {
                    throw new InvalidDataException("Invalid CLI protocol version.");
                }
                if (method == "$/cancelRequest")
                {
                    var cancellationRequest = request.Params?.Deserialize(NativeCliJsonContext.Default.NativeCliCancellation)
                        ?? throw new InvalidDataException("A cancellation requires a request ID.");
                    var key = cancellationRequest.Id.GetRawText();
                    lock (gate)
                    {
                        if (requests.TryGetValue(key, out var pending))
                        {
                            pending.Cancel();
                        }
                    }
                    continue;
                }
                var id = request.Id;
                var requestKey = id?.GetRawText();
                if (id is { ValueKind: not (JsonValueKind.String or JsonValueKind.Number) })
                {
                    throw new InvalidDataException("Invalid CLI request ID.");
                }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (gate)
                {
                    if (tasks.Count >= _options.MaximumConcurrentRequests || requestKey is not null && requests.ContainsKey(requestKey))
                    {
                        cancellation.Dispose();
                        throw new InvalidDataException("The CLI request limit was reached or an ID was reused.");
                    }
                    if (requestKey is not null)
                    {
                        requests.Add(requestKey, cancellation);
                    }
                }
                tasks.Add(RespondAsync(request, method, id, requestKey, cancellation));
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or JsonException or RpcFault)
        {
            Console.Error.WriteLine($"CLI backchannel disconnected: {exception.GetType().Name}.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                foreach (var cursor in streams.Values)
                {
                    await cursor.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        async Task RespondAsync(NativeCliRequest request, string method, JsonElement? id, string? requestKey,
            CancellationTokenSource cancellation)
        {
            var result = JsonSerializer.SerializeToElement((string?)null, NativeCliJsonContext.Default.String);
            NativeCliError? error = null;
            try
            {
                try
                {
                    if (method is not ("$/enumerator/next" or "$/enumerator/abort") &&
                        request.Params is { ValueKind: not JsonValueKind.Null } parameters &&
                        (parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() != 0))
                    {
                        throw new ArgumentException("This CLI method takes no arguments.");
                    }
                    switch (method)
                    {
                        case "GetCapabilitiesAsync":
                            result = JsonSerializer.SerializeToElement(new[] { "baseline.v2" }, NativeCliJsonContext.Default.StringArray);
                            break;
                        case "NotifyAppHostReadyAsync":
                            Interlocked.Exchange(ref _ready, 1);
                            break;
                        case "RequestStopAsync":
                            break;
                        case "GetDashboardUrlsAsync":
                            result = JsonSerializer.SerializeToElement(await _dashboard(cancellation.Token).ConfigureAwait(false),
                                NativeCliJsonContext.Default.NativeCliDashboardUrls);
                            break;
                        case "GetResourceStatesAsync":
                        case "GetAppHostLogEntriesAsync":
                            lock (gate)
                            {
                                if (streams.Count >= _options.MaximumCliStreams)
                                {
                                    throw new InvalidOperationException("The CLI stream limit was reached.");
                                }
                                var token = checked(++nextToken);
                                streams.Add(token, new StreamCursor(method == "GetResourceStatesAsync"
                                    ? EncodeAsync(ResourceStatesAsync(lifetime.Token), NativeCliJsonContext.Default.NativeCliResourceState, lifetime.Token)
                                    : EncodeAsync(_logs.ReadAsync(lifetime.Token), NativeCliJsonContext.Default.NativeCliLogEntry, lifetime.Token),
                                    lifetime.Token));
                                result = JsonSerializer.SerializeToElement(new NativeCliStreamDescriptor(token),
                                    NativeCliJsonContext.Default.NativeCliStreamDescriptor);
                            }
                            break;
                        case "$/enumerator/next":
                        case "$/enumerator/abort":
                            if (request.Params is not { ValueKind: JsonValueKind.Array } arguments || arguments.GetArrayLength() != 1)
                            {
                                throw new ArgumentException("An enumerator request requires one token.");
                            }
                            if (!arguments[0].TryGetInt64(out var cursorId))
                            {
                                throw new ArgumentException("An enumerator token must be an integer.");
                            }
                            StreamCursor cursor;
                            lock (gate)
                            {
                                if (!streams.TryGetValue(cursorId, out cursor!))
                                {
                                    throw new InvalidOperationException("The CLI stream has ended.");
                                }
                                if (method == "$/enumerator/abort")
                                {
                                    streams.Remove(cursorId);
                                }
                            }
                            if (method == "$/enumerator/abort")
                            {
                                await cursor.DisposeAsync().ConfigureAwait(false);
                            }
                            else
                            {
                                result = JsonSerializer.SerializeToElement(await cursor.NextAsync(cancellation.Token).ConfigureAwait(false),
                                    NativeCliJsonContext.Default.NativeCliEnumeratorResult);
                            }
                            break;
                        default:
                            error = new(-32601, "The server does not implement this backchannel method.");
                            break;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or RpcFault or JsonException)
                {
                    error = new(-32602, "The CLI arguments are invalid.");
                }
                catch (InvalidOperationException)
                {
                    error = new(-32000, "The CLI operation is not allowed in the current state.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested || lifetime.IsCancellationRequested)
                {
                    error = new(-32800, "The CLI request was cancelled.");
                }
                if (id is null || lifetime.IsCancellationRequested)
                {
                    return;
                }
                var response = new NativeCliResponse { Id = id.Value, Result = error is null ? result : null, Error = error };
                await writes.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    await NativeRpcFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response,
                        NativeCliJsonContext.Default.NativeCliResponse),
                        lifetime.Token).ConfigureAwait(false);
                    if (method == "RequestStopAsync" && error is null)
                    {
                        _stop();
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
            finally
            {
                lock (gate)
                {
                    if (requestKey is not null)
                    {
                        requests.Remove(requestKey);
                    }
                }
                cancellation.Dispose();
            }
        }
    }

    private async IAsyncEnumerable<NativeCliResourceState> ResourceStatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplicationObserver? previous = null;
        long version = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observer = _server.GetApplicationObserver();
            if (observer is null)
            {
                previous = null;
                version = -1;
                await Task.Delay(_options.RetryInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            ApplicationObservations snapshot;
            try
            {
                snapshot = observer.ReadResourceObservations();
                if (ReferenceEquals(previous, observer) && snapshot.Version == version)
                {
                    var interval = Math.Max((int)_options.Runtime.ObservationInterval.TotalMilliseconds, 1);
                    await observer.WaitResourceObservations(version, interval).WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (ObjectDisposedException)
            {
                continue;
            }
            previous = observer;
            version = snapshot.Version;
            foreach (var resource in snapshot.Resources)
            {
                yield return new NativeCliResourceState(resource.Name, resource.TypeId, resource.State,
                    resource.Urls, resource.Healthy ? "Healthy" : "Unhealthy");
            }
        }
    }

    private static async IAsyncEnumerable<JsonElement> EncodeAsync<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return JsonSerializer.SerializeToElement(value, typeInfo);
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        File.Delete(_path);
    }

    private sealed class StreamCursor : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly IAsyncEnumerator<JsonElement> _enumerator;
        private readonly SemaphoreSlim _gate = new(1);
        private bool _finished;
        private int _disposed;

        public StreamCursor(IAsyncEnumerable<JsonElement> source, CancellationToken cancellationToken)
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
                JsonElement[] values = [];
                if (!_finished && await _enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    values = [_enumerator.Current];
                }
                else
                {
                    _finished = true;
                }
                // StreamJsonRpc serializes IAsyncEnumerable<T> as {"token":1}.
                // $/enumerator/next [1] receives {"values":[...],"finished":false}.
                // https://github.com/microsoft/vs-streamjsonrpc/blob/main/src/StreamJsonRpc/Reflection/MessageFormatterEnumerableTracker.cs
                return new NativeCliEnumeratorResult(values, _finished);
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
