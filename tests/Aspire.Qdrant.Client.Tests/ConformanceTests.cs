// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qdrant.Client;
using Xunit;

namespace Aspire.Qdrant.Client.Tests;

public class ConformanceTests : ConformanceTests<QdrantClient, QdrantClientSettings>, IClassFixture<QdrantContainerFixture>
{
    private readonly QdrantContainerFixture _containerFixture;

    private readonly string _connectionString;

    protected override bool SupportsKeyedRegistrations => true;

    protected override bool CanCreateClientWithoutConnectingToServer => false;

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string[] RequiredLogCategories => Array.Empty<string>();

    protected override string ActivitySourceName => "";

    protected override string? ConfigurationSectionName => "Aspire:Qdrant:Client";

    public ConformanceTests(QdrantContainerFixture containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
        _connectionString = RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker) ?
            $"{_containerFixture.GetConnectionString()}" :
            "Endpoint=http://localhost:6334;Key=pass";
    }

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<QdrantClientSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddQdrantClient("qdrant", configure);
        }
        else
        {
            builder.AddKeyedQdrantClient(key, configure);
        }
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
    {
        configuration.AddInMemoryCollection(
            [
             new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Qdrant:Client", key, "Endpoint"), GetConnectionStringKeyValue(_connectionString,"Endpoint")),
             new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Qdrant:Client", key, "Key"), GetConnectionStringKeyValue(_connectionString,"Key")),
            ]);
    }

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

    protected override void TriggerActivity(QdrantClient service)
    {
    }

    protected override void SetHealthCheck(QdrantClientSettings options, bool enabled)
    {
        options.DisableHealthChecks = !enabled;
    }

    protected override void SetTracing(QdrantClientSettings options, bool enabled) => throw new NotImplementedException();

    protected override void SetMetrics(QdrantClientSettings options, bool enabled) => throw new NotImplementedException();

    protected override string ValidJsonConfig => """
                                                 {
                                                   "Aspire": {
                                                     "Qdrant": {
                                                       "Client": {
                                                         "Endpoint": "http://localhost:6334"
                                                       }
                                                     }
                                                   }
                                                 }
                                                 """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Qdrant":{ "Client": { "Endpoint": 3 }}}}""", "Value is \"integer\" but should be \"string\""),
            ("""{"Aspire": { "Qdrant":{ "Client": { "Endpoint": "hello" }}}}""", "Value does not match format \"uri\"")
        };
}
