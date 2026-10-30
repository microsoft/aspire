// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.AI.Extensions.OpenAI.Tests;

[Collection("OpenAI telemetry")]
public class AzureOpenAIConformanceTests : ConformanceTests<IChatClient, AzureOpenAISettings>
{
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;
    protected override bool SupportsKeyedRegistrations => true;
    protected override string? ConfigurationSectionName => "Aspire:Azure:AI:OpenAI";
    protected override string ActivitySourceName => "Experimental.Microsoft.Extensions.AI";
    protected override RequiredLogCategory[] RequiredLogCategories => [new("Azure.Identity")];
    protected override string ValidJsonConfig => """{"Aspire":{"Azure":{"AI":{"OpenAI":{"Endpoint":"https://test.openai.azure.com","DisableTracing":false,"DisableMetrics":false}}}}}""";
    protected override (string json, string error)[] InvalidJsonToErrorMessage =>
    [
        ("""{"Aspire":{"Azure":{"AI":{"OpenAI":{"Endpoint":"relative"}}}}}""", "Value does not match format \"uri\""),
        ("""{"Aspire":{"Azure":{"AI":{"OpenAI":{"DisableTracing":"true"}}}}}""", "Value is \"string\" but should be \"boolean\"")
    ];

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
    {
        configuration[CreateConfigKey(ConfigurationSectionName!, key, "Endpoint")] = "https://test.openai.azure.com";
        configuration[CreateConfigKey(ConfigurationSectionName!, key, "Deployment")] = "chat";
    }

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureOpenAISettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureOpenAIClient("openai", configure).AddChatClient();
        }
        else
        {
            builder.AddKeyedAzureOpenAIClient(key, configure).AddKeyedChatClient(key);
        }
    }

    public AzureOpenAIConformanceTests(ITestOutputHelper? output = null) : base(output)
    {
    }

    [Fact]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(() => new AzureOpenAIConformanceTests().ActivitySourceTest(null), Output);

    [Fact]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteInvokeWithLogging(() => new AzureOpenAIConformanceTests().ActivitySourceTest("key"), Output);

    protected override void SetHealthCheck(AzureOpenAISettings options, bool enabled)
        => throw new NotImplementedException();
    protected override void SetMetrics(AzureOpenAISettings options, bool enabled)
        => options.DisableMetrics = !enabled;
    protected override void SetTracing(AzureOpenAISettings options, bool enabled)
        => options.DisableTracing = !enabled;
    protected override void TriggerActivity(IChatClient service)
        => service.GetResponseAsync(new ChatMessage()).GetAwaiter().GetResult();
}

[Collection("OpenAI telemetry")]
public class AzureProjectOpenAIConformanceTests : ConformanceTests<IChatClient, AzureProjectOpenAISettings>
{
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;
    protected override bool SupportsKeyedRegistrations => true;
    protected override string? ConfigurationSectionName => "Aspire:Azure:AI:Extensions:OpenAI";
    protected override string ActivitySourceName => "Experimental.Microsoft.Extensions.AI";
    protected override RequiredLogCategory[] RequiredLogCategories => [new("Azure.Identity")];
    protected override string ValidJsonConfig => """{"Aspire":{"Azure":{"AI":{"Extensions":{"OpenAI":{"Endpoint":"https://test.services.ai.azure.com/api/projects/project","DisableTracing":false,"DisableMetrics":false}}}}}}""";
    protected override (string json, string error)[] InvalidJsonToErrorMessage =>
    [
        ("""{"Aspire":{"Azure":{"AI":{"Extensions":{"OpenAI":{"Endpoint":"relative"}}}}}}""", "Value does not match format \"uri\""),
        ("""{"Aspire":{"Azure":{"AI":{"Extensions":{"OpenAI":{"DisableMetrics":"true"}}}}}}""", "Value is \"string\" but should be \"boolean\"")
    ];

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
    {
        configuration[CreateConfigKey(ConfigurationSectionName!, key, "Endpoint")] = "https://test.services.ai.azure.com/api/projects/project";
        configuration[CreateConfigKey(ConfigurationSectionName!, key, "Deployment")] = "chat";
    }

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureProjectOpenAISettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureProjectOpenAIClient("project", configure).AddChatClient();
        }
        else
        {
            builder.AddKeyedAzureProjectOpenAIClient(key, configure).AddKeyedChatClient(key);
        }
    }

    public AzureProjectOpenAIConformanceTests(ITestOutputHelper? output = null) : base(output)
    {
    }

    [Fact]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(() => new AzureProjectOpenAIConformanceTests().ActivitySourceTest(null), Output);

    [Fact]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteInvokeWithLogging(() => new AzureProjectOpenAIConformanceTests().ActivitySourceTest("key"), Output);

    protected override void SetHealthCheck(AzureProjectOpenAISettings options, bool enabled)
        => throw new NotImplementedException();
    protected override void SetMetrics(AzureProjectOpenAISettings options, bool enabled)
        => options.DisableMetrics = !enabled;
    protected override void SetTracing(AzureProjectOpenAISettings options, bool enabled)
        => options.DisableTracing = !enabled;
    protected override void TriggerActivity(IChatClient service)
        => service.GetResponseAsync(new ChatMessage()).GetAwaiter().GetResult();
}
