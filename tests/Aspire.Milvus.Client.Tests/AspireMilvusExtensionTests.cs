// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Milvus.Client;
using Xunit;

namespace Aspire.Milvus.Client.Tests;
public class AspireMilvusExtensionTests : IClassFixture<MilvusContainerFixture>
{
    private readonly MilvusContainerFixture _containerFixture;
    internal const string DefaultKeyName = "milvus";
    internal const string DefaultApiKey = "root:Milvus";

    private string ConnectionString => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker)
                                        ? _containerFixture.GetConnectionString()
                                        : $"Endpoint=http://localhost:19530/;Key={DefaultApiKey}";

    private string NormalizedConnectionString => ConnectionString;

    public AspireMilvusExtensionTests(MilvusContainerFixture containerFixture)
        => _containerFixture = containerFixture;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>($"ConnectionStrings:{DefaultKeyName}", ConnectionString)
        });

        if (useKeyed)
        {
            builder.AddKeyedMilvusClient(DefaultKeyName);
        }
        else
        {
            builder.AddMilvusClient(DefaultKeyName);
        }

        using var host = builder.Build();
        var dataSource = useKeyed ?
            host.Services.GetRequiredKeyedService<MilvusClient>(DefaultKeyName) :
            host.Services.GetRequiredService<MilvusClient>();

        Assert.Equal(NormalizedConnectionString, $"Endpoint=http://{dataSource.Address}/;Key={DefaultApiKey}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        if (useKeyed)
        {
            builder.AddKeyedMilvusClient(DefaultKeyName, settings => { settings.Endpoint = new Uri("http://localhost:19530"); settings.Key = DefaultApiKey; });
        }
        else
        {
            builder.AddMilvusClient(DefaultKeyName, settings => { settings.Endpoint = new Uri("http://localhost:19530"); settings.Key = DefaultApiKey; });
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<MilvusClient>(DefaultKeyName) :
            host.Services.GetRequiredService<MilvusClient>();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "milvus" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Milvus:Client", key, "Endpoint"), "unused"),
            new KeyValuePair<string, string?>($"ConnectionStrings:{DefaultKeyName}", ConnectionString)
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
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<MilvusClient>(DefaultKeyName) :
            host.Services.GetRequiredService<MilvusClient>();

        Assert.Equal(ConnectionString, $"Endpoint=http://{connection.Address}/;Key={DefaultApiKey}");
        // the connection string from config should not be used since it was found in ConnectionStrings
        Assert.DoesNotContain("unused", connection.Address);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddMilvusClient_HealthCheckShouldBeRegisteredByDefault(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var key = useKeyed ? DefaultKeyName : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Milvus:Client", key, "Endpoint"), "unused"),
            new KeyValuePair<string, string?>($"ConnectionStrings:{DefaultKeyName}", ConnectionString)
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

        var healthCheckReport = await healthCheckService.CheckHealthAsync();

        var healthCheckName = useKeyed ? $"Milvus_{DefaultKeyName}" : "Milvus";

        Assert.Contains(healthCheckReport.Entries, x => x.Key == healthCheckName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddMilvusClient_HealthCheckShouldNotBeRegisteredWhenDisabled(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        if (useKeyed)
        {
            builder.AddKeyedMilvusClient(DefaultKeyName, settings =>
            {
                settings.DisableHealthChecks = true;
            });
        }
        else
        {
            builder.AddMilvusClient(DefaultKeyName, settings =>
            {
                settings.DisableHealthChecks = true;
            });
        }

        using var host = builder.Build();

        var healthCheckService = host.Services.GetService<HealthCheckService>();

        Assert.Null(healthCheckService);
    }
}
