// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Provides operations for creating and managing DCP custom resource objects.
/// Extracted from IDcpExecutor to break the circular dependency between DcpExecutor and object creators.
/// </summary>
internal interface IDcpObjectFactory
{
    /// <summary>
    /// Orchestrates creation of DCP resources that have direct counterparts in the Aspire model.
    /// Uses the provided IObjectCreator to prepare and create the resources.
    /// Raises Aspire model events, handles explicit-startup resources, and isolates errors per replica.
    /// </summary>
    Task CreateRenderedResourcesAsync<TDcpResource, TContext>(
        IObjectCreator<TDcpResource, TContext> creator,
        IEnumerable<RenderedModelResource<TDcpResource>> resources,
        TContext context,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata;

    /// <summary>
    /// Creates DCP custom resource objects via the Kubernetes API. Has no side effects on the Aspire model.
    /// </summary>
    Task CreateDcpObjectsAsync<TDcpResource>(IEnumerable<TDcpResource> objects, CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata;

    /// <summary>
    /// Waits until the provided set of Services have their addresses allocated by the orchestrator
    /// and updates them with the allocated address information.
    /// </summary>
    Task UpdateWithEffectiveAddressInfo(IEnumerable<Service> services, CancellationToken cancellationToken, TimeSpan? timeout = null);

    /// <summary>
    /// Waits until each of the provided DCP objects reports a state that is contained in <paramref name="finalStates"/>,
    /// or until <paramref name="timeout"/> elapses.
    /// </summary>
    /// <returns>
    /// The latest observed instance for each input object (with <c>Status</c> populated when available).
    /// Callers can inspect the returned objects to determine which ones reached a final state and which did not.
    /// </returns>
    Task<IReadOnlyList<TDcpResource>> WaitForStateAsync<TDcpResource>(
        IEnumerable<TDcpResource> objects,
        Func<TDcpResource, string?> stateSelector,
        IReadOnlyCollection<string> finalStates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata;
}
