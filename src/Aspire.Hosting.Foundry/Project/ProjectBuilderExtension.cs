// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;
using Azure.Provisioning;
using Azure.Provisioning.ApplicationInsights;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.CosmosDB;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Azure.Provisioning.Search;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Microsoft Foundry project resources to the distributed application model.
/// </summary>
public static class AzureCognitiveServicesProjectExtensions
{
    /// <summary>
    /// Adds a Microsoft Foundry project resource to the application model.
    ///
    /// This will also attach the project as a deployment target for agents.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for the parent Microsoft Foundry account resource.</param>
    /// <param name="name">The name of the Microsoft Foundry project resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project resource.</returns>
    [AspireExport("addProject", Description = "Adds a Microsoft Foundry project resource to a Microsoft Foundry resource.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectResource> AddProject(
        this IResourceBuilder<FoundryResource> builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (builder.Resource.IsEmulator)
        {
            throw new InvalidOperationException(FoundryExtensions.LocalProjectsNotSupportedMessage);
        }

        builder.ApplicationBuilder.Services.Configure<AzureProvisioningOptions>(o => o.SupportsTargetedRoleAssignments = true);

        var project = builder.ApplicationBuilder.AddResource(new AzureCognitiveServicesProjectResource(name, ConfigureInfrastructure, builder.Resource));
        project.Resource.DefaultContainerRegistry = CreateDefaultRegistry(builder.ApplicationBuilder, $"{name}-acr");
        return project;
    }

    /// <summary>
    /// Associates a container registry with the Microsoft Foundry project resource for
    /// publishing and locating hosted agents.
    /// </summary>
    [AspireExport("withContainerRegistry", Description = "Associates a container registry with a Microsoft Foundry project resource.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectResource> WithContainerRegistry(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IResourceBuilder<AzureContainerRegistryResource> registryBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registryBuilder);

        return builder.WithContainerRegistry(registryBuilder.Resource);
    }

    /// <summary>
    /// Associates a container registry with the Microsoft Foundry project resource for
    /// publishing and locating hosted agents.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the resource-builder overload instead.</remarks>
    [AspireExportIgnore(Reason = "IContainerRegistry is not ATS-compatible. Use the resource-builder overload instead.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectResource> WithContainerRegistry(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IContainerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registry);

        // This will be queried during the "publish" phase
        builder.Resource.Annotations.Add(new ContainerRegistryReferenceAnnotation(registry));
        return builder;
    }

    /// <summary>
    /// Adds a reference to a Microsoft Foundry project resource to the destination resource.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the standard <c>WithReference</c> overload instead.</remarks>
    [AspireExportIgnore(Reason = "The standard WithReference export already covers this polyglot scenario.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder, IResourceBuilder<AzureCognitiveServicesProjectResource> project)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(project);

        // Add standard references and environment variables
        ResourceBuilderExtensions.WithReference(builder, project);

        if (builder is IResourceBuilder<IResourceWithWaitSupport> waitableBuilder)
        {
            waitableBuilder.WaitFor(project);
        }
        return builder;
    }

    /// <summary>
    /// Adds a Key Vault connection to the Microsoft Foundry project.
    /// </summary>
    /// <param name="builder">The resource builder for the Microsoft Foundry project.</param>
    /// <param name="keyVault">The Key Vault resource to associate with the project.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the project already has a Key Vault connection configured.</exception>
    [AspireExport("withKeyVault", Description = "Associates an Azure Key Vault resource with a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectResource> WithKeyVault(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IResourceBuilder<AzureKeyVaultResource> keyVault)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keyVault);
        if (builder.Resource.KeyVaultConn is not null)
        {
            throw new InvalidOperationException($"Azure Cognitive Services project resource '{builder.Resource.Name}' already has a Key Vault connection configured.");
        }

        var conn = builder.AddConnection(keyVault);
        // We need to keep a reference to the connection resource for dependency tracking
        builder.Resource.KeyVaultConn = conn.Resource;
        return builder.WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsOfficer);
    }

    /// <summary>
    /// Adds an Application Insights resource to the Microsoft Foundry project,
    /// overriding the default (which is to create a new Application Insights resource).
    /// </summary>
    /// <param name="builder">The resource builder for the Microsoft Foundry project.</param>
    /// <param name="appInsights">The Application Insights resource to associate with the project.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withAppInsights", Description = "Associates an Azure Application Insights resource with a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureCognitiveServicesProjectResource> WithAppInsights(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        IResourceBuilder<AzureApplicationInsightsResource> appInsights)
    {
        builder.Resource.AppInsights = appInsights.Resource;
        return builder;
    }

    /// <summary>
    /// Adds a capability host to the Microsoft Foundry project, enabling agent capabilities
    /// with external Azure resources such as CosmosDB, Storage, and Search.
    /// </summary>
    /// <param name="builder">The resource builder for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the capability host.</param>
    /// <returns>A <see cref="CapabilityHostBuilder"/> for fluent configuration of the capability host resources.</returns>
    /// <example>
    /// <code lang="csharp">
    /// project.AddCapabilityHost("cap-host")
    ///     .WithCosmosDB(cosmosDb)
    ///     .WithStorage(storage)
    ///     .WithSearch(search)
    ///     .WithAzureOpenAI(foundry);
    /// </code>
    /// </example>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "CapabilityHostBuilder is not ATS-compatible.")]
    public static CapabilityHostBuilder AddCapabilityHost(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var config = new CapabilityHostConfiguration(name);
        builder.Resource.CapabilityHostConfiguration = config;
        return new CapabilityHostBuilder(builder, config);
    }

    /// <summary>
    /// Adds a model deployment to the parent Microsoft Foundry resource of the Microsoft Foundry project.
    /// </summary>
    /// <param name="builder">Aspire resource builder for a project</param>
    /// <param name="name">Name to give the model deployment</param>
    /// <param name="model">The <see cref="FoundryModel"/> to deploy.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the deployment resource.</returns>
    [AspireExport("addModelDeploymentFromModel", Description = "Adds a model deployment to the parent Microsoft Foundry resource by using a model descriptor.")]
    public static IResourceBuilder<FoundryDeploymentResource> AddModelDeployment(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        [ResourceName] string name,
        FoundryModel model)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.Parent).AddDeployment(name, model);
    }

    /// <summary>
    /// Adds a model deployment to the parent Microsoft Foundry resource of the Microsoft Foundry project.
    /// </summary>
    [AspireExport("addModelDeployment", Description = "Adds a model deployment to the parent Microsoft Foundry resource.")]
    public static IResourceBuilder<FoundryDeploymentResource> AddModelDeployment(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> builder,
        [ResourceName] string name,
        string modelName,
        string modelVersion,
        string format)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.Parent).AddDeployment(name, modelName, modelVersion, format);
    }

