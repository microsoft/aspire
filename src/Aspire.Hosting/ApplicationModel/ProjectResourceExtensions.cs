// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
}
