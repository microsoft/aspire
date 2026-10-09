// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelCliOutputTests
{
    [Fact]
    public async Task PumpDoesNotSynchronouslyReadAndDrainsThroughEndOfStream()
    {
        var pipe = new Pipe();
        using var reader = new StreamReader(new TestAsyncReadStream(pipe.Reader.AsStream()));
        var lines = new List<string>();
        var pump = DevTunnelCli.PumpAsync(reader, lines.Add, TestContext.Current.CancellationToken);
        Assert.False(pump.IsCompleted);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("first\r\n\nsecond\n"), TestContext.Current.CancellationToken);
        await pipe.Writer.CompleteAsync();
        await pump.DefaultTimeout();
        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public async Task SilentStandardOutputDoesNotBlockStandardError()
    {
        var stdout = new Pipe();
        var stderr = new Pipe();
        using var outputReader = new StreamReader(new TestAsyncReadStream(stdout.Reader.AsStream()));
        using var errorReader = new StreamReader(new TestAsyncReadStream(stderr.Reader.AsStream()));
        var errorLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputTask = DevTunnelCli.PumpAsync(outputReader, _ => Assert.Fail("No stdout was written."), TestContext.Current.CancellationToken);
        var errorTask = DevTunnelCli.PumpAsync(errorReader, line => errorLine.TrySetResult(line), TestContext.Current.CancellationToken);
        await stderr.Writer.WriteAsync(Encoding.UTF8.GetBytes("error\n"), TestContext.Current.CancellationToken);
        Assert.Equal("error", await errorLine.Task.DefaultTimeout());
        Assert.False(outputTask.IsCompleted);
        await stdout.Writer.CompleteAsync();
        await stderr.Writer.CompleteAsync();
        await Task.WhenAll(outputTask, errorTask).DefaultTimeout();
    }

    [Fact]
    public async Task PumpCancellationInterruptsPendingRead()
    {
        var pipe = new Pipe();
        using var reader = new StreamReader(new TestAsyncReadStream(pipe.Reader.AsStream()));
        using var cts = new CancellationTokenSource();
        var pump = DevTunnelCli.PumpAsync(reader, _ => Assert.Fail("No output was written."), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump).DefaultTimeout();
        await pipe.Writer.CompleteAsync();
    }
}
