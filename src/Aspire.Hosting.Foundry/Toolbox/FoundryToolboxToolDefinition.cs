// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.AI.Projects.Agents;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Base type for Microsoft Foundry Toolbox tool definitions.
/// </summary>
internal abstract class FoundryToolboxToolDefinition
{
    private protected FoundryToolboxToolDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
    }

    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Resolves this tool definition into the SDK shape (<see cref="ProjectsAgentTool"/>) used by the
    /// Foundry data plane when creating a new toolbox version.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async ValueTask<ProjectsAgentTool> ToProjectsAgentToolAsync(CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return resolved.Tool;
    }

    internal abstract ValueTask<ResolvedFoundryToolboxTool> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Describes a web search tool in a Microsoft Foundry Toolbox.
/// </summary>
internal sealed class FoundryToolboxWebSearchToolDefinition : FoundryToolboxToolDefinition
{
    internal FoundryToolboxWebSearchToolDefinition(string name, string? description = null)
        : base(name)
    {
        Description = description;
    }

    public string? Description { get; }

    internal override ValueTask<ResolvedFoundryToolboxTool> ResolveAsync(CancellationToken cancellationToken)
    {
        // Build the OpenAI Responses "web_search" tool wire JSON by hand and read it back as a
        // ProjectsAgentTool, bypassing ModelReaderWriter.Write on an OpenAI.Responses tool entirely.
        //
        // The natural implementation here is:
        //
        //   var openAiTool = OpenAI.Responses.ResponseTool.CreateWebSearchTool();
        //   var agentTool  = openAiTool.AsAgentTool(); // round-trips via ModelReaderWriter.Write
        //
        // That works in a normal .NET process where every assembly is loaded once. It does NOT
        // work in the polyglot (e.g. JavaScript/TypeScript) AppHostServer host process. That host
        // ships its own copy of OpenAI + System.ClientModel inside its application folder, and
        // loads hosting integrations into an isolated AssemblyLoadContext (see Aspire.Hosting.RemoteHost
        // IntegrationLoadContext). Today the host carries System.ClientModel 1.10.0 while this
        // integration is built against System.ClientModel 1.11.0; the load policy resolves the
        // newer SCM into the probe ALC but keeps OpenAI bound to the older SCM in the default ALC.
        // The two SCMs surface as distinct CLR assemblies, so the WebSearchTool instance (loaded
        // in the default ALC) implements IPersistableModel<WebSearchTool> against default-ALC SCM,
        // while ModelReaderWriter.Write<WebSearchTool> runs from probe-ALC SCM and checks
        // `model is IPersistableModel<T>` against probe-ALC SCM. The interface check returns false
        // and SCM throws the misleading "WebSearchTool must implement IEnumerable or IPersistableModel".
        //
        // Constructing the wire JSON ourselves keeps everything inside types that are shared across
        // ALCs (BCL + Azure.AI.Projects.Agents in the probe ALC), so the cross-ALC mismatch never
        // comes into play. The Read side is fine because AzureAIProjectsAgentsContext is resolved
        // from the same ALC as the SCM it talks to.
        //
        // Toolbox tools support an additional "name" field that is not modeled by the current
        // Azure.AI.Projects.Agents SDK but is preserved through its additional-properties bag:
        //   {"type":"web_search","name":"web-search"}
        // See https://learn.microsoft.com/azure/foundry/agents/how-to/tools/toolbox#multiple-tool-types.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "web_search");
            writer.WriteString("name", Name);
            if (Description is not null)
            {
                writer.WriteString("description", Description);
            }
            writer.WriteEndObject();
        }

        var json = BinaryData.FromBytes(stream.ToArray());
        var agentTool = ModelReaderWriter.Read<ProjectsAgentTool>(json, ModelReaderWriterOptions.Json, AzureAIProjectsAgentsContext.Default);
        return new ValueTask<ResolvedFoundryToolboxTool>(
            new ResolvedFoundryToolboxTool(Name, agentTool!, json.ToString()));
    }
}

