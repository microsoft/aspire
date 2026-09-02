// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Agents;

#pragma warning disable ASPIREINTERACTION001 // InteractionInput is used to describe dashboard command arguments.

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

    private const string DefaultA2AHttpJsonSendMessagePath = "/message:send";
    private const string DefaultA2AHttpJsonStreamingMessagePath = "/message:stream";
    private const string DefaultA2AHttpJsonV03SendMessagePath = "/v1/message:send";
    private const string DefaultA2AHttpJsonV03StreamingMessagePath = "/v1/message:stream";
    private const string A2AProtocolBindingJsonRpc = "JSONRPC";
    private const string A2AProtocolBindingHttpJson = "HTTP+JSON";
    private const string AgentNameArgumentName = "agentName";
    private const string AgentMessageArgumentName = "message";

    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Configures the resource as an agent that supports the specified protocol.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="protocol">The protocol supported by the agent.</param>
    /// <param name="agentName">The registered agent name for Responses or ACP. When omitted, the dashboard command prompts for it.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// Call this method once for each protocol exposed by the resource. Responses and ACP agent names are protocol
    /// identifiers and do not need to match the Aspire resource name.
    /// <code>
    /// var agent = builder.AddProject&lt;Projects.Agent&gt;("agent-service")
    ///     .AsAgent(AgentProtocol.A2A)
    ///     .AsAgent(AgentProtocol.Responses, agentName: "weather-agent");
    /// </code>
    /// </remarks>
    /// <ats-remarks>
    /// Call this method once for each protocol exposed by the resource. Responses and ACP agent names are protocol
    /// identifiers and do not need to match the Aspire resource name.
    /// </ats-remarks>
    [AspireExport]
    public static IResourceBuilder<T> AsAgent<T>(
        this IResourceBuilder<T> builder,
        AgentProtocol protocol,
        string? agentName = null)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsAgentCore(builder, agentCustomPath: null, protocol, A2AInvocationMode.NonStreaming, agentName);
    }

    /// <summary>
    /// Configures the resource as an A2A agent using the specified dashboard invocation mode.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="protocol">The protocol supported by the agent.</param>
    /// <param name="invocationMode">The invocation mode used by dashboard commands.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="invocationMode"/> is used with a protocol other than A2A.</exception>
    /// <remarks>
    /// Streaming must be explicitly requested and is available only when the A2A agent card advertises support.
    /// <code>
    /// var agent = builder.AddProject&lt;Projects.Agent&gt;("agent")
    ///     .AsAgent(AgentProtocol.A2A, A2AInvocationMode.Streaming);
    /// </code>
    /// </remarks>
    /// <ats-remarks>
    /// Streaming must be explicitly requested and is available only when the A2A agent card advertises support.
    /// </ats-remarks>
    [AspireExport("asAgentWithInvocationMode")]
    public static IResourceBuilder<T> AsAgent<T>(
        this IResourceBuilder<T> builder,
        AgentProtocol protocol,
        A2AInvocationMode invocationMode)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsAgentCore(builder, agentCustomPath: null, protocol, invocationMode, agentName: null);
    }

    /// <summary>
    /// Configures the resource as an agent that supports the specified protocol using a custom protocol path.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="agentCustomPath">The custom path for protocol-specific dashboard commands and URLs.</param>
    /// <param name="protocol">The protocol supported by the agent.</param>
    /// <param name="agentName">The registered agent name for Responses or ACP. When omitted, the dashboard command prompts for it.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// Configure each protocol independently when a resource exposes multiple protocols or non-default paths.
    /// <code>
    /// var agent = builder.AddProject&lt;Projects.Agent&gt;("agent-service")
    ///     .AsAgent("/agent-card.json", AgentProtocol.A2A)
    ///     .AsAgent("/responses", AgentProtocol.Responses, agentName: "weather-agent");
    /// </code>
    /// </remarks>
    /// <ats-remarks>
    /// Configure each protocol independently when a resource exposes multiple protocols or non-default paths.
    /// </ats-remarks>
    [AspireExport("asAgentWithPath")]
    public static IResourceBuilder<T> AsAgent<T>(
        this IResourceBuilder<T> builder,
        string? agentCustomPath,
        AgentProtocol protocol,
        string? agentName = null)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsAgentCore(builder, agentCustomPath, protocol, A2AInvocationMode.NonStreaming, agentName);
    }

    /// <summary>
    /// Configures the resource as an A2A agent using a custom protocol path and dashboard invocation mode.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="agentCustomPath">The custom path for protocol-specific dashboard commands and URLs.</param>
    /// <param name="protocol">The protocol supported by the agent.</param>
    /// <param name="invocationMode">The invocation mode used by dashboard commands.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="invocationMode"/> is used with a protocol other than A2A.</exception>
    /// <remarks>
    /// Use this overload when an A2A agent has both a non-default agent-card path and streaming invocation enabled.
    /// <code>
    /// var agent = builder.AddProject&lt;Projects.Agent&gt;("agent")
    ///     .AsAgent("/agent-card.json", AgentProtocol.A2A, A2AInvocationMode.Streaming);
    /// </code>
    /// </remarks>
    /// <ats-remarks>
    /// Use this overload when an A2A agent has both a non-default agent-card path and streaming invocation enabled.
    /// </ats-remarks>
    [AspireExport("asAgentWithPathAndInvocationMode")]
    public static IResourceBuilder<T> AsAgent<T>(
        this IResourceBuilder<T> builder,
        string? agentCustomPath,
        AgentProtocol protocol,
        A2AInvocationMode invocationMode)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsAgentCore(builder, agentCustomPath, protocol, invocationMode, agentName: null);
    }

    private static IResourceBuilder<T> AsAgentCore<T>(
        IResourceBuilder<T> builder,
        string? agentCustomPath,
        AgentProtocol protocol,
        A2AInvocationMode invocationMode,
        string? agentName)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (protocol is not AgentProtocol.A2A && invocationMode is not A2AInvocationMode.NonStreaming)
        {
            throw new ArgumentException("A2A invocation modes can only be configured for the A2A protocol.", nameof(invocationMode));
        }
        if (agentName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
            if (protocol is not (AgentProtocol.Responses or AgentProtocol.Acp))
            {
                throw new ArgumentException("An agent name can only be configured for the Responses or ACP protocol.", nameof(agentName));
            }
        }

        var normalizedPath = NormalizePath(agentCustomPath);
        var annotation = new AgentResourceAnnotation(protocol, normalizedPath, invocationMode, agentName);

        builder.WithAnnotation(annotation);
        builder.WithIconName("Agents");

        var hasHighlightedCommand = builder.Resource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .Any(command => command.IsHighlighted);

        if (IsA2AProtocol(protocol))
        {
            ConfigureA2A(builder, normalizedPath ?? DefaultA2AAgentCardPath, invocationMode, ShouldHighlightCommand);
        }

        if (protocol is AgentProtocol.Responses)
        {
            ConfigureResponses(builder, normalizedPath ?? DefaultResponsesPath, agentName, ShouldHighlightCommand);
        }

        if (protocol is AgentProtocol.AgUi)
        {
            ConfigureAgUi(builder, normalizedPath ?? DefaultAgUiPath, ShouldHighlightCommand);
        }

        if (protocol is AgentProtocol.Acp)
        {
            ConfigureAcp(builder, normalizedPath ?? DefaultAcpPath, agentName, ShouldHighlightCommand);
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
        return protocol is AgentProtocol.A2A;
    }

    internal static EndpointReference GetDefaultAgentEndpoint(IResourceWithEndpoints source, NetworkIdentifier network)
    {
        var endpointName = source.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => !e.ExcludeReferenceEndpoint && e.UriScheme is "http" or "https")
            .OrderByDescending(e => string.Equals(e.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .FirstOrDefault()
            ?? throw new DistributedApplicationException(
                $"Could not configure agent resource '{source.Name}' because no non-excluded HTTP or HTTPS endpoint was found.");

        return new EndpointReference(source, endpointName, network);
    }

    private static void ConfigureA2A<T>(
        IResourceBuilder<T> builder,
        string agentCardPath,
        A2AInvocationMode invocationMode,
        Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        builder.WithEnvironment(context =>
        {
            var network = context.Resource.IsContainer()
                ? KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                : KnownNetworkIdentifiers.LocalhostNetwork;
            var advertisedEndpoint = GetDefaultAgentEndpoint(builder.Resource, network);
            context.EnvironmentVariables[A2AAgentBaseUrlEnvironmentVariableName] =
                ReferenceExpression.Create($"{advertisedEndpoint.Property(EndpointProperty.Url)}");
        });

        AddProtocolEndpointUrl(builder, agentCardPath, "Agent Card");

        AddHttpCommandIfMissing(
            builder,
            commandName: $"{builder.Resource.Name}-a2a-send-message",
            path: agentCardPath,
            displayName: "Invoke A2A",
            commandOptions: new()
            {
                Method = HttpMethod.Get,
                IconName = "ChatSparkle",
                IconVariant = IconVariant.Regular,
                IsHighlighted = shouldHighlightCommand(),
                Arguments = [CreateMessageArgument("What is the weather in Seattle?")],
                EndpointSelector = () => GetDefaultAgentEndpoint(builder.Resource, KnownNetworkIdentifiers.LocalhostNetwork),
                PrepareRequest = ctx => PrepareA2ARequestAsync(ctx, invocationMode),
                GetCommandResult = GetA2ACommandResultAsync
            });
    }

    private static void ConfigureResponses<T>(
        IResourceBuilder<T> builder,
        string responsesPath,
        string? agentName,
        Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        AddProtocolEndpointUrl(builder, responsesPath, "Responses Endpoint");

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
                Arguments = CreateAgentCommandArguments(agentName, "Hello, what can you do?"),
                EndpointSelector = () => GetDefaultAgentEndpoint(builder.Resource, KnownNetworkIdentifiers.LocalhostNetwork),
                PrepareRequest = ctx => PrepareResponsesRequestAsync(ctx, agentName),
                GetCommandResult = GetAgentCommandJsonResultAsync
            });
    }

    private static void ConfigureAgUi<T>(IResourceBuilder<T> builder, string agUiPath, Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints
    {
        AddProtocolEndpointUrl(builder, agUiPath, "AG-UI Endpoint");

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
                Arguments = [CreateMessageArgument("What is the weather in Seattle?")],
                EndpointSelector = () => GetDefaultAgentEndpoint(builder.Resource, KnownNetworkIdentifiers.LocalhostNetwork),
                PrepareRequest = PrepareAgUiRequestAsync,
                GetCommandResult = GetAgUiCommandResultAsync
            });
    }

    private static void ConfigureAcp<T>(
        IResourceBuilder<T> builder,
        string acpPath,
        string? agentName,
        Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints
    {
        AddProtocolEndpointUrl(builder, acpPath, "ACP Runs Endpoint");

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
                Arguments = CreateAgentCommandArguments(agentName, "Hello, what can you do?"),
                EndpointSelector = () => GetDefaultAgentEndpoint(builder.Resource, KnownNetworkIdentifiers.LocalhostNetwork),
                PrepareRequest = ctx => PrepareAcpRunRequestAsync(ctx, agentName),
                GetCommandResult = GetAcpCommandResultAsync
            });
    }

    private static void AddProtocolEndpointUrl<T>(IResourceBuilder<T> builder, string path, string displayText)
        where T : IResourceWithEndpoints
    {
        builder.WithUrls(context =>
        {
            EndpointReference endpoint;
            try
            {
                endpoint = GetDefaultAgentEndpoint(builder.Resource, KnownNetworkIdentifiers.LocalhostNetwork);
            }
            catch (DistributedApplicationException ex)
            {
                context.Logger.LogWarning(ex, "Could not add agent protocol URL for resource '{ResourceName}'.", builder.Resource.Name);
                return;
            }

            context.Urls.Add(new ResourceUrlAnnotation
            {
                Url = path,
                DisplayText = displayText,
                Endpoint = endpoint
            });
        });
    }

    private static async Task PrepareA2ARequestAsync(HttpCommandRequestContext ctx, A2AInvocationMode invocationMode)
    {
        var cardUri = ctx.Request.RequestUri ?? throw new InvalidOperationException("Could not determine the A2A agent card URL.");
        var invocation = await ResolveA2AInvocationAsync(ctx, cardUri, invocationMode).ConfigureAwait(true);

        var message = GetAgentMessage(ctx.Arguments);

        ctx.Request.Method = HttpMethod.Post;
        ctx.Request.RequestUri = invocation.RequestUri;
        ctx.Request.Headers.Add("A2A-Version", invocation.ProtocolVersion ?? "1.0");
        if (invocation.IsStreaming)
        {
            ctx.Request.Headers.Accept.ParseAdd("text/event-stream");
        }

        if (invocation.ProtocolBinding is A2AProtocolBindingJsonRpc)
        {
            // A2A JSON-RPC sends the abstract message/send operation as a JSON-RPC
            // request over HTTP. Streaming support is advertised in the agent card.
            var isV03 = IsA2AProtocolVersionV03(invocation.ProtocolVersion);
            ctx.Request.Content = new StringContent(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["method"] = GetA2AJsonRpcMethod(invocation.IsStreaming, isV03),
                    ["params"] = CreateA2ASendMessageRequest(
                        message,
                        isV03,
                        includeConfiguration: true)
                }.ToString(),
                Encoding.UTF8,
                "application/json");
            return;
        }

        var isHttpJsonV03 = IsA2AProtocolVersionV03(invocation.ProtocolVersion);
        ctx.Request.Content = new StringContent(
            CreateA2ASendMessageRequest(
                message,
                isHttpJsonV03,
                includeConfiguration: !isHttpJsonV03).ToString(),
            Encoding.UTF8,
            "application/a2a+json");
    }

    private static string GetA2AJsonRpcMethod(bool isStreaming, bool isV03)
    {
        return (isStreaming, isV03) switch
        {
            (true, true) => "message/stream",
            (false, true) => "message/send",
            (true, false) => "SendStreamingMessage",
            (false, false) => "SendMessage"
        };
    }

    private static bool IsA2AProtocolVersionV03(string? protocolVersion)
    {
        return protocolVersion is not null && protocolVersion.StartsWith("0.", StringComparison.Ordinal);
    }

    private static async Task<A2AInvocation> ResolveA2AInvocationAsync(
        HttpCommandRequestContext ctx,
        Uri cardUri,
        A2AInvocationMode invocationMode)
    {
        using var response = await ctx.HttpClient.GetAsync(cardUri, ctx.CancellationToken).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Could not read the A2A agent card at '{cardUri}'. The request failed with status code {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var card = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ctx.CancellationToken).ConfigureAwait(true)
            ?? throw new InvalidOperationException($"The A2A agent card at '{cardUri}' was empty.");

        var supportsStreaming = card["capabilities"]?["streaming"]?.GetValue<bool>() is true;
        var streaming = invocationMode is A2AInvocationMode.Streaming;
        if (streaming && !supportsStreaming)
        {
            throw new InvalidOperationException($"The A2A agent card at '{cardUri}' does not advertise streaming support.");
        }
        var interfaces = GetA2AInterfaces(card, cardUri).ToArray();

        foreach (var agentInterface in interfaces)
        {
            var interfaceUri = CreateDashboardReachableA2AUri(cardUri, agentInterface.Url);
            if (agentInterface.ProtocolBinding is A2AProtocolBindingJsonRpc)
            {
                return new A2AInvocation(interfaceUri, agentInterface.ProtocolBinding, agentInterface.ProtocolVersion, streaming);
            }

            if (agentInterface.ProtocolBinding is A2AProtocolBindingHttpJson)
            {
                var path = (streaming, IsA2AProtocolVersionV03(agentInterface.ProtocolVersion)) switch
                {
                    (true, true) => DefaultA2AHttpJsonV03StreamingMessagePath,
                    (false, true) => DefaultA2AHttpJsonV03SendMessagePath,
                    (true, false) => DefaultA2AHttpJsonStreamingMessagePath,
                    (false, false) => DefaultA2AHttpJsonSendMessagePath
                };
                var requestUri = AppendPath(interfaceUri, path);
                return new A2AInvocation(requestUri, agentInterface.ProtocolBinding, agentInterface.ProtocolVersion, streaming);
            }
        }

        var bindings = interfaces.Length == 0
            ? "none"
            : string.Join(", ", interfaces.Select(agentInterface => agentInterface.ProtocolBinding));
        throw new InvalidOperationException($"The A2A agent card at '{cardUri}' does not advertise a dashboard-invokable protocol binding. Supported dashboard bindings are JSONRPC and HTTP+JSON. Advertised bindings: {bindings}.");
    }

    private static IEnumerable<A2AAgentInterface> GetA2AInterfaces(JsonObject card, Uri cardUri)
    {
        var supportedInterfaces = card["supportedInterfaces"]?.AsArray();
        if (supportedInterfaces is not null)
        {
            foreach (var item in supportedInterfaces.OfType<JsonObject>())
            {
                var agentInterface = CreateA2AAgentInterface(item, cardUri);
                if (agentInterface is not null)
                {
                    yield return agentInterface;
                }
            }

            yield break;
        }

        if (card["url"]?.GetValue<string>() is { Length: > 0 } url)
        {
            var protocolBinding = card["preferredTransport"]?.GetValue<string>() ?? A2AProtocolBindingJsonRpc;
            if (TryCreateUri(cardUri, url, out var interfaceUri))
            {
                yield return new A2AAgentInterface(interfaceUri, NormalizeA2AProtocolBinding(protocolBinding), card["protocolVersion"]?.GetValue<string>());
            }
        }
    }

    private static A2AAgentInterface? CreateA2AAgentInterface(JsonObject item, Uri cardUri)
    {
        if (item["url"]?.GetValue<string>() is not { Length: > 0 } url ||
            item["protocolBinding"]?.GetValue<string>() is not { Length: > 0 } protocolBinding ||
            !TryCreateUri(cardUri, url, out var interfaceUri))
        {
            return null;
        }

        return new A2AAgentInterface(
            interfaceUri,
            NormalizeA2AProtocolBinding(protocolBinding),
            item["protocolVersion"]?.GetValue<string>());
    }

    private static bool TryCreateUri(Uri baseUri, string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!))
        {
            return true;
        }

        return Uri.TryCreate(baseUri, url, out uri!);
    }

    private static string NormalizeA2AProtocolBinding(string protocolBinding)
    {
        return protocolBinding.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
    }

    private static Uri CreateDashboardReachableA2AUri(Uri cardUri, Uri interfaceUri)
    {
        // A containerized agent should advertise a container-network URL in its card so
        // container consumers can call it. The dashboard command reads that same card
        // through the selected Aspire endpoint, so keep the advertised path but use the
        // already-resolved card endpoint origin for host-side invocation.
        var builder = new UriBuilder(interfaceUri)
        {
            Scheme = cardUri.Scheme,
            Host = cardUri.Host,
            Port = cardUri.IsDefaultPort ? -1 : cardUri.Port
        };

        return builder.Uri;
    }

    private static Uri AppendPath(Uri baseUri, string path)
    {
        var builder = new UriBuilder(baseUri);
        var basePath = builder.Path.TrimEnd('/');
        builder.Path = $"{basePath}{path}";
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;

        return builder.Uri;
    }

    private static Task PrepareResponsesRequestAsync(HttpCommandRequestContext ctx, string? agentName)
    {
        agentName = GetAgentName(ctx.Arguments, agentName);
        var message = GetAgentMessage(ctx.Arguments);

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

        return Task.CompletedTask;
    }

    private static Task PrepareAgUiRequestAsync(HttpCommandRequestContext ctx)
    {
        var message = GetAgentMessage(ctx.Arguments);

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

        return Task.CompletedTask;
    }

    private static Task PrepareAcpRunRequestAsync(HttpCommandRequestContext ctx, string? agentName)
    {
        agentName = GetAgentName(ctx.Arguments, agentName);
        var message = GetAgentMessage(ctx.Arguments);

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

        return Task.CompletedTask;
    }

    private static InteractionInput[] CreateAgentCommandArguments(string? agentName, string messagePlaceholder)
    {
        return agentName is null
            ? [CreateAgentNameArgument(), CreateMessageArgument(messagePlaceholder)]
            : [CreateMessageArgument(messagePlaceholder)];
    }

    private static InteractionInput CreateAgentNameArgument()
    {
        return new InteractionInput
        {
            Name = AgentNameArgumentName,
            Label = "Agent Name",
            Description = "Registered protocol agent name.",
            InputType = InputType.Text,
            Required = true
        };
    }

    private static InteractionInput CreateMessageArgument(string placeholder)
    {
        return new InteractionInput
        {
            Name = AgentMessageArgumentName,
            Label = "Message",
            Description = "Message to send to the agent.",
            InputType = InputType.Text,
            Required = true,
            Placeholder = placeholder
        };
    }

    private static string GetAgentMessage(InteractionInputCollection arguments)
    {
        return arguments.GetString(AgentMessageArgumentName)
            ?? throw new InvalidOperationException("Agent command message argument is required.");
    }

    private static string GetAgentName(InteractionInputCollection arguments, string? configuredAgentName)
    {
        return configuredAgentName
            ?? arguments.GetString(AgentNameArgumentName)
            ?? throw new InvalidOperationException("Agent command agent name argument is required.");
    }

    private static Task<ExecuteCommandResult> GetAgentCommandJsonResultAsync(HttpCommandResultContext ctx)
    {
        return GetAgentCommandJsonResultAsync(ctx, validateA2ATaskState: false, validateAcpRunStatus: false);
    }

    private static async Task<ExecuteCommandResult> GetAgentCommandJsonResultAsync(
        HttpCommandResultContext ctx,
        bool validateA2ATaskState,
        bool validateAcpRunStatus)
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

        if (validateAcpRunStatus && TryGetAcpTerminalFailureStatus(responseJson, out var runStatus))
        {
            return CommandResults.Failure(
                $"Agent run ended in the '{runStatus}' state.",
                JsonSerializer.Serialize(responseJson["error"] ?? responseJson, s_indentedJsonOptions),
                CommandResultFormat.Json);
        }

        if (responseJson["error"] is { } error)
        {
            return CommandResults.Failure(
                "Agent request returned a JSON-RPC error.",
                JsonSerializer.Serialize(error, s_indentedJsonOptions),
                CommandResultFormat.Json);
        }

        if (validateA2ATaskState && TryGetA2ATerminalFailureState(responseJson, out var taskState))
        {
            return CreateA2ATaskFailure(responseJson, taskState);
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

    private static async Task<ExecuteCommandResult> GetA2ACommandSseResultAsync(HttpCommandResultContext ctx)
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

        foreach (var responseJson in GetSseJsonPayloads(responseBody))
        {
            if (responseJson["error"] is { } error)
            {
                return CommandResults.Failure(
                    "Agent request returned a JSON-RPC error.",
                    JsonSerializer.Serialize(error, s_indentedJsonOptions),
                    CommandResultFormat.Json);
            }

            if (TryGetA2ATerminalFailureState(responseJson, out var taskState))
            {
                return CreateA2ATaskFailure(responseJson, taskState);
            }
        }

        return CommandResults.Success(
            message: "Agent response received.",
            result: responseBody,
            resultFormat: CommandResultFormat.Text,
            displayImmediately: true);
    }

    private static async Task<ExecuteCommandResult> GetAgUiCommandResultAsync(HttpCommandResultContext ctx)
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

        var terminalEvent = GetSseJsonPayloads(responseBody)
            .FirstOrDefault(payload => GetJsonString(payload["type"]) is "RUN_FINISHED" or "RUN_ERROR");
        if (string.Equals(GetJsonString(terminalEvent?["type"]), "RUN_ERROR", StringComparison.Ordinal))
        {
            return CommandResults.Failure(
                "Agent run returned a RUN_ERROR event.",
                JsonSerializer.Serialize(terminalEvent, s_indentedJsonOptions),
                CommandResultFormat.Json);
        }

        if (terminalEvent is null)
        {
            return CommandResults.Failure(
                "Agent run ended without a terminal event.",
                responseBody,
                CommandResultFormat.Text);
        }

        return CommandResults.Success(
            message: "Agent response received.",
            result: responseBody,
            resultFormat: CommandResultFormat.Text,
            displayImmediately: true);
    }

    private static Task<ExecuteCommandResult> GetA2ACommandResultAsync(HttpCommandResultContext ctx)
    {
        return ctx.Response.Content.Headers.ContentType?.MediaType switch
        {
            "application/json" or "application/a2a+json" => GetAgentCommandJsonResultAsync(ctx, validateA2ATaskState: true, validateAcpRunStatus: false),
            "text/event-stream" => GetA2ACommandSseResultAsync(ctx),
            _ => GetAgentCommandTextResultAsync(ctx)
        };
    }

    private static Task<ExecuteCommandResult> GetAcpCommandResultAsync(HttpCommandResultContext ctx)
    {
        return GetAgentCommandJsonResultAsync(ctx, validateA2ATaskState: false, validateAcpRunStatus: true);
    }

    private static ExecuteCommandResult CreateA2ATaskFailure(JsonObject responseJson, string taskState)
    {
        return CommandResults.Failure(
            $"Agent task ended in the '{taskState}' state.",
            JsonSerializer.Serialize(responseJson, s_indentedJsonOptions),
            CommandResultFormat.Json);
    }

    private static bool TryGetA2ATerminalFailureState(JsonObject responseJson, [NotNullWhen(true)] out string? taskState)
    {
        var result = responseJson["result"] as JsonObject ?? responseJson;
        taskState = result["status"] is JsonObject status ? GetJsonString(status["state"]) : null;
        if (taskState is null)
        {
            return false;
        }

        var normalizedState = taskState.StartsWith("TASK_STATE_", StringComparison.OrdinalIgnoreCase)
            ? taskState["TASK_STATE_".Length..]
            : taskState;

        return normalizedState.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || normalizedState.Equals("rejected", StringComparison.OrdinalIgnoreCase)
            || normalizedState.Equals("canceled", StringComparison.OrdinalIgnoreCase)
            || normalizedState.Equals("cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetJsonString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    }

    private static bool TryGetAcpTerminalFailureStatus(JsonObject responseJson, [NotNullWhen(true)] out string? runStatus)
    {
        runStatus = GetJsonString(responseJson["status"]);
        return runStatus is not null &&
            (runStatus.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
             runStatus.Equals("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<JsonObject> GetSseJsonPayloads(string responseBody)
    {
        // A2A and AG-UI streaming responses contain one JSON object per SSE event:
        //   event: message
        //   data: {"jsonrpc":"2.0","id":"...","error":{"code":-32603,"message":"Agent failed."}}
        //   data: {"type":"RUN_ERROR","message":"Agent failed."}
        // Join consecutive data fields because SSE allows an event payload to span multiple lines.
        using var reader = new StringReader(responseBody);
        var eventData = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                if (TryParseEventData(eventData, out var payload))
                {
                    yield return payload;
                }

                eventData.Clear();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["data:".Length..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (eventData.Length > 0)
            {
                eventData.Append('\n');
            }

            eventData.Append(value);
        }

        if (TryParseEventData(eventData, out var finalPayload))
        {
            yield return finalPayload;
        }

        static bool TryParseEventData(StringBuilder eventData, [NotNullWhen(true)] out JsonObject? payload)
        {
            if (eventData.Length == 0)
            {
                payload = null;
                return false;
            }

            try
            {
                payload = JsonNode.Parse(eventData.ToString()) as JsonObject;
                return payload is not null;
            }
            catch (JsonException)
            {
                payload = null;
                return false;
            }
        }
    }

    private static JsonObject CreateA2ASendMessageRequest(
        string message,
        bool isV03,
        bool includeConfiguration)
    {
        var messageObject = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString("N"),
            ["role"] = isV03 ? "user" : "ROLE_USER",
            ["parts"] = new JsonArray
            {
                new JsonObject
                {
                    ["text"] = message
                }
            }
        };

        if (isV03)
        {
            messageObject["kind"] = "message";
            messageObject["parts"]![0]!["kind"] = "text";
        }

        var request = new JsonObject
        {
            ["message"] = messageObject
        };

        if (includeConfiguration)
        {
            request["configuration"] = new JsonObject
            {
                ["returnImmediately"] = false,
                ["acceptedOutputModes"] = new JsonArray("text/plain")
            };
        }

        return request;
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

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path[0] == '/' ? path : $"/{path}";
    }

    private sealed record A2AAgentInterface(Uri Url, string ProtocolBinding, string? ProtocolVersion);

    private sealed record A2AInvocation(Uri RequestUri, string ProtocolBinding, string? ProtocolVersion, bool IsStreaming);

}

#pragma warning restore ASPIREINTERACTION001
