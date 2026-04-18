// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

internal sealed class DockerContainerRuntime : ContainerRuntimeBase<DockerContainerRuntime>
{
    public DockerContainerRuntime(ILogger<DockerContainerRuntime> logger) : base(logger)
    {
    }

    protected override string RuntimeExecutable => "docker";
    public override string Name => "Docker";
    private async Task RunDockerBuildAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
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
            await CreateBuildkitInstanceAsync(builderName, cancellationToken).ConfigureAwait(false);
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

            // Prepare environment variables for build secrets
            var environmentVariables = new Dictionary<string, string>();
            foreach (var buildSecret in buildSecrets)
            {
                if (buildSecret.Value.Type == BuildImageSecretType.Environment && buildSecret.Value.Value is not null)
                {
                    environmentVariables[buildSecret.Key.ToUpperInvariant()] = buildSecret.Value.Value;
                }
            }

            var buildOutput = new BuildOutputCapture();

            var exitCode = await ExecuteContainerCommandWithExitCodeAsync(
                arguments,
                "Docker build for {ImageName} failed with exit code {ExitCode}.",
                "Docker build for {ImageName} succeeded.",
                cancellationToken,
                new object[] { imageName },
                environmentVariables,
                buildOutput).ConfigureAwait(false);

            if (exitCode != 0)
            {
                throw new ProcessFailedException(
                    $"Docker build failed with exit code {exitCode}.",
                    exitCode,
                    buildOutput.ToArray(),
                    buildOutput.TotalLineCount);
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
        var buildOutput = new BuildOutputCapture();

        var exitCode = await ExecuteContainerCommandWithExitCodeAsync(
            arguments,
            "Failed to create buildkit instance {BuilderName} with exit code {ExitCode}.",
            "Successfully created buildkit instance {BuilderName}.",
            cancellationToken,
            new object[] { builderName },
            outputBuffer: buildOutput).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new ProcessFailedException(
                $"Failed to create buildkit instance '{builderName}' with exit code {exitCode}.",
                exitCode,
                buildOutput.ToArray(),
                buildOutput.TotalLineCount);
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
