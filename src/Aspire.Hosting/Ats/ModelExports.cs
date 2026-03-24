// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for distributed application model access.
/// </summary>
internal static class ModelExports
{
    /// <summary>
    /// Gets the distributed application model from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>The distributed application model handle.</returns>
    [AspireExport("getDistributedApplicationModel", Description = "Gets the distributed application model from the service provider")]
    public static DistributedApplicationModel GetDistributedApplicationModel(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<DistributedApplicationModel>();
    }

    /// <summary>
    /// Gets all resources in the distributed application model.
    /// </summary>
    /// <param name="model">The distributed application model handle.</param>
    /// <returns>The resources in the model.</returns>
    [AspireExport("getResources", Description = "Gets resources from the distributed application model")]
    public static IResource[] GetResources(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return [.. model.Resources];
    }

    /// <summary>
    /// Finds a resource by name.
    /// </summary>
    /// <param name="model">The distributed application model handle.</param>
    /// <param name="name">The resource name.</param>
    /// <returns>The matching resource, or <see langword="null"/> when not found.</returns>
    [AspireExport("findResourceByName", Description = "Finds a resource by name")]
    public static IResource? FindResourceByName(this DistributedApplicationModel model, string name)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return model.Resources.SingleOrDefault(resource => StringComparers.ResourceName.Equals(resource.Name, name));
    }
}
