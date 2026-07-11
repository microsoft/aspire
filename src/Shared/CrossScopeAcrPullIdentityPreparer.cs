// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning.ContainerRegistry;

namespace Aspire.Hosting.Azure;

internal interface IAcrPullIdentityAnnotation : IResourceAnnotation
{
    AzureUserAssignedIdentityResource Identity { get; }
}

internal static class CrossScopeAcrPullIdentityPreparer
{
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
