// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp;

internal interface IDcpExecutor
{
    Task RunApplicationAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the application and waits until DCP reports that its resources were cleaned up.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when DCP confirmed the cleanup; <see langword="false"/> when the cleanup could not be
    /// confirmed, for example because a stop was already in progress or the wait was canceled.
    /// </returns>
    Task<bool> StopAndWaitForResourceCleanupAsync(CancellationToken cancellationToken);

    IResourceReference GetResource(string resourceName);

    Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken);

    Task StopResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken);
}
