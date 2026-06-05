// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Redis;
using CdkRedisResource = Azure.Provisioning.Redis.RedisResource;
using RedisResource = Aspire.Hosting.ApplicationModel.RedisResource;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Azure Redis resources to the application model.
/// </summary>
public static class AzureRedisExtensions
{
    /// <summary>
    /// Configures the resource to be published as Azure Cache for Redis when deployed via Azure Developer CLI.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{RedisResource}"/> builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{RedisResource}"/> builder.</returns>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AzureManagedRedisExtensions.AddAzureManagedRedis)} instead which provisions Azure Managed Redis.")]
    public static IResourceBuilder<RedisResource> PublishAsAzureRedis(this IResourceBuilder<RedisResource> builder)
        => PublishAsAzureRedisInternal(builder, useProvisioner: false);

    [Obsolete]
    private static IResourceBuilder<RedisResource> PublishAsAzureRedisInternal(this IResourceBuilder<RedisResource> builder, bool useProvisioner)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ApplicationBuilder.AddAzureProvisioning();

        var configureInfrastructure = (AzureResourceInfrastructure infrastructure) =>
        {
            var kvNameParam = new ProvisioningParameter(AzureBicepResource.KnownParameters.KeyVaultName, typeof(string));
            infrastructure.Add(kvNameParam);

            var keyVault = KeyVaultService.FromExisting("keyVault");
            keyVault.Name = kvNameParam;
            infrastructure.Add(keyVault);

            var redisCache = CreateRedisResource(infrastructure);

            var secret = new KeyVaultSecret("connectionString")
            {
                Parent = keyVault,
                Name = "connectionString",
                Properties = new SecretProperties
                {
                    Value = BicepFunction.Interpolate($"{redisCache.HostName},ssl=true,password={redisCache.GetKeys().PrimaryKey}")
                }
            };
            infrastructure.Add(secret);
        };

        var resource = new AzureRedisResource(builder.Resource, configureInfrastructure);
        var resourceBuilder = builder.ApplicationBuilder.CreateResourceBuilder(resource)
                                     .WithManifestPublishingCallback(resource.WriteToManifest);

