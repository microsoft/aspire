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
            infrastructure.Add(new ProvisioningOutput("location", typeof(string)) { Value = sandboxGroup.Location.ToBicepExpression() });

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
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sandboxGroup);

        var sandboxOptions = options ?? new AzureSandboxOptions();
        ValidateSandboxOptions(sandboxOptions);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

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
        if (!builder.Resource.UserAssignedIdentities.Contains(identity.Resource))
        {
            builder.Resource.UserAssignedIdentities.Add(identity.Resource);
        }
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
