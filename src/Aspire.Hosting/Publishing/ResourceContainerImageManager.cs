// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Specifies the format for container images.
/// </summary>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum ContainerImageFormat
{
    /// <summary>
    /// Docker format (default).
    /// </summary>
    Docker,

    /// <summary>
    /// OCI format.
    /// </summary>
    Oci
}

/// <summary>
/// Specifies the destination for container images.
/// </summary>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum ContainerImageDestination
{
    /// <summary>
    /// Image will be pushed to a container registry.
    /// </summary>
    Registry,

    /// <summary>
    /// Image will be saved as an archive file.
    /// </summary>
    Archive
}

/// <summary>
/// Specifies the target platform for container images.
/// </summary>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[Flags]
public enum ContainerTargetPlatform
{
    /// <summary>
    /// Linux AMD64 (linux/amd64).
    /// </summary>
    LinuxAmd64 = 1,

    /// <summary>
    /// Linux ARM64 (linux/arm64).
    /// </summary>
    LinuxArm64 = 2,

    /// <summary>
    /// Linux ARM (linux/arm).
    /// </summary>
    LinuxArm = 4,

    /// <summary>
    /// Linux 386 (linux/386).
    /// </summary>
    Linux386 = 8,

    /// <summary>
    /// Windows AMD64 (windows/amd64).
    /// </summary>
    WindowsAmd64 = 16,

    /// <summary>
    /// Windows ARM64 (windows/arm64).
    /// </summary>
    WindowsArm64 = 32,

    /// <summary>
    /// All Linux platforms (AMD64 and ARM64).
    /// </summary>
    AllLinux = LinuxAmd64 | LinuxArm64
}

/// <summary>
/// Options for building container images.
/// </summary>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class ContainerImageBuildOptions
{
    /// <summary>
    /// Gets the name to assign to the built image.
    /// </summary>
    public string? ImageName { get; init; }

    /// <summary>
    /// Gets the tag to assign to the built image.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Gets the destination for the container image.
    /// </summary>
    public ContainerImageDestination? Destination { get; init; }

    /// <summary>
    /// Gets the output path for the container archive.
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Gets the container image format.
    /// </summary>
    public ContainerImageFormat? ImageFormat { get; init; }

    /// <summary>
    /// Gets the target platform for the container.
    /// </summary>
    public ContainerTargetPlatform? TargetPlatform { get; init; }
}

/// <summary>
/// Provides a service to publishers for building and pushing container images that represent a resource.
/// </summary>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IResourceContainerImageManager
{
    /// <summary>
    /// Builds a container that represents the specified resource.
    /// </summary>
    /// <param name="resource">The resource to build.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task BuildImageAsync(IResource resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds container images for a collection of resources.
    /// </summary>
    /// <param name="resources">The resources to build images for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    Task BuildImagesAsync(IEnumerable<IResource> resources, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes a container image to a registry.
    /// </summary>
    /// <param name="resource">The resource to push.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task PushImageAsync(IResource resource, CancellationToken cancellationToken);
}

