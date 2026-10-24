// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Redis resource.
/// </summary>
/// <param name="innerResource">The inner resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
[Obsolete($"This class is obsolete and will be removed in a future version. Use {nameof(AzureRedisExtensions.AddAzureRedis)} instead to add an Azure Cache for Redis resource.")]
public class AzureRedisResource(RedisResource innerResource, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(innerResource.Name, configureInfrastructure), IResourceWithConnectionString
{
    /// <inheritdoc/>
    public override string ServiceDisplayName => "Azure Cache for Redis";

    private readonly RedisResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <summary>
    /// Gets the "connectionString" output reference from the bicep template for the Azure Redis resource.
    /// </summary>
    public BicepSecretOutputReference ConnectionString => new("connectionString", this);

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure Redis resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{ConnectionString}");

    /// <inheritdoc/>
    public override string Name => _innerResource.Name;

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;
}
