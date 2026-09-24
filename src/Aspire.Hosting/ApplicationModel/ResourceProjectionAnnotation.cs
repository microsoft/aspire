// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stores the effective configuration view applied to a resource for the current operation.
/// </summary>
internal sealed class ResourceProjectionAnnotation : IResourceAnnotation
{
    internal ResourceProjectionAnnotation(IResource owner)
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
    internal IResource? Projection { get; private set; }

    private Type? CustomProjectionType { get; set; }

    internal TProjection GetOrCreateDefaultProjection<TProjection>(
        Func<TProjection> createProjection,
        Action<TProjection> validateProjection,
        string projectionKind)
        where TProjection : class, IResource
    {
        if (Projection is null)
        {
            var projection = createProjection();
            validateProjection(projection);
            Projection = projection;
        }

        if (Projection is not TProjection compatibleProjection)
        {
            throw new InvalidOperationException(
                $"The resource '{Owner.Name}' has a configured projected resource of type '{Projection.GetType().Name}' and cannot also use " +
                $"the default {projectionKind} projection. The first projection selected for an operation cannot be replaced.");
        }

        return compatibleProjection;
    }

    internal TProjection GetOrCreateCustomProjection<TProjection>(
        Func<TProjection> createProjection,
        Action<TProjection> validateProjection,
        string projectionKind)
        where TProjection : class, IResource
    {
        if (Projection is null)
        {
            var projection = createProjection();
            validateProjection(projection);
            Projection = projection;
            CustomProjectionType = typeof(TProjection);
            return projection;
        }

        if (CustomProjectionType != typeof(TProjection))
        {
            var selectedProjection = CustomProjectionType is null
                ? $"the default {projectionKind} projection"
                : $"a custom {projectionKind} projection of type '{CustomProjectionType.Name}'";

            throw new InvalidOperationException(
                $"The resource '{Owner.Name}' already uses {selectedProjection} and cannot also use " +
                $"a custom {projectionKind} projection of type '{typeof(TProjection).Name}'. The first projection selected for an operation cannot be replaced.");
        }

        return (TProjection)Projection;
    }
}
