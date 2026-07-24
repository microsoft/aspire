// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.OpenAI.Tests;

public class ConformanceTests : ConformanceTests<IChatClient, OpenAISettings>
{
    protected const string ConnectionString = "Endpoint=https://api.openai.com/;Key=fake";

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override bool SupportsKeyedRegistrations => true;

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
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
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "OpenAI": {"Endpoint": "YOUR_URI"}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "OpenAI": {"Endpoint": "http://YOUR_URI", "DisableTracing": "true"}}}""", "Value is \"string\" but should be \"boolean\""),
        };

    protected override string ActivitySourceName => "Experimental.Microsoft.Extensions.AI";

    protected override RequiredLogCategory[] RequiredLogCategories => [];

    protected override string? ConfigurationSectionName => "Aspire:OpenAI";

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[]
        {
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:OpenAI", key, "Key"), "fake"),
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:OpenAI", key, "Deployment"), "DEPLOYMENT_NAME")
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<OpenAISettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddOpenAIClient("openai", configure).AddChatClient();
        }
        else
        {
            builder.AddKeyedOpenAIClient(key, configure).AddKeyedChatClient(key);
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

    protected override void SetHealthCheck(OpenAISettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetMetrics(OpenAISettings options, bool enabled)
        => options.DisableMetrics = !enabled;

    protected override void SetTracing(OpenAISettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void TriggerActivity(IChatClient service)
    {
        service.GetResponseAsync(new ChatMessage()).GetAwaiter().GetResult();
    }
}
