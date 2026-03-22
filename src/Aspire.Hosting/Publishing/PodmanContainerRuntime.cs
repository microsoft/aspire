// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

internal sealed class PodmanContainerRuntime : ContainerRuntimeBase<PodmanContainerRuntime>
{
    public PodmanContainerRuntime(ILogger<PodmanContainerRuntime> logger) : base(logger)
    {
    }

    protected override string RuntimeExecutable => "podman";
    public override string Name => "Podman";
    private async Task<int> RunPodmanBuildAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        var imageName = !string.IsNullOrEmpty(options?.Tag)
            ? $"{options.ImageName}:{options.Tag}"
            : options?.ImageName ?? throw new ArgumentException("ImageName must be provided in options.", nameof(options));

        var arguments = $"build --file \"{dockerfilePath}\" --tag \"{imageName}\"";

        // Add platform support if specified
        if (options?.TargetPlatform is not null)
        {
            arguments += $" --platform \"{options.TargetPlatform.Value.ToRuntimePlatformString()}\"";
        }

        // Add format support if specified
        if (options?.ImageFormat is not null)
        {
            var format = options.ImageFormat.Value switch
            {
                ContainerImageFormat.Oci => "oci",
                ContainerImageFormat.Docker => "docker",
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.ImageFormat, "Invalid container image format")
            };
            arguments += $" --format \"{format}\"";
        }

        // Add output support if specified
        if (!string.IsNullOrEmpty(options?.OutputPath))
        {
            // Extract resource name from imageName for the file name
            var resourceName = imageName.Split('/').Last().Split(':').First();
            arguments += $" --output \"{Path.Combine(options.OutputPath, resourceName)}.tar\"";
        }

        // Add build arguments if specified
        arguments += BuildArgumentsString(buildArguments);

        // Add build secrets if specified
        arguments += BuildSecretsString(buildSecrets, requireValue: true);

        // Add stage if specified
        arguments += BuildStageString(stage);

        arguments += $" \"{contextPath}\"";

        // Prepare environment variables for build secrets (only for environment-type secrets)
        var environmentVariables = new Dictionary<string, string>();
        foreach (var buildSecret in buildSecrets)
        {
            if (buildSecret.Value.Type == BuildImageSecretType.Environment && buildSecret.Value.Value is not null)
            {
                environmentVariables[buildSecret.Key.ToUpperInvariant()] = buildSecret.Value.Value;
            }
        }

        return await ExecuteContainerCommandWithExitCodeAsync(
            arguments,
            "Podman build for {ImageName} failed with exit code {ExitCode}.",
            "Podman build for {ImageName} succeeded.",
            cancellationToken,
            new object[] { imageName },
            environmentVariables).ConfigureAwait(false);
    }

    public override async Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        var exitCode = await RunPodmanBuildAsync(
            contextPath,
            dockerfilePath,
            options,
            buildArguments,
            buildSecrets,
            stage,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new DistributedApplicationException($"Podman build failed with exit code {exitCode}.");
        }
    }

    public override async Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await ExecuteContainerCommandWithExitCodeAsync(
                "container ls -n 1",
                "Podman container ls failed with exit code {ExitCode}.",
                "Podman is running and healthy.",
                cancellationToken,
                Array.Empty<object>()).ConfigureAwait(false);
            
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
