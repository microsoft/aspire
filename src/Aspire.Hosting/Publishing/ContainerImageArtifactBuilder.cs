// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Prepares a Dockerfile source using the standard build descriptions and runtime-specific artifact transport.
/// </summary>
internal static class ContainerImageArtifactBuilder
{
    internal static async Task<string> BuildAsync(
        ContainerImageResource source, DockerfileBuildAnnotation description,
        IContainerRuntime runtime, PipelineStepContext context)
    {
        if (runtime is not IContainerImageArtifactRuntime artifactRuntime)
        {
            throw new DistributedApplicationException(
                $"Container runtime '{runtime.Name}' does not support complete Dockerfile image artifact publication. Use Docker with its containerd image store.");
        }
        var options = await source.ProcessContainerBuildOptionsCallbackAsync(
            context.Services, context.Logger, context.ExecutionContext, context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(options.LocalImageName) || string.IsNullOrWhiteSpace(options.LocalImageTag))
        {
            throw new DistributedApplicationException($"Image artifact '{source.Name}' requires a local image name and tag.");
        }
        if (options.Destination == ContainerImageDestination.Archive && string.IsNullOrWhiteSpace(options.OutputPath))
        {
            throw new DistributedApplicationException($"Image artifact '{source.Name}' requires an output path for archive builds.");
        }

        // Each execution materializes a fresh description. A previous manifest or
        // deployment must not cache builder callbacks whose inputs have changed.
        var annotation = DockerfileHelper.CloneBuildAnnotation(description);
        await annotation.EmitDockerfileArtifactsAsync(new DockerfileFactoryContext
        {
            Resource = source,
            Services = context.Services,
            CancellationToken = context.CancellationToken
        }).ConfigureAwait(false);
        var (arguments, secrets) = await ResourceContainerImageManager.ResolveDockerfileBuildInputsAsync(
            annotation, context.CancellationToken).ConfigureAwait(false);
        if (options.OutputPath is { } path)
        {
            Directory.CreateDirectory(path);
        }
        var digest = await artifactRuntime.BuildImageArtifactAsync(
            annotation.ContextPath, annotation.DockerfilePath,
            new ContainerImageBuildOptions
            {
                ImageName = options.LocalImageName,
                Tag = options.LocalImageTag,
                Destination = options.Destination,
                OutputPath = options.OutputPath,
                ImageFormat = options.ImageFormat,
                TargetPlatform = options.TargetPlatform
            },
            arguments, secrets, annotation.Stage, context.CancellationToken).ConfigureAwait(false);
        try
        {
            ContainerImageName.ValidateDigest(digest, nameof(digest));
        }
        catch (ArgumentException ex)
        {
            throw new DistributedApplicationException($"Container runtime returned an invalid built image digest for '{source.Name}'.", ex);
        }

        return digest;
    }
}
