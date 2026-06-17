// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;

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
    private const string AgentMessageArgumentName = "message";

    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Configures the resource as an agent that supports the specified protocol.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="protocol">The protocol supported by the agent.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<T> AsAgent<T>(this IResourceBuilder<T> builder, AgentProtocol protocol)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsAgent(builder, agentCustomPath: null, protocol);
    }

    /// <summary>
    /// Configures the resource as an agent that supports the specified protocol using a custom protocol path.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="agentCustomPath">The custom path for protocol-specific dashboard commands and URLs.</param>
    /// <param name="protocol">The protocol supported by the agent.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("asAgentWithPath")]
    public static IResourceBuilder<T> AsAgent<T>(this IResourceBuilder<T> builder, string? agentCustomPath, AgentProtocol protocol)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var normalizedPath = NormalizePath(agentCustomPath);
        var annotation = new AgentResourceAnnotation(protocol, normalizedPath);

        builder.WithAnnotation(annotation);
        builder.WithIconName("Agents");

        var endpoint = GetAgentEndpoint(builder, KnownNetworkIdentifiers.LocalhostNetwork);
        var hasHighlightedCommand = builder.Resource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .Any(command => command.IsHighlighted);

        if (IsA2AProtocol(protocol))
        {
            ConfigureA2A(builder, endpoint, normalizedPath ?? DefaultA2AAgentCardPath, ShouldHighlightCommand);
        }

        if (protocol is AgentProtocol.Responses)
        {
            ConfigureResponses(builder, endpoint, normalizedPath ?? DefaultResponsesPath, ShouldHighlightCommand);
        }

        if (protocol is AgentProtocol.AgUi)
        {
            ConfigureAgUi(builder, endpoint, normalizedPath ?? DefaultAgUiPath, ShouldHighlightCommand);
        }

        if (protocol is AgentProtocol.Acp)
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

    /// <summary>
    /// Adds a reference from the destination resource to an agent container resource.
    /// </summary>
    /// <typeparam name="TDestination">The type of the destination resource.</typeparam>
    /// <param name="builder">The destination resource builder.</param>
    /// <param name="source">The agent container resource builder to reference.</param>
    /// <param name="name">An optional name used for the injected environment variables.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the standard <c>WithReference</c> overload instead.</remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the generic withReference dispatcher export from Aspire.Hosting.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<ContainerResource> source,
        string? name = null)
        where TDestination : IResourceWithEnvironment
    {
        return WithAgentReference(builder, source, name);
    }

    /// <summary>
    /// Adds a reference from the destination resource to an agent executable resource.
    /// </summary>
    /// <typeparam name="TDestination">The type of the destination resource.</typeparam>
    /// <param name="builder">The destination resource builder.</param>
    /// <param name="source">The agent executable resource builder to reference.</param>
    /// <param name="name">An optional name used for the injected environment variables.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the standard <c>WithReference</c> overload instead.</remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the generic withReference dispatcher export from Aspire.Hosting.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<ExecutableResource> source,
        string? name = null)
        where TDestination : IResourceWithEnvironment
    {
        return WithAgentReference(builder, source, name);
    }

    /// <summary>
    /// Adds a reference from the destination resource to an agent resource with endpoints.
    /// </summary>
    /// <typeparam name="TDestination">The type of the destination resource.</typeparam>
    /// <typeparam name="TSource">The type of the agent resource.</typeparam>
    /// <param name="builder">The destination resource builder.</param>
    /// <param name="source">The agent resource builder to reference.</param>
    /// <param name="name">An optional name used for the injected environment variables.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the standard <c>WithReference</c> overload instead.</remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the generic withReference dispatcher export from Aspire.Hosting.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination, TSource>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TSource> source,
        string? name = null)
        where TDestination : IResourceWithEnvironment
        where TSource : IResourceWithEndpoints
    {
        return WithAgentReference(builder, source, name);
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

    private static IResourceBuilder<TDestination> WithAgentReference<TDestination, TSource>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<TSource> source,
        string? name)
        where TDestination : IResourceWithEnvironment
        where TSource : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        var agentAnnotations = source.Resource.Annotations.OfType<AgentResourceAnnotation>()
            .Where(a => IsA2AProtocol(a.Protocol))
            .ToArray();

        if (agentAnnotations.Length == 0)
        {
            throw new InvalidOperationException($"The resource '{source.Resource.Name}' can't be used with withReference because it doesn't provide a connection string, service discovery, or a custom withReference implementation.");
        }

        var referenceName = name ?? source.Resource.Name;
        builder.WithReferenceRelationship(source.Resource);

        if (source.Resource is IResourceWithServiceDiscovery)
        {
            builder = AddServiceDiscoveryReference(builder, source.Resource, referenceName);
        }

        foreach (var agentAnnotation in agentAnnotations)
        {
            builder = builder.WithEnvironment(context =>
            {
                context.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injectionAnnotation);
                var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;
                if (!flags.HasFlag(ReferenceEnvironmentInjectionFlags.Endpoints))
                {
                    return;
                }

                var network = context.Resource.IsContainer()
                    ? KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                    : KnownNetworkIdentifiers.LocalhostNetwork;
                var endpoint = GetDefaultAgentEndpoint(source.Resource, network);
                var envVarName = GetAgentCardEnvironmentVariableName(referenceName);
                context.EnvironmentVariables[envVarName] = CreateA2AAgentCardUrl(endpoint, GetA2AAgentCardPath(agentAnnotation));
            });
        }

        return builder;
    }

    private static IResourceBuilder<TDestination> AddServiceDiscoveryReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceWithEndpoints source,
        string referenceName)
        where TDestination : IResourceWithEnvironment
    {
        return builder.WithEnvironment(context =>
        {
            context.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injectionAnnotation);
            var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;

            var network = context.Resource.IsContainer()
                ? KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                : KnownNetworkIdentifiers.LocalhostNetwork;
            var schemeIndexTracker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var endpoint in source.GetEndpoints(network))
            {
                if (endpoint.Exists && flags.HasFlag(ReferenceEnvironmentInjectionFlags.Endpoints))
                {
                    var encodedEndpointName = EnvironmentVariableNameEncoder.Encode(endpoint.EndpointName);
                    context.EnvironmentVariables[$"{EnvironmentVariableNameEncoder.Encode(referenceName).ToUpperInvariant()}_{encodedEndpointName.ToUpperInvariant()}"] = endpoint;
                }

                if (endpoint.Exists && flags.HasFlag(ReferenceEnvironmentInjectionFlags.ServiceDiscovery))
                {
                    var schemeKey = endpoint.IsHttpSchemeNamedEndpoint ? endpoint.Scheme : endpoint.EndpointName;
                    if (!schemeIndexTracker.TryGetValue(schemeKey, out var index))
                    {
                        index = 0;
                    }

                    var key = $"services__{referenceName}__{schemeKey}__{index}";
                    while (context.EnvironmentVariables.ContainsKey(key))
                    {
                        index++;
                        key = $"services__{referenceName}__{schemeKey}__{index}";
                    }

                    context.EnvironmentVariables[key] = endpoint;
                    schemeIndexTracker[schemeKey] = index + 1;
                }
            }
        });
    }

    private static EndpointReference GetDefaultAgentEndpoint(IResourceWithEndpoints source, NetworkIdentifier network)
    {
        var endpointName = source.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.UriScheme is "http" or "https")
            .OrderByDescending(e => string.Equals(e.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .FirstOrDefault() ?? "http";

        return new EndpointReference(source, endpointName, network);
    }

    private static void ConfigureA2A<T>(
        IResourceBuilder<T> builder,
        EndpointReference endpoint,
        string agentCardPath,
        Func<bool> shouldHighlightCommand)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        var advertisedEndpoint = GetAgentEndpoint(
            builder,
            builder.Resource.IsContainer()
                ? KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                : KnownNetworkIdentifiers.LocalhostNetwork);
        builder.WithEnvironment(A2AAgentBaseUrlEnvironmentVariableName, ReferenceExpression.Create($"{advertisedEndpoint.Property(EndpointProperty.Url)}"));

        AddProtocolEndpointUrl(builder, endpoint, agentCardPath, "Agent Card");

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
                EndpointSelector = () => endpoint,
                PrepareRequest = PrepareA2ARequestAsync,
                GetCommandResult = GetAgentCommandResultAsync
            });
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
                Arguments = [CreateMessageArgument("Hello, what can you do?")],
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
                Arguments = [CreateMessageArgument("What is the weather in Seattle?")],
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
                Arguments = [CreateMessageArgument("Hello, what can you do?")],
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

    private static async Task PrepareA2ARequestAsync(HttpCommandRequestContext ctx)
    {
        var cardUri = ctx.Request.RequestUri ?? throw new InvalidOperationException("Could not determine the A2A agent card URL.");
        var invocation = await ResolveA2AInvocationAsync(ctx, cardUri).ConfigureAwait(true);

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
                        role: isV03 ? "user" : "ROLE_USER",
                        partsPropertyName: "parts")
                }.ToString(),
                Encoding.UTF8,
                "application/json");
            return;
        }

        var isHttpJsonV03 = IsA2AProtocolVersionV03(invocation.ProtocolVersion);
        ctx.Request.Content = new StringContent(
            CreateA2ASendMessageRequest(
                message,
                role: isHttpJsonV03 ? "user" : "ROLE_USER",
                partsPropertyName: isHttpJsonV03 ? "content" : "parts").ToString(),
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

    private static async Task<A2AInvocation> ResolveA2AInvocationAsync(HttpCommandRequestContext ctx, Uri cardUri)
    {
        using var response = await ctx.HttpClient.GetAsync(cardUri, ctx.CancellationToken).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Could not read the A2A agent card at '{cardUri}'. The request failed with status code {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var card = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ctx.CancellationToken).ConfigureAwait(true)
            ?? throw new InvalidOperationException($"The A2A agent card at '{cardUri}' was empty.");

        var streaming = card["capabilities"]?["streaming"]?.GetValue<bool>() is true;
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

    private static Task PrepareResponsesRequestAsync(HttpCommandRequestContext ctx, string agentName)
    {
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

    private static Task PrepareAcpRunRequestAsync(HttpCommandRequestContext ctx, string agentName)
    {
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

    private static Task<ExecuteCommandResult> GetAgentCommandResultAsync(HttpCommandResultContext ctx)
    {
        return ctx.Response.Content.Headers.ContentType?.MediaType is "application/json" or "application/a2a+json"
            ? GetAgentCommandJsonResultAsync(ctx)
            : GetAgentCommandTextResultAsync(ctx);
    }

    private static JsonObject CreateA2ASendMessageRequest(string message, string role, string partsPropertyName)
    {
        return new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["messageId"] = Guid.NewGuid().ToString("N"),
                ["role"] = role,
                [partsPropertyName] = new JsonArray
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

    private static EndpointReference GetAgentEndpoint<T>(IResourceBuilder<T> builder, NetworkIdentifier network)
        where T : IResourceWithEndpoints
    {
        var endpointName = builder.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.UriScheme is "http" or "https")
            .OrderByDescending(e => string.Equals(e.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .FirstOrDefault() ?? "http";

        return new EndpointReference(builder.Resource, endpointName, network);
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