    internal static void ConfigureInfrastructure(AzureResourceInfrastructure infra)
    {
        var prefix = infra.AspireResource.Name;
        var aspireResource = (AzureCognitiveServicesProjectResource)infra.AspireResource;
        var tags = new ProvisioningParameter("tags", typeof(object))
        {
            Value = new BicepDictionary<string>()
        };
        infra.Add(tags);

        // This tells azd to avoid creating infrastructure
        var userPrincipalId = new ProvisioningParameter(AzureBicepResource.KnownParameters.UserPrincipalId, typeof(string)) { Value = new BicepValue<string>(string.Empty) };
        infra.Add(userPrincipalId);

        /*
         * Create managed identity
         */

        ManagedServiceIdentity managedIdentity;
        if (aspireResource.TryGetAppIdentityResource(out var idResource) && idResource is AzureUserAssignedIdentityResource identityResource)
        {
            managedIdentity = new ManagedServiceIdentity()
            {
                ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned,
                // We hack in this dictionary because the CDK doesn't take BicepValues as
                // keys.
                UserAssignedIdentities =
                {
                    { ((UserAssignedIdentity)identityResource.AddAsExistingResource(infra)).Id.Compile().ToString(), new UserAssignedIdentityDetails() }
                }
            };
        }
        else
        {
            managedIdentity = new ManagedServiceIdentity()
            {
                ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned
            };
        }
        var account = aspireResource.Parent.AddAsExistingResource(infra);

        /*
         * Create the project
         */

        var project = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(
            infra,
            (identifier, resourceName) =>
            {
                var resource = aspireResource.FromExisting(identifier);
                resource.Parent = account;
                resource.Name = resourceName;
                return resource;
            },
            infra =>
            {
                var resource = new CognitiveServicesProject(infra.AspireResource.GetBicepIdentifier())
                {
                    Parent = account,
                    Name = aspireResource.Name,
                    Identity = managedIdentity,
                    Properties = new CognitiveServicesProjectProperties
                    {
                        DisplayName = aspireResource.Name
                    },
                    Tags = { { "aspire-resource-name", infra.AspireResource.Name } }
                };
                return resource;
            });
        var projectPrincipalId = project.Identity.PrincipalId;
        infra.Add(new ProvisioningOutput("id", typeof(string))
        {
            Value = project.Id
        });
        infra.Add(new ProvisioningOutput("name", typeof(string)) { Value = project.Name });
        infra.Add(new ProvisioningOutput("endpoint", typeof(string))
        {
            Value = (BicepValue<string>)new IndexExpression((BicepExpression)project.Properties.Endpoints!, "AI Foundry API")
        });
        infra.Add(new ProvisioningOutput("principalId", typeof(string))
        {
            Value = projectPrincipalId
        });

        /*
         * Container registry for hosted agents
         *
         * TODO: only provision if we need to create a Hosted Agent
         */

        AzureProvisioningResource? registry = null;
        if (aspireResource.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var registryReferenceAnnotation) && registryReferenceAnnotation.Registry is AzureProvisioningResource r)
        {
            registry = r;
        }
        else if (aspireResource.DefaultContainerRegistry is not null)
        {
            registry = aspireResource.DefaultContainerRegistry;
        }
        else
        {
            throw new InvalidOperationException($"No container registry configured for Azure Cognitive Services project resource '{aspireResource.Name}'. A container registry is required to publish and run hosted agents.");
        }
        var containerRegistry = (ContainerRegistryService)registry.AddAsExistingResource(infra);
        // Why do we need this?
        infra.Add(containerRegistry);

