// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Server;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeCliProtocolTests
{
    [Fact]
    public async Task ExistingRpcNamesNegotiateBeforeCompositionAndReportRealReadinessAndDashboardState()
    {
        var urls = new NativeCliDashboardUrls { BaseUrlWithLoginToken = "http://localhost:48123/login?t=test" };
        await using var host = NativeCliTestHost.Start(new NativeApplicationServer(), new(), () => urls);
        Assert.Equal(["baseline.v2"], await host.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
        Assert.False(host.IsReady);
        await host.Rpc.InvokeAsync("NotifyAppHostReadyAsync");
        Assert.True(host.IsReady);
        var dashboard = await host.Rpc.InvokeAsync<NativeCliDashboardUrls>("GetDashboardUrlsAsync");
        Assert.Equal(urls, dashboard);
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => host.Rpc.InvokeAsync("UnknownMethod"));
        // The stop response must arrive before the listener is cancelled.
        await host.Rpc.InvokeAsync("RequestStopAsync");
    }

    [Fact]
    public async Task ResourceStreamFollowsArbitraryTypesAndGenerationReplacement()
    {
        var server = new NativeApplicationServer();
        await using var host = NativeCliTestHost.Start(server, new(), () => new());
        var stream = await host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliResourceState>>("GetResourceStatesAsync");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var enumerator = stream.GetAsyncEnumerator(timeout.Token);
        var session = server.OpenApplication();
        var composition = session.StartGeneration();
        composition.AddResource("arbitrary-job", "third-party/BackgroundWorker");
        composition.CreateExecution();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("arbitrary-job", enumerator.Current.Resource);
        Assert.Equal("third-party/BackgroundWorker", enumerator.Current.Type);
        await session.RetireGeneration(composition);
        var replacement = session.StartGeneration();
        replacement.AddResource("another-job", "custom/Resource");
        replacement.CreateExecution();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("another-job", enumerator.Current.Resource);
        Assert.Equal("custom/Resource", enumerator.Current.Type);
        session.Close();
    }

    [Fact]
    public async Task DiagnosticLogStreamUsesTypedPayloadsAndConfiguredRetention()
    {
        var options = new NativeServerOptions { RetainedAppHostLogEntries = 2 };
        await using var host = NativeCliTestHost.Start(new NativeApplicationServer(), options, () => new());
        var generation = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            host.Logs.OnNext(new OperationEvent(Guid.NewGuid(), "test.operation", "failed", null, generation,
                null, null, null, DateTimeOffset.UtcNow, 0, "TestFailure"));
        }
        var stream = await host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliLogEntry>>("GetAppHostLogEntriesAsync");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var enumerator = stream.GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, enumerator.Current.SequenceNumber);
        Assert.Equal(LogLevel.Error, enumerator.Current.LogLevel);
        Assert.Equal("test.operation: failed (TestFailure)", enumerator.Current.Message);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(3, enumerator.Current.SequenceNumber);
        var pending = enumerator.MoveNextAsync().AsTask();
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(["baseline.v2"], await host.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
    }

    [Fact]
    public async Task StreamCapacityIsConfiguredAndAbortReleasesItsSlot()
    {
        await using var host = NativeCliTestHost.Start(new NativeApplicationServer(),
            new NativeServerOptions { MaximumCliStreams = 1 }, () => new());
        var resources = await host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliResourceState>>("GetResourceStatesAsync");
        await using (var enumerator = resources.GetAsyncEnumerator())
        {
            await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliLogEntry>>("GetAppHostLogEntriesAsync"));
        }
        var logs = await host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliLogEntry>>("GetAppHostLogEntriesAsync");
        await using var logEnumerator = logs.GetAsyncEnumerator();
    }
}
