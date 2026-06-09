// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for <see cref="DistributedApplicationModel"/> to work with <see cref="ProjectResource"/> instances.
/// </summary>
public static class ProjectResourceExtensions
{
    /// <summary>
    /// Returns all project resources in the distributed application model.
    /// </summary>
    /// <param name="model">The distributed application model.</param>
    /// <returns>An enumerable collection of project resources.</returns>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static IEnumerable<ProjectResource> GetProjectResources(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.Resources.OfType<ProjectResource>();
    }

    /// <summary>
    /// Gets the project metadata for the specified project resource.
    /// </summary>
    /// <param name="projectResource">The project resource.</param>
    /// <returns>The project metadata.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the project resource doesn't have project metadata.</exception>
    [AspireExportIgnore(Reason = "Project metadata is a .NET-specific contract and is not part of the ATS surface.")]
    public static IProjectMetadata GetProjectMetadata(this ProjectResource projectResource)
    {
        ArgumentNullException.ThrowIfNull(projectResource);

        return projectResource.Annotations.OfType<IProjectMetadata>().Single();
    }

    /// <summary>
    /// Attempts to retrieve the last <see cref="IProjectMetadata"/> annotation from the specified resource.
    /// </summary>
    /// <param name="resource">The resource to inspect.</param>
    /// <param name="projectMetadata">When this method returns, contains the last <see cref="IProjectMetadata"/> annotation, if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a project metadata annotation was found; otherwise, <see langword="false"/>.</returns>
    [AspireExportIgnore(Reason = "Project annotation inspection helper — not part of the ATS surface.")]
    internal static bool TryGetProjectAnnotation(this IResource resource, [NotNullWhen(true)] out IProjectMetadata? projectMetadata)
    {
        return resource.TryGetLastAnnotation<IProjectMetadata>(out projectMetadata);
    }

    /// <summary>
    /// Returns resources that are project-like via <see cref="IProjectMetadata"/>.
    /// </summary>
    /// <param name="model">The distributed application model.</param>
    /// <returns>The project-like resources discovered in the model.</returns>
    internal static IEnumerable<IResource> GetProjectAnnotatedResources(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var resource in model.Resources)
        {
            if (resource.TryGetProjectAnnotation(out _))
            {
                yield return resource;
            }
        }
    }
}
