// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Microsoft.Azure.Cosmos.Tests;

public class ConformanceTests : ConformanceTests<CosmosClient, MicrosoftAzureCosmosSettings>
{
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "Azure.Cosmos.Operation";

    protected override RequiredLogCategory[] RequiredLogCategories => [];

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[1]
        {
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Microsoft:Azure:Cosmos", key, "ConnectionString"),
                "AccountEndpoint=https://example.documents.azure.com:443/;AccountKey=fake;")
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<MicrosoftAzureCosmosSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureCosmosClient("cosmosdb", configure);
        }
        else
        {
            builder.AddKeyedAzureCosmosClient(key, configure);
        }
    }

    protected override void SetHealthCheck(MicrosoftAzureCosmosSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(MicrosoftAzureCosmosSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(MicrosoftAzureCosmosSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Microsoft": {
              "Azure": {
                "Cosmos": {
                  "ConnectionString": "YOUR_CONNECTION_STRING",
                  "DisableTracing": false
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Microsoft":{ "Azure": { "Cosmos": { "AccountEndpoint": 3 }}}}}""", "Value is \"integer\" but should be \"string\""),
            ("""{"Aspire": { "Microsoft":{ "Azure": { "Cosmos": { "AccountEndpoint": "hello" }}}}}""", "Value does not match format \"uri\"")
        };

    protected override void TriggerActivity(CosmosClient service)
    {
        // TODO: Get rid of GetAwaiter().GetResult()
        service.ReadAccountAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:cosmosdb1", "AccountEndpoint=https://example1.documents.azure.com:443/;AccountKey=fake;"),
            new KeyValuePair<string, string?>("ConnectionStrings:cosmosdb2", "AccountEndpoint=https://example2.documents.azure.com:443/;AccountKey=fake;"),
            new KeyValuePair<string, string?>("ConnectionStrings:cosmosdb3", "AccountEndpoint=https://example3.documents.azure.com:443/;AccountKey=fake;"),
        ]);

        builder.AddAzureCosmosClient("cosmosdb1");
        builder.AddKeyedAzureCosmosClient("cosmosdb2");
        builder.AddKeyedAzureCosmosClient("cosmosdb3");

        using var host = builder.Build();

        var client1 = host.Services.GetRequiredService<CosmosClient>();
        var client2 = host.Services.GetRequiredKeyedService<CosmosClient>("cosmosdb2");
        var client3 = host.Services.GetRequiredKeyedService<CosmosClient>("cosmosdb3");

        Assert.NotSame(client1, client2);
        Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);

        Assert.Equal("https://example1.documents.azure.com/", client1.Endpoint.ToString());
        Assert.Equal("https://example2.documents.azure.com/", client2.Endpoint.ToString());
        Assert.Equal("https://example3.documents.azure.com/", client3.Endpoint.ToString());
    }
}
