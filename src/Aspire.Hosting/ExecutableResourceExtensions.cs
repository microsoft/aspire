// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for working with <see cref="ExecutableResource"/> objects.
/// </summary>
public static class ExecutableResourceExtensions
{
    /// <summary>
    /// Returns executable resources whose effective shape remains an executable for the current AppHost invocation.
    /// </summary>
    /// <param name="model">The distributed application model to retrieve executable resources from.</param>
    /// <returns>An enumerable collection of resources whose effective shape is executable.</returns>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static IEnumerable<ExecutableResource> GetExecutableResources(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.Resources
            .OfType<ExecutableResource>()
            .Where(static resource => resource.AsContainer() is null);
    }
}
