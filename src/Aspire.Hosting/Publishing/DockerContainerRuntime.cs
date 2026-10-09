// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

internal sealed class DockerContainerRuntime : ContainerRuntimeBase<DockerContainerRuntime>, IContainerImageArtifactRuntime
{
    private const string LocalImageOciArchiveNotSupportedMessage =
        "Docker cannot export an OCI archive when container-file layering references locally built images. " +
        "Use ContainerImageFormat.Docker for this archive or run the publish with Podman.";
    private readonly ILogger<DockerContainerRuntime> _logger;

    public DockerContainerRuntime(ILogger<DockerContainerRuntime> logger, IProcessRunner processRunner) : base(logger, processRunner)
    {
        _logger = logger;
    }

    protected override string RuntimeExecutable => KnownContainerRuntimes.Docker;
    public override string Name => "Docker";

    public async Task<string> BuildImageArtifactAsync(
        string contextPath, string dockerfilePath, ContainerImageBuildOptions options,
        Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets,
        string? stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var imageName = $"{options.ImageName}:{options.Tag}";
        _ = ContainerImageName.ParseSource(imageName);
        if (options.ImageFormat == ContainerImageFormat.Oci)
        {
            throw new DistributedApplicationException(
                "Docker image artifacts currently support the local containerd store and Docker archives, not OCI archive output.");
        }

        var status = await ExecuteContainerCommandForOutputAsync(
            ["info", "--format", "{{json .DriverStatus}}"], "check lossless image store support", imageName,
            cancellationToken).ConfigureAwait(false);
        try
        {
            // Docker reports the store as [["driver-type","io.containerd.snapshotter.v1"]].
            // Classic stores cannot retain multi-platform indexes or build attestations.
            // https://docs.docker.com/desktop/features/containerd/
            using var document = JsonDocument.Parse(status);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                !document.RootElement.EnumerateArray().Any(entry =>
                    entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() == 2 &&
                    entry[0].ValueKind == JsonValueKind.String && entry[0].GetString() == "driver-type" &&
                    entry[1].ValueKind == JsonValueKind.String && entry[1].GetString() == "io.containerd.snapshotter.v1"))
            {
                throw new DistributedApplicationException(
                    "Dockerfile image artifact publication requires Docker's containerd image store to preserve all platforms and attestations. " +
                    "Use a Docker environment with that store enabled. Existing compute image builds are unaffected.");
            }
        }
        catch (JsonException ex)
        {
            throw new DistributedApplicationException("Docker returned invalid image-store information.", ex);
        }

