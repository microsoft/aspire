// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods on the <see cref="DistributedApplicationModel"/> class.
/// </summary>
public static class DistributedApplicationModelExtensions
{
    /// <summary>
    /// Returns the canonical model members without resolving projections.
    /// </summary>
    /// <param name="model">The distributed application model.</param>
    /// <returns>The resources that own the effective resources exposed by <see cref="DistributedApplicationModel.Resources"/>.</returns>
    [AspireExportIgnore(Reason = "Canonical resource enumeration is not part of the ATS surface.")]
    public static IEnumerable<IResource> GetResourceOwners(this DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.ResourceOwners;
    }

    /// <summary>
    /// Returns the compute resources from the <see cref="DistributedApplicationModel"/>.
    /// Compute resources are those that are either containers or project resources, and are not marked to be ignored by the manifest publishing callback annotation.
    /// </summary>
    /// <param name="model">The distributed application model to extract compute resources from.</param>
    /// <returns>An enumerable of compute <see cref="IResource"/> in the model.</returns>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static IEnumerable<IResource> GetComputeResources(this DistributedApplicationModel model)
    {
        foreach (var r in model.Resources)
        {
            var effectiveResource = r.GetEffectiveResource();

            if (effectiveResource.IsExcludedFromPublish())
            {
                continue;
            }

            if (!effectiveResource.IsContainer() && !effectiveResource.IsEmulator() && effectiveResource is not ProjectResource)
            {
                continue;
            }

            if (effectiveResource.IsBuildOnlyContainer())
            {
                continue;
            }

            yield return effectiveResource;
        }
    }

    /// <summary>
    /// Returns the build resources from the <see cref="DistributedApplicationModel"/>.
    /// Build resources are those that are either build-only containers or project resources, and are not marked to be ignored by the manifest publishing callback annotation.
    /// </summary>
    /// <param name="model">The distributed application model to extract build resources from.</param>
    /// <returns>An enumerable of build <see cref="IResource"/> in the model.</returns>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static IEnumerable<IResource> GetBuildResources(this DistributedApplicationModel model)
    {
        foreach (var r in model.Resources)
        {
            var effectiveResource = r.GetEffectiveResource();

            if (effectiveResource.RequiresImageBuild())
            {
                yield return effectiveResource;
            }
        }
    }

    /// <summary>
    /// Returns the build and push resources from the <see cref="DistributedApplicationModel"/>.
    /// Build and push resources are those that require building and pushing container images to a registry, and are not marked to be ignored by the manifest publishing callback annotation.
    /// </summary>
    /// <param name="model">The distributed application model to extract build and push resources from.</param>
    /// <returns>An enumerable of build and push <see cref="IResource"/> in the model.</returns>
    [AspireExportIgnore(Reason = "Application model inspection helper — not part of the ATS surface.")]
    public static IEnumerable<IResource> GetBuildAndPushResources(this DistributedApplicationModel model)
    {
        foreach (var r in model.Resources)
        {
            var effectiveResource = r.GetEffectiveResource();

            if (effectiveResource.RequiresImageBuildAndPush())
            {
                yield return effectiveResource;
            }
        }
    }
}
