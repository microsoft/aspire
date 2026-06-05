// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.RedisEnterprise;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Managed Redis resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
public class AzureManagedRedisResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithConnectionString
{
    /// <summary>
    /// Gets the "connectionString" output reference from the bicep template for the Azure Managed Redis resource.
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
    /// Gets the "primaryAccessKey" secret reference from the key vault associated with this resource.
    ///
    /// This is set when access key authentication is used. The primary access key is stored in a secret in the Azure Key Vault.
    /// </summary>
    internal IAzureKeyVaultSecretReference? PrimaryAccessKeySecretOutput { get; set; }

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
    [MemberNotNullWhen(true, nameof(PrimaryAccessKeySecretOutput))]
    public bool UseAccessKeyAuthentication => ConnectionStringSecretOutput is not null;

    /// <summary>
    /// Gets the inner Redis resource.
    /// 
    /// This is set when RunAsContainer is called on the AzureManagedRedisResource resource to create a local Redis container.
    /// </summary>
    internal RedisResource? InnerResource { get; private set; }

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource?.Annotations ?? base.Annotations;

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure Managed Redis resource.
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
            ReferenceExpression.Create($"{InnerResource.PrimaryEndpoint.Property(EndpointProperty.Host)}") :
            ReferenceExpression.Create($"{HostNameOutput}");

    /// <summary>
    /// Gets the port for the Redis server.
    /// </summary>
    /// <remarks>
    /// In container mode, resolves to the container's primary endpoint port.
    /// In Azure mode, resolves to 10000.
    /// </remarks>
    public ReferenceExpression Port =>
        InnerResource is not null ?
            ReferenceExpression.Create($"{InnerResource.Port}") :
            ReferenceExpression.Create($"10000"); // Based on the ConfigureRedisInfrastructure method

    /// <summary>
    /// Gets the password/access key for the Redis server.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>When running as a container, returns the password parameter value if one exists.</item>
    /// <item>When using access key authentication in Azure mode, returns the primary access key from KeyVault.</item>
    /// <item>When using Entra ID authentication in Azure mode, returns an empty expression.</item>
    /// </list>
    /// </remarks>
    public ReferenceExpression? Password =>
        InnerResource is not null ?
            (InnerResource.PasswordParameter is not null ?
                ReferenceExpression.Create($"{InnerResource.PasswordParameter}") :
                null) :
            UseAccessKeyAuthentication ?
                ReferenceExpression.Create($"{PrimaryAccessKeySecretOutput}") :
                null;

    /// <summary>
    /// Gets the connection URI expression for the Redis server.
    /// </summary>
    /// <remarks>
    /// Format: <c>redis://[:{password}@]{host}:{port}</c>. The password segment is omitted when using Entra ID authentication in Azure mode.
    /// </remarks>
    public ReferenceExpression UriExpression
    {
        get
        {
            if (InnerResource is not null)
            {
                return InnerResource.UriExpression;
            }

            if (UseAccessKeyAuthentication)
            {
                return ReferenceExpression.Create($"rediss://:{PrimaryAccessKeySecretOutput:uri}@{HostName}:{Port}");
            }

            return ReferenceExpression.Create($"rediss://{HostName}:{Port}");
        }
    }

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

        // Check if a RedisEnterpriseCluster with the same identifier already exists
        var existingCluster = resources.OfType<RedisEnterpriseCluster>().SingleOrDefault(cluster => cluster.BicepIdentifier == bicepIdentifier);

        if (existingCluster is not null)
        {
            return existingCluster;
        }

        // Create and add new resource if it doesn't exist
        var cluster = RedisEnterpriseCluster.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            cluster))
        {
            cluster.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(cluster);
        return cluster;
    }

    /// <inheritdoc/>
    public override void AddRoleAssignments(IAddRoleAssignmentsContext roleAssignmentContext)
    {
        Debug.Assert(!UseAccessKeyAuthentication, "AddRoleAssignments should not be called when using AccessKeyAuthentication");

        var infra = roleAssignmentContext.Infrastructure;
        var redisEnterprise = (RedisEnterpriseCluster)AddAsExistingResource(infra);

        var redisEnterpriseDatabase = infra.GetProvisionableResources()
            .OfType<RedisEnterpriseDatabase>()
            .SingleOrDefault(db => db.BicepIdentifier == redisEnterprise.BicepIdentifier + "_default");
        if (redisEnterpriseDatabase is null)
        {
            redisEnterpriseDatabase = RedisEnterpriseDatabase.FromExisting(redisEnterprise.BicepIdentifier + "_default");
            redisEnterpriseDatabase.Name = "default";
            redisEnterpriseDatabase.Parent = redisEnterprise;
            infra.Add(redisEnterpriseDatabase);
        }

        var principalId = roleAssignmentContext.PrincipalId;

        AddEnterpriseContributorPolicyAssignment(infra, redisEnterpriseDatabase, principalId);
    }

    private static void AddEnterpriseContributorPolicyAssignment(AzureResourceInfrastructure infra, RedisEnterpriseDatabase database, BicepValue<Guid> principalId)
    {
        // For Redis Enterprise, we need to use the appropriate access policy assignment
        // This may need to be adjusted based on the actual Azure.Provisioning.Redis types available
        infra.Add(new AccessPolicyAssignment($"{database.BicepIdentifier}_contributor")
        {
            Name = BicepFunction.CreateGuid(database.Id, principalId, "default"),
            Parent = database,
            AccessPolicyName = "default",
            UserObjectId = principalId
        });
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        if (InnerResource is not null)
        {
            foreach (var property in ((IResourceWithConnectionString)InnerResource).GetConnectionProperties())
            {
                yield return property;
            }
            yield break;
        }

        yield return new("Host", HostName);
        yield return new("Port", Port);
        yield return new("Uri", UriExpression);
        
        if (Password is not null)
        {
            yield return new("Password", Password);
        }
    }
}
