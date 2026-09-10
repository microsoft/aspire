// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREEXTENSION001

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Internal;
using Aspire.Hosting.Utils;
using Aspire.Shared;
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

internal interface IDotnetProgramContainerImageManager
{
    Task<DotnetProgramImageBuildResult> BuildDotnetProgramImageAsync(
        IResource resource,
        IReadOnlyList<IDotnetProgramBuildEnvironmentProvider> buildEnvironmentProviders,
        Func<IContainerRuntime, CancellationToken, Task> ensureContainerRuntimeRunning,
        CancellationToken cancellationToken);
}

internal sealed class DotnetProgramImageBuildResult(
    string localImageName,
    string localImageTag,
    ContainerTargetPlatform? targetPlatform,
    ContainerImageDestination? destination,
    string? outputPath,
    ContainerImageFormat? imageFormat,
    string containerWorkingDirectory,
    IDisposable? buildContext) : IDisposable
{
    public string LocalImageName { get; } = localImageName;

    public string LocalImageTag { get; } = localImageTag;

    public ContainerTargetPlatform? TargetPlatform { get; } = targetPlatform;

    public ContainerImageDestination? Destination { get; } = destination;

    public string? OutputPath { get; } = outputPath;

    public ContainerImageFormat? ImageFormat { get; } = imageFormat;

    public string ContainerWorkingDirectory { get; } = containerWorkingDirectory;

    public void Dispose() => buildContext?.Dispose();
}

