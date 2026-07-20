// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Threading.Channels;
using Aspire.Dashboard.Configuration;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using DashboardServiceBase = Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceBase;

namespace Aspire.Dashboard.Tests.Integration;

public sealed class DashboardClientAuthTests
{
    private const string ApiKeyHeaderName = "x-resource-service-api-key";

    private readonly ITestOutputHelper _testOutputHelper;

    public DashboardClientAuthTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectsToResourceService_Unsecured(bool useHttps)
    {
        using var activitySource = new DashboardActivitySource();
        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper);
        await using var server = await CreateResourceServiceServerAsync(loggerFactory, useHttps).DefaultTimeout();
        await using var client = await CreateDashboardClientAsync(activitySource, loggerFactory, server.Url, authMode: ResourceClientAuthMode.Unsecured).DefaultTimeout();

        var call = await server.Calls.ResourceInformationCallsChannel.Reader.ReadAsync().DefaultTimeout();

        Assert.NotNull(call.Request);
        Assert.NotNull(call.RequestHeaders);
        Assert.Null(call.RequestHeaders.Get(ApiKeyHeaderName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectsToResourceService_ApiKey(bool useHttps)
    {
        using var activitySource = new DashboardActivitySource();
        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper);
        await using var server = await CreateResourceServiceServerAsync(loggerFactory, useHttps).DefaultTimeout();
        await using var client = await CreateDashboardClientAsync(activitySource, loggerFactory, server.Url, authMode: ResourceClientAuthMode.ApiKey, configureOptions: options => options.ResourceServiceClient.ApiKey = "TestApiKey!").DefaultTimeout();

        var call = await server.Calls.ResourceInformationCallsChannel.Reader.ReadAsync().DefaultTimeout();

        Assert.NotNull(call.Request);
        Assert.NotNull(call.RequestHeaders);
        Assert.Equal("TestApiKey!", call.RequestHeaders.GetValue(ApiKeyHeaderName));
    }

    private static async Task<ResourceServiceServer> CreateResourceServiceServerAsync(ILoggerFactory loggerFactory, bool useHttps, Action<TestCalls>? configureCalls = null)
    {
        var serverAppBuilder = WebApplication.CreateSlimBuilder();

        var testCalls = new TestCalls();

        configureCalls?.Invoke(testCalls);

        serverAppBuilder.Services.AddGrpc(options => options.EnableDetailedErrors = true);
        serverAppBuilder.Services.AddSingleton(testCalls);
        serverAppBuilder.Services.AddSingleton(loggerFactory);
        serverAppBuilder.WebHost.ConfigureKestrel(ConfigureKestrel);

        var serverApp = serverAppBuilder.Build();

        serverApp.MapGrpcService<MockDashboardService>();
        serverApp.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

        await serverApp.StartAsync();

        return new(serverApp, testCalls);

        void ConfigureKestrel(KestrelServerOptions kestrelOptions)
        {
            // Listen on a random port.
            kestrelOptions.Listen(IPAddress.Loopback, port: 0, ConfigureListen);

            void ConfigureListen(ListenOptions options)
            {
                // Force HTTP/2 for gRPC, so that it works over non-TLS connections
                // which cannot negotiate between HTTP/1.1 and HTTP/2.
                options.Protocols = HttpProtocols.Http2;

                if (useHttps)
                {
                    options.UseHttps();
                }
            }
        }
    }

    private static async Task<DashboardClient> CreateDashboardClientAsync(
        DashboardActivitySource activitySource,
        ILoggerFactory loggerFactory,
        string serverAddress,
        ResourceClientAuthMode authMode = ResourceClientAuthMode.Unsecured,
        Action<DashboardOptions>? configureOptions = null)
    {
        var options = new DashboardOptions
        {
            ResourceServiceClient =
            {
                AuthMode = authMode,
                Url = serverAddress
            }
        };

        configureOptions?.Invoke(options);

        options.ResourceServiceClient.TryParseOptions(out _);

        var client = new DashboardClient(
            activitySource: activitySource,
            loggerFactory: loggerFactory,
            configuration: new ConfigurationManager(),
            dashboardOptions: Options.Create(options),
            knownPropertyLookup: new MockKnownPropertyLookup(),
            loc: new Aspire.Dashboard.Tests.TestStringLocalizer<Dashboard.Resources.Resources>(),
            configureHttpHandler: handler => handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true);

        var iClient = (IDashboardClient)client;

        await iClient.WhenConnected;

        return client;
    }

    private sealed class ResourceServiceServer(WebApplication serverApp, TestCalls testCalls) : IAsyncDisposable
    {
        public TestCalls Calls { get; } = testCalls;

        public string Url => serverApp.Urls.First();

        public async ValueTask DisposeAsync()
        {
            await serverApp.StopAsync();
            await serverApp.DisposeAsync();
        }
    }

    private sealed class TestCalls
    {
        public Channel<ReceivedCallInfo<ApplicationInformationRequest>> ResourceInformationCallsChannel { get; } = Channel.CreateUnbounded<ReceivedCallInfo<ApplicationInformationRequest>>();
    }

    private sealed class MockDashboardService(TestCalls testCalls) : DashboardServiceBase
    {
        public override Task<ApplicationInformationResponse> GetApplicationInformation(
            ApplicationInformationRequest request,
            ServerCallContext context)
        {
            testCalls.ResourceInformationCallsChannel.Writer.TryWrite(new ReceivedCallInfo<ApplicationInformationRequest>(request, context.RequestHeaders));

            return Task.FromResult(new ApplicationInformationResponse()
            {
                ApplicationName = "Test application"
            });
        }

        public override Task WatchResources(
            WatchResourcesRequest request,
            IServerStreamWriter<WatchResourcesUpdate> responseStream,
            ServerCallContext context)
        {
            responseStream.WriteAsync(new WatchResourcesUpdate()
            {
                InitialData = new InitialResourceData()
                {
                    ResourceTypes = { new ResourceType() { UniqueName = "test", DisplayName = "Test" } },
                    Resources = { new Resource() { Name = "resource1", ResourceType = "test", Uid = "resource1", CreatedAt = Timestamp.FromDateTime(DateTime.Now) } }
                }
            });

            return Task.CompletedTask;
        }
    }

    private sealed class ReceivedCallInfo<T>(T request, Metadata requestHeaders) where T : IMessage
    {
        public T Request => request;
        public Metadata RequestHeaders => requestHeaders;
    }
}
