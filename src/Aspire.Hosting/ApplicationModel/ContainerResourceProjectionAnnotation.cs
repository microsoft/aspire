// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stores the container configuration view applied to a resource for the current operation.
/// </summary>
internal sealed class ContainerResourceProjectionAnnotation : IResourceAnnotation
{
    internal ContainerResourceProjectionAnnotation(IResource owner)
    {
        Owner = owner;
    }

    /// <summary>
    /// Gets the canonical model resource the projection was created for.
    /// </summary>
    /// <remarks>
    /// A projection shares the owner's <see cref="ResourceAnnotationCollection"/>, so this annotation is
    /// reachable from both sides of the pair. Storing the owner here lets projection types authored by
    /// integrations (for example the Azure emulator surrogates) participate without implementing a marker
    /// interface, which keeps the projection contract additive for resources compiled against earlier versions.
    /// </remarks>
    internal IResource Owner { get; }

    /// <summary>
    /// Gets the custom projection registered for the owner, or <see langword="null"/> when callbacks use the
    /// default container projection.
    /// </summary>
    internal ContainerResource? Projection { get; set; }

    internal bool CallbacksEvaluated { get; set; }

    internal void RegisterProjection(ContainerResource projection)
    {
        if (Projection is null)
        {
            Projection = projection;
            return;
        }

        if (Projection.GetType() != projection.GetType())
        {
            throw new InvalidOperationException(
                $"The resource '{Owner.Name}' already has a custom container projection of type " +
                $"'{Projection.GetType().Name}' and cannot also use '{projection.GetType().Name}'.");
        }
    }
}

/// <summary>
/// Stores one configuration callback registered for a container projection.
/// </summary>
internal sealed class ContainerResourceProjectionCallbackAnnotation(
    Action<ContainerResource> callback) : IResourceAnnotation
{
    internal Action<ContainerResource> Callback { get; } = callback;
}
