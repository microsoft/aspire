// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.RedisEnterprise;
using RedisResource = Aspire.Hosting.ApplicationModel.RedisResource;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Azure Managed Redis resources to the application model.
/// </summary>
public static class AzureManagedRedisExtensions
{
    /// <summary>
    /// Adds an Azure Managed Redis resource to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureManagedRedisResource}"/> builder.</returns>
    /// <remarks>
    /// By default, the Azure Managed Redis resource is configured to use Microsoft Entra ID (Azure Active Directory) for authentication.
    /// This requires changes to the application code to use an azure credential to authenticate with the resource. See
    /// https://github.com/Azure/Microsoft.Azure.StackExchangeRedis for more information.
    ///
    /// <example>
    /// The following example creates an Azure Managed Redis resource and referencing that resource in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var cache = builder.AddAzureManagedRedis("cache");
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(cache);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<AzureManagedRedisResource> AddAzureManagedRedis(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzureManagedRedisResource(name, ConfigureRedisInfrastructure);
        return builder.AddResource(resource)
            .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
    }

    /// <summary>
    /// Configures an Azure Managed Redis resource to run locally in a container.
    /// </summary>
    /// <param name="builder">The Azure Managed Redis resource builder.</param>
    /// <param name="configureContainer">Callback that exposes underlying container to allow for customization.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureRedisCacheResource}"/> builder.</returns>
    /// <remarks>
    /// <example>
    /// The following example creates an Azure Managed Redis resource that runs locally in a
    /// Redis container and referencing that resource in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var cache = builder.AddAzureManagedRedis("cache")
    ///     .RunAsContainer();
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(cache);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<AzureManagedRedisResource> RunAsContainer(
        this IResourceBuilder<AzureManagedRedisResource> builder,
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
    /// Configures the resource to use access key authentication for Azure Managed Redis.
    /// </summary>
    /// <param name="builder">The Azure Managed Redis resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureManagedRedisResource}"/> builder.</returns>
    /// <remarks>
    /// <example>
    /// The following example creates an Azure Managed Redis resource that uses access key authentication.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var cache = builder.AddAzureManagedRedis("cache")
    ///     .WithAccessKeyAuthentication();
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(cache);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<AzureManagedRedisResource> WithAccessKeyAuthentication(this IResourceBuilder<AzureManagedRedisResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var kv = builder.ApplicationBuilder.AddAzureKeyVault($"{builder.Resource.Name}-kv")
                                           .WithParentRelationship(builder.Resource);

        // Remove the KeyVault from the model if the emulator is used during run mode.
        // need to do this later in case builder becomes an emulator after this method is called.
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((data, token) =>
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
    /// Configures the resource to use access key authentication for Azure Managed Redis.
    /// </summary>
    /// <param name="builder">The Azure Managed Redis resource builder.</param>
    /// <param name="keyVaultBuilder">The Azure Key Vault resource builder where the connection string used to connect to this AzureManagedRedisResource will be stored.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureManagedRedisResource}"/> builder.</returns>
    public static IResourceBuilder<AzureManagedRedisResource> WithAccessKeyAuthentication(this IResourceBuilder<AzureManagedRedisResource> builder, IResourceBuilder<IAzureKeyVaultResource> keyVaultBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keyVaultBuilder);

        var azureResource = builder.Resource;
        azureResource.ConnectionStringSecretOutput = keyVaultBuilder.Resource.GetSecret($"connectionstrings--{azureResource.Name}");
        azureResource.PrimaryAccessKeySecretOutput = keyVaultBuilder.Resource.GetSecret($"primaryaccesskey--{azureResource.Name}");
        // Set the secret owner to this resource
        azureResource.ConnectionStringSecretOutput.SecretOwner = azureResource;
        azureResource.PrimaryAccessKeySecretOutput.SecretOwner = azureResource;

        // remove role assignment annotations when using access key authentication so an empty roles bicep module isn't generated
        var roleAssignmentAnnotations = azureResource.Annotations.OfType<DefaultRoleAssignmentsAnnotation>().ToArray();
        foreach (var annotation in roleAssignmentAnnotations)
        {
            azureResource.Annotations.Remove(annotation);
        }

        return builder;
    }

    private static void ConfigureRedisInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var redisResource = (AzureManagedRedisResource)infrastructure.AspireResource;

        var redis = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
            (identifier, name) =>
            {
                var resource = RedisEnterpriseCluster.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infra) =>
            {
                var cluster = new RedisEnterpriseCluster(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Sku = new RedisEnterpriseSku
                    {
                        Name = RedisEnterpriseSkuName.BalancedB0
                    },
                    MinimumTlsVersion = RedisEnterpriseTlsVersion.Tls1_2
                };
                infra.Add(cluster);

                infra.Add(new RedisEnterpriseDatabase(cluster.BicepIdentifier + "_default")
                {
                    Name = "default",
                    Parent = cluster,
                    Port = 10000,
                    AccessKeysAuthentication = redisResource.UseAccessKeyAuthentication ?
                        AccessKeysAuthentication.Enabled :
                        AccessKeysAuthentication.Disabled
                });

                return cluster;
            });

        if (redisResource.UseAccessKeyAuthentication)
        {
            var kvNameParam = redisResource.ConnectionStringSecretOutput.Resource.NameOutputReference.AsProvisioningParameter(infrastructure);

            var keyVault = KeyVaultService.FromExisting("keyVault");
            keyVault.Name = kvNameParam;
            infrastructure.Add(keyVault);

            var database = infrastructure.GetProvisionableResources()
                .OfType<RedisEnterpriseDatabase>()
                .SingleOrDefault(db => db.BicepIdentifier == redis.BicepIdentifier + "_default");
            if (database is null)
            {
                // existing resource scenario
                database = new RedisEnterpriseDatabase(redis.BicepIdentifier + "_default")
                {
                    Name = "default",
                    Parent = redis,
                    Port = 10000,
                };
                infrastructure.Add(database);
            }

            var secret = new KeyVaultSecret("connectionString")
            {
                Parent = keyVault,
                Name = $"connectionstrings--{redisResource.Name}",
                Properties = new SecretProperties
                {
                    Value = BicepFunction.Interpolate($"{redis.HostName}:10000,ssl=true,password={database.GetKeys().PrimaryKey}")
                }
            };
            infrastructure.Add(secret);

            var accessKeySecret = new KeyVaultSecret("primaryAccessKey")
            {
                Parent = keyVault,
                Name = $"primaryaccesskey--{redisResource.Name}",
                Properties = new SecretProperties
                {
                    Value = database.GetKeys().PrimaryKey
                }
            };
            infrastructure.Add(accessKeySecret);
        }
        else
        {
            infrastructure.Add(new ProvisioningOutput("connectionString", typeof(string))
            {
                Value = BicepFunction.Interpolate($"{redis.HostName}:10000,ssl=true")
            });
        }

        // We need to output name to externalize role assignments.
        infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = redis.Name.ToBicepExpression() });

        // Always output the hostName for the Redis server.
        infrastructure.Add(new ProvisioningOutput("hostName", typeof(string)) { Value = redis.HostName.ToBicepExpression() });
    }
}
