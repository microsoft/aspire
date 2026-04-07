// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Storage;
using Azure.Provisioning;
using Azure.Provisioning.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Storage resources to an application model.
/// </summary>
public static class AzureStorageExtensions
{
    private const string SkipApiVersionCheckArgument = "--skipApiVersionCheck";

    /// <summary>
    /// Adds an Azure Storage resource to the application model. This resource can be used to create Azure blob, table, and queue resources.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns></returns>
    /// <remarks>
    /// By default references to the Azure Storage resource will be assigned the following roles:
    /// 
    /// - <see cref="StorageBuiltInRole.StorageBlobDataContributor"/>
    /// - <see cref="StorageBuiltInRole.StorageTableDataContributor"/>
    /// - <see cref="StorageBuiltInRole.StorageQueueDataContributor"/>
    ///
    /// These can be replaced by calling <see cref="WithRoleAssignments{T}(IResourceBuilder{T}, IResourceBuilder{AzureStorageResource}, StorageBuiltInRole[])"/>.
    /// </remarks>
    [AspireExport(Description = "Adds an Azure Storage resource")]
    public static IResourceBuilder<AzureStorageResource> AddAzureStorage(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var configureInfrastructure = (AzureResourceInfrastructure infrastructure) =>
        {
            var azureResource = (AzureStorageResource)infrastructure.AspireResource;

            var storageAccount = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = StorageAccount.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) =>
                {
                    // Check if this storage has a private endpoint (via annotation)
                    var hasPrivateEndpoint = azureResource.HasAnnotationOfType<PrivateEndpointTargetAnnotation>();

                    var storageAccount = new StorageAccount(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        Kind = StorageKind.StorageV2,
                        AccessTier = StorageAccountAccessTier.Hot,
                        Sku = new StorageSku() { Name = StorageSkuName.StandardGrs },
                        IsHnsEnabled = azureResource.IsHnsEnabled,
                        NetworkRuleSet = new StorageAccountNetworkRuleSet()
                        {
                            // When using private endpoints, deny public access.
                            // Otherwise, we need to allow it since Azure Storage does not list ACA 
                            // as one of the resource types in which the AzureServices firewall policy works.
                            DefaultAction = hasPrivateEndpoint
                                ? StorageNetworkDefaultAction.Deny
                                : StorageNetworkDefaultAction.Allow
                        },
                        // Set the minimum TLS version to 1.2 to ensure resources provisioned are compliant
                        // with the pending deprecation of TLS 1.0 and 1.1.
                        MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2,
                        // Disable shared key access to the storage account as managed identity is configured
                        // to access the storage account by default.
                        AllowSharedKeyAccess = false,
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };

                    // When using private endpoints, completely disable public network access.
                    if (hasPrivateEndpoint)
                    {
                        storageAccount.PublicNetworkAccess = StoragePublicNetworkAccess.Disabled;
                    }

                    return storageAccount;
                });

            if (azureResource.BlobContainers.Count > 0 || azureResource.DataLakeFileSystems.Count > 0)
            {
                // The provisioned resource uses "blobs" as the name for backward compatibility.
                var blobService = new BlobService("blobs")
                {
                    Parent = storageAccount
                };

                infrastructure.Add(blobService);

                foreach (var blobContainer in azureResource.BlobContainers)
                {
                    var cdkBlobContainer = blobContainer.ToProvisioningEntity();
                    cdkBlobContainer.Parent = blobService;
                    infrastructure.Add(cdkBlobContainer);
                }

                foreach (var dataLakeFileSystem in azureResource.DataLakeFileSystems)
                {
                    var cdkBlobContainer = dataLakeFileSystem.ToProvisioningEntity();
                    cdkBlobContainer.Parent = blobService;
                    infrastructure.Add(cdkBlobContainer);
                }
            }

            if (azureResource.Queues.Count > 0)
            {
                var queueService = new QueueService("queues")
                {
                    Parent = storageAccount
                };

                infrastructure.Add(queueService);

                foreach (var queue in azureResource.Queues)
                {
                    var cdkQueue = queue.ToProvisioningEntity();
                    cdkQueue.Parent = queueService;
                    infrastructure.Add(cdkQueue);
                }
            }

            // TODO: When Tables are added, change this check to use Count > 0
            if (azureResource.TableStorageBuilder is not null)
            {
                var tableService = new TableService("tables")
                {
                    Parent = storageAccount
                };

                infrastructure.Add(tableService);
            }

