// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPROJECTS001

using System.IO.Compression;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Installs and executes the shared .NET SDK container-publishing pipeline.
/// </summary>
internal static class DotnetProgramPublishing
{
    public static void Configure(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (resource.HasAnnotationOfType<DotnetProgramPublishingAnnotation>())
        {
            return;
        }

        resource.Annotations.Add(new DotnetProgramPublishingAnnotation());
        resource.Annotations.Add(new PipelineStepAnnotation(factoryContext =>
        {
            var stepResource = factoryContext.Resource;
            var buildEnvironmentCallbacks = stepResource.Annotations
                .OfType<DotnetProgramBuildEnvironmentCallbackAnnotation>()
                .ToArray();
            var steps = new List<PipelineStep>();

            if (!stepResource.RequiresImageBuild())
            {
                return steps;
            }

            var buildStep = new PipelineStep
            {
                Name = $"build-{stepResource.Name}",
                Description = $"Builds the container image for the {stepResource.Name} project.",
                Action = context => BuildImageAsync(stepResource, buildEnvironmentCallbacks, context),
                Tags = [WellKnownPipelineTags.BuildCompute],
                RequiredBySteps = [WellKnownPipelineSteps.Build],
                DependsOnSteps = [WellKnownPipelineSteps.BuildPrereq],
                Resource = stepResource
            };
            steps.Add(buildStep);

            if (stepResource.RequiresImageBuildAndPush())
            {
                var pushStep = new PipelineStep
                {
                    Name = $"push-{stepResource.Name}",
                    Action = context => PipelineStepHelpers.PushImageToRegistryAsync(stepResource, context),
                    Tags = [WellKnownPipelineTags.PushContainerImage],
                    RequiredBySteps = [WellKnownPipelineSteps.Push],
                    Resource = stepResource
                };
                steps.Add(pushStep);
            }

            return steps;
        }));

        resource.Annotations.Add(new ContainerBuildOptionsCallbackAnnotation(context =>
        {
            context.LocalImageName = context.Resource.Name.ToLowerInvariant();
            context.LocalImageTag = "latest";
            context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
        }));

        resource.Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            if (resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out var containerFilesAnnotations))
            {
                var buildSteps = context.GetSteps(resource, WellKnownPipelineTags.BuildCompute);

                foreach (var containerFile in containerFilesAnnotations)
                {
                    buildSteps.DependsOn(context.GetSteps(containerFile.Source, WellKnownPipelineTags.BuildCompute));
                }
            }

            var projectBuildSteps = context.GetSteps(resource, WellKnownPipelineTags.BuildCompute);
            var pushSteps = context.GetSteps(resource, WellKnownPipelineTags.PushContainerImage);

