// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Milvus.Client;
using Xunit;

namespace Aspire.Milvus.Client.Tests;

public class ConformanceTests : ConformanceTests<MilvusClient, MilvusClientSettings>, IClassFixture<MilvusContainerFixture>
{
    private readonly MilvusContainerFixture? _containerFixture;
    private string ConnectionString { get; set; }

    public ConformanceTests(MilvusContainerFixture? containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
        ConnectionString = (_containerFixture is not null && RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
                                        ? _containerFixture.GetConnectionString()
                                        : $"Endpoint=http://localhost:19530;Key=root:Milvus";
    }
    protected override bool SupportsKeyedRegistrations => true;

    protected override bool CanCreateClientWithoutConnectingToServer => true;

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string[] RequiredLogCategories => Array.Empty<string>();

    protected override string ActivitySourceName => "";

    protected override string? ConfigurationSectionName => "Aspire:Milvus:Client";

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<MilvusClientSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddMilvusClient("milvus", configure);
        }
        else
        {
            builder.AddKeyedMilvusClient(key, configure);
        }
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[2]
        {
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Milvus:Client", key, "Endpoint"), GetConnectionStringKeyValue(ConnectionString, "Endpoint")),
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Milvus:Client", key, "Key"), GetConnectionStringKeyValue(ConnectionString, "Key"))
        });

    internal static string GetConnectionStringKeyValue(string connectionString, string configKey)
    {
        // from the connection string, extract the key value of the configKey
        var parts = connectionString.Split(';');
        foreach (var part in parts)
        {
            var keyValue = part.Split('=');
            if (keyValue.Length == 2 && keyValue[0].Equals(configKey, StringComparison.OrdinalIgnoreCase))
            {
                return keyValue[1];
            }
        }
        return string.Empty;
    }

    protected override void TriggerActivity(MilvusClient service)
    {
        service.GetVersionAsync().Wait();
    }

    protected override void SetHealthCheck(MilvusClientSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(MilvusClientSettings options, bool enabled) => throw new NotImplementedException();

    protected override void SetMetrics(MilvusClientSettings options, bool enabled) => throw new NotImplementedException();

    protected override string ValidJsonConfig => """
                                                 {
                                                   "Aspire": {
                                                     "Milvus": {
                                                       "Client": {
                                                         "Endpoint": "http://localhost:19530"
                                                       }
                                                     }
                                                   }
                                                 }
                                                 """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Milvus":{ "Client": { "Endpoint": 3 }}}}""", "Value is \"integer\" but should be \"string\""),
            ("""{"Aspire": { "Milvus":{ "Client": { "Endpoint": "hello" }}}}""", "Value does not match format \"uri\"")
        };

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:milvus1", "Endpoint=http://localhost1:19530;Key=pass"),
            new KeyValuePair<string, string?>("ConnectionStrings:milvus2", "Endpoint=http://localhost2:19531;Key=pass"),
            new KeyValuePair<string, string?>("ConnectionStrings:milvus3", "Endpoint=http://localhost3:19532;Key=pass"),
        ]);

        builder.AddMilvusClient("milvus1");
        builder.AddKeyedMilvusClient("milvus2");
        builder.AddKeyedMilvusClient("milvus3");

        using var host = builder.Build();

        var client1 = host.Services.GetRequiredService<MilvusClient>();
        var client2 = host.Services.GetRequiredKeyedService<MilvusClient>("milvus2");
        var client3 = host.Services.GetRequiredKeyedService<MilvusClient>("milvus3");

        Assert.NotSame(client1, client2);
        Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);
    }
}
