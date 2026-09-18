// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Coordinates the application-run prerequisites for provisioning endpoint representations on the default container network.
/// </summary>
internal sealed class ContainerNetworkEndpointContext(
    Task containerNetworkReady,
    Task workloadEndpointsReady,
    CancellationToken applicationRunCancellationToken)
{
    public Task ContainerNetworkReady { get; } = containerNetworkReady;

    public Task WorkloadEndpointsReady { get; } = workloadEndpointsReady;

    public CancellationToken ApplicationRunCancellationToken { get; } = applicationRunCancellationToken;
}
