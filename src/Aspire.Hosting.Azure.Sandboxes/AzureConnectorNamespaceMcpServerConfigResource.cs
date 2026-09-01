// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a managed MCP server configuration child resource in an Azure Connector Namespace.
/// </summary>
[AspireExport]
public sealed class AzureConnectorNamespaceMcpServerConfigResource : Resource, IResourceWithParent<AzureConnectorNamespaceResource>, IResourceWithoutLifetime
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureConnectorNamespaceMcpServerConfigResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="configName">The Azure MCP server config name.</param>
    /// <param name="description">The description shown to MCP clients.</param>
    /// <param name="parent">The parent connector namespace resource.</param>
    public AzureConnectorNamespaceMcpServerConfigResource(string name, string configName, string? description, AzureConnectorNamespaceResource parent)
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
    public AzureConnectorNamespaceResource Parent { get; }

    internal List<AzureConnectorNamespaceMcpConnectorDefinition> Connectors { get; } = [];

    internal bool IsExisting { get; set; }
}
