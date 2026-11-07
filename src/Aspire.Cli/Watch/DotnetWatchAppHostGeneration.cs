// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Backchannel;
using Aspire.Shared;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Watch;

internal sealed class DotnetWatchAppHostGeneration : IAsyncDisposable, IGracefulShutdownWindow
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _graceful;
    private readonly IGracefulShutdownWindow _commandShutdown;
    private readonly TimeSpan _shutdownBudget;
    private readonly string _homeDirectory;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _invalidation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _hostStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeServerStream? _pipe;
    private Socket? _socket;
    private Task _listener = Task.CompletedTask;
    private int _gracefulWindowStarted;
    private string? _endpoint;
    private string? _hostInstance;
    private volatile bool _isEnabled;
    private bool _disposed;

    internal DotnetWatchAppHostGeneration(
        IGracefulShutdownWindow commandShutdown,
        TimeSpan shutdownBudget,
        string homeDirectory,
        ILogger logger)
    {
        _commandShutdown = commandShutdown;
        _shutdownBudget = shutdownBudget;
        _homeDirectory = homeDirectory;
        _logger = logger;
        _graceful = CancellationTokenSource.CreateLinkedTokenSource(commandShutdown.GracefulShutdownToken);
        Session = Guid.NewGuid().ToString("N");
    }

    internal string Endpoint => _endpoint ?? throw new InvalidOperationException("The AppHost generation channel is not enabled.");
    internal string Session { get; }
    internal bool IsEnabled => _isEnabled;
    internal Task Invalidated => _invalidation.Task;
    internal Task<string> HostStarted => _hostStarted.Task;
    internal Task<Exception> Failure => _failure.Task;
    internal bool RequiresLauncherMonitoring { get; init; } = true;
    internal Action? OnBackchannelEstablished { get; init; }

    bool IGracefulShutdownWindow.IsEnabled => _commandShutdown.IsEnabled;
    public CancellationToken GracefulShutdownToken => _graceful.Token;

    internal void ApplyEnvironment(IDictionary<string, string> environment)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_endpoint is null)
        {
            // Unsupported AppHosts and polyglot hosts never open a generation channel. On Unix,
            // use the existing private, compact backchannel directory instead of a shared temp pipe.
            if (OperatingSystem.IsWindows())
            {
                var pipeName = $"ah{Session}";
                _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _endpoint = pipeName;
            }
            else
            {
                var socketPath = BackchannelConstants.ComputeCliSocketPath(_homeDirectory, "apphost-watch");
                SocketPermissionHelper.CreateDirectory(Path.GetDirectoryName(socketPath)!, repairExisting: true);
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    SocketPermissionHelper.Bind(socket, socketPath);
                    socket.Listen(8);
                    _socket = socket;
                    _endpoint = socketPath;
                }
                catch
                {
                    socket.Dispose();
                    File.Delete(socketPath);
                    throw;
                }
            }

            _isEnabled = true;
            _listener = ListenAsync();
        }

        environment[DotnetWatchOwnership.AppHostControlEnvironmentVariable] = Endpoint;
        environment[DotnetWatchOwnership.AppHostSessionEnvironmentVariable] = Session;
    }

    internal void MarkBackchannelEstablished()
    {
        if (IsEnabled && !_hostStarted.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(Aspire.Cli.Resources.RunCommandStrings.WatchAppHostIdentityRequired);
        }

        OnBackchannelEstablished?.Invoke();
    }

    public void BeginGracefulWindow()
    {
        // Reconstructing a model must not consume the command's Ctrl+C budget. Each generation
        // gets its own deadline, still bounded by command-wide shutdown when the user cancels.
        if (Interlocked.Exchange(ref _gracefulWindowStarted, 1) == 0)
        {
            _graceful.CancelAfter(_commandShutdown.IsEnabled ? _shutdownBudget : TimeSpan.Zero);
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && !_invalidation.Task.IsCompleted)
            {
                if (_pipe is { } pipe)
                {
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        await ReadNotificationAsync(pipe).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (pipe.IsConnected)
                        {
                            pipe.Disconnect();
                        }
                    }
                }
                else if (_socket is { } socket)
                {
                    var connection = await socket.AcceptAsync(_stop.Token).ConfigureAwait(false);
                    await using var stream = new NetworkStream(connection, ownsSocket: true);
                    await ReadNotificationAsync(stream).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested)
            {
                _failure.TrySetResult(exception);
            }
        }
    }

    private async Task ReadNotificationAsync(Stream stream)
    {
        try
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            var message = await DotnetWatchAppHostProtocol.ReadAsync(stream, readTimeout.Token).ConfigureAwait(false);
            if (!IsEnabled || !string.Equals(message.Session, Session, StringComparison.Ordinal))
            {
                return;
            }

            if (message.Kind == DotnetWatchAppHostProtocol.HostStarted)
            {
                if (_hostInstance is null)
                {
                    _hostInstance = message.Instance;
                    _hostStarted.TrySetResult(message.Instance);
                }
                else if (!string.Equals(_hostInstance, message.Instance, StringComparison.Ordinal))
                {
                    // A rude edit starts a different real AppHost without calling a metadata handler.
                    // Replace its entire model/backchannel; a disconnect on its own is not invalidation.
                    _invalidation.TrySetResult();
                }
            }
            else if (string.Equals(_hostInstance, message.Instance, StringComparison.Ordinal))
            {
                _invalidation.TrySetResult();
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "Ignoring an invalid AppHost watch notification.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isEnabled = false;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _listener.ConfigureAwait(false);
        if (_pipe is { } pipe)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        _socket?.Dispose();
        if (_socket is not null && _endpoint is { } socketPath)
        {
            File.Delete(socketPath);
        }
        _graceful.Dispose();
        _stop.Dispose();
    }
}
