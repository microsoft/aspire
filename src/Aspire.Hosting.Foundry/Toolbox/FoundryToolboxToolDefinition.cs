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
    internal FoundryToolboxWebSearchToolDefinition(string name)
        : base(name)
    {
    }

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
        // The OpenAI Responses "web_search" tool has a fixed wire shape: {"type":"web_search"}.
        // See https://platform.openai.com/docs/api-reference/responses/create#responses-create-tools.
        var json = BinaryData.FromString("""{"type":"web_search"}""");
        var agentTool = ModelReaderWriter.Read<ProjectsAgentTool>(json, ModelReaderWriterOptions.Json, AzureAIProjectsAgentsContext.Default);
        var canonicalConfiguration = JsonSerializer.Serialize(new
        {
            type = "web_search",
            name = Name
        });
        return new ValueTask<ResolvedFoundryToolboxTool>(
            new ResolvedFoundryToolboxTool(Name, agentTool!, canonicalConfiguration));
    }
}

/// <summary>
/// Describes an MCP tool in a Microsoft Foundry Toolbox.
/// </summary>
internal sealed class FoundryToolboxMcpToolDefinition : FoundryToolboxToolDefinition
{
    internal FoundryToolboxMcpToolDefinition(string name, ReferenceExpression endpointExpression)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(endpointExpression);

        EndpointExpression = endpointExpression;
    }

    /// <summary>
    /// Gets the MCP endpoint expression for the tool.
    /// </summary>
    public ReferenceExpression EndpointExpression { get; }

    internal override async ValueTask<ResolvedFoundryToolboxTool> ResolveAsync(CancellationToken cancellationToken)
    {
        var endpoint = await EndpointExpression.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException(
                $"MCP tool '{Name}' does not have a resolvable endpoint URI.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"MCP tool '{Name}' must resolve to an absolute HTTPS endpoint.");
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
            writer.WriteString("server_label", Name);
            writer.WriteString("server_url", endpointUri.AbsoluteUri);
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

/// <summary>
/// Describes an Azure AI Search tool in a Microsoft Foundry Toolbox.
/// </summary>
internal sealed class FoundryToolboxAzureAISearchToolDefinition : FoundryToolboxToolDefinition
{
    internal FoundryToolboxAzureAISearchToolDefinition(
        string name,
        AzureSearchResource searchResource,
        AzureCognitiveServicesProjectConnectionResource connection,
        string? indexName)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(searchResource);
        ArgumentNullException.ThrowIfNull(connection);

        SearchResource = searchResource;
        Connection = connection;
        IndexName = indexName;
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
    /// Gets the optional Azure AI Search index name.
    /// </summary>
    public string? IndexName { get; }

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
        var tool = new AzureAISearchTool(options);
        var canonicalConfiguration = JsonSerializer.Serialize(new
        {
            type = "azure_ai_search",
            name = Name,
            projectConnectionId = connectionId,
            indexName = IndexName
        });

        return new ResolvedFoundryToolboxTool(Name, tool, canonicalConfiguration);
    }
}
