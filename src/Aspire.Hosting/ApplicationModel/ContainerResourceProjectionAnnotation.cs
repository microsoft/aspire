// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Identifies a resource that is configured and realized as a container through a typed projection.
/// </summary>
/// <remarks>
/// The projected container is a configuration view rather than a logical model member. Container-specific
/// property values are recorded here so publishers can realize the owning resource without changing its identity.
/// </remarks>
public sealed class ContainerResourceProjectionAnnotation : IResourceAnnotation
{
    internal ContainerResourceProjectionAnnotation()
    {
    }

    /// <summary>
    /// Gets the container entrypoint configured through the projection.
    /// </summary>
    public string? Entrypoint { get; internal set; }

    /// <summary>
    /// Gets whether projected container arguments should be executed through a shell.
    /// </summary>
    public bool? ShellExecution { get; internal set; }
}
