// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Tests.Dcp;

internal sealed class RecordingDcpObjectFactory : IDcpObjectFactory
{
    public int CreateDcpObjectsCallCount { get; private set; }

    public Task CreateDcpObjectsAsync<TDcpResource>(
        IEnumerable<TDcpResource> objects,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        CreateDcpObjectsCallCount++;
        return Task.CompletedTask;
    }

    public Task CreateRenderedResourcesAsync<TDcpResource, TContext>(
        IObjectCreator<TDcpResource, TContext> creator,
        IEnumerable<RenderedModelResource<TDcpResource>> resources,
        TContext context,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
        => throw new NotSupportedException();

    public Task<TDcpResource> PatchDcpObjectAsync<TDcpResource>(
        TDcpResource obj,
        Action<TDcpResource> change,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
        => throw new NotSupportedException();

    public Task UpdateWithEffectiveAddressInfo(
        IEnumerable<Service> services,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<TDcpResource>> WaitForStateAsync<TDcpResource>(
        IEnumerable<TDcpResource> objects,
        Func<TDcpResource, string?> stateSelector,
        IReadOnlyCollection<string> finalStates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
        => throw new NotSupportedException();
}
