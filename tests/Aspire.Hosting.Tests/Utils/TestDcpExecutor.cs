// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestDcpExecutor : IDcpExecutor
{
    public IResourceReference GetResource(string resourceName) => throw new NotImplementedException();

    public Task RunApplicationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopResourceAsync(IResourceReference resource, CancellationToken cancellationToken) => Task.CompletedTask;

    public ConcurrentBag<IAppResource> AppResources { get; } = [];

    public Task CreateRenderedResourcesAsync<TDcpResource, TContext>(IObjectCreator<TDcpResource, TContext> creator, IEnumerable<RenderedModelResource<TDcpResource>> resources, TContext context, CancellationToken cancellationToken) where TDcpResource : CustomResource, IKubernetesStaticMetadata => Task.CompletedTask;

    public Task CreateDcpObjectsAsync<TDcpResource>(IEnumerable<TDcpResource> objects, CancellationToken cancellationToken) where TDcpResource : CustomResource, IKubernetesStaticMetadata => Task.CompletedTask;

    public Task UpdateWithEffectiveAddressInfo(IEnumerable<Service> services, CancellationToken cancellationToken, TimeSpan? timeout = null) => Task.CompletedTask;
}
