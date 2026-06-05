// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.CosmosDB;
using Azure.Identity;
using Azure.Provisioning;
using Azure.Provisioning.CosmosDB;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Cosmos DB resources to the application model.
/// </summary>
public static class AzureCosmosExtensions
{
    /// <summary>
    /// Adds an Azure Cosmos DB connection to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureCosmosDBResource> AddAzureCosmosDB(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzureCosmosDBResource(name, ConfigureCosmosDBInfrastructure);
        return builder.AddResource(resource)
            .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
    }

    /// <summary>
    /// Configures an Azure Cosmos DB resource to be emulated using the Azure Cosmos DB emulator with the NoSQL API. This resource requires an <see cref="AzureCosmosDBResource"/> to be added to the application model.
    /// For more information on the Azure Cosmos DB emulator, see <a href="https://learn.microsoft.com/azure/cosmos-db/emulator#authentication"></a>.
    /// </summary>
    /// <param name="builder">The Azure Cosmos DB resource builder.</param>
    /// <param name="configureContainer">Callback that exposes underlying container used for emulation to allow for customization.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// When using the Azure Cosmos DB emulator, the container requires a TLS/SSL certificate.
    /// For more information, see <a href="https://learn.microsoft.com/azure/cosmos-db/how-to-develop-emulator?tabs=docker-linux#export-the-emulators-tlsssl-certificate"></a>.
    /// This version of the package defaults to the <inheritdoc cref="CosmosDBEmulatorContainerImageTags.Tag"/> tag of the <inheritdoc cref="CosmosDBEmulatorContainerImageTags.Registry"/>/<inheritdoc cref="CosmosDBEmulatorContainerImageTags.Image"/> container image.
    /// </remarks>
    public static IResourceBuilder<AzureCosmosDBResource> RunAsEmulator(this IResourceBuilder<AzureCosmosDBResource> builder, Action<IResourceBuilder<AzureCosmosDBEmulatorResource>>? configureContainer = null)
        => RunAsEmulator(builder, configureContainer, useVNextPreview: false);

    /// <summary>
    /// Configures an Azure Cosmos DB resource to be emulated using the Azure Cosmos DB Linux-based emulator (preview) with the NoSQL API. This resource requires an <see cref="AzureCosmosDBResource"/> to be added to the application model.
    /// For more information on the Azure Cosmos DB emulator, see <a href="https://learn.microsoft.com/azure/cosmos-db/emulator-linux"></a>.
    /// </summary>
    /// <param name="builder">The Azure Cosmos DB resource builder.</param>
    /// <param name="configureContainer">Callback that exposes underlying container used for emulation to allow for customization.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="CosmosDBEmulatorContainerImageTags.TagVNextPreview"/> tag of the <inheritdoc cref="CosmosDBEmulatorContainerImageTags.Registry"/>/<inheritdoc cref="CosmosDBEmulatorContainerImageTags.Image"/> container image.
    /// </remarks>
    [Experimental("ASPIRECOSMOSDB001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureCosmosDBResource> RunAsPreviewEmulator(this IResourceBuilder<AzureCosmosDBResource> builder, Action<IResourceBuilder<AzureCosmosDBEmulatorResource>>? configureContainer = null)
        => RunAsEmulator(builder, configureContainer, useVNextPreview: true);

    private static IResourceBuilder<AzureCosmosDBResource> RunAsEmulator(this IResourceBuilder<AzureCosmosDBResource> builder, Action<IResourceBuilder<AzureCosmosDBEmulatorResource>>? configureContainer, bool useVNextPreview)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Mark this resource as an emulator for consistent resource identification and tooling support
        builder.WithAnnotation(new EmulatorResourceAnnotation());

        var scheme = useVNextPreview ? "http" : null;
        builder.WithEndpoint(name: "emulator", scheme: scheme, targetPort: 8081)
               .WithAnnotation(new ContainerImageAnnotation
               {
                   Registry = CosmosDBEmulatorContainerImageTags.Registry,
                   Image = CosmosDBEmulatorContainerImageTags.Image,
                   Tag = useVNextPreview ? CosmosDBEmulatorContainerImageTags.TagVNextPreview : CosmosDBEmulatorContainerImageTags.Tag
               });

        CosmosClient? cosmosClient = null;
        builder.OnConnectionStringAvailable(async (cosmosDb, @event, ct) =>
        {
            var connectionString = await cosmosDb.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{builder.Resource.Name}' resource but the connection string was null.");
            }

            cosmosClient = CreateCosmosClient(connectionString);
        })
        .OnResourceReady(async (cosmosDb, @event, ct) =>
        {
            if (cosmosClient is null)
            {
                throw new InvalidOperationException("CosmosClient is not initialized.");
            }

            await cosmosClient.ReadAccountAsync().WaitAsync(ct).ConfigureAwait(false);

            foreach (var database in cosmosDb.Databases)
            {
                var db = (await cosmosClient.CreateDatabaseIfNotExistsAsync(database.DatabaseName, cancellationToken: ct).ConfigureAwait(false)).Database;

                foreach (var container in database.Containers)
                {
                    var containerProperties = container.ContainerProperties;

                    await db.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken: ct).ConfigureAwait(false);
                }
            }
        });

        var healthCheckKey = $"{builder.Resource.Name}_check";
        builder.ApplicationBuilder.Services.AddHealthChecks().AddAzureCosmosDB(
            sp => cosmosClient ?? throw new InvalidOperationException("CosmosClient is not initialized."),
            name: healthCheckKey
            );

        builder.WithHealthCheck(healthCheckKey);

        if (configureContainer != null)
        {
            var surrogate = new AzureCosmosDBEmulatorResource(builder.Resource);
            var surrogateBuilder = builder.ApplicationBuilder.CreateResourceBuilder(surrogate);
            configureContainer(surrogateBuilder);
        }

        return builder;

        static CosmosClient CreateCosmosClient(string connectionString)
        {
            var clientOptions = new CosmosClientOptions();
            clientOptions.CosmosClientTelemetryOptions.DisableDistributedTracing = true;

            if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
            {
                return new CosmosClient(uri.OriginalString, new DefaultAzureCredential(), clientOptions);
            }
            else
            {
                if (CosmosUtils.IsEmulatorConnectionString(connectionString))
                {
                    clientOptions.ConnectionMode = ConnectionMode.Gateway;
                    clientOptions.LimitToEndpoint = true;
                }

                return new CosmosClient(connectionString, clientOptions);
            }
        }
    }

    /// <summary>
    /// Adds a named volume for the data folder to an Azure Cosmos DB emulator resource.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureCosmosDBEmulatorResource"/>.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <returns>A builder for the <see cref="AzureCosmosDBEmulatorResource"/>.</returns>
    public static IResourceBuilder<AzureCosmosDBEmulatorResource> WithDataVolume(this IResourceBuilder<AzureCosmosDBEmulatorResource> builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var dataPath = builder.Resource.InnerResource.IsPreviewEmulator ? "/data" : "/tmp/cosmos/appdata";

        return builder.WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "true")
            .WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), dataPath, isReadOnly: false);
    }

    /// <summary>
    /// Configures the gateway port for the Azure Cosmos DB emulator.
    /// </summary>
    /// <param name="builder">Builder for the Cosmos emulator container</param>
    /// <param name="port">Host port to bind to the emulator gateway port.</param>
    /// <returns>Cosmos emulator resource builder.</returns>
    public static IResourceBuilder<AzureCosmosDBEmulatorResource> WithGatewayPort(this IResourceBuilder<AzureCosmosDBEmulatorResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("emulator", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Configures the partition count for the Azure Cosmos DB emulator.
    /// </summary>
    /// <param name="builder">Builder for the Cosmos emulator container</param>
    /// <param name="count">Desired partition count.</param>
    /// <returns>Cosmos emulator resource builder.</returns>
    /// <remarks>Not calling this method will result in the default of 10 partitions. The actual started partitions is always one more than specified.
    /// See <a href="https://learn.microsoft.com/azure/cosmos-db/emulator-windows-arguments#change-the-number-of-default-containers">this documentation</a> about setting the partition count.
    /// </remarks>
    public static IResourceBuilder<AzureCosmosDBEmulatorResource> WithPartitionCount(this IResourceBuilder<AzureCosmosDBEmulatorResource> builder, int count)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.InnerResource.IsPreviewEmulator)
        {
            throw new NotSupportedException($"'{nameof(WithPartitionCount)}' does not work when using the preview version of the Azure Cosmos DB emulator.");
        }

        if (count < 1 || count > 250)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be between 1 and 250.");
        }

        return builder.WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Adds a database to the associated Cosmos DB account resource.
    /// </summary>
    /// <param name="builder">AzureCosmosDB resource builder.</param>
    /// <param name="databaseName">Name of database.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use {nameof(AddCosmosDatabase)} instead to add a Cosmos DB database.")]
    public static IResourceBuilder<AzureCosmosDBResource> AddDatabase(this IResourceBuilder<AzureCosmosDBResource> builder, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        builder.AddCosmosDatabase(databaseName);

        return builder;
    }

    /// <summary>
    /// Adds a database to the associated Cosmos DB account resource.
    /// </summary>
    /// <param name="builder">AzureCosmosDB resource builder.</param>
    /// <param name="name">The name of the database resource.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureCosmosDBDatabaseResource> AddCosmosDatabase(this IResourceBuilder<AzureCosmosDBResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var database = new AzureCosmosDBDatabaseResource(name, databaseName, builder.Resource);
        builder.Resource.Databases.Add(database);

        return builder.ApplicationBuilder.AddResource(database);
    }

    /// <summary>
    /// Adds a container to the associated Cosmos DB database resource.
    /// </summary>
    /// <param name="builder">CosmosDBDatabase resource builder.</param>
    /// <param name="name">Name of container resource.</param>
    /// <param name="partitionKeyPath">Partition key path for the container.</param>
    /// <param name="containerName">The name of the container. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureCosmosDBContainerResource> AddContainer(this IResourceBuilder<AzureCosmosDBDatabaseResource> builder, [ResourceName] string name, string partitionKeyPath, string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(partitionKeyPath);

        // Use the resource name as the container name if it's not provided
        containerName ??= name;

        var container = new AzureCosmosDBContainerResource(name, containerName, partitionKeyPath, builder.Resource);
        builder.Resource.Containers.Add(container);

        return builder.ApplicationBuilder.AddResource(container);
    }

    /// <summary>
    /// Adds a container to the associated Cosmos DB database resource with hierarchical partition keys.
    /// </summary>
    /// <param name="builder">CosmosDBDatabase resource builder.</param>
    /// <param name="name">Name of container resource.</param>
    /// <param name="partitionKeyPaths">Hierarchical partition key paths for the container.</param>
    /// <param name="containerName">The name of the container. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureCosmosDBContainerResource> AddContainer(this IResourceBuilder<AzureCosmosDBDatabaseResource> builder, [ResourceName] string name, IEnumerable<string> partitionKeyPaths, string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(partitionKeyPaths);
        var partitionKeyPathsArray = partitionKeyPaths.ToArray();
        if (partitionKeyPathsArray.Length == 0)
        {
            throw new ArgumentException("At least one partition key path should be provided.", nameof(partitionKeyPaths));
        }

        if (partitionKeyPathsArray.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("Partition key paths cannot contain null or empty strings.", nameof(partitionKeyPaths));
        }

        // Use the resource name as the container name if it's not provided
        containerName ??= name;

        var container = new AzureCosmosDBContainerResource(name, containerName, partitionKeyPaths, builder.Resource);

        builder.Resource.Containers.Add(container);

        return builder.ApplicationBuilder.AddResource(container);
    }

    /// <summary>
    /// Configures the Azure Cosmos DB resource to be deployed use the default SKU provided by Azure.
    /// </summary>
    /// <param name="builder">The builder for the Azure Cosmos DB resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureCosmosDBResource> WithDefaultAzureSku(this IResourceBuilder<AzureCosmosDBResource> builder)
    {
        builder.Resource.UseDefaultAzureSku = true;
        return builder;
    }

    /// <summary>
    /// Configures the Azure Cosmos DB preview emulator to expose the Data Explorer endpoint.
    /// </summary>
    /// <param name="builder">Builder for the Cosmos emulator container</param>
    /// <param name="port">Optional host port to bind the Data Explorer to.</param>
    /// <returns>Cosmos emulator resource builder.</returns>
    /// <remarks>
    /// The Data Explorer is only available with <see cref="RunAsPreviewEmulator"/>.
    /// </remarks>
    [Experimental("ASPIRECOSMOSDB001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureCosmosDBEmulatorResource> WithDataExplorer(this IResourceBuilder<AzureCosmosDBEmulatorResource> builder, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Resource.InnerResource.IsPreviewEmulator)
        {
            throw new NotSupportedException($"The Data Explorer endpoint is only available when using the preview version of the Azure Cosmos DB emulator. Call '{nameof(RunAsPreviewEmulator)}' instead.");
        }

        return
            builder.WithEndpoint(endpointName: KnownUrls.DataExplorer.EndpointName, endpoint =>
            {
                endpoint.UriScheme = "http";
                endpoint.TargetPort = 1234;
                endpoint.Port = port;
            })
            .WithUrls(context =>
            {
                var url = context.Urls.FirstOrDefault(u => u.Endpoint?.EndpointName == KnownUrls.DataExplorer.EndpointName);
#pragma warning disable IDE0031 // Use null propagation (IDE0031)
                if (url is not null)
#pragma warning restore IDE0031
                {
                    url.DisplayText = KnownUrls.DataExplorer.DisplayText;
                }
            });
    }

    /// <summary>
    /// Configures the resource to use access key authentication with Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The Azure Cosmos DB resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> builder.</returns>
    /// <remarks>
    /// <example>
    /// The following example creates an Azure Cosmos DB resource that uses access key authentication.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var cosmosdb = builder.AddAzureCosmosDB("cache")
    ///     .WithAccessKeyAuthentication();
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(cosmosdb);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<AzureCosmosDBResource> WithAccessKeyAuthentication(this IResourceBuilder<AzureCosmosDBResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var kv = builder.ApplicationBuilder.AddAzureKeyVault($"{builder.Resource.Name}-kv")
                                           .WithParentRelationship(builder.Resource);

        // remove the KeyVault from the model if the emulator is used during run mode.
        // need to do this later in case builder becomes an emulator after this method is called.
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((data, _) =>
            {
                if (builder.Resource.IsEmulator)
                {
                    data.Model.Resources.Remove(kv.Resource);
                }
                return Task.CompletedTask;
            });
        }

        return builder.WithAccessKeyAuthentication(kv);
    }

    /// <summary>
    /// Configures the resource to use access key authentication with Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The Azure Cosmos DB resource builder.</param>
    /// <param name="keyVaultBuilder">The Azure Key Vault resource builder where the connection string used to connect to this AzureCosmosDBResource will be stored.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> builder.</returns>
    public static IResourceBuilder<AzureCosmosDBResource> WithAccessKeyAuthentication(this IResourceBuilder<AzureCosmosDBResource> builder, IResourceBuilder<IAzureKeyVaultResource> keyVaultBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);

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

    private static void ConfigureCosmosDBInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var azureResource = (AzureCosmosDBResource)infrastructure.AspireResource;
        bool disableLocalAuth = !azureResource.UseAccessKeyAuthentication;

        var cosmosAccount = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
            (identifier, name) =>
            {
                var resource = CosmosDBAccount.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infrastructure) => new CosmosDBAccount(infrastructure.AspireResource.GetBicepIdentifier())
            {
                Kind = CosmosDBAccountKind.GlobalDocumentDB,
                Capabilities = azureResource.UseDefaultAzureSku ? [] : new BicepList<CosmosDBAccountCapability>
                {
                    new CosmosDBAccountCapability { Name = CosmosConstants.EnableServerlessCapability }
                },
                ConsistencyPolicy = new ConsistencyPolicy()
                {
                    DefaultConsistencyLevel = DefaultConsistencyLevel.Session
                },
                DatabaseAccountOfferType = CosmosDBAccountOfferType.Standard,
                Locations =
                {
                    new CosmosDBAccountLocation
                    {
                        LocationName = new IdentifierExpression("location"),
                        FailoverPriority = 0
                    }
                },
                DisableLocalAuth = disableLocalAuth,
                Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
            });

        foreach (var database in azureResource.Databases)
        {
            var cosmosSqlDatabase = new CosmosDBSqlDatabase(Infrastructure.NormalizeBicepIdentifier(database.Name))
            {
                Parent = cosmosAccount,
                Name = database.DatabaseName,
                Resource = new CosmosDBSqlDatabaseResourceInfo()
                {
                    DatabaseName = database.DatabaseName
                }
            };
            infrastructure.Add(cosmosSqlDatabase);

            foreach (var container in database.Containers)
            {
                var cosmosContainer = new CosmosDBSqlContainer(Infrastructure.NormalizeBicepIdentifier(container.Name))
                {
                    Parent = cosmosSqlDatabase,
                    Name = container.ContainerName,
                    Resource = new CosmosDBSqlContainerResourceInfo()
                    {
                        ContainerName = container.ContainerName,
                        PartitionKey = new CosmosDBContainerPartitionKey
                        {
                            Paths = [.. container.PartitionKeyPaths],
                            Kind = container.PartitionKeyPaths.Count > 1 ? CosmosDBPartitionKind.MultiHash : CosmosDBPartitionKind.Hash,
                        }
                    }
                };

                if (container.ContainerProperties.PartitionKeyDefinitionVersion is { } version)
                {
                    cosmosContainer.Resource.PartitionKey.Version = (int)version;
                }

                infrastructure.Add(cosmosContainer);
            }
        }

        if (azureResource.UseAccessKeyAuthentication)
        {
            var kvNameParam = azureResource.ConnectionStringSecretOutput.Resource.NameOutputReference.AsProvisioningParameter(infrastructure);

            var keyVault = KeyVaultService.FromExisting("keyVault");
            keyVault.Name = kvNameParam;
            infrastructure.Add(keyVault);

            var secret = new KeyVaultSecret("connectionString")
            {
                Parent = keyVault,
                Name = $"connectionstrings--{azureResource.Name}",
                Properties = new SecretProperties
                {
                    Value = BicepFunction.Interpolate($"AccountEndpoint={cosmosAccount.DocumentEndpoint};AccountKey={cosmosAccount.GetKeys().PrimaryMasterKey}")
                }
            };
            infrastructure.Add(secret);

            var primaryAccessKey = new KeyVaultSecret("primaryAccessKey")
            {
                Parent = keyVault,
                Name = $"primaryaccesskey--{azureResource.Name}",
                Properties = new SecretProperties
                {
                    Value = BicepFunction.Interpolate($"{cosmosAccount.GetKeys().PrimaryMasterKey}")
                }
            };
            infrastructure.Add(primaryAccessKey);

            foreach (var database in azureResource.Databases)
            {
                var dbSecret = new KeyVaultSecret(Infrastructure.NormalizeBicepIdentifier(database.Name + "_connectionString"))
                {
                    Parent = keyVault,
                    Name = AzureCosmosDBResource.GetKeyValueSecretName(database.Name),
                    Properties = new SecretProperties
                    {
                        Value = BicepFunction.Interpolate($"AccountEndpoint={cosmosAccount.DocumentEndpoint};AccountKey={cosmosAccount.GetKeys().PrimaryMasterKey};Database={database.DatabaseName}")
                    }
                };
                infrastructure.Add(dbSecret);

                foreach (var container in database.Containers)
                {
                    var containerSecret = new KeyVaultSecret(Infrastructure.NormalizeBicepIdentifier(container.Name + "_connectionString"))
                    {
                        Parent = keyVault,
                        Name = AzureCosmosDBResource.GetKeyValueSecretName(container.Name),
                        Properties = new SecretProperties
                        {
                            Value = BicepFunction.Interpolate($"AccountEndpoint={cosmosAccount.DocumentEndpoint};AccountKey={cosmosAccount.GetKeys().PrimaryMasterKey};Database={database.DatabaseName};Container={container.ContainerName}")
                        }
                    };
                    infrastructure.Add(containerSecret);
                }
            }
        }
        else
        {
            // use managed identity

            infrastructure.Add(new ProvisioningOutput("connectionString", typeof(string))
            {
                Value = cosmosAccount.DocumentEndpoint.ToBicepExpression()
            });
        }

        // We need to output name to externalize role assignments.
        infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = cosmosAccount.Name.ToBicepExpression() });
    }

    internal static void AddContributorRoleAssignment(AzureResourceInfrastructure infra, CosmosDBAccount cosmosAccount, BicepValue<Guid> principalId)
    {
        var roleDefinition = CosmosDBSqlRoleDefinition_Derived.FromExisting(cosmosAccount.BicepIdentifier + "_roleDefinition");
        roleDefinition.Parent = cosmosAccount;
        roleDefinition.NameOverride = "00000000-0000-0000-0000-000000000002"; // data plane contributor role
        infra.Add(roleDefinition);

        infra.Add(new CosmosDBSqlRoleAssignment_Derived(cosmosAccount.BicepIdentifier + "_roleAssignment")
        {
            NameOverride = BicepFunction.CreateGuid(principalId, roleDefinition.Id, cosmosAccount.Id),
            Parent = cosmosAccount,
            Scope = cosmosAccount.Id,
            RoleDefinitionId = roleDefinition.Id,
            PrincipalId = principalId
        });
    }
}

