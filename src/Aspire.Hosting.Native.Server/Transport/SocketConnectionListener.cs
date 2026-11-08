// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;

namespace Aspire.Hosting.Native.Transport;

/// <summary>Owns bounded concurrent socket connections independently of their application protocol.</summary>
internal static class SocketConnectionListener
{
    public static async Task RunAsync(Socket listener, int maximumConnections,
        Func<Stream, CancellationToken, Task> serve, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(serve);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConnections, 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clients = new List<Task>();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    // Reap after accept: a client may have ended while accept was pending.
                    await Task.WhenAll(clients.Where(client => client.IsCompleted)).ConfigureAwait(false);
                    clients.RemoveAll(client => client.IsCompletedSuccessfully);
                    if (clients.Count >= maximumConnections)
                    {
                        socket.Dispose();
                        continue;
                    }
                    clients.Add(ServeAsync(socket));
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(clients).ConfigureAwait(false);
        }

        async Task ServeAsync(Socket socket)
        {
            using var owned = socket;
            using var stream = new NetworkStream(socket, ownsSocket: false);
            try
            {
                await serve(stream, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch
            {
                // Surface handler failures even when no further client connects.
                await lifetime.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
