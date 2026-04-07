// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Prepares Azure resources for provisioning and publish.
///
/// This includes preparing role assignment annotations for Azure resources.
/// </summary>
internal sealed class AzureResourcePreparer(
    IOptions<AzureProvisioningOptions> options,
    DistributedApplicationExecutionContext executionContext) : IDistributedApplicationEventingSubscriber
{
    public async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken)
    {
        var azureResources = GetAzureResourcesFromAppModel(@event.Model);
        if (azureResources.Count == 0)
        {
            return;
        }

        if (!EnvironmentSupportsIdentitiesAndAssignments())
        {
            // If the app infrastructure does not support targeted identities and role assignments, then we need to ensure that
            // there are no identity or role assignment annotations in the app model because they won't be honored otherwise.
            EnsureNoIdentityOrRoleAssignmentAnnotations(@event.Model);
        }

        await BuildRoleAssignmentAnnotations(@event.Model, azureResources, cancellationToken).ConfigureAwait(false);

        // set the ProvisioningBuildOptions on the resource, if necessary
        foreach (var r in azureResources)
        {
            if (r.AzureResource is AzureProvisioningResource provisioningResource)
            {
                provisioningResource.ProvisioningBuildOptions = options.Value.ProvisioningBuildOptions;
            }
        }
    }

    internal static List<(IResource Resource, IAzureResource AzureResource)> GetAzureResourcesFromAppModel(DistributedApplicationModel appModel)
    {
        // Some resources do not derive from IAzureResource but can be handled
        // by the Azure provisioner because they have the AzureBicepResourceAnnotation
        // which holds a reference to the surrogate AzureBicepResource which implements
        // IAzureResource and can be used by the Azure Bicep Provisioner.

        var azureResources = new List<(IResource, IAzureResource)>();
        foreach (var resource in appModel.Resources)
        {
            if (resource.IsExcludedFromPublish() || resource.IsContainer() || resource.IsEmulator())
            {
                continue;
            }
            else if (resource is IAzureResource azureResource)
            {
                // If we are dealing with an Azure resource then we just return it.
                azureResources.Add((resource, azureResource));
            }
            else if (resource.Annotations.OfType<AzureBicepResourceAnnotation>().SingleOrDefault() is { } annotation)
            {
                // If we aren't an Azure resource and there is no surrogate, return null for
                // the Azure resource in the tuple (we'll filter it out later.
                azureResources.Add((resource, annotation.Resource));
            }
        }

        return azureResources;
    }

    private bool EnvironmentSupportsIdentitiesAndAssignments()
    {
        // run mode always supports targeted role assignments
        // publish mode only supports targeted role assignments if the environment supports it
        return executionContext.IsRunMode || options.Value.SupportsTargetedRoleAssignments;
    }

    private static void EnsureNoIdentityOrRoleAssignmentAnnotations(DistributedApplicationModel appModel)
    {
        foreach (var resource in appModel.Resources)
        {
            if (resource.HasAnnotationOfType<RoleAssignmentAnnotation>())
            {
                throw new InvalidOperationException("The application model does not support role assignments. Ensure you are using an environment that supports role assignments, for example AddAzureContainerAppEnvironment.");
            }

            if (resource.HasAnnotationOfType<AppIdentityAnnotation>())
            {
                throw new InvalidOperationException("The application model does not support using explicit managed identities. Ensure you are using an environment that supports managed identities, for example AddAzureContainerAppEnvironment.");
            }
        }
    }

    private async Task BuildRoleAssignmentAnnotations(DistributedApplicationModel appModel, List<(IResource Resource, IAzureResource AzureResource)> azureResources, CancellationToken cancellationToken)
    {
        var globalRoleAssignments = new Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>>();

        if (!EnvironmentSupportsIdentitiesAndAssignments())
        {
            // when the app infrastructure doesn't support targeted role assignments, just copy all the default role assignments to applied role assignments
            foreach (var resource in azureResources.Select(r => r.AzureResource).OfType<AzureProvisioningResource>())
            {
                if (resource.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaultRoleAssignments))
                {
                    AppendGlobalRoleAssignments(globalRoleAssignments, resource, defaultRoleAssignments.Roles);
                }
            }
        }
        else
        {
            // when the app infrastructure supports targeted role assignments, walk the resource graph and
            // - if in RunMode
            //   - if a compute resource has RoleAssignmentAnnotations, add them to globalRoleAssignments on the referenced Azure resource
            //   - if the resource doesn't, copy the DefaultRoleAssignments to globalRoleAssignments
            //
            // - if in PublishMode
            //   - if a compute resource has RoleAssignmentAnnotations, use them
            //   - if the resource doesn't, copy the DefaultRoleAssignments to RoleAssignmentAnnotations to apply the defaults
            var resourceSnapshot = appModel.GetComputeResources()
                .Concat(appModel.Resources
                    .OfType<AzureUserAssignedIdentityResource>()
                    .Where(r => !r.IsExcludedFromPublish()))
                .ToArray(); // avoid modifying the collection while iterating
            foreach (var resource in resourceSnapshot)
            {
                var prerequisiteResources = new HashSet<AzureBicepResource>();
                var azureReferences = await GetAzureReferences(resource, cancellationToken).ConfigureAwait(false);

                var azureReferencesWithRoleAssignments =
                    (resource.TryGetAnnotationsOfType<RoleAssignmentAnnotation>(out var annotations)
                        ? annotations
                        : [])
                        .ToLookup(a => a.Target);
                foreach (var azureReference in azureReferences.OfType<AzureProvisioningResource>())
                {
                    if (azureReference.IsContainer() || azureReference.IsEmulator())
                    {
                        // Skip emulators
                        continue;
                    }

                    var roleAssignments = azureReferencesWithRoleAssignments[azureReference];
                    if (roleAssignments.Any())
                    {
                        if (executionContext.IsRunMode)
                        {
                            // in RunMode, we need to add the role assignments to the resource
                            AppendGlobalRoleAssignments(globalRoleAssignments, azureReference, roleAssignments.SelectMany(a => a.Roles));
                        }
                        // in PublishMode, this is a no-op since GetAllRoleAssignments will handle the role assignments
                    }
                    else if (azureReference.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaults))
                    {
                        if (executionContext.IsRunMode)
                        {
                            // in RunMode, we copy the default role assignments to the Azure reference,
                            // even if the roles are empty, since empty roles are used by some resources - like databases
                            AppendGlobalRoleAssignments(globalRoleAssignments, azureReference, defaults.Roles);
                        }
                        else
                        {
                            // in PublishMode, we copy the default role assignments to the compute resource
                            resource.Annotations.Add(new RoleAssignmentAnnotation(azureReference, defaults.Roles));
                        }
                    }

                    // Find private endpoints that target Azure resources referenced by this compute resource.
                    // These must be provisioned before the compute resource is deployed.
                    if (azureReference.TryGetAnnotationsOfType<PrivateEndpointTargetAnnotation>(out var peAnnotations))
                    {
                        foreach (var peAnnotation in peAnnotations)
                        {
                            prerequisiteResources.Add(peAnnotation.PrivateEndpointResource);
                        }
                    }
                }

                // in PublishMode with SupportsTargetedRoleAssignments, we need to create the identity and role assignment resources
                // if the resource references any Azure resources, or has role assignments to Azure resources
                if (executionContext.IsPublishMode)
                {
                    var roleAssignments = GetAllRoleAssignments(resource);
                    if (roleAssignments.Count > 0)
                    {
                        var (identityResource, roleAssignmentResources) = CreateIdentityAndRoleAssignmentResources(resource, roleAssignments);

                        if (resource != identityResource)
                        {
                            // Only add the AppIdentityAnnotation if the resource doesn't already have one
                            if (!resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var existingAppIdentityAnnotation) ||
                                existingAppIdentityAnnotation.IdentityResource != identityResource)
                            {
                                resource.Annotations.Add(new AppIdentityAnnotation(identityResource));
                            }

                            // add the identity resource to the resource collection so it can be provisioned
                            // but only if it's not already there
                            if (!appModel.Resources.Contains(identityResource))
                            {
                                appModel.Resources.Add(identityResource);
                            }
                        }

                        foreach (var roleAssignmentResource in roleAssignmentResources)
                        {
                            appModel.Resources.Add(roleAssignmentResource);
                            prerequisiteResources.Add(roleAssignmentResource);
                        }
                    }
                }

                // Add prerequisite infrastructure resources on the compute resource.
                // Deployment infrastructure subscribers will transfer these to deployment target References
                // so AzureBicepResource dependency wiring can apply provision ordering.
                if (prerequisiteResources.Count > 0)
                {
                    resource.Annotations.Add(new DeploymentPrerequisitesAnnotation(prerequisiteResources));
                }
            }

            if (executionContext.IsRunMode)
            {
                // in RunMode, any Azure resources that are not referenced by a compute resource should have their default role assignments applied
                foreach (var azureResource in azureResources.Select(r => r.AzureResource).OfType<AzureProvisioningResource>())
                {
                    if (!globalRoleAssignments.TryGetValue(azureResource, out _) &&
                        azureResource.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaultRoleAssignments))
                    {
                        AppendGlobalRoleAssignments(globalRoleAssignments, azureResource, defaultRoleAssignments.Roles);
                    }
                }
            }
        }

        if (globalRoleAssignments.Count > 0)
        {
            CreateGlobalRoleAssignments(appModel, globalRoleAssignments);
        }
    }

    private static Dictionary<AzureProvisioningResource, IEnumerable<RoleDefinition>> GetAllRoleAssignments(IResource resource)
    {
        var result = new Dictionary<AzureProvisioningResource, IEnumerable<RoleDefinition>>();
        if (resource.TryGetAnnotationsOfType<RoleAssignmentAnnotation>(out var roleAssignments))
        {
            foreach (var g in roleAssignments.GroupBy(r => r.Target))
            {
                result[g.Key] = g.SelectMany(r => r.Roles);
            }
        }
        return result;
    }

    private (AzureUserAssignedIdentityResource IdentityResource, List<AzureBicepResource> RoleAssignmentResources) CreateIdentityAndRoleAssignmentResources(
        IResource resource,
        Dictionary<AzureProvisioningResource, IEnumerable<RoleDefinition>> roleAssignments)
    {
        AzureUserAssignedIdentityResource identityResource;

        // If we're currently targeting an AzureUserAssignedIdentityResource, we can use it as the identity resource
        // for the role assignments. If we are targeting a compute resource that has an AppIdentityAnnotation, we can
        // use the identity resource from that annotation. Otherwise, create a new identity resource to use for role assignments.
        if (resource is AzureUserAssignedIdentityResource existingIdentityResource)
        {
            identityResource = existingIdentityResource;
        }
        else if (resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentityAnnotation) &&
                appIdentityAnnotation.IdentityResource is AzureUserAssignedIdentityResource existingAppIdentity)
        {
            identityResource = existingAppIdentity;
        }
        else
        {
            identityResource = new AzureUserAssignedIdentityResource($"{resource.Name}-identity")
            {
                ProvisioningBuildOptions = options.Value.ProvisioningBuildOptions
            };
        }

        var roleAssignmentResources = CreateRoleAssignmentsResources(resource, roleAssignments, identityResource);
        return (identityResource, roleAssignmentResources);
    }

    private List<AzureBicepResource> CreateRoleAssignmentsResources(
        IResource resource,
        Dictionary<AzureProvisioningResource, IEnumerable<RoleDefinition>> roleAssignments,
        AzureUserAssignedIdentityResource appIdentityResource)
    {
        var roleAssignmentResources = new List<AzureBicepResource>();
        foreach (var (targetResource, roles) in roleAssignments)
        {
            var roleAssignmentResource = new AzureProvisioningResource(
                $"{resource.Name}-roles-{targetResource.Name}",
                infra => AddRoleAssignmentsInfrastructure(infra, targetResource, roles, appIdentityResource))
            {
                ProvisioningBuildOptions = options.Value.ProvisioningBuildOptions,
            };

            // existing resource role assignments need to be scoped to the resource's resource group
            if (targetResource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAnnotation) &&
                existingAnnotation.ResourceGroup is not null)
            {
                roleAssignmentResource.Scope = new(existingAnnotation.ResourceGroup);
            }

            roleAssignmentResources.Add(roleAssignmentResource);
        }

        return roleAssignmentResources;
    }

    private void AddRoleAssignmentsInfrastructure(
        AzureResourceInfrastructure infra,
        AzureProvisioningResource azureResource,
        IEnumerable<RoleDefinition> roles,
        AzureUserAssignedIdentityResource appIdentityResource)
    {

        var context = new AddRoleAssignmentsContext(
            infra,
            executionContext,
            roles,
            new(() => RoleManagementPrincipalType.ServicePrincipal),
            new(() => appIdentityResource.PrincipalId.AsProvisioningParameter(infra, parameterName: AzureBicepResource.KnownParameters.PrincipalId)),
            new(() => appIdentityResource.PrincipalName.AsProvisioningParameter(infra, parameterName: AzureBicepResource.KnownParameters.PrincipalName)));

        azureResource.AddRoleAssignments(context);
    }

    /// <summary>
    /// Context for adding role assignments to an Azure resource.
    /// </summary>
    private sealed class AddRoleAssignmentsContext(
        AzureResourceInfrastructure infrastructure,
        DistributedApplicationExecutionContext executionContext,
        IEnumerable<RoleDefinition> roles,
        Lazy<BicepValue<RoleManagementPrincipalType>> getPrincipalType,
        Lazy<BicepValue<Guid>> getPrincipalId,
        Lazy<BicepValue<string>> getPrincipalName) : IAddRoleAssignmentsContext
    {
        public AzureResourceInfrastructure Infrastructure { get; } = infrastructure;

        public IEnumerable<RoleDefinition> Roles { get; } = roles;

        public BicepValue<RoleManagementPrincipalType> PrincipalType => getPrincipalType.Value;

        public BicepValue<Guid> PrincipalId => getPrincipalId.Value;

        public BicepValue<string> PrincipalName => getPrincipalName.Value;

        public DistributedApplicationExecutionContext ExecutionContext => executionContext;
    }

    private async Task<HashSet<IAzureResource>> GetAzureReferences(IResource resource, CancellationToken cancellationToken)
    {
        var dependencies = await resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.DirectOnly, cancellationToken).ConfigureAwait(false);

        HashSet<IAzureResource> azureReferences = [];
        foreach (var azureResource in dependencies.OfType<IAzureResource>())
        {
            azureReferences.Add(azureResource);
        }

        return azureReferences;
    }

    private static void AppendGlobalRoleAssignments(Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> globalRoleAssignments, AzureProvisioningResource azureResource, IEnumerable<RoleDefinition> newRoles)
    {
        if (!globalRoleAssignments.TryGetValue(azureResource, out var existingRoles))
        {
            existingRoles = new HashSet<RoleDefinition>();
            globalRoleAssignments[azureResource] = existingRoles;
        }

        existingRoles.UnionWith(newRoles);
    }

    private void CreateGlobalRoleAssignments(DistributedApplicationModel appModel, Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> globalRoleAssignments)
    {
        foreach (var (azureResource, roles) in globalRoleAssignments)
        {
            var roleAssignmentResource = CreateGlobalRoleAssignmentsResource(azureResource, roles);
            appModel.Resources.Add(roleAssignmentResource);

            azureResource.Annotations.Add(new RoleAssignmentResourceAnnotation(roleAssignmentResource));

            roleAssignmentResource.Annotations.Add(new ResourceRelationshipAnnotation(azureResource, KnownRelationshipTypes.Parent));
        }
    }

    private AzureProvisioningResource CreateGlobalRoleAssignmentsResource(
        AzureProvisioningResource targetResource,
        IEnumerable<RoleDefinition> roles)
    {
        var roleAssignmentResource = new AzureProvisioningResource(
            $"{targetResource.Name}-roles",
            infra => AddGlobalRoleAssignmentsInfrastructure(infra, targetResource, roles))
        {
            ProvisioningBuildOptions = options.Value.ProvisioningBuildOptions,
        };

        // existing resource role assignments need to be scoped to the resource's resource group
        if (targetResource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAnnotation) &&
            existingAnnotation.ResourceGroup is not null)
        {
            roleAssignmentResource.Scope = new(existingAnnotation.ResourceGroup);
        }

        return roleAssignmentResource;
    }

    private void AddGlobalRoleAssignmentsInfrastructure(
        AzureResourceInfrastructure infra,
        AzureProvisioningResource azureResource,
        IEnumerable<RoleDefinition> roles)
    {
        ProvisioningParameter CreatePrincipalParam(string name)
        {
            var param = new ProvisioningParameter(name, typeof(string));
            infra.Add(param);
            return param;
        }

        var context = new AddRoleAssignmentsContext(
            infra,
            executionContext,
            roles,
            new(() => CreatePrincipalParam(AzureBicepResource.KnownParameters.PrincipalType)),
            new(() => CreatePrincipalParam(AzureBicepResource.KnownParameters.PrincipalId)),
            new(() => CreatePrincipalParam(AzureBicepResource.KnownParameters.PrincipalName)));

        azureResource.AddRoleAssignments(context);
    }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }
}
