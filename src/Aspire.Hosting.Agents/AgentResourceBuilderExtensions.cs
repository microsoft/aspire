// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Agents;

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is used to prompt for dashboard command input.

/// <summary>
/// Provides extension methods for configuring resources as agents.
/// </summary>
public static class AgentResourceBuilderExtensions
{
    /// <summary>
    /// The environment variable set on A2A resources with the base URL they should advertise in their agent card.
    /// </summary>
    public const string A2AAgentBaseUrlEnvironmentVariableName = "A2A_AGENT_BASE_URL";

    /// <summary>
    /// The default A2A agent card path.
    /// </summary>
    public const string DefaultA2AAgentCardPath = "/.well-known/agent-card.json";

    /// <summary>
    /// The default A2A JSON-RPC path.
    /// </summary>
    public const string DefaultA2AJsonRpcPath = "/";

    /// <summary>
    /// The default A2A HTTP+JSON message send path.
    /// </summary>
    public const string DefaultA2AHttpJsonSendMessagePath = "/message:send";

    /// <summary>
    /// The default A2A HTTP+JSON streaming message path.
    /// </summary>
    public const string DefaultA2AHttpJsonStreamingMessagePath = "/message:stream";

    /// <summary>
    /// The default OpenAI Responses API path.
    /// </summary>
    public const string DefaultResponsesPath = "/v1/responses";

    /// <summary>
    /// The default AG-UI protocol path.
    /// </summary>
    public const string DefaultAgUiPath = "/ag-ui";

