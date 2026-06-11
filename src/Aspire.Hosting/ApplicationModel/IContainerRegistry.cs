// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents container registry information for deployment targets.
/// </summary>
public interface IContainerRegistry
{
    /// <summary>
    /// Gets the name of the container registry.
    /// </summary>
    ReferenceExpression Name { get; }

    /// <summary>
    /// Gets the endpoint URL of the container registry.
    /// </summary>
    /// <remarks>
    /// An empty endpoint value indicates a local container registry where images are built and used locally
    /// without being pushed to a remote registry (e.g., Docker Compose scenarios).
    /// </remarks>
    ReferenceExpression Endpoint { get; }

    /// <summary>
    /// Gets the repository path within the container registry.
    /// </summary>
    /// <remarks>
    /// The repository represents the namespace or path segment that appears after the registry endpoint
    /// in a container image reference. For example:
    /// <list type="bullet">
    /// <item><description>For Docker Hub (<c>docker.io</c>): typically a username like <c>captainsafia</c></description></item>
    /// <item><description>For GitHub Container Registry (<c>ghcr.io</c>): typically <c>username/reponame</c></description></item>
    /// <item><description>For Azure Container Registry: typically left empty as images are pushed directly to the registry</description></item>
    /// </list>
    /// When not <see langword="null"/>, the repository is combined with the image name to form the full
    /// image path: <c>{endpoint}/{repository}/{imageName}:{tag}</c>.
    /// When <see langword="null"/>, the image path is: <c>{endpoint}/{imageName}:{tag}</c>.
    /// </remarks>
    ReferenceExpression? Repository => null;
}
