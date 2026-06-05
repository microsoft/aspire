// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

internal sealed class DockerContainerRuntime : ContainerRuntimeBase<DockerContainerRuntime>
{
    public DockerContainerRuntime(ILogger<DockerContainerRuntime> logger) : base(logger)
    {
    }

    protected override string RuntimeExecutable => "docker";
    public override string Name => "Docker";
    private async Task<int> RunDockerBuildAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, string?> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        var imageName = !string.IsNullOrEmpty(options?.Tag)
            ? $"{options.ImageName}:{options.Tag}"
            : options?.ImageName ?? throw new ArgumentException("ImageName must be provided in options.", nameof(options));

        string? builderName = null;
        var resourceName = imageName.Replace('/', '-').Replace(':', '-');

        // Docker requires a custom buildkit instance for the image when
        // targeting the OCI format so we construct it and remove it here.
        if (options?.ImageFormat == ContainerImageFormat.Oci)
        {
            if (string.IsNullOrEmpty(options?.OutputPath))
            {
                throw new ArgumentException("OutputPath must be provided when ImageFormat is Oci.", nameof(options));
            }

            builderName = $"{resourceName}-builder";
            var createBuilderResult = await CreateBuildkitInstanceAsync(builderName, cancellationToken).ConfigureAwait(false);

            if (createBuilderResult != 0)
            {
                Logger.LogError("Failed to create buildkit instance {BuilderName} with exit code {ExitCode}.", builderName, createBuilderResult);
                return createBuilderResult;
            }
        }

        try
        {
            var arguments = $"buildx build --file \"{dockerfilePath}\" --tag \"{imageName}\"";

            // Use the specific builder for OCI builds
            if (!string.IsNullOrEmpty(builderName))
            {
                arguments += $" --builder \"{builderName}\"";
            }

            // Add platform support if specified
            if (options?.TargetPlatform is not null)
            {
                arguments += $" --platform \"{options.TargetPlatform.Value.ToRuntimePlatformString()}\"";
            }

            // Add output format support if specified
            if (options?.ImageFormat is not null || !string.IsNullOrEmpty(options?.OutputPath))
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
                    var archivePath = ResourceExtensions.GetContainerImageArchivePath(options.OutputPath, resourceName, imageTag: null);
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

            var spec = new ProcessSpec("docker")
            {
                Arguments = arguments,
                OnOutputData = output =>
                {
                    Logger.LogDebug("docker buildx (stdout): {Output}", output);
                },
                OnErrorData = error =>
                {
                    Logger.LogDebug("docker buildx (stderr): {Error}", error);
                },
                ThrowOnNonZeroReturnCode = false,
                InheritEnv = true,
            };

            // Add build secrets as environment variables
            foreach (var buildSecret in buildSecrets)
            {
                if (buildSecret.Value is not null)
                {
                    spec.EnvironmentVariables[buildSecret.Key.ToUpperInvariant()] = buildSecret.Value;
                }
            }

            Logger.LogDebug("Running Docker CLI with arguments: {ArgumentList}", spec.Arguments);
            var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

            await using (processDisposable)
            {
                var processResult = await pendingProcessResult
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (processResult.ExitCode != 0)
                {
                    Logger.LogError("docker buildx for {ImageName} failed with exit code {ExitCode}.", imageName, processResult.ExitCode);
                    return processResult.ExitCode;
                }

                Logger.LogInformation("docker buildx for {ImageName} succeeded.", imageName);
                return processResult.ExitCode;
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

    public override async Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, string?> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        // Normalize the context path to handle trailing slashes and relative paths
        var normalizedContextPath = Path.GetFullPath(contextPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var exitCode = await RunDockerBuildAsync(
            normalizedContextPath,
            dockerfilePath,
            options,
            buildArguments,
            buildSecrets,
            stage,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new DistributedApplicationException($"Docker build failed with exit code {exitCode}.");
        }
    }

    public override async Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken)
    {
        // First check if Docker daemon is running using the same check that DCP uses
        if (!await CheckDockerDaemonAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Then check if Docker buildx is available
        return await CheckDockerBuildxAsync(cancellationToken).ConfigureAwait(false);
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
                "Docker buildx is available and running.",
                cancellationToken,
                Array.Empty<object>()).ConfigureAwait(false);
            
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<int> CreateBuildkitInstanceAsync(string builderName, CancellationToken cancellationToken)
    {
        var arguments = $"buildx create --name \"{builderName}\" --driver docker-container";

        return await ExecuteContainerCommandWithExitCodeAsync(
            arguments,
            "Failed to create buildkit instance {BuilderName} with exit code {ExitCode}.",
            "Successfully created buildkit instance {BuilderName}.",
            cancellationToken,
            new object[] { builderName }).ConfigureAwait(false);
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
