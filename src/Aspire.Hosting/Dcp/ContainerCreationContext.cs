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
    public required ServiceAppResource ServiceResource { get; init; }
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
    /// Disposes resources owned by the container creation context.
    /// </summary>
    public void Dispose()
    {
        ContainerServicesSpecReady.Dispose();
    }
}
