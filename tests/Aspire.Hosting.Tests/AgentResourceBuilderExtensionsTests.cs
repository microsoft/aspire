// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Agents;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AgentResourceBuilderExtensionsTests
{
    [Fact]
    public void AsAgent_AddsAgentAnnotationAndCommands()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(AgentProtocol.A2AJsonRpc, AgentProtocol.Responses);

        var annotation = Assert.Single(agent.Resource.Annotations.OfType<AgentResourceAnnotation>());
        Assert.Contains(AgentProtocol.A2AJsonRpc, annotation.Protocols);
        Assert.Contains(AgentProtocol.Responses, annotation.Protocols);
        Assert.Equal(A2AInvocationMode.NonStreaming, annotation.A2AInvocationMode);

        var commands = agent.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.DoesNotContain(commands, c => c.Name == "agent-a2a-agent-card");
        Assert.Contains(commands, c => c.Name == "agent-a2a-jsonrpc-send-message" && c.DisplayName == "Send Message" && c.IconName == "ChatSparkle" && c.IconVariant == IconVariant.Regular && c.IsHighlighted);
        Assert.Contains(commands, c => c.Name == "agent-responses-send-message" && c.DisplayName == "Send Message");
    }

    [Fact]
    public void AsAgent_A2AGrpcDoesNotAddHttpInvocationCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(AgentProtocol.A2AGrpc);

        var annotation = Assert.Single(agent.Resource.Annotations.OfType<AgentResourceAnnotation>());
        Assert.Contains(AgentProtocol.A2AGrpc, annotation.Protocols);

        var commands = agent.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.DoesNotContain(commands, c => c.Name.Contains("-a2a-", StringComparison.Ordinal) && c.Name.EndsWith("-send-message", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Name == "agent-a2a-agent-card");
    }

    [Fact]
    public void AsAgent_A2AStreamingInvocationModeIsOptional()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(A2AInvocationMode.Streaming, AgentProtocol.A2AJsonRpc);

        var annotation = Assert.Single(agent.Resource.Annotations.OfType<AgentResourceAnnotation>());
        Assert.Equal(A2AInvocationMode.Streaming, annotation.A2AInvocationMode);
    }

    [Fact]
    public void AsAgent_A2AHttpJsonAddsInvocationCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(AgentProtocol.A2AHttpJson);

        var annotation = Assert.Single(agent.Resource.Annotations.OfType<AgentResourceAnnotation>());
        Assert.Contains(AgentProtocol.A2AHttpJson, annotation.Protocols);

        var commands = agent.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.Contains(commands, c => c.Name == "agent-a2a-http-json-send-message" && c.DisplayName == "Send Message" && c.IconName == "ChatSparkle" && c.IconVariant == IconVariant.Regular && c.IsHighlighted);
    }

    [Fact]
    public async Task AsAgent_A2AInjectsBaseUrlIntoAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 18080))
            .AsAgent(AgentProtocol.A2AJsonRpc);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(agent.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://localhost:18080", config[AgentResourceBuilderExtensions.A2AAgentBaseUrlEnvironmentVariableName]);
    }

    [Fact]
    public async Task WithReference_A2AAgentInjectsAgentCardUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("weather-agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => AllocateEndpoint(e, "weather-agent.dev.internal", 8080))
            .AsAgent(AgentProtocol.A2AJsonRpc);

        var consumer = builder.AddContainer("consumer", "image")
            .WithReference(agent);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://weather-agent.dev.internal:8080/.well-known/agent-card.json", config["WEATHER_AGENT_AGENTCARD_URL"]);
    }

    [Fact]
    public async Task WithReference_A2AAgentUsesCustomPathAndReferenceName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("weather-agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => AllocateEndpoint(e, "weather-agent.dev.internal", 8080))
            .AsAgent("agent-card.json", AgentProtocol.A2AJsonRpc);

        var consumer = builder.AddContainer("consumer", "image")
            .WithReference(agent, name: "ski-agent");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://weather-agent.dev.internal:8080/agent-card.json", config["SKI_AGENT_AGENTCARD_URL"]);
    }

    [Fact]
    public async Task WithReference_ProjectA2AAgentAlsoInjectsServiceDiscovery()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddProject<ProjectA>("weather-agent")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => AllocateEndpoint(e, "weather-agent.dev.internal", 8080))
            .AsAgent(AgentProtocol.A2AJsonRpc);

        var consumer = builder.AddContainer("consumer", "image")
            .WithReference(agent);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://weather-agent.dev.internal:8080", config["services__weather-agent__http__0"]);
        Assert.Equal("http://weather-agent.dev.internal:8080", config["WEATHER_AGENT_HTTP"]);
        Assert.Equal("http://weather-agent.dev.internal:8080/.well-known/agent-card.json", config["WEATHER_AGENT_AGENTCARD_URL"]);
    }

    [Fact]
    public void WithReference_NonAgentEndpointOnlyResourceStillThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var source = builder.AddContainer("endpoint-only", "image")
            .WithHttpEndpoint(targetPort: 8080);
        var consumer = builder.AddContainer("consumer", "image");

        var ex = Assert.Throws<InvalidOperationException>(() => consumer.WithReference(source));

        Assert.Equal("The resource 'endpoint-only' can't be used with withReference because it doesn't provide a connection string, service discovery, or a custom withReference implementation.", ex.Message);
    }

    [Fact]
    public void AsAgent_ThrowsWhenNoProtocolsAreSpecified()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080);

        var ex = Assert.Throws<ArgumentException>(() => agent.AsAgent());

        Assert.Equal("protocols", ex.ParamName);
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";

        public LaunchSettings LaunchSettings { get; } = new();
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
