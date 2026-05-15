// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// A ContainerNetworkService represents a service implemented by a host resource but exposed on a container network.
/// </summary>
internal record class ContainerNetworkService
{
    public required AppResource<Service> ServiceResource { get; init; }
    public TunnelConfiguration? TunnelConfig { get; init; }
}

/// <summary>
/// Helps coordinate container creation tasks and container tunnel creation and configuration task.
/// </summary>
internal sealed class ContainerCreationContext(
    Task containerPrerequisitesReady, 
    Task containerTunnelPrerequisitesReady,
    CancellationToken applicationRunCancellationToken)
{
    // The task that completes when the container prerequisites are ready.
    public Task ContainerPrerequisitesReady { get; } = containerPrerequisitesReady;

    // The task that completes when the container tunnel prerequisites are ready.
    // For container tunnel to start, both the container prerequisites and the container tunnel prerequisites must be ready.
    public Task ContainerTunnelPrerequisitesReady { get; } = containerTunnelPrerequisitesReady;

    // The cancellation token for the application run that this context belongs to. 
    // This is used to cancel the container tunnel creation task if the application run is cancelled.
    public CancellationToken ApplicationRunCancellationToken { get; } = applicationRunCancellationToken;
}
