// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ProtoResource = Aspire.DashboardService.Proto.V1.Resource;

namespace Aspire.Dashboard.Backend.Tests;

public class DashboardBackendApplicationTests
{
    [Fact]
    public async Task Discovery_AdvertisesImplementedVersionedCapabilities()
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(
            "{\"product\":\"Aspire.Dashboard\",\"versions\":[{\"version\":1,\"basePath\":\"/api/dashboard/v1\",\"capabilities\":[\"configuration\",\"resources\",\"resources-live\",\"commands\"]}]}",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("null")]
    [InlineData("file://")]
    public async Task DevelopmentAccessPolicy_RejectsNonLoopbackBrowserOrigins(string origin)
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, DashboardApiContract.DiscoveryPath);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:1430")]
    [InlineData("https://Stress.dev.localhost:49985")]
    [InlineData("http://127.0.0.1:1430")]
    [InlineData("http://[::1]:1430")]
    public async Task DevelopmentAccessPolicy_AllowsLoopbackBrowserOrigins(string origin)
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, DashboardApiContract.DiscoveryPath);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.42.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("2001:db8::1", false)]
    public void DevelopmentAccessPolicy_RestrictsConnectionsToLoopback(string address, bool expected)
    {
        Assert.Equal(expected, DashboardDevelopmentAccessPolicy.IsLoopback(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task GetConfiguration_ReturnsConfiguredIdentityFromVersionOneRoute()
    {
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DashboardBackend:ApplicationName"] = "Stress AppHost",
                ["DashboardBackend:Version"] = "13.5.0-aot"
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/v1/config", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("Stress AppHost", root.GetProperty("applicationName").GetString());
        Assert.Equal("13.5.0-aot", root.GetProperty("dashboardVersion").GetString());
        Assert.StartsWith(".NET", root.GetProperty("runtimeVersion").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task GetResources_ReturnsSourceGeneratedSnapshotFromVersionOneRoute()
    {
        DashboardResource[] resources =
        [
            new DashboardResource(
                "api-abc123",
                "Project",
                "api",
                "resource-1",
                "Running",
                "success",
                "Healthy",
                DateTime.Parse("2026-07-13T12:00:00Z"),
                DateTime.Parse("2026-07-13T12:00:01Z"),
                null,
                [new("http", "https://api.example.test", false, false, "API", 1)],
                [new("project.path", "Project path", "/src/api.csproj", false, true, 10)],
                [new("ASPNETCORE_ENVIRONMENT", "Development", true)],
                [new("Healthy", "ready", "Ready")],
                [new("restart", "Restart", "Restart API", null, "ArrowCounterclockwise", "regular", true, "enabled")],
                [new("postgres", "Reference")],
                false,
                true,
                "Code",
                "filled",
                false,
                null)
        ];

        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardResourceSnapshotProvider>(new TestResourceSnapshotProvider(resources));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/v1/resources", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var resource = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("api-abc123", resource.GetProperty("name").GetString());
        Assert.Equal("https://api.example.test", resource.GetProperty("urls")[0].GetProperty("url").GetString());
        Assert.Equal("/src/api.csproj", resource.GetProperty("properties")[0].GetProperty("value").GetString());
        Assert.Equal("enabled", resource.GetProperty("commands")[0].GetProperty("state").GetString());
        Assert.Equal(22, resource.EnumerateObject().Count());
    }

    [Fact]
    public async Task GetResources_ReturnsServiceUnavailableWithoutResourceServiceConfiguration()
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/v1/resources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResources_ReturnsServiceUnavailableWhenConfiguredResourceServiceCannotConnect()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)socket.LocalEndPoint!;

        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = $"http://127.0.0.1:{endpoint.Port}",
                ["DashboardBackend:InitialSnapshotTimeout"] = "00:00:01"
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/v1/resources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("will keep retrying", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCommand_ReturnsSourceGeneratedResponseFromVersionOneRoute()
    {
        var result = new DashboardCommandResponse(
            "succeeded",
            "Restarted",
            new DashboardCommandResult("done", "text", true));
        var executor = new TestCommandExecutor(result);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardCommandExecutor>(executor);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/dashboard/v1/commands/execute",
            new { resourceName = "api", commandName = "restart" },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(new DashboardExecuteCommandRequest("api", "restart"), executor.Request);
        Assert.Equal(
            "{\"kind\":\"succeeded\",\"message\":\"Restarted\",\"result\":{\"value\":\"done\",\"format\":\"text\",\"displayImmediately\":true}}",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"resourceName\":\"api\",\"commandName\":\"\"}")]
    public async Task ExecuteCommand_RejectsInvalidRequests(string content)
    {
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardCommandExecutor>(new TestCommandExecutor(null));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.PostAsync(
            "/api/dashboard/v1/commands/execute",
            new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteCommand_ReturnsNotFoundForUnknownCommand()
    {
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardCommandExecutor>(new TestCommandExecutor(null));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/dashboard/v1/commands/execute",
            new { resourceName = "api", commandName = "missing" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResourceSnapshot_RecoversAfterInitialConnectionFailure()
    {
        var service = new DashboardResourceSnapshotService(
            new ConfigurationBuilder().Build(),
            NullLoggerFactory.Instance,
            NullLogger<DashboardResourceSnapshotService>.Instance);
        service.ReportInitialFailure("Unavailable");

        var exception = await Assert.ThrowsAsync<DashboardResourceServiceUnavailableException>(async () =>
            await service.GetSnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Unavailable", exception.Message);

        var initialUpdate = new WatchResourcesUpdate
        {
            InitialData = new InitialResourceData()
        };
        initialUpdate.InitialData.Resources.Add(new ProtoResource
        {
            Name = "api",
            DisplayName = "API",
            ResourceType = "Project",
            Uid = "resource-1"
        });
        service.ApplyUpdate(initialUpdate);

        Assert.Equal("api", Assert.Single(await service.GetSnapshotAsync(TestContext.Current.CancellationToken)).Name);
    }

    [Fact]
    public void ResourceMapper_ProjectsResourceServiceContractWithoutDashboardDependencies()
    {
        var resource = new ProtoResource
        {
            Name = "worker-xyz",
            ResourceType = "Executable",
            DisplayName = "worker",
            Uid = "resource-2",
            State = "Running",
            StateStyle = "success",
            CreatedAt = Timestamp.FromDateTime(DateTime.Parse("2026-07-13T12:00:00Z").ToUniversalTime()),
            StartedAt = Timestamp.FromDateTime(DateTime.Parse("2026-07-13T12:00:01Z").ToUniversalTime()),
            SupportsDetailedTelemetry = true,
            IconName = "WindowConsole",
            IconVariant = IconVariant.Filled
        };
        resource.Urls.Add(new Url
        {
            EndpointName = "http",
            FullUrl = "http://localhost:5000",
            DisplayProperties = new UrlDisplayProperties { DisplayName = "HTTP", SortOrder = 2 }
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "terminal.enabled",
            Value = Value.ForString("true")
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "terminal.replicaIndex",
            Value = Value.ForString("3")
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "resource.state",
            Value = Value.ForString("Running")
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "executable.pid",
            DisplayName = "Process ID",
            Value = Value.ForString("123"),
            SortOrder = 2
        });
        resource.HealthReports.Add(new HealthReport
        {
            Key = "live",
            Status = HealthStatus.Degraded,
            Description = "Slow"
        });
        resource.Commands.Add(new ResourceCommand
        {
            Name = "restart",
            DisplayName = "Restart",
            IconVariant = IconVariant.Regular,
            State = ResourceCommandState.Disabled
        });

        var result = DashboardResourceSnapshotService.Map(resource);

        Assert.Equal("worker", result.DisplayName);
        Assert.Equal("Degraded", result.Health);
        Assert.True(result.HasTerminal);
        Assert.Equal(3, result.TerminalReplicaIndex);
        Assert.Equal("filled", result.IconVariant);
        Assert.Equal("disabled", Assert.Single(result.Commands).State);
        Assert.Equal("HTTP", Assert.Single(result.Urls).DisplayName);
        Assert.Equal("http://localhost:5000/", Assert.Single(result.Urls).Url);
        Assert.Collection(
            result.Properties,
            property =>
            {
                Assert.Equal("resource.state", property.Name);
                Assert.Equal("State", property.DisplayName);
                Assert.Equal(1, property.SortOrder);
            },
            property =>
            {
                Assert.Equal("executable.pid", property.Name);
                Assert.Equal(9, property.SortOrder);
            },
            property => Assert.Equal("terminal.enabled", property.Name),
            property => Assert.Equal("terminal.replicaIndex", property.Name));
    }

    [Fact]
    public async Task ResourceEventSource_SendsSnapshotBeforeIncrementalChanges()
    {
        var service = new DashboardResourceSnapshotService(
            new ConfigurationBuilder().Build(),
            NullLoggerFactory.Instance,
            NullLogger<DashboardResourceSnapshotService>.Instance);
        var initialResource = new ProtoResource
        {
            Name = "api",
            DisplayName = "API",
            ResourceType = "Project",
            Uid = "resource-1",
            State = "Starting"
        };
        var initialUpdate = new WatchResourcesUpdate
        {
            InitialData = new InitialResourceData()
        };
        initialUpdate.InitialData.Resources.Add(initialResource);
        service.ApplyUpdate(initialUpdate);

        await using var events = service
            .WatchAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await events.MoveNextAsync());
        var snapshot = events.Current;
        Assert.Equal("snapshot", snapshot.Type);
        Assert.Equal("Starting", Assert.Single(snapshot.Resources!).State);
        Assert.Null(snapshot.Upserts);
        Assert.Null(snapshot.Deletes);

        var updatedResource = initialResource.Clone();
        updatedResource.State = "Running";
        var changes = new WatchResourcesUpdate
        {
            Changes = new WatchResourcesChanges()
        };
        changes.Changes.Value.Add(new WatchResourcesChange { Upsert = updatedResource });
        changes.Changes.Value.Add(new WatchResourcesChange
        {
            Delete = new ResourceDeletion { ResourceName = "worker", ResourceType = "Project" }
        });
        service.ApplyUpdate(changes);

        Assert.True(await events.MoveNextAsync());
        var change = events.Current;
        Assert.Equal("change", change.Type);
        Assert.Equal("Running", Assert.Single(change.Upserts!).State);
        Assert.Equal("worker", Assert.Single(change.Deletes!));
        Assert.Null(change.Resources);
    }

    [Fact]
    public async Task ResourceHub_StreamsSourceGeneratedSnapshotAndChanges()
    {
        DashboardResource[] resources =
        [
            new DashboardResource(
                "api",
                "Project",
                "API",
                "resource-1",
                "Running",
                "success",
                "Healthy",
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                [],
                [],
                false,
                true,
                "Code",
                "regular",
                false,
                null)
        ];
        DashboardResourcesEvent[] resourceEvents =
        [
            DashboardResourcesEvent.Snapshot(resources),
            DashboardResourcesEvent.Change(resources, ["worker"])
        ];

        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardResourceEventSource>(new TestResourceEventSource(resourceEvents));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{DashboardApiContract.ResourceStreamPath}", options =>
            {
                options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DashboardBackendJsonSerializerContext.Default);
            })
            .Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);

        await using var events = connection
            .StreamAsync<DashboardResourcesEvent>(nameof(DashboardResourcesHub.WatchResources), TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("snapshot", events.Current.Type);
        Assert.Equal("api", Assert.Single(events.Current.Resources!).Name);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("change", events.Current.Type);
        Assert.Equal("api", Assert.Single(events.Current.Upserts!).Name);
        Assert.Equal("worker", Assert.Single(events.Current.Deletes!));
    }

    private sealed class TestResourceSnapshotProvider(DashboardResource[] resources) : IDashboardResourceSnapshotProvider
    {
        public ValueTask<DashboardResource[]> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(resources);
        }
    }

    private sealed class TestResourceEventSource(DashboardResourcesEvent[] events) : IDashboardResourceEventSource
    {
        public async IAsyncEnumerable<DashboardResourcesEvent> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var resourceEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return resourceEvent;
                await Task.Yield();
            }
        }
    }

    private sealed class TestCommandExecutor(DashboardCommandResponse? response) : IDashboardCommandExecutor
    {
        public DashboardExecuteCommandRequest? Request { get; private set; }

        public ValueTask<DashboardCommandResponse?> ExecuteAsync(
            DashboardExecuteCommandRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(response);
        }
    }
}
