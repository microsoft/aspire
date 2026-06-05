// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents options for pushing container images to a registry.
/// </summary>
/// <remarks>
/// This class allows customization of how container images are named and tagged when pushed to a container registry.
/// The <see cref="RemoteImageName"/> specifies the repository path (without registry endpoint or tag),
/// and <see cref="RemoteImageTag"/> specifies the tag to apply. Use <see cref="GetFullRemoteImageNameAsync"/>
/// to construct the complete image reference including registry endpoint and tag.
/// </remarks>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class ContainerImagePushOptions
{
    /// <summary>
    /// Gets or sets the remote image name (repository path without registry endpoint or tag).
    /// </summary>
    /// <value>
    /// <para>
    /// The repository path for the image. This can be specified in several formats:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Simple image name: <c>myapp</c> - Uses the registry endpoint and repository from the associated <see cref="IContainerRegistry"/>.</description></item>
    /// <item><description>Repository and image: <c>myorg/myapp</c> - Uses the registry endpoint but overrides the repository.</description></item>
    /// <item><description>Full path with host: <c>docker.io/captainsafia/myapp</c> - Overrides both the registry endpoint and repository.</description></item>
    /// </list>
    /// <para>
    /// This should not include the tag (e.g., <c>:latest</c>). Use <see cref="RemoteImageTag"/> for the tag.
    /// If not set explicitly, defaults to the resource name in lowercase.
    /// </para>
    /// </value>
    public string? RemoteImageName { get; set; }

    /// <summary>
    /// Gets or sets the remote image tag.
    /// </summary>
    /// <value>
    /// The tag to apply to the image when pushed to the registry, such as "latest", "v1.0.0", or "dev".
    /// If not set explicitly, defaults to "latest".
    /// </value>
    public string? RemoteImageTag { get; set; }

    /// <summary>
    /// Gets the full remote image name including registry endpoint and tag.
    /// </summary>
    /// <param name="registry">The container registry to use for constructing the full image name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The fully qualified image name in the format "{registryEndpoint}/{repository}/{imageName}:{tag}"
    /// or "{registryEndpoint}/{imageName}:{tag}" if no repository is specified.
    /// For example: "myregistry.azurecr.io/myapp:v1.0.0" or "docker.io/captainsafia/myapp:latest".
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="RemoteImageName"/> is <c>null</c> or empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry"/> is <c>null</c>.</exception>
    /// <remarks>
    /// <para>
    /// This method retrieves the registry endpoint asynchronously and combines it with the remote image name and tag.
    /// If <see cref="RemoteImageTag"/> is <c>null</c> or empty, "latest" is used as the default tag.
    /// </para>
    /// <para>
    /// The <see cref="RemoteImageName"/> value is parsed to determine if it contains an override for the registry
    /// host or repository. If the <see cref="RemoteImageName"/> contains a host component (detected by the presence
    /// of a dot in the first segment), that host will be used instead of the registry endpoint. Otherwise, the
    /// registry endpoint is used and the <see cref="IContainerRegistry.Repository"/> (if set) is prepended to the image name.
    /// </para>
    /// </remarks>
    public async Task<string> GetFullRemoteImageNameAsync(
        IContainerRegistry registry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(RemoteImageName))
        {
            throw new InvalidOperationException("RemoteImageName must be set.");
        }

        ArgumentNullException.ThrowIfNull(registry);

        var tag = string.IsNullOrEmpty(RemoteImageTag) ? "latest" : RemoteImageTag;

        // Parse the RemoteImageName to check if it contains a host override
        var (host, imagePath) = ParseImageReference(RemoteImageName);

        if (!string.IsNullOrEmpty(host))
        {
            // RemoteImageName contains a host, use it directly instead of the registry endpoint
            return $"{host}/{imagePath}:{tag}";
        }

        // Use the registry endpoint
        var registryEndpoint = await registry.Endpoint
            .GetValueAsync(cancellationToken)
            .ConfigureAwait(false);

        // Check if the registry has a repository configured
        var repository = registry.Repository is not null
            ? await registry.Repository.GetValueAsync(cancellationToken).ConfigureAwait(false)
            : null;

        // Combine registry endpoint, repository, and image path
        if (!string.IsNullOrEmpty(repository) && !string.IsNullOrEmpty(registryEndpoint))
        {
            return $"{registryEndpoint}/{repository}/{imagePath}:{tag}";
        }

        // No repository, just use registry endpoint and image path
        if (!string.IsNullOrEmpty(registryEndpoint))
        {
            return $"{registryEndpoint}/{imagePath}:{tag}";
        }

        return $"{imagePath}:{tag}";
    }

    /// <summary>
    /// Parses an image reference to extract the host (if present) and image path.
    /// </summary>
    /// <param name="imageReference">The image reference to parse (e.g., "docker.io/library/nginx" or "myapp").</param>
    /// <returns>
    /// A tuple containing the host (or <see langword="null"/> if not present) and the image path.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The host is detected by checking if the first segment of the path contains a dot (<c>.</c>) or colon (<c>:</c>),
    /// or if it equals <c>localhost</c>. This follows the Docker image reference specification.
    /// </para>
    /// <para>
    /// Examples:
    /// <list type="bullet">
    /// <item><description><c>docker.io/library/nginx</c> → host: <c>docker.io</c>, path: <c>library/nginx</c></description></item>
    /// <item><description><c>ghcr.io/user/repo</c> → host: <c>ghcr.io</c>, path: <c>user/repo</c></description></item>
    /// <item><description><c>localhost:5000/myapp</c> → host: <c>localhost:5000</c>, path: <c>myapp</c></description></item>
    /// <item><description><c>myorg/myapp</c> → host: <c>null</c>, path: <c>myorg/myapp</c></description></item>
    /// <item><description><c>myapp</c> → host: <c>null</c>, path: <c>myapp</c></description></item>
    /// </list>
    /// </para>
    /// </remarks>
    internal static (string? Host, string ImagePath) ParseImageReference(string imageReference)
    {
        var firstSlashIndex = imageReference.IndexOf('/');

        if (firstSlashIndex == -1)
        {
            // No slash, so it's just an image name with no host
            return (null, imageReference);
        }

        var firstSegment = imageReference[..firstSlashIndex];
        var remainder = imageReference[(firstSlashIndex + 1)..];

        // Check if the first segment looks like a host:
        // - Contains a dot (e.g., docker.io, ghcr.io, myregistry.azurecr.io)
        // - Contains a colon (e.g., localhost:5000)
        // - Is "localhost"
        if (firstSegment.Contains('.') || firstSegment.Contains(':') || firstSegment.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return (firstSegment, remainder);
        }

        // First segment doesn't look like a host, treat the whole thing as the image path
        return (null, imageReference);
    }
}
