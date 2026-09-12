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
    /// Gets the selected projection, or <see langword="null"/> when no projection has been selected.
    /// </summary>
    internal ContainerResource? Projection { get; private set; }

    private Type? CustomProjectionType { get; set; }

    internal ContainerResource GetOrCreateDefaultProjection(
        Func<ContainerResource> createProjection,
        Action<ContainerResource> validateProjection)
    {
        if (Projection is null)
        {
            var projection = createProjection();
            validateProjection(projection);
            Projection = projection;
        }

        return Projection;
    }

    internal TContainer GetOrCreateCustomProjection<TContainer>(
        Func<TContainer> createProjection,
        Action<TContainer> validateProjection)
        where TContainer : ContainerResource
    {
        if (Projection is null)
        {
            var projection = createProjection();
            validateProjection(projection);
            Projection = projection;
            CustomProjectionType = typeof(TContainer);
            return projection;
        }

        if (CustomProjectionType != typeof(TContainer))
        {
            var selectedProjection = CustomProjectionType is null
                ? "the default container projection"
                : $"a custom container projection of type '{CustomProjectionType.Name}'";

            throw new InvalidOperationException(
                $"The resource '{Owner.Name}' already uses {selectedProjection} and cannot also use " +
                $"a custom container projection of type '{typeof(TContainer).Name}'. The first projection selected for an operation cannot be replaced.");
        }

        return (TContainer)Projection;
    }
}
