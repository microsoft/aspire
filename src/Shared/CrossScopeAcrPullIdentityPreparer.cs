// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003
#pragma warning disable ASPIREAZURERG001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Roles;

// ACA and App Service normally emit their ACR pull identity and AcrPull assignment inline in the
// environment module. That remains the preferred path, but Bicep rejects an inline role assignment
// when PublishAsExisting selects a registry in an explicit resource group or subscription (BCP139).
//
// This linked helper runs after the application model and final registry selection are complete. For
// only that cross-scope case, it creates a standalone identity and lets AzureResourcePreparer emit the
// role assignment as a separately scoped module. Package-specific annotations remain in their owning
// assemblies, so this source is linked into only the ACA and App Service projects.
namespace Aspire.Hosting.Azure;

/// <summary>
/// Identifies the package-specific annotation that supplies an environment's ACR pull identity.
/// </summary>
internal interface IAcrPullIdentityAnnotation : IResourceAnnotation
{
    AzureUserAssignedIdentityResource Identity { get; }
}

/// <summary>
/// Promotes an inline ACR pull identity to a standalone resource when its final registry is explicitly cross-scoped.
/// </summary>
internal static class CrossScopeAcrPullIdentityPreparer
{
    /// <summary>
    /// Registers late publish preparation while preserving existing inline and user-supplied identity paths.
    /// </summary>
    [AspireExportIgnore(Reason = "Internal publish pipeline wiring.")]
    public static IResourceBuilder<TEnvironment> WithCrossScopeAcrPullIdentity<TEnvironment>(
        this IResourceBuilder<TEnvironment> builder,
        Func<AzureUserAssignedIdentityResource, IAcrPullIdentityAnnotation> createIdentityAnnotation,
        Action<IResourceBuilder<AzureUserAssignedIdentityResource>>? configureIdentity = null)
        where TEnvironment : IResource, IAzureComputeEnvironmentResource
    {
        builder.WithAnnotation(new PipelineStepAnnotation(context =>
        {
            if (!context.PipelineContext.ExecutionContext.IsPublishMode)
            {
                return [];
            }

            PrepareRegistryScope(builder);
            if (!ShouldPrepareIdentity(context.PipelineContext.ExecutionContext, builder))
            {
                return [];
            }

            return
            [
                new PipelineStep
                {
                    Name = $"prepare-cross-scope-acr-pull-identity-{builder.Resource.Name}",
                    Description = $"Prepares the ACR pull identity for {builder.Resource.Name}.",
                    Action = stepContext =>
                    {
                        PrepareIdentity(stepContext, builder, createIdentityAnnotation, configureIdentity);
                        return Task.CompletedTask;
                    },
                    RequiredBySteps = [AzureEnvironmentResource.PrepareResourcesStepName]
                }
            ];
        }));

        return builder;
    }

