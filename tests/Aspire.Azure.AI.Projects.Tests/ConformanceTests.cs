// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.AI.Projects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.AI.Projects.Tests;

public class ConformanceTests : ConformanceTests<AIProjectClient, AzureAIProjectsSettings>
{
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;
    protected override bool SupportsKeyedRegistrations => true;
    protected override string? ConfigurationSectionName => "Aspire:Azure:AI:Projects";
    protected override string ActivitySourceName => "Azure.AI.Projects.AIProjectConnectionsOperations";
    protected override RequiredLogCategory[] RequiredLogCategories => [new("Azure.Identity")];
    protected override string ValidJsonConfig => """{"Aspire":{"Azure":{"AI":{"Projects":{"Endpoint":"https://test.services.ai.azure.com/api/projects/project","DisableTracing":false}}}}}""";
    protected override (string json, string error)[] InvalidJsonToErrorMessage =>
    [
        ("""{"Aspire":{"Azure":{"AI":{"Projects":{"Endpoint":"relative"}}}}}""", "Value does not match format \"uri\""),
        ("""{"Aspire":{"Azure":{"AI":{"Projects":{"DisableTracing":"true"}}}}}""", "Value is \"string\" but should be \"boolean\"")
    ];

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration[CreateConfigKey(ConfigurationSectionName!, key, "Endpoint")] = "https://test.services.ai.azure.com/api/projects/project";

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureAIProjectsSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureAIProjectClient("project", configure);
        }
        else
        {
            builder.AddKeyedAzureAIProjectClient(key, configure);
        }
    }

    public ConformanceTests(ITestOutputHelper? output = null) : base(output)
    {
    }

    [Fact]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(() => new ConformanceTests().ActivitySourceTest(null), Output);

    [Fact]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteInvokeWithLogging(() => new ConformanceTests().ActivitySourceTest("key"), Output);

    protected override void SetHealthCheck(AzureAIProjectsSettings options, bool enabled)
        => throw new NotImplementedException();
    protected override void SetMetrics(AzureAIProjectsSettings options, bool enabled)
        => throw new NotImplementedException();
    protected override void SetTracing(AzureAIProjectsSettings options, bool enabled)
        => options.DisableTracing = !enabled;
    protected override void TriggerActivity(AIProjectClient service)
        => service.Connections.GetConnection("test");
}
