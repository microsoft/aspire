// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

internal sealed class DockerContainerRuntime : ContainerRuntimeBase<DockerContainerRuntime>
{
    private const string LocalImageOciArchiveNotSupportedMessage =
        "Docker cannot export an OCI archive when container-file layering references locally built images. " +
        "Use ContainerImageFormat.Docker for this archive or run the publish with Podman.";

    public DockerContainerRuntime(ILogger<DockerContainerRuntime> logger, IProcessRunner processRunner) : base(logger, processRunner)
    {
    }

    protected override string RuntimeExecutable => KnownContainerRuntimes.Docker;
    public override string Name => "Docker";
    private async Task RunDockerBuildAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        var imageName = !string.IsNullOrEmpty(options?.Tag)
            ? $"{options.ImageName}:{options.Tag}"
            : options?.ImageName ?? throw new ArgumentException("ImageName must be provided in options.", nameof(options));

        string? builderName = null;
        var exportsArchive = !string.IsNullOrEmpty(options?.OutputPath);
        var requiresLocalImageStore = options?.RequiresLocalImageStore == true;
        var exportsLocalImageArchive = exportsArchive && requiresLocalImageStore;

        if (options?.ImageFormat == ContainerImageFormat.Oci)
        {
            if (!exportsArchive)
            {
                throw new ArgumentException("OutputPath must be provided when ImageFormat is Oci.", nameof(options));
            }
        }

