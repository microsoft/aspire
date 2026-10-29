// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Integration;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public async Task TerminalStream_EndedBeforeHandshakeClosesWithCompletionStatusAndDisposesCall()
    {
        var channel = Channel.CreateUnbounded<TerminalServerFrame>();
        channel.Writer.TryWrite(new TerminalServerFrame { Ended = true });
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new ConcurrentQueue<TerminalClientFrame>();
        using var call = new AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame>(
            new ClientStreamWriter<TerminalClientFrame>
            {
                OnWrite = frame =>
                {
                    writes.Enqueue(frame);
                    return Task.CompletedTask;
                }
            },
            new AsyncStreamReader<TerminalServerFrame>(channel: channel.Reader),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => disposed.TrySetResult());
        using var stream = new GrpcTerminalClientStream(call, "terminal");
        var dashboardClient = new TestDashboardClient(attachTerminal: (_, _) => Task.FromResult<Stream>(stream));
        var sessions = new TerminalViewSessionRegistry();
        using var session = sessions.Create("/api/apphost-terminal?terminalId=terminal", readOnly: false);
        using var host = await BuildTerminalTestHostAsync(dashboardClient, sessions);
        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Origin = "https://dashboard.example.com";
        using var socket = await client.ConnectAsync(
            new Uri($"wss://dashboard.example.com/api/apphost-terminal?terminalId=terminal&viewId={session.Id}"), CancellationToken.None).DefaultTimeout();
        var buffer = new byte[64];

        Assert.True(stream.TerminalEnded);
        await session.Ended.DefaultTimeout();
        Assert.True(session.ReadOnly);
        var handshakeWrites = writes.ToArray();
        Assert.NotEmpty(handshakeWrites);
        // Input already in flight must not reach the AppHost after authoritative completion.
        await socket.SendAsync("""{"type":"input","text":"ignored"}"""u8.ToArray(), WebSocketMessageType.Text,
            true, CancellationToken.None).DefaultTimeout();
        var message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).DefaultTimeout();
        Assert.Equal(WebSocketMessageType.Close, message.MessageType);
        Assert.Equal((WebSocketCloseStatus)4000, message.CloseStatus);
        Assert.Equal("Terminal ended", message.CloseStatusDescription);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Received", CancellationToken.None).DefaultTimeout();
        await disposed.Task.DefaultTimeout();
        Assert.Equal(handshakeWrites, writes);
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

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.NotFound)]
    public async Task TerminalStream_RpcReadFailurePreservesStatusAsStreamError(StatusCode statusCode)
    {
        var error = new RpcException(new Status(statusCode, "Terminal transport failed."));
        var channel = Channel.CreateUnbounded<TerminalServerFrame>();
        channel.Writer.TryComplete(error);
        using var call = new AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame>(
            new ClientStreamWriter<TerminalClientFrame>(),
            new AsyncStreamReader<TerminalServerFrame>(channel: channel.Reader),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
        using var stream = new GrpcTerminalClientStream(call, "terminal");

        var exception = await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[1]).AsTask());

        Assert.Same(error, exception.InnerException);
        Assert.False(stream.TerminalEnded);
    }

    [Theory]
    [InlineData(StatusCode.NotFound, true)]
    [InlineData(StatusCode.FailedPrecondition, true)]
    [InlineData(StatusCode.Unavailable, false)]
    [InlineData(StatusCode.DeadlineExceeded, false)]
    public async Task TerminalStream_HandshakeFailurePreservesPermanentAndTransientStatus(StatusCode statusCode, bool permanentFailure)
    {
        var channel = Channel.CreateUnbounded<TerminalServerFrame>();
        channel.Writer.TryComplete(new RpcException(new Status(statusCode, "Terminal is unavailable.")));
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var call = new AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame>(
            new ClientStreamWriter<TerminalClientFrame> { OnWrite = _ => Task.CompletedTask },
            new AsyncStreamReader<TerminalServerFrame>(channel: channel.Reader),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => disposed.TrySetResult());
        using var stream = new GrpcTerminalClientStream(call, "terminal");
        var dashboardClient = new TestDashboardClient(attachTerminal: (_, _) => Task.FromResult<Stream>(stream));
        var sessions = new TerminalViewSessionRegistry();
        using var session = sessions.Create("/api/apphost-terminal?terminalId=terminal", readOnly: false);
        using var host = await BuildTerminalTestHostAsync(dashboardClient, sessions);
        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Origin = "https://dashboard.example.com";
        var uri = new Uri($"wss://dashboard.example.com/api/apphost-terminal?terminalId=terminal&viewId={session.Id}");

        if (permanentFailure)
        {
            using var socket = await client.ConnectAsync(uri, CancellationToken.None).DefaultTimeout();
            var message = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), CancellationToken.None).DefaultTimeout();

            Assert.Equal(WebSocketMessageType.Close, message.MessageType);
            Assert.Equal((WebSocketCloseStatus)4000, message.CloseStatus);
            Assert.Equal("Terminal ended", message.CloseStatusDescription);
            await session.Ended.DefaultTimeout();
            Assert.True(session.ReadOnly);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Received", CancellationToken.None).DefaultTimeout();
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(uri, CancellationToken.None).DefaultTimeout());

            Assert.Contains(StatusCodes.Status503ServiceUnavailable.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message);
            Assert.False(session.Ended.IsCompleted);
            Assert.False(session.ReadOnly);
        }

        await disposed.Task.DefaultTimeout();
        // The proxy classifies the RPC status; the stream never received an Ended frame.
        Assert.False(stream.TerminalEnded);
    }

    private static Task<IHost> BuildTerminalTestHostAsync(IDashboardClient dashboardClient, TerminalViewSessionRegistry sessions)
    {
        return new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(context =>
                    {
                        context.Request.Scheme = "https";
                        context.Request.Host = new HostString("dashboard.example.com");
                        return TerminalWebSocketProxy.HandleAppHostTerminalAsync(context, dashboardClient, sessions, NullLogger.Instance, "test");
                    });
                }))
            .StartAsync();
    }

    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddXunit(testOutputHelper, LogLevel.Trace, DateTimeOffset.UtcNow);
        builder.SetMinimumLevel(LogLevel.Trace);
    });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-password")]
    public async Task FileCertificate_LoadsPkcs12WithPrivateKey(string? password)
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=Dashboard client test", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        X509Certificate2? loadedCertificate = null;
        try
        {
            var path = Path.Combine(workspace.Path, "client.pfx");
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));

            await using var client = CreateResourceServiceClient(
                clientCertificate: new ResourceServiceClientCertificateOptions
                {
                    Source = DashboardClientCertificateSource.File,
                    FilePath = path,
                    Password = password
                },
                configureHttpHandler: handler =>
                {
                    Assert.NotNull(handler.SslOptions.ClientCertificates);
                    loadedCertificate = Assert.IsType<X509Certificate2>(Assert.Single(handler.SslOptions.ClientCertificates.Cast<X509Certificate>()));
                });

            Assert.NotNull(loadedCertificate);
            Assert.Equal(certificate.RawData, loadedCertificate.RawData);
            Assert.True(loadedCertificate.HasPrivateKey);
        }
        finally
        {
            loadedCertificate?.Dispose();
        }
    }

    [Theory]
    [InlineData("der")]
    [InlineData("pem")]
    [InlineData("pkcs7")]
    [InlineData("invalid")]
    public void FileCertificate_RejectsNonPkcs12(string format)
    {
        using var certificate = TestCertificateLoader.GetTestCertificate();
        var data = format switch
        {
            "der" => certificate.RawData,
            "pem" => Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()),
            "pkcs7" => new X509Certificate2Collection(certificate).Export(X509ContentType.Pkcs7)!,
            "invalid" => new byte[] { 1, 2, 3 },
            _ => throw new InvalidOperationException()
        };
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var path = Path.Combine(workspace.Path, "client.pfx");
        File.WriteAllBytes(path, data);

        Assert.ThrowsAny<CryptographicException>(() => CreateResourceServiceClient(
            clientCertificate: new ResourceServiceClientCertificateOptions
            {
                Source = DashboardClientCertificateSource.File,
                FilePath = path
            }));
    }

    [Fact]
    public void FileCertificate_IncorrectPassword()
    {
        Assert.ThrowsAny<CryptographicException>(() => CreateResourceServiceClient(
            clientCertificate: new ResourceServiceClientCertificateOptions
            {
                Source = DashboardClientCertificateSource.File,
                FilePath = TestCertificateLoader.TestCertificatePath,
                Password = "incorrect-password"
            }));
    }

    [Fact]
    public void FileCertificate_MissingFile()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var path = Path.Combine(workspace.Path, "missing.pfx");
        var exception = Assert.Throws<CryptographicException>(() => CreateResourceServiceClient(
            clientCertificate: new ResourceServiceClientCertificateOptions
            {
                Source = DashboardClientCertificateSource.File,
                FilePath = path
            }));
        Assert.Equal(path, Assert.IsType<FileNotFoundException>(exception.InnerException).FileName);
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
    public async Task CurrentRun_CommandConfirmedWhileOffline_ReturnsCancelledWithoutDispatch()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = Options.Create(new DashboardOptions
        {
            Data = new DashboardDataOptions { Directory = workspace.Path, PersistenceMode = DashboardPersistenceMode.Run }
        });
        using var services = CreateRepositoryServices(options);
        using var store = new DashboardRunStore(options, NullLogger<DashboardRunStore>.Instance, TimeProvider.System);
        using var pool = new DashboardDataSourcePool(store, new RepositoryFactory(services));
        await pool.InitializeAsync(CancellationToken.None);
        using var source = new DashboardDataSource(store, NullLogger<DashboardDataSource>.Instance, pool);
        await using var client = CreateResourceServiceClient(resourceRepositoryWriter: new CurrentResourceWriter(pool), dataSourcePool: pool);
        var service = new MockDashboardServiceClient
        {
            ResourceUpdatesChannel = Channel.CreateUnbounded<WatchResourcesUpdate>().Reader
        };
        client.SetDashboardServiceClient(service);
        await client.WhenConnected.DefaultTimeout();
        client.SetConnectionStateForTesting(DashboardConnectionState.Disconnected);
        var selected = new SelectedDashboardClient(client, source);

        var response = await selected.ExecuteResourceCommandAsync("api", "Executable", CreateCommand(),
            new ExecuteResourceCommandOptions(), CancellationToken.None);

        Assert.Equal(Aspire.Dashboard.Model.ResourceCommandResponseKind.Cancelled, response.Kind);
        Assert.Empty(service.CommandCalls);
    }

    [Fact]
    public async Task ConsoleLogs_DisposedBeforeFirstHandshake_CancelsPendingCapture()
    {
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(new MockDashboardServiceClient { FailOnGetApplicationInformation = true });
        await using var batches = client.SubscribeConsoleLogs("api", CancellationToken.None).GetAsyncEnumerator();
        var pending = batches.MoveNextAsync().AsTask();

        await client.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.DefaultTimeout());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsoleLogs_RunChangesWhileBatchIsInFlight_BatchStaysInOriginalRun(bool follow)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = Options.Create(new DashboardOptions
        {
            Data = new DashboardDataOptions { Directory = workspace.Path, PersistenceMode = DashboardPersistenceMode.Run }
        });
        using var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(options)
            .AddSingleton<PauseManager>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IKnownPropertyLookup, MockKnownPropertyLookup>()
            .BuildServiceProvider();
        using var store = new DashboardRunStore(options, NullLogger<DashboardRunStore>.Instance, TimeProvider.System);
        using var pool = new DashboardDataSourcePool(store, new RepositoryFactory(services));
        await pool.InitializeAsync(CancellationToken.None);
        pool.PublishRun();
        await pool.StartAppHostRunAsync("first", CancellationToken.None);
        var firstRunId = store.RunId;
        var writer = new CurrentResourceWriter(pool);
        await writer.ReplaceResourcesAsync([new Resource { Name = "api", DisplayName = "api", ResourceType = "Executable", CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) }]);
        var logs = Channel.CreateUnbounded<WatchResourceConsoleLogsUpdate>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = CreateResourceServiceClient(resourceRepositoryWriter: writer, dataSourcePool: pool);
        client.SetDashboardServiceClient(new MockDashboardServiceClient
        {
            ResourceUpdatesChannel = Channel.CreateUnbounded<WatchResourcesUpdate>().Reader,
            ConsoleUpdatesChannel = logs.Reader,
            OnConsoleWatchStarted = () => started.TrySetResult()
        });
        await using var batches = (follow
            ? client.SubscribeConsoleLogs("api", CancellationToken.None)
            : client.GetConsoleLogs("api", CancellationToken.None)).GetAsyncEnumerator();
        var next = batches.MoveNextAsync().AsTask();
        await started.Task.DefaultTimeout();

        await pool.StartAppHostRunAsync("second", CancellationToken.None);
        await pool.PruningTask;
        await logs.Writer.WriteAsync(new WatchResourceConsoleLogsUpdate
        {
            LogLines = { new ConsoleLogLine { LineNumber = 1, Text = "old AppHost batch" } }
        });
        logs.Writer.Complete();
        Assert.True(await next.DefaultTimeout());
        Assert.Equal("old AppHost batch", Assert.Single(batches.Current).Content);
        Assert.False(await batches.MoveNextAsync().AsTask().DefaultTimeout());

        var currentLogs = new List<ResourceLogLine>();
        await foreach (var batch in pool.Current.ResourceRepository.GetConsoleLogs("api", CancellationToken.None))
        {
            currentLogs.AddRange(batch);
        }
        Assert.Empty(currentLogs);
        using var history = pool.TryAcquire(store.GetRunById(firstRunId, onlyCompatible: true)!);
        Assert.NotNull(history);
        var historicalLogs = new List<ResourceLogLine>();
        await foreach (var batch in history.ResourceRepository.GetConsoleLogs("api", CancellationToken.None))
        {
            historicalLogs.AddRange(batch);
        }
        Assert.Equal("old AppHost batch", Assert.Single(historicalLogs).Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsoleLogs_RunChangesWhileMarkingLoaded_RpcKeepsOriginalAttachment(bool follow)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = Options.Create(new DashboardOptions
        {
            Data = new DashboardDataOptions { Directory = workspace.Path, PersistenceMode = DashboardPersistenceMode.Run }
        });
        using var services = CreateRepositoryServices(options);
        using var store = new DashboardRunStore(options, NullLogger<DashboardRunStore>.Instance, TimeProvider.System);
        using var pool = new DashboardDataSourcePool(store, new RepositoryFactory(services));
        await pool.InitializeAsync(CancellationToken.None);
        pool.PublishRun();
        var writer = new CurrentResourceWriter(pool);
        var first = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var second = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var subscribed = Channel.CreateUnbounded<int>();
        var attempts = 0;
        var rpc = new MockDashboardServiceClient
        {
            RunId = "first",
            ResourceUpdatesProvider = () =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                subscribed.Writer.TryWrite(attempt);
                return attempt == 1 ? first.Reader : second.Reader;
            }
        };
        await using var client = CreateResourceServiceClient(resourceRepositoryWriter: writer, dataSourcePool: pool);
        client.SetDashboardServiceClient(rpc);
        await client.WhenConnected.DefaultTimeout();
        Assert.Equal(1, await subscribed.Reader.ReadAsync().AsTask().DefaultTimeout());
        await using var batches = (follow
            ? client.SubscribeConsoleLogs("api", CancellationToken.None)
            : client.GetConsoleLogs("api", CancellationToken.None)).GetAsyncEnumerator();
        Task<bool> next;
        using (await pool.Current.Database.WriteLock.LockAsync())
        {
            // MarkConsoleLogsLoadedAsync is held in the old database after the attachment is captured.
            next = batches.MoveNextAsync().AsTask();
            rpc.RunId = "second";
            first.Writer.Complete();
            Assert.Equal(2, await subscribed.Reader.ReadAsync().AsTask().DefaultTimeout());
            await pool.PruningTask;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        var call = Assert.Single(rpc.ConsoleCalls);
        Assert.Equal("first", call.RunId);
        Assert.True(call.Cancelled);
    }

    [Theory]
    [InlineData(DashboardPersistenceMode.None, false)]
    [InlineData(DashboardPersistenceMode.None, true)]
    [InlineData(DashboardPersistenceMode.Resume, false)]
    [InlineData(DashboardPersistenceMode.Resume, true)]
    [InlineData(DashboardPersistenceMode.Run, false)]
    public async Task SelectedConsoleLogs_OpenedOffline_ResumesCaptureWithoutDuplicatingReplay(DashboardPersistenceMode mode, bool restartAppHost)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = Options.Create(new DashboardOptions
        {
            Data = new DashboardDataOptions { Directory = workspace.Path, PersistenceMode = mode }
        });
        using var services = CreateRepositoryServices(options);
        using var store = new DashboardRunStore(options, NullLogger<DashboardRunStore>.Instance, TimeProvider.System);
        using var pool = new DashboardDataSourcePool(store, new RepositoryFactory(services));
        await pool.InitializeAsync(CancellationToken.None);
        pool.PublishRun();
        var writer = new CurrentResourceWriter(pool);
        var updates = Channel.CreateUnbounded<WatchResourcesUpdate>();
        updates.Writer.TryWrite(new WatchResourcesUpdate { InitialData = new InitialResourceData() });
        var reconnected = Channel.CreateUnbounded<WatchResourcesUpdate>();
        reconnected.Writer.TryWrite(new WatchResourcesUpdate
        {
            InitialData = new InitialResourceData
            {
                Resources = { new Resource { Name = "api", DisplayName = "api", ResourceType = "Executable", CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) } }
            }
        });
        var resourceWatches = 0;
        var logs = Channel.CreateUnbounded<WatchResourceConsoleLogsUpdate>();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var live = CreateResourceServiceClient(resourceRepositoryWriter: writer, dataSourcePool: pool);
        var rpc = new MockDashboardServiceClient
        {
            RunId = "first",
            ResourceUpdatesProvider = () => Interlocked.Increment(ref resourceWatches) == 1 ? updates.Reader : reconnected.Reader,
            ConsoleUpdatesChannel = logs.Reader,
            OnConsoleWatchStarted = () => captureStarted.TrySetResult()
        };
        live.SetDashboardServiceClient(rpc);
        await live.WhenResourcesReady.DefaultTimeout();
        var originalConsoleWriter = (IAppHostConsoleLogWriter)pool.Current.ResourceRepository;
        // Populate the captured snapshot after the mock's empty initial resource snapshot.
        await writer.ReplaceResourcesAsync([new Resource { Name = "api", DisplayName = "api", ResourceType = "Executable", CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) }]);
        await writer.AddConsoleLogsAsync("api", [new ConsoleLogLine { LineNumber = 1, Text = "cached" }]);
        live.SetConnectionStateForTesting(DashboardConnectionState.Disconnected);
        using var source = new DashboardDataSource(store, NullLogger<DashboardDataSource>.Instance, pool);
        var selected = new SelectedDashboardClient(live, source);
        using var cts = new CancellationTokenSource();
        await using var batches = selected.SubscribeConsoleLogs("api", cts.Token).GetAsyncEnumerator();

        Assert.True(await batches.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal("cached", Assert.Single(batches.Current).Content);
        Assert.False(captureStarted.Task.IsCompleted);
        if (restartAppHost)
        {
            rpc.RunId = "second";
            updates.Writer.Complete();
        }
        else
        {
            live.SetConnectionStateForTesting(DashboardConnectionState.Connected);
        }
        await captureStarted.Task.DefaultTimeout();
        if (restartAppHost)
        {
            Assert.False(await originalConsoleWriter.AddConsoleLogsAsync("api",
                [new ConsoleLogLine { LineNumber = 99, Text = "late old-run batch" }], "first"));
        }
        await logs.Writer.WriteAsync(new WatchResourceConsoleLogsUpdate
        {
            LogLines = { new ConsoleLogLine { LineNumber = 1, Text = restartAppHost ? "new run first line" : "cached" }, new ConsoleLogLine { LineNumber = 2, Text = "after reconnect" } }
        });

        Assert.True(await batches.MoveNextAsync().AsTask().DefaultTimeout());
        string[] expectedLines = restartAppHost ? ["new run first line", "after reconnect"] : ["after reconnect"];
        Assert.Equal(expectedLines, batches.Current.Select(line => line.Content));
        var persistedLines = new List<string>();
        await foreach (var batch in source.ResourceRepository.GetConsoleLogs("api", CancellationToken.None))
        {
            persistedLines.AddRange(batch.Select(line => line.Content));
        }
        Assert.Equal(new[] { "cached" }.Concat(expectedLines), persistedLines);
        cts.Cancel();
    }

    [Fact]
    public async Task SelectedConsoleLogs_HistoricalRun_DoesNotStartLiveCapture()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = Options.Create(new DashboardOptions
        {
            Data = new DashboardDataOptions { Directory = workspace.Path, PersistenceMode = DashboardPersistenceMode.Run }
        });
        using var services = CreateRepositoryServices(options);
        using var store = new DashboardRunStore(options, NullLogger<DashboardRunStore>.Instance, TimeProvider.System);
        using var pool = new DashboardDataSourcePool(store, new RepositoryFactory(services));
        await pool.InitializeAsync(CancellationToken.None);
        pool.PublishRun();
        await pool.StartAppHostRunAsync("first", CancellationToken.None);
        var historyId = store.RunId;
        var writer = new CurrentResourceWriter(pool);
        await writer.ReplaceResourcesAsync([new Resource { Name = "api", DisplayName = "api", ResourceType = "Executable", CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) }]);
        await writer.AddConsoleLogsAsync("api", [new ConsoleLogLine { LineNumber = 1, Text = "historical" }]);
        await pool.StartAppHostRunAsync("second", CancellationToken.None);
        await pool.PruningTask;
        var updates = Channel.CreateUnbounded<WatchResourcesUpdate>();
        updates.Writer.TryWrite(new WatchResourcesUpdate { InitialData = new InitialResourceData() });
        var rpc = new MockDashboardServiceClient { ResourceUpdatesChannel = updates.Reader };
        await using var live = CreateResourceServiceClient(resourceRepositoryWriter: writer, dataSourcePool: pool);
        live.SetDashboardServiceClient(rpc);
        await live.WhenResourcesReady.DefaultTimeout();
        using var source = new DashboardDataSource(store, NullLogger<DashboardDataSource>.Instance, pool);
        source.SelectRun(historyId);
        var selected = new SelectedDashboardClient(live, source);
        using var cts = new CancellationTokenSource();
        await using var logs = selected.SubscribeConsoleLogs("api", cts.Token).GetAsyncEnumerator();

        Assert.True(await logs.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal("historical", Assert.Single(logs.Current).Content);
        Assert.True(selected.IsReadOnly);
        Assert.Empty(rpc.ConsoleCalls);
        cts.Cancel();
    }

    private static ServiceProvider CreateRepositoryServices(IOptions<DashboardOptions> options) => new ServiceCollection()
        .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
        .AddSingleton(options)
        .AddSingleton<PauseManager>()
        .AddSingleton(TimeProvider.System)
        .AddSingleton<IKnownPropertyLookup, MockKnownPropertyLookup>()
        .BuildServiceProvider();

    [Theory]
    [InlineData("http://localhost:80", "http://localhost", "https://localhost:443", "https://localhost")]
    [InlineData("https://localhost:443", "https://localhost", "http://localhost:80", "http://localhost")]
    public async Task ResourceOrigins_DefaultPorts_MatchBrowserOriginAfterHandshakeAndUpdates(
        string initialOrigin, string initialBrowserOrigin, string updatedOrigin, string updatedBrowserOrigin)
    {
        var updates = Channel.CreateUnbounded<WatchResourcesUpdate>();
        updates.Writer.TryWrite(new WatchResourcesUpdate { InitialData = new InitialResourceData() });
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(new MockDashboardServiceClient
        {
            ResourceUpdatesChannel = updates.Reader,
            CorsAllowedOrigins = initialOrigin
        });
        await client.WhenResourcesReady.DefaultTimeout();
        Assert.True(client.IsAppHostOriginAllowed(initialBrowserOrigin));
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            config =>
            {
                config[DashboardConfigNames.ResourceServiceUrlName.ConfigKey] = "http://localhost:12345";
                config[DashboardConfigNames.ResourceServiceClientAuthModeName.ConfigKey] = nameof(ResourceClientAuthMode.Unsecured);
            },
            preConfigureBuilder: builder => builder.Services.AddSingleton(client));
        await app.StartAsync().DefaultTimeout();
        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");
        Assert.Equal(initialBrowserOrigin, await GetAllowedOriginAsync(initialBrowserOrigin));
        var (_, changes) = await client.SubscribeResourcesAsync(CancellationToken.None);
        await using var changesReader = changes.GetAsyncEnumerator();

        await updates.Writer.WriteAsync(new WatchResourcesUpdate
        {
            CorsAllowedOrigins = updatedOrigin,
            Changes = new WatchResourcesChanges
            {
                Value = { new WatchResourcesChange { Upsert = new Resource { Name = "web", DisplayName = "web", ResourceType = "Executable", CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) } } }
            }
        });
        Assert.True(await changesReader.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.False(client.IsAppHostOriginAllowed(initialBrowserOrigin));
        Assert.True(client.IsAppHostOriginAllowed(updatedBrowserOrigin));
        Assert.Null(await GetAllowedOriginAsync(initialBrowserOrigin));
        Assert.Equal(updatedBrowserOrigin, await GetAllowedOriginAsync(updatedBrowserOrigin));

        async Task<string?> GetAllowedOriginAsync(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/traces");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type");
            using var response = await httpClient.SendAsync(request).DefaultTimeout();
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? Assert.Single(values) : null;
        }
    }

    [Theory]
    [InlineData("https://localhost:8443")]
    [InlineData("https://localhost:443/path")]
    [InlineData("https://localhost:443/../")]
    [InlineData("https://user@localhost:443")]
    public async Task ResourceOrigins_Normalization_DoesNotGrantAccessToDifferentOrigin(string advertisedOrigin)
    {
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(new MockDashboardServiceClient { CorsAllowedOrigins = advertisedOrigin });
        await client.WhenConnected.DefaultTimeout();

        Assert.False(client.IsAppHostOriginAllowed("https://localhost"));
    }

    [Fact]
    public async Task ResourceUpdates_RefreshOriginsAfterAllocationAndPreserveOlderServerMetadata()
    {
        var updates = Channel.CreateUnbounded<WatchResourcesUpdate>();
        updates.Writer.TryWrite(new WatchResourcesUpdate { InitialData = new InitialResourceData() });
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(new MockDashboardServiceClient
        {
            ResourceUpdatesChannel = updates.Reader,
            CorsAllowedOrigins = "http://localhost:4000"
        });
        await client.WhenResourcesReady.DefaultTimeout();
        // Missing optional metadata from an older server must not clear the handshake's origins.
        Assert.True(client.IsAppHostOriginAllowed("http://localhost:4000"));
        var (_, changes) = await client.SubscribeResourcesAsync(CancellationToken.None);
        await using var changesReader = changes.GetAsyncEnumerator();
        await updates.Writer.WriteAsync(new WatchResourcesUpdate
        {
            CorsAllowedOrigins = "http://localhost:5000",
            Changes = new WatchResourcesChanges
            {
                Value = { new WatchResourcesChange { Upsert = new Resource { Name = "web", DisplayName = "web", ResourceType = "Executable", CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) } } }
            }
        });
        Assert.True(await changesReader.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.False(client.IsAppHostOriginAllowed("http://localhost:4000"));
        Assert.True(client.IsAppHostOriginAllowed("http://localhost:5000"));

        await updates.Writer.WriteAsync(new WatchResourcesUpdate
        {
            CorsAllowedOrigins = "",
            Changes = new WatchResourcesChanges { Value = { new WatchResourcesChange { Upsert = new Resource { Name = "web", DisplayName = "web", ResourceType = "Executable", CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) } } } }
        });
        Assert.True(await changesReader.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.False(client.IsAppHostOriginAllowed("http://localhost:5000"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenResourcesReady_WaitsForInitialSnapshotPersistence(bool emptySnapshot)
    {
        var updates = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var persisting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingResourceRepositoryWriter
        {
            OnReplaceResourcesAsync = async _ =>
            {
                persisting.TrySetResult();
                await persisted.Task;
            }
        };
        await using var client = CreateResourceServiceClient(resourceRepositoryWriter: writer);
        client.SetDashboardServiceClient(new MockDashboardServiceClient { ResourceUpdatesChannel = updates.Reader });
        var ready = client.WhenResourcesReady;
        await client.WhenConnected.DefaultTimeout();
        var subscription = client.SubscribeResourcesAsync(CancellationToken.None);
        Assert.False(ready.IsCompleted);
        Assert.False(subscription.IsCompleted);

        try
        {
            var initialData = new InitialResourceData();
            if (!emptySnapshot)
            {
                initialData.Resources.Add(new Resource { Name = "shell", CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) });
            }
            await updates.Writer.WriteAsync(new WatchResourcesUpdate { InitialData = initialData });
            await persisting.Task.DefaultTimeout();
            Assert.False(ready.IsCompleted);
            Assert.False(subscription.IsCompleted);

            persisted.SetResult();
            await ready.DefaultTimeout();
            var (snapshot, _) = await subscription.DefaultTimeout();
            Assert.Equal(emptySnapshot ? [] : ["shell"], snapshot.Select(r => r.Name));
            Assert.Equal(emptySnapshot ? [] : ["shell"], client.GetResources().Select(r => r.Name));
        }
        finally
        {
            persisted.TrySetResult();
        }
    }

    [Fact]
    public async Task ExecuteCommand_RunChangesAfterConfirmation_RejectsOriginalAttachment()
    {
        var first = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var second = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var reattached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new MockDashboardServiceClient
        {
            RunId = "first",
            ResourceUpdatesProvider = () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return first.Reader;
                }
                reattached.TrySetResult();
                return second.Reader;
            }
        };
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(service);
        await client.WhenConnected.DefaultTimeout();
        var origin = client.AppHostRunId;
        service.RunId = "second";
        first.Writer.Complete(new RpcException(new Status(StatusCode.Unavailable, "AppHost restarted during confirmation")));
        await reattached.Task.DefaultTimeout();
        var command = new CommandViewModel("delete", CommandViewModelState.Enabled, "Delete", "", "Confirm?", [], false, "", Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular);
        var response = await client.ExecuteResourceCommandAsync("api", "Project", command,
            new ExecuteResourceCommandOptions { ExpectedAppHostRunId = origin }, CancellationToken.None);
        Assert.Equal(Aspire.Dashboard.Model.ResourceCommandResponseKind.Cancelled, response.Kind);
        Assert.Empty(service.CommandCalls);

        response = await client.ExecuteResourceCommandAsync("api", "Project", command,
            new ExecuteResourceCommandOptions { ExpectedAppHostRunId = client.AppHostRunId }, CancellationToken.None);
        Assert.Equal(Aspire.Dashboard.Model.ResourceCommandResponseKind.Succeeded, response.Kind);
        Assert.Equal("second", Assert.Single(service.CommandCalls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteCommand_AttachmentChangesDuringDispatch_ReturnsDisconnectedFailure(bool throwOperationCanceled)
    {
        var first = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var second = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var subscriptions = 0;
        var service = new MockDashboardServiceClient
        {
            RunId = "first",
            CancelExecuteResourceCommandOnCallCancellation = true,
            ThrowOperationCanceledOnCommandCancellation = throwOperationCanceled,
            ResourceUpdatesProvider = () => Interlocked.Increment(ref subscriptions) == 1 ? first.Reader : second.Reader
        };
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(service);
        await client.WhenConnected.DefaultTimeout();
        var pending = client.ExecuteResourceCommandAsync("api", "Executable", CreateCommand(),
            new ExecuteResourceCommandOptions { ExpectedAppHostRunId = "first" }, CancellationToken.None);
        Assert.Single(service.CommandCalls);
        service.RunId = "second";
        first.Writer.Complete(new RpcException(new Status(StatusCode.Unavailable, "AppHost restarted during command")));

        var response = await pending.DefaultTimeout();

        Assert.Equal(Aspire.Dashboard.Model.ResourceCommandResponseKind.Failed, response.Kind);
        Assert.Equal("Localized:ResourceCommandAppHostDisconnected", response.Message);
    }

    [Fact]
    public async Task UploadFile_RunChanges_RejectsDelayedSelectionForReusedInteractionId()
    {
        var first = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var second = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var reattached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new MockDashboardServiceClient
        {
            RunId = "first",
            ResourceUpdatesProvider = () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return first.Reader;
                }
                reattached.TrySetResult();
                return second.Reader;
            }
        };
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(service);
        await client.WhenConnected.DefaultTimeout();
        var origin = client.AppHostRunId;
        service.RunId = "second";
        first.Writer.Complete(new RpcException(new Status(StatusCode.Unavailable, "AppHost restarted during file selection")));
        await reattached.Task.DefaultTimeout();
        using var file = new MemoryStream("selected file"u8.ToArray());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.UploadFileAsync(file, "selected.txt", file.Length, 1, "file", origin, CancellationToken.None));
        Assert.Empty(service.UploadCalls);
        Assert.Equal(0, file.Position);

        var fileId = await client.UploadFileAsync(file, "selected.txt", file.Length, 1, "file", client.AppHostRunId, CancellationToken.None);
        Assert.Equal("uploaded-file", fileId);
        Assert.Equal("second", Assert.Single(service.UploadCalls));
        var chunk = Assert.Single(service.UploadChunks);
        Assert.Equal(1, chunk.InteractionId);
        Assert.Equal("file", chunk.InputName);
        Assert.Equal("selected.txt", chunk.FileName);
        Assert.Equal("selected file", chunk.Data.ToStringUtf8());
    }

    [Fact]
    public async Task Interactions_RunChanges_RejectsDelayedRepliesForReusedInteractionId()
    {
        var firstResources = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var secondResources = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var firstInteractions = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var secondInteractions = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var secondSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = Channel.CreateUnbounded<(string? RunId, WatchInteractionsRequestUpdate Update)>();
        var resourceCalls = 0;
        var interactionCalls = 0;
        var mock = new MockDashboardServiceClient
        {
            RunId = "first",
            ResourceUpdatesProvider = () => Interlocked.Increment(ref resourceCalls) == 1 ? firstResources.Reader : secondResources.Reader,
            InteractionUpdatesProvider = () =>
            {
                if (Interlocked.Increment(ref interactionCalls) == 1)
                {
                    return firstInteractions.Reader;
                }
                secondSubscribed.TrySetResult();
                return secondInteractions.Reader;
            },
            OnInteractionWrite = (runId, update) => sent.Writer.TryWrite((runId, update))
        };
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(mock);
        using var cts = new CancellationTokenSource();
        await using var updates = client.SubscribeInteractionsAsync(cts.Token).GetAsyncEnumerator();
        var next = updates.MoveNextAsync().AsTask();
        await client.WhenConnected.DefaultTimeout();
        await firstInteractions.Writer.WriteAsync(new WatchInteractionsResponseUpdate { InteractionId = 1, MessageBox = new InteractionMessageBox() });
        Assert.True(await next.DefaultTimeout());
        Assert.Equal("first", updates.Current.AppHostRunId);
        var oldReply = new WatchInteractionsRequestUpdate { InteractionId = 1, AppHostRunId = updates.Current.AppHostRunId, Complete = new InteractionComplete() };

        mock.RunId = "second";
        firstResources.Writer.Complete(new RpcException(new Status(StatusCode.Unavailable, "AppHost restarted")));
        await secondSubscribed.Task.DefaultTimeout();
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(WatchInteractionsResponseUpdate.KindOneofCase.Complete, updates.Current.KindCase);
        Assert.Equal("first", updates.Current.AppHostRunId);
        await secondInteractions.Writer.WriteAsync(new WatchInteractionsResponseUpdate { InteractionId = 1, MessageBox = new InteractionMessageBox() });
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal("second", updates.Current.AppHostRunId);

        await client.SendInteractionRequestAsync(oldReply, CancellationToken.None);
        var currentReply = new WatchInteractionsRequestUpdate { InteractionId = 1, AppHostRunId = updates.Current.AppHostRunId, MessageBox = new InteractionMessageBox { Result = true } };
        await client.SendInteractionRequestAsync(currentReply, CancellationToken.None);
        var actual = await sent.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal("second", actual.RunId);
        Assert.Same(currentReply, actual.Update);
        Assert.False(sent.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenResourcesReady_InteractionReconnectPreservesResourceReadiness(bool resourcesReceived)
    {
        var resources = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var firstInteractions = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var secondInteractions = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var resubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionSubscriptions = 0;
        var resourceSubscriptions = 0;
        await using var client = CreateResourceServiceClient();
        client.SetDashboardServiceClient(new MockDashboardServiceClient
        {
            ResourceUpdatesProvider = () =>
            {
                Interlocked.Increment(ref resourceSubscriptions);
                return resources.Reader;
            },
            InteractionUpdatesProvider = () =>
            {
                if (Interlocked.Increment(ref interactionSubscriptions) == 1)
                {
                    return firstInteractions.Reader;
                }

                resubscribed.TrySetResult();
                return secondInteractions.Reader;
            }
        });
        var ready = client.WhenResourcesReady;
        await client.WhenConnected.DefaultTimeout();
        if (resourcesReceived)
        {
            await resources.Writer.WriteAsync(new WatchResourcesUpdate { InitialData = new InitialResourceData() });
            await ready.DefaultTimeout();
        }

        firstInteractions.Writer.Complete(new RpcException(new Status(StatusCode.Unavailable, "Interaction stream disconnected")));
        await resubscribed.Task.DefaultTimeout();
        // An idle interaction stream must not reset resource connection readiness. Otherwise a new
        // resource page can wait forever even though its snapshot is already available.
        Assert.Equal(DashboardConnectionState.Connected, client.ConnectionState);
        Assert.True(client.WhenConnected.IsCompletedSuccessfully);
        Assert.Same(ready, client.WhenResourcesReady);
        Assert.Equal(resourcesReceived, ready.IsCompleted);

        await secondInteractions.Writer.WriteAsync(new WatchInteractionsResponseUpdate { InteractionId = 1 });
        Assert.Same(ready, client.WhenResourcesReady);
        Assert.Equal(resourcesReceived, ready.IsCompleted);
        if (!resourcesReceived)
        {
            await resources.Writer.WriteAsync(new WatchResourcesUpdate { InitialData = new InitialResourceData() });
        }
        await ready.DefaultTimeout();
        var (snapshot, _) = await client.SubscribeResourcesAsync(CancellationToken.None).DefaultTimeout();
        Assert.Empty(snapshot);
        Assert.Equal(1, Volatile.Read(ref resourceSubscriptions));
        Assert.Equal(2, Volatile.Read(ref interactionSubscriptions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenResourcesReady_ResourceReconnectWaitsForPersistedSnapshot(bool failStream)
    {
        var first = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var second = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var third = Channel.CreateUnbounded<WatchResourcesUpdate>();
        var subscriptionsStarted = Channel.CreateUnbounded<int>();
        var persisting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptionCount = 0;
        var snapshotCount = 0;
        var writer = new RecordingResourceRepositoryWriter
        {
            OnReplaceResourcesAsync = async _ =>
            {
                if (Interlocked.Increment(ref snapshotCount) == 2)
                {
                    persisting.TrySetResult();
                    await persisted.Task;
                }
            }
        };
        await using var client = CreateResourceServiceClient(resourceRepositoryWriter: writer);
        client.SetDashboardServiceClient(new MockDashboardServiceClient
        {
            ResourceUpdatesProvider = () =>
            {
                var attempt = Interlocked.Increment(ref subscriptionCount);
                subscriptionsStarted.Writer.TryWrite(attempt);
                return attempt switch
                {
                    1 => first.Reader,
                    2 => second.Reader,
                    _ => third.Reader
                };
            }
        });
        var ready = client.WhenResourcesReady;
        Assert.Equal(1, await subscriptionsStarted.Reader.ReadAsync().AsTask().DefaultTimeout());
        await first.Writer.WriteAsync(new WatchResourcesUpdate { InitialData = new InitialResourceData() });
        await ready.DefaultTimeout();

        try
        {
            first.Writer.Complete(failStream ? new RpcException(new Status(StatusCode.Unavailable, "Resource stream disconnected")) : null);
            Assert.Equal(2, await subscriptionsStarted.Reader.ReadAsync().AsTask().DefaultTimeout());
            var replacementReady = client.WhenResourcesReady;
            Assert.NotSame(ready, replacementReady);
            Assert.False(replacementReady.IsCompleted);
            var subscription = client.SubscribeResourcesAsync(CancellationToken.None);
            Assert.False(subscription.IsCompleted);

            // A retry before its initial snapshot must retain the task that callers are already waiting on.
            second.Writer.Complete(failStream ? new RpcException(new Status(StatusCode.Unavailable, "Resource stream disconnected again")) : null);
            Assert.Equal(3, await subscriptionsStarted.Reader.ReadAsync().AsTask().DefaultTimeout());
            Assert.Same(replacementReady, client.WhenResourcesReady);

            await third.Writer.WriteAsync(new WatchResourcesUpdate { InitialData = new InitialResourceData() });
            await persisting.Task.DefaultTimeout();
            Assert.False(replacementReady.IsCompleted);
            Assert.False(subscription.IsCompleted);

            persisted.SetResult();
            await replacementReady.DefaultTimeout();
            var (snapshot, _) = await subscription.DefaultTimeout();
            Assert.Empty(snapshot);
        }
        finally
        {
            persisted.TrySetResult();
        }
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

    [Theory]
    [InlineData("", null, "Aspire")]
    [InlineData(" \t", "Configured", "Configured")]
    [InlineData("", " ", "Aspire")]
    [InlineData("Service", "Configured", "Service")]
    public async Task ApplicationName_ServiceNameFallsBackToConfiguredName(string serviceName, string? configuredName, string expected)
    {
        await using var instance = CreateResourceServiceClient(applicationName: configuredName);
        instance.SetDashboardServiceClient(new MockDashboardServiceClient { ApplicationName = serviceName });

        await instance.WhenConnected.DefaultTimeout();

        Assert.Equal(expected, instance.ApplicationName);
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

        // Trigger the connection without awaiting it. The first watch failure can reset
        // WhenConnected before the getter returns, leaving it waiting for a reconnect that never succeeds.
        _ = instance.WhenConnected;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteResourceCommandAsync_ClientCancellation_ReturnsAppHostDisconnectedFailure(bool throwOperationCanceled)
    {
        await using var instance = CreateResourceServiceClient();
        instance.SetDashboardServiceClient(new MockDashboardServiceClient { CancelExecuteResourceCommandOnCallCancellation = true, ThrowOperationCanceledOnCommandCancellation = throwOperationCanceled });

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
        public bool ThrowOperationCanceledOnCommandCancellation { get; init; }
        public string ApplicationName { get; init; } = "TestApplication";
        public string MinDashboardVersion { get; init; } = "";
        public string RunId { get; set; } = "";
        public string CorsAllowedOrigins { get; init; } = "";
        public ConcurrentQueue<(string? RunId, bool Cancelled)> ConsoleCalls { get; } = new();
        public ConcurrentQueue<string?> CommandCalls { get; } = new();
        public ConcurrentQueue<string?> UploadCalls { get; } = new();
        public ConcurrentQueue<UploadFileChunk> UploadChunks { get; } = new();
        public IReadOnlyList<WatchResourceConsoleLogsUpdate> ConsoleLogUpdates { get; init; } = [];
        public ChannelReader<WatchResourceConsoleLogsUpdate>? ConsoleUpdatesChannel { get; init; }
        public Action? OnConsoleWatchStarted { get; init; }
        public Action<string?, WatchInteractionsRequestUpdate>? OnInteractionWrite { get; init; }
        public IReadOnlyList<WatchResourcesUpdate> ResourceUpdates { get; init; } = [];
        public ChannelReader<WatchResourcesUpdate>? ResourceUpdatesChannel { get; init; }
        public Func<ChannelReader<WatchResourcesUpdate>>? ResourceUpdatesProvider { get; init; }
        public Func<ChannelReader<WatchInteractionsResponseUpdate>>? InteractionUpdatesProvider { get; init; }
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
            ConsoleCalls.Enqueue((options.Headers?.GetValue("x-aspire-apphost-run-id"), options.CancellationToken.IsCancellationRequested));
            options.CancellationToken.ThrowIfCancellationRequested();
            OnConsoleWatchStarted?.Invoke();
            return new AsyncServerStreamingCall<WatchResourceConsoleLogsUpdate>(
                new AsyncStreamReader<WatchResourceConsoleLogsUpdate>(ConsoleLogUpdates, ConsoleUpdatesChannel),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncDuplexStreamingCall<WatchInteractionsRequestUpdate, WatchInteractionsResponseUpdate> WatchInteractions(CallOptions options)
        {
            return new AsyncDuplexStreamingCall<WatchInteractionsRequestUpdate, WatchInteractionsResponseUpdate>(
                new ClientStreamWriter<WatchInteractionsRequestUpdate>
                {
                    OnWrite = update =>
                    {
                        OnInteractionWrite?.Invoke(options.Headers?.GetValue("x-aspire-apphost-run-id"), update);
                        return Task.CompletedTask;
                    }
                },
                new AsyncStreamReader<WatchInteractionsResponseUpdate>(channel: InteractionUpdatesProvider?.Invoke()),
                Task.FromResult(new Metadata()),
                () => InteractionUpdatesProvider is null ? new Status(StatusCode.Unimplemented, "Unimplemented!") : Status.DefaultSuccess,
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
                    ApplicationName = ApplicationName,
                    MinDashboardVersion = MinDashboardVersion,
                    RunId = RunId,
                    CorsAllowedOrigins = CorsAllowedOrigins
                }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncClientStreamingCall<UploadFileChunk, UploadFileResponse> UploadFile(CallOptions options)
        {
            UploadCalls.Enqueue(options.Headers?.GetValue("x-aspire-apphost-run-id"));
            return new AsyncClientStreamingCall<UploadFileChunk, UploadFileResponse>(
                new ClientStreamWriter<UploadFileChunk>
                {
                    OnComplete = () => Task.CompletedTask,
                    OnWrite = chunk =>
                    {
                        UploadChunks.Enqueue(chunk);
                        return Task.CompletedTask;
                    }
                },
                Task.FromResult(new UploadFileResponse { FileId = "uploaded-file" }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncUnaryCall<ResourceCommandResponse> ExecuteResourceCommandAsync(ResourceCommandRequest request, CallOptions options)
        {
            CommandCalls.Enqueue(options.Headers?.GetValue("x-aspire-apphost-run-id"));
            if (CancelExecuteResourceCommandOnCallCancellation)
            {
                return new AsyncUnaryCall<ResourceCommandResponse>(
                    WaitForCallCancellationAsync(options.CancellationToken, ThrowOperationCanceledOnCommandCancellation),
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

        private static async Task<ResourceCommandResponse> WaitForCallCancellationAsync(CancellationToken cancellationToken, bool throwOperationCanceled)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (!throwOperationCanceled)
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
                    ResourceUpdatesProvider?.Invoke() ?? ResourceUpdatesChannel);

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
        public Func<IReadOnlyList<Resource>, Task>? OnReplaceResourcesAsync { get; init; }

        public Task ReplaceResourcesAsync(IReadOnlyList<Resource> resources)
        {
            return OnReplaceResourcesAsync?.Invoke(resources) ?? Task.CompletedTask;
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
        public Func<T, Task>? OnWrite { get; init; }
        public Func<Task>? OnComplete { get; init; }

        public Task CompleteAsync()
        {
            return OnComplete?.Invoke() ?? throw new NotImplementedException();
        }

        public Task WriteAsync(T message)
        {
            return OnWrite?.Invoke(message) ?? throw new NotImplementedException();
        }

        public Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }
    }

    private DashboardClient CreateResourceServiceClient(
        DashboardActivitySource? activitySource = null,
        IResourceRepositoryWriter? resourceRepositoryWriter = null,
        string? applicationName = null,
        ResourceServiceClientCertificateOptions? clientCertificate = null,
        Action<SocketsHttpHandler>? configureHttpHandler = null,
        DashboardDataSourcePool? dataSourcePool = null)
    {
        var options = new DashboardOptions
        {
            ApplicationName = applicationName,
            ResourceServiceClient =
            {
                AuthMode = ResourceClientAuthMode.Unsecured,
                Url = "http://localhost:12345"
            }
        };
        if (clientCertificate is not null)
        {
            options.ResourceServiceClient.AuthMode = ResourceClientAuthMode.Certificate;
            options.ResourceServiceClient.ClientCertificate = clientCertificate;
        }

        options.ResourceServiceClient.TryParseOptions(out _);

        return new DashboardClient(
            activitySource ?? new DashboardActivitySource(),
            _loggerFactory,
            new ConfigurationManager(),
            Options.Create(options),
            new MockKnownPropertyLookup(),
            new TestStringLocalizer<DashboardResources>(),
            dataSourcePool: dataSourcePool,
            resourceRepositoryWriter: resourceRepositoryWriter ?? new RecordingResourceRepositoryWriter(),
            configureHttpHandler: configureHttpHandler);
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
