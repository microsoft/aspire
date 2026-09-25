// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Shared.TerminalHost;
using StreamJsonRpc;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// AppHost-side client for a single terminal host process's control UDS. Opens a fresh
/// JsonRpc connection per call, invokes the requested method, and disposes the connection.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>aspire.terminalhost</c> process serves one parent replica and exposes a single-session
/// control surface. To enumerate the full set of replicas for a target resource, the AppHost
/// fans out across each per-replica host's control UDS using this client.
/// </para>
/// <para>
/// Connection retries are bounded by the per-call <c>totalTimeout</c> with a short per-attempt
/// delay so that a control RPC issued shortly after the AppHost has asked DCP to launch the
/// host doesn't see a transient connection-refused error.
/// </para>
/// </remarks>
internal static class TerminalHostControlClient
{
    private static readonly TimeSpan s_defaultRetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Opens the control UDS at <paramref name="socketPath"/> and invokes
    /// <see cref="TerminalHostControlProtocol.GetSessionMethod"/>, returning the host's
    /// snapshot of its single session. Throws on transport failures and times out per
    /// <paramref name="totalTimeout"/>.
    /// </summary>
    public static async Task<TerminalHostSessionInfo> GetSessionAsync(
        string socketPath,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(totalTimeout);

        using var rpc = await ConnectWithRetryAsync(socketPath, timeoutCts.Token).ConfigureAwait(false);

        return await rpc.InvokeWithCancellationAsync<TerminalHostSessionInfo>(
            TerminalHostControlProtocol.GetSessionMethod,
            arguments: null,
            timeoutCts.Token).ConfigureAwait(false);
    }

    private static async Task<JsonRpc> ConnectWithRetryAsync(string socketPath, CancellationToken cancellationToken)
    {
        // Bounded retry loop: keep trying to connect until the linked timeout fires.
        // The host's accept loop binds the UDS during StartAsync, but if the AppHost
        // races the host process startup we want to wait for it rather than fail-fast.
        SocketException? lastException = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                var formatter = new SystemTextJsonFormatter();
                var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
                var rpc = new JsonRpc(handler);
                rpc.StartListening();
                return rpc;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastException = ex;
                try
                {
                    await Task.Delay(s_defaultRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw lastException ?? new SocketException();
    }
}
