// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Materializes role assignments that cannot be emitted in the API Management module.
/// </summary>
internal static class AzureApiManagementRoleAssignmentPreparer
{
    /// <summary>
    /// Registers publish preparation after the application model has its final backend and Key Vault configuration.
    /// </summary>
    [AspireExportIgnore(Reason = "Internal publish pipeline wiring.")]
    public static IResourceBuilder<AzureApiManagementResource> WithRoleAssignmentPreparation(
        this IResourceBuilder<AzureApiManagementResource> builder)
    {
        builder.WithAnnotation(new PipelineStepAnnotation(context =>
        {
            if (!context.PipelineContext.ExecutionContext.IsPublishMode)
            {
                return [];
            }

            return
            [
                new PipelineStep
                {
                    Name = $"prepare-api-management-role-assignments-{builder.Resource.Name}",
                    Description = $"Prepares cross-scope role assignments for {builder.Resource.Name}.",
                    Action = stepContext =>
                    {
                        PrepareRoleAssignments(stepContext, builder);
                        return Task.CompletedTask;
                    },
                    RequiredBySteps = [AzureEnvironmentResource.PrepareResourcesStepName]
                }
            ];
        }));

        return builder;
    }

    internal static bool TryGetExplicitExistingScope(
        AzureProvisioningResource resource,
        out AzureBicepResourceScope? scope)
    {
        scope = null;
        if (!resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existing) ||
            (existing.ResourceGroup is null && existing.Subscription is null))
        {
            return false;
        }