        if (useProvisioner)
        {
            // Used to hold a reference to the azure surrogate for use with the provisioner.
            builder.WithAnnotation(new AzureBicepResourceAnnotation(resource));
            builder.WithConnectionStringRedirection(resource);

            // Remove the container annotation so that DCP doesn't do anything with it.
            if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().SingleOrDefault() is { } containerAnnotation)
            {
                builder.Resource.Annotations.Remove(containerAnnotation);
            }
        }

        return builder;
    }

    /// <summary>
    /// Configures resource to use Azure for local development and when doing a deployment via the Azure Developer CLI.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{RedisResource}"/> builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{RedisResource}"/> builder.</returns>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AzureManagedRedisExtensions.AddAzureManagedRedis)} instead which provisions Azure Managed Redis.")]
    public static IResourceBuilder<RedisResource> AsAzureRedis(this IResourceBuilder<RedisResource> builder)
        => PublishAsAzureRedisInternal(builder, useProvisioner: true);

    /// <summary>
    /// Adds an Azure Cache for Redis resource to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureRedisCacheResource}"/> builder.</returns>
    /// <remarks>
    /// By default, the Azure Cache for Redis resource is configured to use Microsoft Entra ID (Azure Active Directory) for authentication.
    /// This requires changes to the application code to use an azure credential to authenticate with the resource. See
    /// https://github.com/Azure/Microsoft.Azure.StackExchangeRedis for more information.
    ///
    /// You can use the <see cref="WithAccessKeyAuthentication(IResourceBuilder{AzureRedisCacheResource}, IResourceBuilder{IAzureKeyVaultResource})"/> method to configure the resource to use access key authentication.
    /// <example>
    /// The following example creates an Azure Cache for Redis resource and referencing that resource in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var cache = builder.AddAzureRedis("cache");
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(cache);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AzureManagedRedisExtensions.AddAzureManagedRedis)} instead which provisions Azure Managed Redis.")]
    public static IResourceBuilder<AzureRedisCacheResource> AddAzureRedis(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzureRedisCacheResource(name, ConfigureRedisInfrastructure);
        return builder.AddResource(resource)
            .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
    }

    /// <summary>
    /// Configures an Azure Cache for Redis resource to run locally in a container.
    /// </summary>
    /// <param name="builder">The Azure Cache for Redis resource builder.</param>
    /// <param name="configureContainer">Callback that exposes underlying container to allow for customization.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureRedisCacheResource}"/> builder.</returns>
    /// <remarks>
    /// <example>
    /// The following example creates an Azure Cache for Redis resource that runs locally in a
    /// Redis container and referencing that resource in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var cache = builder.AddAzureRedis("cache")
    ///     .RunAsContainer();
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(cache);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AzureManagedRedisExtensions.AddAzureManagedRedis)} instead which provisions Azure Managed Redis.")]
    public static IResourceBuilder<AzureRedisCacheResource> RunAsContainer(
        this IResourceBuilder<AzureRedisCacheResource> builder,
        Action<IResourceBuilder<RedisResource>>? configureContainer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var azureResource = builder.Resource;
        builder.ApplicationBuilder.Resources.Remove(azureResource);

        var redisContainer = builder.ApplicationBuilder.AddRedis(azureResource.Name);

        azureResource.SetInnerResource(redisContainer.Resource);

        configureContainer?.Invoke(redisContainer);

        return builder;
    }

    /// <summary>
    /// Configures the resource to use access key authentication for Azure Cache for Redis.
    /// </summary>
    /// <param name="builder">The Azure Cache for Redis resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureRedisCacheResource}"/> builder.</returns>
    /// <remarks>
    /// <example>
    /// The following example creates an Azure Cache for Redis resource that uses access key authentication.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var cache = builder.AddAzureRedis("cache")
    ///     .WithAccessKeyAuthentication();
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(cache);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AzureManagedRedisExtensions.AddAzureManagedRedis)} instead which provisions Azure Managed Redis.")]
    public static IResourceBuilder<AzureRedisCacheResource> WithAccessKeyAuthentication(this IResourceBuilder<AzureRedisCacheResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var kv = builder.ApplicationBuilder.AddAzureKeyVault($"{builder.Resource.Name}-kv")
                                           .WithParentRelationship(builder.Resource);

        // remove the KeyVault from the model if the emulator is used during run mode.
        // need to do this later in case builder becomes an emulator after this method is called.
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.ApplicationBuilder.OnBeforeStart((data, token) =>
            {
                if (builder.Resource.IsContainer())
                {
                    data.Model.Resources.Remove(kv.Resource);
                }
                return Task.CompletedTask;
            });
        }

        return builder.WithAccessKeyAuthentication(kv);
    }

    /// <summary>
    /// Configures the resource to use access key authentication for Azure Cache for Redis.
    /// </summary>
    /// <param name="builder">The Azure Cache for Redis resource builder.</param>
    /// <param name="keyVaultBuilder">The Azure Key Vault resource builder where the connection string used to connect to this AzureRedisCacheResource will be stored.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> builder.</returns>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AzureManagedRedisExtensions.AddAzureManagedRedis)} instead which provisions Azure Managed Redis.")]
    public static IResourceBuilder<AzureRedisCacheResource> WithAccessKeyAuthentication(this IResourceBuilder<AzureRedisCacheResource> builder, IResourceBuilder<IAzureKeyVaultResource> keyVaultBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keyVaultBuilder);

        var azureResource = builder.Resource;
        azureResource.ConnectionStringSecretOutput = keyVaultBuilder.Resource.GetSecret($"connectionstrings--{azureResource.Name}");
        // Set the secret owner to this resource
        azureResource.ConnectionStringSecretOutput.SecretOwner = azureResource;

        // remove role assignment annotations when using access key authentication so an empty roles bicep module isn't generated
        var roleAssignmentAnnotations = azureResource.Annotations.OfType<DefaultRoleAssignmentsAnnotation>().ToArray();
        foreach (var annotation in roleAssignmentAnnotations)
        {
            azureResource.Annotations.Remove(annotation);
        }

        return builder;
    }

    private static CdkRedisResource CreateRedisResource(AzureResourceInfrastructure infrastructure)
    {
        return AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
        (identifier, name) =>
        {
            var resource = CdkRedisResource.FromExisting(identifier);
            resource.Name = name;
            return resource;
        },
        (infrastructure) => new CdkRedisResource(infrastructure.AspireResource.GetBicepIdentifier())
        {
            Sku = new RedisSku()
            {
                Name = RedisSkuName.Basic,
                Family = RedisSkuFamily.BasicOrStandard,
                Capacity = 1
            },
            EnableNonSslPort = false,
            MinimumTlsVersion = RedisTlsVersion.Tls1_2,
            Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
        });
    }

    [Obsolete]
    private static void ConfigureRedisInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var redis = CreateRedisResource(infrastructure);

        var redisResource = (AzureRedisCacheResource)infrastructure.AspireResource;
        if (redisResource.UseAccessKeyAuthentication)
        {
            var kvNameParam = redisResource.ConnectionStringSecretOutput.Resource.NameOutputReference.AsProvisioningParameter(infrastructure);

            var keyVault = KeyVaultService.FromExisting("keyVault");
            keyVault.Name = kvNameParam;
            infrastructure.Add(keyVault);

            var secret = new KeyVaultSecret("connectionString")
            {
                Parent = keyVault,
                Name = $"connectionstrings--{redisResource.Name}",
                Properties = new SecretProperties
                {
                    Value = BicepFunction.Interpolate($"{redis.HostName},ssl=true,password={redis.GetKeys().PrimaryKey}")
                }
            };
            infrastructure.Add(secret);
        }
        else
        {
            if (!redis.IsExistingResource)
            {
                redis.RedisConfiguration = new RedisCommonConfiguration()
                {
                    IsAadEnabled = "true"
                };
                redis.IsAccessKeyAuthenticationDisabled = true;
            }

            infrastructure.Add(new ProvisioningOutput("connectionString", typeof(string))
            {
                Value = BicepFunction.Interpolate($"{redis.HostName},ssl=true")
            });
        }

        // We need to output name to externalize role assignments.
        infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = redis.Name.ToBicepExpression() });

        // Always output the hostName for the Redis server.
        infrastructure.Add(new ProvisioningOutput("hostName", typeof(string)) { Value = redis.HostName.ToBicepExpression() });
    }

    internal static void AddContributorPolicyAssignment(AzureResourceInfrastructure infra, CdkRedisResource redis, BicepValue<Guid> principalId, BicepValue<string> principalName)
    {
        infra.Add(new RedisCacheAccessPolicyAssignment($"{redis.BicepIdentifier}_contributor")
        {
            Name = BicepFunction.CreateGuid(redis.Id, principalId, "Data Contributor"),
            Parent = redis,
            AccessPolicyName = "Data Contributor",
            ObjectId = principalId,
            ObjectIdAlias = principalName
        });
    }
}