internal sealed class ResourceContainerImageManager(
    ILogger<ResourceContainerImageManager> logger,
    IContainerRuntime containerRuntime,
    IServiceProvider serviceProvider,
    DistributedApplicationExecutionContext? executionContext = null) : IResourceContainerImageManager
{
    // Disable concurrent builds for project resources to avoid issues with overlapping msbuild projects
    private readonly SemaphoreSlim _throttle = new(1);

    private IContainerRuntime ContainerRuntime { get; } = containerRuntime;

    private sealed class ResolvedContainerBuildOptions
    {
        public string? OutputPath { get; set; }
        public ContainerImageFormat? ImageFormat { get; set; }
        public ContainerTargetPlatform? TargetPlatform { get; set; }
        public ContainerImageDestination? Destination { get; set; }
        public string LocalImageName { get; set; } = string.Empty;
        public string LocalImageTag { get; set; } = "latest";
    }

    private async Task<ResolvedContainerBuildOptions> ResolveContainerBuildOptionsAsync(
        IResource resource,
        CancellationToken cancellationToken)
    {
        var options = new ResolvedContainerBuildOptions
        {
            LocalImageName = resource.Name,
            LocalImageTag = "latest"
        };

        var context = await resource.ProcessContainerBuildOptionsCallbackAsync(
            serviceProvider,
            logger,
            executionContext,
            cancellationToken).ConfigureAwait(false);

        options.OutputPath = context.OutputPath;
        options.ImageFormat = context.ImageFormat;
        options.TargetPlatform = context.TargetPlatform;
        options.Destination = context.Destination;
        options.LocalImageName = context.LocalImageName ?? options.LocalImageName;
        options.LocalImageTag = context.LocalImageTag ?? options.LocalImageTag;

        return options;
    }

    public async Task BuildImagesAsync(IEnumerable<IResource> resources, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting to build container images");

        // Only check container runtime health if there are resources that need it
        if (await ResourcesRequireContainerRuntimeAsync(resources, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Checking {ContainerRuntimeName} health", ContainerRuntime.Name);

            var containerRuntimeHealthy = await ContainerRuntime.CheckIfRunningAsync(cancellationToken).ConfigureAwait(false);

            if (!containerRuntimeHealthy)
            {
                logger.LogError("Container runtime '{ContainerRuntimeName}' is not running or is unhealthy. Cannot build container images.", ContainerRuntime.Name);
                throw new InvalidOperationException($"Container runtime '{ContainerRuntime.Name}' is not running or is unhealthy.");
            }

            logger.LogDebug("{ContainerRuntimeName} is healthy", ContainerRuntime.Name);
        }

        foreach (var resource in resources)
        {
            // TODO: Consider parallelizing this.
            await BuildImageAsync(resource, cancellationToken).ConfigureAwait(false);
        }

        logger.LogDebug("Building container images completed");
    }

    public async Task BuildImageAsync(IResource resource, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Building container image for resource {ResourceName}", resource.Name);

        var options = await ResolveContainerBuildOptionsAsync(resource, cancellationToken).ConfigureAwait(false);

        // Check if this resource needs a container runtime
        if (await ResourcesRequireContainerRuntimeAsync([resource], cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Checking {ContainerRuntimeName} health", ContainerRuntime.Name);

            var containerRuntimeHealthy = await ContainerRuntime.CheckIfRunningAsync(cancellationToken).ConfigureAwait(false);

            if (!containerRuntimeHealthy)
            {
                logger.LogError("Container runtime '{ContainerRuntimeName}' is not running or is unhealthy. Cannot build container image.", ContainerRuntime.Name);
                throw new InvalidOperationException($"Container runtime '{ContainerRuntime.Name}' is not running or is unhealthy.");
            }

            logger.LogDebug("{ContainerRuntimeName} is healthy", ContainerRuntime.Name);
        }

        if (resource is ProjectResource)
        {
            // If it is a project resource we need to build the container image
            // using the .NET SDK.
            await BuildProjectContainerImageAsync(
                resource,
                options,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        else if (resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
        {
            if (!resource.TryGetContainerImageName(out var imageName))
            {
                throw new InvalidOperationException("Resource image name could not be determined.");
            }

            // This is a container resource so we'll use the container runtime to build the image
            await BuildContainerImageFromDockerfileAsync(
                resource,
                dockerfileBuildAnnotation,
                imageName,
                options,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        else if (resource.TryGetLastAnnotation<ContainerImageAnnotation>(out var _))
        {
            // This resource already has a container image associated with it so no build is needed.
            logger.LogDebug("Resource {ResourceName} already has a container image associated and no build annotation. Skipping build.", resource.Name);
            return;
        }
        else
        {
            throw new NotSupportedException($"The resource type '{resource.GetType().Name}' is not supported.");
        }
    }

    private async Task BuildProjectContainerImageAsync(IResource resource, ResolvedContainerBuildOptions options, CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            logger.LogInformation("Building image: {ResourceName}", resource.Name);

            var success = await ExecuteDotnetPublishAsync(resource, options, cancellationToken).ConfigureAwait(false);

            if (!success)
            {
                logger.LogError("Building image for {ResourceName} failed", resource.Name);
                throw new DistributedApplicationException($"Failed to build container image.");
            }
            else
            {
                logger.LogInformation("Building image for {ResourceName} completed", resource.Name);
            }
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<bool> ExecuteDotnetPublishAsync(IResource resource, ResolvedContainerBuildOptions options, CancellationToken cancellationToken)
    {
        // This is a resource project so we'll use the .NET SDK to build the container image.
        if (!resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
        {
            throw new DistributedApplicationException($"The resource '{projectMetadata}' does not have a project metadata annotation.");
        }

        var arguments = $"publish \"{projectMetadata.ProjectPath}\" --configuration Release /t:PublishContainer /p:ContainerRepository=\"{options.LocalImageName}\" /p:ContainerImageTag=\"{options.LocalImageTag}\"";

        // Add additional arguments based on options
        if (!string.IsNullOrEmpty(options.OutputPath))
        {
            arguments += $" /p:ContainerArchiveOutputPath=\"{options.OutputPath}\"";
        }

        if (options.ImageFormat is not null)
        {
            var format = options.ImageFormat.Value switch
            {
                ContainerImageFormat.Docker => "Docker",
                ContainerImageFormat.Oci => "OCI",
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.ImageFormat, "Invalid container image format")
            };
            arguments += $" /p:ContainerImageFormat=\"{format}\"";
        }

        if (options.TargetPlatform is not null)
        {
            // Use the appropriate MSBuild properties based on the number of RIDs
            var runtimeIds = options.TargetPlatform.Value.ToMSBuildRuntimeIdentifierString();
            var ridArray = runtimeIds.Split(';');

            if (ridArray.Length == 1)
            {
                // Single platform - use RuntimeIdentifier/ContainerRuntimeIdentifier
                arguments += $" /p:RuntimeIdentifier=\"{ridArray[0]}\"";
                arguments += $" /p:ContainerRuntimeIdentifier=\"{ridArray[0]}\"";
            }
            else
            {
                // Multiple platforms - use RuntimeIdentifiers/ContainerRuntimeIdentifiers
                // MSBuild doesn't handle ';' in parameters well, need to escape the double quote. See https://github.com/dotnet/msbuild/issues/471
                arguments += $" /p:RuntimeIdentifiers=\\\"{runtimeIds}\\\"";
                arguments += $" /p:ContainerRuntimeIdentifiers=\\\"{runtimeIds}\\\"";
            }
        }

#pragma warning disable ASPIREDOCKERFILEBUILDER001
        if (resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation) &&
            baseImageAnnotation.RuntimeImage is string baseImage)
        {
            arguments += $" /p:ContainerBaseImage=\"{baseImage}\"";
        }
#pragma warning restore ASPIREDOCKERFILEBUILDER001

        var spec = new ProcessSpec("dotnet")
        {
            Arguments = arguments,
            OnOutputData = output =>
            {
                logger.LogDebug("dotnet publish {ProjectPath} (stdout): {Output}", projectMetadata.ProjectPath, output);
            },
            OnErrorData = error =>
            {
                logger.LogDebug("dotnet publish {ProjectPath} (stderr): {Error}", projectMetadata.ProjectPath, error);
            }
        };

        logger.LogDebug(
            "Starting .NET CLI with arguments: {Arguments}",
            string.Join(" ", spec.Arguments)
            );

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                logger.LogError("dotnet publish for project {ProjectPath} failed with exit code {ExitCode}.", projectMetadata.ProjectPath, processResult.ExitCode);
                return false;
            }
            else
            {
                logger.LogDebug(
                    ".NET CLI completed with exit code: {ExitCode}",
                    processResult.ExitCode);
                return true;
            }
        }
    }

    private async Task BuildContainerImageFromDockerfileAsync(IResource resource, DockerfileBuildAnnotation dockerfileBuildAnnotation, string imageName, ResolvedContainerBuildOptions options, CancellationToken cancellationToken)
    {
        logger.LogInformation("Building image: {ResourceName}", resource.Name);

        // If there's a factory, generate the Dockerfile content and write it to the specified path
        if (dockerfileBuildAnnotation.DockerfileFactory is not null)
        {
            var context = new DockerfileFactoryContext
            {
                Services = serviceProvider,
                Resource = resource,
                CancellationToken = cancellationToken
            };
            await dockerfileBuildAnnotation.MaterializeDockerfileAsync(context, cancellationToken).ConfigureAwait(false);
        }

        // Resolve build arguments
        var resolvedBuildArguments = new Dictionary<string, string?>();
        foreach (var buildArg in dockerfileBuildAnnotation.BuildArguments)
        {
            resolvedBuildArguments[buildArg.Key] = await ResolveValue(buildArg.Value, cancellationToken).ConfigureAwait(false);
        }

        // Resolve build secrets
        var resolvedBuildSecrets = new Dictionary<string, string?>();
        foreach (var buildSecret in dockerfileBuildAnnotation.BuildSecrets)
        {
            resolvedBuildSecrets[buildSecret.Key] = await ResolveValue(buildSecret.Value, cancellationToken).ConfigureAwait(false);
        }

        // ensure outputPath is created if specified since docker/podman won't create it for us
        if (options.OutputPath is { } outputPath)
        {
            Directory.CreateDirectory(outputPath);
        }

        // Parse image name and tag
        var imageNameParts = imageName.Split(':', 2);
        var imageNameOnly = imageNameParts[0];
        var imageTag = imageNameParts.Length > 1 ? imageNameParts[1] : null;

        // Create a ContainerImageBuildOptions for the container runtime
        var containerBuildOptions = new ContainerImageBuildOptions
        {
            ImageName = imageNameOnly,
            Tag = imageTag,
            OutputPath = options.OutputPath,
            ImageFormat = options.ImageFormat,
            TargetPlatform = options.TargetPlatform
        };

        try
        {
            await ContainerRuntime.BuildImageAsync(
                dockerfileBuildAnnotation.ContextPath,
                dockerfileBuildAnnotation.DockerfilePath,
                containerBuildOptions,
                resolvedBuildArguments,
                resolvedBuildSecrets,
                dockerfileBuildAnnotation.Stage,
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Building image for {ResourceName} completed", resource.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build container image from Dockerfile for {ResourceName}", resource.Name);
            throw;
        }
    }

    internal static async Task<string?> ResolveValue(object? value, CancellationToken cancellationToken)
    {
        try
        {
            return value switch
            {
                FileInfo filePath => filePath.FullName,
                string stringValue => stringValue,
                IValueProvider valueProvider => await valueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false),
                bool boolValue => boolValue ? "true" : "false",
                null => null,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }
        catch (MissingParameterValueException)
        {
            // If a parameter value is missing, we return null to indicate that the build argument or secret cannot be resolved
            // and we should fallback to resolving it from environment variables.
            return null;
        }
    }

    public async Task PushImageAsync(IResource resource, CancellationToken cancellationToken)
    {
        await ContainerRuntime.PushImageAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    // .NET Container builds that push OCI images to a local file path do not need a runtime
    private async Task<bool> ResourcesRequireContainerRuntimeAsync(IEnumerable<IResource> resources, CancellationToken cancellationToken)
    {
        foreach (var resource in resources)
        {
            // Dockerfile resources always need container runtime
            if (resource.TryGetLastAnnotation<ContainerImageAnnotation>(out _) &&
                resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _))
            {
                return true;
            }

            // Check if any resource uses Docker format or has no output path
            var options = await ResolveContainerBuildOptionsAsync(resource, cancellationToken).ConfigureAwait(false);

            // Skip resources that are explicitly configured to save as archives - they don't need Docker
            if (options.Destination == ContainerImageDestination.Archive)
            {
                continue;
            }

            var usesDocker = options.ImageFormat == null || options.ImageFormat == ContainerImageFormat.Docker;
            var hasNoOutputPath = options.OutputPath == null;

            if (usesDocker || hasNoOutputPath)
            {
                return true;
            }
        }

        return false;
    }

}

/// <summary>
/// Extension methods for <see cref="ContainerTargetPlatform"/>.
/// </summary>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
internal static class ContainerTargetPlatformExtensions
{
    /// <summary>
    /// Converts the target platform to the format used by container runtimes (Docker/Podman).
    /// </summary>
    /// <param name="platform">The target platform.</param>
    /// <returns>The platform string in the format used by container runtimes.</returns>
    public static string ToRuntimePlatformString(this ContainerTargetPlatform platform)
    {
        var platforms = new List<string>();

        if (platform.HasFlag(ContainerTargetPlatform.LinuxAmd64))
        {
            platforms.Add("linux/amd64");
        }
        if (platform.HasFlag(ContainerTargetPlatform.LinuxArm64))
        {
            platforms.Add("linux/arm64");
        }
        if (platform.HasFlag(ContainerTargetPlatform.LinuxArm))
        {
            platforms.Add("linux/arm");
        }
        if (platform.HasFlag(ContainerTargetPlatform.Linux386))
        {
            platforms.Add("linux/386");
        }
        if (platform.HasFlag(ContainerTargetPlatform.WindowsAmd64))
        {
            platforms.Add("windows/amd64");
        }
        if (platform.HasFlag(ContainerTargetPlatform.WindowsArm64))
        {
            platforms.Add("windows/arm64");
        }

        if (platforms.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown container target platform");
        }

        return string.Join(",", platforms);
    }

    /// <summary>
    /// Converts the target platform to the format used by MSBuild RuntimeIdentifiers.
    /// </summary>
    /// <param name="platform">The target platform.</param>
    /// <returns>The platform string in the format used by MSBuild.</returns>
    public static string ToMSBuildRuntimeIdentifierString(this ContainerTargetPlatform platform)
    {
        var rids = new List<string>();

        if (platform.HasFlag(ContainerTargetPlatform.LinuxAmd64))
        {
            rids.Add("linux-x64");
        }
        if (platform.HasFlag(ContainerTargetPlatform.LinuxArm64))
        {
            rids.Add("linux-arm64");
        }
        if (platform.HasFlag(ContainerTargetPlatform.LinuxArm))
        {
            rids.Add("linux-arm");
        }
        if (platform.HasFlag(ContainerTargetPlatform.Linux386))
        {
            rids.Add("linux-x86");
        }
        if (platform.HasFlag(ContainerTargetPlatform.WindowsAmd64))
        {
            rids.Add("win-x64");
        }
        if (platform.HasFlag(ContainerTargetPlatform.WindowsArm64))
        {
            rids.Add("win-arm64");
        }

        if (rids.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown container target platform");
        }

        return string.Join(";", rids);
    }
}
