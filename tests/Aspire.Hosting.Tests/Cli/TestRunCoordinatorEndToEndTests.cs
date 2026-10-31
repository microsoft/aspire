// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Collections.Concurrent;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamJsonRpc;

namespace Aspire.Hosting.Tests;

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
        var process = exitCode is null
            ? builder.AddExecutable("tests", "aspire-test-command-that-does-not-exist", ".")
            : OperatingSystem.IsWindows()
                ? builder.AddExecutable("tests", "cmd.exe", ".", "/c", $"if not exist \"%TEST_OUTPUT_DIRECTORY%\\.\" exit /b 99 & exit /b {exitCode}")
                : builder.AddExecutable("tests", "/bin/sh", ".", "-c", $"test -d \"$TEST_OUTPUT_DIRECTORY\" || exit 99; exit {exitCode}");
        var configured = false;
        process.WithTestRun(context =>
        {
            Assert.Equal(outputDirectory.FullName, context.OutputPath);
            Assert.True(Directory.Exists(context.OutputPath));
            process.WithEnvironment("TEST_OUTPUT_DIRECTORY", context.OutputPath);
            configured = true;
            return Task.CompletedTask;
        });
        var remaining = builder.AddParameter("remaining", "value").WithTestRun();
        await using var app = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await app.StartAsync(timeout.Token);
        Assert.True(configured);
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
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        await rpc.InvokeWithCancellationAsync("RequestStopAsync", [], timeout.Token);
        await app.StopAsync(timeout.Token);
        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
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
