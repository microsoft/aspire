// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Specifies which endpoints to process when creating AllocatedEndpoint info.
/// </summary>
[Flags]
internal enum AllocatedEndpointsMode
{
    Workload = 0x1,
    ContainerTunnel = 0x2,
    All = 0xFF
}

internal interface IDcpExecutor
{
    Task RunApplicationAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    IResourceReference GetResource(string resourceName);

    Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken);

    Task StopResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken);
}
