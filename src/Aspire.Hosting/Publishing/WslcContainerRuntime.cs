// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Publishes container images using Windows Subsystem for Linux containers.
/// </summary>
internal sealed class WslcContainerRuntime(ILogger<WslcContainerRuntime> logger, IProcessRunner processRunner)
    : ContainerRuntimeBase<WslcContainerRuntime>(logger, processRunner)
{
    protected override string RuntimeExecutable => KnownContainerRuntimes.Wslc;

    public override string Name => "WSLc";

    public override async Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await ExecuteContainerCommandWithExitCodeAsync(
                "container ls -n 1",
                "WSLc container ls failed with exit code {ExitCode}.",
                "WSLc is running and healthy.",
                cancellationToken,
                []).ConfigureAwait(false);

            return exitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Logger.LogWarning(ex, "Could not start WSLc to check container runtime health.");
            return false;
        }
    }

    public override async Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options,
        Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets,
        string? stage, CancellationToken cancellationToken)
    {
        var imageName = !string.IsNullOrEmpty(options?.Tag)
            ? $"{options.ImageName}:{options.Tag}"
            : options?.ImageName ?? throw new ArgumentException("ImageName must be provided in options.", nameof(options));

        // WSLC 3.0.4 builds for the native Linux architecture and does not accept --platform.
        // Do not silently produce a native image when a different platform was requested.
        var nativePlatform = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => ContainerTargetPlatform.LinuxAmd64,
            Architecture.Arm64 => ContainerTargetPlatform.LinuxArm64,
            _ => (ContainerTargetPlatform?)null
        };
        if (options.TargetPlatform is not null && options.TargetPlatform != nativePlatform)
        {
            throw new DistributedApplicationException(
                $"WSLc cannot build images for the requested platform '{options.TargetPlatform}'. Only the native Linux platform is supported.");
        }

        if (options.ImageFormat == ContainerImageFormat.Oci && string.IsNullOrEmpty(options.OutputPath))
        {
            throw new ArgumentException("OutputPath must be provided when ImageFormat is Oci.", nameof(options));
        }
        if (options.ImageFormat is not null and not ContainerImageFormat.Docker and not ContainerImageFormat.Oci)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ImageFormat, "Invalid container image format");
        }

        var arguments = $"image build --file \"{dockerfilePath}\" --tag \"{imageName}\" --progress plain";
        arguments += BuildArgumentsString(buildArguments);
        arguments += BuildSecretsString(buildSecrets);
        arguments += BuildStageString(stage);
        arguments += $" \"{Path.GetFullPath(contextPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}\"";

        var environmentVariables = new Dictionary<string, string>();
        foreach (var (key, secret) in buildSecrets)
        {
            if (secret.Type == BuildImageSecretType.Environment && secret.Value is not null)
            {
                environmentVariables[key.ToUpperInvariant()] = secret.Value;
            }
        }

        var result = await ExecuteContainerCommandWithResultAsync(
            arguments,
            "WSLc build for {ImageName} failed with exit code {ExitCode}.",
            "WSLc build for {ImageName} succeeded.",
            cancellationToken,
            [imageName],
            environmentVariables,
            retainOutput: true).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ProcessFailedException(
                $"WSLc build failed with exit code {result.ExitCode}.",
                result.ExitCode, result.ProcessOutput, result.TotalProcessOutputLineCount);
        }

        if (!string.IsNullOrEmpty(options.OutputPath))
        {
            // WSLC's daemon-backed builder rejects Docker/OCI archive exporters:
            // "ERROR: Docker exporter is not supported for the docker driver."
            // Build into the local store, then save an archive containing both OCI
            // layout (oci-layout/index.json) and Docker (manifest.json) metadata.
            var archivePath = ResourceExtensions.GetContainerImageArchivePath(options.OutputPath, imageName);
            var saveResult = await ExecuteContainerCommandWithResultAsync(
                $"image save --output \"{archivePath}\" \"{imageName}\"",
                "WSLc save for {ImageName} failed with exit code {ExitCode}.",
                "WSLc save for {ImageName} succeeded.",
                cancellationToken,
                [imageName],
                retainOutput: true).ConfigureAwait(false);
            if (saveResult.ExitCode != 0)
            {
                throw new ProcessFailedException(
                    $"WSLc save failed with exit code {saveResult.ExitCode}.",
                    saveResult.ExitCode, saveResult.ProcessOutput, saveResult.TotalProcessOutputLineCount);
            }
        }
    }

    public override async Task<ContainerImageConfigInspectionResult> InspectImageConfigAsync(string imageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        string? output = null;
        try
        {
            output = await ExecuteContainerCommandForOutputAsync(
                ["image", "inspect", imageName, "--format", "json"],
                "inspect image config", imageName, cancellationToken).ConfigureAwait(false);
            // WSLC returns Docker-shaped metadata, but only accepts "json", not Go templates:
            // [{"Config":{"Entrypoint":["dotnet"],"Cmd":["app.dll"],"WorkingDir":"/app"}, ...}]
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Array &&
                document.RootElement.GetArrayLength() == 1 &&
                document.RootElement[0].TryGetProperty("Config", out var configJson) &&
                TryParseImageConfig(configJson.GetRawText(), out var config))
            {
                return new ContainerImageConfigInspectionResult(
                    ContainerImageInspectionStatus.Succeeded, output, errorMessage: null, () => config);
            }
        }
        catch (Exception ex) when (ex is DistributedApplicationException or JsonException or InvalidOperationException)
        {
            return new ContainerImageConfigInspectionResult(
                ContainerImageInspectionStatus.Failed, output, ex.Message, configAccessor: null);
        }

        return new ContainerImageConfigInspectionResult(
            ContainerImageInspectionStatus.Failed, output,
            $"WSLc returned invalid image configuration for '{imageName}'.", configAccessor: null);
    }

    // WSLC has no remote manifest-inspection or Compose commands. Report the inspection
    // capability through its contract and fail Compose before attempting a nonexistent command.
    public override Task<ContainerImageManifestInspectionResult> InspectImageManifestAsync(string imageName, CancellationToken cancellationToken) =>
        Task.FromResult(ContainerImageManifestInspectionResult.Unsupported);

    public override Task ComposeUpAsync(ComposeOperationContext context, CancellationToken cancellationToken) =>
        throw new DistributedApplicationException("WSLc does not support Compose. Use Docker or Podman for Compose deployment.");

    public override Task ComposeDownAsync(ComposeOperationContext context, CancellationToken cancellationToken) =>
        throw new DistributedApplicationException("WSLc does not support Compose. Use Docker or Podman for Compose deployment.");

    public override Task<IReadOnlyList<ComposeServiceInfo>?> ComposeListServicesAsync(ComposeOperationContext context, CancellationToken cancellationToken) =>
        throw new DistributedApplicationException("WSLc does not support Compose. Use Docker or Podman for Compose deployment.");
}
