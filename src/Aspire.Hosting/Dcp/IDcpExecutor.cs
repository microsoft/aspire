// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Dcp.Model;

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

    ConcurrentBag<IAppResource> AppResources { get; }

    // Orchestrates creation of DCP resources that have direct counterparts in the Aspire model.
    // Uses the provided IObjectCreator to prepare and create the resources.
    // Raises Aspire model events, handles explicit-startup resources, and isolates errors per replica.
    Task CreateRenderedResourcesAsync<TDcpResource, TContext>(
        IObjectCreator<TDcpResource, TContext> creator,
        IEnumerable<RenderedModelResource<TDcpResource>> resources,
        TContext context,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata;

    // Creates DCP custom resource objects via the Kubernetes API. Has no side effects on the Aspire model.
    // Typical use includes implementation of createResourceFunc passed to CreateRenderedResourcesAsync,  
    // and creating objects that do not have corresponding Aspire model resources,
    // for example Service, ContainerNetwork, and ContainerNetworkTunnelProxy.
    Task CreateDcpObjectsAsync<TDcpResource>(IEnumerable<TDcpResource> objects, CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata;

    // Waits until the provided set of Services have their addresses allocated by the orchestrator
    // and updates them with the allocated address information.
    Task UpdateWithEffectiveAddressInfo(IEnumerable<Service> services, CancellationToken cancellationToken, TimeSpan? timeout = null);
}
