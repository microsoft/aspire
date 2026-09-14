// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.Common.TestUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Milvus.Client.Tests;

public class MilvusHealthCheckTests
{
    private const string DefaultKeyName = "milvus";
    private const string DefaultApiKey = "root:Milvus";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HealthCheckReportsConnectionFailureDescription(bool useKeyed)
    {
        var endpoint = ComponentTestUrls.CreateUnavailableHttpUri();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var key = useKeyed ? DefaultKeyName : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Milvus:Client", key, "Endpoint"), "unused"),
            new KeyValuePair<string, string?>($"ConnectionStrings:{DefaultKeyName}", $"Endpoint={endpoint};Key={DefaultApiKey}")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedMilvusClient(DefaultKeyName);
        }
        else
        {
            builder.AddMilvusClient(DefaultKeyName);
        }

        using var host = builder.Build();
        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var healthCheckReport = await healthCheckService.CheckHealthAsync(cts.Token);
        var healthCheckName = useKeyed ? $"Milvus_{DefaultKeyName}" : "Milvus";
        Assert.True(healthCheckReport.Entries.TryGetValue(healthCheckName, out var entry));

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal("Failed to connect to Milvus server.", entry.Description);
        Assert.NotNull(entry.Exception);
    }
}