internal sealed class ResourceContainerImageManager(
    ILogger<ResourceContainerImageManager> logger,
    IContainerRuntimeResolver containerRuntimeResolver,
    IProcessRunner processRunner,
    IServiceProvider serviceProvider,
    DistributedApplicationExecutionContext? executionContext = null) :
    IResourceContainerImageManager,
    IDotnetProgramContainerImageManager
{
    private const string CrossOsAotDiagnostic = "Cross-OS native compilation is not supported.";

    // Disable concurrent builds for project resources to avoid issues with overlapping msbuild projects
    private readonly SemaphoreSlim _throttle = new(1);

    private async Task<IContainerRuntime> GetContainerRuntimeAsync(CancellationToken cancellationToken)
        => await containerRuntimeResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);

    private sealed record ResolvedContainerBuildOptions
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
        var containerRuntime = await GetContainerRuntimeAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Starting to build container images");

        // Only check container runtime health if there are resources that need it
        if (await ResourcesRequireContainerRuntimeAsync(resources, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Checking {ContainerRuntimeName} health", containerRuntime.Name);

            var containerRuntimeHealthy = await containerRuntime.CheckIfRunningAsync(cancellationToken).ConfigureAwait(false);

            if (!containerRuntimeHealthy)
            {
                logger.LogError("Container runtime '{ContainerRuntimeName}' is not running or is unhealthy. Cannot build container images.", containerRuntime.Name);
                throw new InvalidOperationException($"Container runtime '{containerRuntime.Name}' is not running or is unhealthy.");
            }

            logger.LogDebug("{ContainerRuntimeName} is healthy", containerRuntime.Name);
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
        if (resource.SupportsDotnetProgramPublishing())
        {
            using var result = await BuildDotnetProgramImageAsync(
                resource,
                resource.Annotations.OfType<IDotnetProgramBuildEnvironmentProvider>().ToArray(),
                EnsureContainerRuntimeRunningAsync,
                cancellationToken).ConfigureAwait(false);

            if (resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out _))
            {
                await DotnetProgramPublishing.LayerContainerFilesAsync(
                    resource,
                    resource.GetProjectMetadata(),
                    result,
                    serviceProvider,
                    logger,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var containerRuntime = await GetContainerRuntimeAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Building container image for resource {ResourceName}", resource.Name);

        var options = await ResolveContainerBuildOptionsAsync(resource, cancellationToken).ConfigureAwait(false);

        if (ResourceRequiresContainerRuntime(resource, options))
        {
            logger.LogDebug("Checking {ContainerRuntimeName} health", containerRuntime.Name);

            var containerRuntimeHealthy = await containerRuntime.CheckIfRunningAsync(cancellationToken).ConfigureAwait(false);

            if (!containerRuntimeHealthy)
            {
                logger.LogError("Container runtime '{ContainerRuntimeName}' is not running or is unhealthy. Cannot build container image.", containerRuntime.Name);
                throw new InvalidOperationException($"Container runtime '{containerRuntime.Name}' is not running or is unhealthy.");
            }

            logger.LogDebug("{ContainerRuntimeName} is healthy", containerRuntime.Name);
        }

        if (resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
        {
            if (!resource.TryGetContainerImageName(out var imageName))
            {
                throw new InvalidOperationException($"The container image name for resource '{resource.Name}' could not be determined.");
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
            throw new NotSupportedException($"The resource '{resource.Name}' of type '{resource.GetType().Name}' is not supported.");
        }
    }

    async Task<DotnetProgramImageBuildResult> IDotnetProgramContainerImageManager.BuildDotnetProgramImageAsync(
        IResource resource,
        IReadOnlyList<IDotnetProgramBuildEnvironmentProvider> buildEnvironmentProviders,
        Func<IContainerRuntime, CancellationToken, Task> ensureContainerRuntimeRunning,
        CancellationToken cancellationToken) =>
        await BuildDotnetProgramImageAsync(resource, buildEnvironmentProviders, ensureContainerRuntimeRunning, cancellationToken).ConfigureAwait(false);

    private async Task<DotnetProgramImageBuildResult> BuildDotnetProgramImageAsync(
        IResource resource,
        IReadOnlyList<IDotnetProgramBuildEnvironmentProvider> buildEnvironmentProviders,
        Func<IContainerRuntime, CancellationToken, Task> ensureContainerRuntimeRunning,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Building container image for resource {ResourceName}", resource.Name);
        var options = await ResolveContainerBuildOptionsAsync(resource, cancellationToken).ConfigureAwait(false);
        if (options.Destination == ContainerImageDestination.Archive &&
            string.IsNullOrEmpty(options.OutputPath))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has Destination set to Archive but OutputPath is not configured. " +
                "Please set the OutputPath in the container build options.");
        }

        var hasContainerFiles = resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out _);
        var containerRuntime = await GetContainerRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (hasContainerFiles || ResourceRequiresContainerRuntime(resource, options))
        {
            await ensureContainerRuntimeRunning(containerRuntime, cancellationToken).ConfigureAwait(false);
        }

        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        DotnetProgramBuildContext? buildContext = null;

        try
        {
            logger.LogInformation("Building image: {ResourceName}", resource.Name);

            var projectMetadata = resource.GetProjectMetadata();
            buildContext = await CreateDotnetProgramBuildContextAsync(
                resource,
                projectMetadata,
                buildEnvironmentProviders,
                cancellationToken).ConfigureAwait(false);

            var sdkPublishOptions = hasContainerFiles && options.Destination == ContainerImageDestination.Archive
                ? options with
                {
                    Destination = null,
                    OutputPath = null,
                    ImageFormat = null
                }
                : options;
            await ExecuteDotnetPublishAsync(
                resource,
                projectMetadata,
                sdkPublishOptions,
                buildContext,
                cancellationToken).ConfigureAwait(false);

            var containerWorkingDirectory = hasContainerFiles
                ? await GetContainerWorkingDirectoryAsync(
                    projectMetadata,
                    options,
                    buildContext,
                    cancellationToken).ConfigureAwait(false)
                : "/app";

            logger.LogInformation("Building image for {ResourceName} completed", resource.Name);
            var result = new DotnetProgramImageBuildResult(
                options.LocalImageName,
                options.LocalImageTag,
                options.TargetPlatform,
                options.Destination,
                options.OutputPath,
                options.ImageFormat,
                containerWorkingDirectory,
                buildContext);
            buildContext = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Building image for {ResourceName} failed", resource.Name);
            throw;
        }
        finally
        {
            buildContext?.Dispose();
            _throttle.Release();
        }
    }

    private async Task EnsureContainerRuntimeRunningAsync(IContainerRuntime containerRuntime, CancellationToken cancellationToken)
    {
        logger.LogDebug("Checking {ContainerRuntimeName} health", containerRuntime.Name);
        if (!await containerRuntime.CheckIfRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogError("Container runtime '{ContainerRuntimeName}' is not running or is unhealthy. Cannot build container image.", containerRuntime.Name);
            throw new InvalidOperationException($"Container runtime '{containerRuntime.Name}' is not running or is unhealthy.");
        }
    }

    private async Task ExecuteDotnetPublishAsync(
        IResource resource,
        IProjectMetadata projectMetadata,
        ResolvedContainerBuildOptions options,
        DotnetProgramBuildContext buildContext,
        CancellationToken cancellationToken)
    {
        var containerRuntime = await GetContainerRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var arguments = new List<string>
        {
            "publish",
            projectMetadata.ProjectPath,
            "--configuration",
            "Release",
            "/t:PublishContainer",
            $"/p:ContainerRepository={options.LocalImageName}",
            $"/p:ContainerImageTag={options.LocalImageTag}"
        };

        if (GetLocalRegistryName(containerRuntime) is string localRegistry)
        {
            arguments.Add($"/p:LocalRegistry={localRegistry}");
        }

        if (!string.IsNullOrEmpty(options.OutputPath))
        {
            arguments.Add($"/p:ContainerArchiveOutputPath={options.OutputPath}");
        }

        if (options.ImageFormat is not null)
        {
            var format = options.ImageFormat.Value switch
            {
                ContainerImageFormat.Docker => "Docker",
                ContainerImageFormat.Oci => "OCI",
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.ImageFormat, "Invalid container image format")
            };
            arguments.Add($"/p:ContainerImageFormat={format}");
        }

        AddTargetPlatformArguments(arguments, options.TargetPlatform);

        if (resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation) &&
            baseImageAnnotation.RuntimeImage is string baseImage)
        {
            arguments.Add($"/p:ContainerBaseImage={baseImage}");
        }

        if (buildContext.ResponseFile is not null)
        {
            arguments.Add(buildContext.ResponseFile.Argument);
        }

        // The SDK can emit "error : Cross-OS native compilation is not supported." before
        // other output. Observe both streams before the retained output tail truncates it.
        var crossOsAotDiagnosticSeen = 0;
        void ObserveAotDiagnostic(string line)
        {
            if (line.Contains(CrossOsAotDiagnostic, StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref crossOsAotDiagnosticSeen, 1);
            }
        }

        var spec = new ProcessSpec("dotnet")
        {
            ArgumentList = arguments,
            WorkingDirectory = buildContext.WorkingDirectory,
            EnvironmentVariables = new Dictionary<string, string>(buildContext.Environment, buildContext.Environment.Comparer),
            ThrowOnNonZeroReturnCode = false,
            RetainedOutputLineCount = ProcessSpec.DefaultRetainedOutputLineCount,
            OnOutputData = output =>
            {
                ObserveAotDiagnostic(output);
                logger.LogDebug("dotnet publish {ProjectPath} (stdout): {Output}", projectMetadata.ProjectPath, output);
            },
            OnErrorData = error =>
            {
                ObserveAotDiagnostic(error);
                logger.LogDebug("dotnet publish {ProjectPath} (stderr): {Error}", projectMetadata.ProjectPath, error);
            }
        };

        logger.LogDebug(
            "Starting .NET CLI with arguments: {Arguments}",
            string.Join(" ", arguments));

        var (pendingProcessResult, processDisposable) = processRunner.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                var message =
                    $"Failed to build container image for resource '{resource.Name}' from project '{projectMetadata.ProjectPath}' with exit code {processResult.ExitCode}.";
                var crossOsDiagnosticPresent = Volatile.Read(ref crossOsAotDiagnosticSeen) != 0 ||
                    processResult.ProcessOutput.Any(static line => line.Contains(CrossOsAotDiagnostic, StringComparison.Ordinal));
                var guidance = GetFileAppAotGuidance(
                    projectMetadata,
                    options,
                    crossOsDiagnosticPresent);
                if (guidance is not null)
                {
                    message = $"{message}{Environment.NewLine}{guidance}";
                }

                throw new ProcessFailedException(
                    message,
                    processResult.ExitCode,
                    processResult.ProcessOutput,
                    processResult.TotalProcessOutputLineCount);
            }
            else
            {
                logger.LogDebug(
                    ".NET CLI completed with exit code: {ExitCode}",
                    processResult.ExitCode);
            }
        }
    }

    private static string? GetFileAppAotGuidance(
            IProjectMetadata projectMetadata,
            ResolvedContainerBuildOptions options,
            bool crossOsDiagnosticPresent)
    {
        if (!projectMetadata.IsFileBasedApp ||
            !IsCrossOperatingSystemTarget(options.TargetPlatform) ||
            !crossOsDiagnosticPresent)
        {
            return null;
        }

        var targetRuntimeIdentifiers = options.TargetPlatform?.ToMSBuildRuntimeIdentifierString() ?? "the configured target";
        return $"Native AOT cannot publish this file-based app for '{targetRuntimeIdentifiers}' from the current operating system. " +
            "File-based apps enable PublishAot by default. Add '#:property PublishAot=false' to the C# file, " +
            "or run Aspire publishing on the target operating system to retain Native AOT.";
    }

    private static bool IsCrossOperatingSystemTarget(ContainerTargetPlatform? targetPlatform)
    {
        if (targetPlatform is null)
        {
            return false;
        }

        var targetsLinux = (targetPlatform.Value & ContainerTargetPlatform.AllLinux) != 0 ||
            targetPlatform.Value.HasFlag(ContainerTargetPlatform.LinuxArm) ||
            targetPlatform.Value.HasFlag(ContainerTargetPlatform.Linux386);
        var targetsWindows = targetPlatform.Value.HasFlag(ContainerTargetPlatform.WindowsAmd64) ||
            targetPlatform.Value.HasFlag(ContainerTargetPlatform.WindowsArm64);

        return OperatingSystem.IsWindows()
            ? targetsLinux
            : targetsWindows || (OperatingSystem.IsMacOS() && targetsLinux);
    }

    private async Task<DotnetProgramBuildContext> CreateDotnetProgramBuildContextAsync(
            IResource resource,
            IProjectMetadata projectMetadata,
            IReadOnlyList<IDotnetProgramBuildEnvironmentProvider> providers,
            CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var unresolvedEnvironment = new Dictionary<string, object>(comparer);

        if (providers.Count > 0)
        {
            var activeExecutionContext = executionContext ?? throw new InvalidOperationException(
                $"An execution context is required to evaluate the build environment for resource '{resource.Name}'.");
            var callbackContext = new EnvironmentCallbackContext(
                activeExecutionContext,
                resource,
                unresolvedEnvironment,
                cancellationToken)
            {
                Logger = logger
            };

            foreach (var provider in providers)
            {
                await provider.ApplyAsync(callbackContext).ConfigureAwait(false);
            }
        }

        var environment = new Dictionary<string, string>(unresolvedEnvironment.Count, comparer);
        foreach (var (name, value) in unresolvedEnvironment)
        {
            if (value is not string stringValue)
            {
                throw new DistributedApplicationException(
                    $"The build environment variable '{name}' for .NET program resource '{resource.Name}' " +
                    $"has unsupported value type '{value?.GetType().Name ?? "null"}'. Build environment values must be strings.");
            }

            environment[name] = stringValue;
        }

        var responseFile = await MsBuildResponseFileFactory.CreateAsync(
            environment,
            logger,
            cancellationToken).ConfigureAwait(false);
        var workingDirectory = projectMetadata.BuildWorkingDirectory ??
            Path.GetDirectoryName(projectMetadata.ProjectPath);

        return new DotnetProgramBuildContext(environment, workingDirectory, responseFile);
    }

    private async Task<string> GetContainerWorkingDirectoryAsync(
            IProjectMetadata projectMetadata,
            ResolvedContainerBuildOptions options,
            DotnetProgramBuildContext buildContext,
            CancellationToken cancellationToken)
    {
        try
        {
            var outputLines = new List<string>();
            var command = projectMetadata.IsFileBasedApp ? "build" : "msbuild";
            var arguments = new List<string>
                {
                    command,
                    projectMetadata.ProjectPath,
                    "-p:Configuration=Release",
                    "-getProperty:ContainerWorkingDirectory",
                    "-v:q"
                };
            AddTargetPlatformArguments(arguments, options.TargetPlatform);
            if (buildContext.ResponseFile is not null)
            {
                arguments.Add(buildContext.ResponseFile.Argument);
            }

            var spec = new ProcessSpec("dotnet")
            {
                ArgumentList = arguments,
                WorkingDirectory = buildContext.WorkingDirectory,
                EnvironmentVariables = new Dictionary<string, string>(buildContext.Environment, buildContext.Environment.Comparer),
                OnOutputData = output =>
                {
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        outputLines.Add(output.Trim());
                    }
                },
                OnErrorData = error => logger.LogDebug("dotnet {Command} (stderr): {Error}", command, error),
                ThrowOnNonZeroReturnCode = false
            };

            logger.LogDebug(
                "Getting ContainerWorkingDirectory for .NET program {ProjectPath}",
                projectMetadata.ProjectPath);
            var (pendingResult, processDisposable) = processRunner.Run(spec);

            await using (processDisposable)
            {
                var result = await pendingResult.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    logger.LogDebug(
                        "Failed to get ContainerWorkingDirectory for .NET program {ProjectPath}. Exit code: {ExitCode}. Using default /app",
                        projectMetadata.ProjectPath,
                        result.ExitCode);
                    return "/app";
                }

                var workingDirectory = outputLines.LastOrDefault();
                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    logger.LogDebug(
                        "dotnet {Command} returned an empty ContainerWorkingDirectory for .NET program {ProjectPath}. Using default /app",
                        command,
                        projectMetadata.ProjectPath);
                    return "/app";
                }

                return workingDirectory;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error getting ContainerWorkingDirectory. Using default /app");
            return "/app";
        }
    }

    private static void AddTargetPlatformArguments(
            List<string> arguments,
            ContainerTargetPlatform? targetPlatform)
    {
        if (targetPlatform is null)
        {
            return;
        }

        var runtimeIdentifiers = targetPlatform.Value.ToMSBuildRuntimeIdentifierString();
        if (runtimeIdentifiers.Contains(';'))
        {
            // ArgumentList preserves "linux-x64;linux-arm64" as one argument, but MSBuild still
            // splits property switches on ';'. Escape the value for that second parsing layer.
            // https://github.com/dotnet/msbuild/issues/471
            arguments.Add(MsBuildResponseFileFactory.CreatePropertyArgument("RuntimeIdentifiers", runtimeIdentifiers));
            arguments.Add(MsBuildResponseFileFactory.CreatePropertyArgument("ContainerRuntimeIdentifiers", runtimeIdentifiers));
        }
        else
        {
            arguments.Add($"/p:RuntimeIdentifier={runtimeIdentifiers}");
            arguments.Add($"/p:ContainerRuntimeIdentifier={runtimeIdentifiers}");
        }
    }

    private sealed class DotnetProgramBuildContext(
            Dictionary<string, string> environment,
            string? workingDirectory,
            MsBuildResponseFile? responseFile) : IDisposable
    {
        public Dictionary<string, string> Environment { get; } = environment;

        public string? WorkingDirectory { get; } = workingDirectory;

        public MsBuildResponseFile? ResponseFile { get; } = responseFile;

        public void Dispose() => ResponseFile?.Dispose();
    }

    private static bool ResourceRequiresContainerRuntime(
        IResource resource,
        ResolvedContainerBuildOptions options)
    {
        if (resource.TryGetLastAnnotation<ContainerImageAnnotation>(out _) &&
            resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _))
        {
            return true;
        }

        if (options.Destination == ContainerImageDestination.Archive)
        {
            return false;
        }

        return options.ImageFormat is null or ContainerImageFormat.Docker ||
            options.OutputPath is null;
    }

    private static string? GetLocalRegistryName(IContainerRuntime containerRuntime)
    {
        // The .NET SDK container targets require these exact LocalRegistry values;
        // lower-case executable names fail with CONTAINER2002.
        if (string.Equals(containerRuntime.Name, KnownContainerRuntimes.Docker, StringComparison.OrdinalIgnoreCase))
        {
            return "Docker";
        }

        if (string.Equals(containerRuntime.Name, KnownContainerRuntimes.Podman, StringComparison.OrdinalIgnoreCase))
        {
            return "Podman";
        }

        return null;
    }

    private async Task BuildContainerImageFromDockerfileAsync(IResource resource, DockerfileBuildAnnotation dockerfileBuildAnnotation, string imageName, ResolvedContainerBuildOptions options, CancellationToken cancellationToken)
    {
        var containerRuntime = await GetContainerRuntimeAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Building image: {ResourceName}", resource.Name);

        // If there's a factory, generate the Dockerfile content and write it to the specified path
        if (dockerfileBuildAnnotation.DockerfileFactory is not null)
        {
            await DockerfileHelper.ExecuteDockerfileFactoryAsync(dockerfileBuildAnnotation, resource, serviceProvider, cancellationToken).ConfigureAwait(false);
        }

        // Resolve build arguments
        var resolvedBuildArguments = new Dictionary<string, string?>();
        foreach (var buildArg in dockerfileBuildAnnotation.BuildArguments)
        {
            resolvedBuildArguments[buildArg.Key] = await ResolveValue(buildArg.Value, cancellationToken).ConfigureAwait(false);
        }

        // Resolve build secrets
        var resolvedBuildSecrets = new Dictionary<string, BuildImageSecretValue>();
        foreach (var buildSecret in dockerfileBuildAnnotation.BuildSecrets)
        {
            var secretType = buildSecret.Value is FileInfo ? BuildImageSecretType.File : BuildImageSecretType.Environment;
            var resolvedValue = await ResolveValue(buildSecret.Value, cancellationToken).ConfigureAwait(false);
            resolvedBuildSecrets[buildSecret.Key] = new BuildImageSecretValue(resolvedValue, secretType);
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
            await containerRuntime.BuildImageAsync(
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
        var containerRuntime = await GetContainerRuntimeAsync(cancellationToken).ConfigureAwait(false);
        await containerRuntime.PushImageAsync(resource, cancellationToken).ConfigureAwait(false);
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
