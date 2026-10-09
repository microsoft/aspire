// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Represents a container runtime (e.g., Docker, Podman) that can be used to build, tag, push, and manage container images.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IContainerRuntime
{
    /// <summary>
    /// Gets the name of the container runtime.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Checks if the container runtime is running and available.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if the container runtime is running; otherwise, false.</returns>
    Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Builds a container image from a Dockerfile.
    /// </summary>
    /// <param name="contextPath">The build context path.</param>
    /// <param name="dockerfilePath">The path to the Dockerfile.</param>
    /// <param name="options">Build options including image name and tag.</param>
    /// <param name="buildArguments">Build arguments to pass to the build process.</param>
    /// <param name="buildSecrets">Build secrets to pass to the build process.</param>
    /// <param name="stage">The target build stage.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken);

    /// <summary>
    /// Tags a container image with a new name.
    /// </summary>
    /// <param name="localImageName">The current name of the image.</param>
    /// <param name="targetImageName">The new name to assign to the image.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task TagImageAsync(string localImageName, string targetImageName, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a container image.
    /// </summary>
    /// <param name="imageName">The name of the image to remove.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task RemoveImageAsync(string imageName, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes a container image to a registry.
    /// </summary>
    /// <param name="resource">The resource containing push configuration.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task PushImageAsync(IResource resource, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a remote image reference to its immutable, fully qualified content reference.
    /// </summary>
    /// <param name="imageName">The remote image name, including a tag, digest, or both.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The registry-qualified repository and root manifest digest.</returns>
    /// <remarks>
    /// Resolution does not pull a platform-selected image into the daemon image store.
    /// Resolve a mutable source once before copying it to multiple destinations.
    /// The default implementation fails explicitly for runtimes without this capability.
    /// </remarks>
    /// <example>
    /// <code>
    /// var source = await runtime.ResolveRemoteImageAsync("docker.io/library/busybox:latest", cancellationToken);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException">The source reference is invalid.</exception>
    /// <exception cref="DistributedApplicationException">The runtime cannot resolve the image.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    Task<string> ResolveRemoteImageAsync(string imageName, CancellationToken cancellationToken)
        => throw new DistributedApplicationException($"Container runtime '{Name}' does not support remote image resolution. Use Docker with Buildx.");

    /// <summary>
    /// Copies a digest-pinned remote image and verifies the destination content digest.
    /// </summary>
    /// <param name="sourceImageName">The immutable source returned by <see cref="ResolveRemoteImageAsync"/>.</param>
    /// <param name="destinationImageName">The fully qualified destination repository and explicit publication tag.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The digest-qualified destination reference, without its publication tag.</returns>
    /// <remarks>
    /// Preserves the source root manifest or index and its referenced content, including every platform.
    /// The publication tag is a transport address, not the returned consumer reference.
    /// Authentication uses the runtime's configured registry credentials.
    /// Separate OCI referrers or signatures outside the referenced content graph are not copied.
    /// The default implementation fails explicitly rather than falling back to platform-selected pull/tag/push.
    /// </remarks>
    /// <example>
    /// <code>
    /// var destination = await runtime.CopyRemoteImageAsync(source, "registry.example.com/tools:publication", cancellationToken);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException">A reference is invalid or the source is not digest-pinned.</exception>
    /// <exception cref="DistributedApplicationException">The runtime cannot copy the image or verification fails.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    Task<string> CopyRemoteImageAsync(string sourceImageName, string destinationImageName, CancellationToken cancellationToken)
        => throw new DistributedApplicationException($"Container runtime '{Name}' does not support lossless remote image copying. Use Docker with Buildx.");

    /// <summary>
    /// Logs in to a container registry.
    /// </summary>
    /// <param name="registryServer">The registry server URL.</param>
    /// <param name="username">The username for authentication.</param>
    /// <param name="password">The password for authentication.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LoginToRegistryAsync(string registryServer, string username, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Starts compose services in detached mode.
    /// </summary>
    /// <param name="context">The compose operation parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="DistributedApplicationException">Thrown when the compose up command fails.</exception>
    Task ComposeUpAsync(ComposeOperationContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Stops and removes compose services.
    /// </summary>
    /// <param name="context">The compose operation parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="DistributedApplicationException">Thrown when the compose down command fails.</exception>
    Task ComposeDownAsync(ComposeOperationContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the running services in a compose environment with their port mappings.
    /// </summary>
    /// <param name="context">The compose operation parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of running services, or <c>null</c> if the query could not be completed.</returns>
    Task<IReadOnlyList<ComposeServiceInfo>?> ComposeListServicesAsync(ComposeOperationContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Inspects a container image configuration.
    /// </summary>
    /// <param name="imageName">The image name or reference to inspect.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The image configuration inspection result.</returns>
    Task<ContainerImageConfigInspectionResult> InspectImageConfigAsync(string imageName, CancellationToken cancellationToken)
        => Task.FromResult(ContainerImageConfigInspectionResult.Unsupported);

    /// <summary>
    /// Inspects a container image manifest.
    /// </summary>
    /// <param name="imageName">The image name or reference to inspect.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The image manifest inspection result.</returns>
    Task<ContainerImageManifestInspectionResult> InspectImageManifestAsync(string imageName, CancellationToken cancellationToken)
        => Task.FromResult(ContainerImageManifestInspectionResult.Unsupported);
}
