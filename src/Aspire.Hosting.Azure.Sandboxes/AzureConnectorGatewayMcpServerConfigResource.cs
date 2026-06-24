// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a managed MCP server config child resource in an Azure connector namespace.
/// </summary>
[AspireExport(ExposeProperties = true)]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayMcpServerConfigResource : Resource, IResourceWithParent<AzureConnectorGatewayResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureConnectorGatewayMcpServerConfigResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="configName">The Azure MCP server config name.</param>
    /// <param name="description">The description shown to MCP clients.</param>
    /// <param name="parent">The parent connector namespace resource.</param>
    public AzureConnectorGatewayMcpServerConfigResource(string name, string configName, string? description, AzureConnectorGatewayResource parent)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configName);

        ConfigName = configName;
        Description = description;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    /// <summary>
    /// Gets the Azure MCP server config name.
    /// </summary>
    public string ConfigName { get; }

    /// <summary>
    /// Gets the description shown to MCP clients.
    /// </summary>
    public string? Description { get; }

    /// <inheritdoc/>
    public AzureConnectorGatewayResource Parent { get; }

    internal List<AzureConnectorGatewayMcpConnectorDefinition> Connectors { get; } = [];
}
