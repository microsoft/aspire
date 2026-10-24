// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an resource for Azure Postgres Flexible Server.
/// </summary>
/// <param name="innerResource"><see cref="PostgresServerResource"/> that this resource wraps.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
[Obsolete($"This class is obsolete and will be removed in a future version. Use {nameof(AzurePostgresExtensions.AddAzurePostgresFlexibleServer)} instead to add an Azure Postgres Flexible Server resource.")]
public class AzurePostgresResource(PostgresServerResource innerResource, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(innerResource.Name, configureInfrastructure), IResourceWithConnectionString
{
    /// <inheritdoc/>
    public override string ServiceDisplayName => "Azure Database for PostgreSQL Server";

    private readonly PostgresServerResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <summary>
    /// Gets the "connectionString" secret output reference from the bicep template for the Azure Postgres Flexible Server.
    /// </summary>
    public BicepSecretOutputReference ConnectionString => new("connectionString", this);

    /// <summary>
    /// Gets the connection template for the manifest for the Azure Postgres Flexible Server.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{ConnectionString}");

    /// <inheritdoc/>
    public override string Name => _innerResource.Name;

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;
}