        // Project needs this to pull hosted agent images and run them
        var pullRa = containerRegistry.CreateRoleAssignment(ContainerRegistryBuiltInRole.AcrPull, RoleManagementPrincipalType.ServicePrincipal, projectPrincipalId);
        // There's a bug in the CDK, see https://github.com/Azure/azure-sdk-for-net/issues/47265
        pullRa.Name = BicepFunction.CreateGuid(containerRegistry.Id, project.Id, pullRa.RoleDefinitionId);
        infra.Add(pullRa);
        infra.Add(containerRegistry);
        infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_ENDPOINT", typeof(string))
        {
            Value = containerRegistry.LoginServer
        });
        infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_NAME", typeof(string))
        {
            Value = containerRegistry.Name
        });
        infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID", typeof(string))
        {
            Value = projectPrincipalId
        });

        // Implicit dependencies for capability hosts
        List<ProvisionableResource> capHostDeps = [];

        var keyVaultConn = aspireResource.KeyVaultConn?.AddAsExistingResource(infra);

        /*
         * Application Insights for telemetry
         */

        ApplicationInsightsComponent appInsights;
        if (aspireResource.AppInsights is not null && !aspireResource.AppInsights.IsEmulator())
        {
            appInsights = (ApplicationInsightsComponent)aspireResource.AppInsights.AddAsExistingResource(infra);
        }
        else
        {
            appInsights = new ApplicationInsightsComponent(Infrastructure.NormalizeBicepIdentifier($"{prefix}-ai"))
            {
                ApplicationType = ApplicationInsightsApplicationType.Web,
                Name = $"{aspireResource.Name}-ai",
                Kind = "web",
                Tags = tags
            };
            infra.Add(appInsights);
        }
        var pubRoleRa = appInsights.CreateRoleAssignment(ApplicationInsightsBuiltInRole.MonitoringMetricsPublisher, RoleManagementPrincipalType.ServicePrincipal, projectPrincipalId);
        pubRoleRa.Name = BicepFunction.CreateGuid(appInsights.Id, project.Id, pubRoleRa.RoleDefinitionId);
        infra.Add(pubRoleRa);
        // This is for passing into hosted agent application code
        infra.Add(new ProvisioningOutput("APPLICATION_INSIGHTS_CONNECTION_STRING", typeof(string))
        {
            Value = appInsights.ConnectionString
        });
        // Project needs a connection to send server-side telemetry
        var appInsightsConn = new CognitiveServicesProjectConnection($"{aspireResource.GetBicepIdentifier()}_ai_conn")
        {
            Parent = project,
            Name = $"{aspireResource.Name}-ai-conn",
            Properties = new AppInsightsConnectionProperties()
            {
                Target = appInsights.Id,
                IsSharedToAll = false,
                CredentialsKey = appInsights.ConnectionString,
                Metadata =
                {
                    { "ApiType", "Azure" },
                    { "ResourceId", appInsights.Id },
                    { "location", appInsights.Location }
                }
            }
        };
        if (keyVaultConn is not null)
        {
            appInsightsConn.DependsOn.Add(keyVaultConn);
        }
        infra.Add(appInsightsConn);

        /*
         * Capability host for BYO resources.
         * These resources are all-or-nothing (except for Azure OpenAI), and will replace the public hosting
         * caphost.
         * TODO: private network
         */

        if (aspireResource.CapabilityHostConfiguration is null)
        {
            return;
        }

        aspireResource.CapabilityHostConfiguration.Validate(aspireResource.Name);

        var capHostConfig = aspireResource.CapabilityHostConfiguration;

        var capHostProps = new PublicHostingCognitiveServicesCapabilityHostProperties()
        {
            CapabilityHostKind = CapabilityHostKind.Agents
        };

        /*
        * Storage
        */

        var storage = (StorageAccount)capHostConfig.Storage!.AddAsExistingResource(infra);
        var storageConn = new CognitiveServicesProjectConnection($"{aspireResource.GetBicepIdentifier()}_storage_conn")
        {
            Parent = project,
            Name = BicepFunction.Interpolate($"{project.Name}-{storage.Name}"),
            Properties = new AzureStorageAccountConnectionProperties()
            {
                Target = capHostConfig.Storage!.BlobEndpoint.AsProvisioningParameter(infra),
                Metadata =
                {
                    { "ApiType", "Azure" },
                    { "ResourceId", storage.Id },
                    { "location", storage.Location }
                }
            }
        };
        if (keyVaultConn is not null)
        {
            storageConn.DependsOn.Add(keyVaultConn);
        }
        infra.Add(storageConn);
        var storageRoleRa = storage.CreateRoleAssignment(StorageBuiltInRole.StorageBlobDataContributor, RoleManagementPrincipalType.ServicePrincipal, projectPrincipalId);
        storageRoleRa.Name = BicepFunction.CreateGuid(storage.Id, project.Id, storageRoleRa.RoleDefinitionId);
        infra.Add(storageRoleRa);
        capHostDeps.Add(storage);
        capHostDeps.Add(storageRoleRa);
        capHostProps.StorageConnections = [storageConn.Name];

        /*
        * CosmosDB
        */

        var cosmosDb = (CosmosDBAccount)capHostConfig.CosmosDB!.AddAsExistingResource(infra);
        var cosmosDbConn = new CognitiveServicesProjectConnection($"{aspireResource.GetBicepIdentifier()}_cosmosdb_conn")
        {
            Parent = project,
            Name = BicepFunction.Interpolate($"{project.Name}-{cosmosDb.Name}"),
            Properties = new AadAuthTypeConnectionProperties()
            {
                Category = CognitiveServicesConnectionCategory.CosmosDB,
                // This is the document endpoint
                Target = capHostConfig.CosmosDB!.ConnectionStringOutput.AsProvisioningParameter(infra),
                Metadata =
                {
                    { "ApiType", "Azure" },
                    { "ResourceId", cosmosDb.Id },
                    { "location", cosmosDb.Location }
                }
            }
        };
        if (keyVaultConn is not null)
        {
            cosmosDbConn.DependsOn.Add(keyVaultConn);
        }
        infra.Add(cosmosDbConn);
        var cosmosDbRoleRa = cosmosDb.CreateRoleAssignment(
            // Data Contributor
            new CosmosDBBuiltInRole("00000000-0000-0000-0000-000000000002"),
            RoleManagementPrincipalType.ServicePrincipal,
            projectPrincipalId
        );
        cosmosDbRoleRa.Name = BicepFunction.CreateGuid(cosmosDb.Id, project.Id, cosmosDbRoleRa.RoleDefinitionId);
        infra.Add(cosmosDbRoleRa);
        capHostDeps.Add(cosmosDb);
        capHostDeps.Add(cosmosDbRoleRa);
        capHostProps.ThreadStorageConnections = [cosmosDbConn.Name];

        /*
        * Azure Search
        */

        var searchService = (SearchService)capHostConfig.Search!.AddAsExistingResource(infra);
        var searchConn = new CognitiveServicesProjectConnection($"{aspireResource.GetBicepIdentifier()}_search_conn")
        {
            Parent = project,
            Name = BicepFunction.Interpolate($"{project.Name}-{searchService.Name}"),
            Properties = new AadAuthTypeConnectionProperties()
            {
                Category = CognitiveServicesConnectionCategory.CognitiveSearch,
                Target = BicepFunction.Interpolate($"https://{searchService.Name}.search.windows.net"),
                Metadata =
                {
                    { "ApiType", "Azure" },
                    { "ResourceId", searchService.Id },
                    { "location", searchService.Location }
                }
            }
        };
        if (keyVaultConn is not null)
        {
            searchConn.DependsOn.Add(keyVaultConn);
        }
        infra.Add(searchConn);
        var contributor = searchService.CreateRoleAssignment(SearchBuiltInRole.SearchServiceContributor, RoleManagementPrincipalType.ServicePrincipal, projectPrincipalId);
        contributor.Name = BicepFunction.CreateGuid(searchService.Id, project.Id, contributor.RoleDefinitionId);
        infra.Add(contributor);
        var indexDataContrib = searchService.CreateRoleAssignment(SearchBuiltInRole.SearchIndexDataContributor, RoleManagementPrincipalType.ServicePrincipal, projectPrincipalId);
        indexDataContrib.Name = BicepFunction.CreateGuid(searchService.Id, project.Id, indexDataContrib.RoleDefinitionId);
        infra.Add(indexDataContrib);
        capHostDeps.Add(searchService);
        capHostDeps.Add(contributor);
        capHostDeps.Add(indexDataContrib);
        capHostProps.VectorStoreConnections = [searchConn.Name];

        /*
        * Azure OpenAI Account (optional)
        */

        CognitiveServicesProjectConnection? aoaiConn = null;
        if (capHostConfig.AzureOpenAI is not null)
        {
            var aoaiAccount = capHostConfig.AzureOpenAI.AddAsExistingResource(infra);
            aoaiConn = new CognitiveServicesProjectConnection($"{aspireResource.GetBicepIdentifier()}_aoai_conn")
            {
                Parent = project,
                Name = BicepFunction.Interpolate($"{project.Name}-{aoaiAccount.Name}"),
                Properties = new AadAuthTypeConnectionProperties()
                {
                    Category = CognitiveServicesConnectionCategory.AzureOpenAI,
                    Target = aoaiAccount.Properties.Endpoint,
                    Metadata =
                    {
                        { "ApiType", "Azure" },
                        { "ResourceId", aoaiAccount.Id },
                        { "location", aoaiAccount.Location }
                    }
                }
            };
            if (keyVaultConn is not null)
            {
                aoaiConn.DependsOn.Add(keyVaultConn);
            }
            infra.Add(aoaiConn);
            var aoaiRoleRa = aoaiAccount.CreateRoleAssignment(CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser, RoleManagementPrincipalType.ServicePrincipal, projectPrincipalId);
            aoaiRoleRa.Name = BicepFunction.CreateGuid(aoaiAccount.Id, project.Id, aoaiRoleRa.RoleDefinitionId);
            infra.Add(aoaiRoleRa);
            capHostDeps.Add(aoaiAccount);
            capHostDeps.Add(aoaiRoleRa);
            capHostProps.AiServicesConnections = [aoaiConn.Name];
        }

        // Necesssary to have parameter enablePublicHostingEnvironment
        var capHost = new CognitiveServicesProjectCapabilityHost(Infrastructure.NormalizeBicepIdentifier($"{prefix}-caphost"), "2025-10-01-preview")
        {
            Parent = project,
            Name = $"{project.Name}-ch",
            Properties = capHostProps,
        };
        if (keyVaultConn is not null)
        {
            capHost.DependsOn.Add(keyVaultConn);
        }
        foreach (var dep in capHostDeps)
        {
            capHost.DependsOn.Add(dep);
        }
        infra.Add(capHost);
    }

    private static AzureContainerRegistryResource CreateDefaultRegistry(IDistributedApplicationBuilder builder, string name)
    {
        static void configureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var registry = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, resourceName) =>
                {
                    var resource = ContainerRegistryService.FromExisting(identifier);
                    resource.Name = resourceName;
                    return resource;
                },
                (infra) => new ContainerRegistryService(infra.AspireResource.GetBicepIdentifier())
                {
                    Sku = new ContainerRegistrySku { Name = ContainerRegistrySkuName.Basic },
                    Tags = { { "aspire-resource-name", infra.AspireResource.Name } }
                });

            infrastructure.Add(registry);
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = registry.Name });
            infrastructure.Add(new ProvisioningOutput("loginServer", typeof(string)) { Value = registry.LoginServer });
        }

        var resource = new AzureContainerRegistryResource(name, configureInfrastructure);
        builder.AddResource(resource);
        return resource;
    }
}
