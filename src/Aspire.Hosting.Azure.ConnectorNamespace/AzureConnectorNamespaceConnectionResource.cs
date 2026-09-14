// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.ConnectorNamespace.Provisioning;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a connection child resource in an Azure Connector Namespace.
/// </summary>
[AspireExport]
public sealed class AzureConnectorNamespaceConnectionResource :
    Resource,
    IResourceWithParent<AzureConnectorNamespaceResource>,
    IResourceWithCustomWithReference<AzureConnectorNamespaceConnectionResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureConnectorNamespaceConnectionResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="connectionName">The Azure connector connection name.</param>
    /// <param name="connectorName">The connector catalog name.</param>
    /// <param name="displayName">The friendly display name shown for the connection.</param>
    /// <param name="parent">The parent connector namespace resource.</param>
    public AzureConnectorNamespaceConnectionResource(string name, string connectionName, string connectorName, string? displayName, AzureConnectorNamespaceResource parent)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        ConnectionName = connectionName;
        ConnectorName = connectorName;
        DisplayName = displayName;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        BicepIdentifier = ConnectorNamespaceBicepIdentifiers.CreateConnection(parent.Name, name);
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
    public AzureConnectorNamespaceResource Parent { get; }

    static IResourceBuilder<TDestination>? IResourceWithCustomWithReference<AzureConnectorNamespaceConnectionResource>.TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
    {
        if (source is not IResourceBuilder<AzureConnectorNamespaceConnectionResource> connection)
        {
            return null;
        }

        if (optional)
        {
            throw new InvalidOperationException("Optional references are not supported for Connector Namespace connections.");
        }

        if (name is not null)
        {
            throw new InvalidOperationException("Named service references are not supported for Connector Namespace connections.");
        }

        return global::Aspire.Hosting.AzureConnectorNamespaceExtensions.WithReference(builder, connection, connectionName);
    }

    internal string BicepIdentifier { get; }

    internal List<AzureConnectorNamespaceConnectionAccessPolicyResource> AccessPolicies { get; } = [];

    internal bool IsExisting { get; set; }
}
