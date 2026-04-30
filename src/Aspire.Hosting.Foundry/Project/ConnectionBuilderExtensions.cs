// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;
using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Search;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Microsoft Foundry project connection resources to the distributed application model.
/// </summary>
public static class AzureCognitiveServicesProjectConnectionsBuilderExtensions
{
    private const string BingAccountsResourceVersion = "2020-06-10";

    /// <summary>
    /// Adds a Microsoft Foundry project connection resource to a project. This is a low level
    /// interface that requires the caller to specify all connection properties.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for the parent Microsoft Foundry project resource.</param>
    /// <param name="name">The name of the Microsoft Foundry project connection resource.</param>
    /// <param name="configureProperties">Action to customize the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project connection resource.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "The configureProperties callback returns Azure provisioning types that are not ATS-compatible.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        string name,
        Func<AzureResourceInfrastructure, CognitiveServicesConnectionProperties> configureProperties)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        void configureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var aspireResource = (AzureCognitiveServicesProjectConnectionResource)infrastructure.AspireResource;
            var projectBicepId = aspireResource.Parent.GetBicepIdentifier();
            var project = aspireResource.Parent.AddAsExistingResource(infrastructure);

            var connection = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(
                infrastructure,
                (identifier, resourceName) =>
                {
                    var resource = aspireResource.FromExisting(identifier);
                    resource.Parent = project;
                    resource.Name = resourceName;
                    return resource;
                },
                infra =>
                {
                    var resource = new CognitiveServicesProjectConnection(aspireResource.GetBicepIdentifier(), AzureCognitiveServicesProjectConnectionResource.ResourceVersion)
                    {
                        Parent = project,
                        Name = name,
                        Properties = configureProperties(infra)
                    };
                    return resource;
                });
            if (aspireResource.Parent.KeyVaultConn is not null)
            {
                var keyVaultConn = aspireResource.Parent.KeyVaultConn.AddAsExistingResource(infrastructure);
                connection.DependsOn.Add(keyVaultConn);
            }
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = connection.Name });
            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = connection.Id });
        }
        var connectionResource = new AzureCognitiveServicesProjectConnectionResource(name, configureInfrastructure, builder.Resource);
        return builder.ApplicationBuilder.AddResource(connectionResource);
    }

    /// <summary>
    /// Adds CosmosDB to a project as a connection
    /// </summary>
    [AspireExportIgnore(Reason = "Raw AzureCosmosDBResource parameters are not ATS-compatible. Use the resource-builder overload instead.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        AzureCosmosDBResource db)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (db.IsEmulator())
        {
            throw new InvalidOperationException("Cannot create a Microsoft Foundry project connection to an emulator Cosmos DB instance.");
        }
        return builder.AddConnection($"connection-{Guid.NewGuid():N}", (infra) => new AadAuthTypeConnectionProperties()
        {
            Category = CognitiveServicesConnectionCategory.CosmosDB,
            Target = db.ConnectionStringOutput.AsProvisioningParameter(infra),
            IsSharedToAll = true,
            Metadata =
            {
                { "ApiType", "Azure" },
                { "ResourceId", db.Id.AsProvisioningParameter(infra) }
            }
        });
    }

    /// <summary>
    /// Adds CosmosDB to a project as a connection
    /// </summary>
    [AspireExport("addCosmosConnection", Description = "Adds an Azure Cosmos DB connection to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IResourceBuilder<AzureCosmosDBResource> db)
    {
        return builder.AddConnection(db.Resource);
    }

    /// <summary>
    /// Adds an Azure Storage account to a project as a connection.
    /// </summary>
    /// <returns></returns>
    [AspireExportIgnore(Reason = "Raw AzureStorageResource parameters are not ATS-compatible. Use the resource-builder overload instead.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        AzureStorageResource storage)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.IsEmulator())
        {
            throw new InvalidOperationException("Cannot create a Microsoft Foundry project connection to an emulator Storage account.");
        }
        return builder.AddConnection($"connection-{Guid.NewGuid():N}", (infra) => new AadAuthTypeConnectionProperties()
        {
            Category = CognitiveServicesConnectionCategory.AzureBlob,
            Target = storage.BlobEndpoint.AsProvisioningParameter(infra),
            IsSharedToAll = true,
            Metadata =
            {
                { "ApiType", "Azure" },
                { "ResourceId", storage.Id.AsProvisioningParameter(infra) }
            }
        });
    }

    /// <summary>
    /// Adds an Azure Storage account to a project as a connection.
    /// </summary>
    [AspireExport("addStorageConnection", Description = "Adds an Azure Storage connection to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IResourceBuilder<AzureStorageResource> storage)
    {
        builder.WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor);
        return builder.AddConnection(storage.Resource);
    }

    /// <summary>
    /// Adds a container registry connection to the Microsoft Foundry project.
    /// </summary>
    /// <returns></returns>
    [AspireExportIgnore(Reason = "Raw AzureContainerRegistryResource parameters are not ATS-compatible. Use the resource-builder overload instead.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        AzureContainerRegistryResource registry)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.IsEmulator())
        {
            throw new InvalidOperationException("Cannot create a Microsoft Foundry project connection to an emulator Container Registry");
        }
        return builder.AddConnection($"connection-{Guid.NewGuid():N}", (infra) => new ManagedIdentityAuthTypeConnectionProperties()
        {
            Category = CognitiveServicesConnectionCategory.ContainerRegistry,
            Target = registry.RegistryEndpoint.AsProvisioningParameter(infra),
            IsSharedToAll = true,
            Credentials = new CognitiveServicesConnectionManagedIdentity(){
                ClientId = "aiprojectidentityprincipleaid",
                ResourceId = registry.NameOutputReference.AsProvisioningParameter(infra)
            },
            Metadata =
            {
                { "ApiType", "Azure" },
                { "ResourceId", registry.NameOutputReference.AsProvisioningParameter(infra) }
            }
        });
    }

    /// <summary>
    /// Adds a container registry connection to the Microsoft Foundry project.
    /// </summary>
    /// <returns></returns>
    [AspireExport("addContainerRegistryConnection", Description = "Adds an Azure Container Registry connection to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IResourceBuilder<AzureContainerRegistryResource> registry)
    {
        return builder.AddConnection(registry.Resource);
    }

    /// <summary>
    /// Adds an Azure AI Search connection to a Microsoft Foundry project.
    /// </summary>
    [AspireExport("addSearchConnectionFromResource", Description = "Adds an Azure AI Search connection to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        AzureSearchResource search)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(search);

        return builder.AddConnection($"connection-{Guid.NewGuid():N}", (infra) =>
        {
            var searchService = (SearchService)search.AddAsExistingResource(infra);
            return new AadAuthTypeConnectionProperties()
            {
                Category = CognitiveServicesConnectionCategory.CognitiveSearch,
                Target = BicepFunction.Interpolate($"https://{searchService.Name}.search.windows.net"),
                Metadata =
                {
                    { "ApiType", "Azure" },
                    { "ResourceId", searchService.Id },
                    { "location", searchService.Location }
                }
            };
        });
    }

    /// <summary>
    /// Adds an Azure AI Search connection to a Microsoft Foundry project.
    /// </summary>
    [AspireExport("addSearchConnection", Description = "Adds an Azure AI Search connection to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IResourceBuilder<AzureSearchResource> search)
    {
        builder.WithRoleAssignments(search,
            SearchBuiltInRole.SearchIndexDataReader,
            SearchBuiltInRole.SearchServiceContributor);
        return builder.AddConnection(search.Resource);
    }

    /// <summary>
    /// Adds a Key Vault connection to the Microsoft Foundry project.
    /// </summary>
    /// <remarks>
    /// This connection allows the Microsoft Foundry project to store secrets for various other connections.
    /// As such, we recommend adding this connection *before* any others, so that those connections
    /// can leverage the Key Vault connection for secret storage.
    /// </remarks>
    [AspireExport("addKeyVaultConnection", Description = "Adds an Azure Key Vault connection to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IResourceBuilder<AzureKeyVaultResource> keyVault)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keyVault);
        if (keyVault.Resource.IsEmulator())
        {
            throw new InvalidOperationException("Cannot create a Microsoft Foundry project connection to an emulator Key Vault.");
        }
        builder.WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsOfficer);
        // Configuration based on https://github.com/azure-ai-foundry/foundry-samples/blob/9551912af4d4fdb8ea73e996145e940a7e369c84/infrastructure/infrastructure-setup-bicep/01-connections/connection-key-vault.bicep
        // We use a custom subclass because Azure.Provisioning.CognitiveServices does not support the "AzureKeyVault" connection category yet (as of 2026-01-06).
        // We also swap `ManagedIdentity` auth type for `AccountManagedIdentity`, because the latter seems to be an error in the Bicep template.
        return builder.AddConnection($"{keyVault.Resource.Name}-{Guid.NewGuid():N}", (infra) =>
        {
            var vault = (KeyVaultService)keyVault.Resource.AddAsExistingResource(infra);
            return new AzureKeyVaultConnectionProperties()
            {
                Target = vault.Id,
                IsSharedToAll = true,
                Metadata =
                {
                    { "ApiType", "Azure" },
                    { "ResourceId", vault.Id },
                    { "location", vault.Location }
                }
            };
            });
    }

    [AspireExport("addConnection", Description = "Adds a connection to a Microsoft Foundry project.")]
    internal static IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> AddConnectionForPolyglot(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        [AspireUnion(
            typeof(IResourceBuilder<AzureCosmosDBResource>),
            typeof(IResourceBuilder<AzureStorageResource>),
            typeof(IResourceBuilder<AzureContainerRegistryResource>),
            typeof(IResourceBuilder<AzureKeyVaultResource>))]
        object resource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);

        return resource switch
        {
            IResourceBuilder<AzureCosmosDBResource> cosmosDb => builder.AddConnection(cosmosDb),
            IResourceBuilder<AzureStorageResource> storage => builder.AddConnection(storage),
            IResourceBuilder<AzureContainerRegistryResource> registry => builder.AddConnection(registry),
            IResourceBuilder<AzureKeyVaultResource> keyVault => builder.AddConnection(keyVault),
            _ => throw new ArgumentException(
                "Resource must be a Cosmos DB, Storage, Container Registry, or Key Vault resource builder.",
                nameof(resource))
        };
    }

    /// <summary>
    /// Adds a Grounding with Bing Search connection to a Microsoft Foundry project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Bing Search resource (<c>Microsoft.Bing/accounts</c>) cannot be provisioned through Aspire
    /// or Bicep. It must be created manually in the
    /// <a href="https://portal.azure.com">Azure portal</a>.
    /// </para>
    /// <para>
    /// Once the Bing resource exists, pass its resource ID to this method. The connection is
    /// created in the Foundry project using API key authentication with
    /// <c>category: "ApiKey"</c> and <c>metadata.Type: "bing_grounding"</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for the parent Microsoft Foundry project resource.</param>
    /// <param name="name">The name of the connection resource.</param>
    /// <param name="bingResourceId">
    /// The full Azure resource ID of the Bing Search resource
    /// (e.g., <c>/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Bing/accounts/{name}</c>).
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the connection resource.</returns>
    [AspireExport(Description = "Adds a Grounding with Bing Search connection to a Microsoft Foundry project.")]
    public static IResourceBuilder<BingGroundingConnectionResource> AddBingGroundingConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        string name,
        string bingResourceId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(bingResourceId);

        return builder.AddBingConnection(name, (infra) =>
        {
            return new BingGroundingConnectionProperties()
            {
                Target = "https://api.bing.microsoft.com/",
                UseWorkspaceManagedIdentity = false,
                IsSharedToAll = false,
                SharedUserList = [],
                PeRequirement = ManagedPERequirement.NotRequired,
                PeStatus = ManagedPEStatus.NotApplicable,
                CredentialsKey = (BicepValue<string>)new MemberExpression(
                    new FunctionCallExpression(
                        new IdentifierExpression("listKeys"),
                        new StringLiteralExpression(bingResourceId),
                        new StringLiteralExpression(BingAccountsResourceVersion)),
                    "key1"),
                Metadata =
                {
                    { "type", "bing_grounding" },
                    { "ApiType", "Azure" },
                    { "ResourceId", bingResourceId }
                }
            };
        });
    }

    /// <summary>
    /// Adds a Grounding with Bing Search connection to a Microsoft Foundry project using a
    /// parameter resource for the Bing resource ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This overload allows the Bing resource ID to be supplied as a parameter (e.g., from user secrets
    /// or configuration) rather than a hardcoded string. The parameter value is resolved at deployment time
    /// and embedded in the Bicep template.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for the parent Microsoft Foundry project resource.</param>
    /// <param name="name">The name of the connection resource.</param>
    /// <param name="bingResourceId">
    /// A parameter resource containing the full Azure resource ID of the Bing Search resource.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the connection resource.</returns>
    [AspireExport("addBingGroundingConnectionFromParameter", Description = "Adds a Grounding with Bing Search connection to a Microsoft Foundry project using a parameter.")]
    public static IResourceBuilder<BingGroundingConnectionResource> AddBingGroundingConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        string name,
        IResourceBuilder<ParameterResource> bingResourceId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(bingResourceId);

        return builder.AddBingConnection(name, (infra) =>
        {
            var resourceIdParam = bingResourceId.AsProvisioningParameter(infra);
            return new BingGroundingConnectionProperties()
            {
                Target = "https://api.bing.microsoft.com/",
                UseWorkspaceManagedIdentity = false,
                IsSharedToAll = false,
                SharedUserList = [],
                PeRequirement = ManagedPERequirement.NotRequired,
                PeStatus = ManagedPEStatus.NotApplicable,
                CredentialsKey = (BicepValue<string>)new MemberExpression(
                    new FunctionCallExpression(
                        new IdentifierExpression("listKeys"),
                        resourceIdParam.Value.Compile(),
                        new StringLiteralExpression(BingAccountsResourceVersion)),
                    "key1"),
                Metadata =
                {
                    { "type", "bing_grounding" },
                    { "ApiType", "Azure" },
                    { "ResourceId", resourceIdParam }
                }
            };
        });
    }

    private static IResourceBuilder<BingGroundingConnectionResource> AddBingConnection(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        string name,
        Func<AzureResourceInfrastructure, CognitiveServicesConnectionProperties> configureProperties)
    {
        void configureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var aspireResource = (BingGroundingConnectionResource)infrastructure.AspireResource;
            var account = aspireResource.Parent.Parent.AddAsExistingResource(infrastructure);

            var connection = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(
                infrastructure,
                (identifier, resourceName) =>
                {
                    var resource = aspireResource.FromExisting(identifier);
                    resource.Parent = account;
                    resource.Name = resourceName;
                    return resource;
                },
                infra =>
                {
                    var resource = new CognitiveServicesConnection(aspireResource.GetBicepIdentifier(), AzureCognitiveServicesProjectConnectionResource.ResourceVersion)
                    {
                        Parent = account,
                        Name = name,
                        Properties = configureProperties(infra)
                    };
                    return resource;
                });
            if (aspireResource.Parent.KeyVaultConn is not null)
            {
                var keyVaultConn = aspireResource.Parent.KeyVaultConn.AddAsExistingResource(infrastructure);
                connection.DependsOn.Add(keyVaultConn);
            }
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = connection.Name });
            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = connection.Id });
        }
        var connectionResource = new BingGroundingConnectionResource(name, configureInfrastructure, builder.Resource);
        return builder.ApplicationBuilder.AddResource(connectionResource);
    }
}
