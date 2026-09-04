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

        // Check the owner reference rather than merely the shared annotation collection so a bare
        // ContainerResource without an image still uses the legacy image annotation fallback below.
        if (resource.Annotations.OfType<ContainerResourceProjectionAnnotation>().SingleOrDefault() is { } registration &&
            ReferenceEquals(resource, registration.Owner))
        {
            return true;
        }

        return resource.Annotations.OfType<ContainerImageAnnotation>().Any();
    }

    /// <summary>
    /// Gets the effective container for a resource.
    /// </summary>
    /// <param name="resource">The resource to inspect.</param>
    /// <returns>
    /// The resource itself when it is a <see cref="ContainerResource"/>, its selected container projection,
    /// or <see langword="null"/> when the resource has no projection or is classified as a container only through
    /// legacy annotations.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <ats-summary>Gets a resource's effective container.</ats-summary>
    [AspireExport]
    public static ContainerResource? AsContainer(this IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (resource is ContainerResource container)
        {
            return container;
        }

        var registration = resource.Annotations
            .OfType<ContainerResourceProjectionAnnotation>()
            .SingleOrDefault();

        return registration?.Projection;
    }
}
