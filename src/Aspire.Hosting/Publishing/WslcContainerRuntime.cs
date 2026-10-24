// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Dcp.Process;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

internal sealed class WslcContainerRuntime : ContainerRuntimeBase<WslcContainerRuntime>
{
    public WslcContainerRuntime(ILogger<WslcContainerRuntime> logger, IProcessRunner processRunner) : base(logger, processRunner)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DistributedApplicationException(
                "WSLc container publishing is only supported on Windows. Use Docker or Podman on this host.");
        }
    }

    protected override string RuntimeExecutable => KnownContainerRuntimes.Wslc;
    public override string Name => "WSLc";

    public override async Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteContainerCommandWithExitCodeAsync(
                "container ls -n 1",
                "WSLc is not running. Exit code: {ExitCode}.",
                "WSLc is running.",
                cancellationToken,
                []).ConfigureAwait(false) == 0;
        }
        catch (Win32Exception ex)
        {
            Logger.LogDebug(ex, "Could not start WSLc. Ensure wslc.exe is installed and available on PATH.");
            return false;
        }
    }

    public override async Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (options?.TargetPlatform is not null)
        {
            throw new DistributedApplicationException(
                "WSLc does not support selecting a platform for Dockerfile image builds. Use Docker or Podman, " +
                "or set TargetPlatform to null with WithContainerBuildOptions to build for the WSLc host platform.");
        }

        if (options?.Destination == ContainerImageDestination.Archive ||
            !string.IsNullOrEmpty(options?.OutputPath) ||
            options?.ImageFormat == ContainerImageFormat.Oci)
        {
            // WSLc exposes --output, but its Docker driver rejects Docker and OCI image exporters.
            // Do not substitute save or create a different builder to work around this limitation.
            throw new DistributedApplicationException(
                "WSLc does not support Dockerfile image archive output or OCI image format selection. " +
                "Use Docker or Podman for this publish operation.");
        }

        if (options?.ImageFormat is not null and not ContainerImageFormat.Docker)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ImageFormat, "Invalid container image format");
        }

        var imageName = !string.IsNullOrEmpty(options?.Tag)
            ? $"{options.ImageName}:{options.Tag}"
            : options?.ImageName ?? throw new ArgumentException("ImageName must be provided in options.", nameof(options));
        var normalizedContextPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contextPath));
        var arguments = new List<string> { "build", "--file", dockerfilePath, "--tag", imageName };
        arguments.AddRange(BuildArguments(buildArguments));
        arguments.AddRange(BuildSecrets(buildSecrets, useCsv: true));
        arguments.AddRange(BuildStage(stage));
        arguments.Add(normalizedContextPath);

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
            "WSLc build for {ImageName} failed with exit code {ExitCode}.",
            "WSLc build for {ImageName} succeeded.",
            cancellationToken,
            [imageName],
            environmentVariables,
            retainOutput: true).ConfigureAwait(false);

        if (processResult.ExitCode != 0)
        {
            throw new ProcessFailedException(
                $"WSLc build failed with exit code {processResult.ExitCode}.",
                processResult.ExitCode,
                processResult.ProcessOutput,
                processResult.TotalProcessOutputLineCount);
        }
    }

    public override async Task<ContainerImageConfigInspectionResult> InspectImageConfigAsync(string imageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);

        string output;
        try
        {
            output = await ExecuteContainerCommandForOutputAsync(
                ["image", "inspect", imageName, "--format", "json"],
                "inspect image config",
                imageName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DistributedApplicationException ex)
        {
            return ContainerImageConfigInspectionResult.Failure(ex.Message);
        }

        try
        {
            // `wslc image inspect --format json` returns an array, not a Go-template projection:
            // [{"Config":{"Entrypoint":null,"Cmd":["/bin/sh"],"WorkingDir":"/"}}]
            var images = JsonSerializer.Deserialize(output, WslcImageInspectionJsonContext.Default.ListWslcInspectedImage);
            if (images is [{ Config: { } config }])
            {
                return ContainerImageConfigInspectionResult.Success(
                    new ContainerImageConfig(config.Entrypoint ?? [], config.Cmd ?? [], config.WorkingDir),
                    output);
            }
        }
        catch (JsonException)
        {
            // Report malformed or unexpected metadata through the shared inspection result contract.
        }

        return ContainerImageConfigInspectionResult.Failure(
            $"WSLc returned invalid image configuration for '{imageName}'.",
            output);
    }

    public override Task<ContainerImageManifestInspectionResult> InspectImageManifestAsync(string imageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ContainerImageManifestInspectionResult.Unsupported);
    }

    public override Task ComposeUpAsync(ComposeOperationContext context, CancellationToken cancellationToken)
        => throw CreateComposeNotSupportedException();

    public override Task ComposeDownAsync(ComposeOperationContext context, CancellationToken cancellationToken)
        => throw CreateComposeNotSupportedException();

    public override Task<IReadOnlyList<ComposeServiceInfo>?> ComposeListServicesAsync(ComposeOperationContext context, CancellationToken cancellationToken)
        => throw CreateComposeNotSupportedException();

    private static DistributedApplicationException CreateComposeNotSupportedException()
        => new("WSLc does not support Docker Compose deployment or management. Set ASPIRE_CONTAINER_RUNTIME to docker or podman for Compose operations.");
}

internal sealed class WslcInspectedImage
{
    public WslcImageConfig? Config { get; set; }
}

internal sealed class WslcImageConfig
{
    public List<string>? Entrypoint { get; set; }
    public List<string>? Cmd { get; set; }
    public string? WorkingDir { get; set; }
}

[JsonSerializable(typeof(List<WslcInspectedImage>))]
internal sealed partial class WslcImageInspectionJsonContext : JsonSerializerContext
{
}