/// <summary>
/// Describes an MCP tool in a Microsoft Foundry Toolbox.
/// </summary>
internal sealed class FoundryToolboxMcpToolDefinition : FoundryToolboxToolDefinition
{
    internal static bool IsFoundryReachableHttpsEndpoint(Uri endpointUri)
    {
        var host = endpointUri.Host.TrimEnd('.');

        return endpointUri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(endpointUri.UserInfo) &&
            !endpointUri.IsLoopback &&
            !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    internal FoundryToolboxMcpToolDefinition(
        string name,
        ReferenceExpression endpointExpression,
        FoundryToolboxMcpToolOptions? options = null)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(endpointExpression);

        EndpointExpression = endpointExpression;
        ServerLabel = options?.ServerLabel ?? name;
        ServerDescription = options?.ServerDescription;
        ApprovalPolicy = ResolvedFoundryToolboxMcpApprovalPolicy.Create(options?.ApprovalPolicy);

        ArgumentException.ThrowIfNullOrWhiteSpace(ServerLabel);
        if (ServerDescription is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ServerDescription);
        }
    }

    /// <summary>
    /// Gets the MCP endpoint expression for the tool.
    /// </summary>
    public ReferenceExpression EndpointExpression { get; }

    public string ServerLabel { get; }

    public string? ServerDescription { get; }

    internal ResolvedFoundryToolboxMcpApprovalPolicy? ApprovalPolicy { get; }

    internal override async ValueTask<ResolvedFoundryToolboxTool> ResolveAsync(CancellationToken cancellationToken)
    {
        var endpoint = await EndpointExpression.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException(
                $"MCP tool '{Name}' does not have a resolvable endpoint URI.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            !IsFoundryReachableHttpsEndpoint(endpointUri))
        {
            throw new InvalidOperationException(
                $"MCP tool '{Name}' must resolve to a Foundry-reachable absolute HTTPS endpoint.");
        }

        // Build the OpenAI Responses "mcp" tool wire JSON by hand and read it back as a
        // ProjectsAgentTool. See the comment on FoundryToolboxWebSearchToolDefinition for the
        // underlying cross-ALC System.ClientModel version mismatch that makes the natural
        // `ResponseTool.CreateMcpTool(...).AsAgentTool()` round-trip throw in the polyglot
        // (e.g. JavaScript/TypeScript) AppHostServer host process. Constructing the JSON
        // ourselves keeps everything inside types that are consistent across the integration's
        // ALC (BCL + Azure.AI.Projects.Agents + that ALC's copy of System.ClientModel).
        //
        // OpenAI Responses "mcp" tool wire shape:
        //   {
        //     "type": "mcp",
        //     "server_label": "<required>",
        //     "server_url":   "<absolute uri>" // required for hosted MCP
        //   }
        // See https://platform.openai.com/docs/api-reference/responses/create#responses-create-tools
        // and openai-dotnet's McpTool.Serialization.cs for the exact property names.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "mcp");
            writer.WriteString("server_label", ServerLabel);
            writer.WriteString("server_url", endpointUri.AbsoluteUri);
            if (ServerDescription is not null)
            {
                writer.WriteString("server_description", ServerDescription);
            }
            if (ApprovalPolicy is not null)
            {
                writer.WritePropertyName("require_approval");
                ApprovalPolicy.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        var json = BinaryData.FromBytes(stream.ToArray());
        var tool = ModelReaderWriter.Read<ProjectsAgentTool>(
            json,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default)!;

        return new ResolvedFoundryToolboxTool(Name, tool, json.ToString(), ServerLabel);
    }
}

internal sealed record ResolvedFoundryToolboxMcpApprovalPolicy(
    FoundryToolboxMcpGlobalApprovalMode? Global,
    ResolvedFoundryToolboxMcpApprovalFilter? Always,
    ResolvedFoundryToolboxMcpApprovalFilter? Never)
{
    public static ResolvedFoundryToolboxMcpApprovalPolicy? Create(
        FoundryToolboxMcpApprovalPolicy? policy)
    {
        if (policy is null)
        {
            return null;
        }

        var always = ResolvedFoundryToolboxMcpApprovalFilter.Create(
            policy.Always,
            nameof(policy.Always));
        var never = ResolvedFoundryToolboxMcpApprovalFilter.Create(
            policy.Never,
            nameof(policy.Never));

        if (policy.Global is not null && (always is not null || never is not null))
        {
            throw new ArgumentException(
                "A global MCP approval policy cannot be combined with custom filters.",
                nameof(policy));
        }

        if (policy.Global is null && always is null && never is null)
        {
            throw new ArgumentException(
                "An MCP approval policy must specify a global mode or at least one custom filter.",
                nameof(policy));
        }

        if (policy.Global is not null &&
            policy.Global is not FoundryToolboxMcpGlobalApprovalMode.Never &&
            policy.Global is not FoundryToolboxMcpGlobalApprovalMode.Always)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.Global,
                "The global MCP approval mode is not supported.");
        }

        var overlap = always?.ToolNames
            .Intersect(never?.ToolNames ?? [], StringComparer.Ordinal)
            .FirstOrDefault();
        if (overlap is not null)
        {
            throw new ArgumentException(
                $"MCP tool '{overlap}' cannot both always and never require approval.",
                nameof(policy));
        }

        if (always?.ReadOnly is { } alwaysReadOnly && never?.ReadOnly == alwaysReadOnly)
        {
            throw new ArgumentException(
                $"MCP tools with read_only set to '{alwaysReadOnly.ToString().ToLowerInvariant()}' cannot both always and never require approval.",
                nameof(policy));
        }

        return new(policy.Global, always, never);
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        if (Global is { } global)
        {
            writer.WriteStringValue(global switch
            {
                FoundryToolboxMcpGlobalApprovalMode.Never => "never",
                FoundryToolboxMcpGlobalApprovalMode.Always => "always",
                _ => throw new InvalidOperationException($"Unsupported MCP approval mode '{global}'.")
            });
            return;
        }

        writer.WriteStartObject();
        Always?.WriteTo(writer, "always");
        Never?.WriteTo(writer, "never");
        writer.WriteEndObject();
    }
}

