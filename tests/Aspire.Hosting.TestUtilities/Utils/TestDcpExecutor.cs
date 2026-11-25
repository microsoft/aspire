// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Dcp;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestDcpExecutor : IDcpExecutor
{
    private readonly Dictionary<string, IResourceReference> _resources = new(StringComparers.ResourceName);
    private readonly ConcurrentQueue<string> _startedResources = new();
    private readonly ConcurrentQueue<string> _stoppedResources = new();

    /// <summary>
    /// DCP resource names passed to <see cref="StartResourceAsync"/>, in the order they were requested.
    /// </summary>
    /// <remarks>
    /// <c>ApplicationOrchestrator.StartResourceAsync</c> only forwards to the executor when it decides the resource
    /// is not already starting, so this is how a test tells "the start reached DCP" apart from "the start was
    /// swallowed because the snapshot claimed the resource was already on its way up".
    /// </remarks>
    public IReadOnlyCollection<string> StartedResources => _startedResources;

    /// <summary>
    /// Registers a resource so that <see cref="GetResource"/> can resolve it, mimicking a DCP resource that was
    /// created for <paramref name="modelResource"/> under the DCP name <paramref name="dcpResourceName"/>.
    /// </summary>
    public void AddResource(IResource modelResource, string dcpResourceName)
    {
        _resources[dcpResourceName] = new TestResourceReference(modelResource, dcpResourceName);
    }

    public IResourceReference GetResource(string resourceName)
    {
        return _resources.TryGetValue(resourceName, out var resource)
            ? resource
            : throw new InvalidOperationException($"Resource '{resourceName}' not found.");
    }

    public Task RunApplicationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken)
    {
        _startedResources.Enqueue(resourceReference.DcpResourceName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> StopAndWaitForResourceCleanupAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>DCP resource names passed to <see cref="StopResourceAsync"/>, in the order they were requested.</summary>
    public IReadOnlyCollection<string> StoppedResources => _stoppedResources;

    public Task<bool> StopResourceAsync(IResourceReference resource, CancellationToken cancellationToken)
    {
        _stoppedResources.Enqueue(resource.DcpResourceName);
        return Task.FromResult(true);
    }

    /// <summary>
    /// What <see cref="CancelPendingStartAsync"/> reports for an instance. By default no start is pending. A test that
    /// reports a canceled start also publishes the state that the real executor would.
    /// </summary>
    public Func<IResourceReference, Task<PendingStartCancellation>> OnCancelPendingStart { get; set; } = _ => Task.FromResult(PendingStartCancellation.None);

    /// <summary>
    /// What <see cref="HasDcpObject"/> reports for an instance. By default every registered resource stands for a DCP
    /// object, so a Stop reaches <see cref="StopResourceAsync"/>.
    /// </summary>
    public Func<IResourceReference, bool> OnHasDcpObject { get; set; } = _ => true;

    public Task<PendingStartCancellation> CancelPendingStartAsync(IResourceReference resourceReference, CancellationToken cancellationToken) =>
        OnCancelPendingStart(resourceReference);

    public bool HasDcpObject(IResourceReference resourceReference) => OnHasDcpObject(resourceReference);

    /// <summary>What <see cref="IsStartCanceled"/> reports for an instance. By default no start was canceled.</summary>
    public Func<string, bool> OnIsStartCanceled { get; set; } = _ => false;

    public bool IsStartCanceled(string dcpResourceName) => OnIsStartCanceled(dcpResourceName);

    private sealed class TestResourceReference(IResource modelResource, string dcpResourceName) : IResourceReference
    {
        public IResource ModelResource { get; } = modelResource;

        public string DcpResourceName { get; } = dcpResourceName;
    }
}
