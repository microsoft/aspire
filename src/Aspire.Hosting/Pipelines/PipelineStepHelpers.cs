// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECONTAINERRUNTIME001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Helper methods for common pipeline step operations.
/// </summary>
internal static class PipelineStepHelpers
{
    /// <summary>
    /// Pushes a container image to a registry with proper logging and task reporting.
    /// If the registry is a local registry (empty endpoint), only tags the image without pushing.
    /// If the image format is OCI (or other non-Docker format), this is a no-op since OCI images
    /// are typically written to local file paths and don't require a push to a registry.
    /// </summary>
    /// <param name="resource">The resource whose image should be pushed.</param>
    /// <param name="context">The pipeline step context for logging and task reporting.</param>
    /// <returns>A task representing the async operation.</returns>
    public static async Task PushImageToRegistryAsync(IResource resource, PipelineStepContext context)
    {
        // Check if the resource is configured to save images as archives
        // rather than pushing to a registry. These don't require a push operation.
        var buildOptionsContext = await resource.ProcessContainerBuildOptionsCallbackAsync(
            context.Services,
            context.Logger,
            context.ExecutionContext,
            context.CancellationToken).ConfigureAwait(false);

        // Skip push operation if Destination is explicitly set to Archive
        if (buildOptionsContext.Destination == ContainerImageDestination.Archive)
        {
            context.Logger.LogInformation("Skipping push for resource '{ResourceName}' - destination is {Destination}, not Registry", 
                resource.Name, buildOptionsContext.Destination);
            return;
        }

        var registry = resource.GetContainerRegistry();
        var registryEndpoint = await registry.Endpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

        // Check if this is a local registry (empty endpoint means no remote push needed)
        var isLocalRegistry = string.IsNullOrEmpty(registryEndpoint);

        if (isLocalRegistry)
        {
            await TagImageForLocalRegistryAsync(resource, context).ConfigureAwait(false);
        }
        else
        {
            await PushImageToRemoteRegistryAsync(resource, registry, context).ConfigureAwait(false);
        }
    }

    private static async Task TagImageForLocalRegistryAsync(IResource resource, PipelineStepContext context)
    {
        IValueProvider cir = new ContainerImageReference(resource);
        var targetTag = await cir.GetValueAsync(new ValueProviderContext { ExecutionContext = context.ExecutionContext }, context.CancellationToken).ConfigureAwait(false);

        var tagTask = await context.ReportingStep.CreateTaskAsync(
            $"Tagging **{resource.Name}** for local use",
            context.CancellationToken).ConfigureAwait(false);

        await using (tagTask.ConfigureAwait(false))
        {
            try
            {
                if (targetTag is null)
                {
                    throw new InvalidOperationException($"Failed to get target tag for {resource.Name}");
                }

                // Get the local image name
                var localImageName = resource.TryGetContainerImageName(out var imageName)
                    ? imageName
                    : resource.Name.ToLowerInvariant();

                // Only tag the image, don't push to a remote registry
                var containerRuntime = context.Services.GetRequiredService<IContainerRuntime>();
                await containerRuntime.TagImageAsync(localImageName, targetTag, context.CancellationToken).ConfigureAwait(false);

                await tagTask.CompleteAsync(
                    $"Successfully tagged **{resource.Name}** as `{targetTag}`",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await tagTask.CompleteAsync(
                    $"Failed to tag **{resource.Name}**: {ex.Message}",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task PushImageToRemoteRegistryAsync(IResource resource, IContainerRegistry registry, PipelineStepContext context)
    {
        var registryName = await registry.Name.GetValueAsync(context.CancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Failed to retrieve container registry information.");

        IValueProvider cir = new ContainerImageReference(resource);
        var targetTag = await cir.GetValueAsync(new ValueProviderContext { ExecutionContext = context.ExecutionContext }, context.CancellationToken).ConfigureAwait(false);

        var pushTask = await context.ReportingStep.CreateTaskAsync(
            $"Pushing **{resource.Name}** to **{registryName}**",
            context.CancellationToken).ConfigureAwait(false);

        await using (pushTask.ConfigureAwait(false))
        {
            try
            {
                if (targetTag is null)
                {
                    throw new InvalidOperationException($"Failed to get target tag for {resource.Name}");
                }

                var containerImageManager = context.Services.GetRequiredService<IResourceContainerImageManager>();
                await containerImageManager.PushImageAsync(resource, context.CancellationToken).ConfigureAwait(false);

                await pushTask.CompleteAsync(
                    $"Successfully pushed **{resource.Name}** to `{targetTag}`",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await pushTask.CompleteAsync(
                    $"Failed to push **{resource.Name}**: {ex.Message}",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }
}
