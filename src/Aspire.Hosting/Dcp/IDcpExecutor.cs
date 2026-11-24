// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp;

internal interface IDcpExecutor
{
    Task RunApplicationAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    IResourceReference GetResource(string resourceName);

    Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken);

    /// <summary>Stops the instance's DCP object.</summary>
    /// <returns><see langword="false"/> if the instance had no DCP object to stop.</returns>
    Task<bool> StopResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the starts of an instance that haven't reached DCP (see <see cref="PendingStartRegistry"/>), and waits
    /// until the canceled starts have ended.
    /// </summary>
    Task<PendingStartCancellation> CancelPendingStartAsync(IResourceReference resourceReference, CancellationToken cancellationToken);

    /// <summary>Whether the instance has a DCP object, which a Stop can act on.</summary>
    bool HasDcpObject(IResourceReference resourceReference);

    /// <summary>Whether the most recent start of the instance was canceled before it reached DCP, and it hasn't started since.</summary>
    bool IsStartCanceled(string dcpResourceName);
}
