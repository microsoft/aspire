// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Agents.Tests;

[Trait("Partition", "5")]
public class AgentReferenceEnvironmentTests
{
    [Fact]
    public async Task WithReference_A2AAgentRespectsReferenceEnvironmentInjectionFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("weather-agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => AllocateEndpoint(e, "weather-agent.dev.internal", 8080))
            .AsAgent(AgentProtocol.A2A);

        var consumer = builder.AddContainer("consumer", "image")
            .WithReference(agent)
            .WithReferenceEnvironment(ReferenceEnvironmentInjectionFlags.None);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.DoesNotContain("WEATHER_AGENT_AGENTCARD_URL", config.Keys);
    }

    private static void AllocateEndpoint(EndpointAnnotation endpoint, string containerHost, int containerPort)
    {
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 18080);
        endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
            new AllocatedEndpoint(
                endpoint,
                containerHost,
                containerPort,
                EndpointBindingMode.SingleAddress,
                targetPortExpression: containerPort.ToString(),
                KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
    }
}
