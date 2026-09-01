// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREAZURE001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Container Apps sandbox resources to the application model.
/// </summary>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class AzureSandboxesExtensions
{
    // https://learn.microsoft.com/azure/role-based-access-control/built-in-roles#container-apps-sandboxgroup-data-owner
    private const string SandboxGroupDataOwnerRoleId = "c24cf47c-5077-412d-a19c-45202126392c";

    /// <summary>
    /// Adds an Azure Container Apps sandbox group resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A resource builder for the sandbox group.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> AddAzureSandboxGroup(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var sandboxResource = (AzureSandboxGroupResource)infrastructure.AspireResource;
            UserAssignedIdentity? newImagePullIdentity = null;
            BicepValue<string> imagePullIdentityId;
            BicepValue<string> imagePullIdentityClientId;
            if (sandboxResource.TryGetLastAnnotation<AzureSandboxGroupAcrPullIdentityAnnotation>(out var imagePullIdentityAnnotation))
            {
                if (sandboxResource.IsExisting() &&
                    (imagePullIdentityAnnotation.IsAspireManaged || !imagePullIdentityAnnotation.Identity.IsExisting()))
                {
                    throw CreateExistingSandboxGroupMissingAcrPullIdentityException(sandboxResource);
                }

                imagePullIdentityId = imagePullIdentityAnnotation.Identity.Id.AsProvisioningParameter(infrastructure);
                imagePullIdentityClientId = imagePullIdentityAnnotation.Identity.ClientId.AsProvisioningParameter(infrastructure);
            }
            else
            {
                if (sandboxResource.IsExisting())
                {
                    throw CreateExistingSandboxGroupMissingAcrPullIdentityException(sandboxResource);
                }

                newImagePullIdentity = new UserAssignedIdentity(
                    Infrastructure.NormalizeBicepIdentifier($"{sandboxResource.Name}_mi"));
                infrastructure.Add(newImagePullIdentity);
                imagePullIdentityId = newImagePullIdentity.Id.ToBicepExpression();
                imagePullIdentityClientId = newImagePullIdentity.ClientId.ToBicepExpression();
            }

            var sandboxGroup = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = SandboxGroup.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                infrastructure =>
                {
                    var resource = new SandboxGroup(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        Properties = [],
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    ApplyManagedServiceIdentity(resource.Identity, sandboxResource, imagePullIdentityId, infrastructure);
                    return resource;
                });

            if (newImagePullIdentity is not null)
            {
                var registry = sandboxResource.ContainerRegistry ??
                    throw new InvalidOperationException($"No container registry associated with Azure sandbox group '{sandboxResource.Name}'. This should have been added automatically.");
                var containerRegistry = (ContainerRegistryService)registry.AddAsExistingResource(infrastructure);
                infrastructure.Add(containerRegistry);
                var pullRoleAssignment = containerRegistry.CreateRoleAssignment(
                    ContainerRegistryBuiltInRole.AcrPull,
                    newImagePullIdentity);
                // Azure.Provisioning does not currently generate a stable role-assignment name.
                // See https://github.com/Azure/azure-sdk-for-net/issues/47265.
                pullRoleAssignment.Name = BicepFunction.CreateGuid(
                    containerRegistry.Id,
                    newImagePullIdentity.Id,
                    pullRoleAssignment.RoleDefinitionId);
                infrastructure.Add(pullRoleAssignment);
            }

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = sandboxGroup.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = sandboxGroup.Name.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("location", typeof(string)) { Value = sandboxGroup.Location.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput(AzureSandboxGroupResource.ImagePullIdentityClientIdOutputName, typeof(string))
            {
                Value = imagePullIdentityClientId
            });

            if (!sandboxResource.IsExisting())
            {
                AddSandboxGroupDeploymentPrincipalRoleAssignment(infrastructure, sandboxGroup);
            }
        }

        var resource = new AzureSandboxGroupResource(name, ConfigureInfrastructure);
        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        builder.AddAzureProvisioning();
        AzureSandboxCleanupResource.EnsureAdded(builder);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);
        resource.DefaultContainerRegistry = CreateDefaultAzureContainerRegistry(builder, $"{name}-acr");
        var resourceBuilder = builder.AddResource(resource);
        resourceBuilder.WithCrossScopeAcrPullIdentity(
            identity => new AzureSandboxGroupAcrPullIdentityAnnotation(identity, isAspireManaged: true));
        return resourceBuilder;
    }

    /// <summary>
    /// Publishes the specified compute resource as an Azure sandbox container.
    /// </summary>
    /// <typeparam name="T">The compute resource type.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="sandboxGroup">The Azure sandbox group that hosts the resource.</param>
    /// <param name="options">The sandbox runtime options.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="sandboxGroup"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a configured option is invalid.</exception>
    /// <remarks>
    /// This method assigns the compute resource to <paramref name="sandboxGroup"/> and configures all sandbox-specific
    /// runtime options in one call.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("publishComputeResourceAsAzureSandbox", MethodName = "publishAsAzureSandbox")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> PublishAsAzureSandbox<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> sandboxGroup,
        AzureSandboxOptions? options = null)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sandboxGroup);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var sandboxOptions = options ?? new AzureSandboxOptions();
        ValidateSandboxOptions(sandboxOptions);

        var copiedOptions = CopyAzureSandboxOptions(sandboxOptions);

        return builder
            .WithComputeEnvironment(sandboxGroup)
            .WithAnnotation(new AzureSandboxContainerOptionsAnnotation(copiedOptions), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Publishes the specified compute resource as an Azure sandbox container.
    /// </summary>
    /// <typeparam name="T">The compute resource type.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="sandboxGroup">The Azure sandbox group that hosts the resource.</param>
    /// <param name="configure">The callback that configures sandbox runtime options.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/>, <paramref name="sandboxGroup"/>, or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a configured option is invalid.</exception>
    [AspireExportIgnore(Reason = "Use the AzureSandboxOptions overload from ATS.")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> PublishAsAzureSandbox<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> sandboxGroup,
        Action<AzureSandboxOptions> configure)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sandboxGroup);
        ArgumentNullException.ThrowIfNull(configure);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var options = new AzureSandboxOptions();
        configure(options);

        return builder.PublishAsAzureSandbox(sandboxGroup, options);
    }

    /// <summary>
    /// Clears the managed identities explicitly configured for sandbox workloads.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Workload identities requested by individual compute resources are added to the sandbox group during
    /// deployment preparation. Image pulls require a separate user-assigned identity. Aspire creates one by
    /// default for new groups; callers can select one with <c>WithAcrPullIdentity</c> and must do so for existing groups.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithNoManagedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.WorkloadManagedIdentityType = ManagedServiceIdentityType.None;
        builder.Resource.WorkloadUserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Configures a system-assigned managed identity for sandbox workloads.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Image pulls require a separate user-assigned identity. Aspire creates one by default for new groups;
    /// callers can select one with <c>WithAcrPullIdentity</c> and must do so for existing groups. Workload identities
    /// requested by individual compute resources may add more user-assigned identities.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithSystemAssignedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.WorkloadManagedIdentityType = ManagedServiceIdentityType.SystemAssigned;
        builder.Resource.WorkloadUserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Adds a user-assigned managed identity for sandbox workloads.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <param name="identity">The user-assigned managed identity resource.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Image pulls require a separate user-assigned identity. Aspire creates one by default for new groups;
    /// callers can select one with <c>WithAcrPullIdentity</c> and must do so for existing groups.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithUserAssignedIdentity(
        this IResourceBuilder<AzureSandboxGroupResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);

        builder.Resource.AddWorkloadUserAssignedIdentity(identity.Resource);
        return builder;
    }

    /// <summary>
    /// Configures the user-assigned managed identity that Azure Dev Compute uses to pull sandbox images
    /// from the configured Azure Container Registry.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <param name="identity">The user-assigned managed identity resource.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// The supplied identity must be attached to an existing sandbox group and have the <c>AcrPull</c>
    /// role on its registry. For sandbox groups created by Aspire, the identity is attached automatically,
    /// but the caller remains responsible for granting <c>AcrPull</c>.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithAcrPullIdentity(
        this IResourceBuilder<AzureSandboxGroupResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);

        builder.Resource.References.Add(identity.Resource);
        return builder.WithAnnotation(
            new AzureSandboxGroupAcrPullIdentityAnnotation(identity.Resource),
            ResourceAnnotationMutationBehavior.Replace);
    }

    private static AzureSandboxOptions CopyAzureSandboxOptions(AzureSandboxOptions options)
    {
        return new AzureSandboxOptions
        {
            Tier = options.Tier,
            AutoSuspendEnabled = options.AutoSuspendEnabled,
            AutoSuspendInterval = options.AutoSuspendInterval,
            AutoSuspendMode = options.AutoSuspendMode,
            AutoDeleteEnabled = options.AutoDeleteEnabled,
            AutoDeleteInterval = options.AutoDeleteInterval,
            AutoDeleteTrigger = options.AutoDeleteTrigger,
            PublicEndpointReadyTimeout = options.PublicEndpointReadyTimeout,
            Endpoints = options.Endpoints?.Select(static endpoint => new AzureSandboxEndpointOptions
            {
                Name = endpoint.Name,
                Anonymous = endpoint.Anonymous
            }).ToArray()
        };
    }

    private static InvalidOperationException CreateExistingSandboxGroupMissingAcrPullIdentityException(
        AzureSandboxGroupResource sandboxGroup)
        => new(
            $"Existing Azure sandbox group '{sandboxGroup.Name}' requires a user-assigned ACR pull identity. " +
            $"Call '{nameof(WithAcrPullIdentity)}' with an identity that is already attached to the sandbox group and has AcrPull on the configured registry.");

    private static void AddSandboxGroupDeploymentPrincipalRoleAssignment(AzureResourceInfrastructure infrastructure, SandboxGroup sandboxGroup)
    {
        var principalId = new ProvisioningParameter(AzureBicepResource.KnownParameters.UserPrincipalId, typeof(Guid));
        var principalType = new ProvisioningParameter(AzureBicepResource.KnownParameters.PrincipalType, typeof(string));
        infrastructure.Add(principalId);
        infrastructure.Add(principalType);

        // Sandbox deployment creates disk images, sandboxes, lifecycle settings, and public
        // ports through the Azure Dev Compute data-plane API after the sandbox group ARM
        // resource is provisioned. Model the deployment-principal grant in the sandbox
        // group's own Azure.Provisioning module, just like other Azure deployment targets
        // model environment-owned RBAC in their environment resource. The publish pipeline
        // wires these well-known principal parameters from the outer Azure environment,
        // while direct `aspire deploy` fills it from the current Azure principal.
        // https://learn.microsoft.com/azure/templates/microsoft.authorization/2022-04-01/roleassignments
        infrastructure.Add(new RoleAssignment($"{sandboxGroup.BicepIdentifier}_deploymentPrincipalDataOwner")
        {
            Name = BicepFunction.CreateGuid(
                sandboxGroup.Id,
                principalId,
                BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", SandboxGroupDataOwnerRoleId)),
            Scope = new IdentifierExpression(sandboxGroup.BicepIdentifier),
            PrincipalType = principalType,
            PrincipalId = principalId,
            RoleDefinitionId = BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", SandboxGroupDataOwnerRoleId)
        });
    }

    private static void ValidateSandboxOptions(AzureSandboxOptions options)
    {
        if (!Enum.IsDefined(options.Tier))
        {
            throw new ArgumentException($"'{options.Tier}' is not a valid Azure sandbox tier.", nameof(options));
        }

        ValidateOptionalWholeSecondDuration(
            options.AutoSuspendInterval,
            nameof(AzureSandboxOptions.AutoSuspendInterval),
            TimeSpan.FromSeconds(int.MaxValue));
        ValidateOptionalEnum(options.AutoSuspendMode, nameof(AzureSandboxOptions.AutoSuspendMode));
        ValidateOptionalWholeSecondDuration(options.AutoDeleteInterval, nameof(AzureSandboxOptions.AutoDeleteInterval));
        ValidateOptionalEnum(options.AutoDeleteTrigger, nameof(AzureSandboxOptions.AutoDeleteTrigger));
        ValidateOptionalPositiveDuration(
            options.PublicEndpointReadyTimeout,
            nameof(AzureSandboxOptions.PublicEndpointReadyTimeout),
            TimeSpan.FromSeconds(int.MaxValue));

        if (options.AutoSuspendEnabled is null &&
            (options.AutoSuspendInterval is not null || options.AutoSuspendMode is not null))
        {
            throw new ArgumentException(
                $"{nameof(AzureSandboxOptions.AutoSuspendEnabled)} must be set when configuring auto-suspend interval or mode.",
                nameof(options));
        }

        if (options.AutoDeleteEnabled is null &&
            (options.AutoDeleteInterval is not null || options.AutoDeleteTrigger is not null))
        {
            throw new ArgumentException(
                $"{nameof(AzureSandboxOptions.AutoDeleteEnabled)} must be set when configuring auto-delete interval or trigger.",
                nameof(options));
        }

        if (options.Endpoints is null)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in options.Endpoints)
        {
            if (endpoint is null)
            {
                throw new ArgumentException("Endpoint options cannot contain null values.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(endpoint.Name))
            {
                throw new ArgumentException("Endpoint option names cannot be empty.", nameof(options));
            }

            if (!names.Add(endpoint.Name))
            {
                throw new ArgumentException($"Endpoint option '{endpoint.Name}' is configured more than once.", nameof(options));
            }
        }
    }

    private static void ValidateOptionalWholeSecondDuration(TimeSpan? value, string paramName, TimeSpan? maximum = null)
    {
        if (value is null)
        {
            return;
        }

        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value cannot be negative.");
        }

        if (value.Value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("The value must use whole-second precision.", paramName);
        }

        if (maximum is not null && value > maximum)
        {
            throw new ArgumentOutOfRangeException(paramName, $"The value cannot exceed {maximum}.");
        }
    }

    private static void ValidateOptionalPositiveDuration(TimeSpan? value, string paramName, TimeSpan? maximum = null)
    {
        if (value is not null && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value must be positive.");
        }

        if (maximum is not null && value > maximum)
        {
            throw new ArgumentOutOfRangeException(paramName, $"The value cannot exceed {maximum}.");
        }
    }

    private static void ValidateOptionalEnum<TEnum>(TEnum? value, string paramName)
        where TEnum : struct, Enum
    {
        if (value is not null && !Enum.IsDefined(value.Value))
        {
            throw new ArgumentException($"'{value}' is not a valid {typeof(TEnum).Name} value.", paramName);
        }
    }

    private static void ApplyManagedServiceIdentity(
        ManagedServiceIdentity identity,
        AzureSandboxGroupResource resource,
        BicepValue<string> imagePullIdentityId,
        AzureResourceInfrastructure infrastructure)
    {
        identity.ManagedServiceIdentityType = resource.WorkloadManagedIdentityType switch
        {
            ManagedServiceIdentityType.None => ManagedServiceIdentityType.UserAssigned,
            ManagedServiceIdentityType.SystemAssigned => ManagedServiceIdentityType.SystemAssignedUserAssigned,
            _ => resource.WorkloadManagedIdentityType
        };
        var imagePullIdentityKey = BicepFunction.Interpolate($"{imagePullIdentityId}").Compile().ToString();
        identity.UserAssignedIdentities[imagePullIdentityKey] = new UserAssignedIdentityDetails();

        foreach (var userAssignedIdentity in resource.WorkloadUserAssignedIdentities)
        {
            var userAssignedIdentityIdParameter = userAssignedIdentity.Id.AsProvisioningParameter(infrastructure);
            var userAssignedIdentityId = BicepFunction.Interpolate($"{userAssignedIdentityIdParameter}").Compile().ToString();
            identity.UserAssignedIdentities[userAssignedIdentityId] = new UserAssignedIdentityDetails();
        }
    }

    private static AzureContainerRegistryResource CreateDefaultAzureContainerRegistry(IDistributedApplicationBuilder builder, string name)
    {
        var resource = new AzureContainerRegistryResource(name, ContainerRegistryInfrastructure.ConfigureContainerRegistry);
        if (builder.ExecutionContext.IsPublishMode)
        {
            builder.AddResource(resource)
                .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
        }

        return resource;
    }
}
