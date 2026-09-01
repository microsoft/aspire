// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Resolves the run-mode host directory for a Kubernetes persistent volume.
/// </summary>
internal static class KubernetesPersistentVolumeLocalStorage
{
    internal static string GetOrCreatePath(IAspireStore store, KubernetesPersistentVolumeResource volume)
    {
        var path = GetPath(store, volume);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string GetPath(IAspireStore store, KubernetesPersistentVolumeResource volume)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(volume);

        // IAspireStore is scoped to the AppHost's intermediate output directory, which keeps
        // local persistent data isolated between repositories and worktrees without another hash.
        return VolumeMountPathResolver.GetPathUnderStore(
            store,
            "kubernetes",
            VolumeMountPathResolver.GetStablePathSegment(volume.Parent.Name),
            "volumes",
            VolumeMountPathResolver.GetStablePathSegment(volume.GetClaimName()));
    }
}