            infrastructure.Add(new ProvisioningOutput("blobEndpoint", typeof(string)) { Value = storageAccount.PrimaryEndpoints.BlobUri.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("dataLakeEndpoint", typeof(string)) { Value = storageAccount.PrimaryEndpoints.DfsUri.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("queueEndpoint", typeof(string)) { Value = storageAccount.PrimaryEndpoints.QueueUri.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("tableEndpoint", typeof(string)) { Value = storageAccount.PrimaryEndpoints.TableUri.ToBicepExpression() });

            // We need to output name to externalize role assignments.
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = storageAccount.Name.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = storageAccount.Id });
        };

        var resource = new AzureStorageResource(name, configureInfrastructure);

        return builder.AddResource(resource)
            .WithDefaultRoleAssignments(StorageBuiltInRole.GetBuiltInRoleName,
                StorageBuiltInRole.StorageBlobDataContributor,
                StorageBuiltInRole.StorageTableDataContributor,
                StorageBuiltInRole.StorageQueueDataContributor);
    }

    /// <summary>
    /// Configures an Azure Storage resource to be emulated using Azurite. This resource requires an <see cref="AzureStorageResource"/> to be added to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="StorageEmulatorContainerImageTags.Tag"/> tag of the <inheritdoc cref="StorageEmulatorContainerImageTags.Registry"/>/<inheritdoc cref="StorageEmulatorContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The Azure storage resource builder.</param>
    /// <param name="configureContainer">Callback that exposes underlying container used for emulation to allow for customization.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures the Azure Storage resource to be emulated using Azurite", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<AzureStorageResource> RunAsEmulator(this IResourceBuilder<AzureStorageResource> builder, Action<IResourceBuilder<AzureStorageEmulatorResource>>? configureContainer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Resource.IsHnsEnabled)
        {
            throw new InvalidOperationException("Emulator currently does not support data lake.");
        }

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Mark this resource as an emulator for consistent resource identification and tooling support
        builder.WithAnnotation(new EmulatorResourceAnnotation());

        builder.WithHttpEndpoint(name: "blob", targetPort: 10000)
               .WithHttpEndpoint(name: "queue", targetPort: 10001)
               .WithHttpEndpoint(name: "table", targetPort: 10002)
               .WithAnnotation(new ContainerImageAnnotation
               {
                   Registry = StorageEmulatorContainerImageTags.Registry,
                   Image = StorageEmulatorContainerImageTags.Image,
                   Tag = StorageEmulatorContainerImageTags.Tag
               });

        BlobServiceClient? blobServiceClient = null;
        QueueServiceClient? queueServiceClient = null;

        builder
            .OnBeforeResourceStarted(async (storage, @event, ct) =>
            {
                // The BlobServiceClient and QueueServiceClient are created before the health check is run.
                // We can't use ConnectionStringAvailableEvent here because the resource doesn't have a connection string, so
                // we use BeforeResourceStartedEvent

                var blobConnectionString = await builder.Resource.GetBlobConnectionString().GetValueAsync(ct).ConfigureAwait(false) ?? throw new DistributedApplicationException($"{nameof(ConnectionStringAvailableEvent)} was published for the '{builder.Resource.Name}' resource but the connection string was null.");
                blobServiceClient = CreateBlobServiceClient(blobConnectionString);

                var queueConnectionString = await builder.Resource.GetQueueConnectionString().GetValueAsync(ct).ConfigureAwait(false) ?? throw new DistributedApplicationException($"{nameof(ConnectionStringAvailableEvent)} was published for the '{builder.Resource.Name}' resource but the connection string was null.");
                queueServiceClient = CreateQueueServiceClient(queueConnectionString);
            })
            .OnResourceReady(async (storage, @event, ct) =>
            {
                // The ResourceReadyEvent of a resource is triggered after its health check (AddAzureBlobStorage) is healthy.
                // This means we can safely use this event to create the blob containers.

                _ = blobServiceClient ?? throw new InvalidOperationException($"{nameof(BlobServiceClient)} is not initialized.");
                _ = queueServiceClient ?? throw new InvalidOperationException($"{nameof(QueueServiceClient)} is not initialized.");

                foreach (var container in builder.Resource.BlobContainers)
                {
                    var blobContainerClient = blobServiceClient.GetBlobContainerClient(container.BlobContainerName);
                    await blobContainerClient.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
                }

                foreach (var queue in builder.Resource.Queues)
                {
                    var queueClient = queueServiceClient.GetQueueClient(queue.QueueName);
                    await queueClient.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
                }
            });

        // Add the "Storage" resource health check. There will be separate health checks for the nested child resources.
        var healthCheckKey = $"{builder.Resource.Name}_check";

        builder.ApplicationBuilder.Services.AddHealthChecks().AddAzureBlobStorage(sp =>
        {
            return blobServiceClient ?? throw new InvalidOperationException("BlobServiceClient is not initialized.");
        }, name: healthCheckKey);

        builder.WithHealthCheck(healthCheckKey);

        // The default arguments list is coming from https://github.com/Azure/Azurite/blob/c3f93445fbd8fd54d380eb265a5665166c460d2b/Dockerfile#L47C6-L47C106
        // They need to be repeated in order to be able to add --skipApiVersionCheck
        // --disableProductStyleUrl is required to ensure the emulator uses path-style URLs, and not “product-style” URLs which have the account name in the host name of the URL.

        var surrogate = new AzureStorageEmulatorResource(builder.Resource);
        var surrogateBuilder = builder.ApplicationBuilder
            .CreateResourceBuilder(surrogate)
            .WithArgs("azurite", "-l", "/data", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--disableProductStyleUrl", SkipApiVersionCheckArgument);

        configureContainer?.Invoke(surrogateBuilder);

        return builder;
    }

    /// <summary>
    /// Adds a bind mount for the data folder to an Azure Storage emulator resource.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureStorageEmulatorResource"/>.</param>
    /// <param name="path">Relative path to the AppHost where emulator storage is persisted between runs. Defaults to the path '.azurite/{builder.Resource.Name}'</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>A builder for the <see cref="AzureStorageEmulatorResource"/>.</returns>
    [AspireExport(Description = "Adds a bind mount for the data folder to an Azure Storage emulator resource")]
    public static IResourceBuilder<AzureStorageEmulatorResource> WithDataBindMount(this IResourceBuilder<AzureStorageEmulatorResource> builder, string? path = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithBindMount(path ?? $".azurite/{builder.Resource.Name}", "/data", isReadOnly);
    }

    /// <summary>
    /// Adds a named volume for the data folder to an Azure Storage emulator resource.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureStorageEmulatorResource"/>.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>A builder for the <see cref="AzureStorageEmulatorResource"/>.</returns>
    [AspireExport(Description = "Adds a named volume for the data folder to an Azure Storage emulator resource")]
    public static IResourceBuilder<AzureStorageEmulatorResource> WithDataVolume(this IResourceBuilder<AzureStorageEmulatorResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/data", isReadOnly);
    }

    /// <summary>
    /// Modifies the host port that the storage emulator listens on for blob requests.
    /// </summary>
    /// <param name="builder">Storage emulator resource builder.</param>
    /// <param name="port">Host port to use.</param>
    /// <returns></returns>
    [AspireExport(Description = "Sets the host port for blob requests on the storage emulator")]
    public static IResourceBuilder<AzureStorageEmulatorResource> WithBlobPort(this IResourceBuilder<AzureStorageEmulatorResource> builder, int port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("blob", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Modifies the host port that the storage emulator listens on for queue requests.
    /// </summary>
    /// <param name="builder">Storage emulator resource builder.</param>
    /// <param name="port">Host port to use.</param>
    /// <returns></returns>
    [AspireExport(Description = "Sets the host port for queue requests on the storage emulator")]
    public static IResourceBuilder<AzureStorageEmulatorResource> WithQueuePort(this IResourceBuilder<AzureStorageEmulatorResource> builder, int port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("queue", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Modifies the host port that the storage emulator listens on for table requests.
    /// </summary>
    /// <param name="builder">Storage emulator resource builder.</param>
    /// <param name="port">Host port to use.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureStorageEmulatorResource"/>.</returns>
    [AspireExport(Description = "Sets the host port for table requests on the storage emulator")]
    public static IResourceBuilder<AzureStorageEmulatorResource> WithTablePort(this IResourceBuilder<AzureStorageEmulatorResource> builder, int port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("table", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Ensures the emulator checks that the requested API version is valid.
    /// </summary>
    /// <param name="builder">Storage emulator resource builder.</param>
    /// <param name="enable">Whether to enable API version check or not. Default is <lang>true</lang>.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureStorageEmulatorResource"/>.</returns>
    [AspireExport(Description = "Configures whether the emulator checks API version validity")]
    public static IResourceBuilder<AzureStorageEmulatorResource> WithApiVersionCheck(this IResourceBuilder<AzureStorageEmulatorResource> builder, bool enable = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithArgs(context =>
        {
            context.Args.Remove(SkipApiVersionCheckArgument);

            if (enable)
            {
                context.Args.Add(SkipApiVersionCheckArgument);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureBlobStorageResource"/> which can be referenced to get the Azure Storage blob endpoint for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureBlobStorageResource"/>.</returns>
    [AspireExport(Description = "Adds an Azure Blob Storage resource")]
    public static IResourceBuilder<AzureBlobStorageResource> AddBlobs(this IResourceBuilder<AzureStorageResource> builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (string.Equals(name, builder.Resource.Name + "-blobs", StringComparisons.ResourceName))
        {
            // If the name is the default name, use the GetBlobService method instead so we keep
            // track of the default resource.
            return GetBlobService(builder);
        }

        return CreateBlobService(builder, name);
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureDataLakeStorageResource"/> which can be referenced to get the Azure Data Lake endpoint for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureDataLakeStorageResource"/>.</returns>
    [AspireExport(Description = "Adds an Azure Data Lake Storage resource")]
    public static IResourceBuilder<AzureDataLakeStorageResource> AddDataLake(this IResourceBuilder<AzureStorageResource> builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        builder.Resource.IsHnsEnabled = true;

        if (string.Equals(name, builder.Resource.Name + "-data-lake", StringComparisons.ResourceName))
        {
            // If the name is the default name, use the GetDataLakeService method instead so we keep
            // track of the default resource.
            return GetDataLakeService(builder);
        }

        return CreateDataLakeService(builder, name);
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureBlobStorageResource"/> which can be referenced to get the Azure Storage blob endpoint for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureBlobStorageResource"/>.</returns>
    /// <remarks>
    /// The name of the resource will be the default name of the blob service, which is <c>{builder.Resource.Name}-blobs</c>.
    /// <para>
    /// In order to register this resource using a custom name, use the <code>WithReference</code> method that takes a connection name.
    /// </para>
    /// <example>
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var blobs = builder.AddAzureStorage("storage").GetBlobService();
    ///
    /// var myService = builder.AddProject&lt;Projects.MyService&gt;()
    ///                       .WithReference(blobs, "blobs");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    private static IResourceBuilder<AzureBlobStorageResource> GetBlobService(this IResourceBuilder<AzureStorageResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var name = builder.Resource.Name + "-blobs";

        return builder.Resource.BlobStorageBuilder ??= CreateBlobService(builder, name);
    }

    private static IResourceBuilder<AzureDataLakeStorageResource> GetDataLakeService(this IResourceBuilder<AzureStorageResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var name = builder.Resource.Name + "-data-lake";

        return builder.Resource.DataLakeStorageBuilder ??= CreateDataLakeService(builder, name);
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureBlobStorageContainerResource"/> which can be referenced to get the Azure Storage blob container endpoint for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="blobContainerName">The name of the blob container.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureBlobStorageContainerResource"/>.</returns>
    [AspireExport(Description = "Adds an Azure Blob Storage container resource")]
    public static IResourceBuilder<AzureBlobStorageContainerResource> AddBlobContainer(this IResourceBuilder<AzureStorageResource> builder, [ResourceName] string name, string? blobContainerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        blobContainerName ??= name;

        AzureBlobStorageContainerResource resource = new(name, blobContainerName, GetBlobService(builder).Resource);
        builder.Resource.BlobContainers.Add(resource);

        string? connectionString = null;

        var healthCheckKey = $"{resource.Name}_check";

        BlobServiceClient? blobServiceClient = null;
        builder.ApplicationBuilder.Services.AddHealthChecks().AddAzureBlobStorage(
            sp => blobServiceClient ??= CreateBlobServiceClient(connectionString ?? throw new InvalidOperationException("Connection string is not initialized.")),
            optionsFactory: sp => new HealthChecks.Azure.Storage.Blobs.AzureBlobStorageHealthCheckOptions { ContainerName = blobContainerName },
            name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(resource)
            .WithHealthCheck(healthCheckKey)
            .OnConnectionStringAvailable(async (containerResource, @event, ct) =>
            {
                connectionString = await resource.Parent.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);
            });
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureDataLakeStorageFileSystemResource"/> which can be referenced to get the Azure DataLake file system connection string.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="dataLakeFileSystemName">The name of the data lake file system.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureDataLakeStorageFileSystemResource"/>.</returns>
    [AspireExport(Description = "Adds an Azure Data Lake Storage file system resource")]
    public static IResourceBuilder<AzureDataLakeStorageFileSystemResource> AddDataLakeFileSystem(this IResourceBuilder<AzureStorageResource> builder, [ResourceName] string name, string? dataLakeFileSystemName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.Resource.IsHnsEnabled = true;

        dataLakeFileSystemName ??= name;

        AzureDataLakeStorageFileSystemResource resource = new(name, dataLakeFileSystemName, GetDataLakeService(builder).Resource);
        builder.Resource.DataLakeFileSystems.Add(resource);

        return builder.ApplicationBuilder
            .AddResource(resource);
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureBlobStorageContainerResource"/> which can be referenced to get the Azure Storage blob container endpoint for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureBlobStorageResource"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="blobContainerName">The name of the blob container.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureBlobStorageContainerResource"/>.</returns>
    [Obsolete("Use AddBlobContainer on IResourceBuilder<AzureStorageResource> instead.")]
    public static IResourceBuilder<AzureBlobStorageContainerResource> AddBlobContainer(this IResourceBuilder<AzureBlobStorageResource> builder, [ResourceName] string name, string? blobContainerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        blobContainerName ??= name;

        AzureBlobStorageContainerResource resource = new(name, blobContainerName, builder.Resource);
        builder.Resource.Parent.BlobContainers.Add(resource);

        string? connectionString = null;
        builder.OnConnectionStringAvailable(async (blobStorage, @event, ct) =>
        {
            connectionString = await blobStorage.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);
        });

        var healthCheckKey = $"{resource.Name}_check";

        BlobServiceClient? blobServiceClient = null;
        builder.ApplicationBuilder.Services.AddHealthChecks().AddAzureBlobStorage(
            sp => blobServiceClient ??= CreateBlobServiceClient(connectionString ?? throw new InvalidOperationException("Connection string is not initialized.")),
            optionsFactory: sp => new HealthChecks.Azure.Storage.Blobs.AzureBlobStorageHealthCheckOptions { ContainerName = blobContainerName },
            name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(resource).WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureTableStorageResource"/> which can be referenced to get the Azure Storage tables endpoint for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureTableStorageResource"/>.</returns>
    [AspireExport(Description = "Adds an Azure Table Storage resource")]
    public static IResourceBuilder<AzureTableStorageResource> AddTables(this IResourceBuilder<AzureStorageResource> builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return CreateTableService(builder, name);
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureQueueStorageResource"/> which can be referenced to get the Azure Storage queues endpoint for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureQueueStorageResource"/>.</returns>
    [AspireExport(Description = "Adds an Azure Queue Storage resource")]
    public static IResourceBuilder<AzureQueueStorageResource> AddQueues(this IResourceBuilder<AzureStorageResource> builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (string.Equals(name, builder.Resource.Name + "-queues", StringComparisons.ResourceName))
        {
            // If the name is the default name, use the GetQueueService method instead so we keep
            // track of the default resource.
            return GetQueueService(builder);
        }

        return CreateQueueService(builder, name);
    }

    /// <summary>
    /// Gets a builder for the <see cref="AzureQueueStorageResource"/> which can be referenced to get the Azure Storage queues endpoint for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureQueueStorageResource"/>.</returns>
    private static IResourceBuilder<AzureQueueStorageResource> GetQueueService(this IResourceBuilder<AzureStorageResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var name = builder.Resource.Name + "-queues";

        return builder.Resource.QueueStorageBuilder ??= CreateQueueService(builder, name);
    }

    /// <summary>
    /// Creates a builder for the <see cref="AzureQueueStorageQueueResource"/> which can be referenced to get the Azure Storage queue for the storage account.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for <see cref="AzureStorageResource"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="queueName">The name of the queue.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureQueueStorageQueueResource"/>.</returns>
    [AspireExport(Description = "Adds an Azure Storage queue resource")]
    public static IResourceBuilder<AzureQueueStorageQueueResource> AddQueue(this IResourceBuilder<AzureStorageResource> builder, [ResourceName] string name, string? queueName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        queueName ??= name;

        AzureQueueStorageQueueResource resource = new(name, queueName, builder.GetQueueService().Resource);
        builder.Resource.Queues.Add(resource);

        string? connectionString = null;

        var healthCheckKey = $"{resource.Name}_check";

        QueueServiceClient? queueServiceClient = null;
        builder.ApplicationBuilder.Services.AddHealthChecks().AddAzureQueueStorage(
            sp => queueServiceClient ??= CreateQueueServiceClient(connectionString ?? throw new InvalidOperationException("Connection string is not initialized.")),
            optionsFactory: sp => new HealthChecks.Azure.Storage.Queues.AzureQueueStorageHealthCheckOptions { QueueName = queueName },
            name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(resource)
            .WithHealthCheck(healthCheckKey)
            .OnConnectionStringAvailable(async (containerResource, @event, ct) =>
            {
                connectionString = await resource.Parent.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);
            });
    }

    private static BlobServiceClient CreateBlobServiceClient(string connectionString)
    {
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            return new BlobServiceClient(uri, AzureCredentialHelper.CreateDefaultAzureCredential());
        }
        else
        {
            return new BlobServiceClient(connectionString);
        }
    }

    private static QueueServiceClient CreateQueueServiceClient(string connectionString)
    {
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            return new QueueServiceClient(uri, AzureCredentialHelper.CreateDefaultAzureCredential());
        }
        else
        {
            return new QueueServiceClient(connectionString);
        }
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Storage account. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure Storage account.</param>
    /// <param name="roles">The built-in storage roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <remarks>
    /// <example>
    /// Assigns the StorageBlobDataContributor role to the 'Projects.Api' project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var storage = builder.AddAzureStorage("storage");
    /// var blobs = storage.AddBlobs("blobs");
    /// 
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor)
    ///   .WithReference(blobs);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "StorageBuiltInRole is an Azure.Provisioning type not compatible with ATS. Use the AzureStorageRole-based overload instead.")]
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureStorageResource> target,
        params StorageBuiltInRole[] roles)
        where T : IResource
    {
        if (roles is null || roles.Length == 0)
        {
            return builder.WithRoleAssignments(target, StorageBuiltInRole.GetBuiltInRoleName, Array.Empty<StorageBuiltInRole>());
        }

        return builder.WithRoleAssignments(target, StorageBuiltInRole.GetBuiltInRoleName, roles);
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Storage account. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure Storage account.</param>
    /// <param name="roles">The storage roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <exception cref="ArgumentException">Thrown when a role value is not a valid <see cref="AzureStorageRole"/> value.</exception>
    [AspireExport("withStorageRoleAssignments", Description = "Assigns Azure Storage roles to a resource")]
    internal static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureStorageResource> target,
        params AzureStorageRole[] roles)
        where T : IResource
    {
        if (roles is null || roles.Length == 0)
        {
            return builder.WithRoleAssignments(target, Array.Empty<StorageBuiltInRole>());
        }

        var builtInRoles = new StorageBuiltInRole[roles.Length];
        for (var i = 0; i < roles.Length; i++)
        {
            builtInRoles[i] = roles[i] switch
            {
                AzureStorageRole.ClassicStorageAccountContributor => StorageBuiltInRole.ClassicStorageAccountContributor,
                AzureStorageRole.ClassicStorageAccountKeyOperatorServiceRole => StorageBuiltInRole.ClassicStorageAccountKeyOperatorServiceRole,
                AzureStorageRole.StorageAccountBackupContributor => StorageBuiltInRole.StorageAccountBackupContributor,
                AzureStorageRole.StorageAccountContributor => StorageBuiltInRole.StorageAccountContributor,
                AzureStorageRole.StorageAccountKeyOperatorServiceRole => StorageBuiltInRole.StorageAccountKeyOperatorServiceRole,
                AzureStorageRole.StorageBlobDataContributor => StorageBuiltInRole.StorageBlobDataContributor,
                AzureStorageRole.StorageBlobDataOwner => StorageBuiltInRole.StorageBlobDataOwner,
                AzureStorageRole.StorageBlobDataReader => StorageBuiltInRole.StorageBlobDataReader,
                AzureStorageRole.StorageBlobDelegator => StorageBuiltInRole.StorageBlobDelegator,
                AzureStorageRole.StorageFileDataPrivilegedContributor => StorageBuiltInRole.StorageFileDataPrivilegedContributor,
                AzureStorageRole.StorageFileDataPrivilegedReader => StorageBuiltInRole.StorageFileDataPrivilegedReader,
                AzureStorageRole.StorageFileDataSmbShareContributor => StorageBuiltInRole.StorageFileDataSmbShareContributor,
                AzureStorageRole.StorageFileDataSmbShareReader => StorageBuiltInRole.StorageFileDataSmbShareReader,
                AzureStorageRole.StorageFileDataSmbShareElevatedContributor => StorageBuiltInRole.StorageFileDataSmbShareElevatedContributor,
                AzureStorageRole.StorageQueueDataContributor => StorageBuiltInRole.StorageQueueDataContributor,
                AzureStorageRole.StorageQueueDataReader => StorageBuiltInRole.StorageQueueDataReader,
                AzureStorageRole.StorageQueueDataMessageSender => StorageBuiltInRole.StorageQueueDataMessageSender,
                AzureStorageRole.StorageQueueDataMessageProcessor => StorageBuiltInRole.StorageQueueDataMessageProcessor,
                AzureStorageRole.StorageTableDataContributor => StorageBuiltInRole.StorageTableDataContributor,
                AzureStorageRole.StorageTableDataReader => StorageBuiltInRole.StorageTableDataReader,
                _ => throw new ArgumentException($"'{roles[i]}' is not a valid {nameof(AzureStorageRole)} value.", nameof(roles))
            };
        }

        return builder.WithRoleAssignments(target, builtInRoles);
    }

    private static IResourceBuilder<AzureBlobStorageResource> CreateBlobService(IResourceBuilder<AzureStorageResource> builder, string name)
    {
        var resource = new AzureBlobStorageResource(name, builder.Resource);

        string? connectionString = null;

        // Add the "Blobs" resource health check. This is a separate health check from the "Storage" resource health check.
        // Doing it on the storage is not sufficient as the WaitForHealthyAsync doesn't bubble up to the parent resources.
        var healthCheckKey = $"{resource.Name}_check";

        BlobServiceClient? blobServiceClient = null;
        builder.ApplicationBuilder.Services.AddHealthChecks().AddAzureBlobStorage(sp =>
        {
            return blobServiceClient ??= CreateBlobServiceClient(connectionString ?? throw new InvalidOperationException("Connection string is not initialized."));
        }, name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(resource)
            .WithHealthCheck(healthCheckKey)
            .OnConnectionStringAvailable(async (blobs, @event, ct) =>
            {
                connectionString = await resource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);
            });
    }

    private static IResourceBuilder<AzureDataLakeStorageResource> CreateDataLakeService(IResourceBuilder<AzureStorageResource> builder, string name)
    {
        var resource = new AzureDataLakeStorageResource(name, builder.Resource);

        return builder.ApplicationBuilder
            .AddResource(resource);
    }

    private static IResourceBuilder<AzureTableStorageResource> CreateTableService(IResourceBuilder<AzureStorageResource> builder, string name)
    {
        var resource = new AzureTableStorageResource(name, builder.Resource);
        return builder.ApplicationBuilder.AddResource(resource);
    }

    private static IResourceBuilder<AzureQueueStorageResource> CreateQueueService(IResourceBuilder<AzureStorageResource> builder, string name)
    {
        var resource = new AzureQueueStorageResource(name, builder.Resource);

        string? connectionString = null;

        // Add the "Queues" resource health check. This is a separate health check from the "Storage" resource health check.
        // Doing it on the storage is not sufficient as the WaitForHealthyAsync doesn't bubble up to the parent resources.
        var healthCheckKey = $"{resource.Name}_check";

        QueueServiceClient? queueServiceClient = null;
        builder.ApplicationBuilder.Services.AddHealthChecks().AddAzureQueueStorage(sp =>
        {
            return queueServiceClient ??= CreateQueueServiceClient(connectionString ?? throw new InvalidOperationException("Connection string is not initialized."));
        }, name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(resource)
            .WithHealthCheck(healthCheckKey)
            .OnConnectionStringAvailable(async (queues, @event, ct) =>
            {
                connectionString = await resource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);
            });
    }

}
