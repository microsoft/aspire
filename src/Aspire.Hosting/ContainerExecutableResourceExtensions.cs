// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for working with <see cref="ExecutableResource"/> objects.
/// </summary>
internal static class ContainerExecutableResourceExtensions
{
    /// <summary>
    /// Returns an enumerable collection of executable resources from the specified distributed application model.
    /// </summary>
    /// <param name="model">The distributed application model to retrieve executable resources from.</param>
    /// <returns>An enumerable collection of executable resources.</returns>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static IEnumerable<ContainerExecutableResource> GetContainerExecutableResources(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.Resources.OfType<ContainerExecutableResource>();
    }
}
