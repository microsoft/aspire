// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Search.Documents.Tests;

public class ConformanceTests : ConformanceTests<SearchIndexClient, AzureSearchSettings>
{
    protected const string Endpoint = "https://aspireazuresearchtests.search.windows.net/";

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string[] RequiredLogCategories => ["Azure.Identity"];

    protected override bool SupportsKeyedRegistrations => true;

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Azure": {
              "Search": {
                "Documents": {
                  "Endpoint": "http://YOUR_URI",
                  "DisableTracing": false,
                  "ClientOptions": {
                    "Retry": {
                      "Mode": "Fixed",
                      "MaxDelay": "00:00:03"
                    }
                  }
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage =>
        [
            ("""{"Aspire": { "Azure": { "Search":{ "Documents": {"Endpoint": "YOUR_URI"}}}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "Azure": { "Search":{ "Documents": {"Endpoint": "http://YOUR_URI", "DisableTracing": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
        ];

    protected override string ActivitySourceName => "Azure.Search.Documents.SearchIndexClient";

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(
        [
            new(CreateConfigKey("Aspire:Azure:Search:Documents", key, "Endpoint"), Endpoint)
        ]);

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureSearchSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureSearchClient("search", ConfigureCredentials);
        }
        else
        {
            builder.AddKeyedAzureSearchClient(key, ConfigureCredentials);
        }

        void ConfigureCredentials(AzureSearchSettings settings)
        {
            if (CanConnectToServer)
            {
                settings.Credential = new DefaultAzureCredential();
            }

            configure?.Invoke(settings);
        }
    }

    public ConformanceTests(ITestOutputHelper? output = null) : base(output)
    {
    }

    [Fact]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(static () => new ConformanceTests().ActivitySourceTest(key: null), Output);

    [Fact]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteInvokeWithLogging(static () => new ConformanceTests().ActivitySourceTest(key: "key"), Output);

    protected override void SetHealthCheck(AzureSearchSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetMetrics(AzureSearchSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(AzureSearchSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void TriggerActivity(SearchIndexClient service)
        => service.GetIndex("my-index");
}
