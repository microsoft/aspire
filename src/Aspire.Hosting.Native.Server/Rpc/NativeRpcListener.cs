// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Hosts authenticated, independently owned capability connections over local sockets.</summary>
internal sealed class NativeRpcListener : IDisposable
{
    private readonly Socket _listener;
    private readonly string _token;
    private readonly NativeApplicationServer _server;
    private readonly NativeLanguageCatalog? _languages;
    private readonly Action<string> _diagnostic;
    private readonly NativeServerOptions _options;
    private int _running;

    public EndPoint EndPoint => _listener.LocalEndPoint!;

    public NativeRpcListener(EndPoint endPoint, string token, Action<string> diagnostic)
        : this(endPoint, token, diagnostic, new NativeApplicationServer())
    {
    }

    public NativeRpcListener(EndPoint endPoint, string token, Action<string> diagnostic, NativeApplicationServer server)
        : this(endPoint, token, diagnostic, server, null)
    {
    }

    public NativeRpcListener(EndPoint endPoint, string token, Action<string> diagnostic, NativeApplicationServer server,
        NativeLanguageCatalog? languages)
        : this(endPoint, token, diagnostic, server, languages, new())
    {
    }

    public NativeRpcListener(EndPoint endPoint, string token, Action<string> diagnostic, NativeApplicationServer server,
        NativeLanguageCatalog? languages, NativeServerOptions options)
    {
        options.Validate();
        _options = options;
        _server = server;
        _languages = languages;
        if (endPoint is not UnixDomainSocketEndPoint &&
            (endPoint is not IPEndPoint ip || !IPAddress.IsLoopback(ip.Address)))
        {
            throw new ArgumentException("The native server requires a local endpoint.", nameof(endPoint));
        }
        // Validate the token before publishing a listener.
        using var validation = new NativeRpcConnection(token, _server);
        _token = token;
        _diagnostic = diagnostic;
        _listener = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _listener.Bind(endPoint);
            _listener.Listen(options.MaximumConnections);
        }
        catch
        {
            _listener.Dispose();
            throw;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("The listener is already running.");
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connections = new List<Task>();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.WhenAll(connections.Where(task => task.IsCompleted)).ConfigureAwait(false);
                connections.RemoveAll(task => task.IsCompletedSuccessfully);
                var socket = await _listener.AcceptAsync(lifetime.Token).ConfigureAwait(false);
                if (connections.Count >= _options.MaximumConnections)
                {
                    _diagnostic("Native connection rejected: connection capacity reached.");
                    socket.Dispose();
                    continue;
                }
                connections.Add(RunConnectionAsync(socket, lifetime.Token));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(connections).ConfigureAwait(false);
            }
            finally
            {
                _diagnostic("Native connections drained; retiring workloads.");
                _server.Close();
                await _server.DrainAsync().ConfigureAwait(false);
                _diagnostic("Native workloads drained.");
            }
        }
    }

    private async Task RunConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(socket, ownsSocket: true);
        using var connection = new NativeRpcConnection(_token, _server, _languages, _options);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(_options.AuthenticationTimeout);
        using var writes = new SemaphoreSlim(1);
        var framing = new NativeRpcFraming(stream, _options.MaximumRequestBytes);
        var requests = new List<Task>();
        try
        {
            while (await framing.ReadAsync(lifetime.Token).ConfigureAwait(false) is { } payload)
            {
                await Task.WhenAll(requests.Where(task => task.IsCompleted)).ConfigureAwait(false);
                requests.RemoveAll(task => task.IsCompletedSuccessfully);
                if (requests.Count >= _options.MaximumConcurrentRequests)
                {
                    throw new InvalidDataException("The connection response backlog exceeds the limit.");
                }
                requests.Add(RespondAsync(payload));
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            // A truncated/malformed frame ends only this connection. Report the
            // classification, never payloads, bearer tokens, or peer input.
            _diagnostic($"Native connection closed: {exception.GetType().Name}.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            connection.Dispose();
            await Task.WhenAll(requests).ConfigureAwait(false);
        }

        async Task RespondAsync(byte[] payload)
        {
            var response = await connection.ProcessAsync(payload).ConfigureAwait(false);
            if (connection.IsAuthenticated)
            {
                lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            if (response is null || lifetime.IsCancellationRequested)
            {
                return;
            }
            try
            {
                await writes.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    await NativeRpcFraming.WriteAsync(stream, response, lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    writes.Release();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                _diagnostic($"Native response failed: {exception.GetType().Name}.");
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        _server.Close();
    }
}
