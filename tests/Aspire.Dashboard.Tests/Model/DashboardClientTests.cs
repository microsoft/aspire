// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task GetInteractionAssetAsync_Found_WritesContentAndSetsContentType()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient(assetResponses:
        [
            new GetInteractionAssetResponse { ContentType = "text/plain" },
            new GetInteractionAssetResponse { Content = ByteString.CopyFromUtf8("hello ") },
            new GetInteractionAssetResponse { Content = ByteString.CopyFromUtf8("world") }
        ]));

        using var asset = await instance.GetInteractionAssetAsync("assets/file.txt", CancellationToken.None);

        Assert.NotNull(asset);
        Assert.Equal("text/plain", asset.ContentType);

        using var stream = new MemoryStream();
        await asset.CopyToAsync(stream, CancellationToken.None);
        Assert.Equal("hello world", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task GetInteractionAssetAsync_NotFound_ReturnsNull()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient(assetNotFound: true));

        var asset = await instance.GetInteractionAssetAsync("assets/missing.txt", CancellationToken.None);

        Assert.Null(asset);
    }

    [Fact]
    public async Task StartPageInteractionAsync_Found_ReturnsResult()
    {
        await using var instance = CreateResourceServiceClient();
        StartPageInteractionRequest? capturedRequest = null;
        instance.SetDashboardServiceClient(new MockDashboardServiceClient(
            startPageResponse: new StartPageInteractionResponse
            {
                InteractionId = 42
            },
            onStartPageInteraction: request => capturedRequest = request));

        var result = await instance.StartPageInteractionAsync(
            "pages/test",
            "session-1",
            new Dictionary<string, string> { ["name"] = "value" },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(42, result.InteractionId);

        Assert.NotNull(capturedRequest);
        Assert.Equal("pages/test", capturedRequest.Route);
        Assert.Equal("session-1", capturedRequest.SessionId);
        Assert.Equal("value", capturedRequest.QueryParameters["name"]);
    }

    [Fact]
    public async Task StartPageInteractionAsync_NotFound_ReturnsNull()
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient(startPageNotFound: true));

        var result = await instance.StartPageInteractionAsync(
            "pages/missing",
            "session-1",
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class MockDashboardServiceClient(
        IEnumerable<GetInteractionAssetResponse>? assetResponses = null,
        bool assetNotFound = false,
        StartPageInteractionResponse? startPageResponse = null,
        bool startPageNotFound = false,
        Action<StartPageInteractionRequest>? onStartPageInteraction = null) : Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient
    {
        private readonly IReadOnlyList<GetInteractionAssetResponse> _assetResponses = assetResponses?.ToList() ?? [];
        private readonly bool _assetNotFound = assetNotFound;
        private readonly StartPageInteractionResponse? _startPageResponse = startPageResponse;
        private readonly bool _startPageNotFound = startPageNotFound;
        private readonly Action<StartPageInteractionRequest>? _onStartPageInteraction = onStartPageInteraction;

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

        public override AsyncServerStreamingCall<GetInteractionAssetResponse> GetInteractionAsset(GetInteractionAssetRequest request, CallOptions options)
        {
            if (_assetNotFound)
            {
                return new AsyncServerStreamingCall<GetInteractionAssetResponse>(
                    new AsyncStreamReader<GetInteractionAssetResponse>([]),
                    Task.FromResult(new Metadata()),
                    () => new Status(StatusCode.NotFound, "Not found"),
                    () => new Metadata(),
                    () => { });
            }

            return new AsyncServerStreamingCall<GetInteractionAssetResponse>(
                new AsyncStreamReader<GetInteractionAssetResponse>(_assetResponses),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncUnaryCall<StartPageInteractionResponse> StartPageInteractionAsync(StartPageInteractionRequest request, CallOptions options)
        {
            _onStartPageInteraction?.Invoke(request);

            var responseTask = _startPageNotFound
                ? Task.FromException<StartPageInteractionResponse>(new RpcException(new Status(StatusCode.NotFound, "Not found")))
                : Task.FromResult(_startPageResponse ?? new StartPageInteractionResponse());

            return new AsyncUnaryCall<StartPageInteractionResponse>(
                responseTask,
                Task.FromResult(new Metadata()),
                () => _startPageNotFound ? new Status(StatusCode.NotFound, "Not found") : Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncUnaryCall<ApplicationInformationResponse> GetApplicationInformationAsync(ApplicationInformationRequest request, CallOptions options)
        {
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
            return new AsyncServerStreamingCall<WatchResourcesUpdate>(
                new AsyncStreamReader<WatchResourcesUpdate>(),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    private sealed class AsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _enumerator;

        public AsyncStreamReader(IEnumerable<T>? items = null)
        {
            _enumerator = (items ?? []).GetEnumerator();
        }

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var moved = _enumerator.MoveNext();
            if (moved)
            {
                Current = _enumerator.Current;
            }

            return Task.FromResult(moved);
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
