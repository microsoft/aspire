// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for working with <see cref="ExecutableResource"/> objects.
/// </summary>
public static class ExecutableResourceExtensions
{
    /// <summary>
    /// Returns an enumerable collection of executable resources from the specified distributed application model.
    /// </summary>
    /// <param name="model">The distributed application model to retrieve executable resources from.</param>
    /// <returns>An enumerable collection of executable resources.</returns>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static IEnumerable<ExecutableResource> GetExecutableResources(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.Resources.OfType<ExecutableResource>();
    }

    /// <summary>
    /// Returns resources that are executable via <see cref="ExecutableAnnotation"/>.
    /// </summary>
    /// <param name="model">The distributed application model to inspect.</param>
    /// <returns>The executable resources discovered in the model.</returns>
    internal static IEnumerable<IResource> GetExecutableAnnotatedResources(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var resource in model.Resources)
        {
            if (!resource.TryGetProjectAnnotation(out _) && resource.TryGetExecutableAnnotation(out _))
            {
                yield return resource;
            }
        }
    }

    /// <summary>
    /// Attempts to retrieve the last <see cref="ExecutableAnnotation"/> from the specified resource.
    /// </summary>
    /// <param name="resource">The resource to inspect.</param>
    /// <param name="executableAnnotation">When this method returns, contains the last <see cref="ExecutableAnnotation"/>, if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an executable annotation was found; otherwise, <see langword="false"/>.</returns>
    [AspireExportIgnore(Reason = "Executable annotation inspection helper — not part of the ATS surface.")]
    internal static bool TryGetExecutableAnnotation(this IResource resource, [NotNullWhen(true)] out ExecutableAnnotation? executableAnnotation)
    {
        return resource.TryGetLastAnnotation<ExecutableAnnotation>(out executableAnnotation);
    }
}