            pushSteps.DependsOn(projectBuildSteps);
            pushSteps.DependsOn(WellKnownPipelineSteps.PushPrereq);
        }));
    }

    private static async Task BuildImageAsync(
        IResource resource,
        IReadOnlyList<DotnetProgramBuildEnvironmentCallbackAnnotation> buildEnvironmentCallbacks,
        PipelineStepContext context)
    {
        var currentCallbacks = resource.Annotations
            .OfType<DotnetProgramBuildEnvironmentCallbackAnnotation>();
        if (!currentCallbacks.SequenceEqual(buildEnvironmentCallbacks, ReferenceEqualityComparer.Instance))
        {
            throw new DistributedApplicationException(
                $"The build environment of .NET program resource '{resource.Name}' changed after the publish pipeline was resolved.");
        }

        var containerImageBuilder = context.Services.GetRequiredService<IResourceContainerImageManager>();
        if (containerImageBuilder is not IDotnetProgramContainerImageManager dotnetProgramImageBuilder)
        {
            // A replacement for the public manager owns the complete build contract, including
            // ContainerFilesDestinationAnnotation. Only the built-in partial contract exposes the
            // resolved image identity and archive options needed for framework-provided layering.
            await containerImageBuilder.BuildImageAsync(resource, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        // Only the resolved build options determine whether SDK publishing needs a runtime.
        // Keep interactive recovery for those builds without blocking daemon-free archives.
        var readiness = context.Services.GetRequiredService<ContainerRuntimeReadiness>();
        var buildResult = await dotnetProgramImageBuilder.BuildDotnetProgramImageAsync(
            resource,
            buildEnvironmentCallbacks,
            readiness.EnsureRunningAsync,
            context.CancellationToken).ConfigureAwait(false);
        await using var buildResultLifetime = buildResult.ConfigureAwait(false);

        if (resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out _))
        {
            await LayerContainerFilesAsync(
                resource,
                resource.GetProjectMetadata(),
                buildResult,
                context.Services,
                context.Logger,
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task LayerContainerFilesAsync(
        IResource resource,
        IProjectMetadata projectMetadata,
        DotnetProgramImageBuildResult buildResult,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sourceImageName = buildResult.SourceImageReference;
        var exportsArchive = buildResult.Destination == ContainerImageDestination.Archive;
        var tempImageTag = exportsArchive ? $"aspire-layered-{Guid.NewGuid():N}" : $"temp-{Guid.NewGuid():N}";
        var tempImageName = $"{buildResult.LocalImageName}:{tempImageTag}";
        var containerRuntime = await services
            .GetRequiredService<IContainerRuntimeResolver>()
            .ResolveAsync(cancellationToken)
            .ConfigureAwait(false);
        var directoryService = services.GetRequiredService<IFileSystemService>();
        TemporaryContainerImage? temporaryImage = null;
        TempDirectory? stagedArchiveDirectory = null;
        string? tempDockerfilePath = null;
        var builtSuccessfully = false;

        try
        {
            stagedArchiveDirectory = exportsArchive
                ? directoryService.TempDirectory.CreateTempSubdirectory("aspire-container-archive")
                : null;

            if (!exportsArchive)
            {
                temporaryImage = new TemporaryContainerImage(containerRuntime, tempImageName, logger);
                logger.LogDebug("Tagging image {SourceImageName} as {TempImageName}", sourceImageName, tempImageName);
                await containerRuntime.TagImageAsync(sourceImageName, tempImageName, cancellationToken).ConfigureAwait(false);
                sourceImageName = tempImageName;
            }

            var dockerfileBuilder = new DockerfileBuilder();
            dockerfileBuilder.AddContainerFilesStages(resource, logger);
            dockerfileBuilder
                .From(sourceImageName)
                .AddContainerFiles(resource, buildResult.ContainerWorkingDirectory, logger);

            var projectDirectory = Path.GetDirectoryName(projectMetadata.ProjectPath)!;
            tempDockerfilePath = directoryService.TempDirectory.CreateTempFile("Dockerfile").Path;
            using (var writer = new StreamWriter(tempDockerfilePath))
            {
                await dockerfileBuilder.WriteAsync(writer, cancellationToken).ConfigureAwait(false);
            }

            var runtimeOutputPath = stagedArchiveDirectory?.Path ?? buildResult.OutputPath;
            var buildOptions = new ContainerImageBuildOptions
            {
                ImageName = buildResult.LocalImageName,
                Tag = exportsArchive ? tempImageTag : buildResult.LocalImageTag,
                Destination = buildResult.Destination,
                OutputPath = runtimeOutputPath,
                ImageFormat = buildResult.ImageFormat,
                TargetPlatform = buildResult.TargetPlatform ?? ContainerTargetPlatform.LinuxAmd64,
                RequiresLocalImageStore = true
            };

            if (exportsArchive)
            {
                // Docker and Podman both build locally before saving these archives. Isolate the
                // layered image too; rewriting archive metadata must never require tagging the daemon.
                temporaryImage = new TemporaryContainerImage(containerRuntime, tempImageName, logger);
            }

            await containerRuntime.BuildImageAsync(
                projectDirectory,
                tempDockerfilePath,
                buildOptions,
                [],
                [],
                null,
                cancellationToken).ConfigureAwait(false);

            if (stagedArchiveDirectory is not null)
            {
                var stagedArchivePath = ResourceExtensions.GetContainerImageArchivePath(
                    stagedArchiveDirectory.Path,
                    buildResult.LocalImageName,
                    tempImageTag);
                var normalizedArchivePath = Path.Combine(stagedArchiveDirectory.Path, "normalized.tar");
                await ContainerImageArchiveRewriter.RewriteImageTagAsync(
                    stagedArchivePath,
                    normalizedArchivePath,
                    buildResult.LocalImageName,
                    tempImageTag,
                    buildResult.LocalImageTag,
                    cancellationToken).ConfigureAwait(false);
                var outputPath = IsExplicitArchiveOutputPath(buildResult.OutputPath!)
                    ? buildResult.OutputPath!
                    : ResourceExtensions.GetContainerImageArchivePath(
                        buildResult.OutputPath!,
                        buildResult.LocalImageName,
                        buildResult.LocalImageTag);
                await PublishArchiveAsync(
                    normalizedArchivePath,
                    outputPath,
                    logger,
                    cancellationToken).ConfigureAwait(false);
            }

            builtSuccessfully = true;
        }
        finally
        {
            if ((builtSuccessfully || exportsArchive) && tempDockerfilePath is not null && File.Exists(tempDockerfilePath))
            {
                try
                {
                    File.Delete(tempDockerfilePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Failed to delete temporary Dockerfile {DockerfilePath}", tempDockerfilePath);
                }
            }
            else if (!builtSuccessfully && tempDockerfilePath is not null)
            {
                logger.LogDebug("Failed build - temporary Dockerfile left at {DockerfilePath} for debugging", tempDockerfilePath);
            }

            if (stagedArchiveDirectory is not null)
            {
                try
                {
                    stagedArchiveDirectory.Dispose();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Failed to delete temporary container archive directory {ArchiveDirectory}", stagedArchiveDirectory.Path);
                }
            }

            if (temporaryImage is not null)
            {
                await temporaryImage.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static bool IsExplicitArchiveOutputPath(string outputPath)
    {
        // The SDK accepts arbitrary file extensions, such as "image.custom". A trailing
        // directory separator makes a dotted path such as "artifacts.v1\" unambiguous.
        // https://github.com/dotnet/sdk/blob/v10.0.400/src/Containers/Microsoft.NET.Build.Containers/LocalDaemons/ArchiveFileRegistry.cs
        return !Directory.Exists(outputPath) &&
            (File.Exists(outputPath) || Path.HasExtension(outputPath));
    }

    private static async Task PublishArchiveAsync(
        string sourcePath,
        string destinationPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var tempPath = Path.Combine(
            string.IsNullOrEmpty(destinationDirectory) ? Directory.GetCurrentDirectory() : destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        var tempFileCreated = false;

        try
        {
            // Keep the staging file beside the destination so the final replacement is same-volume and atomic.
            // FileMode.CreateNew prevents a path race even if an unlikely random-name collision occurs.
            using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true))
            using (var destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                tempFileCreated = true;
                if (destinationPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
                    destinationPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                {
                    using var gzip = new GZipStream(destination, CompressionLevel.Optimal);
                    await source.CopyToAsync(gzip, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath))
            {
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
        }
        finally
        {
            if (tempFileCreated && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Failed to delete incomplete container archive {ArchivePath}", tempPath);
                }
            }
        }
    }
}
