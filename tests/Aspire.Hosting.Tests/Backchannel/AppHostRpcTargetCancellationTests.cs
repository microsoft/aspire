// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Tests that cancellation of streaming RPC methods on <see cref="AppHostRpcTarget"/> completes
/// the stream gracefully (i.e. no <see cref="OperationCanceledException"/> propagates to callers).
/// </summary>
[Trait("Partition", "4")]
public class AppHostRpcTargetCancellationTests(ITestOutputHelper outputHelper)
{
    // -------------------------------------------------------------------------
    // GetAppHostLogEntriesAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAppHostLogEntriesAsync_WhenOuterTokenAlreadyCancelled_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Iterating with an already-cancelled token should complete without throwing.
        await foreach (var _ in target.GetAppHostLogEntriesAsync(cts.Token))
        {
        }

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task GetAppHostLogEntriesAsync_WhenOuterTokenCancelledDuringIteration_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        using var cts = new CancellationTokenSource();

        // Start iterating; cancel the token after starting but let it complete normally.
        var iterationTask = Task.Run(async () =>
        {
            await foreach (var _ in target.GetAppHostLogEntriesAsync(cts.Token))
            {
            }
        });

        await cts.CancelAsync();

        // Should finish without exception within a short timeout.
        await iterationTask.WaitAsync(TimeSpan.FromSeconds(10));

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task GetAppHostLogEntriesAsync_WhenShutdownCtsCancelled_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var iterationTask = Task.Run(async () =>
        {
            await foreach (var _ in target.GetAppHostLogEntriesAsync(CancellationToken.None))
            {
            }
        });

        // Simulate the shutdown path (same as RequestStopAsync calling _shutdownCts.Cancel()).
        target.CancelInflightRpcCalls();

        await iterationTask.WaitAsync(TimeSpan.FromSeconds(10));

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    // -------------------------------------------------------------------------
    // GetPublishingActivitiesAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPublishingActivitiesAsync_WhenOuterTokenAlreadyCancelled_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await foreach (var _ in target.GetPublishingActivitiesAsync(cts.Token))
        {
        }

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task GetPublishingActivitiesAsync_WhenOuterTokenCancelledDuringIteration_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        using var cts = new CancellationTokenSource();

        var iterationTask = Task.Run(async () =>
        {
            await foreach (var _ in target.GetPublishingActivitiesAsync(cts.Token))
            {
            }
        });

        await cts.CancelAsync();

        await iterationTask.WaitAsync(TimeSpan.FromSeconds(10));

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task GetPublishingActivitiesAsync_WhenShutdownCtsCancelled_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var iterationTask = Task.Run(async () =>
        {
            await foreach (var _ in target.GetPublishingActivitiesAsync(CancellationToken.None))
            {
            }
        });

        target.CancelInflightRpcCalls();

        await iterationTask.WaitAsync(TimeSpan.FromSeconds(10));

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    // -------------------------------------------------------------------------
    // GetResourceStatesAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetResourceStatesAsync_WhenOuterTokenAlreadyCancelled_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await foreach (var _ in target.GetResourceStatesAsync(cts.Token))
        {
        }

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task GetResourceStatesAsync_WhenOuterTokenCancelledDuringIteration_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        using var cts = new CancellationTokenSource();

        var iterationTask = Task.Run(async () =>
        {
            await foreach (var _ in target.GetResourceStatesAsync(cts.Token))
            {
            }
        });

        await cts.CancelAsync();

        await iterationTask.WaitAsync(TimeSpan.FromSeconds(10));

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task GetResourceStatesAsync_WhenShutdownCtsCancelled_StreamCompletesWithoutException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var iterationTask = Task.Run(async () =>
        {
            await foreach (var _ in target.GetResourceStatesAsync(CancellationToken.None))
            {
            }
        });

        target.CancelInflightRpcCalls();

        await iterationTask.WaitAsync(TimeSpan.FromSeconds(10));

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    // -------------------------------------------------------------------------
    // BackchannelService — socket waiting when stoppingToken fires
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BackchannelService_WhenStoppingTokenFiredWhileWaitingForConnection_AppStopsCleanly()
    {
        // Configure a socket path but never connect a client, so AcceptAsync will be
        // waiting when the app host stops and fires the stoppingToken.
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration[KnownConfigNames.UnixSocketPath] = UnixSocketHelper.GetBackchannelSocketPath();

        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        // Stop without ever connecting — BackchannelService's AcceptAsync is still waiting.
        // Prior to the fix this would surface an unhandled OperationCanceledException;
        // after the fix it should stop cleanly.
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
}
