// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.Common.TestUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Qdrant.Client.Tests;

public class QdrantHealthCheckTests
{
    private const string DefaultConnectionName = "qdrant";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HealthCheckReportsConnectionFailureDescription(bool useKeyed)
    {
        var endpoint = ComponentTestUrls.CreateUnavailableHttpUri();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>($"ConnectionStrings:{DefaultConnectionName}", $"Endpoint={endpoint};Key=pass")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedQdrantClient(DefaultConnectionName);
        }
        else
        {
            builder.AddQdrantClient(DefaultConnectionName);
        }

        using var host = builder.Build();
        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var healthCheckReport = await healthCheckService.CheckHealthAsync(cts.Token);
        var healthCheckName = useKeyed ? $"Qdrant.Client_{DefaultConnectionName}" : "Qdrant.Client";
        Assert.True(healthCheckReport.Entries.TryGetValue(healthCheckName, out var entry));

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal("Failed to connect to Qdrant server.", entry.Description);
        Assert.NotNull(entry.Exception);
    }
}
