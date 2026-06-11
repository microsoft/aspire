// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.AI.OpenAI.Tests;

public class ConformanceTests : ConformanceTests<IChatClient, AzureOpenAISettings>
{
    protected const string Endpoint = "https://aspireopenaitests.openai.azure.com/";

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string[] RequiredLogCategories => [
        // since we don't have a way to connect to the server, we can't test the actual calls
        "Azure.Identity"
    ];

    protected override bool SupportsKeyedRegistrations => true;

    protected override string? ConfigurationSectionName => "Aspire:Azure:AI:OpenAI";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Azure": {
              "AI": {
                "OpenAI": {
                  "Endpoint": "http://YOUR_URI",
                  "DisableTracing": false,
                  "DisableMetrics": false,
                  "ClientOptions": {
                    "NetworkTimeout": "00:00:02"
                  }
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Azure": { "AI":{ "OpenAI": {"Endpoint": "YOUR_URI"}}}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "Azure": { "AI":{ "OpenAI": {"Endpoint": "http://YOUR_URI", "DisableTracing": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
        };

    protected override string ActivitySourceName => "Experimental.Microsoft.Extensions.AI";

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[]
        {
            new(CreateConfigKey("Aspire:Azure:AI:OpenAI", key, "Endpoint"), Endpoint),
            new(CreateConfigKey("Aspire:Azure:AI:OpenAI", key, "Deployment"), "DEPLOYMENT_NAME")
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureOpenAISettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureOpenAIClient("openai", ConfigureCredentials).AddChatClient();
        }
        else
        {
            builder.AddKeyedAzureOpenAIClient(key, ConfigureCredentials).AddKeyedChatClient(key);
        }

        void ConfigureCredentials(AzureOpenAISettings settings)
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
        => RemoteInvokeWithLogging(() => new ConformanceTests().ActivitySourceTest(key: null), Output);

    [Fact]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteInvokeWithLogging(() => new ConformanceTests().ActivitySourceTest(key: "key"), Output);

    protected override void SetHealthCheck(AzureOpenAISettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetMetrics(AzureOpenAISettings options, bool enabled)
        => options.DisableMetrics = !enabled;

    protected override void SetTracing(AzureOpenAISettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void TriggerActivity(IChatClient service)
    {
        service.GetResponseAsync(new ChatMessage()).GetAwaiter().GetResult();
    }
}
