// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardClientTests
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<DashboardOptions> _dashboardOptions;

    public DashboardClientTests()
    {
        _configuration = new ConfigurationManager();

        var options = new DashboardOptions
        {
            ResourceServiceClient =
            {
                AuthMode = ResourceClientAuthMode.Unsecured,
                Url = "http://localhost:12345"
            }
        };
        options.ResourceServiceClient.TryParseOptions(out _);

        _dashboardOptions = Options.Create(options);
    }

    [Fact]
    public async Task SubscribeResources_OnCancel_ChannelRemoved()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetInitialDataReceived();

        IDashboardClient client = instance;

        var cts = new CancellationTokenSource();

        Assert.Equal(0, instance.OutgoingResourceSubscriberCount);

        var (_, subscription) = await client.SubscribeResourcesAsync(CancellationToken.None).DefaultTimeout();

        Assert.Equal(1, instance.OutgoingResourceSubscriberCount);

        var readTask = Task.Run(async () =>
        {
            await foreach (var item in subscription.WithCancellation(cts.Token))
            {
            }
        });

        cts.Cancel();

        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();

        Assert.Equal(0, instance.OutgoingResourceSubscriberCount);
    }

    [Fact]
    public async Task SubscribeResources_OnDispose_ChannelRemoved()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetInitialDataReceived();

        IDashboardClient client = instance;

        Assert.Equal(0, instance.OutgoingResourceSubscriberCount);

        var (_, subscription) = await client.SubscribeResourcesAsync(CancellationToken.None).DefaultTimeout();

        Assert.Equal(1, instance.OutgoingResourceSubscriberCount);

        var readTask = Task.Run(async () =>
        {
            await foreach (var item in subscription)
            {
            }
        });

        await instance.DisposeAsync().DefaultTimeout();

        Assert.Equal(0, instance.OutgoingResourceSubscriberCount);

        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();
    }

    [Fact]
    public async Task SubscribeResources_ThrowsIfDisposed()
    {
        await using IDashboardClient client = CreateResourceServiceClient();

        await client.DisposeAsync().DefaultTimeout();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SubscribeResourcesAsync(CancellationToken.None)).DefaultTimeout();
    }

    [Fact]
    public async Task SubscribeResources_IncreasesSubscriberCount()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetInitialDataReceived();

        IDashboardClient client = instance;

        Assert.Equal(0, instance.OutgoingResourceSubscriberCount);

        _ = await client.SubscribeResourcesAsync(CancellationToken.None).DefaultTimeout();

        Assert.Equal(1, instance.OutgoingResourceSubscriberCount);

        await instance.DisposeAsync().DefaultTimeout();

        Assert.Equal(0, instance.OutgoingResourceSubscriberCount);
    }

    [Fact]
    public async Task SubscribeResources_HasInitialData_InitialDataReturned()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;

        var cts = new CancellationTokenSource();

        var subscribeTask = client.SubscribeResourcesAsync(CancellationToken.None);

        Assert.False(subscribeTask.IsCompleted);
        Assert.Equal(0, instance.OutgoingResourceSubscriberCount);

        instance.SetInitialDataReceived([new Resource
        {
            Name = "test",
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }]);

        var (initialData, subscription) = await subscribeTask.DefaultTimeout();

        Assert.Single(initialData);
    }

    [Fact]
    public async Task SubscribeInteractions_OnCancel_ChannelRemoved()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;

        var cts = new CancellationTokenSource();

        Assert.Equal(0, instance.OutgoingInteractionSubscriberCount);

        var subscription = client.SubscribeInteractionsAsync(CancellationToken.None);

        Assert.Equal(1, instance.OutgoingInteractionSubscriberCount);

        var readTask = Task.Run(async () =>
        {
            await foreach (var item in subscription.WithCancellation(cts.Token))
            {
            }
        });

        cts.Cancel();

        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();

        Assert.Equal(0, instance.OutgoingInteractionSubscriberCount);
    }

    [Fact]
    public async Task SubscribeInteractions_OnDispose_ChannelRemoved()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;

        Assert.Equal(0, instance.OutgoingInteractionSubscriberCount);

        var subscription = client.SubscribeInteractionsAsync(CancellationToken.None);

        Assert.Equal(1, instance.OutgoingInteractionSubscriberCount);

        var readTask = Task.Run(async () =>
        {
            await foreach (var item in subscription)
            {
            }
        });

        await instance.DisposeAsync().DefaultTimeout();

        Assert.Equal(0, instance.OutgoingInteractionSubscriberCount);

        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();
    }

    [Fact]
    public async Task SubscribeInteractions_ThrowsIfDisposed()
    {
        await using IDashboardClient client = CreateResourceServiceClient();

        await client.DisposeAsync().DefaultTimeout();

        Assert.Throws<ObjectDisposedException>(() => client.SubscribeInteractionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SubscribeInteractions_IncreasesSubscriberCount()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;

        Assert.Equal(0, instance.OutgoingInteractionSubscriberCount);

        _ = client.SubscribeInteractionsAsync(CancellationToken.None);

        Assert.Equal(1, instance.OutgoingInteractionSubscriberCount);

        await instance.DisposeAsync().DefaultTimeout();

        Assert.Equal(0, instance.OutgoingInteractionSubscriberCount);
    }

    [Fact]
    public async Task WhenConnected_InteractionMethodUnimplemented_InteractionWatchCompleted()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient());

        await instance.WhenConnected.DefaultTimeout();

        await instance.InteractionWatchCompleteTask.DefaultTimeout();
    }

    [Fact]
    public async Task ConnectionState_InitialState_IsConnecting()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;

        Assert.Equal(DashboardConnectionState.Connecting, client.ConnectionState);
    }

    [Fact]
    public async Task ConnectionState_SetConnected_FiresEvent()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;
        var stateChanges = new List<DashboardConnectionState>();
        client.ConnectionStateChanged += stateChanges.Add;

        instance.SetConnectionStateForTesting(DashboardConnectionState.Connected);

        Assert.Equal(DashboardConnectionState.Connected, client.ConnectionState);
        Assert.Single(stateChanges);
        Assert.Equal(DashboardConnectionState.Connected, stateChanges[0]);
    }

    [Fact]
    public async Task ConnectionState_DuplicateState_DoesNotFireEvent()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;
        var stateChanges = new List<DashboardConnectionState>();
        client.ConnectionStateChanged += stateChanges.Add;

        instance.SetConnectionStateForTesting(DashboardConnectionState.Connected);
        instance.SetConnectionStateForTesting(DashboardConnectionState.Connected);

        Assert.Single(stateChanges);
    }

    [Fact]
    public async Task ConnectionState_DisconnectedResetsWhenConnected()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;
        var stateChanges = new List<DashboardConnectionState>();
        client.ConnectionStateChanged += stateChanges.Add;

        // Transition through Connected then to Disconnected.
        instance.SetConnectionStateForTesting(DashboardConnectionState.Connected);
        instance.SetConnectionStateForTesting(DashboardConnectionState.Disconnected);

        Assert.Equal(DashboardConnectionState.Disconnected, client.ConnectionState);
        Assert.Collection(stateChanges,
            s => Assert.Equal(DashboardConnectionState.Connected, s),
            s => Assert.Equal(DashboardConnectionState.Disconnected, s));
    }

    [Fact]
    public async Task ReconnectAsync_CancelsDelay()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;

        // ReconnectAsync should not throw even when there's no active delay.
        await client.ReconnectAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ConnectionState_ConcurrentSetSameState_FiresEventOnce()
    {
        await using var instance = CreateResourceServiceClient();

        IDashboardClient client = instance;
        var eventCount = 0;
        client.ConnectionStateChanged += _ => Interlocked.Increment(ref eventCount);

        // Simulate concurrent calls from both watch tasks transitioning to Disconnected.
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            instance.SetConnectionStateForTesting(DashboardConnectionState.Disconnected);
        }));
        await Task.WhenAll(tasks).DefaultTimeout();

        // The event should fire exactly once because the lock prevents duplicate transitions.
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public async Task WatchWithRecovery_RepeatedFailures_FiresMultipleDisconnectedEvents()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient { FailOnWatchResources = true });

        IDashboardClient client = instance;
        var disconnectedCount = 0;
        var disconnectedSemaphore = new SemaphoreSlim(0);
        client.ConnectionStateChanged += state =>
        {
            if (state == DashboardConnectionState.Disconnected)
            {
                Interlocked.Increment(ref disconnectedCount);
                disconnectedSemaphore.Release();
            }
        };

        // Trigger the connection. ConnectWithRetryAsync succeeds, then WatchResources starts failing.
        await instance.WhenConnected.DefaultTimeout();

        // Wait for at least 3 Disconnected events to prove each retry fires a new event.
        // Without the Connecting transition between retries, only 1 Disconnected event would fire.
        for (var i = 0; i < 3; i++)
        {
            await disconnectedSemaphore.WaitAsync().DefaultTimeout();
        }

        Assert.True(disconnectedCount >= 3, $"Expected at least 3 Disconnected events but got {disconnectedCount}.");
    }

    [Fact]
    public async Task ConnectWithRetry_LogsErrorWithTroubleshootingLink()
    {
        var testSink = new TestSink();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(testSink)));

        await using var instance = new DashboardClient(loggerFactory, _configuration, _dashboardOptions, new MockKnownPropertyLookup());
        instance.SetDashboardServiceClient(new MockDashboardServiceClient { FailOnGetApplicationInformation = true });

        IDashboardClient client = instance;
        var disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += state =>
        {
            if (state == DashboardConnectionState.Disconnected)
            {
                disconnectedTcs.TrySetResult();
            }
        };

        // Trigger the connection attempt which will fail on GetApplicationInformationAsync.
        _ = client.WhenConnected;

        // Wait for the first Disconnected event which means the error has been logged.
        await disconnectedTcs.Task.DefaultTimeout();

        var errorLog = testSink.Writes.FirstOrDefault(w => w.LogLevel == LogLevel.Error);
        Assert.NotNull(errorLog);
        Assert.Contains("https://aka.ms/aspire/dashboard-apphost-connection-failed", errorLog.Message);
    }

    private sealed class MockDashboardServiceClient : Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient
    {
        public bool FailOnWatchResources { get; init; }
        public bool FailOnGetApplicationInformation { get; init; }

        public override AsyncDuplexStreamingCall<WatchInteractionsRequestUpdate, WatchInteractionsResponseUpdate> WatchInteractions(CallOptions options)
        {
            return new AsyncDuplexStreamingCall<WatchInteractionsRequestUpdate, WatchInteractionsResponseUpdate>(
                new ClientStreamWriter<WatchInteractionsRequestUpdate>(),
                new AsyncStreamReader<WatchInteractionsResponseUpdate>(),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.Unimplemented, "Unimplemented!"),
                () => new Metadata(),
                () => { });
        }

        public override AsyncUnaryCall<ApplicationInformationResponse> GetApplicationInformationAsync(ApplicationInformationRequest request, CallOptions options)
        {
            if (FailOnGetApplicationInformation)
            {
                return new AsyncUnaryCall<ApplicationInformationResponse>(
                    Task.FromException<ApplicationInformationResponse>(new RpcException(new Status(StatusCode.Unavailable, "Service unavailable"))),
                    Task.FromResult(new Metadata()),
                    () => new Status(StatusCode.Unavailable, "Service unavailable"),
                    () => new Metadata(),
                    () => { });
            }

            return new AsyncUnaryCall<ApplicationInformationResponse>(
                Task.FromResult(new ApplicationInformationResponse
                {
                    ApplicationName = "TestApplication"
                }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<WatchResourcesUpdate> WatchResources(WatchResourcesRequest request, CallOptions options)
        {
            var reader = FailOnWatchResources
                ? (IAsyncStreamReader<WatchResourcesUpdate>)new FailingAsyncStreamReader<WatchResourcesUpdate>()
                : new AsyncStreamReader<WatchResourcesUpdate>();

            return new AsyncServerStreamingCall<WatchResourcesUpdate>(
                reader,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    private sealed class FailingAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        public T Current { get; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Service unavailable"));
        }
    }

    private sealed class AsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        public T Current { get; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class ClientStreamWriter<T> : IClientStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task CompleteAsync()
        {
            throw new NotImplementedException();
        }

        public Task WriteAsync(T message)
        {
            throw new NotImplementedException();
        }
    }

    private DashboardClient CreateResourceServiceClient()
    {
        return new DashboardClient(NullLoggerFactory.Instance, _configuration, _dashboardOptions, new MockKnownPropertyLookup());
    }
}
