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
        Assert.Equal(A2AInvocationMode.NonStreaming, annotation.InvocationMode);
        Assert.Null(annotation.CustomPath);
        Assert.Null(annotation.AgentName);

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
            .AsAgent("/responses", AgentProtocol.Responses, agentName: "weather-agent");

        var annotations = agent.Resource.Annotations.OfType<AgentResourceAnnotation>().ToArray();
        Assert.Equal(2, annotations.Length);
        Assert.Contains(annotations, a => a.Protocol == AgentProtocol.A2A && a.CustomPath == "/a2a-card.json");
        Assert.Contains(annotations, a =>
            a.Protocol == AgentProtocol.Responses &&
            a.CustomPath == "/responses" &&
            a.AgentName == "weather-agent");

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
            .AsAgent(AgentProtocol.Acp, agentName: "registered-agent");

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
            .AsAgent(AgentProtocol.Acp, agentName: "registered-agent");

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
        Assert.Equal("registered-agent", Assert.Single(annotations, a => a.Protocol == AgentProtocol.Acp).AgentName);
        Assert.Single(commands, c => c.IsHighlighted);
    }

    [Fact]
    public void AsAgent_WithoutConfiguredProtocolNameAddsAgentNameArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent-service", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(AgentProtocol.Responses);

        var command = Assert.Single(agent.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == "agent-service-responses-send-message");
        Assert.Collection(
            command.Arguments,
            agentName =>
            {
                Assert.Equal("agentName", agentName.Name);
                Assert.Equal("Agent Name", agentName.Label);
                Assert.True(agentName.Required);
            },
            AssertMessageArgument);
    }

    [Theory]
    [InlineData(AgentProtocol.Responses, "agent-service-responses-send-message")]
    [InlineData(AgentProtocol.Acp, "agent-service-acp-run")]
    public async Task InvokeAgentUsesConfiguredProtocolAgentName(AgentProtocol protocol, string commandName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new CaptureAgentCommandHandler();
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent-service")
            .AsAgent(protocol, agentName: "registered-agent");

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, commandName);
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, commandName, CreateMessageArgument("hello")).DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(handler.RequestBody);
        var body = JsonNode.Parse(handler.RequestBody);
        var actualAgentName = protocol is AgentProtocol.Responses
            ? body?["agent"]?["name"]?.GetValue<string>()
            : body?["agent_name"]?.GetValue<string>();
        Assert.Equal("registered-agent", actualAgentName);
    }

    [Fact]
    public void AsAgent_RejectsA2AInvocationModeForOtherProtocols()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint(targetPort: 8080);

        var ex = Assert.Throws<ArgumentException>(() => agent.AsAgent(AgentProtocol.Responses, A2AInvocationMode.Streaming));

        Assert.Equal("invocationMode", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void AsAgent_RejectsUndefinedProtocolWithoutMutatingResource(int value)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var agent = builder.AddContainer("agent", "image");
        var annotations = agent.Resource.Annotations.ToArray();
        var services = builder.Services.ToArray();
        var protocol = (AgentProtocol)value;
        Action[] configure =
        [
            () => agent.AsAgent(protocol),
            () => agent.AsAgent(protocol, A2AInvocationMode.NonStreaming),
            () => agent.AsAgent("/agent-card.json", protocol),
            () => agent.AsAgent("/agent-card.json", protocol, A2AInvocationMode.Streaming)
        ];

        foreach (var action in configure)
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(action);
            Assert.Equal("protocol", exception.ParamName);
            Assert.Equal(protocol, exception.ActualValue);
            Assert.Equal(annotations, agent.Resource.Annotations);
            Assert.Equal(services, builder.Services);
        }
    }

    [Theory]
    [InlineData(AgentProtocol.A2A, -1)]
    [InlineData(AgentProtocol.A2A, int.MaxValue)]
    [InlineData(AgentProtocol.Responses, -1)]
    [InlineData(AgentProtocol.Responses, int.MaxValue)]
    [InlineData(AgentProtocol.AgUi, -1)]
    [InlineData(AgentProtocol.AgUi, int.MaxValue)]
    [InlineData(AgentProtocol.Acp, -1)]
    [InlineData(AgentProtocol.Acp, int.MaxValue)]
    public void AsAgent_RejectsUndefinedInvocationModeWithoutMutatingResource(AgentProtocol protocol, int value)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var agent = builder.AddContainer("agent", "image");
        var annotations = agent.Resource.Annotations.ToArray();
        var services = builder.Services.ToArray();
        var invocationMode = (A2AInvocationMode)value;
        Action[] configure =
        [
            () => agent.AsAgent(protocol, invocationMode),
            () => agent.AsAgent("/agent-card.json", protocol, invocationMode)
        ];

        foreach (var action in configure)
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(action);
            Assert.Equal("invocationMode", exception.ParamName);
            Assert.Equal(invocationMode, exception.ActualValue);
            Assert.Equal(annotations, agent.Resource.Annotations);
            Assert.Equal(services, builder.Services);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void AsAgent_RejectsInvalidCustomPath(string? path)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var agent = builder.AddContainer("agent", "image");

        var exception = Assert.ThrowsAny<ArgumentException>(() => agent.AsAgent(path!, AgentProtocol.A2A));
        var invocationModeException = Assert.ThrowsAny<ArgumentException>(() => agent.AsAgent(path!, AgentProtocol.A2A, A2AInvocationMode.Streaming));

        Assert.Equal("agentCustomPath", exception.ParamName);
        Assert.Equal("agentCustomPath", invocationModeException.ParamName);
        if (path is null)
        {
            Assert.IsType<ArgumentNullException>(exception);
            Assert.IsType<ArgumentNullException>(invocationModeException);
        }
        Assert.Empty(agent.Resource.Annotations.OfType<AgentResourceAnnotation>());
        Assert.Empty(agent.Resource.Annotations.OfType<ResourceCommandAnnotation>());
    }

    [Fact]
    public void AsAgent_CustomPathWithInvocationModePreservesConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var agent = builder.AddContainer("agent", "image")
            .AsAgent("agent-card.json", AgentProtocol.A2A, A2AInvocationMode.Streaming);

        var annotation = Assert.Single(agent.Resource.Annotations.OfType<AgentResourceAnnotation>());
        Assert.Equal("/agent-card.json", annotation.CustomPath);
        Assert.Equal(A2AInvocationMode.Streaming, annotation.InvocationMode);
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

    [Fact]
    public async Task AsAgent_CanBeConfiguredBeforeEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new A2ACommandHandler("JSONRPC", "1.0", supportsStreaming: false, "http://localhost:8080/a2a");
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = builder.AddResource(new CustomResource("agent"))
            .AsAgent(AgentProtocol.A2A)
            .WithHttpEndpoint(targetPort: 8080);
        var endpoint = Assert.Single(agent.Resource.Annotations.OfType<EndpointAnnotation>());
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(new Uri("http://localhost:8080/a2a"), handler.InvocationRequest?.RequestUri);
    }

    [Theory]
    [InlineData("JSONRPC", "1.0", false, A2AInvocationMode.NonStreaming, "http://localhost:8080/a2a", "http://localhost:8080/a2a", "SendMessage", "ROLE_USER", "parts")]
    [InlineData("JSONRPC", "1.0", true, A2AInvocationMode.NonStreaming, "http://localhost:8080/a2a", "http://localhost:8080/a2a", "SendMessage", "ROLE_USER", "parts")]
    [InlineData("JSONRPC", "0.3", false, A2AInvocationMode.NonStreaming, "http://localhost:8080/a2a", "http://localhost:8080/a2a", "message/send", "user", "parts")]
    [InlineData("JSONRPC", "0.3", true, A2AInvocationMode.Streaming, "http://localhost:8080/a2a", "http://localhost:8080/a2a", "message/stream", "user", "parts")]
    [InlineData("HTTP+JSON", "1.0", false, A2AInvocationMode.NonStreaming, "http://localhost:8080/a2a", "http://localhost:8080/a2a/message:send", null, "ROLE_USER", "parts")]
    [InlineData("HTTP+JSON", "1.0", true, A2AInvocationMode.NonStreaming, "http://localhost:8080/a2a", "http://localhost:8080/a2a/message:send", null, "ROLE_USER", "parts")]
    [InlineData("HTTP+JSON", "0.3", false, A2AInvocationMode.NonStreaming, "http://localhost:8080/a2a", "http://localhost:8080/a2a/v1/message:send", null, "user", "parts")]
    [InlineData("HTTP+JSON", "0.3", true, A2AInvocationMode.Streaming, "http://localhost:8080/a2a", "http://localhost:8080/a2a/v1/message:stream", null, "user", "parts")]
    [InlineData("JSONRPC", "1.0", false, A2AInvocationMode.NonStreaming, "http://agent.dev.internal:8080/a2a", "http://localhost:8080/a2a", "SendMessage", "ROLE_USER", "parts")]
    [InlineData("HTTP+JSON", "1.0", false, A2AInvocationMode.NonStreaming, "http://agent.dev.internal:8080/a2a", "http://localhost:8080/a2a/message:send", null, "ROLE_USER", "parts")]
    public async Task InvokeA2AReadsAgentCardAndChoosesBinding(
        string protocolBinding,
        string protocolVersion,
        bool supportsStreaming,
        A2AInvocationMode invocationMode,
        string interfaceUrl,
        string expectedUrl,
        string? expectedJsonRpcMethod,
        string expectedRole,
        string expectedPartsPropertyName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new A2ACommandHandler(protocolBinding, protocolVersion, supportsStreaming, interfaceUrl);
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.A2A, invocationMode);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(new Uri(expectedUrl), handler.InvocationRequest?.RequestUri);
        Assert.Equal(HttpMethod.Post, handler.InvocationRequest?.Method);
        Assert.Equal(protocolVersion, handler.InvocationRequest?.Headers.GetValues("A2A-Version").Single());
        Assert.Equal(invocationMode is A2AInvocationMode.Streaming, handler.InvocationRequest?.Headers.Accept.Any(h => h.MediaType == "text/event-stream"));

        Assert.NotNull(handler.InvocationBody);
        var body = JsonNode.Parse(handler.InvocationBody);
        var requestBody = expectedJsonRpcMethod is null ? body : body?["params"];
        if (expectedJsonRpcMethod is not null)
        {
            Assert.Equal(expectedJsonRpcMethod, body?["method"]?.GetValue<string>());
            Assert.Equal("application/json", handler.InvocationRequest?.Content?.Headers.ContentType?.MediaType);
        }
        else
        {
            Assert.Equal("application/a2a+json", handler.InvocationRequest?.Content?.Headers.ContentType?.MediaType);
        }

        Assert.Equal(expectedRole, requestBody?["message"]?["role"]?.GetValue<string>());
        Assert.NotNull(requestBody?["message"]?[expectedPartsPropertyName]);
        Assert.Equal("hello", requestBody?["message"]?[expectedPartsPropertyName]?[0]?["text"]?.GetValue<string>());
        if (protocolVersion.StartsWith("0.", StringComparison.Ordinal))
        {
            Assert.Equal("message", requestBody?["message"]?["kind"]?.GetValue<string>());
            Assert.Equal("text", requestBody?["message"]?["parts"]?[0]?["kind"]?.GetValue<string>());
        }
        var expectedConfiguration = expectedJsonRpcMethod is not null || !protocolVersion.StartsWith("0.", StringComparison.Ordinal);
        Assert.Equal(expectedConfiguration, requestBody?["configuration"] is not null);
    }

    [Fact]
    public async Task InvokeA2AStreamingRequiresAgentSupport()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new A2ACommandHandler("JSONRPC", "1.0", supportsStreaming: false, "http://localhost:8080/a2a");
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.A2A, A2AInvocationMode.Streaming);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("does not advertise streaming support", result.Message);
        Assert.Null(handler.InvocationRequest);
    }

    [Fact]
    public async Task InvokeA2ARequiresMessageArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new A2ACommandHandler("JSONRPC", "1.0", supportsStreaming: false, "http://localhost:8080/a2a");
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
    public async Task InvokeA2AReportsJsonRpcError()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new A2ACommandHandler(
            "JSONRPC",
            "1.0",
            supportsStreaming: false,
            "http://localhost:8080/a2a",
            """{"jsonrpc":"2.0","id":"request-id","error":{"code":-32603,"message":"Agent failed."}}""");
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.A2A);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Agent request returned a JSON-RPC error.", result.Message);
    }

    [Theory]
    [MemberData(nameof(A2ATaskResponseCases))]
    public async Task InvokeA2ARecognizesTaskStates(string protocolBinding, string protocolVersion, bool streaming, bool statusUpdate, string taskState, bool success)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var invocationResponse = CreateA2ATaskResponse(protocolBinding, protocolVersion, statusUpdate, taskState);
        var handler = new A2ACommandHandler(
            protocolBinding,
            protocolVersion,
            supportsStreaming: streaming,
            "http://localhost:8080/a2a",
            invocationResponse);
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var invocationMode = streaming ? A2AInvocationMode.Streaming : A2AInvocationMode.NonStreaming;
        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.A2A, invocationMode);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.Equal(success, result.Success);
        var failureState = taskState is "failed" or "rejected" or "canceled" or "TASK_STATE_FAILED" or "TASK_STATE_REJECTED" or "TASK_STATE_CANCELED";
        Assert.Equal(success
            ? "Agent response received."
            : failureState
                ? $"Agent task ended in the '{taskState}' state."
                : "Agent stream ended without a completed task or message response.", result.Message);
    }

    public static IEnumerable<object[]> A2ATaskResponseCases()
    {
        foreach (var protocolBinding in new[] { "JSONRPC", "HTTP+JSON" })
        {
            foreach (var protocolVersion in new[] { "0.3", "1.0" })
            {
                foreach (var statusUpdate in new[] { false, true })
                {
                    foreach (var state in new[] { "completed", "failed", "rejected", "canceled", "submitted", "working", "input-required", "auth-required", "unknown" })
                    {
                        var taskState = protocolVersion is "1.0" ? $"TASK_STATE_{state.Replace('-', '_').ToUpperInvariant()}" : state;
                        yield return [protocolBinding, protocolVersion, true, statusUpdate, taskState, state is "completed"];
                        if (!statusUpdate)
                        {
                            yield return [protocolBinding, protocolVersion, false, false, taskState, state is not ("failed" or "rejected" or "canceled")];
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"result":{}}""")]
    [InlineData("""{"result":null}""")]
    [InlineData("""{"result":[]}""")]
    [InlineData("""{"task":{}}""")]
    [InlineData("""{"message":{}}""")]
    [InlineData("""{"kind":"message"}""")]
    [InlineData("""{"kind":"task","status":{"state":"completed"}}""")]
    [InlineData("""{"task":{"id":"task-id","status":{"state":"completed"}},"message":{"messageId":"message-id","role":"agent","parts":[]}}""")]
    [InlineData("""{"statusUpdate":{"taskId":"task-id","status":{"state":"TASK_STATE_COMPLETED"}}}""")]
    public async Task InvokeA2ARejectsMalformedJsonResponse(string response)
    {
        var result = await InvokeA2AResponseAsync("JSONRPC", "1.0", false, response, "application/json");

        Assert.False(result.Success);
        Assert.Equal("Agent returned an invalid A2A task or message response.", result.Message);
        Assert.NotNull(result.Data);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(response), JsonNode.Parse(result.Data.Value)));
    }

    [Theory]
    [InlineData("Application/JSON", false)]
    [InlineData("Application/A2A+JSON", false)]
    [InlineData("Text/Event-Stream", true)]
    public async Task InvokeA2ARecognizesMixedCaseMediaTypes(string mediaType, bool streaming)
    {
        var error = """{"jsonrpc":"2.0","id":"request-id","error":{"code":-32603,"message":"Agent failed."}}""";
        var result = await InvokeA2AResponseAsync("JSONRPC", "1.0", streaming, streaming ? $"data: {error}\n\n" : error, mediaType);

        Assert.False(result.Success);
        Assert.Equal("Agent request returned a JSON-RPC error.", result.Message);

        var task = CreateA2ATaskResponse("JSONRPC", "1.0", false, "TASK_STATE_FAILED");
        result = await InvokeA2AResponseAsync("JSONRPC", "1.0", streaming, streaming ? $"data: {task}\n\n" : task, mediaType);

        Assert.False(result.Success);
        Assert.Equal("Agent task ended in the 'TASK_STATE_FAILED' state.", result.Message);
    }

    [Theory]
    [InlineData("JSONRPC", "0.3")]
    [InlineData("JSONRPC", "1.0")]
    [InlineData("HTTP+JSON", "0.3")]
    [InlineData("HTTP+JSON", "1.0")]
    public async Task InvokeStreamingA2AAcceptsDirectMessage(string protocolBinding, string protocolVersion)
    {
        var message = new JsonObject
        {
            ["messageId"] = "message-id",
            ["role"] = protocolVersion is "0.3" ? "agent" : "ROLE_AGENT",
            ["parts"] = new JsonArray(new JsonObject { ["text"] = "Hello" })
        };
        if (protocolVersion is "0.3")
        {
            message["kind"] = "message";
            message["parts"]![0]!["kind"] = "text";
        }
        var payload = protocolBinding is "JSONRPC" && protocolVersion is "0.3"
            ? message
            : new JsonObject { ["message"] = message };
        var response = WrapA2AResponse(protocolBinding, payload);

        var result = await InvokeA2AStreamAsync(protocolBinding, protocolVersion, $"data: {response}\n\n");

        Assert.True(result.Success);
        Assert.Equal("Agent response received.", result.Message);

        result = await InvokeA2AResponseAsync(protocolBinding, protocolVersion, false, response, "application/json");

        Assert.True(result.Success);
        Assert.Equal("Agent response received.", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(": keepalive\n\n")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {}\n\n")]
    [InlineData("data: {\"message\":\"not a Message object\"}\n\n")]
    [InlineData("data: {\"status\":{\"state\":\"completed\"}}\n\n")]
    [InlineData("data: {\"result\":{\"kind\":\"artifact-update\",\"taskId\":\"task-id\",\"artifact\":{\"artifactId\":\"artifact-id\",\"parts\":[{\"kind\":\"text\",\"text\":\"hello\"}]},\"lastChunk\":true}}\n\n")]
    [InlineData("data: {\"result\":{\"artifactUpdate\":{\"taskId\":\"task-id\",\"artifact\":{\"artifactId\":\"artifact-id\",\"parts\":[{\"text\":\"hello\"}]},\"lastChunk\":true}}}\n\n")]
    [InlineData("data: {\"result\":{\"kind\":\"status-update\",\"taskId\":\"task-id\",\"status\":{\"state\":\"working\",\"message\":{\"kind\":\"message\",\"role\":\"agent\",\"messageId\":\"message-id\",\"parts\":[]}},\"final\":true}}\n\n")]
    [InlineData("data: {\"result\":{\"statusUpdate\":{\"taskId\":\"task-id\",\"status\":{\"state\":\"TASK_STATE_WORKING\",\"message\":{\"role\":\"ROLE_AGENT\",\"messageId\":\"message-id\",\"parts\":[]}}}}}\n\n")]
    [InlineData("data: {\"result\":{\"task\":{\"id\":\"task-id\",\"status\":{\"state\":\"TASK_STATE_WORKING\"}}}}\n\ndata: {\"result\":{\"message\":{\"role\":\"ROLE_AGENT\",\"messageId\":\"message-id\",\"parts\":[{\"text\":\"hello\"}]}}}\n\n")]
    public async Task InvokeStreamingA2ARejectsStreamsWithoutResult(string response)
    {
        var result = await InvokeA2AStreamAsync("JSONRPC", "1.0", response);

        Assert.False(result.Success);
        Assert.Equal("Agent stream ended without a completed task or message response.", result.Message);
    }

    [Theory]
    [InlineData("JSONRPC", "0.3", "completed")]
    [InlineData("JSONRPC", "1.0", "completed")]
    [InlineData("HTTP+JSON", "0.3", "completed")]
    [InlineData("HTTP+JSON", "1.0", "completed")]
    [InlineData("JSONRPC", "0.3", "failed")]
    [InlineData("JSONRPC", "1.0", "failed")]
    [InlineData("JSONRPC", "0.3", "error")]
    [InlineData("JSONRPC", "1.0", "error")]
    [InlineData("JSONRPC", "0.3", "working")]
    [InlineData("JSONRPC", "1.0", "working")]
    [InlineData("HTTP+JSON", "0.3", "working")]
    [InlineData("HTTP+JSON", "1.0", "working")]
    [InlineData("JSONRPC", "0.3", "submitted")]
    [InlineData("JSONRPC", "1.0", "submitted")]
    [InlineData("HTTP+JSON", "0.3", "submitted")]
    [InlineData("HTTP+JSON", "1.0", "submitted")]
    [InlineData("JSONRPC", "0.3", "artifact")]
    [InlineData("JSONRPC", "1.0", "artifact")]
    [InlineData("HTTP+JSON", "0.3", "artifact")]
    [InlineData("HTTP+JSON", "1.0", "artifact")]
    public async Task InvokeStreamingA2AProcessesAllEvents(string protocolBinding, string protocolVersion, string ending)
    {
        var workingState = protocolVersion is "0.3" ? "working" : "TASK_STATE_WORKING";
        var completedState = protocolVersion is "0.3" ? "completed" : "TASK_STATE_COMPLETED";
        var failedState = protocolVersion is "0.3" ? "failed" : "TASK_STATE_FAILED";
        var working = CreateA2ATaskResponse(protocolBinding, protocolVersion, statusUpdate: false, workingState);
        var completed = CreateA2ATaskResponse(protocolBinding, protocolVersion, statusUpdate: true, completedState);
        var response = $"data: {working}\n\ndata: {completed}\n\n";
        if (ending is "failed")
        {
            response += $"data: {CreateA2ATaskResponse(protocolBinding, protocolVersion, statusUpdate: true, failedState)}\n\n";
        }
        else if (ending is "error")
        {
            response += "data: {\"jsonrpc\":\"2.0\",\"id\":\"request-id\",\"error\":{\"code\":-32603,\"message\":\"Agent failed.\"}}\n\n";
        }
        else if (ending is "working" or "submitted")
        {
            var state = protocolVersion is "0.3" ? ending : $"TASK_STATE_{ending.ToUpperInvariant()}";
            response += $"data: {CreateA2ATaskResponse(protocolBinding, protocolVersion, statusUpdate: true, state)}\n\n";
        }
        else if (ending is "artifact")
        {
            var update = new JsonObject
            {
                ["taskId"] = "task-id",
                ["contextId"] = "context-id",
                ["artifact"] = new JsonObject
                {
                    ["artifactId"] = "artifact-id",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = "hello" })
                },
                ["lastChunk"] = true
            };
            if (protocolVersion is "0.3")
            {
                update["artifact"]!["parts"]![0]!["kind"] = "text";
            }
            JsonObject payload;
            if (protocolBinding is "JSONRPC" && protocolVersion is "0.3")
            {
                update["kind"] = "artifact-update";
                payload = update;
            }
            else
            {
                payload = new JsonObject { ["artifactUpdate"] = update };
            }
            response += $"data: {WrapA2AResponse(protocolBinding, payload)}\n\n";
        }

        var result = await InvokeA2AStreamAsync(protocolBinding, protocolVersion, response);

        Assert.Equal(ending is "completed" or "artifact", result.Success);
        Assert.Equal(ending switch
        {
            "completed" or "artifact" => "Agent response received.",
            "failed" => $"Agent task ended in the '{failedState}' state.",
            "working" or "submitted" => "Agent stream ended without a completed task or message response.",
            _ => "Agent request returned a JSON-RPC error."
        }, result.Message);
    }

    [Fact]
    public async Task InvokeStreamingA2AReportsJsonRpcError()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new A2ACommandHandler(
            "JSONRPC",
            "1.0",
            supportsStreaming: true,
            "http://localhost:8080/a2a",
            """{"jsonrpc":"2.0","id":"request-id","error":{"code":-32603,"message":"Agent failed."}}""");
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.A2A, A2AInvocationMode.Streaming);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Agent request returned a JSON-RPC error.", result.Message);
    }

    [Fact]
    public async Task InvokeAgUiReportsRunError()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new SseAgentCommandHandler(
            """
            event: message
            data: {"type":"RUN_ERROR","message":"Agent failed."}

            """);
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.AgUi);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-ag-ui-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-ag-ui-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Agent run returned a RUN_ERROR event.", result.Message);
    }

    [Fact]
    public async Task InvokeAgUiReportsMissingTerminalEvent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new SseAgentCommandHandler(
            """
            event: message
            data: {"type":"TEXT_MESSAGE_CONTENT","delta":"Partial response"}

            """);
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.AgUi);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-ag-ui-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-ag-ui-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Agent run ended without a terminal event.", result.Message);
    }

    [Fact]
    public async Task InvokeAgUiSucceedsOnRunFinished()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var handler = new SseAgentCommandHandler(
            """
            event: message
            data: {"type":"RUN_FINISHED"}

            """);
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.AgUi);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-ag-ui-send-message");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-ag-ui-send-message", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task InvokeAcpReportsTerminalRunFailure(string runStatus)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var response = new JsonObject
        {
            ["run_id"] = "run-id",
            ["status"] = runStatus,
            ["error"] = new JsonObject
            {
                ["code"] = "server_error",
                ["message"] = "Agent failed."
            }
        }.ToJsonString();
        var handler = new CaptureAgentCommandHandler(response);
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.Acp, agentName: "weather-agent");

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-acp-run");
        var result = await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-acp-run", CreateMessageArgument("hello")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal($"Agent run ended in the '{runStatus}' state.", result.Message);
        Assert.Contains("server_error", result.Data?.Value);
    }

    [Fact]
    public async Task AsAgent_PublishPreservesEndpointExpressionsWithoutCommands()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var agent = builder.AddContainer("weather-agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .AsAgent(AgentProtocol.A2A)
            .AsAgent(AgentProtocol.Responses)
            .AsAgent(AgentProtocol.AgUi)
            .AsAgent(AgentProtocol.Acp);
        var consumer = builder.AddContainer("consumer", "image")
            .WithEnvironment("WEATHER_AGENT_AGENTCARD_URL",
                ReferenceExpression.Create($"{agent.GetEndpoint("http")}/.well-known/agent-card.json"));

        Assert.Equal(4, agent.Resource.Annotations.OfType<AgentResourceAnnotation>().Count());
        Assert.Empty(agent.Resource.Annotations.OfType<ResourceCommandAnnotation>());
        Assert.Empty(agent.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>());
        var agentEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            agent.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance).DefaultTimeout();
        var consumerEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            consumer.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance).DefaultTimeout();

        await VerifyXunit.Verifier.Verify(new { Agent = agentEnvironment, Consumer = consumerEnvironment }).UseDirectory("Snapshots");
    }

    [Fact]
    public async Task WithEnvironment_A2AAgentCardUrlUsesConsumerNetwork()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("weather-agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => AllocateEndpoint(e, "weather-agent.dev.internal", 8080))
            .AsAgent(AgentProtocol.A2A);

        var consumer = builder.AddContainer("consumer", "image")
            .WithEnvironment("WEATHER_AGENT_AGENTCARD_URL",
                ReferenceExpression.Create($"{agent.GetEndpoint("http")}/.well-known/agent-card.json"));

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://weather-agent.dev.internal:8080/.well-known/agent-card.json", config["WEATHER_AGENT_AGENTCARD_URL"]);
    }

    [Fact]
    public async Task WithEnvironment_A2AAgentCardUrlUsesExplicitPathAndName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("weather-agent", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => AllocateEndpoint(e, "weather-agent.dev.internal", 8080))
            .AsAgent("agent-card.json", AgentProtocol.A2A);

        var consumer = builder.AddContainer("consumer", "image")
            .WithEnvironment("SKI_AGENT_AGENTCARD_URL",
                ReferenceExpression.Create($"{agent.GetEndpoint("http")}/agent-card.json"));

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://weather-agent.dev.internal:8080/agent-card.json", config["SKI_AGENT_AGENTCARD_URL"]);
    }

    [Fact]
    public async Task WithReference_ProjectA2AAgentKeepsStandardServiceDiscovery()
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
        Assert.Equal(["WEATHER_AGENT_HTTP", "services__weather-agent__http__0"], config.Keys.Order(StringComparer.Ordinal));

        var relationships = consumer.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Where(r => ReferenceEquals(r.Resource, agent.Resource));
        Assert.Single(relationships);
    }

    [Fact]
    public async Task AsAgent_ImplicitEndpointSelectionSkipsExcludedEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("weather-agent", "image")
            .WithHttpsEndpoint(name: "management", targetPort: 8443)
            .WithEndpoint("management", e =>
            {
                e.ExcludeReferenceEndpoint = true;
                AllocateEndpoint(e, "management.dev.internal", 8443);
            })
            .WithHttpEndpoint(name: "api", targetPort: 8080)
            .WithEndpoint("api", e => AllocateEndpoint(e, "weather-agent.dev.internal", 8080))
            .AsAgent(AgentProtocol.A2A);

        var agentConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(agent.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://weather-agent.dev.internal:8080", agentConfig[AgentResourceBuilderExtensions.A2AAgentBaseUrlEnvironmentVariableName]);
    }

    [Fact]
    public async Task AsAgent_OnlyExcludedEndpointDoesNotFallbackToIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var agent = builder.AddContainer("agent", "image")
            .WithHttpEndpoint()
            .WithEndpoint("http", e => e.ExcludeReferenceEndpoint = true)
            .AsAgent(AgentProtocol.A2A);

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                agent.Resource,
                DistributedApplicationOperation.Run,
                TestServiceProvider.Instance));

        Assert.Contains("no non-excluded HTTP or HTTPS endpoint was found", ex.Message);
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
        AssertMessageArgument(Assert.Single(command.Arguments));
    }

    private static void AssertMessageArgument(InteractionInput argument)
    {
        Assert.Equal("message", argument.Name);
        Assert.Equal("Message", argument.Label);
        Assert.Equal("Message to send to the agent.", argument.Description);
        Assert.Equal(InputType.Text, argument.InputType);
        Assert.True(argument.Required);
        Assert.False(string.IsNullOrWhiteSpace(argument.Placeholder));
    }

    private static string CreateA2ATaskResponse(string protocolBinding, string protocolVersion, bool statusUpdate, string taskState)
    {
        var task = new JsonObject
        {
            [statusUpdate ? "taskId" : "id"] = "task-id",
            ["contextId"] = "context-id",
            ["status"] = new JsonObject { ["state"] = taskState }
        };
        JsonObject payload;
        if (protocolBinding is "JSONRPC" && protocolVersion is "0.3")
        {
            task["kind"] = statusUpdate ? "status-update" : "task";
            if (statusUpdate)
            {
                task["final"] = taskState is not ("submitted" or "working");
            }
            payload = task;
        }
        else
        {
            payload = new JsonObject { [statusUpdate ? "statusUpdate" : "task"] = task };
        }

        return WrapA2AResponse(protocolBinding, payload);
    }

    private static string WrapA2AResponse(string protocolBinding, JsonObject payload)
    {
        return (protocolBinding is "JSONRPC"
            ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "request-id", ["result"] = payload }
            : payload).ToJsonString();
    }

    private static Task<ExecuteCommandResult> InvokeA2AStreamAsync(string protocolBinding, string protocolVersion, string response)
    {
        return InvokeA2AResponseAsync(protocolBinding, protocolVersion, true, response, "text/event-stream");
    }

    private static async Task<ExecuteCommandResult> InvokeA2AResponseAsync(string protocolBinding, string protocolVersion, bool streaming, string response, string mediaType)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var handler = new A2ACommandHandler(protocolBinding, protocolVersion, supportsStreaming: streaming, "http://localhost:8080/a2a",
            invocationResponse: response, streamingResponse: response)
        {
            ResponseMediaType = mediaType
        };
        builder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var agent = CreateResourceWithAllocatedEndpoint(builder, "agent")
            .AsAgent(AgentProtocol.A2A, streaming ? A2AInvocationMode.Streaming : A2AInvocationMode.NonStreaming);
        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, agent.Resource, "agent-a2a-send-message");

        return await app.ResourceCommands.ExecuteCommandAsync(agent.Resource, "agent-a2a-send-message", CreateMessageArgument("hello")).DefaultTimeout();
    }

    private sealed class A2ACommandHandler(
        string protocolBinding,
        string protocolVersion,
        bool supportsStreaming,
        string interfaceUrl,
        string? invocationResponse = null,
        string? streamingResponse = null) : HttpMessageHandler
    {
        public HttpRequestMessage? InvocationRequest { get; private set; }

        public string? InvocationBody { get; private set; }

        public string? ResponseMediaType { get; init; }

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
                              "streaming": {{supportsStreaming.ToString().ToLowerInvariant()}}
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

            var response = invocationResponse ?? CreateA2ATaskResponse(
                protocolBinding,
                protocolVersion,
                statusUpdate: false,
                protocolVersion is "0.3" ? "completed" : "TASK_STATE_COMPLETED");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Headers.Accept.Any(h => h.MediaType == "text/event-stream")
                    ? new StringContent(streamingResponse ?? $"event: message\ndata: {response}\n\n", Encoding.UTF8, ResponseMediaType ?? "text/event-stream")
                    : new StringContent(response, Encoding.UTF8, ResponseMediaType ?? "application/json")
            };
        }
    }

    private sealed class CaptureAgentCommandHandler(string responseBody = """{"ok":true}""") : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SseAgentCommandHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
            });
        }
    }
}

#pragma warning restore ASPIREINTERACTION001
