// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dcp;

internal interface IDcpExecutor
{
    Task RunApplicationAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    IResourceReference GetResource(string resourceName);

    IResourceReference GetResource(IResource modelResource);

    Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken);

    Task StopResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken);
}
