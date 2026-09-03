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

        return resource.HasAppliedContainerProjection() ||
            resource.Annotations.OfType<ContainerImageAnnotation>().Any();
    }
}
