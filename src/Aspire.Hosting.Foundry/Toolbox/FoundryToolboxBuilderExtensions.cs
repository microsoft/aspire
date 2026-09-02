// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Microsoft Foundry Toolbox resources and tools.
/// </summary>
public static class FoundryToolboxBuilderExtensions
{
    /// <summary>
    /// Adds a Microsoft Foundry Toolbox endpoint to a Microsoft Foundry project.
    /// </summary>
    /// <param name="builder">The resource builder for the Microsoft Foundry project.</param>
    /// <param name="name">The Toolbox name.</param>
    /// <param name="configure">Optional callback used to configure the Toolbox resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the Toolbox resource.</returns>
    /// <remarks>
    /// Aspire reuses the current default version when its configuration matches. Otherwise, it
    /// creates and promotes a new immutable version. The <see cref="FoundryToolboxResource.Version"/>
    /// property pins the version used by consumers in the MCP endpoint URI; the version selected
    /// by the most recent reconciliation is exposed via
    /// <see cref="FoundryToolboxResource.DeployedVersion"/>.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the FoundryToolboxOptions overload instead.")]
    public static IResourceBuilder<FoundryToolboxResource> AddToolbox(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        [ResourceName] string name,
        Action<FoundryToolboxResource>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var toolbox = new FoundryToolboxResource(name, builder.Resource);
        configure?.Invoke(toolbox);

        var roles = new HashSet<RoleDefinition>
        {
            new(FoundryResource.FoundryUserRoleDefinitionId, "Foundry User")
        };

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
        toolbox.Annotations.Add(new ReferenceRoleAssignmentAnnotation(builder.Resource.Parent, roles));
#pragma warning restore ASPIREAZURE003

        return builder.ApplicationBuilder.AddResource(toolbox)
            .WithIconName("Toolbox")
            .WithParentRelationship(builder);
    }

    /// <summary>
    /// Adds a Microsoft Foundry Toolbox endpoint to a Microsoft Foundry project.
    /// </summary>
    /// <param name="builder">The resource builder for the Microsoft Foundry project.</param>
    /// <param name="name">The Toolbox name.</param>
    /// <param name="options">Optional Toolbox settings.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the Toolbox resource.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("addToolbox")]
    internal static IResourceBuilder<FoundryToolboxResource> AddToolboxForPolyglot(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        [ResourceName] string name,
        FoundryToolboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return builder.AddToolbox(name, toolbox => toolbox.Version = options?.Version);
    }