        scope = (existing.ResourceGroup, existing.Subscription) switch
        {
            ({ } resourceGroup, { } subscription) => new AzureBicepResourceScope(resourceGroup, subscription),
            ({ } resourceGroup, null) => new AzureBicepResourceScope(resourceGroup),
            (null, { } subscription) => AzureBicepResourceScope.CreateForSubscription(subscription),
            _ => throw new UnreachableException()
        };
        return true;
    }

    internal static bool RequiresExternalRoleAssignment(
        AzureApiManagementResource apiManagement,
        AzureProvisioningResource target)
    {
        return TryGetExplicitExistingScope(apiManagement, out _) ||
            TryGetExplicitExistingScope(target, out _);
    }

    private static void PrepareRoleAssignments(
        PipelineStepContext context,
        IResourceBuilder<AzureApiManagementResource> builder)
    {
        if (!context.ExecutionContext.IsPublishMode)
        {
            return;
        }

        PrepareBackendRoleAssignments(context, builder);
        PrepareKeyVaultRoleAssignments(context, builder);
    }

    private static void PrepareBackendRoleAssignments(
        PipelineStepContext context,
        IResourceBuilder<AzureApiManagementResource> builder)
    {
        var apiManagement = builder.Resource;
        var assignments = apiManagement.Backends
            .SelectMany(backend => backend.RoleAssignments)
            .Where(assignment => RequiresExternalRoleAssignment(apiManagement, assignment.Target))
            .GroupBy(assignment => assignment.Target);

        foreach (var assignmentsForTarget in assignments)
        {
            var target = assignmentsForTarget.Key;
            _ = TryGetExplicitExistingScope(target, out var scope);

            var roles = assignmentsForTarget
                .Select(assignment => CreateRoleDefinition(assignment.Role))
                .ToHashSet();
            var resourceName = AzureApiManagementExtensions.CreateBoundedIdentifier(
                $"{apiManagement.Name}-roles-{target.Name}",
                64);

            if (context.Model.Resources.TryGetByName(resourceName, out var existingResource))
            {
                if (existingResource is AzureRoleAssignmentResource existingRoleAssignments &&
                    existingRoleAssignments.TargetAzureResource == target &&
                    existingRoleAssignments.References.Contains(apiManagement))
                {
                    continue;
                }

                throw new DistributedApplicationException(
                    $"Cannot create API Management role assignments '{resourceName}' because a resource with that name already exists.");
            }

            var roleAssignments = new AzureRoleAssignmentResource(
                resourceName,
                target,
                ownerResource: null,
                identityResource: null,
                infrastructure => target.AddRoleAssignments(new ApiManagementRoleAssignmentContext(
                    infrastructure,
                    context.ExecutionContext,
                    roles,
                    apiManagement.PrincipalId)));
            roleAssignments.Scope = scope;

            // The principal ID is produced by the API Management module. Keep the explicit reference as
            // well as the Bicep parameter so pipeline ordering is established before templates are built.
            roleAssignments.References.Add(apiManagement);
            AddOwnedResource(context, builder, roleAssignments);
        }
    }

    private static void PrepareKeyVaultRoleAssignments(
        PipelineStepContext context,
        IResourceBuilder<AzureApiManagementResource> builder)
    {
        var assignments = GetKeyVaultRoleAssignments(builder.Resource);
        if (!assignments.Keys.Any(target => RequiresExternalRoleAssignment(builder.Resource, target)))
        {
            return;
        }

        if (builder.Resource.KeyVaultIdentity is not null)
        {
            return;
        }

        var identityName = AzureApiManagementExtensions.CreateBoundedIdentifier(
            $"{builder.Resource.Name}-kv-identity",
            64);
        if (context.Model.Resources.TryGetByName(identityName, out _))
        {
            throw new DistributedApplicationException(
                $"Cannot create the API Management Key Vault identity '{identityName}' because a resource with that name already exists.");
        }

        var identity = new AzureUserAssignedIdentityResource(identityName);
        AddOwnedResource(context, builder, identity);
        builder.Resource.KeyVaultIdentity = identity;
        builder.Resource.References.Add(identity);

        foreach (var (target, roles) in assignments)
        {
            var resourceName = AzureApiManagementExtensions.CreateBoundedIdentifier(
                $"{identity.Name}-roles-{target.Name}",
                64);
            if (context.Model.Resources.TryGetByName(resourceName, out _))
            {
                throw new DistributedApplicationException(
                    $"Cannot create API Management Key Vault role assignments '{resourceName}' because a resource with that name already exists.");
            }

            var roleAssignments = new AzureRoleAssignmentResource(
                resourceName,
                target,
                identity,
                identity,
                infrastructure =>
                {
                    target.AddRoleAssignments(new ApiManagementRoleAssignmentContext(
                        infrastructure,
                        context.ExecutionContext,
                        roles,
                        identity.PrincipalId));

                    // This output lets the APIM module consume a value from the completed role module.
                    // The value is intentionally opaque; only the resulting Bicep dependency is needed.
                    infrastructure.Add(new ProvisioningOutput("completed", typeof(string))
                    {
                        Value = new BicepValue<string>("completed"),
                    });
                });
            if (TryGetExplicitExistingScope(target, out var scope))
            {
                roleAssignments.Scope = scope;
            }

            roleAssignments.References.Add(identity);
            AddOwnedResource(context, builder, roleAssignments);
            builder.Resource.References.Add(roleAssignments);
            builder.Resource.KeyVaultRoleAssignmentDependencies.Add(
                new BicepOutputReference("completed", roleAssignments));
        }
    }

    private static void AddOwnedResource(
        PipelineStepContext context,
        IResourceBuilder<AzureApiManagementResource> owner,
        AzureBicepResource resource)
    {
        owner.ApplicationBuilder.CreateResourceBuilder(resource)
            .WithParentRelationship(owner.Resource);
        context.Model.Resources.Add(resource);
    }

    private static Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> GetKeyVaultRoleAssignments(
        AzureApiManagementResource apiManagement)
    {
        var assignments = new Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>>();

        foreach (var customDomain in apiManagement.CustomDomains)
        {
            AddKeyVaultRole(
                assignments,
                customDomain.Certificate,
                KeyVaultBuiltInRole.KeyVaultCertificateUser);
        }

        foreach (var secretReference in apiManagement.NamedValues
            .Select(namedValue => namedValue.Value)
            .OfType<IAzureKeyVaultSecretReference>())
        {
            AddKeyVaultRole(assignments, secretReference, KeyVaultBuiltInRole.KeyVaultSecretsUser);
        }

        return assignments;
    }

    private static void AddKeyVaultRole(
        Dictionary<AzureProvisioningResource, HashSet<RoleDefinition>> assignments,
        IAzureKeyVaultSecretReference secretReference,
        KeyVaultBuiltInRole role)
    {
        if (secretReference.Resource is not AzureProvisioningResource target)
        {
            // Custom IAzureKeyVaultResource implementations use the existing inline path. Only provisioning
            // resources can carry the explicit Azure scope that requires a standalone role-assignment module.
            return;
        }

        if (!assignments.TryGetValue(target, out var roles))
        {
            roles = [];
            assignments.Add(target, roles);
        }

        roles.Add(new RoleDefinition(role.ToString(), KeyVaultBuiltInRole.GetBuiltInRoleName(role)));
    }

    private static RoleDefinition CreateRoleDefinition(object role) =>
        role switch
        {
            CognitiveServicesBuiltInRole cognitiveServicesRole => new(
                cognitiveServicesRole.ToString(),
                CognitiveServicesBuiltInRole.GetBuiltInRoleName(cognitiveServicesRole)),
            StorageBuiltInRole storageRole => new(
                storageRole.ToString(),
                StorageBuiltInRole.GetBuiltInRoleName(storageRole)),
            _ => throw new UnreachableException()
        };

    private sealed class ApiManagementRoleAssignmentContext(
        AzureResourceInfrastructure infrastructure,
        DistributedApplicationExecutionContext executionContext,
        IReadOnlySet<RoleDefinition> roles,
        BicepOutputReference principalId) : IAddRoleAssignmentsContext
    {
        public AzureResourceInfrastructure Infrastructure { get; } = infrastructure;

        public IEnumerable<RoleDefinition> Roles { get; } = roles;

        public BicepValue<RoleManagementPrincipalType> PrincipalType { get; } =
            RoleManagementPrincipalType.ServicePrincipal;

        public BicepValue<Guid> PrincipalId { get; } =
            principalId.AsProvisioningParameter(
                infrastructure,
                AzureBicepResource.KnownParameters.PrincipalId);

        public BicepValue<string> PrincipalName { get; } = new(string.Empty);

        public DistributedApplicationExecutionContext ExecutionContext { get; } = executionContext;
    }
}
