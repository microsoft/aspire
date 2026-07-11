// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning.ContainerRegistry;

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
            if (!ShouldPrepareIdentity(context.PipelineContext.ExecutionContext, builder.Resource))
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
        TEnvironment environment)
        where TEnvironment : IResource, IAzureComputeEnvironmentResource
    {
        return executionContext.IsPublishMode &&
            !environment.HasAnnotationOfType<IAcrPullIdentityAnnotation>() &&
            environment.ContainerRegistry is AzureContainerRegistryResource registry &&
            registry.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingRegistry) &&
            (existingRegistry.ResourceGroup is not null || existingRegistry.Subscription is not null);
    }

    private static void PrepareIdentity<TEnvironment>(
        PipelineStepContext context,
        IResourceBuilder<TEnvironment> builder,
        Func<AzureUserAssignedIdentityResource, IAcrPullIdentityAnnotation> createIdentityAnnotation,
        Action<IResourceBuilder<AzureUserAssignedIdentityResource>>? configureIdentity)
        where TEnvironment : IResource, IAzureComputeEnvironmentResource
    {
        if (!ShouldPrepareIdentity(context.ExecutionContext, builder.Resource) ||
            builder.Resource.ContainerRegistry is not AzureContainerRegistryResource registry)
        {
            return;
        }

        // A cross-scope role assignment cannot be emitted inline in the environment module (BCP139).
        // Promote only this path to a standalone identity so AzureResourcePreparer can emit the
        // role assignment as a module scoped to the existing registry.
        var identity = new AzureUserAssignedIdentityResource($"{builder.Resource.Name}-mi");
        var identityBuilder = builder.ApplicationBuilder.CreateResourceBuilder(identity);
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
}
