// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Lifecycle;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.OperationalInsights;
using Azure.Provisioning.Roles;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.DependencyInjection;
using FileShare = Azure.Provisioning.Storage.FileShare;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for customizing Azure Container App definitions for projects.
/// </summary>
public static class AzureContainerAppExtensions
{
    /// <summary>
    /// Adds the necessary infrastructure for Azure Container Apps to the distributed application builder.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    [Obsolete($"Use {nameof(AddAzureContainerAppEnvironment)} instead. This method will be removed in a future version.")]
    public static IDistributedApplicationBuilder AddAzureContainerAppsInfrastructure(this IDistributedApplicationBuilder builder) =>
        AddAzureContainerAppsInfrastructureCore(builder);

    internal static IDistributedApplicationBuilder AddAzureContainerAppsInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ensure AzureProvisioning is added first so the AzureResourcePreparer lifecycle hook runs before AzureContainerAppsInfrastructure
        builder.AddAzureProvisioning();

        // AzureContainerAppsInfrastructure will handle adding role assignments,
        // so Azure resources don't need to add the default role assignments themselves
        builder.Services.Configure<AzureProvisioningOptions>(o => o.SupportsTargetedRoleAssignments = true);

        builder.Services.TryAddEventingSubscriber<AzureContainerAppsInfrastructure>();

