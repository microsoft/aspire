// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Hex1b.Tokens;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace Aspire.TerminalHost;

/// <summary>
/// Hosts an HMP1 presentation adapter on a Unix domain socket for one terminal session.
/// </summary>
internal sealed class Hmp1UdsServerListenerFilter : IHex1bTerminalPresentationFilter, IDisposable
{
    private readonly string _socketPath;
    private readonly Hmp1PresentationAdapter _presentation;
    private readonly ILogger<Hmp1UdsServerListenerFilter> _logger;
    private readonly Action<Exception> _listenerFaulted;
    private readonly object _gate = new();
    private readonly HashSet<Task> _clientTasks = [];
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private Socket? _listener;

    public Hmp1UdsServerListenerFilter(
        string socketPath,
        Hmp1PresentationAdapter presentation,
        ILogger<Hmp1UdsServerListenerFilter> logger,
        Action<Exception> listenerFaulted)
    {
        _socketPath = socketPath;
        _presentation = presentation;
        _logger = logger;
        _listenerFaulted = listenerFaulted;
        _listener = BindListener(socketPath);
    }

    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(
        int width,
        int height,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenerTask = RunListenerAsync(
            _listener ?? throw new ObjectDisposedException(nameof(Hmp1UdsServerListenerFilter)),
            _listenerCts.Token);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
        IReadOnlyList<AppliedToken> appliedTokens,
        TimeSpan elapsed,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(
            appliedTokens.Select(t => t.Token).ToList());
    }

    /// <inheritdoc />
    public ValueTask OnInputAsync(
        IReadOnlyList<AnsiToken> tokens,
        TimeSpan elapsed,
        CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnResizeAsync(
        int width,
        int height,
        TimeSpan elapsed,
        CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
    {
        if (_listenerCts is null)
        {
            return;
        }

        await _listenerCts.CancelAsync().ConfigureAwait(false);
        DisposeListener();

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException) when (_listenerCts.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_listenerCts.IsCancellationRequested)
            {
            }
        }

        Task[] clientTasks;
        lock (_gate)
        {
            clientTasks = [.. _clientTasks];
        }
        await Task.WhenAll(clientTasks).ConfigureAwait(false);

        _listenerCts.Dispose();
        TryDeleteSocketFile();
    }

    private async Task RunListenerAsync(Socket listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                var clientTask = AddClientAsync(stream, ct);
                lock (_gate)
                {
                    _clientTasks.Add(clientTask);
                }
                _ = ObserveClientTaskAsync(clientTask);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
        }
        catch (SocketException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The HMP1 consumer listener failed.");
            _listenerFaulted(ex);
        }
    }

    private async Task ObserveClientTaskAsync(Task clientTask)
    {
        try
        {
            await clientTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _clientTasks.Remove(clientTask);
            }
        }
    }

    private async Task AddClientAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            _ = await _presentation.AddClient(stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            await DisposeFailedClientStreamAsync(stream).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adding an HMP1 consumer failed unexpectedly.");
            await DisposeFailedClientStreamAsync(stream).ConfigureAwait(false);
        }
    }

    private async Task DisposeFailedClientStreamAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeException) when (
            disposeException is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(disposeException, "Failed to dispose an HMP1 consumer stream after its session ended.");
        }
    }

    private static Socket BindListener(string socketPath)
    {
        var directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog: 16);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    private void DisposeListener()
    {
        Socket? listener;
        lock (_gate)
        {
            listener = _listener;
            _listener = null;
        }
        listener?.Dispose();
    }

    private void TryDeleteSocketFile()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to delete the HMP1 consumer socket file '{SocketPath}'.", _socketPath);
        }
    }

    public void Dispose()
    {
        DisposeListener();
        TryDeleteSocketFile();
    }
}
