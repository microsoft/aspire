// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

internal sealed class PodmanContainerRuntime : ContainerRuntimeBase<PodmanContainerRuntime>
{
    public PodmanContainerRuntime(ILogger<PodmanContainerRuntime> logger, IProcessRunner processRunner) : base(logger, processRunner)
    {
    }

    protected override string RuntimeExecutable => KnownContainerRuntimes.Podman;
    public override string Name => "Podman";

    /// <summary>
    /// Lists compose services using native <c>podman ps</c> with label filters,
    /// which works with both Docker Compose v2 and podman-compose providers.
    /// </summary>
    public override async Task<IReadOnlyList<ComposeServiceInfo>?> ComposeListServicesAsync(ComposeOperationContext context, CancellationToken cancellationToken)
    {
        await EnsureRuntimeAvailableAsync().ConfigureAwait(false);

        var arguments = $"ps --filter label=com.docker.compose.project={context.ProjectName} --format json";

        var outputLines = new List<string>();

        var spec = new ProcessSpec(RuntimeExecutable)
        {
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = output =>
            {
                if (!string.IsNullOrWhiteSpace(output))
                {
                    outputLines.Add(output);
                }
            },
            OnErrorData = error =>
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Logger.LogDebug("podman ps (stderr): {Error}", error);
                }
            }
        };

        var (pendingProcessResult, processDisposable) = ProcessRunner.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                Logger.LogDebug("podman ps failed with exit code {ExitCode}", processResult.ExitCode);
                return null;
            }
        }

        return ParsePodmanPsOutput(outputLines);
    }

    /// <summary>
    /// Parses native <c>podman ps --format json</c> output into normalized <see cref="ComposeServiceInfo"/> entries.
    /// Podman returns a JSON array. Containers are aggregated by compose service name.
    /// </summary>
    /// <example>
    /// <code>
    /// [{"Labels":{"com.docker.compose.service":"web"},"Ports":[{"host_ip":"","container_port":80,"host_port":8080,"range":1,"protocol":"tcp"}]}]
    /// </code>
    /// </example>
    internal static List<ComposeServiceInfo> ParsePodmanPsOutput(List<string> outputLines)
    {
        var allText = string.Join("", outputLines);
        if (string.IsNullOrWhiteSpace(allText))
        {
            return [];
        }

        List<PodmanPsEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize(allText, PodmanPsJsonContext.Default.ListPodmanPsEntry);
        }
        catch (JsonException)
        {
            return [];
        }

        if (entries is null)
        {
            return [];
        }

        // Group by compose service name since Podman may return multiple containers per service
        var grouped = new Dictionary<string, List<ComposeServicePort>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var serviceName = entry.Labels?.GetValueOrDefault("com.docker.compose.service");
            if (serviceName is null)
            {
                continue;
            }

            if (!grouped.TryGetValue(serviceName, out var ports))
            {
                ports = [];
                grouped[serviceName] = ports;
            }

            if (entry.Ports is not null)
            {
                foreach (var port in entry.Ports)
                {
                    ports.Add(new ComposeServicePort
                    {
                        PublishedPort = port.HostPort,
                        TargetPort = port.ContainerPort
                    });
                }
            }
        }

        return grouped.Select(g => new ComposeServiceInfo
        {
            Service = g.Key,
            Publishers = g.Value
        }).ToList();
    }
    private async Task RunPodmanBuildAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        var imageName = !string.IsNullOrEmpty(options?.Tag)
            ? $"{options.ImageName}:{options.Tag}"
            : options?.ImageName ?? throw new ArgumentException("ImageName must be provided in options.", nameof(options));

        if (options.ImageFormat == ContainerImageFormat.Oci && string.IsNullOrEmpty(options.OutputPath))
        {
            throw new ArgumentException("OutputPath must be provided when ImageFormat is Oci.", nameof(options));
        }

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

        // Archive output is deliberately NOT handled here.
        //
        // `podman build` has no image-archive output flag. Its `--output`/`-o` is a *filesystem* export
        // (`-o type=local,dest=...`), not the image-archive equivalent of
        // `docker buildx build --output type=oci,dest=...`, and it is rejected outright when talking to
        // a remote/machine-backed service:
        //   Error: '--output' option is not supported in remote mode
        // The image-archive equivalent is a normal tagged build followed by `podman save`, which
        // RunPodmanSaveAsync performs once the build below succeeds.
        // See https://docs.podman.io/en/latest/markdown/podman-build.1.html and
        // https://docs.podman.io/en/latest/markdown/podman-save.1.html

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

        var processResult = await ExecuteContainerCommandWithResultAsync(
            arguments,
            "Podman build for {ImageName} failed with exit code {ExitCode}.",
            "Podman build for {ImageName} succeeded.",
            cancellationToken,
            new object[] { imageName },
            environmentVariables,
            retainOutput: true).ConfigureAwait(false);

        if (processResult.ExitCode != 0)
        {
            throw new ProcessFailedException(
                $"Podman build failed with exit code {processResult.ExitCode}.",
                processResult.ExitCode,
                processResult.ProcessOutput,
                processResult.TotalProcessOutputLineCount);
        }

        if (!string.IsNullOrEmpty(options?.OutputPath))
        {
            await RunPodmanSaveAsync(imageName, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Exports an already-built image to an archive, which is how Podman produces the equivalent of
    /// <c>docker buildx build --output type=oci,dest=...</c>.
    /// </summary>
    private async Task RunPodmanSaveAsync(string imageName, ContainerImageBuildOptions options, CancellationToken cancellationToken)
    {
        var arguments = BuildSaveArguments(imageName, options);

        var processResult = await ExecuteContainerCommandWithResultAsync(
            arguments,
            "Podman save for {ImageName} failed with exit code {ExitCode}.",
            "Podman save for {ImageName} succeeded.",
            cancellationToken,
            new object[] { imageName },
            retainOutput: true).ConfigureAwait(false);

        if (processResult.ExitCode != 0)
        {
            throw new ProcessFailedException(
                $"Podman save failed with exit code {processResult.ExitCode}.",
                processResult.ExitCode,
                processResult.ProcessOutput,
                processResult.TotalProcessOutputLineCount);
        }
    }

    /// <summary>
    /// Builds the <c>podman save</c> arguments that export <paramref name="imageName"/> to an image archive.
    /// </summary>
    /// <example>
    /// <code>
    /// save --format "oci-archive" --output "/out/myapp-latest.tar" "myapp:latest"
    /// </code>
    /// </example>
    internal static string BuildSaveArguments(string imageName, ContainerImageBuildOptions options)
    {
        var format = options.ImageFormat switch
        {
            ContainerImageFormat.Oci => "oci-archive",
            // The .NET SDK and Docker both treat an unspecified format as the Docker manifest type.
            ContainerImageFormat.Docker or null => "docker-archive",
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.ImageFormat, "Invalid container image format")
        };

        // Derive the archive path through the shared helper so that this runtime, the Docker runtime, and
        // ContainerImageReference (which hands the path to consumers) all resolve the same file.
        var archivePath = ResourceExtensions.GetContainerImageArchivePath(options.OutputPath!, imageName);

        return $"save --format \"{format}\" --output \"{archivePath}\" \"{imageName}\"";
    }

    public override async Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        await RunPodmanBuildAsync(
            contextPath,
            dockerfilePath,
            options,
            buildArguments,
            buildSecrets,
            stage,
            cancellationToken).ConfigureAwait(false);
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

/// <summary>
/// Internal DTO for deserializing <c>podman ps --format json</c> output.
/// </summary>
internal sealed class PodmanPsEntry
{
    public Dictionary<string, string>? Labels { get; set; }
    public List<PodmanPsPort>? Ports { get; set; }
}

/// <summary>
/// Internal DTO for deserializing Podman port mappings.
/// </summary>
internal sealed class PodmanPsPort
{
    [JsonPropertyName("container_port")]
    public int? ContainerPort { get; set; }

    [JsonPropertyName("host_port")]
    public int? HostPort { get; set; }
}

[JsonSerializable(typeof(List<PodmanPsEntry>))]
internal sealed partial class PodmanPsJsonContext : JsonSerializerContext
{
}