    /// <summary>
    /// Uses an existing Microsoft Foundry Toolbox during local runs and reconciles it during deployment.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// Existing mode validates that the remote Toolbox and optional pinned
    /// <see cref="FoundryToolboxResource.Version"/> exist without resolving locally modeled tools or
    /// mutating the Toolbox.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryToolboxResource> RunAsExisting(
        this IResourceBuilder<FoundryToolboxResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            ConfigureAsExisting(builder);
        }

        return builder;
    }

    /// <summary>
    /// Reconciles a Microsoft Foundry Toolbox during local runs and uses an existing Toolbox during deployment.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// Existing mode validates that the remote Toolbox and optional pinned
    /// <see cref="FoundryToolboxResource.Version"/> exist without resolving locally modeled tools or
    /// mutating the Toolbox.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryToolboxResource> PublishAsExisting(
        this IResourceBuilder<FoundryToolboxResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            ConfigureAsExisting(builder);
        }

        return builder;
    }

    /// <summary>
    /// Uses an existing Microsoft Foundry Toolbox during local runs and deployment.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// Existing mode validates that the remote Toolbox and optional pinned
    /// <see cref="FoundryToolboxResource.Version"/> exist without resolving locally modeled tools or
    /// mutating the Toolbox.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryToolboxResource> AsExisting(
        this IResourceBuilder<FoundryToolboxResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ConfigureAsExisting(builder);

        return builder;
    }

    /// <summary>
    /// Adds a web search tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="description">An optional description of the web search tool.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryToolboxResource> WithWebSearchTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name = "web-search",
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (description is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
        }

        builder.Resource.AddTool(new FoundryToolboxWebSearchToolDefinition(name, description));

        return builder;
    }

    /// <summary>
    /// Sets the description persisted with each Toolbox version.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="description">The Toolbox description.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryToolboxResource> WithDescription(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string description)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        builder.Resource.Description = description;

        return builder;
    }

    /// <summary>
    /// Adds an MCP tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="endpoint">The MCP endpoint URI.</param>
    /// <param name="options">Optional MCP server metadata and approval policy.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union overload instead.")]
    public static IResourceBuilder<FoundryToolboxResource> WithMcpTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        string endpoint,
        FoundryToolboxMcpToolOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            !FoundryToolboxMcpToolDefinition.IsFoundryReachableHttpsEndpoint(endpointUri))
        {
            throw new ArgumentException(
                "The MCP endpoint must be a Foundry-reachable absolute HTTPS URI.",
                nameof(endpoint));
        }

        return builder.WithMcpTool(name, ReferenceExpression.Create($"{endpointUri.AbsoluteUri}"), options);
    }

    /// <summary>
    /// Adds an MCP tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="endpoint">The MCP endpoint.</param>
    /// <param name="options">Optional MCP server metadata and approval policy.</param>
    /// <remarks>
    /// During local development, the endpoint must resolve to a Foundry-reachable HTTPS URI, such
    /// as an anonymous development tunnel. A localhost endpoint cannot be reached by the Foundry
    /// data plane. Resource endpoints deployed with public HTTPS ingress can be referenced directly
    /// when using <c>aspire deploy</c>.
    /// </remarks>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union overload instead.")]
    public static IResourceBuilder<FoundryToolboxResource> WithMcpTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        EndpointReference endpoint,
        FoundryToolboxMcpToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return builder.WithMcpTool(name, ReferenceExpression.Create($"{endpoint}"), options);
    }

    /// <summary>
    /// Adds an MCP tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="endpoint">The MCP endpoint. A string URI, an <see cref="EndpointReference"/>
    /// pointing at a resource endpoint, or a <see cref="ReferenceExpression"/> for cases where the
    /// endpoint URL needs to be composed (for example, appending the MCP server's mount path or
    /// chaining through a public ingress like a dev tunnel).</param>
    /// <param name="options">Optional MCP server metadata and approval policy.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withMcpTool")]
    internal static IResourceBuilder<FoundryToolboxResource> WithMcpToolForPolyglot(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        [AspireUnion(typeof(string), typeof(EndpointReference), typeof(ReferenceExpression))] object endpoint,
        FoundryToolboxMcpToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint switch
        {
            string endpointString => builder.WithMcpTool(name, endpointString, options),
            EndpointReference endpointReference => builder.WithMcpTool(name, endpointReference, options),
            // ReferenceExpression lets polyglot callers compose URLs (e.g. `refExpr\`${endpoint}/mcp\``)
            // because the polyglot type system can't express a templated string built from a typed
            // endpoint reference any other way.
            ReferenceExpression endpointExpression => builder.WithMcpTool(name, endpointExpression, options),
            _ => throw new ArgumentException("Endpoint must be a string, endpoint reference, or reference expression.", nameof(endpoint))
        };
    }

    /// <summary>
    /// Adds an Azure AI Search tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="search">The Azure AI Search resource backing the tool.</param>
    /// <param name="indexName">The search index name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryToolboxResource> WithAISearchTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        IResourceBuilder<AzureSearchResource> search,
        string indexName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        if (builder.Resource.IsExisting)
        {
            return builder;
        }

        var projectBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.Parent);
        var connectionName = CreateSearchConnectionName(
            builder.Resource.Parent.Name,
            builder.Resource.Name,
            name,
            search.Resource.Name);
        var connection = projectBuilder.AddSearchConnection(connectionName, search.Resource);
        builder.Resource.AddTool(new FoundryToolboxAzureAISearchToolDefinition(
            name,
            search.Resource,
            connection.Resource,
            indexName));

        return builder;
    }

    private static string CreateSearchConnectionName(
        string projectName,
        string toolboxName,
        string toolName,
        string searchName)
    {
        var identity = Encoding.UTF8.GetBytes($"{projectName}\0{toolboxName}\0{toolName}\0{searchName}");
        var hash = XxHash3.Hash(identity);
        return $"toolbox-search-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void ConfigureAsExisting(IResourceBuilder<FoundryToolboxResource> builder)
    {
        foreach (var connection in builder.Resource.Tools
            .OfType<FoundryToolboxAzureAISearchToolDefinition>()
            .Select(tool => tool.Connection))
        {
            builder.ApplicationBuilder.Resources.Remove(connection);
        }

        builder.Resource.ClearTools();
        builder.WithAnnotation(new FoundryToolboxExistingResourceAnnotation());
    }

    private static IResourceBuilder<FoundryToolboxResource> WithMcpTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        ReferenceExpression endpointExpression,
        FoundryToolboxMcpToolOptions? options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(endpointExpression);

        builder.Resource.AddTool(new FoundryToolboxMcpToolDefinition(name, endpointExpression, options));

        return builder;
    }
}
