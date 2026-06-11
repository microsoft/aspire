// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.Primitives;
using CdkRedisResource = Azure.Provisioning.Redis.RedisResource;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Cache for Redis resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
[Obsolete($"This class is obsolete and will be removed in a future version. Use {nameof(AzureManagedRedisExtensions.AddAzureManagedRedis)} instead which provisions Azure Managed Redis.")]
public class AzureRedisCacheResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithEndpoints, IResourceWithConnectionString
{
    /// <summary>
    /// Gets the "connectionString" output reference from the bicep template for the Azure Redis resource.
    ///
    /// This is used when Entra ID authentication is used. The connection string is an output of the bicep template.
    /// </summary>
    private BicepOutputReference ConnectionStringOutput => new("connectionString", this);

    /// <summary>
    /// Gets the "connectionString" secret reference from the key vault associated with this resource.
    ///
    /// This is set when access key authentication is used. The connection string is stored in a secret in the Azure Key Vault.
    /// </summary>
    internal IAzureKeyVaultSecretReference? ConnectionStringSecretOutput { get; set; }

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the "hostName" output reference from the bicep template for the Azure Redis resource.
    /// </summary>
    private BicepOutputReference HostNameOutput => new("hostName", this);

    /// <summary>
    /// Gets a value indicating whether the resource uses access key authentication.
    /// </summary>
    [MemberNotNullWhen(true, nameof(ConnectionStringSecretOutput))]
    public bool UseAccessKeyAuthentication => ConnectionStringSecretOutput is not null;

    /// <summary>
    /// Gets the inner Redis resource.
    /// 
    /// This is set when RunAsContainer is called on the AzureRedisCacheResource resource to create a local Redis container.
    /// </summary>
    internal RedisResource? InnerResource { get; private set; }

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource?.Annotations ?? base.Annotations;

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure Cache for Redis resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        InnerResource?.ConnectionStringExpression ??
            (UseAccessKeyAuthentication ?
                ReferenceExpression.Create($"{ConnectionStringSecretOutput}") :
                ReferenceExpression.Create($"{ConnectionStringOutput}"));

    /// <summary>
    /// Gets the host name for the Redis server.
    /// </summary>
    /// <remarks>
    /// In container mode, resolves to the container's primary endpoint host and port.
    /// In Azure mode, resolves to the Azure Redis server's hostname.
    /// </remarks>
    public ReferenceExpression HostName => 
        InnerResource is not null ?
            ReferenceExpression.Create($"{InnerResource.PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}") :
            ReferenceExpression.Create($"{HostNameOutput}");

    /// <summary>
    /// Gets the password for the Redis server when running as a container.
    /// </summary>
    /// <remarks>
    /// This property returns null when running in Azure mode, as Redis access is handled via connection strings.
    /// When running as a container, it resolves to the password parameter value if one exists.
    /// </remarks>
    public ReferenceExpression? Password => 
        InnerResource is not null && InnerResource.PasswordParameter is not null ?
            ReferenceExpression.Create($"{InnerResource.PasswordParameter}") :
            null;

    internal void SetInnerResource(RedisResource innerResource)
    {
        // Copy the annotations to the inner resource before making it the inner resource
        foreach (var annotation in Annotations)
        {
            innerResource.Annotations.Add(annotation);
        }

        InnerResource = innerResource;
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();
        
        // Check if a RedisResource with the same identifier already exists
        var existingStore = resources.OfType<CdkRedisResource>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);
        
        if (existingStore is not null)
        {
            return existingStore;
        }
        
        // Create and add new resource if it doesn't exist
        var store = CdkRedisResource.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            store))
        {
            store.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(store);
        return store;
    }

    /// <inheritdoc/>
    public override void AddRoleAssignments(IAddRoleAssignmentsContext roleAssignmentContext)
    {
        Debug.Assert(!UseAccessKeyAuthentication, "AddRoleAssignments should not be called when using AccessKeyAuthentication");

        var infra = roleAssignmentContext.Infrastructure;
        var redis = (CdkRedisResource)AddAsExistingResource(infra);

        var principalId = roleAssignmentContext.PrincipalId;
        var principalName = roleAssignmentContext.PrincipalName;

        AzureRedisExtensions.AddContributorPolicyAssignment(infra, redis, principalId, principalName);
    }
}
