// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;

namespace Aspire.Tray;

internal sealed class TrayActivation : IAsyncDisposable
{
    private const byte ShowRequest = 1;
    private const byte StopRequest = 2;
    private const byte ReadyRequest = 3;
    private const byte AcceptedResponse = 1;
    private const byte FailedResponse = 0;
    private const int ResponseLength = 13;
    internal static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(10);
    private readonly NamedPipeServerStream _pipe;
    private readonly Func<CancellationToken, Task> _show;
    private readonly Func<CancellationToken, Task> _ready;
    private readonly Action _stop;
    private readonly TrayProcessIdentity _identity;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly TimeSpan _timeout;

    public TrayActivation(string pipeName, Func<CancellationToken, Task> show,
        Func<CancellationToken, Task> ready, Action stop)
        : this(pipeName, show, ready, stop, RequestTimeout)
    {
    }

    internal TrayActivation(string pipeName, Func<CancellationToken, Task> show,
        Func<CancellationToken, Task> ready, Action stop, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
        _show = show;
        _ready = ready;
        _stop = stop;
        using var process = Process.GetCurrentProcess();
        _identity = new(process.Id, process.StartTime.ToUniversalTime().Ticks);
        // CurrentUserOnly checks the peer identity on Unix as well as applying the Windows
        // ACL. The pipe carries no AppHost data or arbitrary commands.
        _pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _worker = RunAsync();
    }

    public static Task<TrayProcessIdentity> ShowExistingAsync(string pipeName, CancellationToken cancellationToken)
        => SendAsync(pipeName, ShowRequest, cancellationToken);

    public static Task<TrayProcessIdentity> WaitUntilReadyAsync(string pipeName, CancellationToken cancellationToken)
        => SendAsync(pipeName, ReadyRequest, cancellationToken);

    public static Task<TrayProcessIdentity> StopExistingAsync(string pipeName, CancellationToken cancellationToken)
        => SendAsync(pipeName, StopRequest, cancellationToken);

    private static async Task<TrayProcessIdentity> SendAsync(string pipeName, byte command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            // Connecting waits for a first launch still setting up the native UI. Readiness and
            // restoration are acknowledged by the UI thread, not by accepting the socket.
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { command }, timeout.Token).ConfigureAwait(false);
            var response = new byte[ResponseLength];
            await pipe.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
            {
                await pipe.WriteAsync(new byte[] { AcceptedResponse }, timeout.Token).ConfigureAwait(false);
            }
            if (response[0] != AcceptedResponse)
            {
                throw new InvalidOperationException(command switch
                {
                    ShowRequest => "The running tray could not restore its icon.",
                    StopRequest => "The running tray could not accept the stop request.",
                    _ => "The tray could not become ready."
                });
            }
            var identity = new TrayProcessIdentity(
                BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(1)),
                BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(5)));
            if (identity.ProcessId <= 0 || identity.StartTimeUtcTicks <= 0)
            {
                throw new InvalidDataException("The tray returned an invalid process identity.");
            }
            return identity;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The running tray did not respond. Close any open tray menu or dialog and try again.");
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                request.CancelAfter(_timeout);
                try
                {
                    // Private control-v1 protocol: request [1] = restore, [2] = stop,
                    // [3] = ready. The 13-byte reply is [1 = accepted / 0 = rejected,
                    // int32 PID, int64 UTC start ticks], with little-endian integers.
                    // The lifetime lets stop wait for this tray, never a reused PID.
                    var command = new byte[1];
                    await _pipe.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                    var response = FailedResponse;
                    if (command[0] is ShowRequest or StopRequest or ReadyRequest)
                    {
                        try
                        {
                            var operation = command[0] == ShowRequest ? _show : _ready;
                            await operation(request.Token).WaitAsync(request.Token).ConfigureAwait(false);
                            response = AcceptedResponse;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Console.Error.WriteLine($"Tray control request failed ({ex.GetType().Name}).");
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("Rejected an unsupported tray activation request.");
                    }
                    var reply = new byte[ResponseLength];
                    reply[0] = response;
                    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(1), _identity.ProcessId);
                    BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(5), _identity.StartTimeUtcTicks);
                    await _pipe.WriteAsync(reply, request.Token).ConfigureAwait(false);
                    if (OperatingSystem.IsWindows())
                    {
                        // DisconnectNamedPipe discards unread replies. Windows clients acknowledge
                        // consuming the reply before we disconnect or quit; unlike WaitForPipeDrain,
                        // this read is cancellable and cannot hold shutdown hostage.
                        // The Windows control-v1 reply is followed by the client's single byte [1].
                        var acknowledgement = new byte[1];
                        await _pipe.ReadExactlyAsync(acknowledgement, request.Token).ConfigureAwait(false);
                        if (acknowledgement[0] != AcceptedResponse)
                        {
                            throw new InvalidDataException("The tray control acknowledgement is invalid.");
                        }
                    }
                    if (response == AcceptedResponse && command[0] == StopRequest)
                    {
                        // Acknowledge before posting quit: UI shutdown disposes this listener.
                        _stop();
                    }
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                {
                    if (!_shutdown.IsCancellationRequested)
                    {
                        Console.Error.WriteLine("The tray activation request timed out.");
                    }
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine($"Tray activation connection ended ({ex.GetType().Name}).");
                }
                finally
                {
                    _pipe.Disconnect();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tray activation listener failed ({ex.GetType().Name}).");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _pipe.Dispose();
            _shutdown.Dispose();
        }
    }
}
