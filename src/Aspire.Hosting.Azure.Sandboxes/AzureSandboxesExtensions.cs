// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREPIPELINES003 // Container build options are required by the sandbox deployment target.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Aspire.Hosting.Publishing;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Container Apps sandbox resources to the application model.
/// </summary>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class AzureSandboxesExtensions
{
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

        builder.AddAzureProvisioning();
        AzureSandboxCleanupResource.EnsureAdded(builder);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var sandboxResource = (AzureSandboxGroupResource)infrastructure.AspireResource;
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
                        Location = BicepFunction.GetResourceGroup().Location,
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    ApplyManagedServiceIdentity(resource.Identity, sandboxResource, infrastructure);
                    return resource;
                });

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = sandboxGroup.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = sandboxGroup.Name.ToBicepExpression() });

            if (!sandboxResource.IsExisting())
            {
                AddSandboxGroupDeploymentPrincipalRoleAssignment(infrastructure, sandboxGroup);
            }
        }

        var resource = new AzureSandboxGroupResource(name, ConfigureInfrastructure)
        {
            DefaultContainerRegistry = CreateDefaultAzureContainerRegistry(builder, $"{name}-acr")
        };

        return builder.AddResource(resource);
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
        where T : IResource, IComputeResource
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
            .WithContainerBuildOptions(static context =>
            {
                // ADC creates disk images from registry images. Buildx's default output can push an
                // OCI image index with provenance attestations, which ADC currently treats as a
                // ready disk image but boots without a usable root filesystem. Force a single
                // Docker-format linux/amd64 image for resources published to sandboxes.
                context.Destination = ContainerImageDestination.Registry;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            })
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
        where T : IResource, IComputeResource
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
    /// Configures the Azure sandbox group to use no managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithNoManagedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.None;
        builder.Resource.UserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Configures the Azure sandbox group to use a system-assigned managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithSystemAssignedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.SystemAssigned;
        builder.Resource.UserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Configures the Azure sandbox group to use a user-assigned managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <param name="identity">The user-assigned managed identity resource.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithUserAssignedIdentity(
        this IResourceBuilder<AzureSandboxGroupResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.UserAssigned;
        builder.Resource.UserAssignedIdentities.Add(identity.Resource);
        return builder;
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
            AutoDeleteIntervalInDays = options.AutoDeleteIntervalInDays,
            AutoDeleteIntervalInSeconds = options.AutoDeleteIntervalInSeconds,
            AutoDeleteTrigger = options.AutoDeleteTrigger,
            PublicEndpointReadyTimeoutSeconds = options.PublicEndpointReadyTimeoutSeconds,
            Endpoints = options.Endpoints?.Select(static endpoint => new AzureSandboxEndpointOptions
            {
                Name = endpoint.Name,
                Anonymous = endpoint.Anonymous
            }).ToArray()
        };
    }

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

        ValidateOptionalNonNegative(options.AutoSuspendInterval, nameof(AzureSandboxOptions.AutoSuspendInterval));
        ValidateOptionalAllowedValue(options.AutoSuspendMode, nameof(AzureSandboxOptions.AutoSuspendMode), "Memory", "Disk", "None");
        ValidateOptionalNonNegative(options.AutoDeleteIntervalInDays, nameof(AzureSandboxOptions.AutoDeleteIntervalInDays));
        ValidateOptionalNonNegative(options.AutoDeleteIntervalInSeconds, nameof(AzureSandboxOptions.AutoDeleteIntervalInSeconds));
        ValidateOptionalAllowedValue(options.AutoDeleteTrigger, nameof(AzureSandboxOptions.AutoDeleteTrigger), "AfterSuspend", "AfterCreation");
        ValidateOptionalPositive(options.PublicEndpointReadyTimeoutSeconds, nameof(AzureSandboxOptions.PublicEndpointReadyTimeoutSeconds));

        if (options.Endpoints is null)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
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

    private static void ValidateOptionalPositive(int? value, string paramName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value must be positive.");
        }
    }

    private static void ValidateOptionalNonNegative(int? value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value cannot be negative.");
        }
    }

    private static void ValidateOptionalNonNegative(long? value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value cannot be negative.");
        }
    }

    private static void ValidateOptionalAllowedValue(string? value, string paramName, params string[] allowedValues)
    {
        if (value is null)
        {
            return;
        }

        foreach (var allowedValue in allowedValues)
        {
            if (string.Equals(value, allowedValue, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new ArgumentException($"The value '{value}' is not supported. Supported values: {string.Join(", ", allowedValues)}.", paramName);
    }

    private static void ApplyManagedServiceIdentity(ManagedServiceIdentity identity, AzureSandboxGroupResource resource, AzureResourceInfrastructure infrastructure)
    {
        if (resource.ManagedIdentityType == ManagedServiceIdentityType.None && resource.UserAssignedIdentities.Count == 0)
        {
            return;
        }

        identity.ManagedServiceIdentityType = resource.ManagedIdentityType;

        foreach (var userAssignedIdentity in resource.UserAssignedIdentities)
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
