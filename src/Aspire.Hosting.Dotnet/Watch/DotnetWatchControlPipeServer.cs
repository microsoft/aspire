// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text.Json;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// The AppHost end of the watcher's control pipe (<c>--control-pipe</c>): the watcher connects to it as a client and
/// reads one JSON command per line.
/// </summary>
/// <remarks>
/// The watcher (dotnet/sdk <c>WatchControlReader</c>, 10.0.x) supports one command,
/// <c>{"type":"rebuild","projects":[...]}</c>, with project or file-based app entry point paths. Despite its name it
/// doesn't build: it terminates the running processes of those projects and relaunches them with the last build
/// (<c>dotnet run --no-build</c>), every replica of each project. Projects that aren't running are skipped. The
/// watcher reconnects for every launcher it creates, so connections are accepted for as long as the server lives and
/// commands go to the latest one.
/// </remarks>
internal sealed class DotnetWatchControlPipeServer(string pipeName) : IAsyncDisposable
{
    internal const string RebuildCommand = "rebuild";

    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private TaskCompletionSource<NamedPipeServerStream> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _acceptLoop;
    private int _connectionCount;

    public string PipeName { get; } = pipeName;

    /// <summary>Starts listening. The listener exists when this returns, so the watcher can be started right after.</summary>
    public void Start()
    {
        var pipe = CreateServer();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(pipe, _cts.Token));
    }

    /// <summary>How many times the watcher connected (diagnostics and tests).</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>Sends a <c>rebuild</c> command; <see langword="false"/> when the watcher isn't connected.</summary>
    public async Task<bool> SendRebuildAsync(IReadOnlyCollection<string> projects, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", RebuildCommand);
            writer.WriteStartArray("projects");
            foreach (var project in projects)
            {
                writer.WriteStringValue(project);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        var line = buffer.ToArray();

        Task<NamedPipeServerStream> connected;
        lock (_gate)
        {
            connected = _connected.Task;
        }

        NamedPipeServerStream pipe;
        try
        {
            pipe = await connected.WaitAsync(s_connectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The watcher closed this connection (it reconnects for every launcher); wait for the next one.
            lock (_gate)
            {
                if (_connected.Task.IsCompletedSuccessfully && ReferenceEquals(_connected.Task.Result, pipe))
                {
                    _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        var pipe = new NamedPipeServerStream(
            PipeName, PipeDirection.Out, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        DotnetWatchServerInfrastructure.RestrictSocketToCurrentUser(PipeName);
        return pipe;
    }

    /// <summary>
    /// Keeps a listener open and makes every new connection the current one. A connection's end can't be observed
    /// without writing (the watcher only reads), so a replaced connection is disposed when the next one arrives.
    /// </summary>
    private async Task AcceptLoopAsync(NamedPipeServerStream listener, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? current = null;
        try
        {
            while (true)
            {
                await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _connectionCount);
                var connection = listener;
                listener = CreateServer();

                NamedPipeServerStream? previous;
                lock (_gate)
                {
                    previous = current;
                    current = connection;
                    if (_connected.Task.IsCompleted)
                    {
                        _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    _connected.TrySetResult(connection);
                }

                if (previous is not null)
                {
                    await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await previous.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _cts.Dispose();
    }
}
