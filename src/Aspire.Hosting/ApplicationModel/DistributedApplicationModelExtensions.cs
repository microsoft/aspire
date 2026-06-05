// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods on the <see cref="DistributedApplicationModel"/> class.
/// </summary>
public static class DistributedApplicationModelExtensions
{
    /// <summary>
    /// Returns the compute resources from the <see cref="DistributedApplicationModel"/>.
    /// Compute resources are those that are either containers or project resources, and are not marked to be ignored by the manifest publishing callback annotation.
    /// </summary>
    /// <param name="model">The distributed application model to extract compute resources from.</param>
    /// <returns>An enumerable of compute <see cref="IResource"/> in the model.</returns>
    public static IEnumerable<IResource> GetComputeResources(this DistributedApplicationModel model)
    {
        foreach (var r in model.Resources)
        {
            if (r.IsExcludedFromPublish())
            {
                continue;
            }

            if (!r.IsContainer() && !r.IsEmulator() && r is not ProjectResource)
            {
                continue;
            }

            if (r.IsBuildOnlyContainer())
            {
                continue;
            }

            yield return r;
        }
    }

    /// <summary>
    /// Returns the build resources from the <see cref="DistributedApplicationModel"/>.
    /// Build resources are those that are either build-only containers or project resources, and are not marked to be ignored by the manifest publishing callback annotation.
    /// </summary>
    /// <param name="model">The distributed application model to extract build resources from.</param>
    /// <returns>An enumerable of build <see cref="IResource"/> in the model.</returns>
    public static IEnumerable<IResource> GetBuildResources(this DistributedApplicationModel model)
    {
        foreach (var r in model.Resources)
        {
            if (r.RequiresImageBuild())
            {
                yield return r;
            }
        }
    }

    /// <summary>
    /// Returns the build and push resources from the <see cref="DistributedApplicationModel"/>.
    /// Build and push resources are those that require building and pushing container images to a registry, and are not marked to be ignored by the manifest publishing callback annotation.
    /// </summary>
    /// <param name="model">The distributed application model to extract build and push resources from.</param>
    /// <returns>An enumerable of build and push <see cref="IResource"/> in the model.</returns>
    public static IEnumerable<IResource> GetBuildAndPushResources(this DistributedApplicationModel model)
    {
        foreach (var r in model.Resources)
        {
            if (r.RequiresImageBuildAndPush())
            {
                yield return r;
            }
        }
    }
}
