// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Semver;
using Xunit;
using DashboardResources = Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardClientTests(ITestOutputHelper testOutputHelper) : IDisposable
{
    [Fact]
    public async Task TerminalStream_ProxySendsEndedMessageBeforeClosingWebSocket()
    {
        var channel = Channel.CreateUnbounded<TerminalServerFrame>();
        channel.Writer.TryWrite(new TerminalServerFrame { Ended = true });
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var call = new AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame>(
            new ClientStreamWriter<TerminalClientFrame>(),
            new AsyncStreamReader<TerminalServerFrame>(channel: channel.Reader),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => disposed.TrySetResult());
        using var stream = new GrpcTerminalClientStream(call, "terminal");
        var dashboardClient = new TestDashboardClient(attachTerminal: (_, _) => Task.FromResult<Stream>(stream));
        using var server = new TestServer(new WebHostBuilder().Configure(app =>
        {
            app.UseWebSockets();
            app.Run(context =>
            {
                context.Request.Scheme = "https";
                context.Request.Host = new HostString("dashboard.example.com");
                return TerminalWebSocketProxy.HandleAppHostTerminalAsync(context, dashboardClient, NullLogger.Instance, "test");
            });
        }));
        var client = server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Origin = "https://dashboard.example.com";
        using var socket = await client.ConnectAsync(
            new Uri("wss://dashboard.example.com/api/apphost-terminal?terminalId=terminal"), CancellationToken.None).DefaultTimeout();
        var buffer = new byte[64];

        var message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).DefaultTimeout();
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        Assert.True(message.EndOfMessage);
        Assert.Equal("terminal-ended", Encoding.UTF8.GetString(buffer, 0, message.Count));
        await disposed.Task.DefaultTimeout();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalStream_EndedFrameIsDistinctFromTransportEof(bool ended)
    {
        using var call = new AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame>(
            new ClientStreamWriter<TerminalClientFrame>(),
            new AsyncStreamReader<TerminalServerFrame>(
            [
                new TerminalServerFrame(),
                new TerminalServerFrame { Data = ByteString.CopyFromUtf8("output") },
                new TerminalServerFrame { Ended = ended }
            ]),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
        using var stream = new GrpcTerminalClientStream(call, "terminal");
        var buffer = new byte[3];

        Assert.Equal(3, await stream.ReadAsync(buffer));
        Assert.Equal("out"u8.ToArray(), buffer);
        Assert.False(stream.TerminalEnded);
        Assert.Equal(3, await stream.ReadAsync(buffer));
        Assert.Equal("put"u8.ToArray(), buffer);
        Assert.False(stream.TerminalEnded);
        Assert.Equal(0, await stream.ReadAsync(buffer));
        Assert.Equal(ended, stream.TerminalEnded);
        Assert.Equal(0, await stream.ReadAsync(buffer));
    }

    [Fact]
    public async Task TerminalStream_EndedBeforeHandshakeDoesNotWaitForMoreFrames()
    {
        var channel = Channel.CreateUnbounded<TerminalServerFrame>();
        channel.Writer.TryWrite(new TerminalServerFrame { Ended = true });
        using var call = new AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame>(
            new ClientStreamWriter<TerminalClientFrame>(),
            new AsyncStreamReader<TerminalServerFrame>(channel: channel.Reader),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
        using var stream = new GrpcTerminalClientStream(call, "terminal");

        // The server keeps the RPC open until the proxy consumes this status and disconnects.
        Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().DefaultTimeout());
        Assert.True(stream.TerminalEnded);
    }

    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddXunit(testOutputHelper, LogLevel.Trace, DateTimeOffset.UtcNow);
        builder.SetMinimumLevel(LogLevel.Trace);
    });

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
    public async Task SubscribeConsoleLogs_ReceivesAndPersistsLogs()
    {
        var repositoryWriter = new RecordingResourceRepositoryWriter();
        await using var instance = CreateResourceServiceClient(resourceRepositoryWriter: repositoryWriter);
        instance.SetDashboardServiceClient(new MockDashboardServiceClient
        {
            ConsoleLogUpdates =
            [
                new WatchResourceConsoleLogsUpdate
                {
                    LogLines =
                    {
                        new ConsoleLogLine { LineNumber = 1, Text = "Hello", IsStdErr = false }
                    }
                }
            ]
        });

        var batches = new List<IReadOnlyList<ResourceLogLine>>();
        await foreach (var batch in instance.SubscribeConsoleLogs("api", CancellationToken.None))
        {
            batches.Add(batch);
        }

        var line = Assert.Single(Assert.Single(batches));
        Assert.Equal(new ResourceLogLine(1, "Hello", false), line);
        var persistedLogs = Assert.Single(repositoryWriter.ConsoleLogs);
        Assert.Equal("api", persistedLogs.ResourceName);
        Assert.Equal("Hello", Assert.Single(persistedLogs.LogLines).Text);
        Assert.Equal("api", Assert.Single(repositoryWriter.LoadedConsoleLogs));
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
    public async Task WhenConnected_AmbientActivity_DoesNotFlowToConnection()
    {
        await using var instance = CreateResourceServiceClient();
        var serviceClient = new MockDashboardServiceClient();
        instance.SetDashboardServiceClient(serviceClient);

        using var activity = new Activity("request").Start();

        await instance.WhenConnected.DefaultTimeout();

        Assert.Null(serviceClient.ActivityOnGetApplicationInformation);
    }

    [Fact]
    public async Task WatchResources_ResponseCreatesActivity()
    {
        using var activitySource = new DashboardActivitySource();
        await using var instance = CreateResourceServiceClient(activitySource);
        instance.SetDashboardServiceClient(new MockDashboardServiceClient
        {
            ResourceUpdates =
            [
                new WatchResourcesUpdate { InitialData = new InitialResourceData() },
                new WatchResourcesUpdate { Changes = new WatchResourcesChanges() }
            ]
        });
        var activities = new ConcurrentQueue<Activity>();
        var activitiesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = ActivityListenerHelper.Create(activitySource.ActivitySource, onActivityStopped: activity =>
        {
            activities.Enqueue(activity);
            if (activities.Count == 2)
            {
                activitiesReceived.TrySetResult();
            }
        });

        _ = instance.WhenConnected;
        await activitiesReceived.Task.DefaultTimeout();
        await instance.DisposeAsync().DefaultTimeout();

        Assert.Collection(
            activities,
            activity => AssertActivity(activity, WatchResourcesUpdate.KindOneofCase.InitialData),
            activity => AssertActivity(activity, WatchResourcesUpdate.KindOneofCase.Changes));

        static void AssertActivity(Activity activity, WatchResourcesUpdate.KindOneofCase kind)
        {
            Assert.Equal(DashboardActivitySource.ActivitySourceName, activity.Source.Name);
            Assert.Equal("Process resource update", activity.OperationName);
            Assert.Equal(ActivityKind.Consumer, activity.Kind);
            Assert.Equal(kind.ToString(), activity.GetTagItem("aspire.dashboard.resource_update.type"));
        }
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
        _loggerFactory.AddProvider(new TestLoggerProvider(testSink));

        await using var instance = CreateResourceServiceClient();
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

    [Fact]
    public async Task ConnectWithRetry_UnsupportedDashboardVersion_SetsUnsupportedState()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient { MinDashboardVersion = "99.0.0" });

        IDashboardClient client = instance;
        var unsupportedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += state =>
        {
            if (state == DashboardConnectionState.Unsupported)
            {
                unsupportedTcs.TrySetResult();
            }
        };

        _ = client.WhenConnected;

        await unsupportedTcs.Task.DefaultTimeout();

        Assert.Equal(DashboardConnectionState.Unsupported, client.ConnectionState);
        Assert.False(client.WhenConnected.IsCompleted);
    }

    [Theory]
    [InlineData("13.5.0", "13.5.0", true)]
    [InlineData("13.5.0-dev", "13.5.0", true)]
    [InlineData("13.5.0-preview.1.26307.2", "13.5.0", true)]
    [InlineData("13.6.0", "13.5.0", true)]
    [InlineData("14.0.0", "13.5.0", true)]
    [InlineData("13.5.1", "13.5.0", true)]
    [InlineData("13.4.0", "13.5.0", false)]
    [InlineData("13.4.9", "13.5.0", false)]
    [InlineData("12.0.0", "13.5.0", false)]
    [InlineData("13.5.0-dev", "13.5.1", false)]
    [InlineData("13.5.0", null, true)]
    [InlineData("13.5.0", "", true)]
    [InlineData(null, "13.5.0", false)]
    [InlineData(null, null, true)]
    [InlineData(null, "", true)]
    public void IsDashboardVersionSufficient_ReturnsExpectedResult(string? dashboardVersion, string? requiredVersion, bool expected)
    {
        var dashboard = dashboardVersion is not null ? SemVersion.Parse(dashboardVersion, SemVersionStyles.Any) : null;

        var result = DashboardClient.IsDashboardVersionSufficient(dashboard, requiredVersion);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.0.0")]
    [InlineData("1.0.0")]
    public async Task ConnectWithRetry_CompatibleMinVersion_SetsConnectedState(string minDashboardVersion)
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient { MinDashboardVersion = minDashboardVersion });

        IDashboardClient client = instance;
        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += state =>
        {
            if (state == DashboardConnectionState.Connected)
            {
                connectedTcs.TrySetResult();
            }
        };

        _ = client.WhenConnected;

        await connectedTcs.Task.DefaultTimeout();

        Assert.Equal(DashboardConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_AppHostUnavailable_ReturnsClearFailure()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient { FailOnExecuteResourceCommand = true });

        var response = await instance.ExecuteResourceCommandAsync(
            "api",
            "Project",
            CreateCommand(),
            new ExecuteResourceCommandOptions(),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(Aspire.Dashboard.Model.ResourceCommandResponseKind.Failed, response.Kind);
        Assert.Equal("Localized:ResourceCommandAppHostDisconnected", response.Message);
        Assert.Equal(response.Message, response.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_ClientCancellation_ReturnsAppHostDisconnectedFailure()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient { CancelExecuteResourceCommandOnCallCancellation = true });

        var commandTask = instance.ExecuteResourceCommandAsync(
            "api",
            "Project",
            CreateCommand(),
            new ExecuteResourceCommandOptions(),
            CancellationToken.None);

        await instance.DisposeAsync().DefaultTimeout();

        var response = await commandTask.DefaultTimeout();

        Assert.Equal(Aspire.Dashboard.Model.ResourceCommandResponseKind.Failed, response.Kind);
        Assert.Equal("Localized:ResourceCommandAppHostDisconnected", response.Message);
        Assert.Equal(response.Message, response.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscribeTerminals_StreamEnds_ResubscribesWithSnapshot(bool failStream)
    {
        var first = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var second = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var disposed = Channel.CreateUnbounded<bool>();
        var subscriptions = 0;
        var service = new MockDashboardServiceClient
        {
            ResourceUpdatesChannel = Channel.CreateUnbounded<WatchResourcesUpdate>().Reader,
            TerminalUpdatesProvider = () => Interlocked.Increment(ref subscriptions) == 1 ? first.Reader : second.Reader,
            OnTerminalWatchDisposed = () => disposed.Writer.TryWrite(true)
        };
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(service);
        await using var updates = client.SubscribeTerminalsAsync(CancellationToken.None).GetAsyncEnumerator();

        var initial = new WatchTerminalsUpdate { Snapshot = new TerminalDescriptorList() };
        await first.Writer.WriteAsync(initial);
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Same(initial, updates.Current);

        var recovery = new WatchTerminalsUpdate
        {
            Snapshot = new TerminalDescriptorList
            {
                Terminals = { new TerminalDescriptor { TerminalId = "recovered", Title = "Recovered" } },
                ActivatedTerminalId = "recovered"
            }
        };
        await first.Writer.WriteAsync(recovery);
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Same(recovery, updates.Current);
        Assert.Equal(1, Volatile.Read(ref subscriptions));

        first.Writer.Complete(failStream ? new RpcException(new Status(StatusCode.Unavailable, "Disconnected")) : null);
        var replacement = new WatchTerminalsUpdate
        {
            Snapshot = new TerminalDescriptorList
            {
                Terminals = { new TerminalDescriptor { TerminalId = "replacement", Title = "Replacement" } }
            }
        };
        await second.Writer.WriteAsync(replacement);

        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Same(replacement, updates.Current);
        Assert.Equal(2, Volatile.Read(ref subscriptions));
        Assert.True(await disposed.Reader.ReadAsync().AsTask().DefaultTimeout());
        await updates.DisposeAsync().DefaultTimeout();
        Assert.True(await disposed.Reader.ReadAsync().AsTask().DefaultTimeout());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SubscribeTerminals_Cancellation_StopsActiveStreamOrRecovery(bool disposeClient, bool duringRecovery)
    {
        var channel = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var streamDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MockDashboardServiceClient
        {
            ResourceUpdatesChannel = Channel.CreateUnbounded<WatchResourcesUpdate>().Reader,
            TerminalUpdatesProvider = () => channel.Reader,
            OnTerminalWatchDisposed = () => streamDisposed.TrySetResult()
        };
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(service);
        using var cts = new CancellationTokenSource();
        await using var updates = client.SubscribeTerminalsAsync(cts.Token).GetAsyncEnumerator();
        await channel.Writer.WriteAsync(new WatchTerminalsUpdate { Snapshot = new TerminalDescriptorList() });
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());

        var next = updates.MoveNextAsync().AsTask();
        if (duringRecovery)
        {
            channel.Writer.Complete();
            await streamDisposed.Task.DefaultTimeout();
        }

        if (disposeClient)
        {
            await client.DisposeAsync().DefaultTimeout();
        }
        else
        {
            await cts.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next).DefaultTimeout();
        await streamDisposed.Task.DefaultTimeout();
    }

    [Fact]
    public async Task SubscribeTerminals_Cancellation_StopsConnectionWait()
    {
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(new MockDashboardServiceClient { FailOnGetApplicationInformation = true });
        using var cts = new CancellationTokenSource();
        await using var updates = client.SubscribeTerminalsAsync(cts.Token).GetAsyncEnumerator();

        var next = updates.MoveNextAsync().AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next).DefaultTimeout();
    }

    [Fact]
    public async Task SubscribeTerminals_Unimplemented_CompletesWithoutRetry()
    {
        var channel = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        channel.Writer.Complete(new RpcException(new Status(StatusCode.Unimplemented, "Older AppHost")));
        var subscriptions = 0;
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(new MockDashboardServiceClient
        {
            ResourceUpdatesChannel = Channel.CreateUnbounded<WatchResourcesUpdate>().Reader,
            TerminalUpdatesProvider = () =>
            {
                Interlocked.Increment(ref subscriptions);
                return channel.Reader;
            }
        });
        await using var updates = client.SubscribeTerminalsAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.False(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(1, Volatile.Read(ref subscriptions));
    }

    private sealed class MockDashboardServiceClient : Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient
    {
        public bool FailOnWatchResources { get; init; }
        public bool FailOnGetApplicationInformation { get; init; }
        public bool FailOnExecuteResourceCommand { get; init; }
        public bool CancelExecuteResourceCommandOnCallCancellation { get; init; }
        public string MinDashboardVersion { get; init; } = "";
        public IReadOnlyList<WatchResourceConsoleLogsUpdate> ConsoleLogUpdates { get; init; } = [];
        public IReadOnlyList<WatchResourcesUpdate> ResourceUpdates { get; init; } = [];
        public ChannelReader<WatchResourcesUpdate>? ResourceUpdatesChannel { get; init; }
        public Func<ChannelReader<WatchTerminalsUpdate>>? TerminalUpdatesProvider { get; init; }
        public Action? OnTerminalWatchDisposed { get; init; }
        public Activity? ActivityOnGetApplicationInformation { get; private set; }
        private int _resourceUpdatesReturned;

        public override AsyncServerStreamingCall<WatchTerminalsUpdate> WatchTerminals(WatchTerminalsRequest request, CallOptions options)
        {
            return new AsyncServerStreamingCall<WatchTerminalsUpdate>(
                new AsyncStreamReader<WatchTerminalsUpdate>(channel: TerminalUpdatesProvider?.Invoke()),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => OnTerminalWatchDisposed?.Invoke());
        }

        public override AsyncServerStreamingCall<WatchResourceConsoleLogsUpdate> WatchResourceConsoleLogs(WatchResourceConsoleLogsRequest request, CallOptions options)
        {
            return new AsyncServerStreamingCall<WatchResourceConsoleLogsUpdate>(
                new AsyncStreamReader<WatchResourceConsoleLogsUpdate>(ConsoleLogUpdates),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

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
            ActivityOnGetApplicationInformation = Activity.Current;
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
                    ApplicationName = "TestApplication",
                    MinDashboardVersion = MinDashboardVersion
                }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncUnaryCall<ResourceCommandResponse> ExecuteResourceCommandAsync(ResourceCommandRequest request, CallOptions options)
        {
            if (CancelExecuteResourceCommandOnCallCancellation)
            {
                return new AsyncUnaryCall<ResourceCommandResponse>(
                    WaitForCallCancellationAsync(options.CancellationToken),
                    Task.FromResult(new Metadata()),
                    () => new Status(StatusCode.Cancelled, "Cancelled"),
                    () => new Metadata(),
                    () => { });
            }

            if (FailOnExecuteResourceCommand)
            {
                return new AsyncUnaryCall<ResourceCommandResponse>(
                    Task.FromException<ResourceCommandResponse>(new RpcException(new Status(StatusCode.Unavailable, "Service unavailable"))),
                    Task.FromResult(new Metadata()),
                    () => new Status(StatusCode.Unavailable, "Service unavailable"),
                    () => new Metadata(),
                    () => { });
            }

            return new AsyncUnaryCall<ResourceCommandResponse>(
                Task.FromResult(new ResourceCommandResponse
                {
                    Kind = Aspire.DashboardService.Proto.V1.ResourceCommandResponseKind.Succeeded
                }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        private static async Task<ResourceCommandResponse> WaitForCallCancellationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "Cancelled"));
            }

            throw new InvalidOperationException("The command should only complete when the call is canceled.");
        }

        public override AsyncServerStreamingCall<WatchResourcesUpdate> WatchResources(WatchResourcesRequest request, CallOptions options)
        {
            var reader = FailOnWatchResources
                ? (IAsyncStreamReader<WatchResourcesUpdate>)new FailingAsyncStreamReader<WatchResourcesUpdate>()
                : new AsyncStreamReader<WatchResourcesUpdate>(
                    Interlocked.Exchange(ref _resourceUpdatesReturned, 1) == 0 ? ResourceUpdates : [],
                    ResourceUpdatesChannel);

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
        private readonly Queue<T> _items;
        private readonly ChannelReader<T>? _channel;

        public AsyncStreamReader(IEnumerable<T>? items = null, ChannelReader<T>? channel = null)
        {
            _items = new Queue<T>(items ?? []);
            _channel = channel;
        }

        public T Current { get; private set; } = default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_items.TryDequeue(out var item))
            {
                Current = item;
                return true;
            }

            if (_channel is { } channel)
            {
                while (await channel.WaitToReadAsync(cancellationToken))
                {
                    if (channel.TryRead(out var update))
                    {
                        Current = update;
                        return true;
                    }
                }
            }

            return false;
        }
    }

    private sealed class RecordingResourceRepositoryWriter : IResourceRepositoryWriter
    {
        public List<(string ResourceName, IReadOnlyList<ConsoleLogLine> LogLines)> ConsoleLogs { get; } = [];
        public List<string> LoadedConsoleLogs { get; } = [];

        public Task ReplaceResourcesAsync(IReadOnlyList<Resource> resources)
        {
            return Task.CompletedTask;
        }

        public Task ApplyChangesAsync(IReadOnlyList<WatchResourcesChange> changes)
        {
            return Task.CompletedTask;
        }

        public Task MarkConsoleLogsLoadedAsync(string resourceName)
        {
            LoadedConsoleLogs.Add(resourceName);
            return Task.CompletedTask;
        }

        public Task AddConsoleLogsAsync(string resourceName, IReadOnlyList<ConsoleLogLine> logLines)
        {
            ConsoleLogs.Add((resourceName, logLines));
            return Task.CompletedTask;
        }

        public Task ClearConsoleLogsAsync(IReadOnlyList<string> resourceNames, DateTime clearDate) => Task.CompletedTask;
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

    private DashboardClient CreateResourceServiceClient(
        DashboardActivitySource? activitySource = null,
        IResourceRepositoryWriter? resourceRepositoryWriter = null)
    {
        return CreateResourceServiceClient(_loggerFactory, activitySource, resourceRepositoryWriter);
    }

    private static DashboardClient CreateResourceServiceClient(
        ILoggerFactory loggerFactory,
        DashboardActivitySource? activitySource,
        IResourceRepositoryWriter? resourceRepositoryWriter)
    {
        var options = new DashboardOptions
        {
            ResourceServiceClient =
            {
                AuthMode = ResourceClientAuthMode.Unsecured,
                Url = "http://localhost:12345"
            }
        };
        options.ResourceServiceClient.TryParseOptions(out _);

        return new DashboardClient(
            activitySource ?? new DashboardActivitySource(),
            loggerFactory,
            new ConfigurationManager(),
            Options.Create(options),
            new MockKnownPropertyLookup(),
            new TestStringLocalizer<DashboardResources>(),
            resourceRepositoryWriter: resourceRepositoryWriter ?? new RecordingResourceRepositoryWriter());
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private static CommandViewModel CreateCommand()
    {
        return new CommandViewModel(
            "restart",
            CommandViewModelState.Enabled,
            "Restart",
            "Restart API",
            confirmationMessage: string.Empty,
            [],
            isHighlighted: false,
            iconName: string.Empty,
            iconVariant: Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular);
    }
}