    private static bool ShouldPrepareIdentity<TEnvironment>(
        DistributedApplicationExecutionContext executionContext,
        IResourceBuilder<TEnvironment> builder)
        where TEnvironment : IResource, IAzureComputeEnvironmentResource
    {
        var environment = builder.Resource;

        // Run mode does not emit the environment Bicep that contains the problematic role assignment.
        // Adding a standalone Azure identity there would unnecessarily change the local application model.
        if (!executionContext.IsPublishMode)
        {
            return false;
        }

        // WithAcrPullIdentity has already selected a user-supplied identity, so preserve that path.
        if (environment.HasAnnotationOfType<IAcrPullIdentityAnnotation>())
        {
            return false;
        }

        // ContainerRegistry is evaluated late so registry replacement APIs have already selected the final registry.
        if (environment.ContainerRegistry is not AzureContainerRegistryResource registry)
        {
            return false;
        }

        // An environment module in an explicit resource group cannot emit an inline role assignment
        // against the primary-group registry. Promote the identity so a separately scoped module can
        // create the assignment.
        if (environment is AzureBicepResource { Scope: not null })
        {
            return true;
        }

        return registry.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingRegistry) &&
            (existingRegistry.ResourceGroup is not null || existingRegistry.Subscription is not null);
    }

    private static void PrepareIdentity<TEnvironment>(
        PipelineStepContext context,
        IResourceBuilder<TEnvironment> builder,
        Func<AzureUserAssignedIdentityResource, IAcrPullIdentityAnnotation> createIdentityAnnotation,
        Action<IResourceBuilder<AzureUserAssignedIdentityResource>>? configureIdentity)
        where TEnvironment : IResource, IAzureComputeEnvironmentResource
    {
        PrepareRegistryScope(builder);
        if (!ShouldPrepareIdentity(context.ExecutionContext, builder) ||
            builder.Resource.ContainerRegistry is not AzureContainerRegistryResource registry)
        {
            return;
        }

        // A cross-scope role assignment cannot be emitted inline in the environment module (BCP139).
        // Promote only this path to a standalone identity so AzureResourcePreparer can emit the
        // role assignment as a module scoped to the existing registry.
        var identityName = $"{builder.Resource.Name}-mi";
        if (context.Model.Resources.TryGetByName(identityName, out _))
        {
            throw new DistributedApplicationException(
                $"Cannot create the cross-scope ACR pull identity '{identityName}' for environment '{builder.Resource.Name}' because a resource with that name already exists. Call 'WithAcrPullIdentity' on the environment to select an existing identity, or use a different resource name.");
        }

        var identity = new AzureUserAssignedIdentityResource(identityName);
        if (builder.Resource is AzureBicepResource environmentResource)
        {
            identity.Scope = environmentResource.Scope;
            foreach (var parameter in environmentResource.Parameters.Where(static parameter =>
                parameter.Value is BicepOutputReference { Resource: AzureResourceGroupResource }))
            {
                identity.Parameters[parameter.Key] = parameter.Value;
            }
            foreach (var resourceGroup in environmentResource.References.OfType<AzureResourceGroupResource>())
            {
                identity.References.Add(resourceGroup);
            }
            if (environmentResource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.Location, out var location) &&
                location is not null)
            {
                AzureDeclaredLocation.Set(identity, location);
            }
        }
        var identityBuilder = builder.ApplicationBuilder.CreateResourceBuilder(identity);
        identityBuilder.ConfigureInfrastructure(infrastructure =>
        {
            // The inline identity uses the environment module's standard tags parameter. Recreate that
            // contract on the promoted module so deployment tags and required-tag policies still apply.
            var tags = new ProvisioningParameter("tags", typeof(object))
            {
                Value = new BicepDictionary<string>()
            };
            infrastructure.Add(tags);

            var identity = infrastructure.GetProvisionableResources().OfType<UserAssignedIdentity>().Single();
            identity.Tags = tags;
        });
        configureIdentity?.Invoke(identityBuilder);
        identityBuilder.WithRoleAssignments(
            builder.ApplicationBuilder.CreateResourceBuilder(registry),
            ContainerRegistryBuiltInRole.AcrPull);

        context.Model.Resources.Add(identity);
        builder.Resource.Annotations.Add(createIdentityAnnotation(identity));
        if (builder.Resource is AzureBicepResource environment)
        {
            environment.References.Add(identity);
        }
    }

    private static void PrepareRegistryScope<TEnvironment>(IResourceBuilder<TEnvironment> builder)
        where TEnvironment : IResource, IAzureComputeEnvironmentResource
    {
        if (builder.Resource is not AzureBicepResource { Scope: not null } ||
            builder.Resource.ContainerRegistry is not AzureContainerRegistryResource registry ||
            registry.HasAnnotationOfType<ExistingAzureResourceAnnotation>() ||
            registry.Scope is not null)
        {
            return;
        }

        var azureEnvironment = builder.ApplicationBuilder.Resources.OfType<AzureEnvironmentResource>().FirstOrDefault() ??
            throw new DistributedApplicationException(
                $"Cannot configure cross-resource-group access to Azure Container Registry '{registry.Name}' because the application model does not contain an Azure environment.");
        registry.Scope = new AzureBicepResourceScope(azureEnvironment.ResourceGroupName);
    }
}
