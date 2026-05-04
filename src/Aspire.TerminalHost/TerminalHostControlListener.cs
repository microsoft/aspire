// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.TerminalHost;

/// <summary>
/// Listens on the control UDS and serves a <see cref="TerminalHostControlRpcTarget"/>
/// over StreamJsonRpc to each connecting client (typically the Aspire AppHost).
/// </summary>
internal sealed class TerminalHostControlListener : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly TerminalHostControlRpcTarget _target;
    private readonly ILogger _logger;
    private Socket? _socket;
    private Task? _acceptLoop;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<JsonRpc> _activeRpcs = new();
    private readonly object _gate = new();
    private bool _disposed;

    public TerminalHostControlListener(
        string socketPath,
        TerminalHostControlRpcTarget target,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(logger);

        _socketPath = socketPath;
        _target = target;
        _logger = logger;
    }

    /// <summary>
    /// Binds the UDS and starts the background accept loop.
    /// </summary>
    public Task StartAsync()
    {
        var dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(_socketPath))
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
                // Best effort — fall through and let Bind report the error.
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        socket.Listen(backlog: 5);
        _socket = socket;

        _logger.LogInformation("Control listener bound to '{Path}'.", _socketPath);

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_disposeCts.Token));
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Control listener accept failed; stopping.");
                return;
            }

            _ = Task.Run(() => ServeClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeClientAsync(Socket client, CancellationToken cancellationToken)
    {
        await using var stream = new NetworkStream(client, ownsSocket: true);

        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);

        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcMethod(
            TerminalHostControlProtocol.GetReplicasMethod,
            _target.GetType().GetMethod(nameof(TerminalHostControlRpcTarget.GetReplicasAsync))!,
            _target);
        rpc.AddLocalRpcMethod(
            TerminalHostControlProtocol.GetInfoMethod,
            _target.GetType().GetMethod(nameof(TerminalHostControlRpcTarget.GetInfoAsync))!,
            _target);
        rpc.AddLocalRpcMethod(
            TerminalHostControlProtocol.ShutdownMethod,
            _target.GetType().GetMethod(nameof(TerminalHostControlRpcTarget.ShutdownAsync))!,
            _target);

        lock (_gate)
        {
            if (_disposed)
            {
                rpc.Dispose();
                return;
            }
            _activeRpcs.Add(rpc);
        }

        try
        {
            rpc.StartListening();
            await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Control RPC connection ended with an error.");
        }
        finally
        {
            lock (_gate)
            {
                _activeRpcs.Remove(rpc);
            }
            rpc.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            _socket?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        List<JsonRpc> rpcs;
        lock (_gate)
        {
            rpcs = [.. _activeRpcs];
            _activeRpcs.Clear();
        }
        foreach (var rpc in rpcs)
        {
            rpc.Dispose();
        }

        _disposeCts.Dispose();

        try
        {
            File.Delete(_socketPath);
        }
        catch
        {
            // Best effort.
        }
    }
}
