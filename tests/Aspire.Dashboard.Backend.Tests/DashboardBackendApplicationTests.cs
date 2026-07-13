// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            "{\"product\":\"Aspire.Dashboard\",\"versions\":[{\"version\":1,\"basePath\":\"/api/dashboard/v1\",\"capabilities\":[\"configuration\",\"resources\"]}]}",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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

    private sealed class TestResourceSnapshotProvider(DashboardResource[] resources) : IDashboardResourceSnapshotProvider
    {
        public ValueTask<DashboardResource[]> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(resources);
        }
    }
}
