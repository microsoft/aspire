// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Aspire.Tray.Tests;

public class WindowsTrayActivationTests
{
    public static bool SupportsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "The acknowledgement protects Windows pipe buffers.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public async Task StopWaitsForTheClientToConsumeTheProcessIdentityBeforeQuitting()
    {
        var name = $"at-{Guid.NewGuid():N}";
        var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new TrayActivation(name, _ => Task.CompletedTask,
            _ => Task.CompletedTask, () => quit.TrySetResult());
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await client.WriteAsync(new byte[] { 2 }, TestContext.Current.CancellationToken);
        var response = new byte[13];
        await client.ReadExactlyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal(1, response[0]);
        Assert.False(quit.Task.IsCompleted);

        await client.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
        await quit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact(Skip = "The acknowledgement protects Windows pipe buffers.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public async Task ShutdownCancelsAClientThatNeverAcknowledgesTheReply()
    {
        var name = $"at-{Guid.NewGuid():N}";
        var stopped = false;
        var server = new TrayActivation(name, _ => Task.CompletedTask,
            _ => Task.CompletedTask, () => stopped = true);
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            await client.WriteAsync(new byte[] { 2 }, TestContext.Current.CancellationToken);
            var response = new byte[13];
            await client.ReadExactlyAsync(response, TestContext.Current.CancellationToken);
            Assert.Equal(1, response[0]);
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            server = null;
            Assert.False(stopped);
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
        }
    }
}
