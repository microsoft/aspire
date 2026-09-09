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
            var buildEnvironmentProviders = stepResource.Annotations
                .OfType<IDotnetProgramBuildEnvironmentProvider>()
                .ToArray();
            var steps = new List<PipelineStep>();

            if (stepResource.IsExcludedFromPublish())
            {
                return steps;
            }

            var buildStep = new PipelineStep
            {
                Name = $"build-{stepResource.Name}",
                Description = $"Builds the container image for the {stepResource.Name} project.",
                Action = context => BuildImageAsync(stepResource, buildEnvironmentProviders, context),
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
        IReadOnlyList<IDotnetProgramBuildEnvironmentProvider> buildEnvironmentProviders,
        PipelineStepContext context)
    {
        var currentProviders = resource.Annotations
            .OfType<IDotnetProgramBuildEnvironmentProvider>();
        if (!currentProviders.SequenceEqual(buildEnvironmentProviders, ReferenceEqualityComparer.Instance))
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
        using var buildResult = await dotnetProgramImageBuilder.BuildDotnetProgramImageAsync(
            resource,
            buildEnvironmentProviders,
            readiness.EnsureRunningAsync,
            context.CancellationToken).ConfigureAwait(false);

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
        var sourceImageName = string.Equals(buildResult.LocalImageTag, "latest", StringComparison.Ordinal)
            ? buildResult.LocalImageName
            : $"{buildResult.LocalImageName}:{buildResult.LocalImageTag}";
        var tempImageName = $"{buildResult.LocalImageName}:temp-{Guid.NewGuid():N}";
        var containerRuntime = await services
            .GetRequiredService<IContainerRuntimeResolver>()
            .ResolveAsync(cancellationToken)
            .ConfigureAwait(false);
        var directoryService = services.GetRequiredService<IFileSystemService>();
        var removeSourceImage = buildResult.Destination == ContainerImageDestination.Archive;
        var tempImageCreated = false;
        TempDirectory? stagedArchiveDirectory = null;
        string? tempDockerfilePath = null;
        var builtSuccessfully = false;

        try
        {
            stagedArchiveDirectory = ShouldStageArchive(buildResult)
                ? directoryService.TempDirectory.CreateTempSubdirectory("aspire-container-archive")
                : null;

            logger.LogDebug("Tagging image {SourceImageName} as {TempImageName}", sourceImageName, tempImageName);
            await containerRuntime.TagImageAsync(sourceImageName, tempImageName, cancellationToken).ConfigureAwait(false);
            tempImageCreated = true;

            var dockerfileBuilder = new DockerfileBuilder();
            dockerfileBuilder.AddContainerFilesStages(resource, logger);
            dockerfileBuilder
                .From(tempImageName)
                .AddContainerFiles(resource, buildResult.ContainerWorkingDirectory, logger);

            var projectDirectory = Path.GetDirectoryName(projectMetadata.ProjectPath)!;
            tempDockerfilePath = directoryService.TempDirectory.CreateTempFile("Dockerfile").Path;
            using (var writer = new StreamWriter(tempDockerfilePath))
            {
                await dockerfileBuilder.WriteAsync(writer, cancellationToken).ConfigureAwait(false);
            }

            var runtimeOutputPath = stagedArchiveDirectory?.Path ?? buildResult.OutputPath;
            if (buildResult.Destination == ContainerImageDestination.Archive &&
                runtimeOutputPath is not null &&
                stagedArchiveDirectory is null)
            {
                Directory.CreateDirectory(runtimeOutputPath);
            }

            var buildOptions = new ContainerImageBuildOptions
            {
                ImageName = buildResult.LocalImageName,
                Tag = buildResult.LocalImageTag,
                Destination = buildResult.Destination,
                OutputPath = runtimeOutputPath,
                ImageFormat = buildResult.ImageFormat,
                TargetPlatform = buildResult.TargetPlatform ?? ContainerTargetPlatform.LinuxAmd64
            };

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
                    buildResult.LocalImageTag);
                await MoveArchiveAsync(
                    stagedArchivePath,
                    buildResult.OutputPath!,
                    logger,
                    cancellationToken).ConfigureAwait(false);
            }

            builtSuccessfully = true;
        }
        finally
        {
            if (builtSuccessfully && tempDockerfilePath is not null && File.Exists(tempDockerfilePath))
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

            if (tempImageCreated)
            {
                await RemoveImageBestEffortAsync(containerRuntime, tempImageName, logger).ConfigureAwait(false);
            }

            if (removeSourceImage)
            {
                await RemoveImageBestEffortAsync(containerRuntime, sourceImageName, logger).ConfigureAwait(false);
            }
        }
    }

    private static bool ShouldStageArchive(DotnetProgramImageBuildResult buildResult)
    {
        return buildResult.Destination == ContainerImageDestination.Archive &&
            buildResult.OutputPath is { } outputPath &&
            IsExplicitArchiveOutputPath(outputPath);
    }

    internal static bool IsExplicitArchiveOutputPath(string outputPath)
    {
        // The SDK accepts arbitrary file extensions, such as "image.custom". A trailing
        // directory separator makes a dotted path such as "artifacts.v1\" unambiguous.
        // https://github.com/dotnet/sdk/blob/v10.0.400/src/Containers/Microsoft.NET.Build.Containers/LocalDaemons/ArchiveFileRegistry.cs
        return !Directory.Exists(outputPath) &&
            (File.Exists(outputPath) || Path.HasExtension(outputPath));
    }

    private static async Task MoveArchiveAsync(
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

        if (destinationPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
            destinationPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await CompressArchiveAsync(sourcePath, destinationPath, logger, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            File.Move(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static async Task CompressArchiveAsync(
        string sourcePath,
        string destinationPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(destinationDirectory) ? Directory.GetCurrentDirectory() : destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

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
            using (var gzip = new GZipStream(destination, CompressionLevel.Optimal))
            {
                await source.CopyToAsync(gzip, cancellationToken).ConfigureAwait(false);
            }

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
            if (File.Exists(tempPath))
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

    private static async Task RemoveImageBestEffortAsync(
        IContainerRuntime containerRuntime,
        string imageName,
        ILogger logger)
    {
        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await containerRuntime.RemoveImageAsync(imageName, cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove temporary container image {ImageName}", imageName);
        }
    }
}
