// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Exposes an AppHost terminal's existing viewer transport over a private local socket.
/// </summary>
internal sealed class TerminalSocketListener : IAsyncDisposable
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<Task> _clients = [];
    private readonly Func<Stream, CancellationToken, Task> _attach;
    private readonly ILogger _logger;
    private readonly Task _runTask;

    public TerminalSocketListener(string path, Func<Stream, CancellationToken, Task> attach, ILogger logger)
    {
        Path = path;
        _attach = attach;
        _logger = logger;
        try
        {
            SocketPermissionHelper.Bind(_listener, path);
            _listener.Listen(16);
            _runTask = RunAsync();
        }
        catch
        {
            _listener.Dispose();
            _stopping.Dispose();
            throw;
        }
    }

    public string Path { get; }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var socket = await _listener.AcceptAsync(_stopping.Token).ConfigureAwait(false);
                var task = AttachAsync(socket);
                lock (_clients)
                {
                    _clients.Add(task);
                }
                _ = RemoveClientAsync(task);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _logger.LogDebug("AppHost terminal listener {Path} stopped.", Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AppHost terminal listener {Path} failed.", Path);
            throw;
        }
    }

    private async Task AttachAsync(Socket socket)
    {
        var stream = new NetworkStream(socket, ownsSocket: true);
        await using var _ = stream.ConfigureAwait(false);
        try
        {
            await _attach(stream, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "AppHost terminal viewer at {Path} disconnected.", Path);
        }
    }

    private async Task RemoveClientAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AppHost terminal viewer at {Path} failed.", Path);
        }
        finally
        {
            lock (_clients)
            {
                _clients.Remove(task);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            finally
            {
                Task[] clients;
                lock (_clients)
                {
                    clients = [.. _clients];
                }
                await Task.WhenAll(clients).ConfigureAwait(false);
            }
        }
        finally
        {
            _listener.Dispose();
            _stopping.Dispose();
            File.Delete(Path);
        }
    }
}