// The following classes are working around https://github.com/Azure/azure-sdk-for-net/issues/47979 and can be removed once the issue is fixed.

internal class CosmosDBSqlRoleDefinition_Derived : CosmosDBSqlRoleDefinition
{
    private BicepValue<string>? _nameOverride;

    public CosmosDBSqlRoleDefinition_Derived(string name) : base(name)
    {
    }

    public static CosmosDBSqlRoleDefinition_Derived FromExisting(string bicepIdentifier)
    {
        return new CosmosDBSqlRoleDefinition_Derived(bicepIdentifier)
        {
            IsExistingResource = true
        };
    }

    public BicepValue<string> NameOverride
    {
        get
        {
            Initialize();
            return _nameOverride!;
        }
        set
        {
            Initialize();
            _nameOverride!.Assign(value);
        }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();

        _nameOverride = DefineProperty<string>("Name", new string[1] { "name" });
    }
}

internal class CosmosDBSqlRoleAssignment_Derived : CosmosDBSqlRoleAssignment
{
    private BicepValue<string>? _nameOverride;

    public CosmosDBSqlRoleAssignment_Derived(string name) : base(name)
    {
    }

    public BicepValue<string> NameOverride
    {
        get
        {
            Initialize();
            return _nameOverride!;
        }
        set
        {
            Initialize();
            _nameOverride!.Assign(value);
        }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();

        _nameOverride = DefineProperty<string>("Name", new string[1] { "name" });
    }
}
