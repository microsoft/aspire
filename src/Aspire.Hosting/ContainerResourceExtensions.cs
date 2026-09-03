// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for working with container resources in a distributed application model.
/// </summary>
public static class ContainerResourceExtensions
{
    /// <summary>
    /// Returns a collection of container resources in the specified distributed application model.
    /// </summary>
    /// <param name="model">The distributed application model to search for container resources.</param>
    /// <returns>A collection of container resources in the specified distributed application model.</returns>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static IEnumerable<IResource> GetContainerResources(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var resource in model.Resources)
        {
            if (resource.IsContainer())
            {
                yield return resource;
            }
        }
    }

    /// <summary>
    /// Determines whether the specified resource is configured to execute as a container in the current AppHost invocation.
    /// </summary>
    /// <param name="resource">The resource to check.</param>
    /// <returns><see langword="true"/> if the resource is configured to execute as a container; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// An applied projection is authoritative for the active operation. Container image annotations
    /// remain supported as the compatibility fallback for resources without a projection.
    /// </remarks>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static bool IsContainer(this IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // A registered projection is authoritative: the owner executes as the projected container even when the
        // owner itself is not a ContainerResource. The reference check excludes the projection side of the pair,
        // which shares the owner's annotations, so a bare ContainerResource without an image is still classified
        // by the legacy image annotation fallback below rather than by merely being its own container view.
        if (resource.AsContainer() is { } container && !ReferenceEquals(container, resource))
        {
            return true;
        }

        return resource.Annotations.OfType<ContainerImageAnnotation>().Any();
    }

    /// <summary>
    /// Gets the container resource represented by the specified resource.
    /// </summary>
    /// <param name="resource">The resource to inspect.</param>
    /// <returns>
    /// The resource itself when it is a <see cref="ContainerResource"/>, the container projection applied for
    /// the current AppHost invocation, or <see langword="null"/> when no strongly typed container view exists.
    /// </returns>
    /// <remarks>
    /// This method reflects projection configuration completed so far. It returns <see langword="null"/> before
    /// an applicable projection is registered and for resources classified as containers solely through the
    /// legacy <see cref="ContainerImageAnnotation"/> fallback. Use <see cref="IsContainer(IResource)"/> when only
    /// effective container classification is required.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <ats-summary>Gets the container resource represented by a resource.</ats-summary>
    [AspireExport]
    public static ContainerResource? AsContainer(this IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (resource is ContainerResource container)
        {
            return container;
        }

        return resource.Annotations
            .OfType<ContainerResourceProjectionAnnotation>()
            .SingleOrDefault()
            ?.Projection;
    }
}
