// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Qdrant.Client;
using Xunit;

namespace Aspire.Qdrant.Client.Tests;

public class AspireQdrantClientExtensionsTest : IClassFixture<QdrantContainerFixture>
{
    private const string DefaultConnectionName = "qdrant";

    private readonly QdrantContainerFixture _containerFixture;

    public AspireQdrantClientExtensionsTest(QdrantContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
    }

    private string DefaultConnectionString =>
            RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker) ? _containerFixture.GetConnectionString() : "Endpoint=http://localhost1:6331;Key=pass";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AddQdrantClient_HealthCheckShouldBeRegisteredWhenEnabled(bool useKeyed)
    {
        var key = DefaultConnectionName;

        var builder = CreateBuilder(DefaultConnectionString);

        if (useKeyed)
        {
            builder.AddKeyedQdrantClient(key);
        }
        else
        {
            builder.AddQdrantClient(DefaultConnectionName);
        }

        using var host = builder.Build();

        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        var healthCheckReport = await healthCheckService.CheckHealthAsync();

        var healthCheckName = useKeyed ? $"Qdrant.Client_{key}" : "Qdrant.Client";

        Assert.Contains(healthCheckReport.Entries, x => x.Key == healthCheckName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddQdrant_HealthCheckShouldNotBeRegisteredWhenDisabled(bool useKeyed)
    {
        var builder = CreateBuilder(DefaultConnectionString);

        if (useKeyed)
        {
            builder.AddKeyedQdrantClient(DefaultConnectionName, settings =>
            {
                settings.DisableHealthChecks = true;
            });
        }
        else
        {
            builder.AddQdrantClient(DefaultConnectionName, settings =>
            {
                settings.DisableHealthChecks = true;
            });
        }

        using var host = builder.Build();

        var healthCheckService = host.Services.GetService<HealthCheckService>();

        Assert.Null(healthCheckService);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:qdrant1", "Endpoint=http://localhost1:6331;Key=pass"),
            new KeyValuePair<string, string?>("ConnectionStrings:qdrant2", "Endpoint=http://localhost2:6332;Key=pass"),
            new KeyValuePair<string, string?>("ConnectionStrings:qdrant3", "Endpoint=http://localhost3:6333;Key=pass"),
        ]);

        builder.AddQdrantClient("qdrant1");
        builder.AddKeyedQdrantClient("qdrant2");
        builder.AddKeyedQdrantClient("qdrant3");

        using var host = builder.Build();

        var client1 = host.Services.GetRequiredService<QdrantClient>();
        var client2 = host.Services.GetRequiredKeyedService<QdrantClient>("qdrant2");
        var client3 = host.Services.GetRequiredKeyedService<QdrantClient>("qdrant3");

        Assert.NotSame(client1, client2);
        Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);
    }

    private static HostApplicationBuilder CreateBuilder(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>($"ConnectionStrings:{DefaultConnectionName}", connectionString)
        ]);
        return builder;
    }
}
