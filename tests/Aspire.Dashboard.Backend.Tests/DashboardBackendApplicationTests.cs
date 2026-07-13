// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Dashboard.Backend.Tests;

public class DashboardBackendApplicationTests
{
    [Fact]
    public async Task Discovery_AdvertisesOnlyVersionedConfigurationCapability()
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(
            "{\"product\":\"Aspire.Dashboard\",\"versions\":[{\"version\":1,\"basePath\":\"/api/dashboard/v1\",\"capabilities\":[\"configuration\"]}]}",
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
}
