// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;

namespace Aspire.Tray.Tests;

public class TrayActivationTests
{
    [Fact]
    public async Task RepeatedStartsRestoreTheExistingInstanceAndWaitForUiAcknowledgement()
    {
        var name = PipeName();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var server = new TrayActivation(name, async cancellationToken =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await restored.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }, _ => Task.CompletedTask, UnexpectedStop);
        await using var lifetime = server.ConfigureAwait(true);

        var first = TrayActivation.ShowExistingAsync(name, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);
        restored.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await TrayActivation.ShowExistingAsync(name, TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task FailedNativeRestorationIsNotReportedAsSuccess()
    {
        var name = PipeName();
        var server = new TrayActivation(name, _ => throw new InvalidOperationException("Native operation failed."),
            _ => Task.CompletedTask, UnexpectedStop);
        await using var lifetime = server.ConfigureAwait(true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrayActivation.ShowExistingAsync(name, TestContext.Current.CancellationToken));
        Assert.Equal("The running tray could not restore its icon.", error.Message);
    }

    [Fact]
    public async Task UnsupportedRequestsAreRejectedWithoutTouchingTheUi()
    {
        var name = PipeName();
        var calls = 0;
        var server = new TrayActivation(name, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, _ => Task.CompletedTask, UnexpectedStop);
        await using var lifetime = server.ConfigureAwait(true);
        using (var client = ConnectClient(name))
        {
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            await client.WriteAsync(new byte[] { 99 }, TestContext.Current.CancellationToken);
            var response = new byte[13];
            await client.ReadExactlyAsync(response, TestContext.Current.CancellationToken);
            Assert.Equal(0, response[0]);
            using var process = Process.GetCurrentProcess();
            Assert.Equal(process.Id, BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(1)));
            Assert.Equal(process.StartTime.ToUniversalTime().Ticks, BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(5)));
            if (OperatingSystem.IsWindows())
            {
                await client.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
            }
        }
        Assert.Equal(0, Volatile.Read(ref calls));
        await TrayActivation.ShowExistingAsync(name, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task AClientThatDisconnectsWithoutARequestDoesNotBreakTheListener()
    {
        var name = PipeName();
        var server = new TrayActivation(name, _ => Task.CompletedTask, _ => Task.CompletedTask, UnexpectedStop);
        await using var lifetime = server.ConfigureAwait(true);
        using (var client = ConnectClient(name))
        {
            await client.ConnectAsync(TestContext.Current.CancellationToken);
        }

        await TrayActivation.ShowExistingAsync(name, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASlowClientCannotKeepTheOnlyConnectionForever()
    {
        var name = PipeName();
        var calls = 0;
        var server = new TrayActivation(name, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, _ => Task.CompletedTask, UnexpectedStop, TimeSpan.FromSeconds(1));
        await using var lifetime = server.ConfigureAwait(true);
        using var stalled = ConnectClient(name);
        await stalled.ConnectAsync(TestContext.Current.CancellationToken);
        var buffer = new byte[1];
        Assert.Equal(0, await stalled.ReadAsync(buffer, TestContext.Current.CancellationToken));
        Assert.Equal(0, Volatile.Read(ref calls));

        await TrayActivation.ShowExistingAsync(name, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ShutdownCancelsAnActivationWaitingForTheUi()
    {
        var name = PipeName();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new TrayActivation(name, async cancellationToken =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                cancelled.TrySetResult();
            }
        }, _ => Task.CompletedTask, UnexpectedStop);
        var request = TrayActivation.ShowExistingAsync(name, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await server.DisposeAsync();
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<IOException>(() => request);
    }

    [Fact]
    public async Task MissingListenerHonorsCancellationInsteadOfStartingAnotherTray()
    {
        using var cancellation = new CancellationTokenSource();
        var request = TrayActivation.ShowExistingAsync(PipeName(), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task ListenerCanShutDownWithoutAnyClients()
    {
        var server = new TrayActivation(PipeName(), _ => Task.CompletedTask, _ => Task.CompletedTask, UnexpectedStop);
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadinessWaitsForTheUiWithoutRestoringPlacement()
    {
        var name = PipeName();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = false;
        var server = new TrayActivation(name, _ =>
        {
            restored = true;
            return Task.CompletedTask;
        }, token =>
        {
            entered.TrySetResult();
            return ready.Task.WaitAsync(token);
        }, UnexpectedStop);
        await using var lifetime = server.ConfigureAwait(true);

        var request = TrayActivation.WaitUntilReadyAsync(name, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(request.IsCompleted);
        ready.SetResult();
        var identity = await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var process = Process.GetCurrentProcess();
        Assert.Equal(new(process.Id, process.StartTime.ToUniversalTime().Ticks), identity);
        Assert.False(restored);
    }

    [Fact]
    public async Task StopReturnsTheExactTrayIdentityAndRequestsQuit()
    {
        var name = PipeName();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = false;
        var server = new TrayActivation(name, _ =>
        {
            restored = true;
            return Task.CompletedTask;
        }, _ => Task.CompletedTask, () => stopped.TrySetResult());
        await using var lifetime = server.ConfigureAwait(true);

        var identity = await TrayActivation.StopExistingAsync(name, TestContext.Current.CancellationToken);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var process = Process.GetCurrentProcess();
        Assert.Equal(new(process.Id, process.StartTime.ToUniversalTime().Ticks), identity);
        Assert.False(restored);
    }

    [Fact]
    public async Task AFailedReadinessCheckDoesNotRequestQuit()
    {
        var name = PipeName();
        var stopped = false;
        var server = new TrayActivation(name, _ => Task.CompletedTask,
            _ => throw new InvalidOperationException("UI unavailable."), () => stopped = true);
        await using var lifetime = server.ConfigureAwait(true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrayActivation.StopExistingAsync(name, TestContext.Current.CancellationToken));
        Assert.Equal("The running tray could not accept the stop request.", error.Message);
        Assert.False(stopped);
    }

    [Fact]
    public async Task StopAcknowledgementSurvivesImmediateListenerShutdown()
    {
        var name = PipeName();
        var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new TrayActivation(name, _ => Task.CompletedTask,
            _ => Task.CompletedTask, () => quit.TrySetResult());
        var shutdown = ShutdownAsync();
        try
        {
            var identity = await TrayActivation.StopExistingAsync(name, TestContext.Current.CancellationToken);
            Assert.Equal(Environment.ProcessId, identity.ProcessId);
        }
        finally
        {
            quit.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        async Task ShutdownAsync()
        {
            await quit.Task.ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task AnInvalidProcessIdentityIsRejected()
    {
        var name = PipeName();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var request = TrayActivation.StopExistingAsync(name, TestContext.Current.CancellationToken);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var command = new byte[1];
        await server.ReadExactlyAsync(command, TestContext.Current.CancellationToken);
        Assert.Equal(2, command[0]);
        var response = new byte[13];
        response[0] = 1;
        await server.WriteAsync(response, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsWindows())
        {
            // The Windows control protocol requires the peer to consume the [1] reply
            // acknowledgement; otherwise the client can block before validating the identity.
            var acknowledgement = new byte[1];
            await server.ReadExactlyAsync(acknowledgement, TestContext.Current.CancellationToken);
            Assert.Equal(1, acknowledgement[0]);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => request);
    }

    [Fact]
    public async Task ExitWaitNeverTargetsAReusedProcessId()
    {
        using var process = Process.GetCurrentProcess();
        var identity = new TrayProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks + 1);
        await identity.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(process.HasExited);
    }

    private static void UnexpectedStop() => throw new InvalidOperationException("Unexpected stop request.");

    // macOS includes its long per-user temporary directory in the 104-byte socket limit.
    private static string PipeName() => $"at-{Guid.NewGuid():N}";

    private static NamedPipeClientStream ConnectClient(string name)
        => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
