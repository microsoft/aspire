// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Annotation added to <see cref="MauiProjectResource"/> to serialize builds across
/// platform resources that share the same project.
/// </summary>
internal sealed class MauiBuildQueueAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets the semaphore used to serialize builds for this project.
    /// </summary>
    public SemaphoreSlim BuildSemaphore { get; } = new(1, 1);

    /// <summary>
    /// Per-resource CTS that allows the stop command to cancel a queued or building resource.
    /// The key is the resource name.
    /// </summary>
    public ConcurrentDictionary<string, CancellationTokenSource> ResourceCancellations { get; } = new();

    /// <summary>
    /// Cancels a resource's queued or building state.
    /// </summary>
    /// <returns><c>true</c> if the resource was found and cancelled; <c>false</c> otherwise.</returns>
    public bool CancelResource(string resourceName)
    {
        if (ResourceCancellations.TryRemove(resourceName, out var cts))
        {
            try
            {
                cts.Cancel(throwOnFirstException: false);
            }
            catch (ObjectDisposedException)
            {
                // The CTS was disposed by the event handler's using block after the build
                // completed naturally at the exact moment the user clicked stop.
                return false;
            }

            return true;
        }

        return false;
    }
}
