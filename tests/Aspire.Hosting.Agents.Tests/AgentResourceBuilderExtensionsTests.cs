// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Agents.Tests;

#pragma warning disable ASPIREINTERACTION001 // InteractionInput is used to test dashboard command arguments.

[Trait("Partition", "5")]
public class AgentResourceBuilderExtensionsTests
{
    [Fact]
    public void AsAgent_AddsAgentAnnotationAndCommands()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(AgentProtocol.A2A);

        var annotation = Assert.Single(agent.Resource.Annotations.OfType<AgentResourceAnnotation>());
        Assert.Equal(AgentProtocol.A2A, annotation.Protocol);

        var commands = agent.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.DoesNotContain(commands, c => c.Name == "agent-a2a-agent-card");
        var command = Assert.Single(commands, c => c.Name == "agent-a2a-send-message");
        Assert.Equal("Invoke A2A", command.DisplayName);
        Assert.Equal("ChatSparkle", command.IconName);
        Assert.Equal(IconVariant.Regular, command.IconVariant);
        Assert.True(command.IsHighlighted);
        AssertMessageArgument(command);
        Assert.Single(commands, c => c.IsHighlighted);
    }

    [Fact]
    public void AsAgent_CanBeCalledMultipleTimesForMultipleProtocolPaths()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent("/a2a-card.json", AgentProtocol.A2A)
            .AsAgent("/responses", AgentProtocol.Responses);

        var annotations = agent.Resource.Annotations.OfType<AgentResourceAnnotation>().ToArray();
        Assert.Equal(2, annotations.Length);
        Assert.Contains(annotations, a => a.Protocol == AgentProtocol.A2A && a.CustomPath == "/a2a-card.json");
        Assert.Contains(annotations, a => a.Protocol == AgentProtocol.Responses && a.CustomPath == "/responses");

        var commands = agent.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.Contains(commands, c => c.Name == "agent-a2a-send-message" && c.IsHighlighted);
        Assert.Contains(commands, c => c.Name == "agent-responses-send-message" && !c.IsHighlighted);
        Assert.Single(commands, c => c.IsHighlighted);
    }

    [Fact]
    public void AsAgent_CanBeCalledMultipleTimesForMultipleAgentAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(AgentProtocol.A2A)
            .AsAgent(AgentProtocol.AgUi)
            .AsAgent(AgentProtocol.Acp);

        var annotations = agent.Resource.Annotations.OfType<AgentResourceAnnotation>().ToArray();
        Assert.Equal(3, annotations.Length);
        Assert.Contains(annotations, a => a.Protocol == AgentProtocol.A2A);
        Assert.Contains(annotations, a => a.Protocol == AgentProtocol.AgUi);
        Assert.Contains(annotations, a => a.Protocol == AgentProtocol.Acp);

        var commands = agent.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.Contains(commands, c => c.Name == "agent-a2a-send-message" && c.IsHighlighted);
        Assert.Contains(commands, c => c.Name == "agent-ag-ui-send-message" && !c.IsHighlighted);
        Assert.Contains(commands, c => c.Name == "agent-acp-run" && !c.IsHighlighted);
        Assert.Single(commands, c => c.IsHighlighted);
    }

    [Fact]
    public void AsAgent_AgUiAndAcpAddInvocationCommands()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(AgentProtocol.AgUi)
            .AsAgent(AgentProtocol.Acp);

        var annotations = agent.Resource.Annotations.OfType<AgentResourceAnnotation>().ToArray();
        Assert.Equal(2, annotations.Length);
        Assert.Contains(annotations, a => a.Protocol == AgentProtocol.AgUi);
        Assert.Contains(annotations, a => a.Protocol == AgentProtocol.Acp);

        var commands = agent.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        var agUiCommand = Assert.Single(commands, c => c.Name == "agent-ag-ui-send-message");
        Assert.Equal("Invoke AG-UI", agUiCommand.DisplayName);
        Assert.Equal("ChatSparkle", agUiCommand.IconName);
        Assert.Equal(IconVariant.Regular, agUiCommand.IconVariant);
        Assert.True(agUiCommand.IsHighlighted);
        AssertMessageArgument(agUiCommand);

        var acpCommand = Assert.Single(commands, c => c.Name == "agent-acp-run");
        Assert.Equal("Invoke ACP", acpCommand.DisplayName);
        Assert.Equal("ChatSparkle", acpCommand.IconName);
        Assert.Equal(IconVariant.Regular, acpCommand.IconVariant);
        Assert.False(acpCommand.IsHighlighted);
        AssertMessageArgument(acpCommand);
        Assert.Single(commands, c => c.IsHighlighted);
    }

    [Fact]
    public async Task AsAgent_A2AInjectsBaseUrlIntoAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => AllocateEndpoint(e, "agent.dev.internal", 8080))
            .AsAgent(AgentProtocol.A2A);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(agent.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://agent.dev.internal:8080", config[AgentResourceBuilderExtensions.A2AAgentBaseUrlEnvironmentVariableName]);
    }

    [Theory]
    [InlineData("JSONRPC", "1.0", false, "http://localhost:8080/a2a", "http://localhost:8080/a2a", "SendMessage", "ROLE_USER", "parts")]
    [InlineData("JSONRPC", "1.0", true, "http://localhost:8080/a2a", "http://localhost:8080/a2a", "SendStreamingMessage", "ROLE_USER", "parts")]
    [InlineData("JSONRPC", "0.3", false, "http://localhost:8080/a2a", "http://localhost:8080/a2a", "message/send", "user", "parts")]
    [InlineData("JSONRPC", "0.3", true, "http://localhost:8080/a2a", "http://localhost:8080/a2a", "message/stream", "user", "parts")]
    [InlineData("HTTP+JSON", "1.0", false, "http://localhost:8080/a2a", "http://localhost:8080/a2a/message:send", null, "ROLE_USER", "parts")]
    [InlineData("HTTP+JSON", "1.0", true, "http://localhost:8080/a2a", "http://localhost:8080/a2a/message:stream", null, "ROLE_USER", "parts")]
    [InlineData("HTTP+JSON", "0.3", false, "http://localhost:8080/a2a", "http://localhost:8080/a2a/v1/message:send", null, "user", "content")]
    [InlineData("HTTP+JSON", "0.3", true, "http://localhost:8080/a2a", "http://localhost:8080/a2a/v1/message:stream", null, "user", "content")]
    [InlineData("JSONRPC", "1.0", false, "http://agent.dev.internal:8080/a2a", "http://localhost:8080/a2a", "SendMessage", "ROLE_USER", "parts")]
    [InlineData("HTTP+JSON", "1.0", false, "http://agent.dev.internal:8080/a2a", "http://localhost:8080/a2a/message:send", null, "ROLE_USER", "parts")]
    public async Task InvokeA2AReadsAgentCardAndChoosesBinding(string protocolBinding, string protocolVersion, bool streaming, string interfaceUrl, string expectedUrl, string? expectedJsonRpcMethod, string expectedRole, string expectedPartsPropertyName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new A2ACommandHandler(protocolBinding, protocolVersion, streaming, interfaceUrl);
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.A2A);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(new Uri(expectedUrl), handler.InvocationRequest?.RequestUri);
        Assert.Equal(HttpMethod.Post, handler.InvocationRequest?.Method);
        Assert.Equal(protocolVersion, handler.InvocationRequest?.Headers.GetValues("A2A-Version").Single());
        Assert.Equal(streaming, handler.InvocationRequest?.Headers.Accept.Any(h => h.MediaType == "text/event-stream"));

        Assert.NotNull(handler.InvocationBody);
        var body = JsonNode.Parse(handler.InvocationBody);
        if (expectedJsonRpcMethod is not null)
        {
            Assert.Equal(expectedJsonRpcMethod, body?["method"]?.GetValue<string>());
            Assert.Equal(expectedRole, body?["params"]?["message"]?["role"]?.GetValue<string>());
            Assert.NotNull(body?["params"]?["message"]?[expectedPartsPropertyName]);
            Assert.Equal("hello", body?["params"]?["message"]?[expectedPartsPropertyName]?[0]?["text"]?.GetValue<string>());
            Assert.Equal("application/json", handler.InvocationRequest?.Content?.Headers.ContentType?.MediaType);
        }
        else
        {
            Assert.Equal(expectedRole, body?["message"]?["role"]?.GetValue<string>());
            Assert.NotNull(body?["message"]?[expectedPartsPropertyName]);
            Assert.Equal("hello", body?["message"]?[expectedPartsPropertyName]?[0]?["text"]?.GetValue<string>());
            Assert.Equal("application/a2a+json", handler.InvocationRequest?.Content?.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task InvokeA2ARequiresMessageArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new A2ACommandHandler("JSONRPC", "1.0", streaming: false, "http://localhost:8080/a2a");
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.A2A);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Command argument validation failed.", result.Message);
        Assert.Null(handler.InvocationRequest);
    }

    [Fact]
    public async Task WithReference_A2AAgentInjectsAgentCardUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("weather-agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => AllocateEndpoint(e, "weather-agent.dev.internal", 8080))
            .AsAgent(AgentProtocol.A2A);

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
            .AsAgent("agent-card.json", AgentProtocol.A2A);

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
            .AsAgent(AgentProtocol.A2A);

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

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";

        public LaunchSettings LaunchSettings { get; } = new();
    }

    private static void AllocateEndpoint(Aspire.Hosting.ApplicationModel.EndpointAnnotation endpoint, string containerHost, int containerPort)
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

    private static async Task MoveResourceToRunningStateAsync(DistributedApplication app, IResource resource, string commandName)
    {
        await app.ResourceNotifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        await app.ResourceNotifications.WaitForResourceAsync(
            resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Running &&
                 e.Snapshot.Commands.FirstOrDefault(c => c.Name == commandName)?.State == ResourceCommandState.Enabled).DefaultTimeout();
    }

    private static IResourceBuilder<CustomResource> CreateResourceWithAllocatedEndpoint(IDistributedApplicationBuilder builder, string name)
    {
        var service = builder.AddResource(new CustomResource(name))
            .WithHttpEndpoint(targetPort: 8080);

        var endpointAnnotation = service.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        return service;
    }

    private sealed class CustomResource(string name) : Resource(name), IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource, IResourceWithWaitSupport;

    private static InteractionInputCollection CreateMessageArgument(string message)
    {
        return new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "message",
                InputType = InputType.Text,
                Value = message
            }
        ]);
    }

    private static void AssertMessageArgument(ResourceCommandAnnotation command)
    {
        var argument = Assert.Single(command.Arguments);
        Assert.Equal("message", argument.Name);
        Assert.Equal("Message", argument.Label);
        Assert.Equal("Message to send to the agent.", argument.Description);
        Assert.Equal(InputType.Text, argument.InputType);
        Assert.True(argument.Required);
        Assert.False(string.IsNullOrWhiteSpace(argument.Placeholder));
    }

    private sealed class A2ACommandHandler(string protocolBinding, string protocolVersion, bool streaming, string interfaceUrl) : HttpMessageHandler
    {
        public HttpRequestMessage? InvocationRequest { get; private set; }

        public string? InvocationBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == AgentResourceBuilderExtensions.DefaultA2AAgentCardPath)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                          {
                            "capabilities": {
                              "streaming": {{streaming.ToString().ToLowerInvariant()}}
                            },
                            "supportedInterfaces": [
                              {
                                "url": "{{interfaceUrl}}",
                                "protocolBinding": "{{protocolBinding}}",
                                "protocolVersion": "{{protocolVersion}}"
                              }
                            ]
                          }
                          """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            InvocationRequest = request;
            InvocationBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = streaming
                    ? new StringContent("event: message\ndata: {}\n\n", Encoding.UTF8, "text/event-stream")
                    : new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            };
        }
    }
}

#pragma warning restore ASPIREINTERACTION001
