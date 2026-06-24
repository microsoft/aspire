// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a connector connection child resource in an Azure connector namespace.
/// </summary>
[AspireExport(ExposeProperties = true)]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayConnectionResource : Resource, IResourceWithParent<AzureConnectorGatewayResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureConnectorGatewayConnectionResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="connectionName">The Azure connector connection name.</param>
    /// <param name="connectorName">The connector catalog name.</param>
    /// <param name="displayName">The friendly display name shown for the connection.</param>
    /// <param name="parent">The parent connector namespace resource.</param>
    public AzureConnectorGatewayConnectionResource(string name, string connectionName, string connectorName, string? displayName, AzureConnectorGatewayResource parent)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        ConnectionName = connectionName;
        ConnectorName = connectorName;
        DisplayName = displayName;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    /// <summary>
    /// Gets the Azure connector connection name.
    /// </summary>
    public string ConnectionName { get; }

    /// <summary>
    /// Gets the connector catalog name.
    /// </summary>
    public string ConnectorName { get; }

    /// <summary>
    /// Gets the friendly display name shown for the connection.
    /// </summary>
    public string? DisplayName { get; }

    /// <inheritdoc/>
    public AzureConnectorGatewayResource Parent { get; }

    internal List<AzureConnectorGatewayConnectionAccessPolicyResource> AccessPolicies { get; } = [];
}
