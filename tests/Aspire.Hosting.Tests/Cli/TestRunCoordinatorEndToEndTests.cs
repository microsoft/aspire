// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
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
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = true, outputHelper);
        builder.Configuration[KnownConfigNames.UnixSocketPath] = UnixSocketHelper.GetBackchannelSocketPath();
        var ready = new TaskCompletionSource<BackchannelReadyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Eventing.Subscribe<BackchannelReadyEvent>((e, _) =>
        {
            ready.TrySetResult(e);
            return Task.CompletedTask;
        });
        var process = exitCode is null
            ? builder.AddExecutable("tests", "aspire-test-command-that-does-not-exist", ".")
            : OperatingSystem.IsWindows()
                ? builder.AddExecutable("tests", "cmd.exe", ".", "/c", $"exit /b {exitCode}")
                : builder.AddExecutable("tests", "/bin/sh", ".", "-c", $"exit {exitCode}");
        process.WithTestRun();
        var remaining = builder.AddParameter("remaining", "value").WithTestRun();
        await using var app = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await app.StartAsync(timeout.Token);
        var backchannel = await ready.Task.WaitAsync(timeout.Token);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(backchannel.SocketPath), timeout.Token);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var rpc = JsonRpc.Attach(stream);
        var capabilities = await rpc.InvokeWithCancellationAsync<string[]>("GetCapabilitiesAsync", [], timeout.Token);
        Assert.Contains("test-lifetime.v1", capabilities);
        var completion = rpc.InvokeWithCancellationAsync<bool>("WaitForTestCompletionAsync", [], timeout.Token);

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
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        await rpc.InvokeWithCancellationAsync("RequestStopAsync", [], timeout.Token);
        await app.StopAsync(timeout.Token);
        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
    }
}
