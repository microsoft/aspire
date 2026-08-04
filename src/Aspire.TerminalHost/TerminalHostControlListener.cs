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
    // Reservation counter for connections that have been accepted by the loop
    // but whose ServeClientAsync task has not yet run far enough to add its
    // JsonRpc instance to _activeRpcs. Without this, two fast back-to-back
    // accepts could both observe _activeRpcs.Count == 0 under the lock and
    // both be served, defeating the documented single-client invariant.
    // Counted under _gate alongside _activeRpcs.
    private int _pendingClients;
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
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(_socketPath));

            // Restrict the control socket to the owning user (0600). Without this, file
            // permissions are governed solely by the inherited umask — on developer
            // machines that's frequently 002/022, leaving the socket world- or
            // group-accessible. Any local user who can traverse to the path could then
            // dial and invoke ShutdownAsync (no auth) or GetSessionAsync (leaks peer
            // DisplayNames). Skipped on Windows (UDS is supported but SetUnixFileMode
            // is not, and Windows access control on the socket file follows ACLs from
            // the temp directory, which is per-user by default).
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // Backlog of 16 is well above the documented "single AppHost client"
            // contract. The AppHost only ever maintains one healthy session, but it
            // may briefly reconnect (e.g., on its own startup retry) and CI runners
            // under load occasionally see transient kernel queue pressure on UDS
            // listen sockets. A larger backlog absorbs those bursts without affecting
            // the steady-state single-client behaviour — the accept loop still
            // refuses additional sessions when one is already active.
            socket.Listen(backlog: 16);
        }
        catch
        {
            // If Bind/SetUnixFileMode/Listen fail (perm denied, EADDRINUSE that
            // survived the pre-delete, broken parent dir), the raw Socket would
            // otherwise leak as a kernel handle until GC. Also delete any
            // partially-bound socket file so a retry isn't fighting our own
            // residue. Best-effort - swallow secondary failures so the original
            // exception is the one the caller sees.
            try { socket.Dispose(); } catch { }
            try { File.Delete(_socketPath); } catch { }
            throw;
        }
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
            catch (SocketException ex) when (IsTransientAcceptError(ex.SocketErrorCode))
            {
                // Linux can return EAGAIN / EINTR from accept() under load (kernel
                // accept-queue pressure or signal interruption) — the socket is still
                // healthy and the next Accept will succeed. Bailing here would silently
                // kill the control channel for the rest of the process lifetime, which
                // we observed in CI as ConcurrentControlConnectsAreRefusedDownToOne
                // failing with `Resource temporarily unavailable`.
                _logger.LogDebug(ex, "Transient accept error ({SocketError}); retrying.", ex.SocketErrorCode);
                continue;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Control listener accept failed; stopping.");
                return;
            }

            // The control protocol is documented as a single AppHost client (lifecycle,
            // shutdown, stats). If we already have an active session — or one being set
            // up on a worker task that hasn't reached _activeRpcs.Add yet — the new
            // socket is either an accidental retry from the same AppHost or a hostile
            // second dialer; in either case it is safer to refuse it than to fan out
            // unbounded concurrent JsonRpc instances (each allocates a NetworkStream + a
            // header-delimited message handler and adds three RPC registrations). The
            // 0600 perm on the UDS already restricts to the owning user, but defence in
            // depth.
            //
            // The reservation is taken under _gate at the accept site so two fast
            // back-to-back connects can't both pass the empty-slot check before either
            // has reached its ServeClientAsync. ServeClientAsync converts the
            // reservation to an _activeRpcs entry under the same lock.
            bool reserved;
            lock (_gate)
            {
                if (_activeRpcs.Count + _pendingClients >= 1)
                {
                    _logger.LogWarning(
                        "Refusing additional control connection - {Active} active session(s), {Pending} pending.",
                        _activeRpcs.Count,
                        _pendingClients);
                    reserved = false;
                }
                else
                {
                    _pendingClients++;
                    reserved = true;
                }
            }

            if (!reserved)
            {
                try { client.Dispose(); } catch { }
                continue;
            }

            _ = Task.Run(() => ServeClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeClientAsync(Socket client, CancellationToken cancellationToken)
    {
        // Tracks whether we still owe a _pendingClients-- decrement. Set to false
        // as soon as the reservation is converted to an _activeRpcs entry (or the
        // listener is already disposed). The outer finally releases any leftover
        // reservation if anything between accept and the conversion lock throws.
        var reservationOwned = true;
        JsonRpc? rpc = null;
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);

            var formatter = new SystemTextJsonFormatter();
            var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);

            rpc = new JsonRpc(handler);
            rpc.AddLocalRpcMethod(
                TerminalHostControlProtocol.GetSessionMethod,
                _target.GetType().GetMethod(nameof(TerminalHostControlRpcTarget.GetSessionAsync))!,
                _target);
            rpc.AddLocalRpcMethod(
                TerminalHostControlProtocol.GetInfoMethod,
                _target.GetType().GetMethod(nameof(TerminalHostControlRpcTarget.GetInfoAsync))!,
                _target);
            rpc.AddLocalRpcMethod(
                TerminalHostControlProtocol.ShutdownMethod,
                _target.GetType().GetMethod(nameof(TerminalHostControlRpcTarget.ShutdownAsync))!,
                _target);

            // Convert the accept-site reservation to an _activeRpcs entry under
            // the same lock, or release the reservation if we're already
            // disposing. After this point the slot accounting moves to
            // _activeRpcs and the finally releases via _activeRpcs.Remove.
            lock (_gate)
            {
                _pendingClients--;
                reservationOwned = false;
                if (_disposed)
                {
                    return;
                }
                _activeRpcs.Add(rpc);
            }

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
            // If we never reached the conversion lock (e.g. NetworkStream/JsonRpc
            // setup threw), the slot is still reserved as pending. Release it so
            // the listener can accept future connections.
            if (reservationOwned)
            {
                lock (_gate)
                {
                    _pendingClients--;
                }
            }
            else if (rpc is not null)
            {
                lock (_gate)
                {
                    _activeRpcs.Remove(rpc);
                }
            }

            rpc?.Dispose();
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

    private static bool IsTransientAcceptError(SocketError code) => code switch
    {
        // EAGAIN / EWOULDBLOCK — kernel accept queue empty or temporarily exhausted.
        // Observed on Linux CI under load even though AcceptAsync is awaiting.
        SocketError.TryAgain => true,
        SocketError.WouldBlock => true,
        // EINTR — accept interrupted by a signal.
        SocketError.Interrupted => true,
        _ => false
    };
}