internal sealed record ResolvedFoundryToolboxMcpApprovalFilter(
    IReadOnlyList<string> ToolNames,
    bool? ReadOnly)
{
    public static ResolvedFoundryToolboxMcpApprovalFilter? Create(
        FoundryToolboxMcpApprovalFilter? filter,
        string parameterName)
    {
        if (filter is null)
        {
            return null;
        }

        var toolNames = (filter.ToolNames ?? [])
            .Select(name =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
                return name;
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (toolNames.Length == 0 && filter.ReadOnly is null)
        {
            throw new ArgumentException(
                "An MCP approval filter must specify at least one tool name or a read-only value.",
                parameterName);
        }

        return new(toolNames, filter.ReadOnly);
    }

    public void WriteTo(Utf8JsonWriter writer, string propertyName)
    {
        writer.WriteStartObject(propertyName);
        if (ToolNames.Count > 0)
        {
            writer.WriteStartArray("tool_names");
            foreach (var toolName in ToolNames)
            {
                writer.WriteStringValue(toolName);
            }
            writer.WriteEndArray();
        }

        if (ReadOnly is { } readOnly)
        {
            writer.WriteBoolean("read_only", readOnly);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Describes an Azure AI Search tool in a Microsoft Foundry Toolbox.
/// </summary>
internal sealed class FoundryToolboxAzureAISearchToolDefinition : FoundryToolboxToolDefinition
{
    internal FoundryToolboxAzureAISearchToolDefinition(
        string name,
        AzureSearchResource searchResource,
        AzureCognitiveServicesProjectConnectionResource connection,
        string indexName,
        string? description)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(searchResource);
        ArgumentNullException.ThrowIfNull(connection);

        SearchResource = searchResource;
        Connection = connection;
        IndexName = indexName;
        Description = description;
    }

    /// <summary>
    /// Gets the Azure AI Search resource backing this tool.
    /// </summary>
    public AzureSearchResource SearchResource { get; }

    /// <summary>
    /// Gets the Foundry project connection resource used by the tool.
    /// </summary>
    public AzureCognitiveServicesProjectConnectionResource Connection { get; }

    /// <summary>
    /// Gets the Azure AI Search index name.
    /// </summary>
    public string IndexName { get; }

    public string? Description { get; }

    internal override async ValueTask<ResolvedFoundryToolboxTool> ResolveAsync(CancellationToken cancellationToken)
    {
        // The Foundry project connection's "id" bicep output is only populated after provisioning,
        // so this resolves to a real value only at deploy time. Matches AzureAISearchToolResource.
        var connectionIdRef = new BicepOutputReference("id", Connection);
        var connectionId = await connectionIdRef.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(connectionId))
        {
            throw new InvalidOperationException(
                $"Failed to resolve connection ID for Azure AI Search tool '{Name}'. " +
                "The Foundry project connection may not have been provisioned correctly.");
        }

        var index = new AzureAISearchToolIndex
        {
            ProjectConnectionId = connectionId,
            IndexName = IndexName
        };
        var options = new AzureAISearchToolOptions([index]);
        var unnamedTool = new AzureAISearchTool(options);
        var unnamedJson = ModelReaderWriter.Write(
            unnamedTool,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default);
        using var unnamedDocument = JsonDocument.Parse(unnamedJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in unnamedDocument.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteString("name", Name);
            if (Description is not null)
            {
                writer.WriteString("description", Description);
            }
            writer.WriteEndObject();
        }

        var json = BinaryData.FromBytes(stream.ToArray());
        var tool = ModelReaderWriter.Read<ProjectsAgentTool>(
            json,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default)!;

        return new ResolvedFoundryToolboxTool(Name, tool, json.ToString());
    }
}
