// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Collections.Concurrent;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamJsonRpc;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIRETESTING001 // Tests the experimental testing API.

[Trait("Partition", "4")]
public class TestRunCoordinatorEndToEndTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(null)]
    public async Task BackchannelWaitsForAllTestResourcesAndStopsOnlyOnRequest(int? exitCode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = workspace.CreateDirectory("test-output");
        SocketPermissionHelper.CreateDirectory(outputDirectory.FullName, repairExisting: true);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = true, outputHelper);
        builder.Configuration[KnownConfigNames.UnixSocketPath] = UnixSocketHelper.GetBackchannelSocketPath();
        builder.Configuration[KnownConfigNames.TestOutputPath] = outputDirectory.FullName;
        var ready = new TaskCompletionSource<BackchannelReadyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Eventing.Subscribe<BackchannelReadyEvent>((e, _) =>
        {
            ready.TrySetResult(e);
            return Task.CompletedTask;
        });
        var runnerOutput = workspace.CreateDirectory("runner-output");
        var process = exitCode is null
            ? builder.AddExecutable("tests", "aspire-test-command-that-does-not-exist", ".")
            : OperatingSystem.IsWindows()
                ? builder.AddExecutable("tests", "cmd.exe", ".", "/c", $"if not exist \"%TEST_OUTPUT_DIRECTORY%\\.\" exit /b 99 & exit /b {exitCode}")
                : builder.AddExecutable("tests", "/bin/sh", ".", "-c", $"test -d \"$TEST_OUTPUT_DIRECTORY\" || exit 99; exit {exitCode}");
        process.WithEnvironment("TEST_OUTPUT_DIRECTORY", runnerOutput.FullName);
        var collectorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.WithTesting(async context =>
        {
            Assert.Equal(outputDirectory.FullName, context.RunContext.OutputPath);
            Assert.True(Directory.Exists(context.OutputPath));
            collectorStarted.TrySetResult();
            await context.WaitForCompletionAsync();
            context.PublishResult(new TestResult { Name = "fixture result", Outcome = TestOutcome.Passed });
        });
        Assert.False(collectorStarted.Task.IsCompleted);
        var remaining = builder.AddParameter("remaining", "value").WithTesting();
        await using var app = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await app.StartAsync(timeout.Token);
        await collectorStarted.Task.WaitAsync(timeout.Token);
        var backchannel = await ready.Task.WaitAsync(timeout.Token);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(backchannel.SocketPath), timeout.Token);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var rpc = JsonRpc.Attach(stream);
        var capabilities = await rpc.InvokeWithCancellationAsync<string[]>("GetCapabilitiesAsync", [], timeout.Token);
        Assert.Contains("test-lifetime.v1", capabilities);
        Assert.Contains("test-reporting.v1", capabilities);
        var streamEvents = new ConcurrentQueue<TestRunEvent>();
        var completion = ReadTestStreamAsync(rpc, streamEvents, timeout.Token);

        await app.ResourceNotifications.WaitForResourceAsync(process.Resource.Name,
            e => KnownResourceStates.TerminalStates.Contains(e.Snapshot.State?.Text) &&
                (exitCode is null || e.Snapshot.ExitCode is not null),
            timeout.Token);
        Assert.False(completion.IsCompleted);
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        await app.ResourceNotifications.PublishUpdateAsync(remaining.Resource,
            s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });

        Assert.Equal(exitCode == 0, await completion.WaitAsync(timeout.Token));
        Assert.Contains(streamEvents, e => e.Resource is { ResourceName: "tests", Outcome: "passed" or "failed" });
        Assert.Contains(streamEvents, e => e.Resource is { ResourceName: "remaining", Outcome: "passed" });
        Assert.Contains(streamEvents, e => e.Result is { ResourceName: "tests", Name: "fixture result", Outcome: "passed" });
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        await rpc.InvokeWithCancellationAsync("RequestStopAsync", [], timeout.Token);
        await app.StopAsync(timeout.Token);
        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingBackchannelWatchCancelsCollectorWaitEvenWithExplicitToken(bool supplyCallerToken)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = workspace.CreateDirectory("test-output");
        SocketPermissionHelper.CreateDirectory(outputDirectory.FullName, repairExisting: true);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = true, outputHelper);
        builder.Configuration[KnownConfigNames.UnixSocketPath] = UnixSocketHelper.GetBackchannelSocketPath();
        builder.Configuration[KnownConfigNames.TestOutputPath] = outputDirectory.FullName;
        var ready = new TaskCompletionSource<BackchannelReadyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Eventing.Subscribe<BackchannelReadyEvent>((e, _) =>
        {
            ready.TrySetResult(e);
            return Task.CompletedTask;
        });
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.AddParameter("tests", "value").WithTesting(async context =>
        {
            started.TrySetResult();
            try
            {
                await context.WaitForCompletionAsync(supplyCallerToken ? caller.Token : default);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        });
        await using var app = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await app.StartAsync(timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        var backchannel = await ready.Task.WaitAsync(timeout.Token);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(backchannel.SocketPath), timeout.Token);
        using var network = new NetworkStream(socket, ownsSocket: false);
        using var rpc = JsonRpc.Attach(network);
        var events = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<TestRunEvent>>("WatchTestRunAsync", [], timeout.Token);
        var enumerator = events.GetAsyncEnumerator(timeout.Token);
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
        }
        finally
        {
            // CLI deadline/interruption closes this stream before its bounded AppHost teardown.
            await enumerator.DisposeAsync();
        }

        await cancelled.Task.WaitAsync(timeout.Token);
        Assert.False(caller.IsCancellationRequested);
        Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await app.StopAsync(timeout.Token);
    }

    private static async Task<bool> ReadTestStreamAsync(JsonRpc rpc, ConcurrentQueue<TestRunEvent> events, CancellationToken cancellationToken)
    {
        var stream = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<TestRunEvent>>("WatchTestRunAsync", [], cancellationToken);
        bool? passed = null;
        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            events.Enqueue(item);
            passed ??= item.Passed;
        }

        return passed ?? throw new InvalidOperationException("The test stream did not include completion.");
    }
}