        return builder;
    }

    /// <summary>
    /// Adds a container app environment resource to the distributed application builder.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    [AspireExport("addAzureContainerAppEnvironment", Description = "Adds an Azure Container App Environment resource")]
    public static IResourceBuilder<AzureContainerAppEnvironmentResource> AddAzureContainerAppEnvironment(this IDistributedApplicationBuilder builder, string name)
    {
        builder.AddAzureContainerAppsInfrastructureCore();

        var containerAppEnvResource = new AzureContainerAppEnvironmentResource(name, static infra =>
        {
            var appEnvResource = (AzureContainerAppEnvironmentResource)infra.AspireResource;

            // This tells azd to avoid creating infrastructure
            var userPrincipalId = new ProvisioningParameter(AzureBicepResource.KnownParameters.UserPrincipalId, typeof(string)) { Value = new BicepValue<string>(string.Empty) };
            infra.Add(userPrincipalId);

            var tags = new ProvisioningParameter("tags", typeof(object))
            {
                Value = new BicepDictionary<string>()
            };

            infra.Add(tags);

            ProvisioningVariable? resourceToken = null;
            if (appEnvResource.UseAzdNamingConvention || appEnvResource.UseCompactResourceNaming)
            {
                resourceToken = new ProvisioningVariable("resourceToken", typeof(string))
                {
                    Value = BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)
                };
                infra.Add(resourceToken);
            }

            var identity = new UserAssignedIdentity(Infrastructure.NormalizeBicepIdentifier($"{appEnvResource.Name}_mi"))
            {
                Tags = tags
            };

            infra.Add(identity);

            AzureProvisioningResource? registry = null;
            if (appEnvResource.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var registryReferenceAnnotation) &&
                registryReferenceAnnotation.Registry is AzureProvisioningResource explicitRegistry)
            {
                registry = explicitRegistry;
            }
            else if (appEnvResource.DefaultContainerRegistry is not null)
            {
                registry = appEnvResource.DefaultContainerRegistry;
            }

            if (registry is null)
            {
                throw new InvalidOperationException($"No container registry associated with environment '{appEnvResource.Name}'. This should have been added automatically.");
            }

            var containerRegistry = (ContainerRegistryService)registry.AddAsExistingResource(infra);
            infra.Add(containerRegistry);

            var pullRa = containerRegistry.CreateRoleAssignment(ContainerRegistryBuiltInRole.AcrPull, identity);

            // There's a bug in the CDK, see https://github.com/Azure/azure-sdk-for-net/issues/47265
            pullRa.Name = BicepFunction.CreateGuid(containerRegistry.Id, identity.Id, pullRa.RoleDefinitionId);
            infra.Add(pullRa);

            OperationalInsightsWorkspace? laWorkspace = null;
            if (appEnvResource.TryGetLastAnnotation<AzureLogAnalyticsWorkspaceReferenceAnnotation>(out var logAnalyticsReferenceAnnotation) && logAnalyticsReferenceAnnotation.Workspace is AzureProvisioningResource workspace)
            {
                laWorkspace = (OperationalInsightsWorkspace)workspace.AddAsExistingResource(infra);
            }
            else
            {
                laWorkspace = new OperationalInsightsWorkspace(Infrastructure.NormalizeBicepIdentifier($"{appEnvResource.Name}_law"))
                {
                    Sku = new() { Name = OperationalInsightsWorkspaceSkuName.PerGB2018 },
                    Tags = tags
                };
            }

            infra.Add(laWorkspace);

            var containerAppEnvironment = new ContainerAppManagedEnvironment(appEnvResource.GetBicepIdentifier())
            {
                WorkloadProfiles = [
                    new ContainerAppWorkloadProfile()
                    {
                        WorkloadProfileType = "Consumption",
                        Name = "consumption"
                    }
                ],
                AppLogsConfiguration = new()
                {
                    Destination = "log-analytics",
                    LogAnalyticsConfiguration = new()
                    {
                        CustomerId = laWorkspace.CustomerId,
                        SharedKey = laWorkspace.GetKeys().PrimarySharedKey
                    }
                },
                Tags = tags
            };

            // Configure VNet integration if a subnet is specified
            if (appEnvResource.TryGetLastAnnotation<DelegatedSubnetAnnotation>(out var subnetAnnotation))
            {
                containerAppEnvironment.VnetConfiguration = new ContainerAppVnetConfiguration
                {
                    InfrastructureSubnetId = subnetAnnotation.SubnetId.AsProvisioningParameter(infra)
                };
            }

            infra.Add(containerAppEnvironment);

            if (appEnvResource.EnableDashboard)
            {
                var dashboard = new ContainerAppEnvironmentDotnetComponentResource("aspireDashboard", "2024-10-02-preview")
                {
                    Name = "aspire-dashboard",
                    ComponentType = "AspireDashboard",
                    Parent = containerAppEnvironment
                };

                infra.Add(dashboard);
            }

            var managedStorages = new Dictionary<string, ContainerAppManagedEnvironmentStorage>();

            var resource = (AzureContainerAppEnvironmentResource)infra.AspireResource;

            StorageAccount? storageVolume = null;
            if (resource.VolumeNames.Count > 0)
            {
                storageVolume = new StorageAccount(Infrastructure.NormalizeBicepIdentifier($"{appEnvResource.Name}_storageVolume"))
                {
                    Tags = tags,
                    Sku = new StorageSku() { Name = StorageSkuName.StandardLrs },
                    Kind = StorageKind.StorageV2,
                    LargeFileSharesState = LargeFileSharesState.Enabled,
                    MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2,
                };

                infra.Add(storageVolume);

                var storageVolumeFileService = new FileService("storageVolumeFileService")
                {
                    Parent = storageVolume
                };

                infra.Add(storageVolumeFileService);

                foreach (var (outputName, output) in resource.VolumeNames)
                {
                    var shareName = Infrastructure.NormalizeBicepIdentifier($"shares_{outputName}");
                    var managedStorageName = Infrastructure.NormalizeBicepIdentifier($"managedStorage_{outputName}");

                    var share = new FileShare(shareName)
                    {
                        Parent = storageVolumeFileService,
                        ShareQuota = 1024,
                        EnabledProtocol = FileShareEnabledProtocol.Smb
                    };

                    infra.Add(share);

                    var keysExpr = storageVolume.GetKeys()[0].Compile();
                    var keyValue = new MemberExpression(keysExpr, "value");

                    var containerAppStorage = new ContainerAppManagedEnvironmentStorage(managedStorageName)
                    {
                        Parent = containerAppEnvironment,
                        ManagedEnvironmentStorageAzureFile = new()
                        {
                            ShareName = share.Name,
                            AccountName = storageVolume.Name,
                            AccountKey = keyValue,
                            AccessMode = ContainerAppAccessMode.ReadWrite
                        }
                    };

                    infra.Add(containerAppStorage);

                    managedStorages[outputName] = containerAppStorage;

                    if (appEnvResource.UseAzdNamingConvention)
                    {
                        var volumeName = output.volume.Type switch
                        {
                            ContainerMountType.BindMount => $"bm{output.index}",
                            ContainerMountType.Volume => output.volume.Source ?? $"v{output.index}",
                            _ => throw new NotSupportedException()
                        };

                        // Remove '.' and '-' characters from volumeName
                        volumeName = volumeName.Replace(".", "").Replace("-", "");

                        share.Name = BicepFunction.Take(
                            BicepFunction.Interpolate(
                                $"{BicepFunction.ToLower(output.resource.Name)}-{BicepFunction.ToLower(volumeName)}"),
                            60);

                        containerAppStorage.Name = BicepFunction.Take(
                            BicepFunction.Interpolate(
                                $"{BicepFunction.ToLower(output.resource.Name)}-{BicepFunction.ToLower(volumeName)}"),
                            32);
                    }
                    else if (appEnvResource.UseCompactResourceNaming)
                    {
                        Debug.Assert(resourceToken is not null);

                        var volumeName = output.volume.Type switch
                        {
                            ContainerMountType.BindMount => $"bm{output.index}",
                            ContainerMountType.Volume => output.volume.Source ?? $"v{output.index}",
                            _ => throw new NotSupportedException()
                        };

                        // Remove '.' and '-' characters from volumeName
                        volumeName = volumeName.Replace(".", "").Replace("-", "");

                        share.Name = BicepFunction.Take(
                            BicepFunction.Interpolate(
                                $"{BicepFunction.ToLower(output.resource.Name)}-{BicepFunction.ToLower(volumeName)}"),
                            60);

                        containerAppStorage.Name = BicepFunction.Take(
                            BicepFunction.Interpolate(
                                $"{BicepFunction.ToLower(output.resource.Name)}-{BicepFunction.ToLower(volumeName)}-{resourceToken}"),
                            32);
                    }
                }
            }

            // Add the volume outputs to the container app environment storage
            foreach (var (key, value) in managedStorages)
            {
                infra.Add(new ProvisioningOutput(key, typeof(string))
                {
                    // use an expression here in case the resource's Name was set to a function expression above
                    Value = new MemberExpression(new IdentifierExpression(value.BicepIdentifier), "name")
                });
            }

            if (appEnvResource.UseAzdNamingConvention)
            {
                Debug.Assert(resourceToken is not null);

                identity.Name = BicepFunction.Interpolate($"mi-{resourceToken}");
                containerRegistry.Name = new FunctionCallExpression(
                    new IdentifierExpression("replace"),
                    new InterpolatedStringExpression([
                        new StringLiteralExpression("acr-"),
                        new IdentifierExpression(resourceToken.BicepIdentifier)
                    ]),
                    new StringLiteralExpression("-"),
                    new StringLiteralExpression(""));
                laWorkspace.Name = BicepFunction.Interpolate($"law-{resourceToken}");
                containerAppEnvironment.Name = BicepFunction.Interpolate($"cae-{resourceToken}");

#pragma warning disable IDE0031 // Use null propagation (IDE0031)
                if (storageVolume is not null)
#pragma warning restore IDE0031
                {
                    storageVolume.Name = BicepFunction.Interpolate($"vol{resourceToken}");
                }
            }
            else if (appEnvResource.UseCompactResourceNaming)
            {
                Debug.Assert(resourceToken is not null);

                if (storageVolume is not null)
                {
                    // Sanitize env name for storage accounts: lowercase alphanumeric only.
                    // Reserve 2 chars for "sv" prefix + 13 for uniqueString = 15, leaving 9 for the env name.
                    var sanitizedPrefix = new string(appEnvResource.Name.ToLowerInvariant()
                        .Where(c => char.IsLetterOrDigit(c)).ToArray());
                    if (sanitizedPrefix.Length > 9)
                    {
                        sanitizedPrefix = sanitizedPrefix[..9];
                    }

                    storageVolume.Name = BicepFunction.Take(
                        BicepFunction.Interpolate($"{sanitizedPrefix}sv{resourceToken}"),
                        24);
                }
            }

            // Exposed so that callers reference the LA workspace in other bicep modules
            infra.Add(new ProvisioningOutput("AZURE_LOG_ANALYTICS_WORKSPACE_NAME", typeof(string))
            {
                Value = laWorkspace.Name.ToBicepExpression()
            });

            infra.Add(new ProvisioningOutput("AZURE_LOG_ANALYTICS_WORKSPACE_ID", typeof(string))
            {
                Value = laWorkspace.Id.ToBicepExpression()
            });

            // Required by the IContaineRegistry interface
            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_NAME", typeof(string))
            {
                Value = containerRegistry.Name.ToBicepExpression()
            });

            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_ENDPOINT", typeof(string))
            {
                Value = containerRegistry.LoginServer.ToBicepExpression()
            });

            // Required by the IAzureContainerRegistry interface
            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID", typeof(string))
            {
                Value = identity.Id.ToBicepExpression()
            });

            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_APPS_ENVIRONMENT_NAME", typeof(string))
            {
                Value = containerAppEnvironment.Name.ToBicepExpression()
            });

            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_APPS_ENVIRONMENT_ID", typeof(string))
            {
                Value = containerAppEnvironment.Id.ToBicepExpression()
            });

            // Required for azd to output the dashboard URL
            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN", typeof(string))
            {
                Value = containerAppEnvironment.DefaultDomain.ToBicepExpression()
            });
        });

        // Create the default container registry resource before creating the environment
        var registryName = $"{name}-acr";
        var defaultRegistry = CreateDefaultAzureContainerRegistry(builder, registryName, containerAppEnvResource);
        containerAppEnvResource.DefaultContainerRegistry = defaultRegistry;

        // Create the resource builder first, then attach the registry to avoid recreating builders
        var appEnvBuilder = builder.ExecutionContext.IsRunMode
            // HACK: We need to return a valid resource builder for the container app environment
            // but in run mode, we don't want to add the resource to the builder.
            ? builder.CreateResourceBuilder(containerAppEnvResource)
            : builder.AddResource(containerAppEnvResource);

        return appEnvBuilder;
    }

    /// <summary>
    /// Configures the container app environment resources to use the same naming conventions as azd.
    /// </summary>
    /// <param name="builder">The AzureContainerAppEnvironmentResource to configure.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    /// <remarks>
    /// By default, the container app environment resources use a different naming convention than azd.
    ///
    /// This method allows for reusing the previously deployed resources if the application was deployed using
    /// azd without calling <see cref="AddAzureContainerAppEnvironment"/>
    /// </remarks>
    [AspireExport("withAzdResourceNaming", Description = "Configures resources to use azd naming conventions")]
    public static IResourceBuilder<AzureContainerAppEnvironmentResource> WithAzdResourceNaming(this IResourceBuilder<AzureContainerAppEnvironmentResource> builder)
    {
        builder.Resource.UseAzdNamingConvention = true;
        return builder;
    }

    /// <summary>
    /// Configures the container app environment to use compact resource naming that maximally preserves
    /// the <c>uniqueString</c> suffix for length-constrained Azure resources such as storage accounts.
    /// </summary>
    /// <param name="builder">The <see cref="AzureContainerAppEnvironmentResource"/> to configure.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// By default, the generated Azure resource names use long static suffixes (e.g. <c>storageVolume</c>,
    /// <c>managedStorage</c>) that can consume most of the 24-character storage account name limit, truncating
    /// the <c>uniqueString(resourceGroup().id)</c> portion that provides cross-deployment uniqueness.
    /// </para>
    /// <para>
    /// When enabled, this method shortens the static portions of generated names so the full 13-character
    /// <c>uniqueString</c> is preserved. This prevents naming collisions when deploying multiple environments
    /// to different resource groups.
    /// </para>
    /// <para>
    /// This option only affects volume-related storage resources. It does not change the naming of the
    /// container app environment, container registry, log analytics workspace, or managed identity.
    /// Use <see cref="WithAzdResourceNaming"/> to change those names as well.
    /// </para>
    /// </remarks>
    [AspireExport("withCompactResourceNaming", Description = "Configures resources to use compact naming for length-constrained Azure resources")]
    [Experimental("ASPIREACANAMING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureContainerAppEnvironmentResource> WithCompactResourceNaming(this IResourceBuilder<AzureContainerAppEnvironmentResource> builder)
    {
        builder.Resource.UseCompactResourceNaming = true;
        return builder;
    }

    /// <summary>
    /// Configures whether the Aspire dashboard should be included in the container app environment.
    /// </summary>
    /// <param name="builder">The AzureContainerAppEnvironmentResource to configure.</param>
    /// <param name="enable">Whether to include the Aspire dashboard. Default is true.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    [AspireExport("withDashboard", Description = "Configures whether the Aspire dashboard is included in the container app environment")]
    public static IResourceBuilder<AzureContainerAppEnvironmentResource> WithDashboard(this IResourceBuilder<AzureContainerAppEnvironmentResource> builder, bool enable = true)
    {
        builder.Resource.EnableDashboard = enable;
        return builder;
    }

    /// <summary>
    /// Configures whether HTTP endpoints should be upgraded to HTTPS in Azure Container Apps.
    /// By default, HTTP endpoints are upgraded to HTTPS for security and WebSocket compatibility.
    /// </summary>
    /// <param name="builder">The AzureContainerAppEnvironmentResource to configure.</param>
    /// <param name="upgrade">Whether to upgrade HTTP endpoints to HTTPS. Default is true.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    /// <remarks>
    /// When disabled (<c>false</c>), HTTP endpoints will use HTTP scheme and port 80 in Azure Container Apps.
    /// Note that explicit ports specified for development (e.g., port 8080) are still normalized
    /// to standard ports (80/443) as required by Azure Container Apps.
    /// </remarks>
    [AspireExport("withHttpsUpgrade", Description = "Configures whether HTTP endpoints are upgraded to HTTPS")]
    public static IResourceBuilder<AzureContainerAppEnvironmentResource> WithHttpsUpgrade(this IResourceBuilder<AzureContainerAppEnvironmentResource> builder, bool upgrade = true)
    {
        builder.Resource.PreserveHttpEndpoints = !upgrade;
        return builder;
    }

    /// <summary>
    /// Configures the container app environment resource to use the specified Log Analytics Workspace.
    /// </summary>
    /// <param name="builder">The AzureContainerAppEnvironmentResource to configure.</param>
    /// <param name="workspaceBuilder">The resource builder for the <see cref="AzureLogAnalyticsWorkspaceResource"/> to use.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="workspaceBuilder"/> is null.</exception>
    [AspireExport("withAzureLogAnalyticsWorkspace", Description = "Configures the container app environment to use a specific Log Analytics Workspace")]
    public static IResourceBuilder<AzureContainerAppEnvironmentResource> WithAzureLogAnalyticsWorkspace(this IResourceBuilder<AzureContainerAppEnvironmentResource> builder, IResourceBuilder<AzureLogAnalyticsWorkspaceResource> workspaceBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(workspaceBuilder);

        // Add a LogAnalyticsWorkspaceReferenceAnnotation to indicate that the resource is using a specific workspace
        builder.WithAnnotation(new AzureLogAnalyticsWorkspaceReferenceAnnotation(workspaceBuilder.Resource));

        return builder;
    }

    private static AzureContainerRegistryResource CreateDefaultAzureContainerRegistry(IDistributedApplicationBuilder builder, string name, AzureContainerAppEnvironmentResource containerAppEnvironment)
    {
        var configureInfrastructure = (AzureResourceInfrastructure infrastructure) =>
        {
            var registry = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, resourceName) =>
                {
                    var resource = ContainerRegistryService.FromExisting(identifier);
                    resource.Name = resourceName;
                    return resource;
                },
                (infra) =>
                {
                    var newRegistry = new ContainerRegistryService(infra.AspireResource.GetBicepIdentifier())
                    {
                        Sku = new ContainerRegistrySku { Name = ContainerRegistrySkuName.Basic },
                        Tags = { { "aspire-resource-name", infra.AspireResource.Name } }
                    };

                    if (containerAppEnvironment.UseAzdNamingConvention)
                    {
                        var resourceToken = new ProvisioningVariable("resourceToken", typeof(string))
                        {
                            Value = BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)
                        };
                        infrastructure.Add(resourceToken);

                        newRegistry.Name = new FunctionCallExpression(
                            new IdentifierExpression("replace"),
                            new InterpolatedStringExpression([
                                new StringLiteralExpression("acr-"),
                                new IdentifierExpression(resourceToken.BicepIdentifier)
                            ]),
                            new StringLiteralExpression("-"),
                            new StringLiteralExpression(""));
                    }

                    return newRegistry;
                });

            infrastructure.Add(registry);
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = registry.Name });
            infrastructure.Add(new ProvisioningOutput("loginServer", typeof(string)) { Value = registry.LoginServer });
        };

        var resource = new AzureContainerRegistryResource(name, configureInfrastructure);
        if (builder.ExecutionContext.IsPublishMode)
        {
            builder.AddResource(resource);
        }
        return resource;
    }
}
