// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
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
internal sealed class ContainerCreationContext : IDisposable
{
    public readonly CountdownEvent ContainerServicesSpecReady;
    public readonly Channel<ContainerNetworkService> ContainerServicesChan;
    private readonly Lazy<Task> _createTunnelLazy;

    public Task CreateTunnel => _createTunnelLazy.Value;

    public ContainerCreationContext(int containerCount, Func<ContainerCreationContext, Task> createTunnelFunc)
    {
        ContainerServicesSpecReady = new CountdownEvent(containerCount);
        ContainerServicesChan = Channel.CreateUnbounded<ContainerNetworkService>();
        _createTunnelLazy = new Lazy<Task>(() => createTunnelFunc(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Creates a new ContainerCreationContext for creating additional containers (beyond the initial set created during application startup).
    /// </summary>
    /// <remarks>
    /// The new context will share the same tunnel creation task as the original context, ensuring that the tunnel is only created once
    /// and that all containers will properly synchronize with the tunnel creation task.
    /// </remarks>
    public ContainerCreationContext ForAdditionalContainers(int additionalContainerCount)
    {
        return new ContainerCreationContext(additionalContainerCount, _ => CreateTunnel);
    }

    /// <summary>
    /// Disposes resources owned by the container creation context.
    /// </summary>
    public void Dispose()
    {
        ContainerServicesSpecReady.Dispose();
    }
}