        var directory = Directory.CreateTempSubdirectory("aspire-image-build-");
        try
        {
            var metadataPath = Path.Combine(directory.FullName, "metadata.json");
            var buildOptions = new ContainerImageBuildOptions
            {
                ImageName = options.ImageName,
                Tag = options.Tag,
                Destination = options.Destination,
                OutputPath = options.OutputPath,
                ImageFormat = options.ImageFormat,
                TargetPlatform = options.TargetPlatform,
                RequiresLocalImageStore = true,
                ArtifactMetadataPath = metadataPath
            };
            await BuildImageAsync(contextPath, dockerfilePath, buildOptions, buildArguments, buildSecrets, stage, cancellationToken)
                .ConfigureAwait(false);
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
            // Buildx metadata records the exported root, not a platform config:
            // {"containerimage.digest":"sha256:<64 hex characters>", ...}
            if (metadata.RootElement.ValueKind != JsonValueKind.Object ||
                !metadata.RootElement.TryGetProperty("containerimage.digest", out var value) ||
                value.ValueKind != JsonValueKind.String || value.GetString() is not { } digest)
            {
                throw new DistributedApplicationException("Docker did not record the built image's root manifest digest.");
            }
            try
            {
                ContainerImageName.ValidateDigest(digest, nameof(metadata));
            }
            catch (ArgumentException ex)
            {
                throw new DistributedApplicationException("Docker returned an invalid built image root digest.", ex);
            }
            var localDigest = await ExecuteContainerCommandForOutputAsync(
                ["image", "inspect", imageName, "--format", "{{.Descriptor.Digest}}"],
                "verify local image artifact", imageName, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(digest, localDigest.Trim()))
            {
                throw new DistributedApplicationException($"Local image '{imageName}' does not match the built artifact's root digest.");
            }

            return digest;
        }
        catch (JsonException ex)
        {
            throw new DistributedApplicationException("Docker returned invalid image-build metadata.", ex);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    public async Task<string> PublishImageArtifactAsync(string digest, string destinationImageName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContainerImageName.ValidateDigest(digest, nameof(digest));
        var (repository, reference) = ParsePublicationDestination(destinationImageName);
        // The containerd store addresses the complete index by its root digest.
        // Tag that immutable ID, never the build tag that another build could replace.
        await ExecuteContainerCommandForOutputAsync(
            ["tag", digest, reference], "tag image artifact", reference, cancellationToken).ConfigureAwait(false);
        await ExecuteContainerCommandForOutputAsync(
            ["push", reference], "push image artifact", reference, cancellationToken).ConfigureAwait(false);
        var publishedDigest = await ReadRemoteImageDigestAsync(reference, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(digest, publishedDigest))
        {
            throw new DistributedApplicationException($"Destination image '{reference}' does not have the built artifact's root content digest.");
        }

        return $"{repository}@{digest}";
    }

    public override async Task<string> ResolveRemoteImageAsync(string imageName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ContainerImageName.ParseSource(imageName);
        var repository = $"{source.Registry}/{source.Image}";
        // A tag-plus-digest reference such as tools:v1@sha256:<hex> selects the digest.
        // Discard the tag before invoking Buildx so a moving tag cannot affect preparation.
        var reference = source.Digest is { } expectedDigest
            ? $"{repository}@{expectedDigest}"
            : $"{repository}:{source.Tag}";
        await EnsureRemoteImageCopySupportedAsync(cancellationToken).ConfigureAwait(false);
        var digest = await ReadRemoteImageDigestAsync(reference, cancellationToken).ConfigureAwait(false);
        if (source.Digest is not null && !StringComparer.Ordinal.Equals(source.Digest, digest))
        {
            throw new DistributedApplicationException($"Docker reported a different content digest for pinned source image '{reference}'.");
        }

        return $"{repository}@{digest}";
    }

    public override async Task<string> CopyRemoteImageAsync(string sourceImageName, string destinationImageName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ContainerImageName.ParseSource(sourceImageName);
        if (source.Digest is null)
        {
            throw new ArgumentException("Resolve the source to an immutable digest before copying it.", nameof(sourceImageName));
        }
        var sourceReference = $"{source.Registry}/{source.Image}@{source.Digest}";
        var (destinationRepository, destinationReference) = ParsePublicationDestination(destinationImageName);
        await EnsureRemoteImageCopySupportedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // --prefer-index=false carbon-copies a single source without wrapping a single-platform
        // manifest in a new index. Index children (including attestations) are copied together.
        // https://docs.docker.com/reference/cli/docker/buildx/imagetools/create/
        await ExecuteContainerCommandForOutputAsync(
            ["buildx", "imagetools", "create", "--prefer-index=false", "--tag", destinationReference, sourceReference],
            "copy remote image",
            destinationReference,
            cancellationToken).ConfigureAwait(false);

        var digest = await ReadRemoteImageDigestAsync(destinationReference, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(source.Digest, digest))
        {
            throw new DistributedApplicationException(
                $"Destination image '{destinationReference}' does not have the source content digest. Remote image publication could not be verified.");
        }
        _logger.LogInformation("Verified remote image publication to {ImageName}.", $"{destinationRepository}@{digest}");

        return $"{destinationRepository}@{digest}";
    }

    private async Task EnsureRemoteImageCopySupportedAsync(CancellationToken cancellationToken)
    {
        var help = await ExecuteContainerCommandForOutputAsync(
            ["buildx", "imagetools", "create", "--help"],
            "check lossless remote image copy support",
            "Docker Buildx",
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Cobra help exposes the flag as a whitespace-delimited token:
        //   --prefer-index             When only a single source is specified, ...
        // Probe the flag rather than guessing compatibility from a Buildx version string.
        if (!help.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("--prefer-index", StringComparer.Ordinal))
        {
            throw new DistributedApplicationException(
                "Docker Buildx cannot preserve remote image manifests without --prefer-index=false. " +
                "Install a Buildx version supporting that flag: https://docs.docker.com/build/install-buildx/.");
        }

    }

    private static (string Repository, string Reference) ParsePublicationDestination(string destinationImageName)
    {
        var destination = ContainerImageName.ParseSource(destinationImageName);
        if (!ContainerReferenceParser.TryParse(destinationImageName, out var input) ||
            input.Registry is null || input.Tag is null || input.Digest is not null)
        {
            throw new ArgumentException("The destination must specify a registry, repository, and publication tag, without a digest.", nameof(destinationImageName));
        }
        var repository = $"{destination.Registry}/{destination.Image}";

        return (repository, $"{repository}:{destination.Tag}");
    }

    private async Task<string> ReadRemoteImageDigestAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = await ExecuteContainerCommandForOutputAsync(
            ["buildx", "imagetools", "inspect", reference, "--format", "{{.Manifest.Digest}}"],
            "resolve remote image digest",
            reference,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // The format emits exactly the root digest, for example sha256:<64 lowercase hex
        // characters>, not a platform child's digest. Some versions include a trailing newline.
        var digest = output.Trim();
        try
        {
            ContainerImageName.ValidateDigest(digest, nameof(output));
        }
        catch (ArgumentException ex)
        {
            throw new DistributedApplicationException($"Docker returned an invalid root manifest digest for '{reference}'.", ex);
        }

        return digest;
    }

    private async Task RunDockerBuildAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
    {
        var imageName = !string.IsNullOrEmpty(options?.Tag)
            ? $"{options.ImageName}:{options.Tag}"
            : options?.ImageName ?? throw new ArgumentException("ImageName must be provided in options.", nameof(options));

        string? selectedBuilderName = null;
        string? isolatedBuilderName = null;
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

        if (requiresLocalImageStore)
        {
            // Container-file layering refers to images tagged only in the active daemon's image store.
            // A Docker context name selects that context's daemon-backed Buildx builder, avoiding an
            // ambient custom builder that cannot resolve the local-only image.
            selectedBuilderName = await GetActiveDockerContextAsync(imageName, cancellationToken).ConfigureAwait(false);
        }
        else if (exportsArchive)
        {
            // Docker's in-daemon builder cannot reliably write Docker or OCI archive exporters.
            // Use an isolated BuildKit container for archive output and remove it after the build.
            // https://docs.docker.com/build/exporters/
            isolatedBuilderName = $"aspire-{Guid.NewGuid():N}";
            await CreateBuildkitInstanceAsync(isolatedBuilderName, cancellationToken).ConfigureAwait(false);
            selectedBuilderName = isolatedBuilderName;
        }

        try
        {
            var arguments = $"buildx build --file \"{dockerfilePath}\" --tag \"{imageName}\"";

            if (!string.IsNullOrEmpty(selectedBuilderName))
            {
                arguments += $" --builder \"{selectedBuilderName}\"";
            }

            if (options?.ArtifactMetadataPath is { } metadataPath)
            {
                arguments += $" --metadata-file \"{metadataPath}\" --load";
            }

            // Add platform support if specified
            if (options?.TargetPlatform is not null)
            {
                arguments += $" --platform \"{options.TargetPlatform.Value.ToRuntimePlatformString()}\"";
            }

            // Add output format support if specified
            if (options?.ArtifactMetadataPath is null && !exportsLocalImageArchive &&
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
            if (!string.IsNullOrEmpty(isolatedBuilderName))
            {
                await RemoveBuildkitInstanceBestEffortAsync(isolatedBuilderName).ConfigureAwait(false);
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
        var normalizedContextPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contextPath));

        await RunDockerBuildAsync(
            normalizedContextPath,
            dockerfilePath,
            options,
            buildArguments,
            buildSecrets,
            stage,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetActiveDockerContextAsync(string imageName, CancellationToken cancellationToken)
    {
        var output = await ExecuteContainerCommandForOutputAsync(
            "context show",
            "context discovery",
            imageName,
            cancellationToken).ConfigureAwait(false);

        // `docker context show` emits exactly one context name, for example:
        //   desktop-linux
        var contextNames = output.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (contextNames is not [var contextName])
        {
            throw new DistributedApplicationException("Docker did not report exactly one active context.");
        }

        return contextName;
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

        // Layered archives use a private tag whose platforms were already selected by the build.
        // Save all its variants without requiring API 1.48+ (1.52+ for multiple platforms) for filtering.
        // https://docs.docker.com/reference/cli/docker/image/save/#platform
        return $"image save --output \"{archivePath}\" \"{imageName}\"";
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

    private async Task RemoveBuildkitInstanceBestEffortAsync(string builderName)
    {
        // Cleanup must outlive a canceled publish, but it is bounded so an unresponsive Docker
        // daemon cannot delay cancellation indefinitely.
        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await RemoveBuildkitInstanceAsync(builderName, cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove buildkit instance {BuilderName}", builderName);
        }
    }
}
