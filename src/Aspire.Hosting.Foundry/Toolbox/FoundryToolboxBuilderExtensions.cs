// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    /// A new immutable toolbox version is created on the Foundry data plane at deploy time. The
    /// <see cref="FoundryToolboxResource.Version"/> property pins the version used by consumers in
    /// the MCP endpoint URI; the version produced by the most recent deploy is exposed via
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

        return builder.ApplicationBuilder.AddResource(toolbox)
            .WithIconName("Toolbox");
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
    /// Adds a web search tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryToolboxResource> WithWebSearchTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name = "web-search")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.Resource.AddTool(new FoundryToolboxWebSearchToolDefinition(name));

        return builder;
    }

    /// <summary>
    /// Adds an MCP tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="endpoint">The MCP endpoint URI.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union overload instead.")]
    public static IResourceBuilder<FoundryToolboxResource> WithMcpTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);

        return builder.WithMcpTool(name, ReferenceExpression.Create($"{endpoint}"));
    }

    /// <summary>
    /// Adds an MCP tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="endpoint">The MCP endpoint.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union overload instead.")]
    public static IResourceBuilder<FoundryToolboxResource> WithMcpTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        EndpointReference endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return builder.WithMcpTool(name, ReferenceExpression.Create($"{endpoint}"));
    }

    /// <summary>
    /// Adds an MCP tool definition to the Toolbox.
    /// </summary>
    [AspireExport("withMcpTool")]
    internal static IResourceBuilder<FoundryToolboxResource> WithMcpToolForPolyglot(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        [AspireUnion(typeof(string), typeof(EndpointReference))] object endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint switch
        {
            string endpointString => builder.WithMcpTool(name, endpointString),
            EndpointReference endpointReference => builder.WithMcpTool(name, endpointReference),
            _ => throw new ArgumentException("Endpoint must be a string or endpoint reference.", nameof(endpoint))
        };
    }

    /// <summary>
    /// Adds an Azure AI Search tool definition to the Toolbox.
    /// </summary>
    /// <param name="builder">The resource builder for the Toolbox.</param>
    /// <param name="name">The tool name.</param>
    /// <param name="search">The Azure AI Search resource backing the tool.</param>
    /// <param name="indexName">The optional search index name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryToolboxResource> WithAISearchTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        IResourceBuilder<AzureSearchResource> search,
        string? indexName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(search);

        var projectBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.Parent);
        var connection = projectBuilder.AddConnection(search);
        builder.Resource.AddTool(new FoundryToolboxAzureAISearchToolDefinition(
            name,
            search.Resource,
            connection.Resource,
            indexName));

        return builder;
    }

    private static IResourceBuilder<FoundryToolboxResource> WithMcpTool(
        this IResourceBuilder<FoundryToolboxResource> builder,
        string name,
        ReferenceExpression endpointExpression)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(endpointExpression);

        builder.Resource.AddTool(new FoundryToolboxMcpToolDefinition(name, endpointExpression));

        return builder;
    }
}