    /// <summary>
    /// The default Agent Communication Protocol run creation path.
    /// </summary>
    public const string DefaultAcpPath = "/runs";

    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Configures the resource as an agent that supports the specified protocols.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="protocols">The protocols supported by the agent.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when no protocols are specified.</exception>
    [AspireExport]
    public static IResourceBuilder<T> AsAgent<T>(this IResourceBuilder<T> builder, params AgentProtocol[] protocols)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsAgent(builder, agentCustomPath: null, A2AInvocationMode.NonStreaming, protocols);
    }

    /// <summary>
    /// Configures the resource as an agent that supports the specified protocols.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="a2AInvocationMode">The invocation mode used by dashboard commands for A2A protocols.</param>
    /// <param name="protocols">The protocols supported by the agent.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when no protocols are specified.</exception>
    [AspireExport("asAgentWithA2AInvocationMode")]
    public static IResourceBuilder<T> AsAgent<T>(this IResourceBuilder<T> builder, A2AInvocationMode a2AInvocationMode, params AgentProtocol[] protocols)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsAgent(builder, agentCustomPath: null, a2AInvocationMode, protocols);
    }

    /// <summary>
    /// Configures the resource as an agent that supports the specified protocols using a custom protocol path.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="agentCustomPath">The custom path for protocol-specific dashboard commands and URLs.</param>
    /// <param name="protocols">The protocols supported by the agent.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when no protocols are specified.</exception>
    [AspireExport("asAgentWithPath")]
    public static IResourceBuilder<T> AsAgent<T>(this IResourceBuilder<T> builder, string? agentCustomPath, params AgentProtocol[] protocols)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsAgent(builder, agentCustomPath, A2AInvocationMode.NonStreaming, protocols);
    }

    /// <summary>
    /// Configures the resource as an agent that supports the specified protocols using a custom protocol path.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="agentCustomPath">The custom path for protocol-specific dashboard commands and URLs.</param>
    /// <param name="a2AInvocationMode">The invocation mode used by dashboard commands for A2A protocols.</param>
    /// <param name="protocols">The protocols supported by the agent.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when no protocols are specified.</exception>
    [AspireExport("asAgentWithPathAndA2AInvocationMode")]
    public static IResourceBuilder<T> AsAgent<T>(this IResourceBuilder<T> builder, string? agentCustomPath, A2AInvocationMode a2AInvocationMode, params AgentProtocol[] protocols)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(protocols);

        if (protocols.Length == 0)
        {
            throw new ArgumentException("At least one agent protocol must be specified.", nameof(protocols));
        }

        var normalizedPath = NormalizePath(agentCustomPath);
        var protocolSet = protocols.ToHashSet();
        var annotation = new AgentResourceAnnotation(protocolSet, normalizedPath, a2AInvocationMode);

        builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
        builder.WithIconName("Agents");

        var endpoint = GetAgentEndpoint(builder);
        var hasHighlightedCommand = false;

        if (protocolSet.Any(IsA2AProtocol))
        {
            ConfigureA2A(builder, endpoint, normalizedPath ?? DefaultA2AAgentCardPath, protocolSet, a2AInvocationMode, ShouldHighlightCommand);
        }

        if (protocolSet.Contains(AgentProtocol.Responses))
        {
            ConfigureResponses(builder, endpoint, normalizedPath ?? DefaultResponsesPath, ShouldHighlightCommand);
        }

        if (protocolSet.Contains(AgentProtocol.AgUi))
        {
            ConfigureAgUi(builder, endpoint, normalizedPath ?? DefaultAgUiPath, ShouldHighlightCommand);
        }

        if (protocolSet.Contains(AgentProtocol.Acp))
        {
            ConfigureAcp(builder, endpoint, normalizedPath ?? DefaultAcpPath, builder.Resource.Name, ShouldHighlightCommand);
        }

        return builder;

        bool ShouldHighlightCommand()
        {
            if (hasHighlightedCommand)
            {
                return false;
            }

            hasHighlightedCommand = true;
            return true;
        }
    }

    internal static string GetAgentCardEnvironmentVariableName(string agentName)
    {
        return $"{EnvironmentVariableNameEncoder.Encode(agentName).ToUpperInvariant()}_AGENTCARD_URL";
    }

    internal static ReferenceExpression CreateA2AAgentCardUrl(EndpointReference endpoint, string agentCardPath)
    {
        return ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Url)}{NormalizePath(agentCardPath)}");
    }

    internal static string GetA2AAgentCardPath(AgentResourceAnnotation annotation)
    {
        return annotation.CustomPath ?? DefaultA2AAgentCardPath;
    }

    internal static bool IsA2AProtocol(AgentProtocol protocol)
    {
        return protocol is AgentProtocol.A2AJsonRpc or AgentProtocol.A2AGrpc or AgentProtocol.A2AHttpJson;
    }

    private static void ConfigureA2A<T>(
        IResourceBuilder<T> builder,
        EndpointReference endpoint,
        string agentCardPath,
        IReadOnlySet<AgentProtocol> protocols,
        A2AInvocationMode invocationMode,
        Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        builder.WithEnvironment(A2AAgentBaseUrlEnvironmentVariableName, ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Url)}"));

        AddProtocolEndpointUrl(builder, endpoint, agentCardPath, "Agent Card");

        if (protocols.Contains(AgentProtocol.A2AJsonRpc))
        {
            AddHttpCommandIfMissing(
                builder,
                commandName: $"{builder.Resource.Name}-a2a-jsonrpc-send-message",
                path: DefaultA2AJsonRpcPath,
                displayName: "Invoke A2A (JSON-RPC)",
                commandOptions: new()
                {
                    Method = HttpMethod.Post,
                    IconName = "ChatSparkle",
                    IconVariant = IconVariant.Regular,
                    IsHighlighted = shouldHighlightCommand(),
                    EndpointSelector = () => endpoint,
                    PrepareRequest = invocationMode is A2AInvocationMode.Streaming
                        ? PrepareA2AJsonRpcStreamingRequestAsync
                        : PrepareA2AJsonRpcRequestAsync,
                    GetCommandResult = invocationMode is A2AInvocationMode.Streaming
                        ? GetAgentCommandTextResultAsync
                        : GetAgentCommandJsonResultAsync
                });
        }

        if (protocols.Contains(AgentProtocol.A2AHttpJson))
        {
            AddHttpCommandIfMissing(
                builder,
                commandName: $"{builder.Resource.Name}-a2a-http-json-send-message",
                path: invocationMode is A2AInvocationMode.Streaming ? DefaultA2AHttpJsonStreamingMessagePath : DefaultA2AHttpJsonSendMessagePath,
                displayName: "Invoke A2A (HTTP+JSON)",
                commandOptions: new()
                {
                    Method = HttpMethod.Post,
                    IconName = "ChatSparkle",
                    IconVariant = IconVariant.Regular,
                    IsHighlighted = shouldHighlightCommand(),
                    EndpointSelector = () => endpoint,
                    PrepareRequest = PrepareA2AHttpJsonRequestAsync,
                    GetCommandResult = invocationMode is A2AInvocationMode.Streaming
                        ? GetAgentCommandTextResultAsync
                        : GetAgentCommandJsonResultAsync
                });
        }
    }

    private static void ConfigureResponses<T>(IResourceBuilder<T> builder, EndpointReference endpoint, string responsesPath, Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        AddProtocolEndpointUrl(builder, endpoint, responsesPath, "Responses Endpoint");

        AddHttpCommandIfMissing(
            builder,
            commandName: $"{builder.Resource.Name}-responses-send-message",
            path: responsesPath,
            displayName: "Invoke Responses",
            commandOptions: new()
            {
                Method = HttpMethod.Post,
                IconName = "ChatSparkle",
                IconVariant = IconVariant.Regular,
                IsHighlighted = shouldHighlightCommand(),
                EndpointSelector = () => endpoint,
                PrepareRequest = ctx => PrepareResponsesRequestAsync(ctx, builder.Resource.Name),
                GetCommandResult = GetAgentCommandJsonResultAsync
            });
    }

    private static void ConfigureAgUi<T>(IResourceBuilder<T> builder, EndpointReference endpoint, string agUiPath, Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints
    {
        AddProtocolEndpointUrl(builder, endpoint, agUiPath, "AG-UI Endpoint");

        AddHttpCommandIfMissing(
            builder,
            commandName: $"{builder.Resource.Name}-ag-ui-send-message",
            path: agUiPath,
            displayName: "Invoke AG-UI",
            commandOptions: new()
            {
                Method = HttpMethod.Post,
                IconName = "ChatSparkle",
                IconVariant = IconVariant.Regular,
                IsHighlighted = shouldHighlightCommand(),
                EndpointSelector = () => endpoint,
                PrepareRequest = PrepareAgUiRequestAsync,
                GetCommandResult = GetAgentCommandTextResultAsync
            });
    }

    private static void ConfigureAcp<T>(IResourceBuilder<T> builder, EndpointReference endpoint, string acpPath, string agentName, Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints
    {
        AddProtocolEndpointUrl(builder, endpoint, acpPath, "ACP Runs Endpoint");

        AddHttpCommandIfMissing(
            builder,
            commandName: $"{builder.Resource.Name}-acp-run",
            path: acpPath,
            displayName: "Invoke ACP",
            commandOptions: new()
            {
                Method = HttpMethod.Post,
                IconName = "ChatSparkle",
                IconVariant = IconVariant.Regular,
                IsHighlighted = shouldHighlightCommand(),
                EndpointSelector = () => endpoint,
                PrepareRequest = ctx => PrepareAcpRunRequestAsync(ctx, agentName),
                GetCommandResult = GetAgentCommandJsonResultAsync
            });
    }

    private static void AddProtocolEndpointUrl<T>(IResourceBuilder<T> builder, EndpointReference endpoint, string path, string displayText)
        where T : IResourceWithEndpoints
    {
        builder.WithUrlForEndpoint(
            endpoint.EndpointName,
            _ => new ResourceUrlAnnotation
            {
                Url = path,
                DisplayText = displayText
            });
    }

    private static async Task PrepareA2AJsonRpcRequestAsync(HttpCommandRequestContext ctx)
    {
        var message = await PromptForAgentMessageAsync(
            ctx,
            title: "A2A Agent",
            message: "Enter a message to send to the agent.",
            placeHolder: "What is the weather in Seattle?").ConfigureAwait(true);

        // A2A JSON-RPC 1.0 sends the abstract SendMessage operation as a JSON-RPC
        // request over HTTP. The message payload matches the canonical A2A data model.
        ctx.Request.Headers.Add("A2A-Version", "1.0");
        ctx.Request.Content = new StringContent(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = "SendMessage",
                ["params"] = CreateA2ASendMessageRequest(message)
            }.ToString(),
            Encoding.UTF8,
            "application/json");
    }

    private static async Task PrepareA2AJsonRpcStreamingRequestAsync(HttpCommandRequestContext ctx)
    {
        var message = await PromptForAgentMessageAsync(
            ctx,
            title: "A2A Agent",
            message: "Enter a message to send to the agent.",
            placeHolder: "What is the weather in Seattle?").ConfigureAwait(true);

        ctx.Request.Headers.Add("A2A-Version", "1.0");
        ctx.Request.Content = new StringContent(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = "SendStreamingMessage",
                ["params"] = CreateA2ASendMessageRequest(message)
            }.ToString(),
            Encoding.UTF8,
            "application/json");
    }

    private static async Task PrepareA2AHttpJsonRequestAsync(HttpCommandRequestContext ctx)
    {
        var message = await PromptForAgentMessageAsync(
            ctx,
            title: "A2A Agent",
            message: "Enter a message to send to the agent.",
            placeHolder: "What is the weather in Seattle?").ConfigureAwait(true);

        ctx.Request.Headers.Add("A2A-Version", "1.0");
        ctx.Request.Content = new StringContent(
            CreateA2ASendMessageRequest(message).ToString(),
            Encoding.UTF8,
            "application/a2a+json");
    }

    private static async Task PrepareResponsesRequestAsync(HttpCommandRequestContext ctx, string agentName)
    {
        var message = await PromptForAgentMessageAsync(
            ctx,
            title: "Responses API",
            message: "Enter a message to send to the agent.",
            placeHolder: "Hello, what can you do?").ConfigureAwait(true);

        ctx.Request.Content = new StringContent(
            new JsonObject
            {
                ["agent"] = new JsonObject
                {
                    ["name"] = agentName
                },
                ["input"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = message
                    }
                }
            }.ToString(),
            Encoding.UTF8,
            "application/json");
    }

    private static async Task PrepareAgUiRequestAsync(HttpCommandRequestContext ctx)
    {
        var message = await PromptForAgentMessageAsync(
            ctx,
            title: "AG-UI Agent",
            message: "Enter a message to send to the agent.",
            placeHolder: "What is the weather in Seattle?").ConfigureAwait(true);

        ctx.Request.Headers.Accept.ParseAdd("text/event-stream");
        ctx.Request.Content = new StringContent(
            new JsonObject
            {
                ["threadId"] = Guid.NewGuid().ToString("N"),
                ["runId"] = Guid.NewGuid().ToString("N"),
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = Guid.NewGuid().ToString("N"),
                        ["role"] = "user",
                        ["content"] = message
                    }
                }
            }.ToString(),
            Encoding.UTF8,
            "application/json");
    }

    private static async Task PrepareAcpRunRequestAsync(HttpCommandRequestContext ctx, string agentName)
    {
        var message = await PromptForAgentMessageAsync(
            ctx,
            title: "ACP Agent",
            message: "Enter a message to send to the agent.",
            placeHolder: "Hello, what can you do?").ConfigureAwait(true);

        ctx.Request.Content = new StringContent(
            new JsonObject
            {
                ["agent_name"] = agentName,
                ["mode"] = "sync",
                ["input"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["content_type"] = "text/plain",
                                ["content"] = message
                            }
                        }
                    }
                }
            }.ToString(),
            Encoding.UTF8,
            "application/json");
    }

    private static async Task<string> PromptForAgentMessageAsync(HttpCommandRequestContext ctx, string title, string message, string placeHolder)
    {
        var interactionService = ctx.ServiceProvider.GetRequiredService<IInteractionService>();
        var result = await interactionService.PromptInputAsync(
            title: title,
            message: message,
            inputLabel: "Message",
            placeHolder: placeHolder,
            cancellationToken: ctx.CancellationToken).ConfigureAwait(true);

        if (result.Canceled || string.IsNullOrWhiteSpace(result.Data.Value))
        {
            ctx.HttpClient.CancelPendingRequests();
            throw new OperationCanceledException("User canceled the input prompt.");
        }

        return result.Data.Value;
    }

    private static async Task<ExecuteCommandResult> GetAgentCommandJsonResultAsync(HttpCommandResultContext ctx)
    {
        ctx.CancellationToken.ThrowIfCancellationRequested();

        if (!ctx.Response.IsSuccessStatusCode)
        {
            var errorPayload = await ctx.Response.Content.ReadAsStringAsync(ctx.CancellationToken).ConfigureAwait(true);
            return CommandResults.Failure(
                $"Agent request failed with status code {(int)ctx.Response.StatusCode} ({ctx.Response.StatusCode}).",
                errorPayload,
                CommandResultFormat.Text);
        }

        var responseJson = await ctx.Response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ctx.CancellationToken).ConfigureAwait(true);
        if (responseJson is null)
        {
            return CommandResults.Failure("Agent returned an empty response.");
        }

        return CommandResults.Success(
            message: "Agent response received.",
            result: JsonSerializer.Serialize(responseJson, s_indentedJsonOptions),
            resultFormat: CommandResultFormat.Json,
            displayImmediately: true);
    }

    private static async Task<ExecuteCommandResult> GetAgentCommandTextResultAsync(HttpCommandResultContext ctx)
    {
        ctx.CancellationToken.ThrowIfCancellationRequested();

        var responseBody = await ctx.Response.Content.ReadAsStringAsync(ctx.CancellationToken).ConfigureAwait(true);
        if (!ctx.Response.IsSuccessStatusCode)
        {
            return CommandResults.Failure(
                $"Agent request failed with status code {(int)ctx.Response.StatusCode} ({ctx.Response.StatusCode}).",
                responseBody,
                CommandResultFormat.Text);
        }

        return CommandResults.Success(
            message: "Agent response received.",
            result: responseBody,
            resultFormat: CommandResultFormat.Text,
            displayImmediately: true);
    }

    private static JsonObject CreateA2ASendMessageRequest(string message)
    {
        return new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["messageId"] = Guid.NewGuid().ToString("N"),
                ["role"] = "ROLE_USER",
                ["parts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["text"] = message
                    }
                }
            },
            ["configuration"] = new JsonObject
            {
                ["returnImmediately"] = false,
                ["acceptedOutputModes"] = new JsonArray("text/plain")
            }
        };
    }

    private static void AddHttpCommandIfMissing<T>(
        IResourceBuilder<T> builder,
        string commandName,
        string path,
        string displayName,
        HttpCommandOptions commandOptions)
        where T : IResourceWithEndpoints
    {
        if (builder.Resource.Annotations.OfType<ResourceCommandAnnotation>().Any(c => string.Equals(c.Name, commandName, StringComparison.Ordinal)))
        {
            return;
        }

        builder.WithHttpCommand(path, displayName, endpointSelector: commandOptions.EndpointSelector, commandName, commandOptions);
    }

    private static EndpointReference GetAgentEndpoint<T>(IResourceBuilder<T> builder)
        where T : IResourceWithEndpoints
    {
        var endpointName = builder.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.UriScheme is "http" or "https")
            .OrderByDescending(e => string.Equals(e.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .FirstOrDefault() ?? "http";

        return new EndpointReference(builder.Resource, endpointName, KnownNetworkIdentifiers.LocalhostNetwork);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path[0] == '/' ? path : $"/{path}";
    }

}

#pragma warning restore ASPIREINTERACTION001
