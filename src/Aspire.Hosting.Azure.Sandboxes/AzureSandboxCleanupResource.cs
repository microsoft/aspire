// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Azure;

internal sealed class AzureSandboxCleanupResource : Resource
{
    private const string ResourceName = "azure-sandbox-cleanup";

    private AzureSandboxCleanupResource()
        : base(ResourceName)
    {
        Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        Annotations.Add(new PipelineStepAnnotation(CreateSteps));
        Annotations.Add(new PipelineConfigurationAnnotation(AzureSandboxContainerDeployment.ConfigureStaleCleanupDestroyOrdering));
    }

    public static void EnsureAdded(IDistributedApplicationBuilder builder)
    {
        if (builder.Resources.OfType<AzureSandboxCleanupResource>().Any())
        {
            return;
        }

        builder.AddResource(new AzureSandboxCleanupResource())
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = nameof(AzureSandboxCleanupResource),
                Properties = [],
                IsHidden = true
            });
    }

    private IEnumerable<PipelineStep> CreateSteps(PipelineStepFactoryContext context)
    {
        return
        [
            AzureSandboxContainerDeployment.CreateStaleCleanupPipelineStep(
                this,
                AzureSandboxContainerDeployment.GetActiveStateSectionNames(context.PipelineContext.Model))
        ];
    }
}
