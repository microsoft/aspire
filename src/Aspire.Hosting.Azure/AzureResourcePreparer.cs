// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
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
    DistributedApplicationExecutionContext executionContext)
{
    internal async Task PrepareResourcesAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        var azureResources = GetAzureResourcesFromAppModel(model);
        if (azureResources.Count == 0)
        {
            return;
        }

        var supportsTargetedRoleAssignments = EnvironmentSupportsIdentitiesAndAssignments(model);
        if (!supportsTargetedRoleAssignments)
        {
            // If the app infrastructure does not support targeted identities and role assignments, then we need to ensure that
            // there are no identity or role assignment annotations in the app model because they won't be honored otherwise.
            EnsureNoIdentityOrRoleAssignmentAnnotations(model);
        }

        await BuildRoleAssignmentAnnotations(model, azureResources, supportsTargetedRoleAssignments, cancellationToken).ConfigureAwait(false);

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

    private bool EnvironmentSupportsIdentitiesAndAssignments(DistributedApplicationModel model)
    {
        // run mode always supports targeted role assignments
        // publish mode supports targeted role assignments when the active publisher opted in, or when
        // a compute environment resource is already in the model. The option is the normal extension
        // registration signal; the resource check preserves direct model construction paths where the
        // environment-specific pipeline still knows how to attach identities and deployment prerequisites.
        return executionContext.IsRunMode || options.Value.SupportsTargetedRoleAssignments || model.Resources.OfType<IAzureComputeEnvironmentResource>().Any();
    }

    private static void EnsureNoIdentityOrRoleAssignmentAnnotations(DistributedApplicationModel appModel)
    {
        foreach (var resource in appModel.Resources)
        {
            if (resource.HasAnnotationOfType<RoleAssignmentAnnotation>())
            {
                throw new InvalidOperationException("The application model does not support role assignments. Ensure you are using an environment that supports role assignments, for example AddAzureContainerAppEnvironment.");
            }

            if (resource.HasAnnotationOfType<ContainerRegistryRoleAssignmentAnnotation>())
            {
                throw new InvalidOperationException("The application model does not support container registry role assignments. Ensure you are using an environment that supports role assignments, for example AddAzureContainerAppEnvironment.");
            }

            if (resource.HasAnnotationOfType<AppIdentityAnnotation>())
            {
                throw new InvalidOperationException("The application model does not support using explicit managed identities. Ensure you are using an environment that supports managed identities, for example AddAzureContainerAppEnvironment.");
            }
        }
    }

    private async Task BuildRoleAssignmentAnnotations(
        DistributedApplicationModel appModel,
        List<(IResource Resource, IAzureResource AzureResource)> azureResources,
        bool supportsTargetedRoleAssignments,
        CancellationToken cancellationToken)
    {
        var globalRoleAssignments = new Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>>();

        // The same annotations drive two different outputs. In Run mode, there is no published
        // workload identity to attach, so role assignments target the deployment principal globally.
        // In Publish mode, each owner gets (or supplies) a user-assigned identity plus targeted role
        // modules, and those modules become deployment prerequisites for the owner.
        var provisioningResourcesByResource = azureResources
            .Where(r => r.AzureResource is AzureProvisioningResource)
            .ToDictionary(r => r.Resource, r => (AzureProvisioningResource)r.AzureResource);
        var provisioningResources = provisioningResourcesByResource.Values.ToArray();
        if (!supportsTargetedRoleAssignments)
        {
            AddDefaultRoleAssignments(provisioningResources, globalRoleAssignments);
        }
        else
        {
            foreach (var resource in GetRoleAssignmentProcessingResources(appModel))
            {
                var prerequisiteResources = new HashSet<AzureBicepResource>();
                var directDependencies = await resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.DirectOnly, cancellationToken).ConfigureAwait(false);

                ProcessDirectAzureReferences(resource, directDependencies, provisioningResourcesByResource, globalRoleAssignments, prerequisiteResources);
                ProcessReferenceRoleAssignments(resource, directDependencies, globalRoleAssignments);

                if (executionContext.IsPublishMode)
                {
                    // The two processing steps above may add RoleAssignmentAnnotation instances for
                    // defaults and implied references. Materialize publish resources only after that
                    // mutation so GetAllRoleAssignments sees the complete set for this owner.
                    CreatePublishRoleAssignmentResources(appModel, resource, provisioningResourcesByResource, prerequisiteResources);
                }

                // Add prerequisite infrastructure resources on the owner resource.
                // Deployment infrastructure subscribers will transfer these to deployment target References
                // so AzureBicepResource dependency wiring can apply provision ordering.
                AddDeploymentPrerequisitesAnnotation(resource, prerequisiteResources);
            }

            if (executionContext.IsRunMode)
            {
                // Any Azure resource that was not claimed by a direct reference still gets its defaults
                // in Run mode, preserving the deployment-principal behavior used for F5 provisioning.
                AddDefaultRoleAssignments(provisioningResources.Where(resource => !globalRoleAssignments.ContainsKey(resource)), globalRoleAssignments);
            }
        }

        if (globalRoleAssignments.Count > 0)
        {
            CreateGlobalRoleAssignments(appModel, globalRoleAssignments);
        }
    }

    private static IResource[] GetRoleAssignmentProcessingResources(DistributedApplicationModel appModel)
    {
        // Snapshot the owners before processing because publish materialization can add generated
        // identities and role-assignment resources to the model. Aggregate resources are included
        // because they can declare role needs for internal Azure resources even though they are not
        // compute resources themselves.
        return appModel.GetComputeResources()
            .Concat(appModel.Resources
                .OfType<AzureUserAssignedIdentityResource>()
                .Where(r => !r.IsExcludedFromPublish()))
            .Concat(appModel.Resources
                .Where(r => !r.IsExcludedFromPublish() &&
                    (r.HasAnnotationOfType<RoleAssignmentAnnotation>() ||
                     r.HasAnnotationOfType<ContainerRegistryRoleAssignmentAnnotation>())))
            .Distinct()
            .ToArray();
    }

    private void ProcessDirectAzureReferences(
        IResource resource,
        IReadOnlySet<IResource> directDependencies,
        IReadOnlyDictionary<IResource, AzureProvisioningResource> provisioningResourcesByResource,
        Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> globalRoleAssignments,
        HashSet<AzureBicepResource> prerequisiteResources)
    {
        var roleAssignmentsByTarget = GetResolvedRoleAssignments(resource, provisioningResourcesByResource).ToLookup(a => a.Target);

        foreach (var dependency in directDependencies)
        {
            if (!TryResolveRoleAssignmentTarget(dependency, provisioningResourcesByResource, out var azureReference))
            {
                continue;
            }

            if (ShouldSkipRoleAssignmentTarget(azureReference))
            {
                continue;
            }

            AddPrivateEndpointPrerequisites(azureReference, prerequisiteResources);

            var explicitRoleAssignments = roleAssignmentsByTarget[azureReference];
            if (explicitRoleAssignments.Any())
            {
                if (executionContext.IsRunMode)
                {
                    // Run mode assigns roles only for direct Azure references. Aggregate annotations
                    // whose targets are not direct dependencies are publish-only, which avoids granting
                    // unexpected deployment-principal permissions during local provisioning.
                    AppendRoleAssignments(globalRoleAssignments, azureReference, explicitRoleAssignments.SelectMany(a => a.Roles));
                }

                continue;
            }

            if (!azureReference.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaults))
            {
                continue;
            }

            if (executionContext.IsRunMode)
            {
                // Empty defaults are meaningful for resources like databases: the key marks the target
                // as claimed so the Run-mode fallback does not add broader parent defaults later.
                AppendRoleAssignments(globalRoleAssignments, azureReference, defaults.Roles);
            }
            else
            {
                resource.Annotations.Add(new RoleAssignmentAnnotation(azureReference, defaults.Roles));
            }
        }
    }

    private void ProcessReferenceRoleAssignments(
        IResource resource,
        IReadOnlySet<IResource> directDependencies,
        Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> globalRoleAssignments)
    {
        // A direct dependency that is not itself an Azure resource can still "front" one
        // (e.g. a Foundry hosted agent's node app fronts its owning Foundry account). Such a
        // resource carries ReferenceRoleAssignmentAnnotation(s) declaring that any resource
        // referencing it should be granted roles on a transitive Azure target the normal
        // IAzureResource-only reference walk above cannot reach.
        foreach (var dependency in directDependencies)
        {
            if (!dependency.TryGetAnnotationsOfType<ReferenceRoleAssignmentAnnotation>(out var impliedRoleAssignments))
            {
                continue;
            }

            foreach (var impliedRoleAssignment in impliedRoleAssignments)
            {
                var target = impliedRoleAssignment.Target;
                if (ShouldSkipRoleAssignmentTarget(target))
                {
                    continue;
                }

                if (executionContext.IsRunMode)
                {
                    AppendRoleAssignments(globalRoleAssignments, target, impliedRoleAssignment.Roles);
                }
                else
                {
                    // Publish materialization reads RoleAssignmentAnnotation, so convert implied
                    // reference grants into the same shape as direct WithRoleAssignments calls.
                    resource.Annotations.Add(new RoleAssignmentAnnotation(target, impliedRoleAssignment.Roles));
                }
            }
        }
    }

    private void CreatePublishRoleAssignmentResources(
        DistributedApplicationModel appModel,
        IResource resource,
        IReadOnlyDictionary<IResource, AzureProvisioningResource> provisioningResourcesByResource,
        HashSet<AzureBicepResource> prerequisiteResources)
    {
        var roleAssignments = GetAllRoleAssignments(resource, provisioningResourcesByResource);
        if (roleAssignments.Count == 0)
        {
            return;
        }

        var (identityResource, roleAssignmentResources) = CreateIdentityAndRoleAssignmentResources(resource, roleAssignments);

        if (resource != identityResource)
        {
            // Publishers discover workload identities from AppIdentityAnnotation, not by scanning the
            // role-assignment modules. Keep the identity in the model so generated deployment targets
            // can attach it and expose any required client/id parameters.
            EnsureIdentityResource(appModel, resource, identityResource);
        }

        foreach (var roleAssignmentResource in roleAssignmentResources)
        {
            prerequisiteResources.Add(AddOrGetRoleAssignmentResource(appModel, roleAssignmentResource));
        }
    }

    private void EnsureIdentityResource(DistributedApplicationModel appModel, IResource ownerResource, AzureUserAssignedIdentityResource identityResource)
    {
        if (!ownerResource.TryGetLastAnnotation<AppIdentityAnnotation>(out var existingAppIdentityAnnotation) ||
            existingAppIdentityAnnotation.IdentityResource != identityResource)
        {
            ownerResource.Annotations.Add(new AppIdentityAnnotation(identityResource));
        }

        if (!appModel.Resources.Contains(identityResource))
        {
            identityResource.ProvisioningBuildOptions ??= options.Value.ProvisioningBuildOptions;
            appModel.Resources.Add(identityResource);
        }
    }

    private static void AddDefaultRoleAssignments(
        IEnumerable<AzureProvisioningResource> resources,
        Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> roleAssignments)
    {
        foreach (var resource in resources)
        {
            if (resource.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaultRoleAssignments))
            {
                AppendRoleAssignments(roleAssignments, resource, defaultRoleAssignments.Roles);
            }
        }
    }

    private static void AddPrivateEndpointPrerequisites(AzureProvisioningResource azureReference, HashSet<AzureBicepResource> prerequisiteResources)
    {
        if (!azureReference.TryGetAnnotationsOfType<PrivateEndpointTargetAnnotation>(out var peAnnotations))
        {
            return;
        }

        foreach (var peAnnotation in peAnnotations)
        {
            prerequisiteResources.Add(peAnnotation.PrivateEndpointResource);
        }
    }

    private static bool ShouldSkipRoleAssignmentTarget(AzureProvisioningResource target) => target.IsContainer() || target.IsEmulator();

    private static Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> GetAllRoleAssignments(
        IResource resource,
        IReadOnlyDictionary<IResource, AzureProvisioningResource> provisioningResourcesByResource)
    {
        var result = new Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>>();

        foreach (var (target, roles) in GetResolvedRoleAssignments(resource, provisioningResourcesByResource))
        {
            // Use the same HashSet accumulator as Run mode so duplicate annotations for the same
            // target cannot produce duplicate Bicep role assignment names. Creating the key before
            // unioning is intentional: an empty role set must remain observable after a caller uses
            // it to suppress defaults.
            AppendRoleAssignments(result, target, roles);
        }

        return result;
    }

    private static IEnumerable<(AzureProvisioningResource Target, IReadOnlySet<RoleDefinition> Roles)> GetResolvedRoleAssignments(
        IResource resource,
        IReadOnlyDictionary<IResource, AzureProvisioningResource> provisioningResourcesByResource)
    {
        if (resource.TryGetAnnotationsOfType<RoleAssignmentAnnotation>(out var roleAssignments))
        {
            foreach (var roleAssignment in roleAssignments)
            {
                yield return (roleAssignment.Target, roleAssignment.Roles);
            }
        }

        if (!resource.TryGetAnnotationsOfType<ContainerRegistryRoleAssignmentAnnotation>(out var containerRegistryRoleAssignments))
        {
            yield break;
        }

        if (resource is not IAzureComputeEnvironmentResource computeEnvironment)
        {
            throw new InvalidOperationException($"The resource '{resource.Name}' declares container registry role assignments but is not an Azure compute environment.");
        }

        // The current container registry is last-writer-wins on ContainerRegistryReferenceAnnotation.
        // Resolve it during preparation so a later WithAzureContainerRegistry call updates both image
        // deployment and the generated AcrPull role assignment without a second side channel.
        var containerRegistry = computeEnvironment.ContainerRegistry;
        if (containerRegistry is null)
        {
            throw new InvalidOperationException($"The Azure compute environment '{resource.Name}' does not have an associated Azure Container Registry.");
        }

        if (!TryResolveRoleAssignmentTarget(containerRegistry, provisioningResourcesByResource, out var containerRegistryTarget))
        {
            throw new InvalidOperationException($"The container registry associated with Azure compute environment '{resource.Name}' is not an Azure provisioning resource.");
        }

        foreach (var containerRegistryRoleAssignment in containerRegistryRoleAssignments)
        {
            yield return (containerRegistryTarget, containerRegistryRoleAssignment.Roles);
        }
    }

    private static bool TryResolveRoleAssignmentTarget(
        IResource targetResource,
        IReadOnlyDictionary<IResource, AzureProvisioningResource> provisioningResourcesByResource,
        [NotNullWhen(true)] out AzureProvisioningResource? target)
    {
        if (targetResource is AzureProvisioningResource provisioningResource)
        {
            target = provisioningResource;
            return true;
        }

        return provisioningResourcesByResource.TryGetValue(targetResource, out target);
    }

    private (AzureUserAssignedIdentityResource IdentityResource, List<AzureRoleAssignmentResource> RoleAssignmentResources) CreateIdentityAndRoleAssignmentResources(
        IResource resource,
        Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> roleAssignments)
    {
        AzureUserAssignedIdentityResource identityResource;

        // If the owner is an AzureUserAssignedIdentityResource, it is its own role-assignment identity.
        // Otherwise prefer an explicit AppIdentityAnnotation and create a hidden identity only when the
        // owner has not declared which identity should receive the roles.
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

    private List<AzureRoleAssignmentResource> CreateRoleAssignmentsResources(
        IResource resource,
        Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> roleAssignments,
        AzureUserAssignedIdentityResource appIdentityResource)
    {
        var roleAssignmentResources = new List<AzureRoleAssignmentResource>();
        foreach (var (targetResource, roles) in roleAssignments)
        {
            // Keep targeted role assignments in their own Bicep module instead of inlining them
            // under the owner module. Existing Azure resources can live in a different resource
            // group, and Bicep extension resources must be emitted at the scope they target.
            var roleAssignmentResource = new AzureRoleAssignmentResource(
                $"{resource.Name}-roles-{targetResource.Name}",
                targetResource,
                resource,
                appIdentityResource,
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
        // Role assignment builders only evaluate the principal values they need. Keep them lazy so
        // resources that do not emit a particular field do not unnecessarily create matching Bicep
        // parameters on the role-assignment module.
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

    private static void AppendRoleAssignments(Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> roleAssignments, AzureProvisioningResource azureResource, IEnumerable<RoleDefinition> newRoles)
    {
        if (!roleAssignments.TryGetValue(azureResource, out var existingRoles))
        {
            existingRoles = new HashSet<RoleDefinition>();
            roleAssignments[azureResource] = existingRoles;
        }

        existingRoles.UnionWith(newRoles);
    }

    private void CreateGlobalRoleAssignments(DistributedApplicationModel appModel, Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> globalRoleAssignments)
    {
        foreach (var (azureResource, roles) in globalRoleAssignments)
        {
            var roleAssignmentResource = CreateGlobalRoleAssignmentsResource(azureResource, roles);

            roleAssignmentResource = AddOrGetRoleAssignmentResource(appModel, roleAssignmentResource);

            if (!azureResource.Annotations.OfType<RoleAssignmentResourceAnnotation>().Any(a => a.RolesResource == roleAssignmentResource))
            {
                azureResource.Annotations.Add(new RoleAssignmentResourceAnnotation(roleAssignmentResource));
            }

            if (!roleAssignmentResource.Annotations.OfType<ResourceRelationshipAnnotation>().Any(a => a.Resource == azureResource && a.Type == KnownRelationshipTypes.Parent))
            {
                roleAssignmentResource.Annotations.Add(new ResourceRelationshipAnnotation(azureResource, KnownRelationshipTypes.Parent));
            }
        }
    }

    private static AzureRoleAssignmentResource AddOrGetRoleAssignmentResource(DistributedApplicationModel appModel, AzureRoleAssignmentResource roleAssignmentResource)
    {
        if (!appModel.Resources.TryGetByName(roleAssignmentResource.Name, out var existingResource))
        {
            appModel.Resources.Add(roleAssignmentResource);
            return roleAssignmentResource;
        }

        if (existingResource is AzureRoleAssignmentResource existingRoleAssignmentResource &&
            existingRoleAssignmentResource.TargetAzureResource == roleAssignmentResource.TargetAzureResource &&
            existingRoleAssignmentResource.OwnerResource == roleAssignmentResource.OwnerResource &&
            existingRoleAssignmentResource.IdentityResource == roleAssignmentResource.IdentityResource)
        {
            return existingRoleAssignmentResource;
        }

        appModel.Resources.Add(roleAssignmentResource);
        return roleAssignmentResource;
    }

    private static void AddDeploymentPrerequisitesAnnotation(IResource resource, HashSet<AzureBicepResource> prerequisiteResources)
    {
        if (prerequisiteResources.Count == 0)
        {
            return;
        }

        if (resource.TryGetAnnotationsOfType<DeploymentPrerequisitesAnnotation>(out var existingAnnotations))
        {
            prerequisiteResources.ExceptWith(existingAnnotations.SelectMany(a => a.Resources));
        }

        if (prerequisiteResources.Count > 0)
        {
            resource.Annotations.Add(new DeploymentPrerequisitesAnnotation(prerequisiteResources));
        }
    }

    private AzureRoleAssignmentResource CreateGlobalRoleAssignmentsResource(
        AzureProvisioningResource targetResource,
        IEnumerable<RoleDefinition> roles)
    {
        var roleAssignmentResource = new AzureRoleAssignmentResource(
            $"{targetResource.Name}-roles",
            targetResource,
            ownerResource: null,
            identityResource: null,
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
}