        if (exportsArchive && !requiresLocalImageStore)
        {
            // Docker's in-daemon builder cannot reliably write Docker or OCI archive exporters.
            // Use an isolated BuildKit container for archive output and remove it after the build.
            // https://docs.docker.com/build/exporters/
            builderName = $"aspire-{Guid.NewGuid():N}";
            await CreateBuildkitInstanceAsync(builderName, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var arguments = $"buildx build --file \"{dockerfilePath}\" --tag \"{imageName}\"";

            if (requiresLocalImageStore)
            {
                // Container-file layering refers to images tagged only in the daemon's image store.
                // Pin the build to the daemon-backed builder instead of honoring an ambient Buildx selection.
                arguments += " --builder \"default\"";
            }
            else if (!string.IsNullOrEmpty(builderName))
            {
                arguments += $" --builder \"{builderName}\"";
            }

            // Add platform support if specified
            if (options?.TargetPlatform is not null)
            {
                arguments += $" --platform \"{options.TargetPlatform.Value.ToRuntimePlatformString()}\"";
            }

            // Add output format support if specified
            if (!exportsLocalImageArchive &&
                (options?.ImageFormat is not null || !string.IsNullOrEmpty(options?.OutputPath)))
            {
                var outputType = options?.ImageFormat switch
                {
                    ContainerImageFormat.Oci => "type=oci",
                    ContainerImageFormat.Docker => "type=docker",
                    null => "type=docker",
                    _ => throw new ArgumentOutOfRangeException(nameof(options), options.ImageFormat, "Invalid container image format")
                };

                if (!string.IsNullOrEmpty(options?.OutputPath))
                {
                    var archivePath = ResourceExtensions.GetContainerImageArchivePath(options.OutputPath, imageName);
                    outputType += $",dest={archivePath}";
                }

                arguments += $" --output \"{outputType}\"";
            }

            // Add build arguments if specified
            arguments += BuildArgumentsString(buildArguments);

            // Add build secrets if specified
            arguments += BuildSecretsString(buildSecrets);

            // Add stage if specified
            arguments += BuildStageString(stage);

            arguments += $" \"{contextPath}\"";

            // Prepare environment variables for build secrets
            var environmentVariables = new Dictionary<string, string>();
            foreach (var buildSecret in buildSecrets)
            {
                if (buildSecret.Value.Type == BuildImageSecretType.Environment && buildSecret.Value.Value is not null)
                {
                    environmentVariables[buildSecret.Key.ToUpperInvariant()] = buildSecret.Value.Value;
                }
            }

            var processResult = await ExecuteContainerCommandWithResultAsync(
                arguments,
                "Docker build for {ImageName} failed with exit code {ExitCode}.",
                "Docker build for {ImageName} succeeded.",
                cancellationToken,
                new object[] { imageName },
                environmentVariables,
                retainOutput: true).ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                throw new ProcessFailedException(
                    $"Docker build failed with exit code {processResult.ExitCode}.",
                    processResult.ExitCode,
                    processResult.ProcessOutput,
                    processResult.TotalProcessOutputLineCount);
            }

            if (exportsLocalImageArchive)
            {
                await RunDockerSaveAsync(imageName, options!, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Clean up the buildkit instance if we created one
            if (!string.IsNullOrEmpty(builderName))
            {
                await RemoveBuildkitInstanceAsync(builderName, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override async Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        if (options?.RequiresLocalImageStore == true &&
            !string.IsNullOrEmpty(options.OutputPath))
        {
            switch (options.ImageFormat)
            {
                case ContainerImageFormat.Oci:
                    throw new DistributedApplicationException(LocalImageOciArchiveNotSupportedMessage);
                case ContainerImageFormat.Docker:
                case null:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(options),
                        options.ImageFormat,
                        "Invalid container image format");
            }
        }

        // Verify buildx is available before attempting a Dockerfile build
        if (!await CheckDockerBuildxAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DistributedApplicationException(
                "Docker buildx is not available. Install the buildx plugin and try again.");
        }

        // Normalize the context path to handle trailing slashes and relative paths
        var normalizedContextPath = Path.GetFullPath(contextPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        await RunDockerBuildAsync(
            normalizedContextPath,
            dockerfilePath,
            options,
            buildArguments,
            buildSecrets,
            stage,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunDockerSaveAsync(
        string imageName,
        ContainerImageBuildOptions options,
        CancellationToken cancellationToken)
    {
        var arguments = BuildSaveArguments(imageName, options);
        var processResult = await ExecuteContainerCommandWithResultAsync(
            arguments,
            "Docker image save for {ImageName} failed with exit code {ExitCode}.",
            "Docker image save for {ImageName} succeeded.",
            cancellationToken,
            new object[] { imageName },
            retainOutput: true).ConfigureAwait(false);

        if (processResult.ExitCode != 0)
        {
            throw new ProcessFailedException(
                $"Docker image save failed with exit code {processResult.ExitCode}.",
                processResult.ExitCode,
                processResult.ProcessOutput,
                processResult.TotalProcessOutputLineCount);
        }
    }

    /// <summary>
    /// Builds the arguments that save a locally built image as a Docker archive.
    /// </summary>
    internal static string BuildSaveArguments(string imageName, ContainerImageBuildOptions options)
    {
        var outputPath = options.OutputPath;
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var archivePath = ResourceExtensions.GetContainerImageArchivePath(outputPath, imageName);
        var arguments = $"image save --output \"{archivePath}\"";

        if (options.TargetPlatform is not null)
        {
            arguments += $" --platform \"{options.TargetPlatform.Value.ToRuntimePlatformString()}\"";
        }

        return $"{arguments} \"{imageName}\"";
    }

    public override async Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken)
    {
        return await CheckDockerDaemonAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CheckDockerDaemonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await ExecuteContainerCommandWithExitCodeAsync(
                "container ls -n 1",
                "Docker daemon is not running. Exit code: {ExitCode}.",
                "Docker daemon is running.",
                cancellationToken,
                Array.Empty<object>()).ConfigureAwait(false);

            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckDockerBuildxAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await ExecuteContainerCommandWithExitCodeAsync(
                "buildx version",
                "Docker buildx version failed with exit code {ExitCode}.",
                "Docker buildx is available.",
                cancellationToken,
                Array.Empty<object>()).ConfigureAwait(false);

            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task CreateBuildkitInstanceAsync(string builderName, CancellationToken cancellationToken)
    {
        var arguments = $"buildx create --name \"{builderName}\" --driver docker-container";
        var processResult = await ExecuteContainerCommandWithResultAsync(
            arguments,
            "Failed to create buildkit instance {BuilderName} with exit code {ExitCode}.",
            "Successfully created buildkit instance {BuilderName}.",
            cancellationToken,
            new object[] { builderName },
            retainOutput: true).ConfigureAwait(false);

        if (processResult.ExitCode != 0)
        {
            throw new ProcessFailedException(
                $"Failed to create buildkit instance '{builderName}' with exit code {processResult.ExitCode}.",
                processResult.ExitCode,
                processResult.ProcessOutput,
                processResult.TotalProcessOutputLineCount);
        }
    }

    private async Task<int> RemoveBuildkitInstanceAsync(string builderName, CancellationToken cancellationToken)
    {
        var arguments = $"buildx rm \"{builderName}\"";

        return await ExecuteContainerCommandWithExitCodeAsync(
            arguments,
            "Failed to remove buildkit instance {BuilderName} with exit code {ExitCode}.",
            "Successfully removed buildkit instance {BuilderName}.",
            cancellationToken,
            new object[] { builderName }).ConfigureAwait(false);
    }
}
